#!/usr/bin/env bash
#
# P608 — local reproduction of the pool-mode bot-navigation regression (and its
# fix) WITHOUT touching 221. Launches a LOCAL server (thread-per-peer or pool)
# with the allegiance-mcp showcase, points every combat bot at the Alderan
# rendezvous sector (1052) with STAGING OFF (so bots must transit real warps the
# moment they spawn — the FlyWarpLeg own-pose path under test), lets them run,
# and measures how many bots actually cross at least one warp / reach the goal.
# The metric comes from the bots' own throttled [p433-diag] lines (cluster over
# time): a healthy transport lets a bot's reported cluster change as it transits;
# the P606-pool bug leaves every bot reporting its spawn cluster forever.
#
# Usage: scripts/repro_p608.sh <mode> <size_scale> <duration_s> <outdir>
#   mode   = pool | thread-per-peer
# Stops every process it started (by exact PID) before exiting.
set -uo pipefail
DOTNET="${DOTNET:-$(command -v dotnet)}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
MODE="$1"; SIZE_SCALE="$2"; DURATION="$3"; OUTDIR="$4"
PORT=6099
mkdir -p "$OUTDIR"; OUTDIR="$(cd "$OUTDIR" && pwd)"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"
[ "$MODE" = "pool" ] && TRANS='"mode": "pool"' || TRANS='"mode": "thread-per-peer"'
SERVER_CFG="$OUTDIR/server-$MODE-s$SIZE_SCALE.json"
CSV="$OUTDIR/tick-$MODE-s$SIZE_SCALE.csv"
cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [
    { "name": "P608 repro", "map": "XXV_simple.igc", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] }
  ],
  "transport": { $TRANS },
  "broadcast": { "parallelBuild": true },
  "profiler": { "enabled": true, "reportIntervalSeconds": 5, "csvPath": "$CSV" },
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*" ] },
    "debugMode": true
  }
}
EOF
rm -f "$CSV"
SROUT="$OUTDIR/server-$MODE-s$SIZE_SCALE.out"
ERRLOG="$OUTDIR/mcp_err-$MODE-s$SIZE_SCALE.log"
echo "[p608] mode=$MODE size_scale=$SIZE_SCALE duration=${DURATION}s out=$OUTDIR"
PIDS=()
cleanup(){ for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill "$pid" 2>/dev/null; done; sleep 1; for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null; done; }
trap cleanup EXIT INT TERM
"$SERVER_EXE" --config "$SERVER_CFG" > "$SROUT" 2>&1 &
SRV_PID=$!; PIDS+=("$SRV_PID")
for i in $(seq 1 60); do
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then exec 3>&- 3<&-; break; fi
  kill -0 "$SRV_PID" 2>/dev/null || { echo "server died early"; tail -5 "$SROUT"; exit 1; }
  sleep 1
done
echo "[p608] server pid $SRV_PID listening on :$PORT ($MODE)"
sleep 2
CMD_FILE="$OUTDIR/mcp_cmds-$MODE-s$SIZE_SCALE.jsonl"; MC_OUT="$OUTDIR/mcp_out-$MODE-s$SIZE_SCALE.log"
: > "$CMD_FILE"; : > "$MC_OUT"; : > "$ERRLOG"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$ERRLOG" >> "$MC_OUT" 2>&1 &
MCP_PID=$!; PIDS+=("$MCP_PID")
sleep 3
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": {\"preset\": \"showcase\", \"size_scale\": $SIZE_SCALE, \"target_sector\": \"Sedna\", \"staging\": false}}" >> "$CMD_FILE"
found=0
for i in $(seq 1 240); do grep -q "RESULT spawn_scenario " "$MC_OUT" 2>/dev/null && { found=1; break; }; sleep 1; done
echo "[p608] spawn RESULT seen=$found"; grep "RESULT spawn_scenario" "$MC_OUT" | head -1

# Mid-run snapshots every 60 s.
for mm in $(seq 1 $((DURATION/60))); do
  sleep 60
  echo "[p608] --- $(date +%H:%M:%S) after ${mm} min ---"
  python3 - "$ERRLOG" <<'PY'
import re, sys
pat = re.compile(r"bot (\S+) \(\w+\) cluster (\d+) goal (\d+)")
seen = {}
for line in open(sys.argv[1], errors="replace"):
    m = pat.search(line)
    if m: seen[m.group(1)] = m.group(2)
print(f"  bots_reporting={len(seen)}")
PY
done
echo "[p608] final analysis at $(date +%H:%M:%S):"
python3 - "$ERRLOG" "$MODE" "$SIZE_SCALE" "$DURATION" <<'PY'
import re, sys
pat = re.compile(r"bot (\S+) \(\w+\) cluster (\d+) goal (\d+)")
first, last, goal = {}, {}, {}
for line in open(sys.argv[1], errors="replace"):
    m = pat.search(line)
    if m:
        name, cl, gl = m.group(1), m.group(2), m.group(3)
        first.setdefault(name, cl); last[name] = cl; goal[name] = gl
uniq = len(first); crossed = sum(1 for n in first if last[n] != first[n])
stuck = uniq - crossed
atgoal = sum(1 for n in first if last[n] == goal[n])
mode, scale, dur = sys.argv[2], sys.argv[3], sys.argv[4]
print(f"FINAL mode={mode} scale={scale} dur={dur}s bots={uniq} crossed_ge1_warp={crossed} stuck_in_spawn={stuck} at_goal={atgoal}")
print(f"cross_pct={100*crossed/max(uniq,1):.1f} at_goal_pct={100*atgoal/max(uniq,1):.1f}")
PY
printf '%s\n' '{"tool": "quit"}' >> "$CMD_FILE"; sleep 2
echo "[p608] done."
