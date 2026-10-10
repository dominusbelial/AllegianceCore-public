#!/usr/bin/env bash
# P591d — cleanest live proof: side 0's field at the enemy (side 1) arrival
# aleph 2183 (Juniper). Invest side 0 (reliable), spawn combat on BOTH sides so
# side 1's fleet routes via Juniper INTO side 0's field -> hits + pops. Window
# capture of the field at Alderan.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=6083
OUT="$REPO/p591d-hit2"
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

echo "== invest side 0 (fast/reliable) =="
feed '{"tool":"invest","args":{"side":0,"bucket":".Caltrop","observe_seconds":150}}'
for i in $(seq 1 150); do if grep -q '"Completed":true' "$MCO"; then echo "side0 invest completed ~${i}s"; break; fi; sleep 1; done

echo "== spawn 15v15 + minefields both (side0 field at enemy arrival 2183) =="
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}'
sleep 3
grep -o '"minefields":\[[^]]*\]' "$MCO" | tail -c 1300

echo "== wait for field deploy (polling 170s) =="
DEP=0
for i in $(seq 1 170); do
  DEP=$(grep -c "deployed -> EXPORT(26)/OT_mine" "$OUT/server.out" 2>/dev/null)
  [ "$DEP" -ge 1 ] && { echo "field deployed ~${i}s"; break; }
  sleep 1
done
echo "deployed: $DEP"
sleep 15   # let the enemy (side 1) fleet from Organia route via Juniper INTO the field

echo "== real-window capture at Alderan =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "10,20,30,40,50,60" --autoshot-dir "$OUT" --exit-after 65 \
  --camera chase --window 2560x1440 --auto-goto Alderan > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 115); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "=== deploy + audience ==="
grep -hoE "\[mine\] ship [0-9]+ deployed -> EXPORT\(26\)/OT_mine [0-9]+ pos Vector3 \{ X = [-0-9.]+, Y = [-0-9.]+, Z = [-0-9.]+ \} life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out"
echo "=== hits/pops/strength ==="
echo "hit by mine: $(grep -hc 'hit by mine' "$OUT/server.out")"
echo "c_etMine pops: $(grep -hc 'c_etMine pop' "$OUT/server.out")"
echo "MINE_STRENGTH: $(grep -hc 'MINE_STRENGTH' "$OUT/server.out")"
echo "=== crossings through field alephs ==="
grep -hE "crossed from .* to .Alderan" "$OUT/server.out" | tail -15
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
