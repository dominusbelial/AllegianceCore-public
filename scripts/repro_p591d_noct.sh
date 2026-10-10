#!/usr/bin/env bash
# P591d — server+MCP-only live proof (no client window). Verifies placement
# alephs + export audience + mine hits in the server log.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
PORT=6076
OUT="$REPO/p591d-noct"
rm -rf "$OUT"; mkdir -p "$OUT"
SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"
cat > "$OUT/server.json" <<EOF
{
  "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT,
  "updatesPerSecond": 20, "consoleVerbose": true,
  "games": [ { "name": "P591d Showcase", "map": "m2xspiderw.igc", "teams": 2, "civIds": [ 25, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
  "liveSettings": {
    "testUnlock": { "enabled": true, "allTech": true, "money": 1000000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*", "proof*" ] },
    "debugMode": true
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
sleep 40
printf '%s\n' '{"tool":"quit"}' >> "$CMD"; sleep 2
kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; kill "$MCP" 2>/dev/null
echo "=== minefield deploy lines ==="
grep -hE "\[mine\] ship .*deployed|\[layer\]|ordered BUILD" "$OUT/server.out" | grep -viE "began mining|is full" | tail -50
echo "=== export audience counts (deployed mines) ==="
grep -hoE "OT_mine [0-9]+ pos .* life [0-9]+ -> [0-9]+ same-side peer\(s\)" "$OUT/server.out" | tail -40
echo "=== c_etMine pops ==="
grep -hc "c_etMine pop" "$OUT/server.out"
echo "=== mine hit count ==="
grep -hc "hit by mine" "$OUT/server.out"
echo "done out=$OUT"
