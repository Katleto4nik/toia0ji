using System.Text;

namespace RecentFiles;

internal static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        int hours = 10;
        string exts = "pdf,exe,dll,png";
        bool all = false, app = false, sys = false, access = true, popular = false;

        // Необов'язкові параметри: --hours 24 --ext pdf,exe --all --appdata --system --popular --noaccess
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            if (a == "--hours" && i + 1 < args.Length && int.TryParse(args[i + 1], out var h) && h > 0) { hours = h; i++; }
            else if (a == "--ext" && i + 1 < args.Length) { exts = args[++i]; }
            else if (a == "--all") all = true;
            else if (a == "--appdata") app = true;
            else if (a == "--system") sys = true;
            else if (a == "--popular") popular = true;
            else if (a == "--noaccess") access = false;
        }

        var set = new HashSet<string>(
            exts.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimStart('.')),
            StringComparer.OrdinalIgnoreCase);

        var o = new Options
        {
            Hours = hours,
            Exts = set,
            AllDrives = all,
            IncludeAppData = app,
            IncludeSystem = sys,
            IncludePopular = popular,
            UseAccessTime = access
        };

        Console.WriteLine($"Файли ({string.Join(", ", set)}) за останні {hours} год.");
        Console.WriteLine(all ? "Скан: усі диски" : "Скан: папка користувача (+ Recent і UserAssist)");
        Console.WriteLine("Сканую, зачекай...");
        Console.WriteLine();

        var list = Collector.Collect(o, CancellationToken.None);

        foreach (var e in list)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(e.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  [{e.Kind}/{e.Source}]  ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(Path.GetFileName(e.FullPath));
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("    -> " + (Path.GetDirectoryName(e.FullPath) ?? e.FullPath));
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine($"Усього: {list.Count}");
        Console.WriteLine("Натисни будь-яку клавішу, щоб закрити...");
        try { Console.ReadKey(true); } catch { }
        return 0;
    }
}
