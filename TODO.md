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

## Open

- [ ] More remote sources: SharePoint / OneDrive through Microsoft Graph (sign-in), Outlook mailboxes, phones via MTP.
- [ ] Report designer + print preview inside the app; SMTP mail; portable mode; cleanup recommendations;
      treemap colour by age / growth; SMART disk health; Debian / Ubuntu .deb.
- [ ] Print preview inside the app (currently: PDF opened in the viewer).
- [ ] Explorer menu and drag-out need a manual check (UI automation cannot drive them).
- [ ] Release: publish GUI + CLI side by side (GUI looks for byteatlas-cli next to itself), installer, portable zip,
      "create portable installation", GitHub repo + release (ask first).
- [ ] Translations.
