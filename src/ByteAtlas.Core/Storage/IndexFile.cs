using System.Globalization;
using System.Xml;
using Microsoft.Data.Sqlite;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Storage;

/// <summary>
/// Saves a scan to an index file and loads it back: SQLite (.tsdb / .sqlite) or XML (.xml).
/// Folders keep their totals, so a folders-only index still shows correct sizes.
/// </summary>
public static class IndexFile
{
    public const int FormatVersion = 1;

    public static bool IsXml(string path) => Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);

    public static void Save(ScanSession s, string path, bool includeFiles = true)
    {
        if (IsXml(path))
            SaveXml(s, path, includeFiles);
        else
            SaveSqlite(s, path, includeFiles);
    }

    public static ScanSession Load(string path) => IsXml(path) ? LoadXml(path) : LoadSqlite(path);

    // ---------------- SQLite ----------------

    private static void SaveSqlite(ScanSession s, string path, bool includeFiles)
    {
        if (File.Exists(path))
            File.Delete(path);
        SqliteConnection.ClearAllPools();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open();
        Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(db, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE owners(id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE items(id INTEGER PRIMARY KEY, parent INTEGER, name TEXT NOT NULL, folder INTEGER NOT NULL,
                size INTEGER, alloc INTEGER, files INTEGER, folders INTEGER,
                created INTEGER, modified INTEGER, accessed INTEGER, attr INTEGER, owner INTEGER,
                links INTEGER, streams INTEGER, error TEXT);
            """);
        using var tx = db.BeginTransaction();
        void Meta(string k, string v)
        {
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO meta VALUES($k,$v)";
            c.Parameters.AddWithValue("$k", k);
            c.Parameters.AddWithValue("$v", v);
            c.ExecuteNonQuery();
        }
        Meta("format", "ByteAtlas index");
        Meta("version", FormatVersion.ToString(CultureInfo.InvariantCulture));
        Meta("path", s.Root.FullPath);
        Meta("title", s.Title ?? "");
        Meta("scanDate", (s.ScanDate == default ? DateTime.Now : s.ScanDate).ToString("o", CultureInfo.InvariantCulture));
        Meta("volumeTotal", s.VolumeTotal.ToString(CultureInfo.InvariantCulture));
        Meta("volumeFree", s.VolumeFree.ToString(CultureInfo.InvariantCulture));
        Meta("clusterSize", s.ClusterSize.ToString(CultureInfo.InvariantCulture));
        Meta("fileSystem", s.FileSystem);
        Meta("method", s.MethodUsed);
        Meta("files", includeFiles ? "1" : "0");

        using (var oc = db.CreateCommand())
        {
            oc.Transaction = tx;
            oc.CommandText = "INSERT INTO owners VALUES($i,$n)";
            var pi = oc.Parameters.Add("$i", SqliteType.Integer);
            var pn = oc.Parameters.Add("$n", SqliteType.Text);
            for (var i = 0; i < s.Owners.Count; i++)
            {
                pi.Value = i;
                pn.Value = s.Owners.Name(i);
                oc.ExecuteNonQuery();
            }
        }

        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO items VALUES($id,$p,$n,$d,$s,$a,$f,$fo,$c,$m,$ac,$at,$o,$l,$st,$e)";
        var p = new SqliteParameter[16];
        string[] names = ["$id", "$p", "$n", "$d", "$s", "$a", "$f", "$fo", "$c", "$m", "$ac", "$at", "$o", "$l", "$st", "$e"];
        for (var i = 0; i < names.Length; i++)
            p[i] = cmd.Parameters.Add(names[i], i == 2 || i == 15 ? SqliteType.Text : SqliteType.Integer);
        cmd.Prepare();

        long next = 0;
        var stack = new Stack<(DirNode Node, long Parent)>();
        stack.Push((s.Root, -1));
        while (stack.Count > 0)
        {
            var (d, parent) = stack.Pop();
            var id = next++;
            Row(id, parent, d.Name, true, d.Size, d.Allocated, d.FileCount, d.FolderCount, d.CreatedTicks, d.ModifiedTicks, d.AccessedTicks, (long)d.Attributes, d.OwnerId, 0, 0, d.Error);
            if (includeFiles)
                foreach (var f in d.Files)
                    Row(next++, id, f.Name, false, f.Size, f.Allocated, 0, 0, f.CreatedTicks, f.ModifiedTicks, f.AccessedTicks, (long)f.Attributes, f.OwnerId, f.LinkCount, f.StreamCount, null);
            var dirs = d.Dirs;
            for (var i = dirs.Length - 1; i >= 0; i--)
                stack.Push((dirs[i], id));
        }
        tx.Commit();

        void Row(long id, long parent, string name, bool folder, long size, long alloc, long files, long folders, long c, long m, long a, long attr, int owner, int links, int streams, string? error)
        {
            p[0].Value = id;
            p[1].Value = parent < 0 ? DBNull.Value : parent;
            p[2].Value = name;
            p[3].Value = folder ? 1 : 0;
            p[4].Value = size;
            p[5].Value = alloc;
            p[6].Value = files;
            p[7].Value = folders;
            p[8].Value = c;
            p[9].Value = m;
            p[10].Value = a;
            p[11].Value = attr;
            p[12].Value = owner;
            p[13].Value = links;
            p[14].Value = streams;
            p[15].Value = (object?)error ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static ScanSession LoadSqlite(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        var meta = new Dictionary<string, string>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT key, value FROM meta";
            using var r = c.ExecuteReader();
            while (r.Read())
                meta[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        }
        if (meta.GetValueOrDefault("format") != "ByteAtlas index")
            throw new InvalidDataException("Not a ByteAtlas index file.");

        var rootPath = meta.GetValueOrDefault("path", path);
        var s = NewSession(rootPath, meta, path);
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT id, name FROM owners ORDER BY id";
            using var r = c.ExecuteReader();
            while (r.Read())
                s.Owners.GetId(r.GetString(1));
        }

        var dirs = new Dictionary<long, DirNode>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT id,parent,name,folder,size,alloc,files,folders,created,modified,accessed,attr,owner,links,streams,error FROM items ORDER BY id";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var parentId = r.IsDBNull(1) ? -1 : r.GetInt64(1);
                var name = r.GetString(2);
                var folder = r.GetInt64(3) != 0;
                DirNode? parent = parentId >= 0 && dirs.TryGetValue(parentId, out var pd) ? pd : null;
                if (parentId >= 0 && parent == null)
                    continue;
                if (folder)
                {
                    var d = parent == null ? s.Root : new DirNode(name, parent);
                    d._size = r.GetInt64(4);
                    d._allocated = r.GetInt64(5);
                    d._fileCount = r.GetInt64(6);
                    d._folderCount = r.GetInt64(7);
                    d.CreatedTicks = r.GetInt64(8);
                    d.ModifiedTicks = r.GetInt64(9);
                    d.AccessedTicks = r.GetInt64(10);
                    d._ownModified = d.ModifiedTicks;
                    d._ownAccessed = d.AccessedTicks;
                    d.Attributes = (FileAttributes)r.GetInt64(11);
                    d.OwnerId = (int)r.GetInt64(12);
                    d.Error = r.IsDBNull(15) ? null : r.GetString(15);
                    d.Pending = false;
                    parent?.DirList.Add(d);
                    dirs[id] = d;
                }
                else if (parent != null)
                {
                    var f = new FileItem(name, parent)
                    {
                        CreatedTicks = r.GetInt64(8),
                        ModifiedTicks = r.GetInt64(9),
                        AccessedTicks = r.GetInt64(10),
                        Attributes = (FileAttributes)r.GetInt64(11),
                        OwnerId = (int)r.GetInt64(12),
                        LinkCount = (int)r.GetInt64(13),
                        StreamCount = (int)r.GetInt64(14),
                    };
                    f.SetSizes(r.GetInt64(4), r.GetInt64(5));
                    parent.FileList.Add(f);
                }
            }
        }
        FinishLoaded(s);
        return s;
    }

    private static ScanSession NewSession(string rootPath, Dictionary<string, string> meta, string file)
    {
        var s = new ScanSession(rootPath, new ScanOptions())
        {
            Title = meta.GetValueOrDefault("title") is { Length: > 0 } t ? t : null,
        };
        s.State = ScanState.Done;
        s.IndexFile = file;
        s.MethodUsed = "Index file";
        if (DateTime.TryParse(meta.GetValueOrDefault("scanDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            s.ScanDate = date;
        s.Started = s.Finished = s.ScanDate;
        s.VolumeTotal = long.TryParse(meta.GetValueOrDefault("volumeTotal"), CultureInfo.InvariantCulture, out var vt) ? vt : 0;
        s.VolumeFree = long.TryParse(meta.GetValueOrDefault("volumeFree"), CultureInfo.InvariantCulture, out var vf) ? vf : 0;
        s.ClusterSize = int.TryParse(meta.GetValueOrDefault("clusterSize"), CultureInfo.InvariantCulture, out var cs) ? cs : 4096;
        s.FileSystem = meta.GetValueOrDefault("fileSystem", "");
        return s;
    }

    private static void FinishLoaded(ScanSession s)
    {
        foreach (var d in s.Root.AllFolders())
        {
            d.OwnFilesSize = d.FileList.Sum(f => f.Size);
            d.OwnFilesAllocated = d.FileList.Sum(f => f.Allocated);
            d._aggModified = d.ModifiedTicks;
            d._aggAccessed = d.AccessedTicks;
        }
        SetDepths(s.Root);
        s.Root.Sort(true);
        s._foldersDone = s.Root.FolderCount + 1;
        s._filesDone = s.Root.FileCount;
    }

    private static int SetDepths(DirNode d)
    {
        var depth = 0;
        foreach (var c in d.DirList)
            depth = Math.Max(depth, SetDepths(c) + 1);
        d.MaxDepthBelow = depth;
        return depth;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    // ---------------- XML ----------------

    private static void SaveXml(ScanSession s, string path, bool includeFiles)
    {
        using var w = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        w.WriteStartDocument();
        w.WriteStartElement("ByteAtlasScan");
        w.WriteAttributeString("version", FormatVersion.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("path", s.Root.FullPath);
        if (!string.IsNullOrEmpty(s.Title))
            w.WriteAttributeString("title", s.Title);
        w.WriteAttributeString("scanDate", (s.ScanDate == default ? DateTime.Now : s.ScanDate).ToString("o", CultureInfo.InvariantCulture));
        w.WriteAttributeString("volumeTotal", s.VolumeTotal.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("volumeFree", s.VolumeFree.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("clusterSize", s.ClusterSize.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("fileSystem", s.FileSystem);
        w.WriteStartElement("Owners");
        for (var i = 0; i < s.Owners.Count; i++)
        {
            w.WriteStartElement("Owner");
            w.WriteAttributeString("id", i.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("name", s.Owners.Name(i));
            w.WriteEndElement();
        }
        w.WriteEndElement();
        WriteFolder(w, s.Root, includeFiles);
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WriteFolder(XmlWriter w, DirNode d, bool includeFiles)
    {
        w.WriteStartElement("Folder");
        w.WriteAttributeString("name", d.Name);
        Attr(w, "size", d.Size);
        Attr(w, "alloc", d.Allocated);
        Attr(w, "files", d.FileCount);
        Attr(w, "folders", d.FolderCount);
        Common(w, d);
        if (d.Error != null)
            w.WriteAttributeString("error", d.Error);
        if (includeFiles)
            foreach (var f in d.Files)
            {
                w.WriteStartElement("File");
                w.WriteAttributeString("name", f.Name);
                Attr(w, "size", f.Size);
                Attr(w, "alloc", f.Allocated);
                Common(w, f);
                if (f.LinkCount > 1)
                    Attr(w, "links", f.LinkCount);
                if (f.StreamCount > 0)
                    Attr(w, "streams", f.StreamCount);
                w.WriteEndElement();
            }
        foreach (var c in d.Dirs)
            WriteFolder(w, c, includeFiles);
        w.WriteEndElement();
    }

    private static void Common(XmlWriter w, Item i)
    {
        Attr(w, "created", i.CreatedTicks);
        Attr(w, "modified", i.ModifiedTicks);
        Attr(w, "accessed", i.AccessedTicks);
        Attr(w, "attr", (long)i.Attributes);
        if (i.OwnerId >= 0)
            Attr(w, "owner", i.OwnerId);
    }

    private static void Attr(XmlWriter w, string name, long v) => w.WriteAttributeString(name, v.ToString(CultureInfo.InvariantCulture));

    private static ScanSession LoadXml(string path)
    {
        using var r = XmlReader.Create(path, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        r.MoveToContent();
        if (r.Name != "ByteAtlasScan")
            throw new InvalidDataException("Not a ByteAtlas XML index.");
        var meta = new Dictionary<string, string>();
        foreach (var k in new[] { "path", "title", "scanDate", "volumeTotal", "volumeFree", "clusterSize", "fileSystem" })
            if (r.GetAttribute(k) is { } v)
                meta[k] = v;
        var s = NewSession(meta.GetValueOrDefault("path", path), meta, path);
        var stack = new Stack<DirNode>();
        var first = true;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.EndElement && r.Name == "Folder")
            {
                stack.Pop();
                continue;
            }
            if (r.NodeType != XmlNodeType.Element)
                continue;
            switch (r.Name)
            {
                case "Owner":
                    s.Owners.GetId(r.GetAttribute("name") ?? "");
                    break;
                case "Folder":
                {
                    var parent = stack.Count > 0 ? stack.Peek() : null;
                    var d = first ? s.Root : new DirNode(r.GetAttribute("name") ?? "", parent);
                    first = false;
                    d._size = L(r, "size");
                    d._allocated = L(r, "alloc");
                    d._fileCount = L(r, "files");
                    d._folderCount = L(r, "folders");
                    ReadCommon(r, d);
                    d._ownModified = d.ModifiedTicks;
                    d._ownAccessed = d.AccessedTicks;
                    d.Error = r.GetAttribute("error");
                    d.Pending = false;
                    parent?.DirList.Add(d);
                    if (r.IsEmptyElement)
                        break;
                    stack.Push(d);
                    break;
                }
                case "File" when stack.Count > 0:
                {
                    var parent = stack.Peek();
                    var f = new FileItem(r.GetAttribute("name") ?? "", parent)
                    {
                        LinkCount = (int)L(r, "links"),
                        StreamCount = (int)L(r, "streams"),
                    };
                    ReadCommon(r, f);
                    f.SetSizes(L(r, "size"), L(r, "alloc"));
                    parent.FileList.Add(f);
                    break;
                }
            }
        }
        FinishLoaded(s);
        return s;
    }

    private static void ReadCommon(XmlReader r, Item i)
    {
        i.CreatedTicks = L(r, "created");
        i.ModifiedTicks = L(r, "modified");
        i.AccessedTicks = L(r, "accessed");
        i.Attributes = (FileAttributes)L(r, "attr");
        i.OwnerId = r.GetAttribute("owner") is { } o && int.TryParse(o, CultureInfo.InvariantCulture, out var oid) ? oid : -1;
    }

    private static long L(XmlReader r, string name) =>
        long.TryParse(r.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
