#!/usr/bin/env bash
# Local browser flight generation. No hosting/deployment changes.
set -euo pipefail
cd "$(dirname "$0")/.."
if (( $# < 5 )); then echo "Usage: $0 OUTPUT INTRO_CLASSIC INTRO_MODERN FLIGHT_TRACE..." >&2; exit 2; fi
output=$(realpath -m "$1")
intro_classic=$(realpath "$2")
intro_modern=$(realpath "$3")
shift 3
traces=()
for trace in "$@"; do traces+=("$(realpath "$trace")"); done
[[ ! -e "$output" ]] || { echo "Choose a new generation directory." >&2; exit 2; }
mkdir -p "$(dirname "$output")"
stage=$(mktemp -d "$(dirname "$output")/.browser-flight.XXXXXX")
trap 'rm -rf "$stage"' EXIT
"${DOTNET:-$HOME/.dotnet/dotnet}" publish src/Allegiance.ClientCore.Browser -c Release -o "$stage/publish" \
  --no-restore --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
python3 tools/p1047-browser-packs.py --traces "$intro_classic" "$intro_modern" \
  --artwork "${ALLEGIANCE_ARTWORK_PATH:?Set artwork path}" \
  --content src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content \
  --output "$stage/publish/wwwroot" --receipt "$stage/publish/wwwroot/starter-receipt.json"
python3 tools/p1051-browser-packs.py --traces "${traces[@]}" --artwork "$ALLEGIANCE_ARTWORK_PATH" \
  --content src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content \
  --output "$stage/publish/wwwroot" --receipt "$stage/publish/wwwroot/join-receipt.json"
python3 tools/p1054-browser-packs.py --root "$stage/publish/wwwroot" --artwork "$ALLEGIANCE_ARTWORK_PATH" \
  --traces docs/reports/BROWSER_SECTOR_PACKS_P1054_assets/desktop-classic.json \
    docs/reports/BROWSER_SECTOR_PACKS_P1054_assets/desktop-modern.json
python3 tools/p1065-battle-packs.py --root "$stage/publish/wwwroot" --artwork "$ALLEGIANCE_ARTWORK_PATH"
python3 tools/p1066-scene-packs.py --root "$stage/publish/wwwroot" --artwork "$ALLEGIANCE_ARTWORK_PATH"
mapping_args=()
if [[ -n "${BROWSER_CLIENT_VERSION:-}" ]]; then mapping_args=(--client-version "$BROWSER_CLIENT_VERSION"); fi
python3 tools/p1053-browser-publish.py "$stage/publish/wwwroot" "${mapping_args[@]}"
python3 tools/p1061-browser-provenance.py "$stage/publish/wwwroot"
mv "$stage/publish/wwwroot" "$output"
echo "Local browser flight generation ready."
