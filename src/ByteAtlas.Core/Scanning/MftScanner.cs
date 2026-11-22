using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ByteAtlas.Model;
using ByteAtlas.Native;

namespace ByteAtlas.Scanning;

/// <summary>
/// Reads the NTFS master file table of a local volume directly (needs administrator rights).
/// Far faster than walking folders: the table is read sequentially (the next chunk is read while the
/// current one is parsed on all cores) and the tree is built from it in memory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftScanner
{
    private readonly ScanSession _s;
    private readonly ScanOptions _o;

    public MftScanner(ScanSession session)
    {
        _s = session;
        _o = session.Options;
    }

    /// <summary>Can the volume of <paramref name="path"/> be read this way (NTFS, local, readable volume handle)?</summary>
    public static bool CanScan(string path, out string reason)
    {
        reason = "";
        var v = VolumeInfo.Get(path);
        if (v.IsNetwork || !v.Root.EndsWith(":\\", StringComparison.Ordinal) || v.Root.Length != 3)
        {
            reason = "not a local drive";
            return false;
        }
        if (!string.Equals(v.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            reason = v.FileSystem + " volume";
            return false;
        }
        using var h = OpenVolume(v.Root);
        if (h.IsInvalid)
        {
            reason = "no administrator rights";
            return false;
        }
        return true;
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenVolume(string root, bool unbuffered = false) =>
        Win32.CreateFile(@"\\.\" + root[..2], Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING,
            unbuffered ? FileFlagNoBuffering : 0, IntPtr.Zero);

    // reads bypass the file cache: no 4 GB copied through memory for a big table (needs sector-aligned buffers)
    private const uint FileFlagNoBuffering = 0x20000000;

    // ---------- record data collected in the pass (one compact struct per record) ----------

    private struct Rec
    {
        public string? Name;
        public List<(int Parent, string Name)>? Links;
        public long Size, Alloc, StreamSize, StreamAlloc;
        public long Created, Modified, Accessed;
        public int Parent;
        public int Streams;
        public uint Attr;
        public uint SecurityId;
        public byte NameRank; // namespace of the kept name; 9 = only a DOS 8.3 alias so far
        public bool IsDir;
        public bool InUse;
        public bool Wof;
    }

    private const int RootRecord = 5;
    private const int UsaStride = 512; // NTFS update sequence stride, whatever the sector size

    private int _recordSize, _cluster;

    public void Run(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var vol = VolumeInfo.Get(_s.Path);
        _s.Usn = UsnJournal.Query(vol.Root);
        using var h = OpenVolume(vol.Root);
        if (h.IsInvalid)
            throw new UnauthorizedAccessException("Cannot open the volume (administrator rights needed).");
        using var fs = new FileStream(h, FileAccess.Read, 0, false);

        var boot = new byte[512];
        ReadAt(fs, 0, boot);
        if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS")
            throw new IOException("Not an NTFS volume.");
        var sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        _cluster = sectorSize * boot[0x0D];
        var mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        var cpr = (sbyte)boot[0x40];
        _recordSize = cpr > 0 ? cpr * _cluster : 1 << -cpr;
        _s.ClusterSize = _cluster;
        _s.MethodUsed = "NTFS master file table";

        // record 0 = $MFT itself: its $DATA runs say where the table lives
        var rec0 = new byte[_recordSize];
        ReadAt(fs, mftLcn * _cluster, rec0);
        if (!Fixup(rec0))
            throw new IOException("Damaged $MFT record.");
        var runs = new List<(long Lcn, long Clusters)>();
        long mftBytes = 0;
        var extensionRefs = new List<long>();
        ParseMftDataRuns(rec0, runs, ref mftBytes, extensionRefs);
        foreach (var r in extensionRefs)
        {
            // fragmented $MFT: the remaining runs sit in extension records (inside the part already known)
            var buf = new byte[_recordSize];
            if (ReadRecordFromRuns(fs, runs, r, buf) && Fixup(buf))
            {
                long dummy = 0;
                ParseMftDataRuns(buf, runs, ref dummy, null);
            }
        }
        var total = (int)(mftBytes / _recordSize);
        var recs = new Rec[total];
        var extensions = new ConcurrentQueue<(int Index, byte[] Data)>();

        // the table as 4 MB pieces: read piece i+1 while piece i is parsed in parallel
        var pieces = new List<(long Offset, int Length, int FirstIndex)>();
        var chunk = Math.Max(_recordSize, 8 << 20) / _recordSize * _recordSize;
        long next = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var offset = lcn * _cluster;
            var left = clusters * _cluster;
            while (left > 0 && next < total)
            {
                var n = (int)Math.Min(left, chunk);
                pieces.Add((offset, n, (int)next));
                next += n / _recordSize;
                offset += n;
                left -= n;
            }
        }
        // several readers, each with its own volume handle (one handle serializes its reads), each parsing what it read
        // SSD: 4 readers (measured on NVMe: 4 beats 6, 8 and 16); spinning disk: one reader, front to back
        var hdd = Win32.HasSeekPenalty(h) != false;
        var readers = hdd ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        if (int.TryParse(Environment.GetEnvironmentVariable("BYTEATLAS_MFT_READERS"), out var forced) && forced > 0)
            readers = forced;
        long done = 0;
        var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = readers };
        Parallel.ForEach(pieces, po,
            () => (Handle: OpenVolume(vol.Root, unbuffered: true), Buffer: new AlignedBuffer(chunk)),
            (piece, _, local) =>
            {
                _s.PauseGate.Wait(ct);
                if (local.Handle.IsInvalid)
                    throw new UnauthorizedAccessException("Cannot open the volume (administrator rights needed).");
                var span = local.Buffer.Span[..piece.Length];
                var read = 0;
                while (read < span.Length)
                {
                    var r = RandomAccess.Read(local.Handle, span[read..], piece.Offset + read);
                    if (r <= 0)
                        throw new IOException("Unexpected end of volume.");
                    read += r;
                }
                var count = Math.Min(piece.Length / _recordSize, total - piece.FirstIndex);
                for (var k = 0; k < count; k++)
                    ParseRecord(span.Slice(k * _recordSize, _recordSize), piece.FirstIndex + k, recs, extensions);
                var d = Interlocked.Add(ref done, count);
                _s.Fraction = 0.7 * d / Math.Max(1, total);
                _s.CurrentPath = $"Reading master file table: {SizeFormat.Count(d)} of {SizeFormat.Count(total)} records";
                return local;
            },
            local =>
            {
                local.Handle.Dispose();
                local.Buffer.Dispose();
            });
        // attributes that did not fit in the base record
        foreach (var (_, data) in extensions)
        {
            var baseRef = (int)(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x20)) & 0xFFFFFFFFFFFF);
            if (baseRef >= 0 && baseRef < recs.Length && recs[baseRef].InUse)
                ParseAttributes(data, ref recs[baseRef]);
        }
        var readTime = watch.Elapsed;

        BuildTree(recs, vol, ct, out var ownerTime);
        _s.Timings = $"table {total:N0} records, {readers} reader(s){(hdd ? " (spinning disk)" : "")}: read + parse {readTime.TotalSeconds:N1} s, tree {(watch.Elapsed - readTime - ownerTime).TotalSeconds:N1} s, owners {ownerTime.TotalSeconds:N1} s";
    }

    private static void ReadAt(FileStream fs, long offset, Span<byte> buf)
    {
        fs.Position = offset;
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf[read..]);
            if (n <= 0)
                throw new IOException("Unexpected end of volume.");
            read += n;
        }
    }

    private bool ReadRecordFromRuns(FileStream fs, List<(long Lcn, long Clusters)> runs, long record, byte[] buf)
    {
        var byteOffset = record * _recordSize;
        foreach (var (lcn, clusters) in runs)
        {
            var len = clusters * _cluster;
            if (byteOffset < len)
            {
                ReadAt(fs, lcn * _cluster + byteOffset, buf);
                return true;
            }
            byteOffset -= len;
        }
        return false;
    }

    /// <summary>Applies the update sequence array; false when the record is torn or not a file record.</summary>
    private static bool Fixup(Span<byte> r)
    {
        if (r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E')
            return false;
        int usaOfs = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
        if (usaOfs + usaCount * 2 > r.Length)
            return false;
        var check = BinaryPrimitives.ReadUInt16LittleEndian(r[usaOfs..]);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * UsaStride - 2;
            if (end + 2 > r.Length)
                break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(r[end..]) != check)
                return false;
            r[end] = r[usaOfs + i * 2];
            r[end + 1] = r[usaOfs + i * 2 + 1];
        }
        return true;
    }

    private static void ParseMftDataRuns(byte[] rec, List<(long, long)> runs, ref long dataSize, List<long>? extensionRefs)
    {
        int a = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(0x14));
        while (a + 16 <= rec.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(a));
            if (type == 0xFFFFFFFF)
                break;
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(a + 4));
            if (len <= 0 || a + len > rec.Length)
                break;
            if (type == 0x20 && extensionRefs != null && rec[a + 8] == 0)
            {
                // $ATTRIBUTE_LIST: collect records holding further $DATA extents
                var vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(a + 0x10));
                int vofs = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(a + 0x14));
                var p = a + vofs;
                var end = p + vlen;
                while (p + 26 <= end)
                {
                    var etype = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(p));
                    int elen = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 4));
                    var startVcn = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(p + 8));
                    var refNo = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(p + 0x10)) & 0xFFFFFFFFFFFF);
                    if (etype == 0x80 && refNo != 0 && startVcn > 0 && !extensionRefs.Contains(refNo))
                        extensionRefs.Add(refNo);
                    if (elen <= 0)
                        break;
                    p += elen;
                }
            }
            if (type == 0x80 && rec[a + 8] != 0 && rec[a + 9] == 0)
            {
                var startVcn = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(a + 0x10));
                if (startVcn == 0)
                    dataSize = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(a + 0x30));
                int runOfs = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(a + 0x20));
                DecodeRuns(rec.AsSpan(a + runOfs, len - runOfs), runs);
            }
            a += len;
        }
    }

    private static void DecodeRuns(ReadOnlySpan<byte> p, List<(long, long)> runs)
    {
        long lcn = 0;
        var i = 0;
        while (i < p.Length && p[i] != 0)
        {
            var h = p[i++];
            int lenBytes = h & 0xF, ofsBytes = h >> 4;
            if (i + lenBytes + ofsBytes > p.Length)
                break;
            long count = 0;
            for (var k = 0; k < lenBytes; k++)
                count |= (long)p[i + k] << (8 * k);
            i += lenBytes;
            long delta = 0;
            for (var k = 0; k < ofsBytes; k++)
                delta |= (long)p[i + k] << (8 * k);
            if (ofsBytes > 0 && (p[i + ofsBytes - 1] & 0x80) != 0)
                delta |= -1L << (8 * ofsBytes);
            i += ofsBytes;
            if (ofsBytes == 0)
                continue; // sparse run (not expected in $MFT)
            lcn += delta;
            runs.Add((lcn, count));
        }
    }

    /// <summary>Parses a base record into its slot; extension records are queued (they belong to another slot).</summary>
    private static void ParseRecord(Span<byte> r, int index, Rec[] recs, ConcurrentQueue<(int, byte[])> extensions)
    {
        if (!Fixup(r))
            return;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(r[0x16..]);
        if ((flags & 1) == 0)
            return; // not in use
        var baseRef = BinaryPrimitives.ReadUInt64LittleEndian(r[0x20..]) & 0xFFFFFFFFFFFF;
        if (baseRef != 0)
        {
            extensions.Enqueue((index, r.ToArray()));
            return;
        }
        ref var rec = ref recs[index];
        rec.InUse = true;
        rec.IsDir = (flags & 2) != 0;
        ParseAttributes(r, ref rec);
    }

    private static void ParseAttributes(Span<byte> r, ref Rec rec)
    {
        int a = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]);
        while (a + 16 <= r.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(r[a..]);
            if (type == 0xFFFFFFFF)
                break;
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(a + 4)..]);
            if (len < 16 || a + len > r.Length)
                break;
            var nonRes = r[a + 8] != 0;
            int nameLen = r[a + 9];
            switch (type)
            {
                case 0x10 when !nonRes: // $STANDARD_INFORMATION
                {
                    int vofs = BinaryPrimitives.ReadUInt16LittleEndian(r[(a + 0x14)..]);
                    var vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(a + 0x10)..]);
                    if (vofs >= len)
                        break;
                    var v = r.Slice(a + vofs, Math.Min(vlen, len - vofs));
                    if (v.Length >= 0x24)
                    {
                        rec.Created = FileTime(BinaryPrimitives.ReadInt64LittleEndian(v));
                        rec.Modified = FileTime(BinaryPrimitives.ReadInt64LittleEndian(v[8..]));
                        rec.Accessed = FileTime(BinaryPrimitives.ReadInt64LittleEndian(v[0x18..]));
                        rec.Attr = BinaryPrimitives.ReadUInt32LittleEndian(v[0x20..]);
                    }
                    if (v.Length >= 0x38)
                        rec.SecurityId = BinaryPrimitives.ReadUInt32LittleEndian(v[0x34..]);
                    break;
                }
                case 0x30 when !nonRes: // $FILE_NAME
                    ParseFileName(r, a, len, ref rec);
                    break;
                case 0x80: // $DATA
                    ParseData(r, a, len, nonRes, nameLen, BinaryPrimitives.ReadUInt16LittleEndian(r[(a + 0x0A)..]), ref rec);
                    break;
            }
            a += len;
        }
    }

    private static void ParseFileName(Span<byte> r, int a, int len, ref Rec rec)
    {
        int vofs = BinaryPrimitives.ReadUInt16LittleEndian(r[(a + 0x14)..]);
        if (vofs >= len)
            return;
        var v = r[(a + vofs)..(a + len)];
        if (v.Length < 0x42)
            return;
        var parent = (int)(BinaryPrimitives.ReadUInt64LittleEndian(v) & 0xFFFFFFFFFFFF);
        int fnLen = v[0x40];
        var ns = v[0x41];
        if (0x42 + fnLen * 2 > v.Length)
            return;
        var name = MemoryMarshal.Cast<byte, char>(v.Slice(0x42, fnLen * 2)).ToString();
        if (ns == 2)
        {
            // DOS 8.3 alias: only used when nothing better turns up
            if (rec.Name == null)
            {
                rec.Name = name;
                rec.Parent = parent;
                rec.NameRank = 9;
            }
            return;
        }
        if (rec.Name == null || rec.NameRank == 9)
        {
            rec.Name = name;
            rec.Parent = parent;
            rec.NameRank = ns;
        }
        else if (rec.Parent != parent || !string.Equals(rec.Name, name, StringComparison.Ordinal))
        {
            rec.Links ??= [];
            rec.Links.Add((parent, name));
        }
    }

    private static void ParseData(Span<byte> r, int a, int len, bool nonRes, int nameLen, int nameOfs, ref Rec rec)
    {
        long size, alloc;
        if (nonRes)
        {
            var startVcn = BinaryPrimitives.ReadInt64LittleEndian(r[(a + 0x10)..]);
            if (startVcn != 0)
                return; // later extent: sizes are in the first one
            var compUnit = BinaryPrimitives.ReadUInt16LittleEndian(r[(a + 0x22)..]);
            var attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(r[(a + 0x0C)..]);
            alloc = BinaryPrimitives.ReadInt64LittleEndian(r[(a + 0x28)..]);
            size = BinaryPrimitives.ReadInt64LittleEndian(r[(a + 0x30)..]);
            if ((compUnit != 0 || (attrFlags & 0x8001) != 0) && len >= 0x48)
                alloc = BinaryPrimitives.ReadInt64LittleEndian(r[(a + 0x40)..]); // compressed / sparse: real space
        }
        else
        {
            size = BinaryPrimitives.ReadUInt32LittleEndian(r[(a + 0x10)..]);
            alloc = 0; // resident in the record itself
        }
        if (nameLen == 0)
        {
            rec.Size = size;
            rec.Alloc = alloc;
        }
        else if (nameLen == WofStream.Length && nameOfs + nameLen * 2 <= len
                 && MemoryMarshal.Cast<byte, char>(r.Slice(a + nameOfs, nameLen * 2)).SequenceEqual(WofStream))
        {
            // Windows compact compression: the real data sits in this stream, the main stream is empty
            rec.Alloc += alloc;
            rec.Wof = true;
        }
        else
        {
            rec.Streams++;
            rec.StreamSize += size;
            rec.StreamAlloc += alloc;
        }
    }

    private const string WofStream = "WofCompressedData";

    private static long FileTime(long ft)
    {
        // FILETIME 100 ns since 1601 -> DateTime ticks (100 ns since 0001); out-of-range values count as unknown
        const long Epoch = 504911232000000000L;
        return ft <= 0 || ft > DateTime.MaxValue.Ticks - Epoch ? 0 : ft + Epoch;
    }

    private void BuildTree(Rec[] recs, VolumeInfo vol, CancellationToken ct, out TimeSpan ownerTime)
    {
        var n = recs.Length;
        // built detached from the session: the UI only sees the finished tree
        var root = new DirNode(vol.Root, null) { Pending = false, Frn = RootRecord };
        var dirs = new DirNode?[n];
        dirs[RootRecord] = root;
        if (RootRecord < n)
        {
            ref var rr = ref recs[RootRecord];
            root.SetOwnDates(rr.Created, rr.Modified, rr.Accessed);
            root.Attributes = (FileAttributes)rr.Attr | FileAttributes.Directory;
        }

        // folder hierarchy as first-child / next-sibling lists (parents may come after children in the table)
        var firstChild = new int[n];
        var nextSibling = new int[n];
        Array.Fill(firstChild, -1);
        for (var i = 16; i < n; i++)
        {
            ref var r = ref recs[i];
            if (!r.InUse || !r.IsDir || r.Name == null)
                continue;
            var p = r.Parent;
            if (p < 0 || p >= n || p == i)
                continue;
            nextSibling[i] = firstChild[p];
            firstChild[p] = i;
        }
        // walk from the root: only folders that hang below it (drops $Extend and orphans)
        var stack = new Stack<int>();
        stack.Push(RootRecord);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            var parent = dirs[p]!;
            for (var c = firstChild[p]; c >= 0; c = nextSibling[c])
            {
                ref var r = ref recs[c];
                var d = new DirNode(r.Name!, parent) { Attributes = (FileAttributes)r.Attr | FileAttributes.Directory, Pending = false, Frn = c };
                d.SetOwnDates(r.Created, r.Modified, r.Accessed);
                parent.DirList.Add(d);
                dirs[c] = d;
                stack.Push(c);
            }
        }
        ct.ThrowIfCancellationRequested();

        var track = _o.TrackHardlinksAndStreams;
        var owners = _o.CollectOwners;
        var firstPath = new Dictionary<uint, (DirNode Dir, string? Name)>();
        for (var i = 0; i < n; i++)
            if (dirs[i] is { } d && recs[i].SecurityId != 0 && owners)
            {
                d.OwnerId = (int)recs[i].SecurityId;
                firstPath.TryAdd(recs[i].SecurityId, (d, null));
            }
        var ownerWatch = new Stopwatch();
        long files = 0;
        for (var i = 16; i < n; i++)
        {
            ref var r = ref recs[i];
            if (!r.InUse || r.IsDir || r.Name == null)
                continue;
            AddFile(ref r, r.Parent, r.Name, first: true);
            if (r.Links != null)
                foreach (var (lp, ln) in r.Links)
                    AddFile(ref r, lp, ln, first: false);
            if ((++files & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                _s._filesDone = files;
                _s.Fraction = 0.7 + 0.25 * i / n;
                _s.CurrentPath = $"Building tree: {SizeFormat.Count(files)} files";
            }
        }
        _s._filesDone = files;

        if (owners)
        {
            ownerWatch.Start();
            ResolveOwners(root, firstPath);
            ownerWatch.Stop();
        }
        ownerTime = ownerWatch.Elapsed;

        void AddFile(ref Rec r, int parent, string name, bool first)
        {
            if (parent < 0 || parent >= n || dirs[parent] is not { } dir)
                return;
            if (_o.HasFilters && !_o.IncludeFile(name))
                return;
            var f = new FileItem(name, dir)
            {
                Attributes = (FileAttributes)r.Attr,
                CreatedTicks = r.Created,
                ModifiedTicks = r.Modified,
                AccessedTicks = r.Accessed,
                LinkCount = 1 + (r.Links?.Count ?? 0),
                StreamCount = r.Streams,
                StreamSize = r.StreamSize,
            };
            var alloc = r.Alloc;
            if (track)
                alloc = first ? alloc + r.StreamAlloc : 0;
            f.SetSizes(r.Size, alloc);
            if (owners && r.SecurityId != 0)
            {
                // the raw security id for now; ResolveOwners turns it into an owner index
                f.OwnerId = (int)r.SecurityId;
                if (!firstPath.ContainsKey(r.SecurityId))
                    firstPath[r.SecurityId] = (dir, name);
            }
            dir.FileList.Add(f);
        }

        // folder filters: drop excluded folders
        if (_o.Filters.Any(f => f.Enabled && f.Folders && f.Exclude))
        {
            foreach (var d in root.AllFolders().ToList())
                d.DirList.RemoveAll(c => !_o.IncludeFolder(c.Name, c.FullPath));
        }

        // a folder below the volume root: cut out that branch
        var rel = _s.Root.Name.Length > vol.Root.Length ? _s.Root.Name[vol.Root.Length..] : "";
        var result = root;
        if (rel.Length > 0)
        {
            var sub = root.FindRelative(rel) ?? throw new DirectoryNotFoundException(_s.Path);
            result = new DirNode(_s.Root.Name, null) { Attributes = sub.Attributes, OwnerId = sub.OwnerId, Pending = false, Frn = sub.Frn };
            result.SetOwnDates(sub.CreatedTicks, sub._ownModified, sub._ownAccessed);
            foreach (var c in sub.DirList)
            {
                c.Parent = result;
                result.DirList.Add(c);
            }
            foreach (var f in sub.FileList)
            {
                f.Parent = result;
                result.FileList.Add(f);
            }
        }
        else
            result.Name = _s.Root.Name;
        _s.Root = result;
        _s._foldersDone = result.AllFolders().LongCount() - 1;
    }

    /// <summary>
    /// Items carry their raw security id in OwnerId: look up each distinct id once (in parallel, through the first
    /// file or folder using it) and replace the ids with owner indexes.
    /// </summary>
    private void ResolveOwners(DirNode root, Dictionary<uint, (DirNode Dir, string? Name)> firstPath)
    {
        var names = new ConcurrentDictionary<uint, string?>();
        Parallel.ForEach(firstPath, new ParallelOptions { MaxDegreeOfParallelism = 8 }, kv =>
        {
            var path = kv.Value.Name == null ? kv.Value.Dir.FullPath : Path.Combine(kv.Value.Dir.FullPath, kv.Value.Name);
            string? owner;
            try
            {
                owner = Win32.GetOwner(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                owner = null;
            }
            names[kv.Key] = owner;
        });
        var ids = new Dictionary<uint, int>(names.Count);
        foreach (var (sid, owner) in names)
            ids[sid] = owner == null ? -1 : _s.Owners.GetId(owner);
        int Map(int raw) => raw > 0 && ids.TryGetValue((uint)raw, out var id) ? id : -1;
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            d.OwnerId = Map(d.OwnerId);
            foreach (var f in d.FileList)
                f.OwnerId = Map(f.OwnerId);
            foreach (var c in d.DirList)
                stack.Push(c);
        }
    }

    /// <summary>A buffer aligned to 4 KB (unbuffered volume reads need sector alignment): a pinned array, sliced.</summary>
    private sealed class AlignedBuffer(int size) : IDisposable
    {
        private const int Alignment = 4096;
        private readonly byte[] _array = GC.AllocateUninitializedArray<byte>(size + Alignment, pinned: true);

        public Span<byte> Span
        {
            get
            {
                var address = (long)Marshal.UnsafeAddrOfPinnedArrayElement(_array, 0);
                var offset = (int)((Alignment - address % Alignment) % Alignment);
                return _array.AsSpan(offset, size);
            }
        }

        public void Dispose()
        {
            // pinned arrays live on the pinned object heap: nothing to free by hand
        }
    }
}
