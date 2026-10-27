#!/usr/bin/env bash
# P834b — orientation sweep for the volumetric near/scene clamp.
#
# Runs the cost harness once per camera orientation (yaw about +Y, pitch about
# the camera's right axis; the blast is always placed dead ahead, so the framing
# is identical and only the camera's WORLD heading changes) and prints, per cell,
# the GPU time of the volumetric pass plus the captured frame's mean luma and lit
# coverage. A cell where the plume disappears (lit 0 %) or the cost collapses is
# a clamp bug: that is exactly how P834's `-rd.z` cosine failed (see
# docs/reports/P834_VOLUMETRIC_NEAR_COST.md §9).
#
#   tools/p834-vol-perf/orientation-sweep.sh <label> <outdir> [harness args...]
#
# Examples (from the repo root, one window at a time on :0):
#   # the shipped shader (the renderer's build-output copy)
#   tools/p834-vol-perf/orientation-sweep.sh fix .scratch/p834b/table/fix
#   # a specific bytecode under test, plus the default scenario override
#   tools/p834-vol-perf/orientation-sweep.sh p834 .scratch/p834b/table/p834 \
#       --shader .scratch/shaders/p834.mgfxo
#   # a no-clamp control (master P579 has no Minimal rung: force Low/64)
#   tools/p834-vol-perf/orientation-sweep.sh p579 .scratch/p834b/table/p579 \
#       --shader .scratch/shaders/p579.mgfxo --forced low
#
# Env: FRAMES (default 40), WARMUP (default 12), RES (default 2560x1440).
set -euo pipefail
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
label=${1:?usage: orientation-sweep.sh <label> <outdir> [harness args...]}
out=${2:?usage: orientation-sweep.sh <label> <outdir> [harness args...]}
shift 2
res=${RES:-2560x1440}
frames=${FRAMES:-40}
warmup=${WARMUP:-12}

"$DOTNET" build tools/p834-vol-perf/p834-vol-perf.csproj -v:q --nologo >/dev/null
bin=tools/p834-vol-perf/bin/Debug/net10.0/p834-vol-perf
mkdir -p "$out"

# yaw:pitch cells — the six headings P834b reports plus three pitch cases.
for cell in 0:0 45:0 90:0 135:0 180:0 270:0 0:-40 0:40 135:30; do
  yaw=${cell%%:*}; pitch=${cell##*:}
  "$bin" --res "$res" --scenarios inside:4:60 --frames "$frames" --warmup "$warmup" \
         --yaw "$yaw" --pitch "$pitch" --label "$label-y${yaw}p${pitch}" \
         --png "$out" "$@" 2>&1 \
    | sed -nE "s/^\[p834\] .*gpu mean +([0-9.]+).*/[${label} yaw=${yaw} pitch=${pitch}] gpu_mean_ms=\1/p;
               s/^\[p834-frame\] .*(capture luma=.*)/[${label} yaw=${yaw} pitch=${pitch}] \1/p"
done
