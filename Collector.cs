using System.Text;
using Microsoft.Win32;

namespace RecentFiles;

public sealed record Entry(DateTime Time, string Kind, string FullPath, string Source);

public sealed class Options
{
    public int Hours { get; init; } = 10;
    public HashSet<string> Exts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllDrives { get; init; }
    public bool IncludeAppData { get; init; }
    public bool IncludeSystem { get; init; }
    public bool IncludePopular { get; init; }
    public bool UseAccessTime { get; init; } = true;
}

public static class Collector
{
    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string AppDataRoot = Path.Combine(Profile, "AppData") + "\\";
    static readonly string[] SystemPrefixes = BuildSystemPrefixes();
    static readonly string[] SystemParts =
    {
        "\\$Recycle.Bin\\", "\\System Volume Information\\", "\\WinSxS\\", "\\$WinREAgent\\"
    };

    public static List<Entry> Collect(Options o, CancellationToken ct)
    {
        var cutoff = DateTime.Now.AddHours(-o.Hours);
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        FromRecent(o, cutoff, map);
        FromUserAssist(o, cutoff, map);

        foreach (var root in Roots(o))
        {
            ct.ThrowIfCancellationRequested();
            Scan(root, o, cutoff, map, ct);
        }

        return map.Values.OrderByDescending(e => e.Time).ToList();
    }

    // ---------- фільтр популярних програм ----------
    // Щоб список не засмічували браузери, месенджери, лаунчери, IDE тощо.
    // Новий рядок додається просто в один із масивів нижче.

    static readonly HashSet<string> PopularStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "chrome_proxy", "chrome_pwa_launcher", "msedge", "msedge_proxy", "msedgewebview2",
        "firefox", "opera", "brave", "vivaldi", "yandex",
        "discord", "telegram", "ayugram", "whatsapp", "viber", "skype", "zoom", "slack",
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "spotify",
        "obs64", "obs32", "vlc", "notepad++", "code", "idea64", "pycharm64",
        "java", "javaw", "git", "node", "python", "pythonw",
        "winrar", "7zfm", "7zg", "7z", "acrobat", "acrord32", "sumatrapdf", "foxitpdfreader", "onedrive"
    };

    static readonly string[] PopularPrefixes =
    {
        "16launcher", "tlauncher", "sklauncher", "minecraftlauncher"
    };

    static readonly string[] PopularParts =
    {
        "\\Discord\\", "\\Google\\Chrome\\", "\\Mozilla Firefox\\", "\\Steam\\", "\\Spotify\\",
        "\\JetBrains\\", "\\Microsoft VS Code\\", "\\.vscode\\", "\\.idea\\", "\\.gradle\\",
        "\\.m2\\", "\\.git\\", "\\node_modules\\", "\\__pycache__\\"
    };

    static bool IsPopularDir(string path)
    {
        var p = path.EndsWith('\\') ? path : path + "\\";
        foreach (var part in PopularParts)
            if (p.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool IsPopular(string path, string ext)
    {
        if (IsPopularDir(path)) return true;

        if (ext.Equals("exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals("dll", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (PopularStems.Contains(stem)) return true;
            foreach (var pre in PopularPrefixes)
                if (stem.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------- фільтр системних шляхів ----------

    static string[] BuildSystemPrefixes()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p.TrimEnd('\\') + "\\");
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.Combine(local, "Microsoft"));
        Add(Path.Combine(local, "Packages"));

        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        Add(Path.Combine(sysRoot, "Recovery"));
        Add(Path.Combine(sysRoot, "$WinREAgent"));
        return list.ToArray();
    }

    static bool IsSystemPath(string path)
    {
        var p = path.EndsWith('\\') ? path : path + "\\";
        foreach (var pre in SystemPrefixes)
            if (p.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in SystemParts)
            if (p.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void Add(Dictionary<string, Entry> map, Options o, DateTime time, string kind, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!o.Exts.Contains(ext)) return;
        if (!o.IncludeSystem && IsSystemPath(path)) return;
        if (!o.IncludePopular && IsPopular(path, ext)) return;

        if (map.TryGetValue(path, out var old))
        {
            var src = old.Source.Contains(source) ? old.Source : old.Source + "+" + source;
            map[path] = time > old.Time ? new Entry(time, kind, path, src) : old with { Source = src };
        }
        else
        {
            map[path] = new Entry(time, kind, path, source);
        }
    }

    // ---------- джерело 1: папка Recent (.lnk) ----------

    static void FromRecent(Options o, DateTime cutoff, Dictionary<string, Entry> map)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(dir)) return;

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;

            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                try
                {
                    var time = File.GetLastWriteTime(lnk);
                    if (time < cutoff) continue;

                    dynamic sc = shell.CreateShortcut(lnk);
                    string? target = sc.TargetPath;
                    if (!string.IsNullOrEmpty(target))
                        Add(map, o, time, "Відкрито", target, "Recent");
                }
                catch { }
            }
        }
        catch { }
    }

    // ---------- джерело 2: UserAssist (запуски exe через Провідник) ----------

    static void FromUserAssist(Options o, DateTime cutoff, Dictionary<string, Entry> map)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (root == null) return;

            foreach (var guid in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey(guid + "\\Count");
                if (count == null) continue;

                foreach (var name in count.GetValueNames())
                {
                    if (count.GetValue(name) is not byte[] data || data.Length < 68) continue;

                    long ft = BitConverter.ToInt64(data, 60);
                    if (ft <= 0) continue;

                    DateTime time;
                    try { time = DateTime.FromFileTime(ft); } catch { continue; }
                    if (time < cutoff) continue;

                    var path = ExpandKnownFolders(Rot13(name));
                    if (!path.Contains('\\')) continue;
                    Add(map, o, time, "Запущено", path, "UserAssist");
                }
            }
        }
        catch { }
    }

    static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
            else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string ExpandKnownFolders(string s)
    {
        var map = new (string Guid, string Path)[]
        {
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("{905E63B6-C1BF-494E-B29C-65B732D3D21A}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SystemDirectory),
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("{374DE290-123F-4565-9164-39C4925E467B}", Path.Combine(Profile, "Downloads")),
            ("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("{5E6C858F-0E22-4760-9AFE-EA3317B67173}", Profile),
            ("{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
        };
        foreach (var (g, p) in map)
            s = s.Replace(g, p, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    // ---------- джерело 3: скан файлів за часом створення/зміни/доступу ----------

    static IEnumerable<string> Roots(Options o)
    {
        if (!o.AllDrives)
        {
            yield return Profile;
            yield break;
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable); }
            catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    static bool SkipDir(string dir, Options o)
    {
        var n = dir.EndsWith('\\') ? dir : dir + "\\";
        if (!o.IncludeSystem && IsSystemPath(n)) return true;
        if (!o.IncludePopular && IsPopularDir(n)) return true;
        if (!o.IncludeAppData && n.StartsWith(AppDataRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void Scan(string root, Options o, DateTime cutoff, Dictionary<string, Entry> map, CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            try
            {
                var di = new DirectoryInfo(dir);

                foreach (var sub in di.EnumerateDirectories("*", opts))
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (SkipDir(sub.FullName, o)) continue;
                    stack.Push(sub.FullName);
                }

                foreach (var f in di.EnumerateFiles("*", opts))
                {
                    var ext = f.Extension.TrimStart('.');
                    if (!o.Exts.Contains(ext)) continue;

                    DateTime t = f.LastWriteTime;
                    string kind = "Змінено";
                    if (f.CreationTime > t) { t = f.CreationTime; kind = "Створено"; }
                    if (o.UseAccessTime && f.LastAccessTime > t) { t = f.LastAccessTime; kind = "Доступ"; }
                    if (t < cutoff) continue;

                    Add(map, o, t, kind, f.FullName, "Скан");
                }
            }
            catch { }
        }
    }
}
