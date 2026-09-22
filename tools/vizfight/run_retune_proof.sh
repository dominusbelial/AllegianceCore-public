#!/usr/bin/env bash
# run_retune_proof.sh — P338: run ONE chase-camera live-proof capture pass.
#
#   run_retune_proof.sh <webPort> <duration> <outdir> <viewerHull> [fps]
#
# Launches vizfight in its `proof` mode (real server + gunner/victim companions,
# viewer parked next to a real warp), parses the real transport port, runs the
# MonoGame capture client in `chase` camera mode (the production radius-adaptive
# chase camera framing the local viewer ship), and leaves every frame in <outdir>.
set -uo pipefail

WEBPORT=$1; DUR=$2; OUT=$3; VHULL=$4; FPS=${5:-20}
DOTNET=/home/domingo/.dotnet/dotnet
REPO=/home/domingo/Development/AllegianceCore
VIZ="$REPO/tools/vizfight/bin/Release/net10.0/vizfight.dll"
CLIENTDIR="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0"

mkdir -p "$OUT"
OUT="$(realpath -m "$OUT")"
cd "$REPO"

# proof mode runs until Ctrl-C; give it a window longer than the capture.
RUNTIME=$((DUR + 15))
timeout $RUNTIME "$DOTNET" "$VIZ" proof "$WEBPORT" "$VHULL" > "$OUT/proof.log" 2>&1 &
VPID=$!

PORT=""
for _ in $(seq 1 90); do
    PORT=$(sed -n 's/.*TRANSPORT_PORT=\([0-9][0-9]*\).*/\1/p' "$OUT/proof.log" | head -1)
    if [ -n "$PORT" ]; then break; fi
    if ! kill -0 "$VPID" 2>/dev/null; then
        echo "FAIL: vizfight proof died:"; tail -30 "$OUT/proof.log"; exit 1
    fi
    sleep 0.5
done
if [ -z "$PORT" ]; then echo "FAIL: never got TRANSPORT_PORT"; tail -30 "$OUT/proof.log"; exit 1; fi
echo "RUN viewerHull=$VHULL duration=$DUR port=$PORT out=$OUT"

sleep 2
( cd "$CLIENTDIR" && timeout $((DUR + 20)) "$DOTNET" Allegiance.ClientCore.MonoGame.dll \
      --capture 127.0.0.1 "$PORT" "cap" "$DUR" "$OUT" "$FPS" chase ) > "$OUT/client.log" 2>&1 &
CPID=$!

wait "$CPID" 2>/dev/null
if kill -0 "$VPID" 2>/dev/null; then sleep 8; fi
if kill -0 "$VPID" 2>/dev/null; then kill "$VPID" 2>/dev/null; fi
wait "$VPID" 2>/dev/null

echo "=== proof log (key lines) ==="
grep -E '\[proof\]|TRANSPORT_PORT' "$OUT/proof.log" || true
echo "=== client log (key lines) ==="
grep -E 'nPoses|CAPTURE|error|FAIL' "$OUT/client.log" || true
echo "capture done: $(ls "$OUT"/frame_*.png 2>/dev/null | wc -l) png(s)"
