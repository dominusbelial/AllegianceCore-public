#!/usr/bin/env bash
# P641: real presented-window proof in an isolated temporary worktree.
# Run outside the socket-restricted Codex lane; creates no commits or pushes.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-"$repo/docs/proofs/p641"}
mkdir -p "$out"
out=$(cd "$out" && pwd)
proof_root=$(mktemp -d /tmp/allegiance-p641-XXXXXX)
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
# Exercise actual player senders; wait for server echo before re-equipping.
"$proof_tree/scripts/live-proof.sh" --out "$out/parts" --port 6074 \
    --autoshots '2,4,8,11,15' --exit-after 18 \
    --client-extra '--auto-launch 285 --auto-flight-parts'
compgen -G "$out/parts/*.png" >/dev/null || { echo 'Missing real weapon / mount frames' >&2; exit 1; }
rg -q 'weapon 1: SINGLE' "$out/parts/client.out"
rg -q '\[ctrlak\].*ACCEPTED' "$out/parts/server.out"
rg -q 'in-flight mount CONFIRMED by server echo' "$out/parts/client.out"
# P624 own-ship authority guard: a single transition reset may occur; a storm fails.
resets=$(rg -c '\[sim\] SHIP_RESET #' "$out/parts/client.out" || true)
if [ "${resets:-0}" -gt 2 ]; then echo "SHIP_RESET storm: $resets" >&2; exit 1; fi
# Simulation changed: the mandatory 30v30 march regression asserts reach + kills.
ALLEGIANCE_LIVE_P632_ASSERT=1 ALLEGIANCE_P632_TAG=p641 dotnet test "$proof_tree/tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj" \
    --no-build --no-restore --filter 'FullyQualifiedName~P632PortModeZeroKillsLiveTests.Assert_port_bots_kill_in_window' \
    --logger "trx;LogFileName=p641-botcheck.trx" --results-directory "$out/botcheck"
python3 - "${TMPDIR:-/tmp}/p632_p641.txt" <<'CHECK'
import re, sys
text = open(sys.argv[1]).read()
result = re.search(r"RESULT botsReached=(\d+)/(\d+).*killsTotal=(\d+)", text)
assert result and int(result[1]) > 0 and int(result[3]) > 0, "Bots must reach target AND kill"
CHECK
cp "${TMPDIR:-/tmp}/p632_p641.txt" "$out/botcheck/trace.txt"
echo "P641 presented-window frames and live logs: $out"
