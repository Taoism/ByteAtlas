using System.Diagnostics;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Cli;

internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Help);
            return 0;
        }
        try
        {
            return args[0] switch
            {
                "scan" => await ScanAsync(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'. Run byteatlas-cli --help."),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Fail(e.Message);
        }
    }

    private const string Help = """
        byteatlas-cli — disk space analysis from the command line

          byteatlas-cli scan <path> [options]
            --method auto|fs|mft     how to read the drive (default auto)
            --threads N              threads for the folder walk
            --top N                  print the N largest folders (default 20)
        """;

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    private static async Task<int> ScanAsync(string[] a)
    {
        if (a.Length == 0)
            return Fail("scan needs a path");
        var opt = new ScanOptions();
        var top = 20;
        for (var i = 1; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--method":
                    opt.Method = a[++i] switch { "fs" => ScanMethod.FileSystem, "mft" => ScanMethod.Mft, _ => ScanMethod.Auto };
                    break;
                case "--threads":
                    opt.Threads = int.Parse(a[++i]);
                    break;
                case "--top":
                    top = int.Parse(a[++i]);
                    break;
            }
        }
        var s = new ScanSession(a[0], opt);
        var sw = Stopwatch.StartNew();
        await Scanner.ScanAsync(s, CancellationToken.None);
        sw.Stop();
        var r = s.Root;
        Console.WriteLine($"{r.FullPath}: {SizeFormat.Format(r.Size)} ({SizeFormat.Format(r.Allocated)} on disk), {SizeFormat.Count(r.FileCount)} files, {SizeFormat.Count(r.FolderCount)} folders");
        Console.WriteLine($"{s.MethodUsed}, {sw.Elapsed.TotalSeconds:N1} s, {s.Errors.Count} errors, state {s.State}");
        foreach (var d in r.Dirs.Take(top))
            Console.WriteLine($"  {SizeFormat.Format(d.Size),12}  {d.FileCount,10:N0}  {d.Name}");
        return s.State == ScanState.Done ? 0 : 2;
    }
}
