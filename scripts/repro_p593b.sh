#!/usr/bin/env bash
# P593b — reproduction: Iron Coalition (25) vs Ga'Taraan (3) 15v15 showcase
# fight with a real-window observer capturing combat frames (white-shard
# "before" state on current code). Modeled on repro_p591d_final.sh.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=${PORT:-6085}
OUT="${1:-$REPO/p593b-repro}"
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

echo "== invest both sides (grant tech+money) =="
feed '{"tool":"invest","args":{"side":0,"bucket":".Caltrop","observe_seconds":60}}'
feed '{"tool":"invest","args":{"side":1,"bucket":".Caltrop","observe_seconds":60}}'
sleep 10

echo "== spawn 15v15 showcase at Alderan (wingmen UseMissiles/UseMines) =="
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan"}}'
sleep 8
grep -o '"mode":[^,]*' "$MCO" | tail -3

echo "== real-window observer capture =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "18,24,30,36,42,48,54,60" --autoshot-dir "$OUT" --exit-after 66 \
  --camera chase --window 2560x1440 --auto-goto Alderan --auto-target enemy > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 130); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo "=== server: missile activity ==="
echo "FIRE_MISSILE lines: $(grep -hc 'FIRE_MISSILE\|missile' "$OUT/server.out" 2>/dev/null)"
echo "=== client: missile draw / fallback lines ==="
echo "LIT-SOLID fallback lines: $(grep -hic 'lit-solid' "$OUT/client.out" 2>/dev/null)"
grep -hiE "missile|lit-solid|P593" "$OUT/client.out" 2>/dev/null | head -30
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"