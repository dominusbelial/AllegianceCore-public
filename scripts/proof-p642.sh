#!/usr/bin/env bash
# P642: real presented-window proof in an isolated temporary worktree.
# Run outside the socket-restricted Codex lane; creates no commits or pushes.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-"$repo/docs/proofs/p642"}
mkdir -p "$out"
out=$(cd "$out" && pwd)
proof_root=$(mktemp -d /tmp/allegiance-p642-XXXXXX)
proof_tree="$proof_root/tree"
cleanup() { git -C "$repo" worktree remove --force "$proof_tree" >/dev/null 2>&1 || true; rm -rf "$proof_root"; }
trap cleanup EXIT
# Include uncommitted source changes so this also proves Codex's working tree.
git -C "$repo" diff --binary HEAD > "$proof_root/changes.patch"
git -C "$repo" worktree add --detach "$proof_tree" HEAD
if [ -s "$proof_root/changes.patch" ]; then git -C "$proof_tree" apply "$proof_root/changes.patch"; fi
while IFS= read -r -d '' file; do
    mkdir -p "$proof_tree/$(dirname "$file")"
    cp "$repo/$file" "$proof_tree/$file"
done < <(git -C "$repo" ls-files --others --exclude-standard -z -- src tests scripts docs/parity)
if [ -f "$repo/local.env" ]; then cp "$repo/local.env" "$proof_tree/local.env"; fi
export ALLEGIANCE_KEYMAP="$proof_tree/keymap.json"
# Content outputs are ignored; restore/build a real desktop head in the worktree.
dotnet restore "$proof_tree/AllegianceCore.slnx" -m:1
dotnet build "$proof_tree/AllegianceCore.slnx" --no-restore -m:1
# Production warp target handler, then Inventory/GameState/MiniTeam/Team switches.
"$proof_tree/scripts/live-proof.sh" --out "$out/commands" --port 6076 \
    --autoshots '5,8,12,16,20,24,28,32,36,40,44' --exit-after 47 \
    --client-extra '--auto-parity-commands'
compgen -G "$out/commands/*.png" >/dev/null || { echo 'Missing presented-window frames' >&2; exit 1; }
rg -q '\[p642\] TargetWarp/Nearest ->' "$out/commands/client.log"
for mode in ConModeInventory ConModeGameState ConModeMiniTeam TeamPane ConModeCombat ViewExternal ViewCommand; do
    rg -q "console $mode:" "$out/commands/client.log"
done
compgen -G "$out/commands/*_cexternal.png" >/dev/null || { echo 'Missing external camera frame' >&2; exit 1; }
compgen -G "$out/commands/*_ccommand.png" >/dev/null || { echo 'Missing command grid frames' >&2; exit 1; }
rg -q 'external zoom=' "$out/commands/client.log"
rg -q 'command grid=False' "$out/commands/client.log"
rg -q 'main menu=True' "$out/commands/client.log"
frames=$(find "$out/commands" -name '*.png' | wc -l)
[ "$frames" -ge 11 ] || { echo 'Expected eleven presented frames' >&2; exit 1; }
echo "P642 presented-window frames: $out/commands"
