#!/usr/bin/env bash
# Smoke-test the Explorer companion + enriched `who`, end to end against a real
# in-process server. Feeds the REPL via stdin with real wall-clock gaps (the
# outer printf | { ...sleep... ; printf... } grouping) so two `who` lines sample
# the live world several real seconds apart:
#   t~4s  who — two ships; the companion shows parts=[...] (a non-hull-only
#               loadout = P86 fix live in an interactive session), plus pos/dist.
#   t~8s  who — the companion's pos AND dist must differ from the t~4s sample
#               (live P85 movement, not a frozen snapshot).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
OUT=/tmp/explorer-smoke.out
{
    sleep 4
    printf 'who\n'
    sleep 4
    printf 'who\n'
    sleep 4
    printf 'quit\n'
} | dotnet run --project "$ROOT/src/Allegiance.ClientCore.Explorer" -- 0 smoke >"$OUT" 2>&1
RC=$?
echo "dotnet run exit=$RC  (output file: $OUT)"
# Show only the REPL-echoed `who` lines (skip the noisy server logs).
echo "=== `who` results ==="
grep -E '^\s+\[[0-9]+\]' "$OUT"
