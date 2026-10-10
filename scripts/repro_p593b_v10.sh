#!/usr/bin/env bash
# P593b — v10 (after): arena 1v1 (enemy attacks hovering observer) with DENSE
# autoshots to catch the incoming missile volley.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=${PORT:-6093}
OUT="${1:-$REPO/p593b-repro10}"
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

echo "== arena 1v1 ${LAUNCHER} at $SECTOR =="
feed "{\"tool\":\"spawn_scenario\",\"args\":{\"size\":1,\"target_sector\":\"$SECTOR\",\"arena\":true,\"launcher\":\"$LAUNCHER\"}}"
for i in $(seq 1 120); do
  if grep -q "RESULT spawn_scenario " "$MCO" 2>/dev/null; then echo "spawn RESULT seen (~${i}s)"; break; fi
  sleep 1
done

echo "== dense real-window capture =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "12,13.5,15,16.5,18,19.5,21,22.5,24,25.5,27,28.5,30,31.5,33,34.5,36,37.5,39,40.5,42,43.5,45,46.5,48,49.5,51,52.5,54,55.5,57,58.5" --autoshot-dir "$OUT" --exit-after 60 \
  --camera chase --window 2560x1440 --auto-target enemy > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 130); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo "FIRE_MISSILE: $(grep -hc 'FIRE_MISSILE' "$OUT/server.out" 2>/dev/null)"
echo "=== PNG count: $(find "$OUT" -name '*.png' | wc -l) ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%s %f\n' | sort -rn | head -12
echo "done out=$OUT"