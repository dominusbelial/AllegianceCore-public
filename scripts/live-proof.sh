#!/usr/bin/env bash
#
# P572 — one-command real-window live proof.
#
# Boots a LOCAL Allegiance.Server (separate process, the 221 battle map
# m2xspiderw.igc, testUnlock + optional adminCheats), OPTIONALLY starts the
# MCP spawn-and-forget bot server and spawns a scenario, launches the WINDOWED
# MonoGame client with the --auto-* automation flags (which seat it, ready it,
# fly it and take REAL 2560x1440 backbuffer screenshots via SaveAsPng), waits,
# collects the PNG paths, and shuts everything down cleanly by exact PID.
#
# The client renders into a real on-screen window whose BACKBUFFER is the full
# requested size (--window WxH), so the screenshots are real presented frames,
# not --capture/offscreen renders (the P547/P562 regression class).
#
# Usage:
#   scripts/live-proof.sh [options]
#
# Options:
#   --out <dir>              screenshot output dir (default ./live-proof-<ts>)
#   --port <port>            server port (default 6073)
#   --map <map.igc>          server map (default m2xspiderw.igc)
#   --art <path>             artwork root (default /home/domingo/Development/Artwork)
#   --auto-side <id>         client side to auto-select (default 0)
#   --autoshots <t,...>      after-launch screenshot seconds (default "8,12,20")
#   --exit-after <s>         client exit deadline after launch (default 25)
#   --window <WxH>           client window size (default 2560x1440)
#   --name <name>            client player name (default proof)
#   --chase                  initial chase camera (default cockpit)
#   --hide-hud               hide the HUD overlay for a clean frame
#   --bots "<tool> <json>"   spawn bots via MCP before launching the client,
#                            e.g. 'spawn_scenario {"size":5,"target_sector":"Alderan"}'
#   --no-admin-cheats        do not enable adminCheats in the server config
#   --server-config <file>   use an existing server config instead of generating one
#   --no-window              (debug) skip the client, only boot the server + bots
#   --client-extra "<flags>" extra client flags passthrough
#                            (e.g. '--auto-goto Alderan --auto-target enemy')
#
# Prints the absolute path of every real PNG it produced.
set -uo pipefail

DOTNET="${DOTNET:-/home/domingo/.dotnet/dotnet}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ART="/home/domingo/Development/Artwork"

PORT=6073
MAP="m2xspiderw.igc"
OUT_DIR=""
AUTO_SIDE=0
AUTOSHOTS="8,12,20"
EXIT_AFTER=25
WIN="2560x1440"
NAME="proof"
CAMERA="cockpit"
HIDE_HUD=0
BOTS=""
ADMIN_CHEATS=1
SERVER_CONFIG=""
NO_WINDOW=0
CLIENT_EXTRA=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --out) OUT_DIR="$2"; shift 2;;
    --port) PORT="$2"; shift 2;;
    --map) MAP="$2"; shift 2;;
    --art) ART="$2"; shift 2;;
    --auto-side) AUTO_SIDE="$2"; shift 2;;
    --autoshots) AUTOSHOTS="$2"; shift 2;;
    --exit-after) EXIT_AFTER="$2"; shift 2;;
    --window) WIN="$2"; shift 2;;
    --name) NAME="$2"; shift 2;;
    --chase) CAMERA="chase"; shift;;
    --hide-hud) HIDE_HUD=1; shift;;
    --bots) BOTS="$2"; shift 2;;
    --no-admin-cheats) ADMIN_CHEATS=0; shift;;
    --server-config) SERVER_CONFIG="$2"; shift 2;;
    --no-window) NO_WINDOW=1; shift;;
    --client-extra) CLIENT_EXTRA="$2"; shift 2;;
    *) echo "unknown option: $1"; exit 2;;
  esac
done

if [ -z "$OUT_DIR" ]; then OUT_DIR="$REPO/live-proof-$(date +%Y%m%d-%H%M%S)"; fi
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

echo "[live-proof] === P572 real-window live proof ==="
echo "[live-proof] repo=$REPO art=$ART port=$PORT map=$MAP out=$OUT_DIR"

# ── build (Release) so we run the real apphost executables with stable PIDs ──
echo "[live-proof] building (Release)..."
(cd "$REPO" && "$DOTNET" build -c Release -m:8 -v:m) || { echo "[live-proof] BUILD FAILED"; exit 1; }

SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
DRIVER="$REPO/scripts/mcp_driver.py"

PIDS=()          # exact PIDs to shut down cleanly
cleanup() {
  echo "[live-proof] shutting down..."
  for pid in "${PIDS[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then kill "$pid" 2>/dev/null; fi
  done
  sleep 1
  for pid in "${PIDS[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then kill -9 "$pid" 2>/dev/null; fi
  done
}
trap cleanup EXIT INT TERM

# ── server config ────────────────────────────────────────────────────────────
SERVER_CFG="$SERVER_CONFIG"
if [ -z "$SERVER_CFG" ]; then
  SERVER_CFG="$OUT_DIR/server.json"
  CHEATS='"adminCheats": { "enabled": true, "allowNames": [ "mcp-*", "proof*" ] },'
  [ "$ADMIN_CHEATS" -eq 0 ] && CHEATS=""
  cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [
    { "name": "P572 Live Proof", "map": "$MAP", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] }
  ],
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    $CHEATS
    "debugMode": true
  }
}
EOF
  echo "[live-proof] wrote server config $SERVER_CFG"
fi

# ── start the server ─────────────────────────────────────────────────────────
echo "[live-proof] starting server on :$PORT ..."
"$SERVER_EXE" --config "$SERVER_CFG" > "$OUT_DIR/server.out" 2>&1 &
SRV_PID=$!
PIDS+=("$SRV_PID")

# wait for the port to accept connections
for i in $(seq 1 60); do
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then exec 3>&- 3<&-; break; fi
  if ! kill -0 "$SRV_PID" 2>/dev/null; then echo "[live-proof] server died early — see $OUT_DIR/server.out"; exit 1; fi
  sleep 1
done
echo "[live-proof] server listening on 127.0.0.1:$PORT (pid $SRV_PID)"

# ── optional MCP bot scenario ────────────────────────────────────────────────
if [ -n "$BOTS" ]; then
  tool="${BOTS%% *}"
  args="${BOTS#* }"
  [ -z "$args" ] && args="{}"
  echo "[live-proof] spawning bots via MCP: $tool $args"
  CMD_FILE="$OUT_DIR/mcp_cmds.jsonl"
  MC_OUT="$OUT_DIR/mcp_out.log"
  MC_ERR="$OUT_DIR/mcp_err.log"
  : > "$CMD_FILE"
  : > "$MC_OUT"
  python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$MC_ERR" >> "$MC_OUT" 2>&1 &
  MCP_PID=$!
  PIDS+=("$MCP_PID")
  sleep 3
  printf '%s\n' "{\"tool\": \"$tool\", \"args\": $args}" >> "$CMD_FILE"
  # wait for the RESULT line mentioning the tool (up to 180 s)
  found=0
  for i in $(seq 1 180); do
    if grep -q "RESULT $tool " "$MC_OUT" 2>/dev/null; then found=1; break; fi
    sleep 1
  done
  if [ "$found" -eq 1 ]; then echo "[live-proof] bots spawned ($(grep -c 'RESULT ' "$MC_OUT") result(s))"; else echo "[live-proof] WARNING: no MCP result seen (see $MC_OUT)"; fi
fi

if [ "$NO_WINDOW" -eq 1 ]; then echo "[live-proof] --no-window: server+bots only; sleeping 20s to observe"; sleep 20; exit 0; fi

# ── windowed client with automation flags ────────────────────────────────────
CLIENT_FLAGS=(--auto-side "$AUTO_SIDE" --auto-ready --autoshots "$AUTOSHOTS"
              --autoshot-dir "$OUT_DIR" --exit-after "$EXIT_AFTER"
              --camera "$CAMERA" --window "$WIN")
[ "$HIDE_HUD" -eq 1 ] && CLIENT_FLAGS+=(--hide-hud)
read -r -a EXTRA <<< "$CLIENT_EXTRA"          # split --client-extra into words

echo "[live-proof] launching windowed client: ${CLIENT_FLAGS[*]} (exit after ${EXIT_AFTER}s)"
export ALLEGIANCE_ARTWORK_PATH="$ART"
export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 "$PORT" "$NAME" "${CLIENT_FLAGS[@]}" "${EXTRA[@]}" > "$OUT_DIR/client.out" 2>&1 &
CLIENT_PID=$!
PIDS+=("$CLIENT_PID")
echo "[live-proof] client pid $CLIENT_PID (logs: $OUT_DIR/client.out)"

# wait for the client to exit (its --exit-after deadline) or an outer timeout
MAX=$(awk -v n="$EXIT_AFTER" 'BEGIN{print int(n)+40}')
for i in $(seq 1 "$MAX"); do
  if ! kill -0 "$CLIENT_PID" 2>/dev/null; then break; fi
  sleep 1
done
if kill -0 "$CLIENT_PID" 2>/dev/null; then echo "[live-proof] client still running after ${MAX}s — killing"; kill "$CLIENT_PID" 2>/dev/null; fi

# ── stop MCP bots (despawns them) and report ─────────────────────────────────
if [ -n "$BOTS" ]; then
  printf '%s\n' '{"tool": "quit"}' >> "$CMD_FILE"
  sleep 2
fi

echo
echo "[live-proof] === produced real-window PNGs ($OUT_DIR) ==="
find "$OUT_DIR" -maxdepth 1 -name '*.png' -printf '%f\t%s bytes\n' | sort
echo
echo "[live-proof] absolute PNG paths:"
find "$OUT_DIR" -maxdepth 1 -name '*.png' -printf '%p\n' | sort
echo "[live-proof] done."
