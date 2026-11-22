# ByteAtlas TODO

Goal: every feature of a full disk space manager (feature list: TreeSize Professional's feature page, 70 items),
clean-room, open source. Version 0.1.0 (2026-11-21).

## Done (0.1.0)

- Scanning: NTFS MFT reader (local NTFS, admin; fragmented $MFT via attribute list; hard links, ADS, compressed /
  sparse allocated size, security id → owner), parallel folder walk (1–32 threads, priority), browse while scanning,
  pause / stop, refresh folder (± sub folders), refresh all, several scans, filters (wildcard / regex / name /
  folder), follow mount points, hard links + streams counted once, folder dates from files, watch for changes.
- Tree: size bars, [Files] rows + single files, bold / blue (compressed) / red (long path), size / allocated /
  files / percent, units, decimals, sort, expand to level / full / larger than, hide small / empty, find deepest,
  find folder, back / forward / up, size changes after compare, filter by extension / group / owner.
- Tabs: Chart (treemap, pie, bar; folders / extensions / users / ages; free space; PNG), Details (columns,
  costs, wasted space, …), Extensions (+ file groups), Users, Age of Files (intervals editable), Top Files, History
  (chart, export / import CSV, purge, change location).
- Drive list (used / free, extra paths, server shares, hide, copy).
- File search: templates large / old / temporary / duplicates (size → quick hash → MD5 / SHA-256) / long paths,
  custom criteria; delete, move / copy, zip archive, bulk rename, hard-link dedup, export.
- Bulk rename: replace (regex), insert, serialize, delete, case; scope; preview; undo; save / load rules.
- Exports: xlsx (own writer, outline levels), PDF (PDFsharp 6.2.4 MIT — 6.5 needs a license!), HTML, CSV, text,
  clipboard, email (Simple MAPI), print (PDF), list of all files; export settings (columns, depth, files, min size).
- Index files (SQLite .badb, XML), compare with index / path / shadow copy, create shadow copy.
- Explorer context menu (HKCU), scheduled scans (schtasks + byteatlas-cli), start as admin, recycle bin, map drive,
  command prompt, properties, NTFS compress / decompress, options import / export / reset.
- CLI: scan / compare / search / history with exports, index, history.

## Done (0.1.1, 2026-11-21)

- MFT: 4 parallel unbuffered readers on SSDs, 1 on spinning disks (seek-penalty query), struct records, parallel
  owner lookups, server GC: C: (4.4 M records) 12.8 s -> 6.3-7.7 s; folder owners; tree built off-screen and
  swapped in (UI never reads a half-built list); `--verbose` timings.
- Sub folders: quick folder walk first (2 s budget), MFT only for big folders (1.6 GB folder: 9.5 s -> 0.1 s).
- Future time stamps: ignored for folder dates, own "In the future" row in Age of Files.
- Resizable everything, remembered (settings Layout): drive list / tree, tree / tabs, tree value column (header
  splitter), chart / list on Extensions / Users / Age, all grid column widths, File Search (size + panel), Bulk
  rename (size, panel, rules list), Options (size, page list), list / text dialogs, every dialog can grow.

## Done (0.2.0, 2026-11-21)

- Permissions: Permissions / Inherited columns (read per shown row), dialog with advanced rights, export columns,
  permission search (account + group SIDs via AccountManagement, minimum access, explicit only, folders only).
- Explorer menu (IContextMenu/2/3 + window subclass for submenus) on Shift+right click or "Explorer menu…";
  drag-out from tree, Details, Top Files, thumbnails, search results (own window ignores the drop).
- Group scans ("All scans" row, totals in place, owners merged by name, View › Group scans).
- Compact compression: format column, compress XPRESS 4K/8K/16K / LZX / NTFS / none; MFT counts WofCompressedData;
  folder walk asks each file (option; always on for the quick walk of small folders) — Windows hides it from attributes.
- Preview pane, compare two files, thumbnail view (Explorer thumbnails on an STA thread).
- Extra Details columns per extension / file group / owner / age interval (background totals, sortable).
- Treemap path bar, click selects in the tree, outline of the selection, right-click menu.
- Toolbar icons (Material Design Icons, Apache 2.0, dark grey glyphs).

## Done (next version, 2026-11-21)

- Remote sources: SSH (GNU find, password / key file, trust-on-first-use host keys kept in ssh-known-hosts.txt),
  Amazon S3 + compatible (SigV4, paging), Azure Blob (SAS), WebDAV (PROPFIND), disk images (VHDX / VHD / ISO
  mounted read-only while the scan is open). GUI dialog with saved sources (DPAPI-protected passwords on request),
  addresses typed in the path box, CLI addresses + --key / --endpoint / --access-key / --password-env.
  Tested against Docker servers (tests/remote); items of remote scans are read-only (no delete, Explorer menu, …).
- Menu shortcuts (Ctrl+O, F5, Ctrl+F, …) were only displayed, now bound; screen-reader names for the path box and
  icon-only buttons.
- Change-journal refresh, scan cache, progress panel, accessibility (see git log).

## Done (0.4.0, 2026-11-21)

- Windows installer (Inno Setup: per user or all users, desktop icon, byteatlas-cli on PATH, Explorer menu; closes a
  running copy on uninstall / silent upgrade), tested install → run → uninstall in Windows Sandbox.
- Linux: .deb, .rpm and portable tarball, tested in clean Debian 12, Ubuntu 24.04 and Fedora 41 containers.
  Folder walk reads owner, allocated blocks, hard links (statx) and stays on one file system; drive list shows real
  mounts only; Windows-only menus and options hidden.
- PDF fonts: fixed broken Arial fallback paths, Fedora / Noto locations, any installed font as a last resort.
- Linux trash (freedesktop.org spec: home trash, $topdir/.Trash/$uid or .Trash-$uid on other drives, .trashinfo
  records byte-identical to gio's; never falls back to deleting for good), Empty trash. Before this, "Delete (recycle
  bin)" on Linux deleted for good.
- Linux scheduled scans: systemd user timers (Persistent=true) or crontab lines; same dialog + manage list as Windows;
  `byteatlas-cli schedule add|list|remove` for servers. Tested: systemd timer in WSL, real cron run in Debian.
- Test suite runs on Linux too (tests/linux/run-tests.sh, .NET SDK container): 59/59.

## Done (next version, 2026-11-22)

- **Duplicate Files** and **Duplicate Folders** tabs (Alt+7 / Alt+8, History moved to Alt+9), working below the
  folder selected in the tree; found on first show, again when the scan changes (refresh, change journal after a
  cached start). Compare: same contents / same name + contents / same name + size (fast; the only choice for remote
  scans and index files); MD5 or SHA-256; minimum size (default 1 MB). Files: size → quick checksum (start + end) →
  full checksum, 4 in parallel; hard links of one file count as one copy (groups that are only links are left out).
  Folders: order-independent signature of names + sizes, then of checksums; only the highest folders of a match are
  listed; "contained in others" finds folders whose files all sit at the same places in a bigger folder (the identical
  sub folders of such a pair are not listed again). Results grouped with collapsible headers and the space that could
  be freed; keep rules (oldest, newest, shortest / longest path, inside the tree selection, first) select the other
  copies; delete (recycle bin / for good, warns when every copy of a group is selected), hard-link (Windows; the
  folder is scanned again afterwards), move, export. A selected group is marked in the tree (expanded, orange
  bar) and outlined in the treemap; "Show in chart" opens the treemap on the folder holding all copies.
- Checksums remembered (checksums.db: path + size + modified time, unused entries dropped after 120 days; Settings… ›
  Forget checksums): a search after a refresh only reads changed files.
- Clone selected… (copy-on-write: ReFS / Dev Drive block cloning, FICLONE on btrfs / XFS / bcachefs): stored once, each
  file stays its own, dates kept; tested on a Dev Drive. Hard links on Linux too (link + rename over the copy). Copies
  sharing their blocks (clones, hard links) count as one copy.
- Protected folders (never selected or deleted / linked / moved: Windows, Program Files, ProgramData, .git, …) and
  ignored folders ($Recycle.Bin, …), editable in Settings…; "All open scans" finds copies spread over several scans.
- `byteatlas-cli duplicates <paths…>` (--folders, --match, --hash, --min-size, --ignore, --cache, --export).
- Result grids can be read and selected through UI automation (Value pattern: "3,5", "3-7", "all"); a selected
  duplicate group is scrolled to in the tree without changing the tree selection.

## Open
- [ ] Update check (GitHub releases) + winget / Scoop manifests.
- [ ] "What grew" view: folders that grew most since the last history snapshot.
- [ ] Low-space alerts from scheduled scans (desktop notification below a threshold).
- [ ] GUI log file (errors, scan timings).
- [ ] Release 0.5.0.
- [ ] Later: similar images / audio; memory + speed pass for 10 M+ file scans.
- [ ] Chart colours per file type: every extension / file group gets its own colour in the treemap, pie and bar
      charts, with a legend, editable colours (Options), the same colour for a type everywhere in the app.
- [ ] Details view: file and folder icons, with folder icons drawn for both dark and light mode (no light outlines
      on dark), chosen to match the current theme.

- [ ] More remote sources: SharePoint / OneDrive through Microsoft Graph (sign-in), Outlook mailboxes, phones via MTP.
- [ ] Report designer + print preview inside the app; SMTP mail; portable mode; cleanup recommendations;
      treemap colour by age / growth; SMART disk health.
- [ ] Linux arm64 and macOS packages; Linux SMART.
- [ ] Print preview inside the app (currently: PDF opened in the viewer).
- [ ] Explorer menu and drag-out need a manual check (UI automation cannot drive them).
- [ ] Release: publish GUI + CLI side by side (GUI looks for byteatlas-cli next to itself), installer, portable zip,
      "create portable installation", GitHub repo + release (ask first).
- [ ] Translations.
