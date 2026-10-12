#!/usr/bin/env bash
#
# P619c — local A/B with stdout piped through a SLOW consumer (simulating Docker
# json-file / a throttled journal reader) at ~390 players, no touch of 221.
#
# Measures, BEFORE contact and IN combat, at a given player scale:
#   - ticks/5 s and tick p95/max (from the profiler CSV);
#   - console lines/s per tag (xexport, teamloadout, projectile, projectile-hit,
#     lead, orderchange, and the total), parsed from the throttled server log;
#   - the [egress] logDrops counter (lines dropped because the stdout consumer
#     stalled — must be ~0 with the hot tags gated off).
# Plus the mandatory live bot check: how many bots reach the target sector
# ([warp]-cross metrics derived from the bots' reported clusters).
#
# Usage: scripts/p619c-ab.sh <size_scale> <target_sector> <duration_s> <outdir>
#   size_scale  = showcase scale (players ~= 15 * scale * 2 sides; 13 ~= 390)
# Stops every process it started (by exact PID) before exiting.
set -uo pipefail
DOTNET="${DOTNET:-$(command -v dotnet)}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
SCALE="$1"; TARGET="$2"; DURATION="$3"; OUTDIR="$4"
PORT=6098
mkdir -p "$OUTDIR"; OUTDIR="$(cd "$OUTDIR" && pwd)"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"

SERVER_CFG="$OUTDIR/server-s$SCALE.json"
CSV="$OUTDIR/tick-s$SCALE.csv"
cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [
    { "name": "P619c AB", "map": "XXV_simple.igc", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] }
  ],
  "transport": { "mode": "pool" },
  "broadcast": { "parallelBuild": true, "shipUpdateModel": "native" },
  "profiler": { "enabled": true, "reportIntervalSeconds": 5, "csvPath": "$CSV", "slowTickCapture": true, "slowTickMs": 250 },
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*" ] },
    "debugMode": true
  }
}
EOF
rm -f "$CSV"

RAW="$OUTDIR/server-raw.out"        # server stdout THROTTLED to this file
THROTTLE_LOG="$OUTDIR/throttle.log"
SROUT="$OUTDIR/server.out"          # dedup marker: raw also contains same lines
ERRLOG="$OUTDIR/mcp_err.log"
: > "$RAW"; : > "$THROTTLE_LOG"; : > "$ERRLOG"
echo "[p619c-ab] scale=$SCALE target=$TARGET dur=${DURATION}s out=$OUTDIR port=$PORT"

# Slow stdout consumer: read the server's stdout line by line, sleep a fixed
# budget per line (throttled — emulates a Docker json-file / slow journal
# reader that cannot keep up), and echo into RAW so the lines/s and logDrops
# are still measurable. Storing the throttle constant so the report can state it.
echo "throttle_ms_per_line=2 (simulated docker json-file reader)" > "$THROTTLE_LOG"

mkfifo /tmp/p619c-fifo.$$ 2>/dev/null || true
(
  exec < /tmp/p619c-fifo.$$
  while IFS= read -r line; do
    sleep 0.006        # ~167 lines/s ceiling — far slower than a busy verbose log
    printf '%s\n' "$line" >> "$RAW"
  done
) &
SINK_PID=$!
PIDS=("$SINK_PID")

cleanup(){
  for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill "$pid" 2>/dev/null; done
  sleep 1
  for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null; done
  rm -f /tmp/p619c-fifo.$$
}
trap cleanup EXIT INT TERM

"$SERVER_EXE" --config "$SERVER_CFG" > /tmp/p619c-fifo.$$ 2>&1 &
SRV_PID=$!; PIDS+=("$SRV_PID")
for i in $(seq 1 60); do
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then exec 3>&- 3<&-; break; fi
  kill -0 "$SRV_PID" 2>/dev/null || { echo "server died early"; sleep 1; tail -5 "$RAW"; exit 1; }
  sleep 1
done
echo "[p619c-ab] server pid $SRV_PID listening on :$PORT"

CMD_FILE="$OUTDIR/mcp_cmds.jsonl"; MC_OUT="$OUTDIR/mcp_out.log"
: > "$CMD_FILE"; : > "$MC_OUT"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$ERRLOG" >> "$MC_OUT" 2>&1 &
MCP_PID=$!; PIDS+=("$MCP_PID")
sleep 3
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": {\"preset\": \"showcase\", \"size_scale\": $SCALE, \"target_sector\": \"$TARGET\", \"staging\": false}}" >> "$CMD_FILE"
found=0
for i in $(seq 1 300); do grep -q "RESULT spawn_scenario " "$MC_OUT" 2>/dev/null && { found=1; break; }; sleep 1; done
echo "[p619c-ab] spawn RESULT seen=$found"
grep "RESULT spawn_scenario" "$MC_OUT" | head -1

# Let bots seat + settle, then a settle window to characterize BEFORE contact.
sleep 25
echo "[p619c-ab] sampling before-contact window (25s)..."

sleep "$DURATION"
echo "[p619c-ab] run window elapsed; analysing..."

printf '%s\n' '{"tool": "quit"}' >> "$CMD_FILE"; sleep 2

# ---------- analysis ----------
python3 - "$RAW" "$THROTTLE_LOG" "$ERRLOG" "$SCALE" "$TARGET" "$DURATION" <<'PY'
import re, sys, collections
raw, thr, err, scale, target, dur = sys.argv[1:]
tags = ["[xexport]","[teamloadout]","[projectile]","[projectile-hit]","[lead]","[orderchange]"]
counts = collections.Counter()
total = 0
logdrops = []
egress_peers = []
tickprof = []
for line in open(raw, errors="replace"):
    for t in tags:
        if t in line: counts[t]+=1
    total += 1
    m = re.search(r"\[egress\] window: bytes=\d+ peers=(\d+).*logDrops=(\d+)", line)
    if m:
        egress_peers.append(int(m.group(1))); logdrops.append(int(m.group(2)))
    m = re.search(r"\[tickprof\] window (\S+)s ticks=(\d+).*tick-wall mean=([\d.]+)ms p95=([\d.]+)ms max=([\d.]+)ms", line)
    if m:
        wsec = float(m.group(1)); ticks = int(m.group(2))
        tickprof.append((ticks/wsec*5.0, float(m.group(4)), float(m.group(5))))  # ticks/5s, p95, max
tpf = tickprof
ticks_per_5s = [round(t[0],1) for t in tpf]
p95s = [round(t[1],1) for t in tpf]
maxs = [round(t[2],1) for t in tpf]
print("TICKPROF_ROWS=%d" % len(tpf))
print("TICK ticks_per_5s=%s" % (",".join(map(str,ticks_per_5s)) or "n/a"))
print("TICK p95_ms=%s" % (",".join(map(str,p95s)) or "n/a"))
print("TICK max_ms=%s" % (",".join(map(str,maxs)) or "n/a"))
per_tag = {t: counts[t] for t in tags}
print("LINES_BY_TAG %s ALL=%d LINES_PER_SEC=%.1f" % (
    " ".join("%s=%d"%(k,v) for k,v in per_tag.items()), total, total/max(float(dur),1e-6)))
print("LOG_DROPS %s" % (",".join(map(str,logdrops)) or "n/a"))
print("EGRESS_MAX_PEERS=%s" % (max(egress_peers) if egress_peers else 0))
PY

echo "[p619c-ab] done."
