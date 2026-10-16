#!/usr/bin/env bash
set -euo pipefail
bundle_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
applications="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p -- "$applications"
# Desktop Entry Exec uses its own escaping rules, including literal percent.
exec_path="$bundle_dir/allegiance.sh"
exec_path="${exec_path//\\/\\\\\\\\}"
exec_path="${exec_path//\"/\\\\\\\"}"
exec_path="${exec_path//\$/\\\\\$}"
exec_path="${exec_path//\`/\\\\\`}"
exec_path="${exec_path//%/%%}"
{
  printf '[Desktop Entry]\nType=Application\nName=AllegianceCore\nComment=Allegiance space combat client\n'
  printf 'Exec="%s"\nIcon=%s/allegiance.svg\nTerminal=false\nCategories=Game;ActionGame;\n' "$exec_path" "$bundle_dir"
} > "$applications/allegiance.desktop"
printf 'Installed %s/allegiance.desktop\n' "$applications"
