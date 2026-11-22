#!/usr/bin/env bash
# Run on the external bot host, never on the public game server.
# Usage: bots-spawn-perpetual.sh SERVER [PORT] [CLIENT_VERSION]
set -euo pipefail
if [[ $# -lt 1 || $# -gt 3 ]]; then
  echo 'usage: bots-spawn-perpetual.sh SERVER [PORT] [CLIENT_VERSION]' >&2
  exit 2
fi
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
dotnet_bin="${DOTNET:-$HOME/.dotnet/dotnet}"
extra=()
if [[ -n ${3:-} ]]; then extra+=(--client-version "$3"); fi
exec "$dotnet_bin" "$script_dir/../bin/Release/net10.0/allegiance-mcp.dll" "$1" "${2:-6073}" --backfill-seats 5 "${extra[@]}"
