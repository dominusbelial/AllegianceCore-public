#!/usr/bin/env bash
# P591d — decisive live proof on 221 (natural homes): lay BOTH fields via explicit
# invest+order, spawn the 15v15, wait LONG for the fleet to route to Alderan and
# CROSS the enemy-arrival fields, and point the real client at Alderan to capture
# the field + violet pops + mine hits.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
PORT=6079
OUT="$REPO/p591d-hit"
rm -rf "$OUT"; mkdir -p "$OUT"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"
cat > "$OUT/server.json" <<EOF
{
  "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT,
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
wait_result() { for i in $(seq 1 "${2:-400}"); do if grep -q "RESULT $1 " "$MCO"; then return 0; fi; sleep 1; done; echo "WARN no result $1"; return 1; }

# Lay both sides' fields (explicit long-window invests so neither races).
feed '{"tool":"invest","args":{"side":1,"bucket":".Caltrop","observe_seconds":300}}'; wait_result invest 300
feed '{"tool":"invest","args":{"side":0,"bucket":".Caltrop","observe_seconds":300}}'; wait_result invest 300

# Spawn the 15v15 WITH minefields both -> orders both drones to the enemy arrival alephs.
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}'; wait_result spawn_scenario 300
grep -o '"minefields":\[[^]]*\]' "$MCO" | tail -c 1200

# Wait for both fields to physically deploy, then give the fleet time to route.
echo "== waiting for fields to lay + fleet to route/cross (90s) =="
sleep 90

echo "== launching windowed client pointed at Alderan (auto-goto) =="
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "10,20,30,40,50,60,70,80,90,100,110" --autoshot-dir "$OUT" --exit-after 115 \
  --camera chase --window 2560x1440 --auto-goto Alderan > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 160); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "=== minefield deploy + audience ==="
grep -hoE "\[mine\] ship [0-9]+ deployed -> EXPORT\(26\)/OT_mine [0-9]+ pos Vector3 \{ X = [-0-9.]+, Y = [-0-9.]+, Z = [-0-9.]+ \} life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out"
echo "=== layer/ordered lines ==="
grep -hE "\[layer\]|ordered BUILD" "$OUT/server.out" | tail -10
echo "=== c_etMine pops ==="
echo "pops: $(grep -hc 'c_etMine pop' "$OUT/server.out")"
echo "hit by mine: $(grep -hc 'hit by mine' "$OUT/server.out")"
echo "MINE_DESTROYED: $(grep -hc 'MINE_DESTROYED' "$OUT/server.out")"
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
