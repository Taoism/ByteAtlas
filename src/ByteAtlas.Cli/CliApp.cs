using System.Diagnostics;
using System.Globalization;
using ByteAtlas.Analysis;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Scanning;
using ByteAtlas.Search;
using ByteAtlas.Storage;

namespace ByteAtlas.Cli;

internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?")
        {
            Console.WriteLine(Help);
            return 0;
        }
        try
        {
            return args[0] switch
            {
                "scan" => await ScanAsync(args[1..]),
                "compare" => await CompareAsync(args[1..]),
                "search" => await SearchAsync(args[1..]),
                "history" => History(args[1..]),
                "--version" or "version" => Version(),
                _ => Fail($"Unknown command '{args[0]}'. Run byteatlas-cli --help."),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException or FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Fail(e.Message);
        }
    }

    private const string Help = """
        byteatlas-cli — disk space analysis from the command line (ByteAtlas)

          byteatlas-cli scan <path> [options]
            --method auto|fs|mft      how to read the drive (default auto: MFT when possible)
            --threads N               threads for the folder walk
            --no-owners               skip owner statistics (faster folder walk)
            --exclude PATTERN         leave out files (wildcards; repeatable; dir:NAME for folders)
            --export FILE             write a report: .xlsx .pdf .html .csv .txt  ({date} = today; repeatable)
            --depth N                 folder levels in the report (default 3, 0 = all)
            --files                   list single files in the report
            --unit auto|b|kb|mb|gb|tb size unit
            --top N                   print the N largest folders (default 20, 0 = none)
            --save-index FILE         save the scan (.badb SQLite or .xml) for later comparisons
            --history                 add the totals to the ByteAtlas history
            --extensions FILE         write the file type statistics
            --owners FILE             write the owner statistics
            --ages FILE               write the age statistics
            --top-files N FILE        write the N largest files

          byteatlas-cli compare <path-or-index> <older-index-or-path> [--export FILE] [--top N]
          byteatlas-cli search <path> [--template large|old|temp|duplicates|longpaths]
                [--name PATTERN] [--min-size 100MB] [--max-size 1GB] [--older-days N] [--newer-days N]
                [--owner TEXT] [--hash md5|sha256|none] [--export FILE] [--max N]
          byteatlas-cli history <path> [--export FILE.csv]

        Exit codes: 0 ok, 1 error, 2 scan finished with errors / stopped.
        """;

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    private static int Version()
    {
        Console.WriteLine("byteatlas-cli " + typeof(CliApp).Assembly.GetName().Version?.ToString(3));
        return 0;
    }

    private static string Expand(string path) =>
        Environment.ExpandEnvironmentVariables(path.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));

    private static SizeUnit Unit(string s) => s.ToLowerInvariant() switch
    {
        "b" or "bytes" => SizeUnit.Bytes,
        "kb" => SizeUnit.KB,
        "mb" => SizeUnit.MB,
        "gb" => SizeUnit.GB,
        "tb" => SizeUnit.TB,
        _ => SizeUnit.Auto,
    };

    private static void Write(ExportTable t, string file, ExportSettings o)
    {
        var path = Expand(file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Exporters.Write(t, path, o);
        Console.WriteLine("wrote " + path);
    }

    private static async Task<ScanSession> LoadOrScanAsync(string pathOrIndex, ScanOptions opt)
    {
        if (File.Exists(pathOrIndex))
            return IndexFile.Load(pathOrIndex);
        var s = new ScanSession(pathOrIndex, opt);
        await Scanner.ScanAsync(s, CancellationToken.None);
        return s;
    }

    private static async Task<int> ScanAsync(string[] a)
    {
        if (a.Length == 0)
            return Fail("scan needs a path");
        var opt = new ScanOptions();
        var exp = new ExportSettings();
        var top = 20;
        var exports = new List<string>();
        string? index = null, extFile = null, ownerFile = null, ageFile = null, topFile = null;
        var topFiles = 100;
        var history = false;
        for (var i = 1; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i])
            {
                case "--method":
                    opt.Method = Next() switch { "fs" => ScanMethod.FileSystem, "mft" => ScanMethod.Mft, _ => ScanMethod.Auto };
                    break;
                case "--threads":
                    opt.Threads = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--no-owners":
                    opt.CollectOwners = false;
                    break;
                case "--exclude":
                    var p = Next();
                    var folders = p.StartsWith("dir:", StringComparison.OrdinalIgnoreCase);
                    opt.Filters.Add(new FilterRule { Pattern = folders ? p[4..] : p, Folders = folders, Exclude = true });
                    break;
                case "--export":
                    exports.Add(Next());
                    break;
                case "--depth":
                    exp.MaxDepth = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--files":
                    exp.IncludeFiles = true;
                    break;
                case "--unit":
                    exp.Unit = Unit(Next());
                    break;
                case "--top":
                    top = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--save-index":
                    index = Next();
                    break;
                case "--history":
                    history = true;
                    break;
                case "--extensions":
                    extFile = Next();
                    break;
                case "--owners":
                    ownerFile = Next();
                    break;
                case "--ages":
                    ageFile = Next();
                    break;
                case "--top-files":
                    topFiles = int.Parse(Next(), CultureInfo.InvariantCulture);
                    topFile = Next();
                    break;
                default:
                    return Fail("unknown option " + a[i]);
            }
        }
        var s = new ScanSession(a[0], opt);
        var sw = Stopwatch.StartNew();
        await Scanner.ScanAsync(s, CancellationToken.None);
        sw.Stop();
        var r = s.Root;
        Console.WriteLine($"{r.FullPath}: {SizeFormat.Format(r.Size, exp.Unit)} ({SizeFormat.Format(r.Allocated, exp.Unit)} allocated), {SizeFormat.Count(r.FileCount)} files, {SizeFormat.Count(r.FolderCount)} folders");
        Console.WriteLine($"{s.MethodUsed}, {sw.Elapsed.TotalSeconds:N1} s, {s.Errors.Count} errors, {s.State}");
        foreach (var d in r.Dirs.Take(top))
            Console.WriteLine($"  {SizeFormat.Format(d.Size, exp.Unit),12}  {d.FileCount,12:N0}  {d.Name}");

        foreach (var e in exports)
            Write(ExportBuilder.Tree(s, r, exp), e, exp);
        if (extFile != null)
            Write(ExportBuilder.Stats("File types: " + r.FullPath, "Extension", Statistics.ByExtension(r, new FileGroups()), s, r, true), extFile, exp);
        if (ownerFile != null)
            Write(ExportBuilder.Stats("Owners: " + r.FullPath, "Owner", Statistics.ByOwner(r, s.Owners), s, r), ownerFile, exp);
        if (ageFile != null)
            Write(ExportBuilder.Stats("Age of files: " + r.FullPath, "Interval", Statistics.ByAge(r, new AgeIntervals()), s, r), ageFile, exp);
        if (topFile != null)
            Write(ExportBuilder.Items($"Largest {topFiles} files: {r.FullPath}", Statistics.TopFiles(r, topFiles), s,
                [TreeColumn.Name, TreeColumn.Size, TreeColumn.Allocated, TreeColumn.Modified, TreeColumn.Owner, TreeColumn.FullPath]), topFile, exp);
        if (index != null)
        {
            var p = Expand(index);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p))!);
            IndexFile.Save(s, p);
            Console.WriteLine("saved index " + p);
        }
        if (history)
        {
            new HistoryStore().Record(s);
            Console.WriteLine("history updated");
        }
        return s.State == ScanState.Done && s.Errors.IsEmpty ? 0 : 2;
    }

    private static async Task<int> CompareAsync(string[] a)
    {
        if (a.Length < 2)
            return Fail("compare needs two paths or index files");
        string? export = null;
        var top = 25;
        for (var i = 2; i < a.Length; i++)
        {
            if (a[i] == "--export" && i + 1 < a.Length)
                export = a[++i];
            else if (a[i] == "--top" && i + 1 < a.Length)
                top = int.Parse(a[++i], CultureInfo.InvariantCulture);
        }
        var opt = new ScanOptions { CollectOwners = false };
        var now = await LoadOrScanAsync(a[0], opt);
        var then = await LoadOrScanAsync(a[1], opt);
        var r = Comparison.Compare(now, then, a[1]);
        Console.WriteLine($"then {SizeFormat.Format(r.OldSize)}  now {SizeFormat.Format(r.NewSize)}  change {SizeFormat.FormatChange(r.Change)}");
        foreach (var c in r.Changed.Take(top))
            Console.WriteLine($"  {SizeFormat.FormatChange(c.Change),12}  {c.Path}");
        foreach (var c in r.Added.Take(top))
            Console.WriteLine($"  {"new " + SizeFormat.Format(c.NewSize),12}  {c.Path}");
        foreach (var c in r.Removed.Take(top))
            Console.WriteLine($"  {"gone " + SizeFormat.Format(c.OldSize),12}  {c.Path}");
        if (export != null)
        {
            var o = new ExportSettings { MaxDepth = 3, Columns = [TreeColumn.Name, TreeColumn.Size, TreeColumn.SizeChange, TreeColumn.Files, TreeColumn.Modified] };
            Write(ExportBuilder.Tree(now, now.Root, o), export, o);
        }
        return 0;
    }

    private static async Task<int> SearchAsync(string[] a)
    {
        if (a.Length == 0)
            return Fail("search needs a path");
        var c = new SearchCriteria();
        string? export = null;
        for (var i = 1; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i])
            {
                case "--template":
                    var t = Next() switch
                    {
                        "large" => SearchTemplate.LargeFiles,
                        "old" => SearchTemplate.OldFiles,
                        "temp" => SearchTemplate.TemporaryFiles,
                        "duplicates" or "dup" => SearchTemplate.Duplicates,
                        "longpaths" => SearchTemplate.LongPaths,
                        _ => SearchTemplate.Custom,
                    };
                    var keep = c;
                    c = SearchCriteria.FromTemplate(t);
                    c.Include.AddRange(keep.Include);
                    break;
                case "--name":
                    c.Include.Add(new FilterRule { Pattern = Next(), Exclude = false });
                    break;
                case "--min-size":
                    c.MinSize = SizeFormat.TryParse(Next(), out var mn) ? mn : throw new FormatException("bad --min-size");
                    break;
                case "--max-size":
                    c.MaxSize = SizeFormat.TryParse(Next(), out var mx) ? mx : throw new FormatException("bad --max-size");
                    break;
                case "--older-days":
                    c.OlderThan = DateTime.Now.AddDays(-double.Parse(Next(), CultureInfo.InvariantCulture));
                    break;
                case "--newer-days":
                    c.NewerThan = DateTime.Now.AddDays(-double.Parse(Next(), CultureInfo.InvariantCulture));
                    break;
                case "--owner":
                    c.Owner = Next();
                    break;
                case "--hash":
                    c.DuplicatesHash = Next() switch { "sha256" => HashKind.Sha256, "none" => HashKind.None, _ => HashKind.Md5 };
                    break;
                case "--max":
                    c.MaxResults = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--export":
                    export = Next();
                    break;
                default:
                    return Fail("unknown option " + a[i]);
            }
        }
        var results = await SearchEngine.SearchPathsAsync([a[0]], c, new ScanOptions(), null, CancellationToken.None);
        if (c.Template == SearchTemplate.Duplicates)
            results = SearchEngine.FindDuplicates(results, c, null, CancellationToken.None);
        foreach (var r in results.Take(200))
            Console.WriteLine($"{(r.Group > 0 ? $"[{r.Group}] " : "")}{SizeFormat.Format(r.Size),12}  {r.FullPath}");
        if (results.Count > 200)
            Console.WriteLine($"… {results.Count - 200} more");
        Console.WriteLine($"{results.Count} results, {SizeFormat.Format(results.Sum(r => r.Size))}");
        if (export != null)
        {
            var byItem = results.ToDictionary(r => r.Item);
            var t = ExportBuilder.Items(c.Name, results.Select(r => r.Item), null, [TreeColumn.Name, TreeColumn.Size, TreeColumn.Modified, TreeColumn.Accessed],
                i => [byItem[i].FullPath, (long)byItem[i].Group, byItem[i].Hash ?? ""],
                [new ExportColumn("Path", ColumnKind.Text), new ExportColumn("Group", ColumnKind.Number), new ExportColumn("Checksum", ColumnKind.Text)]);
            Write(t, export, new ExportSettings());
        }
        return 0;
    }

    private static int History(string[] a)
    {
        if (a.Length == 0)
            return Fail("history needs a path");
        var h = new HistoryStore();
        if (a.Length >= 3 && a[1] == "--export")
        {
            h.ExportCsv(Expand(a[2]), a[0]);
            Console.WriteLine("wrote " + a[2]);
            return 0;
        }
        foreach (var p in h.Get(a[0]))
            Console.WriteLine($"{p.At:g}  {SizeFormat.Format(p.Size),12}  {SizeFormat.Count(p.Files),12} files  free {SizeFormat.Format(p.VolumeFree)}");
        return 0;
    }
}
