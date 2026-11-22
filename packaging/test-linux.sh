#!/bin/bash
# Installs the built Linux packages in clean containers and checks the CLI and the GUI (under Xvfb):
#   packaging/test-linux.sh <version> <repo root>
set -uo pipefail
VERSION="$1"; ROOT="$(cd "$2" && pwd)"; OUT="$ROOT/release"
CHECK='set -e
byteatlas-cli --version
mkdir -p /tmp/t/a && head -c 300000 /dev/zero > /tmp/t/a/x.bin && byteatlas-cli scan /tmp/t --top 1 --unit b
byteatlas-cli scan /tmp/t --export /tmp/t.xlsx --export /tmp/t.pdf >/dev/null && ls -s /tmp/t.xlsx /tmp/t.pdf
rc=0; timeout 12 xvfb-run -a byteatlas /tmp/t >/tmp/gui.log 2>&1 || rc=$?
if [ $rc -eq 124 ]; then echo "GUI running after 12 s: OK"; else echo "GUI exited with $rc"; cat /tmp/gui.log; exit 1; fi
test -f /usr/share/applications/byteatlas.desktop && echo "menu entry: OK"'
run() { # image, install commands
  echo "===== $1"
  docker run --rm -v "$OUT:/rel:ro" "$1" bash -c "$2 && $CHECK" 2>&1 | grep -v "^debconf\|^Selecting\|^Preparing\|^Unpacking\|^Setting up\|^Processing\|^(Reading" | tail -12
  echo "exit ${PIPESTATUS[0]}"
}
APT='export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq xvfb xauth /rel/byteatlas_'"$VERSION"'_amd64.deb >/dev/null'
run debian:12 "$APT"
run ubuntu:24.04 "$APT"
run fedora:41 "dnf -q -y install xorg-x11-server-Xvfb xauth /rel/byteatlas-$VERSION-1.x86_64.rpm >/dev/null"
run debian:12 "export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq xvfb xauth libfontconfig1 libx11-6 libice6 libsm6 libicu72 libssl3 >/dev/null && tar -xzf /rel/ByteAtlas-$VERSION-linux-x64.tar.gz -C /opt && ln -s /opt/ByteAtlas-$VERSION-linux-x64/ByteAtlas /usr/local/bin/byteatlas && ln -s /opt/ByteAtlas-$VERSION-linux-x64/byteatlas-cli /usr/local/bin/byteatlas-cli && mkdir -p /usr/share/applications && cp /opt/ByteAtlas-$VERSION-linux-x64/byteatlas.desktop /usr/share/applications/"
