#!/usr/bin/env bash
# Run on the workstation GPU. Same fixture/camera as the quality captures.
# Usage: tools/p1057-aa-measure.sh OUTPUT [--post] [--style classic|modern] [--seconds N]
set -euo pipefail
cd "$(dirname "$0")/.."
output=${1:?Usage: tools/p1057-aa-measure.sh OUTPUT [--post] [--style classic|modern] [--seconds N]}
shift
mkdir -p "$output"
output=$(realpath "$output")
dotnet_cmd=${DOTNET:-dotnet}
for samples in 0 2 4 8; do
  "$dotnet_cmd" tools/p979-visual-proof/bin/Debug/net10.0/p979-visual-proof.dll \
    --aa-benchmark --samples "$samples" --out "$output" "$@"
done
