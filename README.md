<p align="center"><img src="art/byteatlas.svg" width="128" alt="ByteAtlas logo: Atlas holding up a digital globe"></p>

# ByteAtlas

**Where did my disk space go?** ByteAtlas maps it: a size-sorted folder tree, treemap and charts, file types,
owners and ages, the largest files, duplicates — plus the tools to clean up, report and track growth over time.

Open source (MIT), dark by default, written in C# (.NET 10 + Avalonia).

## Features

**Scanning**
- Reads the **NTFS master file table** directly on local drives (as administrator): a 2.8 TB drive with
  2.9 million files in ~13 s. Elsewhere (network shares, FAT/exFAT, other systems) a multi-threaded folder walk
  (up to 32 threads).
- Browse while scanning, several scans side by side, pause / stop, refresh a folder (with or without sub folders).
- Allocated space (cluster rounding, NTFS compression, sparse files), hard links and alternate data streams
  counted once (optional), owners, long paths (> 260 characters), mount points / junctions (optional).
- Include / exclude filters (wildcards, regular expressions, whole names, folders).
- Watch for file system changes and refresh what changed.

**Views**
- Directory tree with size bars, `[Files]` rows, bold for large folders, compressed in blue, long paths in red;
  size / allocated / file count / percent; units and decimals; expand to level, hide small, find deepest nesting.
- **Chart**: squarified treemap (hierarchy, single files, coloured by file type or level, level of detail),
  pie and bar charts of folders, extensions, owners or ages, with free space; save as PNG.
- **Details** with selectable columns (size, allocated, %, files, folders, dates, owner, attributes, type,
  wasted space, compression, hard links, streams, costs, size change, path length).
- **Extensions** (or file groups), **Users** (owners), **Age of Files** (configurable intervals),
  **Top Files**, **History** (used space over time per drive / folder).
- Filter the tree by extension, file group or owner.
- Drive list with used / free space, extra paths, all shares of a server.

**File search** — templates for large, old, temporary, duplicate (MD5 / SHA-256) and long-path files, or your own
criteria (names, size, dates, attributes, owner, path length). On the results: delete (recycle bin), move / copy
(keeping the folder structure), zip archive, bulk rename, replace duplicates with **NTFS hard links**, export.

**Bulk rename** — replace (regex), insert, serialize, delete, case; ordered rules, preview, undo, rule sets.

**Reports** — Excel (.xlsx, outline levels), PDF, HTML, CSV, text, clipboard, email (attachment), print (via PDF);
choose columns, depth, files, units.

**Growth tracking** — save scans to index files (SQLite or XML) and load them later; compare the current scan
with an index file, another path or a **Windows snapshot** (volume shadow copy; create snapshots too).
The tree then shows the change per folder.

**Windows integration** — Explorer context menu ("Analyze / Search with ByteAtlas"), properties dialog, open
command prompt here, NTFS compress / decompress, empty recycle bin, map network drive, start as administrator,
**scheduled scans** (Task Scheduler running `byteatlas-cli`).

## Command line

```
byteatlas-cli scan D:\ --export "D:\Reports\d_{date}.xlsx" --save-index "D:\Reports\d_{date}.badb" --history
byteatlas-cli compare D:\ "D:\Reports\d_2026-11-01.badb" --export changes.html
byteatlas-cli search E:\ --template duplicates --hash sha256 --export dups.csv
byteatlas-cli history C:\
```

`byteatlas-cli --help` lists every option.

## Building

.NET 10 SDK (or newer):

```
dotnet build ByteAtlas.sln
dotnet test tests/ByteAtlas.Tests
dotnet run --project src/ByteAtlas -- C:\
```

Reading the master file table needs administrator rights; without them ByteAtlas walks folders instead.
The icon is drawn in `art/byteatlas.svg`; `dotnet run --project tools/IconRender` renders the PNG / ICO files.

## License

MIT — see [LICENSE](LICENSE). PDF output uses [PDFsharp](https://github.com/empira/PDFsharp) 6.2 (MIT),
index files use Microsoft.Data.Sqlite (MIT), the UI is [Avalonia](https://avaloniaui.net) (MIT).
ByteAtlas is an independent project and not affiliated with any other disk space tool.
