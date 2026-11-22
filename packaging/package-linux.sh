#!/bin/bash
# Builds the Linux packages from publish/linux-x64. Runs on Linux or WSL (Unix permissions, dpkg-deb, Docker for rpm):
#   packaging/package-linux.sh <version> <repo root>
# Output in <repo>/release:
#   byteatlas_<v>_amd64.deb             Debian / Ubuntu / Mint   (/opt/byteatlas, /usr/bin/byteatlas + byteatlas-cli, menu entry)
#   byteatlas-<v>-1.x86_64.rpm          Fedora / RHEL / openSUSE (same layout; built in a Fedora container)
#   ByteAtlas-<v>-linux-x64.tar.gz      portable: unpack and run ./ByteAtlas
set -euo pipefail

VERSION="$1"
ROOT="$(cd "$2" && pwd)"
PUB="$ROOT/publish/linux-x64"
OUT="$ROOT/release"
PKG="$ROOT/packaging/linux"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

SUMMARY="disk space analyzer: folder sizes, treemap, duplicates, reports"
DESCRIPTION="ByteAtlas shows where disk space went: a size-sorted folder tree, treemap,
pie and bar charts, file types, owners, file ages and the largest files.
It finds large, old, temporary and duplicate files, writes Excel, PDF, HTML
and CSV reports, tracks growth over time and compares scans. It also scans
SSH servers, S3 / Azure Blob storage and WebDAV folders. The byteatlas-cli
command does the same from scripts and cron jobs."

# normalise permissions: dirs/files readable, the programs executable
fix_modes() {
  find "$1" -type d -exec chmod 755 {} +
  find "$1" -type f -exec chmod 644 {} +
  local f
  for f in "${@:2}"; do chmod 755 "$f"; done
}

# common file tree (/opt/byteatlas, /usr/bin links, menu entry, icons, docs) into $1
stage() {
  local R="$1"
  mkdir -p "$R/opt/byteatlas" "$R/usr/bin" "$R/usr/share/applications" "$R/usr/share/doc/byteatlas"
  cp -r "$PUB/." "$R/opt/byteatlas/"
  rm -f "$R/opt/byteatlas/"*.pdb "$R/opt/byteatlas/createdump"
  ln -s /opt/byteatlas/ByteAtlas "$R/usr/bin/byteatlas"
  ln -s /opt/byteatlas/byteatlas-cli "$R/usr/bin/byteatlas-cli"
  cp "$PKG/byteatlas.desktop" "$R/usr/share/applications/"
  for s in 48 128 256 512; do
    mkdir -p "$R/usr/share/icons/hicolor/${s}x${s}/apps"
    cp "$PKG/byteatlas-$s.png" "$R/usr/share/icons/hicolor/${s}x${s}/apps/byteatlas.png"
  done
  cp "$ROOT/README.md" "$ROOT/THIRD-PARTY-NOTICES.txt" "$R/usr/share/doc/byteatlas/"
  fix_modes "$R" "$R/opt/byteatlas/ByteAtlas" "$R/opt/byteatlas/byteatlas-cli"
}

# ---------- Debian / Ubuntu .deb ----------
DEB="$WORK/deb"
stage "$DEB"
mkdir -p "$DEB/DEBIAN"
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: ByteAtlas"
  echo "Source: https://github.com/Taoism/ByteAtlas"
  echo
  echo "Files: *"
  echo "License: MIT"
  echo
  sed 's/^$/./; s/^/ /' "$ROOT/LICENSE"
} > "$DEB/usr/share/doc/byteatlas/copyright"
chmod 644 "$DEB/usr/share/doc/byteatlas/copyright"
SIZE_KB=$(du -sk "$DEB" --exclude=DEBIAN | cut -f1)
{
  echo "Package: byteatlas"
  echo "Version: $VERSION"
  echo "Architecture: amd64"
  echo "Maintainer: Taoism <366831+Taoism@users.noreply.github.com>"
  echo "Installed-Size: $SIZE_KB"
  echo "Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, fonts-dejavu-core | fonts-liberation | fonts-noto-core, libx11-6, libice6, libsm6, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67, libssl3t64 | libssl3 | libssl1.1"
  echo "Section: utils"
  echo "Priority: optional"
  echo "Homepage: https://github.com/Taoism/ByteAtlas"
  echo "Description: $SUMMARY"
  echo "$DESCRIPTION" | sed 's/^/ /'
} > "$DEB/DEBIAN/control"
# refresh the menu and icon caches where the tools exist
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
EOF
cp "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
chmod 644 "$DEB/DEBIAN/control"
chmod 755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$OUT/byteatlas_${VERSION}_amd64.deb" >/dev/null
echo "built byteatlas_${VERSION}_amd64.deb"

# ---------- Fedora / RHEL / openSUSE .rpm (rpmbuild in a Fedora container) ----------
RPM="$WORK/rpm"
mkdir -p "$RPM/root" "$RPM/out"
stage "$RPM/root"
cp "$ROOT/LICENSE" "$RPM/root/usr/share/doc/byteatlas/LICENSE"
cat > "$RPM/byteatlas.spec" <<EOF
Name:           byteatlas
Version:        $VERSION
Release:        1
Summary:        ByteAtlas, $SUMMARY
License:        MIT
URL:            https://github.com/Taoism/ByteAtlas
BuildArch:      x86_64
AutoReqProv:    no
Requires:       (font(dejavusans) or font(liberationsans) or font(notosans))
Requires:       libstdc++.so.6()(64bit), libz.so.1()(64bit), libfontconfig.so.1()(64bit), libX11.so.6()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit), libssl.so.3()(64bit), libicu

%description
$DESCRIPTION

%install
cp -a /work/root/. %{buildroot}/

%files
/opt/byteatlas
/usr/bin/byteatlas
/usr/bin/byteatlas-cli
/usr/share/applications/byteatlas.desktop
/usr/share/icons/hicolor/*/apps/byteatlas.png
%doc /usr/share/doc/byteatlas
EOF
docker run --rm -v "$RPM:/work" fedora:41 bash -c \
  "dnf -q -y install rpm-build >/dev/null 2>&1 && rpmbuild -bb --quiet --define '_topdir /tmp/rb' --define '_build_id_links none' \
     --define '__os_install_post %{nil}' /work/byteatlas.spec && cp /tmp/rb/RPMS/x86_64/*.rpm /work/out/ && chown -R $(id -u):$(id -g) /work/out"
cp "$RPM/out/"*.rpm "$OUT/"
echo "built $(basename "$RPM"/out/*.rpm)"

# ---------- portable tarball ----------
T="$WORK/tar/ByteAtlas-$VERSION-linux-x64"
mkdir -p "$T"
cp -r "$PUB/." "$T/"
rm -f "$T/"*.pdb "$T/createdump"
cp "$ROOT/LICENSE" "$ROOT/README.md" "$ROOT/THIRD-PARTY-NOTICES.txt" "$PKG/byteatlas.desktop" "$PKG/byteatlas-256.png" "$T/"
fix_modes "$WORK/tar" "$T/ByteAtlas" "$T/byteatlas-cli"
tar --owner=0 --group=0 -C "$WORK/tar" -czf "$OUT/ByteAtlas-$VERSION-linux-x64.tar.gz" "ByteAtlas-$VERSION-linux-x64"
echo "built ByteAtlas-$VERSION-linux-x64.tar.gz"
