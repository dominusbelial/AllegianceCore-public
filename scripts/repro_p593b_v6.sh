#!/usr/bin/env bash
# P593b — reproduction v6: arena 1v1 in the observer's sector so ONE enemy bot
# (side 1, lit-solid launcher) attacks the hovering observer at close range,
# firing missiles AT it → crisp white-shard volleys in frame. Observer faces
# the enemy (auto-target) in chase cam, dense autoshots.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=${PORT:-6089}
OUT="${1:-$REPO/p593b-repro6}"
SECTOR="${2:-Ganymede}"
LAUNCHER="${3:-MRM Quickfire 1}"
rm -rf "$OUT"; mkdir -p "$OUT"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"
cat > "$OUT/server.json" <<EOF
{
  "artPath": "/home/domingo/Development/Artwork", "staticCore": "PCore001.igc", "port": $PORT,
  "updatesPerSecond": 20, "consoleVerbose": true,
  "games": [ { "name": "P593b IC vs GT", "map": "m2xspiderw.igc", "teams": 2, "civIds": [ 25, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
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

echo "== arena 1v1 ${LAUNCHER} at $SECTOR (enemy attacks observer) =="
feed "{\"tool\":\"spawn_scenario\",\"args\":{\"size\":1,\"target_sector\":\"$SECTOR\",\"arena\":true,\"launcher\":\"$LAUNCHER\"}}"
for i in $(seq 1 120); do
  if grep -q "RESULT spawn_scenario " "$MCO" 2>/dev/null; then echo "spawn RESULT seen (~${i}s)"; break; fi
  sleep 1
done
grep "RESULT spawn_scenario " "$MCO" | tail -1 | cut -c1-250

echo "== real-window observer capture =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "12,15,18,21,24,27,30,33,36,39,42,45" --autoshot-dir "$OUT" --exit-after 48 \
  --camera chase --window 2560x1440 --auto-target enemy > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 120); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo "=== combat evidence ==="
echo "bots seated: $(grep -hc 'seated on side' "$OUT/server.out" 2>/dev/null)"
echo "FIRE_MISSILE: $(grep -hc 'FIRE_MISSILE' "$OUT/server.out" 2>/dev/null)"
echo "missile lock sends: $(grep -hc 'MISSILE_LOCK_UPDATE' "$OUT/server.out" 2>/dev/null)"
grep -hiE "FIRE_MISSILE" "$OUT/server.out" 2>/dev/null | head -5
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"