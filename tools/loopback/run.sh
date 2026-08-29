#!/usr/bin/env bash
# Two-process loopback handshake: launches host and client as SEPARATE OS
# processes over a real loopback TCP socket, captures both transcripts, prints
# them tagged. Proves the transport across a true process boundary, not just
# within one test process.
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build -c Release -v quiet >/dev/null

BIN=bin/Release/net10.0/loopback
PLAYER="${1:-BOT-ONE}"
WORK="$(mktemp -d)"
trap 'kill ${HPID:-} 2>/dev/null || true; rm -rf "$WORK"' EXIT

# host: prints its ephemeral port as the FIRST stdout line
"$BIN" host >"$WORK/host.out" 2>"$WORK/host.err" &
HPID=$!

# wait for the port line, then launch the client as a second process
for _ in $(seq 1 100); do
  PORT="$(sed -n '1p' "$WORK/host.out" 2>/dev/null || true)"
  [[ "$PORT" =~ ^[0-9]+$ ]] && break
  sleep 0.05
done
[[ "$PORT" =~ ^[0-9]+$ ]] || { echo "host never announced a port:"; cat "$WORK/host.err"; exit 1; }

echo "=== host process ($HPID) bound port $PORT ==="
"$BIN" client "$PORT" "$PLAYER"        # client output already tagged [client]
echo "==============================================="
cat "$WORK/host.out"                   # host output already tagged [host]
