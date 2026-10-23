#!/usr/bin/env bash
# P593b — v11 (after, deterministic): march 1v1; observer auto-targets the enemy
# AND we inject middle-click (fire missile) inputs into the client window so the
# observer launches a sustained point-blank missile volley that is definitely in
# the chase-cam frame. Real 2560x1440 autoshots throughout.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
PORT=${PORT:-6094}
OUT="${1:-$REPO/p593b-repro11}"
SECTOR="${2:-Ganymede}"
LAUNCHER="${3:-MRM Quickfire 1}"
rm -rf "$OUT"; mkdir -p "$OUT"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"
cat > "$OUT/server.json" <<EOF
{
  "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT,
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

echo "== march 1v1 ${LAUNCHER} at $SECTOR =="
feed "{\"tool\":\"spawn_scenario\",\"args\":{\"size\":1,\"target_sector\":\"$SECTOR\",\"mode\":\"march\",\"staging\":false,\"launcher\":\"$LAUNCHER\"}}"
for i in $(seq 1 120); do
  if grep -q "RESULT spawn_scenario " "$MCO" 2>/dev/null; then echo "spawn RESULT seen (~${i}s)"; break; fi
  sleep 1
done

echo "== observer captures while WE inject fire-missile clicks =="
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "18,20,22,24,26,28,30,32,34,36,38,40" --autoshot-dir "$OUT" --exit-after 44 \
  --camera chase --window 2560x1440 --auto-target enemy > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
# wait for the client to seat + launch (~16s), then inject fire clicks for ~20s
sleep 16
echo "== injecting fire-missile clicks =="
DISPLAY="${DISPLAY:-:0}" python3 /tmp/xinject.py 20 0.4 2>&1 | tail -3
echo "== waiting for client to exit =="
for i in $(seq 1 60); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo "=== FIRE_MISSILE by observer: $(grep -hc 'FIRE_MISSILE' "$OUT/server.out" 2>/dev/null) ==="
grep -hoE "ship 8100 fired -> FIRE_MISSILE[^\n]*" "$OUT/server.out" 2>/dev/null | head -3
echo "=== PNGs (largest first) ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%s %f\n' | sort -rn | head -12
echo "done out=$OUT"