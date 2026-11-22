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

## Open

- [ ] Remote sources: SharePoint / OneDrive (Graph), Amazon S3, Azure Blob, WebDAV (beyond mapped drives),
      VHD(X) images (mount read-only, scan, dismount), Outlook mailboxes, Linux via SSH, phones via MTP.
- [ ] NTFS permissions: column, export, permissions popup; search by Active Directory user / group permissions.
- [ ] Shell context menu inside the app (IContextMenu), drag files out of the tree / lists into Explorer.
- [ ] Preview pane (text / images), thumbnails view, compare two files side by side.
- [ ] Group scans under a virtual root with summed totals.
- [ ] Windows 10/11 compact compression (WOF LZX / XPress): show the format, compress with it.
- [ ] "Add column for this extension / user / age interval to Details".
- [ ] Print preview inside the app (currently: PDF opened in the viewer).
- [ ] MFT scan of a small sub folder reads the whole MFT (~9 s): walk folders when the folder is small.
- [ ] Owners of folders in MFT mode (files have them).
- [ ] Release: publish GUI + CLI side by side (GUI looks for byteatlas-cli next to itself), installer, portable zip,
      "create portable installation", GitHub repo + release (ask first).
- [ ] Translations.
