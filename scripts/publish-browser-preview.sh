#!/usr/bin/env bash
# Publish a new, immutable generation. OUTPUT is the wwwroot the lobby will read.
# Configure BrowserPreview__BundlePath to OUTPUT and restart the lobby to switch.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ $# != 1 ]]; then echo "Usage: $0 OUTPUT_DIRECTORY" >&2; exit 2; fi
output=$(realpath -m "$1")
if [[ -e "$output" ]]; then echo "Output already exists; choose a new generation directory." >&2; exit 2; fi
mkdir -p "$(dirname "$output")"
stage=$(mktemp -d "$(dirname "$output")/.browser-preview.XXXXXX")
trap 'rm -rf "$stage"' EXIT
dotnet_cmd=${DOTNET:-$HOME/.dotnet/dotnet}
"$dotnet_cmd" publish src/Allegiance.ClientCore.Browser/Allegiance.ClientCore.Browser.csproj \
  -c Release -o "$stage/publish" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
mv "$stage/publish/wwwroot" "$output"
echo "Preview bundle: $output"
