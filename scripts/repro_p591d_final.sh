#!/usr/bin/env bash
# P591d — final careful live proof: wait for the invest to COMPLETE (a real
# completed=True with a drone), then spawn the 15v15 (which lays both fields at
# the enemy arrival alephs), then WAIT for a field to deploy and the enemy fleet
# to cross it. Capture real window + mine hits/pops.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
PORT=6082
OUT="$REPO/p591d-final"
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

# Invest side 1 and WAIT for a completed drone (up to 160s).
echo "== invest side 1 (wait for completion) =="
feed '{"tool":"invest","args":{"side":1,"bucket":".Caltrop","observe_seconds":160}}'
S1=0
for i in $(seq 1 160); do
  if grep -q '"Completed":true' "$MCO"; then S1=1; echo "side1 invest completed (~${i}s)"; break; fi
  sleep 1
done
[ "$S1" -eq 1 ] || echo "side1 invest did not report completed in 160s"

# Invest side 0 too.
echo "== invest side 0 (wait for completion) =="
feed '{"tool":"invest","args":{"side":0,"bucket":".Caltrop","observe_seconds":160}}'
S0=0
COMP0=$(( $(grep -c '"Completed":true' "$MCO") ))
for i in $(seq 1 160); do
  if [ "$(grep -c '"Completed":true' "$MCO")" -ge $((COMP0+1)) ]; then S0=1; echo "side0 invest completed (~${i}s)"; break; fi
  sleep 1
done
[ "$S0" -eq 1 ] || echo "side0 invest did not report a new completed in 160s"

echo "== spawn 15v15 + minefields both =="
feed '{"tool":"spawn_scenario","args":{"preset":"showcase","size_scale":1,"target_sector":"Alderan","minefields":{"side":"both","count":1}}}'
sleep 5
grep -o '"minefields":\[[^]]*\]' "$MCO" | tail -c 1300

echo "== WAIT for a field to physically deploy + enemy to cross (polling 150s) =="
DEP=0; HITS=0
for i in $(seq 1 150); do
  DEP=$(grep -c "deployed -> EXPORT(26)/OT_mine" "$OUT/server.out" 2>/dev/null)
  HITS=$(grep -c "hit by mine" "$OUT/server.out" 2>/dev/null)
  if [ "$HITS" -ge 1 ]; then echo "MINE HIT observed at ~${i}s"; break; fi
  sleep 1
done
echo "deployed fields: $DEP, hits: $HITS"

echo "== real-window capture =="
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "proof" --auto-side 0 --auto-ready \
  --autoshots "8,16,24,32,40,48" --autoshot-dir "$OUT" --exit-after 50 \
  --camera chase --window 2560x1440 --auto-goto Alderan > "$OUT/client.out" 2>&1 &
CLIENT=$!
echo "client pid $CLIENT"
for i in $(seq 1 100); do if ! kill -0 "$CLIENT" 2>/dev/null; then break; fi; sleep 1; done
kill "$CLIENT" 2>/dev/null
feed '{"tool":"quit"}'; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null

echo
echo "=== deploy + audience ==="
grep -hoE "\[mine\] ship [0-9]+ deployed -> EXPORT\(26\)/OT_mine [0-9]+ pos Vector3 \{ X = [-0-9.]+, Y = [-0-9.]+, Z = [-0-9.]+ \} life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out"
echo "=== ordered BUILD ==="
grep -hE "ordered BUILD" "$OUT/server.out" | tail -6
echo "=== hits/pops ==="
echo "hit by mine: $(grep -hc 'hit by mine' "$OUT/server.out")"
echo "c_etMine pops: $(grep -hc 'c_etMine pop' "$OUT/server.out")"
echo "MINE_STRENGTH: $(grep -hc 'MINE_STRENGTH' "$OUT/server.out")"
echo "=== enemy crossings ==="
grep -hE "crossed from .* to .Alderan" "$OUT/server.out" | tail -12
echo "=== PNGs ==="
find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo "done out=$OUT"
