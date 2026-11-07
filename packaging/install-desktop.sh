#!/usr/bin/env bash
set -euo pipefail
bundle_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
applications="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p -- "$applications"
# Desktop Entry Exec uses its own escaping rules, including literal percent.
# GIO probes the executable before expanding %%: env keeps a % or quote in
# the bundle path in the argument position, where field-code expansion is safe.
exec_path="$bundle_dir/allegiance.sh"
exec_path="${exec_path//\\/\\\\\\\\}"
quote_escape='\\\\"'
exec_path="${exec_path//\"/$quote_escape}"
exec_path="${exec_path//\$/\\\\\$}"
exec_path="${exec_path//\`/\\\\\`}"
exec_path="${exec_path//%/%%}"
{
  printf '[Desktop Entry]\nType=Application\nName=AllegianceCore\nComment=Allegiance space combat client\n'
  printf 'Exec=/usr/bin/env "%s" %%u\nIcon=%s/allegiance.svg\nTerminal=false\nCategories=Game;ActionGame;\nMimeType=x-scheme-handler/allegiance;\n' "$exec_path" "$bundle_dir"
} > "$applications/allegiance.desktop"
printf 'Installed %s/allegiance.desktop\n' "$applications"
if command -v xdg-mime >/dev/null 2>&1; then
  xdg-mime default allegiance.desktop x-scheme-handler/allegiance
fi
