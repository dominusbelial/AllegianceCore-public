#!/usr/bin/env bash
# P591d — focused proof of P452 damage + P574 per-hit pops with a REAL field
# crossed: lay ONE side's minefield at the ENEMY's arrival aleph, POLL until it
# physically deploys, then spawn enemy combat bots that route through that aleph
# into the field, and capture the real window.
set -uo pipefail
REPO=/home/domingo/Development/AllegianceCore
PORT=6081
OUT="$REPO/p591d-cross"
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

# Lay side 1's field (enemy = side 0, home Ganymede 1050 -> arrives via Rigel 2187).
echo "== invest side 1 =="
feed '{"tool":"invest","args":{"side":1,"bucket":".Caltrop","observe_seconds":180}}'
sleep 60
echo "== spawn 15v15 + minefields both (orders side 1 to 2187) =="
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}'

echo "== POLLING for the field to physically deploy (up to 150s) =="
DEP=0
for i in $(seq 1 150); do
  DEP=$(grep -c "deployed -> EXPORT(26)/OT_mine" "$OUT/server.out" 2>/dev/null)
  if [ "$DEP" -ge 1 ]; then echo "field deployed after ~${i}s"; break; fi
  sleep 1
done
echo "deployed fields: $DEP"

if [ "$DEP" -ge 1 ]; then
  echo "== field is up; now spawn enemy combat bots routed THROUGH the field =="
  echo "   (side 0 bots from Ganymede route via Rigel 2187 INTO Alderan = cross the field)"
  feed '{"tool":"spawn_bot","args":{"side":0,"hull":"adv_fighter","target_sector":"Alderan"}}'
  feed '{"tool":"spawn_bot","args":{"side":0,"hull":"adv_fighter","target_sector":"Alderan"}}'
  feed '{"tool":"spawn_bot","args":{"side":0,"hull":"adv_fighter","target_sector":"Alderan"}}'
  echo "== observing 60s for crossings/pops =="
  sleep 60
fi

echo "== real-window capture =="
export ALLEGIANCE_ARTWORK_PATH="/home/domingo/Development/Artwork"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "10,20,30,40,50" --autoshot-dir "$OUT" --exit-after 55 \
  --camera chase --window 2560x1440 --auto-goto Alderan > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 105); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "=== deploy + audience ==="
grep -hoE "\[mine\] ship [0-9]+ deployed -> EXPORT\(26\)/OT_mine [0-9]+ pos Vector3 \{ X = [-0-9.]+, Y = [-0-9.]+, Z = [-0-9.]+ \} life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out"
echo "=== ordered BUILD ==="
grep -hE "ordered BUILD" "$OUT/server.out" | tail -4
echo "=== hits/pops/strength ==="
echo "hit by mine: $(grep -hc 'hit by mine' "$OUT/server.out")"
echo "c_etMine pops: $(grep -hc 'c_etMine pop' "$OUT/server.out")"
echo "MINE_STRENGTH lines: $(grep -hc 'MINE_STRENGTH' "$OUT/server.out")"
echo "MINE_DESTROYED: $(grep -hc 'MINE_DESTROYED' "$OUT/server.out")"
echo "=== enemy crossings ==="
grep -hE "crossed from .* to .Alderan" "$OUT/server.out" | tail -8
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
