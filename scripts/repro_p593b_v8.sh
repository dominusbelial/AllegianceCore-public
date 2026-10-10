#!/usr/bin/env bash
# P593b — reproduction v8 (after): arena 10v10 lit-solid launcher, hovering
# observer in the combat sector, dense real-window autoshots.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=${PORT:-6091}
OUT="${1:-$REPO/p593b-repro8}"
SECTOR="${2:-Ganymede}"
LAUNCHER="${3:-MRM Quickfire 1}"
SIZE="${4:-10}"
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

echo "== arena ${SIZE}v${SIZE} ${LAUNCHER} at $SECTOR =="
feed "{\"tool\":\"spawn_scenario\",\"args\":{\"size\":$SIZE,\"target_sector\":\"$SECTOR\",\"arena\":true,\"launcher\":\"$LAUNCHER\"}}"
for i in $(seq 1 120); do
  if grep -q "RESULT spawn_scenario " "$MCO" 2>/dev/null; then echo "spawn RESULT seen (~${i}s)"; break; fi
  sleep 1
done
grep "RESULT spawn_scenario " "$MCO" | tail -1 | cut -c1-250

echo "== real-window hovering observer capture =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "16,20,24,28,32,36,40,44,48,52,56,60" --autoshot-dir "$OUT" --exit-after 64 \
  --camera chase --window 2560x1440 > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 130); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo "=== combat evidence ==="
echo "bots seated: $(grep -hc 'seated on side' "$OUT/server.out" 2>/dev/null)"
echo "FIRE_MISSILE: $(grep -hc 'FIRE_MISSILE' "$OUT/server.out" 2>/dev/null)"
echo "=== audit lines ==="
grep -iE "P593b|P593 " "$OUT/client.out" | head -5
echo "=== PNGs (largest first) ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%s %f\n' | sort -rn | head -12
echo "done out=$OUT"