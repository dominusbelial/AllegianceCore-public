#!/usr/bin/env bash
# P591d — decisive live proof on 221 (natural homes). Lay BOTH fields (explicit
# invests), spawn the 15v15, then INTERNALLY POLL the server log until mine hits
# / c_etMine pops actually appear (each side's fleet crosses the OTHER's arrival
# field), and capture a real-window frame of the field+pops.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
PORT=6080
OUT="$REPO/p591d-pop"
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

echo "== invest side 1 =="; feed '{"tool":"invest","args":{"side":1,"bucket":".Caltrop","observe_seconds":120}}'
echo "== invest side 0 =="; feed '{"tool":"invest","args":{"side":0,"bucket":".Caltrop","observe_seconds":120}}'

# Give invests time (bucket build ~90s) + then spawn.
sleep 120
echo "== spawn 15v15 + minefields both =="
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}'

# Poll the server log until pops/hits appear (up to 180s), or just observe 150s.
echo "== observing for field-lay + fleet crossing (polling for pops) =="
POPS=0
for i in $(seq 1 150); do
  POPS=$(grep -c "hit by mine" "$OUT/server.out" 2>/dev/null)
  LAYS=$(grep -c "deployed -> EXPORT(26)/OT_mine" "$OUT/server.out" 2>/dev/null)
  if [ "$LAYS" -ge 1 ]; then
    # fields laid; wait a bit more for the fleets to cross
    :
  fi
  sleep 1
  if [ "$POPS" -ge 1 ] && [ "$i" -ge 30 ]; then break; fi
done
echo "pops observed: $POPS"

echo "== launching windowed client pointed at Alderan =="
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "8,16,24,32,40,48,56" --autoshot-dir "$OUT" --exit-after 60 \
  --camera chase --window 2560x1440 --auto-goto Alderan > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 110); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "=== minefield deploy + audience ==="
grep -hoE "\[mine\] ship [0-9]+ deployed -> EXPORT\(26\)/OT_mine [0-9]+ pos Vector3 \{ X = [-0-9.]+, Y = [-0-9.]+, Z = [-0-9.]+ \} life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out"
echo "=== ordered BUILD lines ==="
grep -hE "ordered BUILD|\[layer\] ship .*— layer BUILD" "$OUT/server.out" | tail -6
echo "=== hits/pops ==="
echo "hit by mine: $(grep -hc 'hit by mine' "$OUT/server.out")"
echo "c_etMine pops: $(grep -hc 'c_etMine pop' "$OUT/server.out")"
echo "MINE_DESTROYED: $(grep -hc 'MINE_DESTROYED' "$OUT/server.out")"
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
