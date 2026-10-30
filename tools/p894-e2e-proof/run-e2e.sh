#!/usr/bin/env bash
# P894 end-to-end proof: real lobby process + real server process + the offscreen
# client proof tool (listing, joining, and an old client refused at logon).
#
#   ALLEGIANCE_ARTWORK_PATH=/path/to/Artwork tools/p894-e2e-proof/run-e2e.sh
#
# Requires the two apps and the tool to be built first:
#   dotnet build AllegianceCore.slnx
#   dotnet build tools/p894-e2e-proof/p894-e2e-proof.csproj
#
# Scratch lives under .scratch/p894 (git-excluded). Exact PIDs are used for
# teardown — never pattern-matched kills.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRATCH="$ROOT/.scratch/p894"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[[ -n "${ALLEGIANCE_ARTWORK_PATH:-}" ]] || { echo "set ALLEGIANCE_ARTWORK_PATH" >&2; exit 2; }
LOBBY_URL="${P894_LOBBY_URL:-http://127.0.0.1:5147}"
SERVER_PORT="${P894_SERVER_PORT:-26073}"
MINIMUM="${P894_MINIMUM_CLIENT_VERSION:-2.0.0}"
mkdir -p "$SCRATCH/lobby-cwd"
LOBBY_LOG="$SCRATCH/lobby.log"; SERVER_LOG="$SCRATCH/server.log"
: > "$LOBBY_LOG"; : > "$SERVER_LOG"

cat > "$SCRATCH/server.json" <<EOF
{
  "artPath": "${ALLEGIANCE_ARTWORK_PATH}",
  "staticCore": "PCore001.igc",
  "port": ${SERVER_PORT},
  "updatesPerSecond": 20,
  "minimumClientVersion": "${MINIMUM}",
  "games": [
    { "name": "P894 E2E", "map": "XXV_simple.igc", "teams": 2, "civIds": [1, 3], "sideNames": ["Alpha", "Bravo"] }
  ],
  "lobby": {
    "url": "${LOBBY_URL}",
    "displayName": "P894 E2E Server",
    "publicAddress": "127.0.0.1",
    "heartbeatIntervalSeconds": 5,
    "location": "Loopback Lab",
    "minimumClientVersion": "${MINIMUM}"
  }
}
EOF

cleanup() {
  [[ -n "${SERVER_PID:-}" ]] && kill "$SERVER_PID" 2>/dev/null
  [[ -n "${LOBBY_PID:-}" ]] && kill "$LOBBY_PID" 2>/dev/null
  wait "${SERVER_PID:-}" "${LOBBY_PID:-}" 2>/dev/null
}
trap cleanup EXIT

pushd "$SCRATCH/lobby-cwd" >/dev/null
ASPNETCORE_URLS="$LOBBY_URL" \
Lobby__CurrentClientVersion="$MINIMUM" \
Lobby__MinimumClientVersion="$MINIMUM" \
Lobby__UpdateFeedUrl="https://example.invalid/feed/" \
"$DOTNET" "$ROOT/src/Allegiance.Lobby/bin/Debug/net10.0/Allegiance.Lobby.dll" >>"$LOBBY_LOG" 2>&1 &
LOBBY_PID=$!
popd >/dev/null
echo "lobby pid $LOBBY_PID"

for _ in $(seq 1 60); do
  curl -sf "$LOBBY_URL/healthz" >/dev/null && break
  sleep 1
done

"$DOTNET" "$ROOT/src/Allegiance.Server/bin/Debug/net10.0/Allegiance.Server.dll" \
  --config "$SCRATCH/server.json" >>"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
echo "server pid $SERVER_PID"

for _ in $(seq 1 90); do
  curl -sf "$LOBBY_URL/api/servers" | grep -q '"id"' && break
  sleep 1
done

"$DOTNET" "$ROOT/tools/p894-e2e-proof/bin/Debug/net10.0/p894-e2e-proof.dll" \
  --lobby "$LOBBY_URL" --server "127.0.0.1:${SERVER_PORT}" --minimum "$MINIMUM" --old 0.0.1
STATUS=$?
echo "--- server log tail ---"; tail -15 "$SERVER_LOG"
exit $STATUS
