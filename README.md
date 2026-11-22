<p align="center"><img src="art/byteatlas.svg" width="128" alt="ByteAtlas logo: Atlas holding up a digital globe"></p>

# ByteAtlas

**Where did my disk space go?** ByteAtlas maps it: a size-sorted folder tree, treemap and charts, file types,
owners and ages, the largest files, duplicates — plus the tools to clean up, report and track growth over time.

Open source (MIT), dark by default, written in C# (.NET 10 + Avalonia). Windows and Linux.

## Install

From the [latest release](https://github.com/Taoism/ByteAtlas/releases/latest) (all packages are self-contained,
no .NET needed; each has the app and the `byteatlas-cli` command):

| System | Package |
|---|---|
| Windows 10 / 11 | `ByteAtlas-<v>-setup-win-x64.exe` (per user, no admin prompt; options: desktop icon, `byteatlas-cli` on PATH, Explorer menu) or the portable `…-win-x64.zip` |
| Debian / Ubuntu / Mint | `sudo apt install ./byteatlas_<v>_amd64.deb` |
| Fedora / RHEL | `sudo dnf install ./byteatlas-<v>-1.x86_64.rpm` |
| Other Linux | `ByteAtlas-<v>-linux-x64.tar.gz`: unpack, run `./ByteAtlas` |

On Linux the folder walk reads owners, real allocated sizes and hard links (counted once) and stays on one file
system unless *Follow mount points* is on. Windows-only features (master file table, NTFS compression and
permissions, snapshots, Explorer menu) are hidden there.

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
- **Remote sources** (File › Scan remote source, or type the address): Linux / Unix servers over **SSH** (one pass
  of GNU `find`; password or key file; host keys trusted on first use, then checked), **Amazon S3** and
  S3-compatible storage (MinIO, Wasabi, Backblaze B2, Cloudflare R2, …), **Azure Blob** containers (SAS URL),
  **WebDAV** (Nextcloud, SharePoint, …), and **disk images** (VHDX / VHD / ISO, mounted read-only while the scan
  is open). Sources can be saved; passwords only when you ask, encrypted for your Windows account.

**Views**
- Directory tree with size bars, `[Files]` rows, bold for large folders, compressed in blue, long paths in red;
  size / allocated / file count / percent; units and decimals; expand to level, hide small, find deepest nesting.
- **Chart**: squarified treemap (hierarchy, single files, coloured by file type or level, level of detail),
  pie and bar charts of folders, extensions, owners or ages, with free space; save as PNG.
- **Details** with selectable columns (size, allocated, %, files, folders, dates, owner, attributes, type,
  wasted space, compression, hard links, streams, costs, size change, path length).
- **Extensions** (or file groups), **Users** (owners), **Age of Files** (configurable intervals),
  **Top Files**, **History** (used space over time per drive / folder).
- Filter the tree by extension, file group or owner; **group scans** under one "All scans" row with totals.
- **Preview pane** (pictures, text files, Explorer thumbnails for videos / PDFs / documents), **compare two files**
  side by side, **thumbnail view** of a folder.
- **Treemap navigation**: path bar to jump back up, click a tile to select it in the tree, double-click to zoom in,
  right-click for its menu; the tree selection is outlined on the map.
- Extra Details columns: the size of one **file type, file group, owner or age interval** in each folder.
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

**Permissions** — owner, explicit / inherited access, a permissions column and dialog (with advanced rights), export,
and a **search for folders a user or group can access** (group memberships count, local and domain).

**Compression** — NTFS (LZNT1) and Windows **compact compression (XPRESS 4K / 8K / 16K, LZX)**: see each file's format
and real size on disk, compress or uncompress files and folders.

**Windows integration** — the real **Explorer right-click menu** (Shift + right click, or "Explorer menu…"), **drag
files out** into Explorer, Explorer context menu ("Analyze / Search with ByteAtlas"), properties dialog, open
command prompt here, NTFS compress / decompress, empty recycle bin, map network drive, start as administrator,
**scheduled scans** (Task Scheduler running `byteatlas-cli`).

## Command line

```
byteatlas-cli scan D:\ --export "D:\Reports\d_{date}.xlsx" --save-index "D:\Reports\d_{date}.badb" --history
byteatlas-cli compare D:\ "D:\Reports\d_2026-11-01.badb" --export changes.html
byteatlas-cli search E:\ --template duplicates --hash sha256 --export dups.csv
byteatlas-cli history C:\
byteatlas-cli scan ssh://me@server/srv --export srv.html
byteatlas-cli scan s3://my-bucket/logs --access-key AKIA… --export logs.xlsx
byteatlas-cli scan s3+https://s3.wasabisys.com/my-bucket --access-key …
byteatlas-cli scan "azure:https://acct.blob.core.windows.net/backups?sv=…&sig=…"
byteatlas-cli scan davs://me@cloud.example.com/remote.php/dav/files/me/
byteatlas-cli scan D:\Images\disk.vhdx
```

`byteatlas-cli --help` lists every option. Passwords and secret keys are read from environment variables
(`BYTEATLAS_PASSWORD` or `--password-env NAME`; S3 also `AWS_SECRET_ACCESS_KEY`), never from the command line.

## Building

.NET 10 SDK (or newer):

```
dotnet build ByteAtlas.sln
dotnet test tests/ByteAtlas.Tests
dotnet run --project src/ByteAtlas -- C:\
```

Reading the master file table needs administrator rights; without them ByteAtlas walks folders instead.

Release packages: `pwsh packaging/build-release.ps1` (Inno Setup 6 + WSL with Docker) builds the installer, zips, .deb, .rpm and
tarball into `release/`; `packaging/test-linux.sh <version> .` installs the Linux ones in clean Debian, Ubuntu and
Fedora containers and checks the CLI, exports and the GUI (Xvfb).

Remote-source tests use local servers in Docker (WSL): `tests/remote/setup.sh` starts SSH, S3 (rclone), Azurite and
WebDAV with a known tree, `tests/remote/many.sh` adds 2,100 objects and SSH keys; then
`BYTEATLAS_REMOTE_TESTS=1 dotnet test tests/ByteAtlas.Tests`.
The icon is drawn in `art/byteatlas.svg`; `dotnet run --project tools/IconRender` renders the PNG / ICO files.

## License

MIT — see [LICENSE](LICENSE). PDF output uses [PDFsharp](https://github.com/empira/PDFsharp) 6.2 (MIT),
index files use Microsoft.Data.Sqlite (MIT), the UI is [Avalonia](https://avaloniaui.net) (MIT).
ByteAtlas is an independent project and not affiliated with any other disk space tool.
