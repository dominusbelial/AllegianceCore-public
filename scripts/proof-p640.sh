#!/usr/bin/env bash
# P640: real presented-window proof in an isolated temporary worktree.
# Run outside the socket-restricted Codex lane; creates no commits or pushes.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-"$repo/docs/proofs/p640"}
mkdir -p "$out"
out=$(cd "$out" && pwd)
proof_root=$(mktemp -d /tmp/allegiance-p640-XXXXXX)
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
# Content outputs are ignored; restore/build a real desktop head in the worktree.
dotnet restore "$proof_tree/AllegianceCore.slnx" -m:1
dotnet build "$proof_tree/AllegianceCore.slnx" --no-restore -m:1
# 15 real combat bots on each side rendezvous in Omega; the player observes.
bots='spawn_scenario {"size":15,"target_sector":"Omega","arena":true}'
for camera in command commandtd dronefollow dronecockpit; do
    "$proof_tree/scripts/live-proof.sh" --out "$out/$camera" --port 6074 \
        --autoshots '12,20,28' --exit-after 32 --bots "$bots" \
        --client-extra "--camera $camera"
done
"$proof_tree/scripts/live-proof.sh" --out "$out/highlight" --port 6074 \
    --autoshots '8,12,16' --exit-after 20 --client-extra '--camera command --auto-highlight current'
# Fail visibly if a window did not present the requested camera/received mark.
for camera in command commandtd dronefollow dronecockpit; do
    compgen -G "$out/$camera/*_c$camera.png" >/dev/null || { echo "Missing real $camera frame" >&2; exit 1; }
done
compgen -G "$out/highlight/*.png" >/dev/null || { echo 'Missing real highlight frame' >&2; exit 1; }
rg -q 'received HIGHLIGHT_CLUSTER' "$out/highlight/client.log"
echo "P640 presented-window frames: $out"
