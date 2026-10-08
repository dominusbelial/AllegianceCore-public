#!/usr/bin/env bash
# P575 — bots pod-home dock showcase: spawn two opposite-side combat scenarios,
# let the bots die into lifepods, then the BotManager DrivePodHome path (now the
# SHARED DockApproach bay planner) flies each pod home to dock. Counts server
# [dock] vs collision lines.
set -uo pipefail
DOTNET=/home/domingo/.dotnet/dotnet
REPO=/home/domingo/Development/AllegianceCore
ART=/home/domingo/Development/Artwork
PORT=${1:-6113}
OUT=${2:-/home/domingo/Development/AllegianceCore/docs/reports/P575_assets/bots_dock}
MAP=m2xspiderw.igc
SECTOR=${3:-Alderan}
WAIT=${4:-160}
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
echo "[p575bots] out=$OUT sector=$SECTOR wait=${WAIT}s"

SRV=$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server
CLI=$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame
MCP=$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp
DRV=$REPO/scripts/mcp_driver.py
[ -x "$SRV" ] || { echo "no release server"; exit 2; }

cat > "$OUT/server.json" <<EOF
{ "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT, "updatesPerSecond": 20,
  "games": [ { "name": "P575B", "map": "$MAP", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
  "liveSettings": { "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*" ] }, "debugMode": true } }
EOF

PIDS=()
cleanup(){ for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; sleep 1; for p in "${PIDS[@]}"; do kill -9 "$p" 2>/dev/null; done; }
trap cleanup EXIT INT TERM

"$SRV" --config "$OUT/server.json" > "$OUT/server.out" 2>&1 &
SRV_PID=$!; PIDS+=("$SRV_PID")
for i in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && { exec 3>&- 3<&-; break; }; sleep 1; done
echo "[p575bots] server up :$PORT"

: > "$OUT/cmds.jsonl"; : > "$OUT/mcp_out.log"; : > "$OUT/mcp_err.log"
python3 "$DRV" "$MCP" 127.0.0.1 "$PORT" "$OUT/cmds.jsonl" "$OUT/mcp_out.log" "$OUT/mcp_err.log" >> "$OUT/mcp_out.log" 2>&1 &
MCP_PID=$!; PIDS+=("$MCP_PID")
sleep 3

# Two opposite-side combat squads in the arena sector: they fight, ships die,
# pods spawn and DrivePodHome flies them home to dock.
printf '%s\n' '{"tool":"spawn_scenario","args":{"size":3,"target_sector":"'$SECTOR'","arena":true,"behavior":"combat"}}' >> "$OUT/cmds.jsonl"
for i in $(seq 1 90); do
  n=$(grep -c 'RESULT spawn_scenario ' "$OUT/mcp_out.log" 2>/dev/null || true)
  [ "$n" -ge 1 ] && break; sleep 1
done
echo "[p575bots] scenario spawned: $(grep -c 'RESULT spawn_scenario ' "$OUT/mcp_out.log")"

sleep "$WAIT"
echo "[p575bots] ---- server pod/dock/collision summary ----"
echo "pod fly-home lines:   $(grep -ci 'flying home\|pod bot' "$OUT/server.out" "$OUT/mcp_out.log" 2>/dev/null | awk -F: '{s+=$2} END{print s}')"
echo "server [dock] total:  $(grep -ci '\[dock\]' "$OUT/server.out")"
echo "pod DOCKED at station:$(grep -ci 'pod.*DOCKED\|DOCKED.*pod\|pod reached home' "$OUT/server.out" "$OUT/mcp_out.log" 2>/dev/null | awk -F: '{s+=$2} END{print s}')"
echo "collision lines:      $(grep -ci 'collision' "$OUT/server.out")"
echo "---- server [dock] lines ----"
grep -i '\[dock\]' "$OUT/server.out" | tail -25
echo "---- MCP pod-home lines ----"
grep -i 'flying home\|DOCKED at station\|p539' "$OUT/mcp_out.log" | tail -25
