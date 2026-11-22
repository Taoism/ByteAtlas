using System.IO.Compression;
using ByteAtlas.Analysis;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Operations;
using ByteAtlas.Scanning;
using ByteAtlas.Search;
using ByteAtlas.Storage;

namespace ByteAtlas.Tests;

/// <summary>A small folder tree on disk, removed afterwards.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "byteatlas-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempTree()
    {
        Directory.CreateDirectory(Root);
        Write("a.txt", 1000);
        Write("b.log", 5000);
        Write(@"sub\c.bin", 20000);
        Write(@"sub\d.txt", 300);
        Write(@"sub\deep\e.tmp", 70000);
        Write(@"other\dup1.dat", 9000, seed: 7);
        Write(@"other\dup2.dat", 9000, seed: 7);
        Write(@"other\notdup.dat", 9000, seed: 8);
        Directory.CreateDirectory(Path.Combine(Root, "empty"));
    }

    public string Write(string rel, int size, int seed = 1)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(p, data);
        return p;
    }

    public ScanSession Scan(ScanOptions? o = null)
    {
        var s = new ScanSession(Root, o ?? new ScanOptions { Method = ScanMethod.FileSystem, CollectOwners = false });
        Scanner.Scan(s, CancellationToken.None);
        return s;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 Bytes")]
    [InlineData(1023, "1,023 Bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1073741824, "1.0 GB")]
    public void FormatsSizes(long bytes, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, SizeFormat.Format(bytes));
    }

    [Theory]
    [InlineData("500 MB", 500L << 20)]
    [InlineData("1.5G", 1610612736L)]
    [InlineData("2048", 2048L)]
    [InlineData("10kb", 10240L)]
    public void ParsesSizes(string text, long expected)
    {
        Assert.True(SizeFormat.TryParse(text, out var v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("report.TXT", "*.txt", true)]
    [InlineData("report.txt", "rep?rt.*", true)]
    [InlineData("report.txt", "*.log", false)]
    [InlineData("a", "*", true)]
    public void Wildcards(string name, string pattern, bool match) => Assert.Equal(match, Wildcard.IsMatch(name, pattern));

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _old = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _old;
    }
}

public class ScanTests
{
    [Fact]
    public void FolderWalkCountsEverything()
    {
        using var t = new TempTree();
        var s = t.Scan();
        Assert.Equal(ScanState.Done, s.State);
        Assert.Equal(1000 + 5000 + 20000 + 300 + 70000 + 3 * 9000, s.Root.Size);
        Assert.Equal(8, s.Root.FileCount);
        Assert.Equal(4, s.Root.FolderCount); // sub, sub\deep, other, empty
        var sub = s.Root.FindRelative("sub")!;
        Assert.Equal(90300, sub.Size);
        Assert.Equal(3, sub.FileCount);
        Assert.True(s.Root.Allocated >= s.Root.Size);
        Assert.Equal("sub", s.Root.Dirs[0].Name); // sorted by size
        Assert.False(s.Root.Pending);
    }

    [Fact]
    public void MftMatchesFolderWalk()
    {
        if (!OperatingSystem.IsWindows() || !MftScanner.CanScan(Path.GetTempPath(), out _))
            return; // needs administrator rights and NTFS
        using var t = new TempTree();
        var walk = t.Scan();
        var mft = t.Scan(new ScanOptions { Method = ScanMethod.Mft, CollectOwners = true });
        Assert.Equal(ScanState.Done, mft.State);
        Assert.Equal("NTFS master file table", mft.MethodUsed);
        Assert.Equal(walk.Root.Size, mft.Root.Size);
        Assert.Equal(walk.Root.FileCount, mft.Root.FileCount);
        Assert.Equal(walk.Root.FolderCount, mft.Root.FolderCount);
        Assert.Equal(t.Root, mft.Root.FullPath);
        Assert.Equal(20000, mft.Root.FindRelative("sub")!.Files.Single(f => f.Name == "c.bin").Size);
        Assert.True(mft.Root.OwnerId >= 0, "folders get an owner too");
        Assert.All(mft.Root.AllFiles(), f => Assert.True(f.OwnerId >= 0));
    }

    [Fact]
    public void SmallFolderUsesQuickWalk()
    {
        if (!OperatingSystem.IsWindows() || !MftScanner.CanScan(Path.GetTempPath(), out _))
            return;
        using var t = new TempTree();
        var s = t.Scan(new ScanOptions { Method = ScanMethod.Auto, CollectOwners = false });
        Assert.StartsWith("File system", s.MethodUsed);
        Assert.Equal(1000 + 5000 + 20000 + 300 + 70000 + 3 * 9000, s.Root.Size);
    }

    [Fact]
    public void FutureDatesDoNotMoveFolderDates()
    {
        using var t = new TempTree();
        var f = t.Write(Path.Combine("sub", "future.txt"), 10);
        File.SetLastWriteTime(f, DateTime.Now.AddYears(3));
        var s = t.Scan();
        Assert.True(s.Root.Modified < DateTime.Now.AddDays(1));
        var ages = Statistics.ByAge(s.Root, new AgeIntervals());
        Assert.Equal(Statistics.FutureLabel, ages[0].Key);
        Assert.Equal(1, ages[0].Files);
    }

    [Fact]
    public void ExcludeFilterLeavesFilesOut()
    {
        using var t = new TempTree();
        var o = new ScanOptions { Method = ScanMethod.FileSystem, CollectOwners = false };
        o.Filters.Add(new FilterRule { Pattern = "*.tmp", Exclude = true });
        o.Filters.Add(new FilterRule { Pattern = "other", Exclude = true, Folders = true, Kind = PatternKind.WholeName });
        var s = t.Scan(o);
        Assert.Equal(1000 + 5000 + 20000 + 300, s.Root.Size);
        Assert.Null(s.Root.FindRelative("other"));
    }

    [Fact]
    public void RefreshPicksUpChanges()
    {
        using var t = new TempTree();
        var s = t.Scan();
        t.Write(@"sub\new.bin", 4000);
        var sub = s.Root.FindRelative("sub")!;
        Scanner.Refresh(s, sub, true, CancellationToken.None);
        Assert.Equal(94300, sub.Size);
        Assert.Equal(1000 + 5000 + 20000 + 300 + 70000 + 3 * 9000 + 4000, s.Root.Size);
        Assert.Equal(9, s.Root.FileCount);
    }

    [Fact]
    public void FolderTotalsAreConsistent()
    {
        using var t = new TempTree();
        var s = t.Scan();
        foreach (var d in s.Root.AllFolders())
        {
            Assert.Equal(d.Files.Sum(f => f.Size) + d.Dirs.Sum(c => c.Size), d.Size);
            Assert.Equal(d.Files.Length + d.Dirs.Sum(c => c.FileCount), d.FileCount);
        }
    }
}

public class AnalysisTests
{
    [Fact]
    public void ExtensionsOwnersAgesTop()
    {
        using var t = new TempTree();
        var s = t.Scan();
        var ext = Statistics.ByExtension(s.Root, new FileGroups());
        Assert.Equal("tmp", ext[0].Key);
        Assert.Equal(1300, ext.Single(e => e.Key == "txt").Size);
        Assert.Equal(1.0, ext.Sum(e => e.Percent), 6);

        var ages = Statistics.ByAge(s.Root, new AgeIntervals());
        Assert.Equal(8, ages[0].Files); // all just written

        var top = Statistics.TopFiles(s.Root, 2);
        Assert.Equal(["e.tmp", "c.bin"], top.Select(f => f.Name));

        Assert.Equal("deep", Statistics.DeepestFolder(s.Root).Name);
    }

    [Fact]
    public void ViewFilterTotals()
    {
        using var t = new TempTree();
        var s = t.Scan();
        ViewFilter.ForExtensions(["txt"]).Apply(s.Root);
        Assert.Equal(1300, s.Root.FilteredSize);
        Assert.Equal(2, s.Root.FilteredFiles);
        Assert.Equal(300, s.Root.FindRelative("sub")!.FilteredSize);
    }

    [Fact]
    public void ComparisonFindsGrowth()
    {
        using var t = new TempTree();
        var before = t.Scan();
        t.Write(@"sub\grow.bin", 50000);
        Directory.Delete(Path.Combine(t.Root, "other"), true);
        Directory.CreateDirectory(Path.Combine(t.Root, "fresh"));
        t.Write(@"fresh\x.bin", 100);
        var after = t.Scan();
        var r = Comparison.Compare(after, before, "test");
        Assert.Equal(50000 + 100 - 27000, r.Change);
        Assert.Contains(r.Removed, c => c.Path.EndsWith("other", StringComparison.Ordinal));
        Assert.Contains(r.Added, c => c.Path.EndsWith("fresh", StringComparison.Ordinal));
        Assert.Equal(50000, after.Root.FindRelative("sub")!.SizeChange);
        Assert.Equal(-1, after.Root.FindRelative("fresh")!.CompareSize);
    }
}

public class SearchTests
{
    [Fact]
    public void TemplatesFindFiles()
    {
        using var t = new TempTree();
        var s = t.Scan();
        var temp = SearchEngine.Search([s], SearchCriteria.FromTemplate(SearchTemplate.TemporaryFiles));
        Assert.Equal(["b.log", "e.tmp"], temp.Select(r => r.Name).Order());

        var big = new SearchCriteria { MinSize = 10000, SortBySizeDesc = true };
        Assert.Equal(["e.tmp", "c.bin"], SearchEngine.Search([s], big).Select(r => r.Name));
    }

    [Fact]
    public void DuplicatesByContent()
    {
        using var t = new TempTree();
        var s = t.Scan();
        var all = SearchEngine.Search([s], SearchCriteria.FromTemplate(SearchTemplate.AllFiles));
        var dups = SearchEngine.FindDuplicates(all, SearchCriteria.FromTemplate(SearchTemplate.Duplicates), null, CancellationToken.None);
        Assert.Equal(["dup1.dat", "dup2.dat"], dups.Select(d => d.Name).Order());
        Assert.All(dups, d => Assert.Equal(1, d.Group));
    }

    [Fact]
    public void HardlinkDedupKeepsContent()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var t = new TempTree();
        var a = Path.Combine(t.Root, @"other\dup1.dat");
        var b = Path.Combine(t.Root, @"other\dup2.dat");
        var r = FileOperations.ReplaceWithHardlinks(a, [b]);
        Assert.Equal(1, r.Done);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        var o = new ScanOptions { Method = ScanMethod.FileSystem, CollectOwners = false, TrackHardlinksAndStreams = true };
        var s = t.Scan(o);
        var other = s.Root.FindRelative("other")!;
        Assert.Equal(2, other.Files.Single(f => f.Name == "dup2.dat").LinkCount);
        Assert.True(other.Files.Count(f => f.Allocated == 0) == 1); // the second link takes no extra space
    }
}

public class StorageTests
{
    [Theory]
    [InlineData(".sqlite")]
    [InlineData(".xml")]
    public void IndexRoundTrip(string ext)
    {
        using var t = new TempTree();
        var s = t.Scan();
        s.Title = "Test scan";
        var file = Path.Combine(t.Root, "..", Path.GetFileName(t.Root) + ext);
        try
        {
            IndexFile.Save(s, file);
            var l = IndexFile.Load(file);
            Assert.Equal(s.Root.Size, l.Root.Size);
            Assert.Equal(s.Root.Allocated, l.Root.Allocated);
            Assert.Equal(s.Root.FileCount, l.Root.FileCount);
            Assert.Equal(s.Root.FolderCount, l.Root.FolderCount);
            Assert.Equal("Test scan", l.Title);
            Assert.Equal(s.Root.FullPath, l.Root.FullPath);
            Assert.Equal(20000, l.Root.FindRelative("sub")!.Files.Single(f => f.Name == "c.bin").Size);

            // folders only keeps the totals
            IndexFile.Save(s, file, includeFiles: false);
            var f2 = IndexFile.Load(file);
            Assert.Equal(s.Root.Size, f2.Root.Size);
            Assert.Empty(f2.Root.Files);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public void HistoryStoresPoints()
    {
        var file = Path.Combine(Path.GetTempPath(), "byteatlas-history-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            var h = new HistoryStore(file);
            h.Add(new HistoryPoint(@"C:\Data", DateTime.Now.AddDays(-10), 100, 120, 5, 1, 0, 0));
            h.Add(new HistoryPoint(@"c:\data\", DateTime.Now, 200, 220, 6, 1, 0, 0));
            Assert.Equal([100L, 200L], h.Get(@"C:\DATA").Select(p => p.Size));
            Assert.Equal(1, h.Purge(@"C:\Data", DateTime.Now.AddDays(-1)));
            var csv = file + ".csv";
            h.ExportCsv(csv);
            Assert.Equal(1, h.ImportCsv(csv));
            Assert.Equal(2, h.Get(@"C:\Data").Count);
            File.Delete(csv);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}

public class ExportTests
{
    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    [InlineData(".html")]
    [InlineData(".xlsx")]
    [InlineData(".pdf")]
    public void WritesEveryFormat(string ext)
    {
        using var t = new TempTree();
        var s = t.Scan();
        var o = new ExportSettings { MaxDepth = 0, IncludeFiles = true };
        var table = ExportBuilder.Tree(s, s.Root, o);
        Assert.Equal(1 + 4 + 8, table.Rows.Count); // root + folders + files
        var file = Path.Combine(Path.GetTempPath(), "byteatlas-export-" + Guid.NewGuid().ToString("N")[..8] + ext);
        try
        {
            Exporters.Write(table, file, o);
            Assert.True(new FileInfo(file).Length > 100);
            if (ext == ".xlsx")
            {
                using var zip = ZipFile.OpenRead(file);
                var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
                Assert.Contains("e.tmp", sheet);
                Assert.Contains("outlineLevel=\"2\"", sheet);
            }
            if (ext == ".csv")
                Assert.Contains("c.bin", File.ReadAllText(file));
            if (ext == ".pdf")
                Assert.StartsWith("%PDF", File.ReadAllText(file)[..4]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FilesRowWhenFilesNotListed()
    {
        using var t = new TempTree();
        var s = t.Scan();
        var table = ExportBuilder.Tree(s, s.Root, new ExportSettings { MaxDepth = 1 });
        Assert.Contains(table.Rows, r => (r.Values[0] as string)?.StartsWith("[2 Files]", StringComparison.Ordinal) == true);
    }
}

public class RenamerTests
{
    [Fact]
    public void RulesApplyInOrder()
    {
        var r = new BulkRenamer();
        r.Rules.Add(new RenameRule { Type = RenameRuleType.Replace, Search = " ", ReplaceWith = "_" });
        r.Rules.Add(new RenameRule { Type = RenameRuleType.Serialize, Where = InsertWhere.Prefix, StartValue = 1, PadLength = 3 });
        r.Rules.Add(new RenameRule { Type = RenameRuleType.Insert, Text = "-", Where = InsertWhere.Position, Position = 3 });
        r.Rules.Add(new RenameRule { Type = RenameRuleType.Case, Case = CaseMode.Lower, Scope = RenameScope.ExtensionOnly });
        Assert.Equal("001-My_Photo.jpg", r.Apply("My Photo.JPG", 0));
        Assert.Equal("002-b.jpg", r.Apply("b.JPG", 1));
    }

    [Fact]
    public void ExecuteAndUndo()
    {
        using var t = new TempTree();
        var r = new BulkRenamer();
        r.Rules.Add(new RenameRule { Type = RenameRuleType.Insert, Text = "x_", Where = InsertWhere.Prefix });
        var files = new[] { Path.Combine(t.Root, "a.txt"), Path.Combine(t.Root, "b.log") };
        var preview = r.Preview(files);
        Assert.All(preview, p => Assert.Null(p.Problem));
        Assert.Equal(2, r.Execute(preview).Done);
        Assert.True(File.Exists(Path.Combine(t.Root, "x_a.txt")));
        Assert.Equal(2, r.Undo().Done);
        Assert.True(File.Exists(Path.Combine(t.Root, "a.txt")));
    }
}

public class WindowsFeatureTests
{
    [Fact]
    public void PermissionsAreRead()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var t = new TempTree();
        var p = ByteAtlas.Security.Permissions.Read(t.Root);
        Assert.Null(p.Error);
        Assert.NotEmpty(p.Entries);
        Assert.NotEmpty(p.Owner);
        Assert.Contains("Full control", p.Summary);
        Assert.Equal("Inherited", p.InheritanceText);
        Assert.Equal("Modify", ByteAtlas.Security.Permissions.Describe(System.Security.AccessControl.FileSystemRights.Modify));
    }

    [Fact]
    public void PermissionSearchFindsFoldersOfTheUser()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var t = new TempTree();
        var s = t.Scan();
        var c = SearchCriteria.FromTemplate(SearchTemplate.Permissions);
        c.PermissionExplicitOnly = false;
        var r = SearchEngine.Search([s], c);
        Assert.Contains(r, x => x.FullPath == t.Root);
        Assert.Equal(5, r.Count); // root + 4 folders, all inherited from the temp folder
        Assert.All(r, x => Assert.Contains("(inherited)", x.Detail));
        c.PermissionExplicitOnly = true;
        Assert.Empty(SearchEngine.Search([s], c));
    }

    [Fact]
    public void CompactCompressionRoundTrip()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var t = new TempTree();
        var f = Path.Combine(t.Root, "text.txt");
        File.WriteAllText(f, string.Concat(Enumerable.Repeat("ByteAtlas compresses this line very well. ", 50_000)));
        var r = FileOperations.Compress([f], ByteAtlas.Native.CompressionFormat.Xpress4K);
        Assert.Equal(0, r.Failed);
        Assert.Equal(ByteAtlas.Native.CompressionFormat.Xpress4K, ByteAtlas.Native.Compression.Get(f, File.GetAttributes(f)));
        var walk = t.Scan(new ScanOptions { Method = ScanMethod.FileSystem, CollectOwners = false, DetectCompactCompression = true });
        var item = walk.Root.Files.Single(x => x.Name == "text.txt");
        Assert.True(item.Allocated < item.Size / 4, $"allocated {item.Allocated} of {item.Size}");
        if (ByteAtlas.Scanning.MftScanner.CanScan(t.Root, out _))
        {
            var mft = t.Scan(new ScanOptions { Method = ScanMethod.Mft, CollectOwners = false });
            var m = mft.Root.Files.Single(x => x.Name == "text.txt");
            Assert.Equal(item.Size, m.Size);
            Assert.InRange(m.Allocated, 1, item.Size / 4);
        }
        Assert.Equal(0, FileOperations.Compress([f], ByteAtlas.Native.CompressionFormat.Ntfs).Failed);
        Assert.Equal(ByteAtlas.Native.CompressionFormat.Ntfs, ByteAtlas.Native.Compression.Get(f, File.GetAttributes(f)));
        Assert.Equal(0, FileOperations.Compress([f], ByteAtlas.Native.CompressionFormat.None).Failed);
        Assert.Equal(ByteAtlas.Native.CompressionFormat.None, ByteAtlas.Native.Compression.Get(f, File.GetAttributes(f)));
    }

    [Fact]
    public void GroupSumsScans()
    {
        using var a = new TempTree();
        using var b = new TempTree();
        var sa = a.Scan();
        var sb = b.Scan();
        var g = ScanGroup.Build([sa, sb]);
        Assert.True(g.IsGroup);
        Assert.Equal(sa.Root.Size + sb.Root.Size, g.Root.Size);
        Assert.Equal(sa.Root.FileCount * 2, g.Root.FileCount);
        Assert.Equal(2, g.Root.Dirs.Length);
        Assert.Null(sa.Root.Parent); // the scans keep their own paths
        Assert.Equal(a.Root, sa.Root.FullPath);
        Assert.Equal(16, Statistics.TopFiles(g.Root, 100).Count);
    }
}

public class RefreshTests
{
    [Fact]
    public void JournalRefreshMatchesFullScan()
    {
        if (!OperatingSystem.IsWindows() || !ByteAtlas.Scanning.MftScanner.CanScan(Path.GetTempPath(), out _))
            return;
        using var t = new TempTree();
        var s = t.Scan(new ScanOptions { Method = ScanMethod.Mft, CollectOwners = false });
        Assert.NotNull(s.Usn);
        // changes: a new file, a deleted file, a new folder with files, a removed folder, a grown file
        t.Write(@"sub\new.bin", 4321);
        File.Delete(Path.Combine(t.Root, "a.txt"));
        t.Write(@"fresh\one.dat", 1000);
        t.Write(@"fresh\deep\two.dat", 2000);
        Directory.Delete(Path.Combine(t.Root, "empty"));
        t.Write(@"other\notdup.dat", 50000, seed: 9);
        Assert.True(Scanner.TryJournalRefresh(s, CancellationToken.None));
        Assert.StartsWith("change journal", s.Timings);
        var full = t.Scan(new ScanOptions { Method = ScanMethod.FileSystem, CollectOwners = false });
        Assert.Equal(full.Root.Size, s.Root.Size);
        Assert.Equal(full.Root.FileCount, s.Root.FileCount);
        Assert.Equal(full.Root.FolderCount, s.Root.FolderCount);
        Assert.NotNull(s.Root.FindRelative(@"fresh\deep"));
        Assert.Null(s.Root.FindRelative("empty"));
        Assert.True(s.Root.FindRelative(@"fresh\deep")!.Frn > 0, "new folders get their reference for the next refresh");
        // and once more on top
        t.Write(@"fresh\deep\three.dat", 3000);
        Assert.True(Scanner.TryJournalRefresh(s, CancellationToken.None));
        Assert.Equal(full.Root.Size + 3000, s.Root.Size);
    }

    [Fact]
    public void CacheRoundTrip()
    {
        using var t = new TempTree();
        var s = t.Scan();
        s.Title = "cached";
        var dir = Path.Combine(t.Root, "..", "cache-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            ScanCache.Save(s, dir);
            var c = ScanCache.Load(dir, t.Root, new ScanOptions())!;
            Assert.Equal(s.Root.Size, c.Root.Size);
            Assert.Equal(s.Root.Allocated, c.Root.Allocated);
            Assert.Equal(s.Root.FileCount, c.Root.FileCount);
            Assert.Equal(s.Root.FolderCount, c.Root.FolderCount);
            Assert.Equal(t.Root, c.Root.FullPath);
            Assert.Equal("cached", c.Title);
            Assert.EndsWith("(cached)", c.MethodUsed);
            Assert.Null(ScanCache.Load(dir, t.Root + "x", new ScanOptions()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
