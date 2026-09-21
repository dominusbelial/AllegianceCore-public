#!/usr/bin/env bash
# run_stress_capture.sh — P306: run ONE tick-rate x bot-count stress-capture pass.
#
#   run_stress_capture.sh <ups> <bots> <duration> <webPort> <outdir> [maxfps]
#
# Launches vizfight in its real `stress` mode (real server at <ups> upd/s +
# <bots> real Bot peers on one team cruising continuously), parses the real
# transport port, samples the server process CPU% live, runs the MonoGame
# capture client (renders the whole fleet from an automatic overview camera
# into an offscreen target and saves a real PNG frame sequence + metrics),
# encodes the PNGs to an .mp4 with ffmpeg at the measured mean fps, and
# leaves every artifact in <outdir>.
set -uo pipefail

UPS=$1; BOTS=$2; DUR=$3; WEBPORT=$4; OUT=$5; MAXFPS=${6:-30}
DOTNET=/home/domingo/.dotnet/dotnet
REPO=/home/domingo/Development/AllegianceCore
VIZ="$REPO/tools/vizfight/bin/Release/net10.0/vizfight.dll"
CLIENTDIR="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0"

mkdir -p "$OUT"
cd "$REPO"

# vizfight runs its whole window and self-exits (printing the real per-tick
# summary). It must outlive the capture client, which starts ~2s in and runs
# DUR seconds -> ends at ~DUR+2. DUR+8 gives a safe margin while still exiting.
RUNTIME=$((DUR + 8))

"$DOTNET" "$VIZ" stress "$WEBPORT" "$BOTS" "$UPS" "$RUNTIME" 0 > "$OUT/stress.log" 2>&1 &
VPID=$!

# Wait for the real transport port.
PORT=""
for _ in $(seq 1 60); do
    PORT=$(sed -n 's/.*TRANSPORT_PORT=\([0-9][0-9]*\).*/\1/p' "$OUT/stress.log" | head -1)
    if [ -n "$PORT" ]; then break; fi
    if ! kill -0 "$VPID" 2>/dev/null; then
        echo "FAIL: vizfight died before binding:"; tail -30 "$OUT/stress.log"; exit 1
    fi
    sleep 0.5
done
if [ -z "$PORT" ]; then echo "FAIL: never got TRANSPORT_PORT"; tail -30 "$OUT/stress.log"; exit 1; fi
echo "RUN ups=$UPS bots=$BOTS duration=$DUR port=$PORT out=$OUT"

# Live CPU sampler of the server process (vizfight hosts the server + sim).
python3 - "$VPID" "$((DUR + 6))" "$OUT/cpu.tsv" <<'PY' &
import sys, time, os
pid = int(sys.argv[1]); dur = float(sys.argv[2]); outp = sys.argv[3]
def ticks():
    try:
        with open(f"/proc/{pid}/stat") as f:
            p = f.read().split()
        return int(p[13]) + int(p[14])   # utime + stime (process ticks)
    except Exception:
        return None
clk = os.sysconf('SC_CLK_TCK')
prev = None; prevt = None; tot = 0.0; n = 0
rows = []
end = time.time() + dur
while time.time() < end:
    t = time.time(); s = ticks()
    if s is not None and prev is not None and t > prevt:
        dt = t - prevt
        if dt > 0:
            cpu = (s - prev) / clk / dt * 100.0   # percent of one core
            tot += cpu; n += 1
            rows.append(f"{t:.3f}\t{cpu:.2f}")
    prev = s; prevt = t
    time.sleep(0.5)
with open(outp, "w") as f:
    for r in rows:
        f.write(r + "\n")
    mean = tot / max(1, n)
    f.write(f"#mean_cpu_percent_one_core={mean:.2f}\n")
print(f"CPUMEAN {mean:.2f}", file=sys.stderr)
PY
CPUPID=$!

# Give the bots a moment to all be assigned and underway, then start the client.
sleep 2
( cd "$CLIENTDIR" && timeout $((DUR + 25)) "$DOTNET" Allegiance.ClientCore.MonoGame.dll \
      --capture 127.0.0.1 "$PORT" "cap" "$DUR" "$OUT" "$MAXFPS" ) > "$OUT/client.log" 2>&1 &
CPID=$!

wait "$CPID" 2>/dev/null        # capture client self-exits after DUR seconds
wait "$CPUPID" 2>/dev/null      # CPU sampling done
if kill -0 "$VPID" 2>/dev/null; then   # vizfight should have self-exited; kill only if stuck
    sleep 8
fi
if kill -0 "$VPID" 2>/dev/null; then kill "$VPID" 2>/dev/null; fi
wait "$VPID" 2>/dev/null

echo "capture done: $(ls "$OUT"/frame_*.png 2>/dev/null | wc -l) png(s)"

# Encode at the measured mean render cadence (from frames.tsv real timestamps).
python3 - "$OUT" <<'PY'
import sys, os, glob
out = sys.argv[1]
frames = sorted(glob.glob(os.path.join(out, "frame_*.png")))
if not frames:
    print("NO FRAMES to encode"); sys.exit(0)
times = []
try:
    with open(os.path.join(out, "frames.tsv")) as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"): continue
            _, t = line.split("\t")
            times.append(float(t))
except Exception as e:
    print("no frames.tsv", e)
if len(times) >= 2 and times[-1] > times[0]:
    fps = (len(times) - 1) / (times[-1] - times[0])
else:
    fps = 15.0
print(f"encode mean fps = {fps:.2f} over {len(times)} frames")
with open(os.path.join(out, "mean_fps.txt"), "w") as f:
    f.write(f"{fps:.3f}\n")
cmd = (f"ffmpeg -y -loglevel error -framerate {fps:.4f} "
       f"-i {out}/frame_%06d.png -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p "
       f"{out}/run.mp4")
print(cmd)
os.system(cmd)
PY

echo "=== server tick metrics ==="
grep -E "\[stress\] (done|tick real-cost|overruns|achieved)" "$OUT/stress.log" || echo "(no summary — vizfight killed?)"
echo "=== client render metrics ==="
cat "$OUT/metrics.txt" 2>/dev/null || true
echo "=== CPU ==="
grep "#mean_cpu" "$OUT/cpu.tsv" || tail -1 "$OUT/cpu.tsv"
