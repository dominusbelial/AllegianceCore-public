#!/usr/bin/env bash
#
# P1063 — small loopback battle (at most 10 v 10) for the browser/desktop gap audit.
#
# Boots a local Allegiance.Server on XXV_simple.igc / PCore001.igc (the content the browser
# build qualifies), launches ONE desktop client into it (real window; offscreen SDL + software
# GL by default) with the --auto-* proof flags, then spawns N bots per side through the
# allegiance-mcp driver, waits for the client to exit and stops everything by exact PID.
# The pilot takes one seat on side 0, so side 0 gets (N-1) bots and side 1 gets N: the battle
# is N v N including the pilot. Loopback only. Nothing is built here:
#
#   export NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1063/nuget-http
#   for p in src/Allegiance.Server tools/allegiance-mcp src/Allegiance.ClientCore.MonoGame; do
#     dotnet build -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false $p
#   done
#
# Usage: tools/p1063-battle.sh --out <dir> [--size N<=10] [--style Classic|Modern]
#          [--window WxH] [--autoshots t1,t2,...] [--exit-after S] [--port P]
#          [--bots-after S] [--scenario '<spawn_scenario json>'] [--client-extra "<flags>"]
#          [--env "K=V K=V"]
set -uo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
if [ -f "$REPO/local.env" ]; then set -a; source "$REPO/local.env"; set +a; fi   # artwork path
ART="${ALLEGIANCE_ARTWORK_PATH:?set ALLEGIANCE_ARTWORK_PATH (local.env)}"
OUT=""; SIZE=5; STYLE=Modern; WIN=1920x1080; SHOTS="30,60,90"; EXIT_AFTER=100; PORT=6163
BOTS_AFTER=8; SCENARIO=""; EXTRA=""; ENVS=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --out) OUT="$2";; --size) SIZE="$2";; --style) STYLE="$2";; --window) WIN="$2";;
    --autoshots) SHOTS="$2";; --exit-after) EXIT_AFTER="$2";; --port) PORT="$2";;
    --bots-after) BOTS_AFTER="$2";; --scenario) SCENARIO="$2";; --client-extra) EXTRA="$2";;
    --env) ENVS="$2";;
    *) echo "unknown option: $1"; exit 2;;
  esac; shift 2
done
[ -n "$OUT" ] || { echo "--out is required"; exit 2; }
[ "$SIZE" -ge 1 ] && [ "$SIZE" -le 10 ] || { echo "--size must be 1..10 (the audit's cap)"; exit 2; }
mkdir -p "$OUT/xdg"; OUT="$(cd "$OUT" && pwd)"
SERVER="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
for exe in "$SERVER" "$CLIENT" "$MCP"; do [ -x "$exe" ] || { echo "missing $exe — build first (see header)"; exit 1; }; done

PIDS=()
cleanup() {
  for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill "$pid" 2>/dev/null; done
  sleep 1
  for pid in "${PIDS[@]}"; do kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null; done
}
trap cleanup EXIT INT TERM

cat > "$OUT/server.json" <<EOF
{
  "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT, "bindAddress": "127.0.0.1", "updatesPerSecond": 20,
  "discovery": { "enabled": false },
  "games": [ { "name": "P1063 battle", "map": "XXV_simple.igc", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*", "proof*" ] },
    "debugMode": true
  }
}
EOF
echo "{\"Style\":\"$STYLE\"}" > "$OUT/hud.json"

"$SERVER" --config "$OUT/server.json" > "$OUT/server.out" 2>&1 &
SRV=$!; PIDS+=("$SRV")
for _ in $(seq 1 60); do
  (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && { exec 3>&- 3<&-; break; }
  kill -0 "$SRV" 2>/dev/null || { echo "server died — see $OUT/server.out"; exit 1; }
  sleep 1
done
echo "[p1063] server pid $SRV on 127.0.0.1:$PORT"

read -r -a EXTRA_ARR <<< "$EXTRA"
read -r -a ENV_ARR <<< "$ENVS"
env XDG_CONFIG_HOME="$OUT/xdg" ALLEGIANCE_ARTWORK_PATH="$ART" ALLEGIANCE_HUD_SETTINGS="$OUT/hud.json" \
    MONOGAME_GL_PROFILE=OpenGL SDL_VIDEODRIVER="${SDL_VIDEODRIVER:-offscreen}" LIBGL_ALWAYS_SOFTWARE=1 \
    ALSOFT_DRIVERS="${ALSOFT_DRIVERS:-null}" "${ENV_ARR[@]}" \
  "$CLIENT" 127.0.0.1 "$PORT" proof1063 --auto-side 0 --auto-ready --autoshots "$SHOTS" --autoshot-dir "$OUT" \
    --exit-after "$EXIT_AFTER" --window "$WIN" "${EXTRA_ARR[@]}" > "$OUT/client.out" 2>&1 &
CLI=$!; PIDS+=("$CLI")
echo "[p1063] client pid $CLI"

sleep "$BOTS_AFTER"
: > "$OUT/mcp_cmds.jsonl"; : > "$OUT/mcp_out.log"
python3 "$REPO/scripts/mcp_driver.py" "$MCP" 127.0.0.1 "$PORT" "$OUT/mcp_cmds.jsonl" "$OUT/mcp_out.log" "$OUT/mcp_err.log" >> "$OUT/mcp_out.log" 2>&1 &
DRV=$!; PIDS+=("$DRV")
sleep 3
if [ -z "$SCENARIO" ]; then
  if [ "$SIZE" -gt 1 ]; then SCENARIO="{\"sides\":[{\"count\":$((SIZE-1))},{\"count\":$SIZE}]}"
  else SCENARIO="{\"sides\":[{\"count\":1},{\"count\":1}]}"; fi   # 1 v 1 keeps one friendly bot (2 v 1 with the pilot)
fi
echo "[p1063] spawn_scenario $SCENARIO"
printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": $SCENARIO}" >> "$OUT/mcp_cmds.jsonl"

MAX=$(( ${EXIT_AFTER%.*} + 180 ))
for _ in $(seq 1 "$MAX"); do kill -0 "$CLI" 2>/dev/null || break; sleep 1; done
printf '%s\n' '{"tool": "quit"}' >> "$OUT/mcp_cmds.jsonl"; sleep 3
echo "[p1063] PNGs:"; find "$OUT" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
