#!/usr/bin/env bash
# A fresh browser generation plus a starter pack derived from both desktop traces.
# Local/static proof only: the P1046 lobby artwork registry still serves only its v1 preview manifest.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ $# != 3 ]]; then echo "Usage: $0 OUTPUT_DIRECTORY CLASSIC_TRACE MODERN_TRACE" >&2; exit 2; fi
output=$(realpath -m "$1")
classic=$(realpath "$2")
modern=$(realpath "$3")
[[ ! -e "$output" ]] || { echo "Choose a new generation directory." >&2; exit 2; }
mkdir -p "$(dirname "$output")"
stage=$(mktemp -d "$(dirname "$output")/.browser-menu.XXXXXX")
trap 'rm -rf "$stage"' EXIT
dotnet_cmd=${DOTNET:-$HOME/.dotnet/dotnet}
"$dotnet_cmd" publish src/Allegiance.ClientCore.Browser/Allegiance.ClientCore.Browser.csproj \
  -c Release -o "$stage/publish" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
artwork=${ALLEGIANCE_ARTWORK_PATH:?Set ALLEGIANCE_ARTWORK_PATH to the read-only artwork corpus}
python3 tools/p1047-browser-packs.py --traces "$classic" "$modern" --artwork "$artwork" \
  --content src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content \
  --output "$stage/publish/wwwroot" --receipt "$stage/publish/wwwroot/pack-receipt.json"
mv "$stage/publish/wwwroot" "$output"
echo "Browser menu generation ready. Serve on loopback with tools/p1047-static-server.py."
