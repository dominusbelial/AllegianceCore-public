#!/usr/bin/env bash
# P834 — run the volumetric cost harness once per resolution (one process each:
# MonoGame's DesktopGL backbuffer path is not reliable across an in-process
# ApplyChanges on this driver, so a single process per resolution keeps the
# GPU-timer measurements honest) and concatenate the per-sample CSVs.
#
#   tools/p834-vol-perf/run.sh <label> <outdir> [harness args...]
#
# Example:
#   tools/p834-vol-perf/run.sh before .scratch/p834/before --frames 120 --warmup 30 --png .scratch/p834/before
set -euo pipefail
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
label=${1:?usage: run.sh <label> <outdir> [args...]}
out=${2:?usage: run.sh <label> <outdir> [args...]}
shift 2
mkdir -p "$out"
bin=tools/p834-vol-perf/bin/Debug/net10.0/p834-vol-perf
"$DOTNET" build tools/p834-vol-perf/p834-vol-perf.csproj -v:q --nologo >/dev/null
csv="$out/$label.csv"
: > "$csv"
first=1
for res in 1920x1080 2560x1440 3840x2160 5120x2160; do
  part="$out/$label-$res.csv"
  echo "==== $res ===="
  "$bin" --res "$res" --label "$label" --csv "$part" "$@"
  if [ $first -eq 1 ]; then cat "$part" >> "$csv"; first=0; else tail -n +2 "$part" >> "$csv"; fi
done
echo "csv: $csv"
