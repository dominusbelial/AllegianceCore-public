#!/usr/bin/env bash
# P591b — reproduce the layer-drone aleph ping-pong on the 221 config.
# Boots a local Allegiance.Server (m2xspiderw, civs 25/3), drives the MCP to
# spawn the showcase + minefields{both} at Alderan, then observes the drone /
# layer / warp logs and dumps the interesting lines.
set -uo pipefail
DOTNET="${DOTNET:-/home/domingo/.dotnet/dotnet}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="/home/domingo/Development/Artwork"
PORT="${PORT:-6073}"
OBSERVE="${OBSERVE:-380}"          # seconds of observation after spawn RESULT
OUT_DIR="${OUT_DIR:-$REPO/repro-p591b-$(date +%Y%m%d-%H%M%S)}"
SPAWN_JSON="${SPAWN_JSON:-{\"preset\":\"showcase\",\"size_scale\":1,\"target_sector\":\"Alderan\",\"minefields\":{\"side\":\"both\",\"count\":1,\"at\":\"rendezvous\"}}}"

mkdir -p "$OUT_DIR"; OUT_DIR="$(cd "$OUT_DIR" && pwd)"

SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"

SERVER_CFG="$OUT_DIR/server.json"
cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "consoleVerbose": true,
  "games": [
    { "name": "P591b Repro", "map": "m2xspiderw.igc", "teams": 2, "civIds": [ 25, 3 ], "sideNames": [ "Alpha", "Bravo" ] }
  ],
  "liveSettings": {
    "testUnlock": { "enabled": true, "allTech": true, "money": 1000000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*" ] },
    "debugMode": true
  }
}
EOF
echo "[repro] config -> $SERVER_CFG"

echo "[repro] starting server on :$PORT ..."
"$SERVER_EXE" --config "$SERVER_CFG" > "$OUT_DIR/server.out" 2>&1 &
SRV=$!
for i in $(seq 1 60); do
  (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && { exec 3>&- 3<&-; break; }
  if ! kill -0 "$SRV" 2>/dev/null; then echo "[repro] server died"; tail -30 "$OUT_DIR/server.out"; exit 1; fi
  sleep 1
done
echo "[repro] server listening (pid $SRV)"

CMD_FILE="$OUT_DIR/cmds.jsonl"; MC_OUT="$OUT_DIR/mcp_out.log"; MC_ERR="$OUT_DIR/mcp_err.log"
: > "$CMD_FILE"; : > "$MC_OUT"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$MC_ERR" >> "$MC_OUT" 2>&1 &
MCP=$!
sleep 3
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": $SPAWN_JSON}" >> "$CMD_FILE"
echo "[repro] spawn_scenario sent; waiting for RESULT..."
found=0
for i in $(seq 1 300); do
  if grep -q "RESULT spawn_scenario" "$MC_OUT" 2>/dev/null; then found=1; break; fi
  if ! kill -0 "$SRV" 2>/dev/null; then echo "[repro] server died mid-spawn"; break; fi
  sleep 1
done
if [ "$found" -eq 1 ]; then echo "[repro] spawn RESULT received"; else echo "[repro] WARNING no RESULT in 300s"; fi

echo "[repro] observing ${OBSERVE}s for drone/layer/warp behaviour..."
sleep "$OBSERVE"

printf '%s\n' '{"tool": "quit"}' >> "$CMD_FILE"
sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null
kill "$MCP" 2>/dev/null
echo
echo "[repro] ==== interested server lines ===="
grep -hE "\[drone\]|\[layer\]|\[warp\]|\[mine\]|ping|organia|gaia|rigel|alderan" "$OUT_DIR/server.out" | grep -viE "asteroid|lobby|heartbeat" | tail -150
echo
echo "[repro] crossing tallies:"
echo "  warp lines: $(grep -c '\[warp\] ship' "$OUT_DIR/server.out")"
echo "[repro] done. out=$OUT_DIR"
