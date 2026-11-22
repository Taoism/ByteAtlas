#!/bin/bash
# Runs the test suite on Linux in the .NET SDK container (WSL / Linux with Docker), on a copy of the repo:
#   tests/linux/run-tests.sh <repo root> [dotnet test filter]
# /mnt/other is a tmpfs (a second file system for the top-dir trash test); cron is installed for the scheduler test.
set -euo pipefail
ROOT="$(cd "$1" && pwd)"
FILTER="${2:-}"
docker run --rm --tmpfs /mnt/other:exec,mode=1777 -v "$ROOT:/repo:ro" \
  -e BYTEATLAS_OTHER_FS=/mnt/other -e BYTEATLAS_SCHEDULER_TESTS=1 -e FILTER="$FILTER" \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    set -e
    apt-get update -qq >/dev/null && apt-get install -y -qq cron fonts-dejavu-core >/dev/null 2>&1
    mkdir /src && cd /repo && tar --exclude=bin --exclude=obj --exclude=publish --exclude=release --exclude=.git -cf - . | tar -xf - -C /src
    cd /src
    if [ -n "$FILTER" ]; then
      dotnet test tests/ByteAtlas.Tests -nologo -v q --filter "$FILTER" --logger "console;verbosity=detailed" 2>&1 | grep -vE "^\s*$|Build|Restore|Determining" | head -80 || true
    else
      dotnet test tests/ByteAtlas.Tests -nologo -v q 2>&1 | grep -E "error|Failed|Passed!|\[FAIL\]" || true
    fi'
