#!/usr/bin/env bash
set -euo pipefail

PROJECT="Shype Login Server TCP.csproj"
OUTROOT="publish"

usage() {
  echo "Usage: $0 [win-x64|linux-x64|all]"
  echo "  Default: all"
}

rid=${1:-all}

do_publish() {
  local rid="$1"
  local outDir="$OUTROOT/$rid"
  echo "Publishing $rid single-file, self-contained to $outDir ..."
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$outDir"
  echo "\nOutput files:"
  ls -la "$outDir" | sed -n '1,200p'
}

# Restore once
dotnet restore "$PROJECT"

case "$rid" in
  win-x64) do_publish win-x64 ;;
  linux-x64) do_publish linux-x64 ;;
  all)
    do_publish win-x64
    do_publish linux-x64
    ;;
  *) usage; exit 1 ;;
 esac

echo "\nDone."
