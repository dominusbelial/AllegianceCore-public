#!/usr/bin/env bash
#
# P605 — A/B the transport send mode (thread-per-peer vs pool) on a LOCAL
# server (never 221) with the allegiance-mcp showcase at a target player count,
# profiler on, sampling /proc/<server-pid> for thread count, RSS, context
# switches and CPU, and parsing the profiler's [tickprof] log lines for the
# whole-tick wall mean/p95/max.
#
# Usage:  scripts/p605-ab.sh <mode> <size_scale> <settle_seconds> <outdir>
#   mode        = thread-per-peer | pool
#   size_scale  = showcase scale (players ≈ 15 * size_scale * 2 sides)
#   outdir      = where to write config + summary (created if absent)
#
# Prints a RESULT JSON line and writes outdir/summary-<mode>-s<scale>.json.
set -uo pipefail
DOTNET="${DOTNET:-$(command -v dotnet)}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"

MODE="$1"; SIZE_SCALE="$2"; SETTLE="$3"; OUTDIR="$4"
PORT=6073
mkdir -p "$OUTDIR"
OUTDIR="$(cd "$OUTDIR" && pwd)"

SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"

[ "$MODE" = "pool" ] && TRANS='"mode": "pool"' || TRANS='"mode": "thread-per-peer"'
[[ "${SKIP_BUILD:-0}" != "1" ]] && { echo "[p605-ab] building Release..."; (cd "$REPO" && "$DOTNET" build -c Release -m:8 -v:m) >/dev/null || { echo "BUILD FAILED"; exit 1; }; }

SERVER_CFG="$OUTDIR/server-${MODE}-s${SIZE_SCALE}.json"
CSV="$OUTDIR/tick-${MODE}-s${SIZE_SCALE}.csv"
cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [
    { "name": "P605 AB", "map": "XXV_simple.igc", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] }
  ],
  "transport": { $TRANS },
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

SROUT="$OUTDIR/server-${MODE}-s${SIZE_SCALE}.out"
echo "[p605-ab] mode=$MODE size_scale=$SIZE_SCALE settle=${SETTLE}s config=$SERVER_CFG"
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
echo "[p605-ab] server pid $SRV_PID listening on :$PORT (mode=$MODE)"
sleep 2

CMD_FILE="$OUTDIR/mcp_cmds-${MODE}-s${SIZE_SCALE}.jsonl"; MC_OUT="$OUTDIR/mcp_out-${MODE}-s${SIZE_SCALE}.log"
: > "$CMD_FILE"; : > "$MC_OUT"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$OUTDIR/mcp_err-${MODE}-s${SIZE_SCALE}.log" >> "$MC_OUT" 2>&1 &
MCP_PID=$!; PIDS+=("$MCP_PID")
sleep 3
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": {\"preset\": \"showcase\", \"size_scale\": $SIZE_SCALE}}" >> "$CMD_FILE"
found=0
for i in $(seq 1 240); do grep -q "RESULT spawn_scenario " "$MC_OUT" 2>/dev/null && { found=1; break; }; sleep 1; done
echo "[p605-ab] spawn RESULT seen=$found"; grep "RESULT spawn_scenario" "$MC_OUT" | head -1

# Settle, then sample /proc each 2 s (rejecting empty/non-numeric readings).
echo "[p605-ab] settling ${SETTLE}s (sampling /proc each 2s)..."
T_ARR=(); R_ARR=(); VC=0; NV=0
for s in $(seq 1 "$SETTLE"); do
  st="/proc/$SRV_PID/status"
  t=$(awk '/^Threads:/{print $2}' "$st" 2>/dev/null)
  r=$(awk '/^VmRSS:/{print $2}' "$st" 2>/dev/null)
  VC=$((VC + $(awk '/^voluntary_ctxt_switches:/{print $2}' "$st" 2>/dev/null)))
  NV=$((NV + $(awk '/^nonvoluntary_ctxt_switches:/{print $2}' "$st" 2>/dev/null)))
  [[ "$t" =~ ^[0-9]+$ ]] && T_ARR+=("$t")
  [[ "$r" =~ ^[0-9]+$ ]] && R_ARR+=("$r")
  sleep 2
done

# CPU% over the last ~10 s from /proc/<pid>/stat (utime+stime), clk ticks/HZ.
u1=$(awk '{print $14+$15}' "/proc/$SRV_PID/stat" 2>/dev/null); t1=$(date +%s.%N)
sleep 10
u2=$(awk '{print $14+$15}' "/proc/$SRV_PID/stat" 2>/dev/null); t2=$(date +%s.%N)
clk=$(getconf CLK_TCK 2>/dev/null || echo 100)
cpu=$(awk -v a="${u1:-0}" -v b="${u2:-0}" -v t1="$t1" -v t2="$t2" -v clk="$clk" 'BEGIN{d=t2-t1; if(d>0) printf "%.1f", ((b-a)/clk)/d*100; else print 0}')

avg(){ local n=0; local s=0; for x in "${@}"; do s=$((s + x)); n=$((n + 1)); done; [ "$n" -gt 0 ] && echo $((s / n)) || echo 0; }

# Whole-tick wall from the profiler's log line (last window): mean/p95/max ms.
TW=$(grep '\[tickprof\]' "$SROUT" 2>/dev/null | tail -1)
tick_mean=$(echo "$TW" | sed -n 's/.*tick-wall mean=\([0-9.]*\)ms.*/\1/p')
tick_p95=$(echo "$TW"  | sed -n 's/.*p95=\([0-9.]*\)ms.*/\1/p')
tick_max=$(echo "$TW"  | sed -n 's/.*max=\([0-9.]*\)ms.*/\1/p')
[ -z "$tick_mean" ] && { tick_mean="nan"; tick_p95="nan"; tick_max="nan"; }
simthr=$(awk -v m="${tick_mean:-0}" 'BEGIN{ if (m=="nan") printf "nan"; else printf "%.1f", (m/50.0)*100.0 }')

printf '{"mode":"%s","size_scale":%s,"threads_avg":%s,"threads_last":%s,"vmRssKb_avg":%s,"vol_ctx_total":%s,"nonvol_ctx_total":%s,"cpuPercent":%s,"tickMeanMs":%s,"tickP95Ms":%s,"tickMaxMs":%s,"simThreadPct":%s}\n' \
  "$MODE" "$SIZE_SCALE" "$(avg "${T_ARR[@]}")" "${T_ARR[-1]:-0}" "$(avg "${R_ARR[@]}")" "$VC" "$NV" "$cpu" "$tick_mean" "$tick_p95" "$tick_max" "$simthr" \
  | tee "$OUTDIR/summary-${MODE}-s${SIZE_SCALE}.json"

printf '%s\n' '{"tool": "quit"}' >> "$CMD_FILE"; sleep 2
echo "[p605-ab] done."
