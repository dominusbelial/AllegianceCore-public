#!/usr/bin/env bash
set -euo pipefail
bundle_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
config_dir="${XDG_CONFIG_HOME:-$HOME/.config}/AllegianceCore"
mkdir -p -- "$config_dir"
# Read by PlayerConfig; never sourced/executed as shell code.
if [[ ! -f "$config_dir/player.json" ]]; then
  cat > "$config_dir/player.json" <<'JSON'
{"Server":"","Port":6073,"Pilot":"pilot","Lobby":"http://127.0.0.1:5000"}
JSON
  printf 'Edit %s to remember your server and pilot. Launch without arguments to choose Single Player or Multiplayer.\n' "$config_dir/player.json"
fi
cd -- "$bundle_dir"
exec ./Allegiance "$@"
