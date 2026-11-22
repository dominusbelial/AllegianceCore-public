#!/usr/bin/env bash
# P1072 A/B entry: ordinary P1071 backfill fleet, ten total seats per side.
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
if (( $# < 1 )); then echo 'usage: bots-spawn-load.sh SERVER [PORT] [CLIENT_VERSION] [transport options]' >&2; exit 2; fi
bot_target=$1; shift
bot_positionals=()
while (( $# )) && [[ $1 != --* ]]; do bot_positionals+=("$1"); shift; done
exec bash "$script_dir/bots-spawn-perpetual.sh" "$bot_target" "${bot_positionals[@]}" --seats 10 "$@"
