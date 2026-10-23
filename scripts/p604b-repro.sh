#!/usr/bin/env bash
#
# P604b — reproduce the periodic broadcastShipStates hitch on a LOCAL server
# (never 221) with the concentrated native showcase (target_sector Haumea,
# travel-with-staging), native ship-update model, profiler on (CSV), and
# per-bucket mean/p95/max capture.
#
# Usage: scripts/p604b-repro.sh <build:serial|parallel> <transport:pool|tpp> <size_scale> <settle_s> <outdir>
set -uo pipefail
DOTNET="${DOTNET:-$(command -v dotnet)}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"

BUILD="$1"; TRANSPORT="$2"; SIZE_SCALE="$3"; SETTLE="$4"; OUTDIR="$5"
PORT=6073
mkdir -p "$OUTDIR"; OUTDIR="$(cd "$OUTDIR" && pwd)"

SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"

[ "$TRANSPORT" = "pool" ] && TR='"mode": "pool"' || TR='"mode": "thread-per-peer"'
[ "$BUILD" = "parallel" ] && PB='"parallelBuild": true' || PB='"parallelBuild": false'

SERVER_CFG="$OUTDIR/server-${BUILD}-${TRANSPORT}-s${SIZE_SCALE}.json"
CSV="$OUTDIR/tick-${BUILD}-${TRANSPORT}-s${SIZE_SCALE}.csv"
cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [
    { "name": "P604b", "map": "XXV_simple.igc", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] }
  ],
  "transport": { $TR },
  "broadcast": { $PB, "shipUpdateModel": "${SHIP_MODEL:-native}" },
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

SROUT="$OUTDIR/server-${BUILD}-${TRANSPORT}-s${SIZE_SCALE}.out"
echo "[p604b-repro] build=$BUILD transport=$TRANSPORT size_scale=$SIZE_SCALE settle=${SETTLE}s native+concentrated"
PIDS=()
cleanup(){ for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill "$pid" 2>/dev/null; done; sleep 1; for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null; done; }
trap cleanup EXIT INT TERM

"$SERVER_EXE" --config "$SERVER_CFG" > "$SROUT" 2>&1 &
SRV_PID=$!; PIDS+=("$SRV_PID")
for i in $(seq 1 60); do
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then exec 3>&- 3<&-; break; fi
  kill -0 "$SRV_PID" 2>/dev/null || { echo "server died early"; tail -20 "$SROUT"; exit 1; }
  sleep 1
done
echo "[p604b-repro] server pid $SRV_PID listening on :$PORT"
sleep 2

CMD_FILE="$OUTDIR/mcp_cmds.jsonl"; MC_OUT="$OUTDIR/mcp_out.log"
: > "$CMD_FILE"; : > "$MC_OUT"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$OUTDIR/mcp_err.log" >> "$MC_OUT" 2>&1 &
MCP_PID=$!; PIDS+=("$MCP_PID")
sleep 3
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": {\"preset\": \"showcase\", \"size_scale\": $SIZE_SCALE, \"target_sector\": \"Haumea\", \"staging\": true}}" >> "$CMD_FILE"
found=0
for i in $(seq 1 300); do grep -q "RESULT spawn_scenario " "$MC_OUT" 2>/dev/null && { found=1; break; }; sleep 1; done
echo "[p604b-repro] spawn RESULT seen=$found"; grep "RESULT spawn_scenario" "$MC_OUT" | head -1

echo "[p604b-repro] settling ${SETTLE}s..."
sleep "$SETTLE"

echo "[p604b-repro] === last [tickprof] window lines ==="
grep '\[tickprof\]' "$SROUT" | tail -40
echo "[p604b-repro] === per-bucket max (ms) from CSV (last windows) ==="
awk -F, 'NF>=7 && $3!="" && $7!="" && $7>0 {print $3, "mean=" $5, "p95=" $6, "max=" $7}' "$CSV" 2>/dev/null | tail -40

printf '%s\n' '{"tool": "quit"}' >> "$CMD_FILE"; sleep 2
echo "[p604b-repro] done."
