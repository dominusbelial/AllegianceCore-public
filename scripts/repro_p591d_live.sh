#!/usr/bin/env bash
# P591d — live proof on the 221 config, NATURAL homes (no debugMode garage
# relocation): showcase 2x15v15 + minefields{both}. Captures server log
# (placement alephs, export audience, mine hits) and real-window screenshots.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
PORT=6077
OUT="$REPO/p591d-live"
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
printf '%s\n' '{"tool": "spawn_scenario", "args": {"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}' >> "$CMD"
found=0
for i in $(seq 1 300); do if grep -q "RESULT spawn_scenario" "$MCO" 2>/dev/null; then found=1; break; fi; if ! kill -0 "$SRV" 2>/dev/null; then break; fi; sleep 1; done
echo "spawn result=$found"
echo "=== minefields rows in spawn result ==="
grep -o '"minefields":\[[^]]*\]' "$MCO" | tail -c 1500
sleep 15
echo "[p591d] launching windowed client..."
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "20,30,40,50" --autoshot-dir "$OUT" --exit-after 60 \
  --camera chase --window 2560x1440 > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 100); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
printf '%s\n' '{"tool":"quit"}' >> "$CMD"; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null
echo
echo "=== minefield/layer lines ==="
grep -hE "\[mine\] ship .*deployed|\[layer\]|ordered BUILD" "$OUT/server.out" | grep -viE "began mining|is full" | tail -50
echo "=== export audience counts ==="
grep -hoE "OT_mine [0-9]+ pos .* life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out" | tail -40
echo "=== c_etMine pops ==="
grep -hc "c_etMine pop" "$OUT/server.out"
echo "=== mine hits ==="
grep -hc "hit by mine" "$OUT/server.out"
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
