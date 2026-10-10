#!/usr/bin/env bash
# P591d — live proof on the 221 config: showcase 2x15v15 + minefields{both},
# capture the server log (placement alephs, export audience, mine hits) AND a
# real-window client for the field visuals + violet pops.
set -uo pipefail
DOTNET="${DOTNET:-/home/domingo/.dotnet/dotnet}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="/home/domingo/Development/Artwork"
PORT=6073
OUT_DIR="${OUT_DIR:-$REPO/p591d-proof-$(date +%Y%m%d-%H%M%S)}"
SPAWN_JSON='{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}'
AUTOSHOTS="${AUTOSHOTS:-30,40,50,60,70,80}"
EXIT_AFTER="${EXIT_AFTER:-95}"
WIN="2560x1440"

mkdir -p "$OUT_DIR"; OUT_DIR="$(cd "$OUT_DIR" && pwd)"

SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"

cat > "$OUT_DIR/server.json" <<EOF
{
  "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT,
  "updatesPerSecond": 20, "consoleVerbose": true,
  "games": [ { "name": "P591d Showcase", "map": "m2xspiderw.igc", "teams": 2, "civIds": [ 25, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
  "liveSettings": {
    "testUnlock": { "enabled": true, "allTech": true, "money": 1000000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*", "proof*" ] },
    "debugMode": true
  }
}
EOF

echo "[p591d] starting server on :$PORT ..."
"$SERVER_EXE" --config "$OUT_DIR/server.json" > "$OUT_DIR/server.out" 2>&1 &
SRV=$!
for i in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && { exec 3>&- 3<&-; break; }; sleep 1; done
echo "[p591d] server up (pid $SRV)"

CMD_FILE="$OUT_DIR/cmds.jsonl"; MC_OUT="$OUT_DIR/mcp_out.log"; MC_ERR="$OUT_DIR/mcp_err.log"
: > "$CMD_FILE"; : > "$MC_OUT"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$MC_ERR" >> "$MC_OUT" 2>&1 &
MCP=$!
sleep 3
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": $SPAWN_JSON}" >> "$CMD_FILE"
echo "[p591d] spawn_scenario (minefields both) sent; waiting for RESULT..."
found=0
for i in $(seq 1 300); do
  if grep -q "RESULT spawn_scenario" "$MC_OUT" 2>/dev/null; then found=1; break; fi
  if ! kill -0 "$SRV" 2>/dev/null; then echo "[p591d] server died"; break; fi
  sleep 1
done
echo "[p591d] spawn RESULT received: $found"
grep -A2 "RESULT spawn_scenario" "$MC_OUT" | tail -3

# Give the minefields a little time to lay and the fight to develop, then
# launch the real windowed client to capture the field + pops.
sleep 12

echo "[p591d] launching windowed client (2560x1440, autoshots at ${AUTOSHOTS}s)..."
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "$AUTOSHOTS" --autoshot-dir "$OUT_DIR" --exit-after "$EXIT_AFTER" \
  --camera chase --window "$WIN" > "$OUT_DIR/client.out" 2>&1 &
CLIENT=$!
echo "[p591d] client pid $CLIENT"
for i in $(seq 1 $((EXIT_AFTER + 40))); do
  if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi
  sleep 1
done
kill "$CLIENT" 2>/dev/null

printf '%s\n' '{"tool":"quit"}' >> "$CMD_FILE"; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "[p591d] ==== [mine]/[layer]/[drone] server lines ===="
grep -hE "\[mine\]|\[layer\]|\[drone\]|ordered BUILD" "$OUT_DIR/server.out" | grep -viE "began mining|is full" | tail -60
echo
echo "[p591d] ==== export audience counts ===="
grep -hoE "OT_mine [0-9]+ pos .* life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT_DIR/server.out" | tail -40
echo
echo "[p591d] c_etMine pops: $(grep -hc 'c_etMine pop' "$OUT_DIR/server.out")"
echo "[p591d] done. out=$OUT_DIR"
