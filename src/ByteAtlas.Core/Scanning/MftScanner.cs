using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ByteAtlas.Model;
using ByteAtlas.Native;

namespace ByteAtlas.Scanning;

/// <summary>
/// Reads the NTFS master file table of a local volume directly (needs administrator rights).
/// Far faster than walking folders: every file record is read in one sequential pass.
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

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenVolume(string root) =>
        Win32.CreateFile(@"\\.\" + root[..2], Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

    // ---------- record data collected in the pass ----------

    private sealed class Rec
    {
        public string? Name;
        public int Parent = -1;
        public byte NameRank; // 3 win32+dos, 1 win32, 0 posix, 2 dos (lower wins later)
        public List<(int Parent, string Name)>? Links;
        public bool IsDir;
        public long Size, Alloc, StreamSize, StreamAlloc;
        public int Streams;
        public long Created, Modified, Accessed;
        public uint Attr;
        public uint SecurityId;
    }

    private int _recordSize, _cluster, _sectorSize;

    public void Run(CancellationToken ct)
    {
        var vol = VolumeInfo.Get(_s.Path);
        using var h = OpenVolume(vol.Root);
        if (h.IsInvalid)
            throw new UnauthorizedAccessException("Cannot open the volume (administrator rights needed).");
        using var fs = new FileStream(h, FileAccess.Read, 0, false);

        var boot = new byte[512];
        ReadAt(fs, 0, boot);
        if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS")
            throw new IOException("Not an NTFS volume.");
        _sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        _cluster = _sectorSize * boot[0x0D];
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
        if (extensionRefs.Count > 0)
        {
            // fragmented $MFT: the remaining runs sit in extension records (inside the part already known)
            foreach (var r in extensionRefs)
            {
                var buf = new byte[_recordSize];
                if (ReadRecordFromRuns(fs, runs, r, buf) && Fixup(buf))
                {
                    long dummy = 0;
                    ParseMftDataRuns(buf, runs, ref dummy, null);
                }
            }
        }
        var total = (int)(mftBytes / _recordSize);
        var recs = new Rec?[total];

        // sequential pass over the table, 4 MB at a time
        var chunk = new byte[Math.Max(_recordSize, 4 << 20) / _recordSize * _recordSize];
        long index = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var offset = lcn * _cluster;
            var left = clusters * _cluster;
            while (left > 0 && index < total)
            {
                ct.ThrowIfCancellationRequested();
                _s.PauseGate.Wait(ct);
                var n = (int)Math.Min(left, chunk.Length);
                ReadAt(fs, offset, chunk.AsSpan(0, n));
                for (var p = 0; p + _recordSize <= n && index < total; p += _recordSize, index++)
                    ParseRecord(chunk.AsSpan(p, _recordSize), (int)index, recs);
                offset += n;
                left -= n;
                _s._foldersDone = index;
                _s.CurrentPath = $"Reading master file table: {index * 100 / Math.Max(1, total)} %";
            }
        }

        BuildTree(recs, vol, ct);
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

    private static void ReadAt(FileStream fs, long offset, byte[] buf) => ReadAt(fs, offset, buf.AsSpan());

    private bool ReadRecordFromRuns(FileStream fs, List<(long Lcn, long Clusters)> runs, long record, byte[] buf)
    {
        var byteOffset = record * _recordSize;
        foreach (var (lcn, clusters) in runs)
        {
            var len = clusters * _cluster;
            if (byteOffset < len)
            {
                // read whole sectors around the record
                ReadAt(fs, lcn * _cluster + byteOffset, buf);
                return true;
            }
            byteOffset -= len;
        }
        return false;
    }

    /// <summary>Applies the update sequence array; false when the record is torn.</summary>
    private bool Fixup(Span<byte> r)
    {
        if (r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E')
            return false;
        int usaOfs = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
        if (usaOfs + usaCount * 2 > r.Length)
            return false;
        var check = BinaryPrimitives.ReadUInt16LittleEndian(r[usaOfs..]);
        const int stride = 512; // NTFS update sequence stride, whatever the sector size
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * stride - 2;
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
            if (type == 0x20 && extensionRefs != null)
            {
                // $ATTRIBUTE_LIST: collect records holding further $DATA extents
                var nonRes = rec[a + 8] != 0;
                if (!nonRes)
                {
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

    private void ParseRecord(Span<byte> r, int index, Rec?[] recs)
    {
        if (!Fixup(r))
            return;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(r[0x16..]);
        if ((flags & 1) == 0)
            return; // not in use
        var baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r[0x20..]) & 0xFFFFFFFFFFFF);
        var owner = baseRef != 0 ? (int)baseRef : index;
        if (owner < 0 || owner >= recs.Length)
            return;
        var rec = recs[owner] ??= new Rec();
        if (baseRef == 0)
            rec.IsDir = (flags & 2) != 0;

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
                {
                    int vofs = BinaryPrimitives.ReadUInt16LittleEndian(r[(a + 0x14)..]);
                    var v = r[(a + vofs)..(a + len)];
                    if (v.Length < 0x42)
                        break;
                    var parent = (int)(BinaryPrimitives.ReadUInt64LittleEndian(v) & 0xFFFFFFFFFFFF);
                    int fnLen = v[0x40];
                    var ns = v[0x41];
                    if (0x42 + fnLen * 2 > v.Length)
                        break;
                    if (ns == 2)
                    {
                        // DOS 8.3 alias: only used when nothing better turns up
                        if (rec.Name == null)
                        {
                            rec.Name = MemoryMarshal.Cast<byte, char>(v.Slice(0x42, fnLen * 2)).ToString();
                            rec.Parent = parent;
                            rec.NameRank = 9;
                        }
                        break;
                    }
                    var name = MemoryMarshal.Cast<byte, char>(v.Slice(0x42, fnLen * 2)).ToString();
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
                    break;
                }
                case 0x80: // $DATA
                {
                    long size, alloc;
                    if (nonRes)
                    {
                        var startVcn = BinaryPrimitives.ReadInt64LittleEndian(r[(a + 0x10)..]);
                        if (startVcn != 0)
                            break; // later extent: sizes are in the first one
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
                    else
                    {
                        rec.Streams++;
                        rec.StreamSize += size;
                        rec.StreamAlloc += alloc;
                    }
                    break;
                }
            }
            a += len;
        }
    }

    private static long FileTime(long ft)
    {
        if (ft <= 0)
            return 0;
        try
        {
            return DateTime.FromFileTimeUtc(ft).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private void BuildTree(Rec?[] recs, VolumeInfo vol, CancellationToken ct)
    {
        const int RootRecord = 5;
        var dirs = new DirNode?[recs.Length];
        var root = _s.Root;
        dirs[RootRecord] = root;
        if (recs[RootRecord] is { } rr)
            root.SetOwnDates(rr.Created, rr.Modified, rr.Accessed);

        // security id -> owner (resolved through one file carrying that id)
        var secOwner = new Dictionary<uint, int>();

        // folders first (parents may come after children in the table)
        for (var i = 0; i < recs.Length; i++)
        {
            var r = recs[i];
            if (r is not { IsDir: true, Name: not null } || i == RootRecord || i < 16)
                continue;
            var d = new DirNode(r.Name, null) { Attributes = (FileAttributes)r.Attr | FileAttributes.Directory };
            d.SetOwnDates(r.Created, r.Modified, r.Accessed);
            d.Pending = false;
            dirs[i] = d;
        }
        for (var i = 0; i < recs.Length; i++)
        {
            var d = dirs[i];
            if (d == null || i == RootRecord)
                continue;
            var p = recs[i]!.Parent;
            if (p >= 0 && p < dirs.Length && dirs[p] is { } parent && p != i)
            {
                d.Parent = parent;
                parent.DirList.Add(d);
            }
        }
        ct.ThrowIfCancellationRequested();

        // which folders actually hang under the root (drops $Extend and orphans)
        var reachable = new bool[recs.Length];
        reachable[RootRecord] = true;
        var stack = new Stack<int>();
        var childIndex = new Dictionary<DirNode, int>(dirs.Length / 8);
        for (var i = 0; i < dirs.Length; i++)
            if (dirs[i] != null)
                childIndex[dirs[i]!] = i;
        stack.Push(RootRecord);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            foreach (var c in dirs[i]!.DirList)
                if (childIndex.TryGetValue(c, out var ci) && !reachable[ci])
                {
                    reachable[ci] = true;
                    stack.Push(ci);
                }
        }

        var track = _o.TrackHardlinksAndStreams;
        long files = 0;
        for (var i = 16; i < recs.Length; i++)
        {
            var r = recs[i];
            if (r is not { IsDir: false, Name: not null })
                continue;
            AddFile(r, r.Parent, r.Name, first: true);
            if (r.Links != null)
                foreach (var (lp, ln) in r.Links)
                    AddFile(r, lp, ln, first: false);
            if ((++files & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                _s._filesDone = files;
                _s.CurrentPath = $"Building tree: {SizeFormat.Count(files)} files";
            }
        }
        _s._filesDone = files;

        void AddFile(Rec r, int parent, string name, bool first)
        {
            if (parent < 0 || parent >= dirs.Length || !reachable[parent] || dirs[parent] is not { } dir)
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
            if (_o.CollectOwners && r.SecurityId != 0)
            {
                if (!secOwner.TryGetValue(r.SecurityId, out var oid))
                {
                    oid = -1;
                    try
                    {
                        var owner = Win32.GetOwner(Path.Combine(dir.FullPath, name));
                        if (owner != null)
                            oid = _s.Owners.GetId(owner);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        oid = -1;
                    }
                    secOwner[r.SecurityId] = oid;
                }
                f.OwnerId = oid;
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
        if (rel.Length > 0)
        {
            var sub = root.FindRelative(rel) ?? throw new DirectoryNotFoundException(_s.Path);
            var newRoot = new DirNode(_s.Root.Name, null) { Attributes = sub.Attributes };
            newRoot.SetOwnDates(sub.CreatedTicks, sub._ownModified, sub._ownAccessed);
            foreach (var c in sub.DirList)
            {
                c.Parent = newRoot;
                newRoot.DirList.Add(c);
            }
            foreach (var f in sub.FileList)
            {
                f.Parent = newRoot;
                newRoot.FileList.Add(f);
            }
            _s.Root = newRoot;
        }
        _s._foldersDone = _s.Root.AllFolders().LongCount();
    }
}
