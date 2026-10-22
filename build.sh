#!/usr/bin/env bash
# Builds single-file, self-contained, portable RestPeach and restpeach-cli for Linux x64 into ./dist
# Optional version (./build.sh 1.2.0), stamped into both binaries; without it the csproj's <Version> is used.
set -euo pipefail
cd "$(dirname "$0")"
VERSIONARG=()
if [[ $# -gt 0 && -n "$1" ]]; then VERSIONARG=("-p:Version=$1"); fi
rm -rf dist
# Always from scratch: a stale incremental Release build can ship without the compiled XAML (the app dies at startup).
rm -rf src/RestPeach/obj/Release
dotnet publish src/RestPeach/RestPeach.csproj -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false "${VERSIONARG[@]}" \
  -o dist
# The CLI has no UI: trimmed, it is a small binary for CI agents.
dotnet publish src/RestPeach.Cli/RestPeach.Cli.csproj -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:PublishTrimmed=true \
  -p:TrimMode=partial \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false "${VERSIONARG[@]}" \
  -o dist
chmod +x dist/RestPeach dist/restpeach-cli
echo
echo "Listo: $(pwd)/dist/RestPeach y $(pwd)/dist/restpeach-cli"
echo "Para modo 100% portable (datos junto al binario) crea un archivo \"portable.txt\" al lado."
