#!/usr/bin/env bash
# P591d — full live proof on 221 (NATURAL homes, no debugMode): lay BOTH sides'
# minefields via explicit invest+order (long observe windows to beat the P560
# auto-invest race), resolve each to the ENEMY's arrival aleph, then launch a
# 15v15 showcase fleet and a windowed client, waiting long enough for the fleets
# to cross the fields (mine hits + violet pops + real-window field capture).
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=6078
OUT="$REPO/p591d-full"
rm -rf "$OUT"; mkdir -p "$OUT"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"
cat > "$OUT/server.json" <<EOF
{
  "artPath": "/home/domingo/Development/Artwork", "staticCore": "PCore001.igc", "port": $PORT,
  "updatesPerSecond": 20, "consoleVerbose": true,
  "games": [ { "name": "P591d Showcase", "map": "m2xspiderw.igc", "teams": 2, "civIds": [ 25, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
  "liveSettings": {
    "testUnlock": { "enabled": true, "allTech": true, "money": 1000000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*", "proof*" ] }
  }
}
EOF
"$SERVER_EXE" --config "$OUT/server.json" > "$OUT/server.out" 2>&1 &
SRV=$!
for i in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && { exec 3>&- 3<&-; break; }; sleep 1; done
echo "server up $SRV"
CMD="$OUT/cmds.jsonl"; MCO="$OUT/mcp_out.log"
: > "$CMD"; : > "$MCO"
python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD" "$MCO" "$OUT/mcp_err.log" >> "$MCO" 2>&1 &
MCP=$!
sleep 3
feed() { printf '%s\n' "$1" >> "$CMD"; }
wait_result() { for i in $(seq 1 "${2:-300}"); do if grep -q "RESULT $1 " "$MCO"; then return 0; fi; sleep 1; done; echo "WARN no result $1"; return 1; }

echo "== invest side 1 (Caltrop) =="
feed '{"tool":"invest","args":{"side":1,"bucket":".Caltrop","observe_seconds":300}}'; wait_result invest 300
echo "== invest side 0 (Caltrop) =="
feed '{"tool":"invest","args":{"side":0,"bucket":".Caltrop","observe_seconds":300}}'; wait_result invest 300
grep -A3 "RESULT invest" "$MCO" | tail -8

echo "== list drones =="
feed '{"tool":"list_drones","args":{}}'; wait_result list_drones 60
grep -A40 "RESULT list_drones" "$MCO" | grep -iE "Caltrop|drone|ship|pilot|type" | head

echo "== spawn 15v15 showcase with minefields both (placement via resolver) =="
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}'; wait_result spawn_scenario 300
grep -A2 "RESULT spawn_scenario" "$MCO" | tail -3
grep -o '"minefields":\[[^]]*\]' "$MCO" | tail -c 1200

echo "== launching windowed client (capture field + pops) =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "25,35,45,55,65,75,85,95" --autoshot-dir "$OUT" --exit-after 100 \
  --camera chase --window 2560x1440 > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 150); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "=== minefield deploy + layer lines ==="
grep -hE "\[mine\] ship .*deployed|\[layer\]|ordered BUILD|EXPORT\(26\)/OT_mine" "$OUT/server.out" | grep -viE "began mining|is full" | tail -50
echo "=== export audience counts ==="
grep -hoE "OT_mine [0-9]+ pos .* life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out" | tail -40
echo "=== mine hits / pops ==="
echo "c_etMine pops: $(grep -hc 'c_etMine pop' "$OUT/server.out")"
echo "hit by mine: $(grep -hc 'hit by mine' "$OUT/server.out")"
echo "MINE_DESTROYED: $(grep -hc 'MINE_DESTROYED' "$OUT/server.out")"
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
