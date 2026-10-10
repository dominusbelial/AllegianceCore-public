#!/usr/bin/env bash
# P593b — reproduction v2: IC(25) vs GT(3) 15v15 showcase spawned in the
# OBSERVER'S HOME SECTOR so the observer sits inside the combat immediately
# (no warping). Captures real-window frames of missile volleys.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=${PORT:-6086}
OUT="${1:-$REPO/p593b-repro}"
SECTOR="${2:-Ganymede}"
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

echo "== spawn 15v15 showcase at $SECTOR (observer home) =="
feed "{\"tool\":\"spawn_scenario\",\"args\":{\"preset\":\"showcase\",\"size_scale\":1,\"target_sector\":\"$SECTOR\"}}"
for i in $(seq 1 120); do
  if grep -q "RESULT spawn_scenario " "$MCO" 2>/dev/null; then echo "spawn RESULT seen (~${i}s)"; break; fi
  sleep 1
done
grep "RESULT spawn_scenario " "$MCO" | tail -1

echo "== real-window observer capture (in combat sector) =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "14,18,22,26,30,34,38,42,46,50" --autoshot-dir "$OUT" --exit-after 54 \
  --camera chase --window 2560x1440 --auto-target enemy > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 120); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo "=== server combat evidence ==="
echo "spawned bots: $(grep -hc 'seated on side' "$OUT/server.out" 2>/dev/null)"
echo "missile-fired: $(grep -hciE 'fire.?missile|missile.*fire|PROJECTILE' "$OUT/server.out" 2>/dev/null)"
grep -hiE "missile|showcase|wingman|UseMissiles" "$OUT/server.out" 2>/dev/null | head -15
echo "=== client: any missile draw fallback ==="
grep -hiE "missile|lit-solid|P593" "$OUT/client.out" 2>/dev/null | head -20
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"