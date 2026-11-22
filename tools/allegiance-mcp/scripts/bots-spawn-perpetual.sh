#!/usr/bin/env bash
# Run on the bot host. Explicitly acknowledge every non-loopback target.
set -euo pipefail
if (( $# < 1 )); then
  echo 'usage: bots-spawn-perpetual.sh SERVER [PORT] [CLIENT_VERSION] [--seats N] [--transport tcp|udp|ws] [--ws-url URL] [--ws-origin ORIGIN] [--ws-ca-file PEM] [--ws-fraction 0..1] [--i-mean-it-host HOST]' >&2
  exit 2
fi
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
bot_server=$1; shift
bot_port=6073
bot_version=()
if (( $# )) && [[ $1 != --* ]]; then bot_port=$1; shift; fi
if (( $# )) && [[ $1 != --* ]]; then bot_version=(--client-version "$1"); shift; fi
# shellcheck source=bots-transport-options.sh
source "$script_dir/bots-transport-options.sh"
dotnet_bin="${DOTNET:-$HOME/.dotnet/dotnet}"
exec "$dotnet_bin" "$script_dir/../bin/Release/net10.0/allegiance-mcp.dll" "$bot_server" "$bot_port" --backfill-seats "$bot_seats" "${bot_version[@]}" "${bot_extra[@]}"
