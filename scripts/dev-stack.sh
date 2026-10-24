#!/usr/bin/env bash
#
# P811 — one-command local stack: server + lobby + optional MCP bots + optional
# client.
#
#   scripts/dev-stack.sh up    [options]   bring the stack up
#   scripts/dev-stack.sh down               tear the stack down (by exact PID)
#   scripts/dev-stack.sh status             show what is running
#
# By default the stack runs as LOCAL processes (the server and lobby as real
# Release binaries, bots via the MCP driver, the client as the MonoGame app) —
# guaranteed to work with the real artwork corpus and to let a bot actually
# join. When Docker Compose is available you can instead run the server+lobby
# as containers with `--docker` (it reuses the existing docker-compose.yml).
#
# Process bookkeeping lives in .scratch/dev-stack/ INSIDE the repo (git-excluded,
# see .git/info/exclude) so `down` kills exactly the PIDs `up` started — never a
# broad pkill/pgrep pattern — and multiple stacks can coexist via --name.
#
# Usage:
#   scripts/dev-stack.sh up \
#       [--name <tag>]          stack state file suffix (default: default)
#       [--local] [--docker]    run server+lobby as processes (default) or
#                               containers (docker compose up --build -d)
#       [--port <port>]         server port (default 6073)
#       [--map <map.igc>]       server map file (default m2xspiderw.igc)
#       [--map-gen <TYPE>]      use a procedurally generated map of this
#                               topology (e.g. Brawl, HiHigher) instead of a
#                               file map
#       [--map-seed <N>]        fix the generated map's RNG seed (reproducible)
#       [--bots <N>]            spawn N bots per side via the MCP driver
#       [--client]              also launch the MonoGame client
#       [--art <path>]          artwork root (default: $ALLEGIANCE_ARTWORK_PATH)
#       [--config <file>]       use an existing server config instead of
#                               generating one
#       [--no-build]            skip the Release build (use existing binaries)
#
# Smoke (the P811 light gate): `up --local --bots 1` then confirm at least one
# bot joined, then `down`. Example:
#   scripts/dev-stack.sh up --local --map-gen Brawl --map-seed 812377 --bots 1
#   scripts/dev-stack.sh status
#   scripts/dev-stack.sh down
set -uo pipefail

DOTNET="${DOTNET:-$(command -v dotnet)}"
[ -z "$DOTNET" ] && DOTNET="$HOME/.dotnet/dotnet"
REPO="$(cd "$(dirname "$0")/.." && pwd)"

# Local-only external path overrides (see local.env.example) — e.g. the
# owner's artwork corpus. Keeps concrete /home/... paths out of the committed
# script; honoured identically by tools/run-tests.sh.
if [ -f "$REPO/local.env" ]; then
  set -a
  # shellcheck disable=SC1091
  source "$REPO/local.env"
  set +a
fi
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"

NAME=default
MODE=local
PORT=6073
MAP="m2xspiderw.igc"
MAP_GEN=""
MAP_SEED=""
BOTS=0
CLIENT=0
SERVER_CONFIG=""
NO_BUILD=0

usage() { sed -n '2,50p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

[ $# -lt 1 ] && usage 2
CMD="$1"; shift

while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME="$2"; shift 2;;
    --local) MODE=local; shift;;
    --docker) MODE=docker; shift;;
    --port) PORT="$2"; shift 2;;
    --map) MAP="$2"; shift 2;;
    --map-gen) MAP_GEN="$2"; shift 2;;
    --map-seed) MAP_SEED="$2"; shift 2;;
    --bots) BOTS="$2"; shift 2;;
    --client) CLIENT=1; shift;;
    --art) ART="$2"; shift 2;;
    --config) SERVER_CONFIG="$2"; shift 2;;
    --no-build) NO_BUILD=1; shift;;
    *) echo "unknown option: $1"; usage 2;;
  esac
done

STATE_DIR="$REPO/.scratch/dev-stack"
mkdir -p "$STATE_DIR"
ST="$STATE_DIR/$NAME.state"      # PIDs + metadata
LOG_DIR="$STATE_DIR/$NAME.logs"
mkdir -p "$LOG_DIR"

SERVER_EXE="$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server"
LOBBY_EXE="$REPO/src/Allegiance.Lobby/bin/Release/net10.0/Allegiance.Lobby"
MCP_EXE="$REPO/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp"
CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
DRIVER="$REPO/scripts/mcp_driver.py"

save_pid() { # save_pid <component> <pid>
  grep -v "^$1 " "$ST" > "$ST.tmp" 2>/dev/null || true
  mv "$ST.tmp" "$ST" 2>/dev/null || true
  echo "$1 $2" >> "$ST"
}
save_kv() { # save_kv <key> <value>
  grep -v "^$1 " "$ST" > "$ST.tmp" 2>/dev/null || true
  mv "$ST.tmp" "$ST" 2>/dev/null || true
  echo "$1 $2" >> "$ST"
}
read_pid() { awk -v k="$1" '$1==k{print $2}' "$ST" 2>/dev/null | head -1; }
read_kv()  { awk -v k="$1" '$1==k{print $2}' "$ST" 2>/dev/null | head -1; }

kill_pid() { # kill_pid <pid>
  [ -n "$1" ] || return 0
  if kill -0 "$1" 2>/dev/null; then kill "$1" 2>/dev/null; fi
}
force_kill() {
  for pid in "$@"; do
    if kill -0 "$pid" 2>/dev/null; then kill -9 "$pid" 2>/dev/null; fi
  done
}

wait_port() { # wait_port <port> <desc> <pidfile-component>
  local p="$1" desc="$2" comp="$3"
  for _ in $(seq 1 60); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$p") 2>/dev/null; then exec 3>&- 3<&-; return 0; fi
    local pid
    pid="$(read_pid "$comp")"
    if [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; then
      echo "[dev-stack] $desc died before listening — see $LOG_DIR"; return 1
    fi
    sleep 1
  done
  echo "[dev-stack] timeout waiting for $desc on :$p"; return 1
}

do_up_local() {
  if [ "$NO_BUILD" -eq 0 ]; then
    echo "[dev-stack] building (Release)..."
    "$DOTNET" build -c Release -m:8 -v:m "$REPO/src/Allegiance.Server/Allegiance.Server.csproj" \
      > "$LOG_DIR/build-server.log" 2>&1 || { echo "[dev-stack] server build failed"; exit 1; }
    "$DOTNET" build -c Release -m:8 -v:m "$REPO/src/Allegiance.Lobby/Allegiance.Lobby.csproj" \
      > "$LOG_DIR/build-lobby.log" 2>&1 || { echo "[dev-stack] lobby build failed"; exit 1; }
    if [ "$BOTS" -gt 0 ]; then
      "$DOTNET" build -c Release -m:8 -v:m "$REPO/tools/allegiance-mcp/allegiance-mcp.csproj" \
        > "$LOG_DIR/build-mcp.log" 2>&1 || { echo "[dev-stack] mcp build failed"; exit 1; }
    fi
    if [ "$CLIENT" -eq 1 ]; then
      "$DOTNET" build -c Release -m:8 -v:m "$REPO/src/Allegiance.ClientCore.MonoGame/Allegiance.ClientCore.MonoGame.csproj" \
        > "$LOG_DIR/build-client.log" 2>&1 || { echo "[dev-stack] client build failed"; exit 1; }
    fi
  fi

  # Server config: generate unless an existing one was supplied.
  if [ -z "$SERVER_CONFIG" ]; then
    SERVER_CONFIG="$LOG_DIR/server.json"
    local game_block
    if [ -n "$MAP_GEN" ]; then
      local seed_json=""
      [ -n "$MAP_SEED" ] && seed_json="\"seed\": $MAP_SEED,"
      game_block="{ \"name\": \"Dev Stack\", \"mapGen\": { \"type\": \"$MAP_GEN\", $seed_json }, \"teams\": 2, \"civIds\": [ 1, 3 ], \"sideNames\": [ \"Alpha\", \"Bravo\" ] }"
    else
      game_block="{ \"name\": \"Dev Stack\", \"map\": \"$MAP\", \"teams\": 2, \"civIds\": [ 1, 3 ], \"sideNames\": [ \"Alpha\", \"Bravo\" ] }"
    fi
    cat > "$SERVER_CONFIG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [ $game_block ],
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "debugMode": true
  }
}
EOF
    echo "[dev-stack] wrote server config $SERVER_CONFIG"
  fi

  echo "[dev-stack] starting lobby on :8080 ..."
  ASPNETCORE_URLS="http://127.0.0.1:8080" "$LOBBY_EXE" > "$LOG_DIR/lobby.out" 2>&1 &
  save_pid LOBBY $!
  save_kv MODE local

  echo "[dev-stack] starting server on :$PORT ..."
  "$SERVER_EXE" --config "$SERVER_CONFIG" > "$LOG_DIR/server.out" 2>&1 &
  save_pid SERVER $!
  wait_port "$PORT" "server" SERVER || { do_down; exit 1; }
  echo "[dev-stack] server listening on 127.0.0.1:$PORT"

  if [ "$BOTS" -gt 0 ]; then
    CMD_FILE="$LOG_DIR/mcp_cmds.jsonl"
    MC_OUT="$LOG_DIR/mcp_out.log"
    MC_ERR="$LOG_DIR/mcp_err.log"
    : > "$CMD_FILE"
    python3 "$DRIVER" "$MCP_EXE" 127.0.0.1 "$PORT" "$CMD_FILE" "$MC_OUT" "$MC_ERR" >> "$MC_OUT" 2>&1 &
    save_pid MCP $!
    sleep 3
    printf '%s\n' "{\"tool\": \"spawn_scenario\", \"args\": {\"size\": $BOTS}}" >> "$CMD_FILE"
    echo "[dev-stack] requested spawn_scenario size=$BOTS (per side) via MCP"
  fi

  if [ "$CLIENT" -eq 1 ]; then
    echo "[dev-stack] launching client (--window 1280x720, spectator start)..."
    export ALLEGIANCE_ARTWORK_PATH="$ART"
    DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
      "$CLIENT_EXE" 127.0.0.1 "$PORT" "devstack" --auto-ready --window 1280x720 \
      > "$LOG_DIR/client.out" 2>&1 &
    save_pid CLIENT $!
  fi

  echo "[dev-stack] up (mode=local name=$NAME). state=$ST logs=$LOG_DIR"
  echo "[dev-stack] hint: scripts/dev-stack.sh status ; scripts/dev-stack.sh down"
}

do_up_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    echo "[dev-stack] docker not found — use --local for a process-based stack"; exit 2
  fi
  echo "[dev-stack] docker compose up --build -d (server + lobby)..."
  (cd "$REPO" && ALLEGIANCE_ARTWORK_PATH="$ART" docker compose up --build -d) \
    > "$LOG_DIR/compose-up.log" 2>&1 || { echo "[dev-stack] docker compose failed — see $LOG_DIR/compose-up.log"; exit 1; }
  save_kv MODE docker
  save_kv PORT "$PORT"
  echo "[dev-stack] up (mode=docker name=$NAME). logs=$LOG_DIR"
  echo "[dev-stack] NOTE: the container path uses the docker/artwork-placeholder mount;"
  echo "[dev-stack]       bind the real artwork corpus via a compose override for a playable map."
}

do_down() {
  for comp in CLIENT MCP SERVER LOBBY; do
    kill_pid "$(read_pid "$comp")"
  done
  sleep 1
  force_kill "$(read_pid CLIENT)" "$(read_pid MCP)" "$(read_pid SERVER)" "$(read_pid LOBBY)"
  if [ "$(read_kv MODE)" = docker ]; then
    echo "[dev-stack] docker compose down..."
    (cd "$REPO" && docker compose down) > "$LOG_DIR/compose-down.log" 2>&1 || true
  fi
  rm -f "$ST"
  echo "[dev-stack] down"
}

do_status() {
  if [ ! -f "$ST" ]; then
    echo "[dev-stack] name=$NAME: not running (no state file)."
    exit 0
  fi
  echo "[dev-stack] name=$NAME mode=$(read_kv MODE) port=$(read_kv PORT)"
  for comp in LOBBY SERVER MCP CLIENT; do
    local pid
    pid="$(read_pid "$comp")"
    if [ -n "$pid" ]; then
      if kill -0 "$pid" 2>/dev/null; then echo "  $comp: RUNNING (pid $pid)"; else echo "  $comp: DEAD (was $pid)"; fi
    fi
  done
  if [ "$(read_kv MODE)" = docker ]; then
    echo "  docker compose ps:"
    (cd "$REPO" && docker compose ps 2>/dev/null || true)
  fi
}

case "$CMD" in
  up)
    if [ -f "$ST" ]; then echo "[dev-stack] name=$NAME already up (state=$ST); run 'down' first."; exit 3; fi
    if [ "$MODE" = docker ]; then do_up_docker; else do_up_local; fi
    ;;
  down) do_down;;
  status) do_status;;
  *) usage 2;;
esac
