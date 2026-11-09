#!/usr/bin/env bash
# P980 — keep the HUD golden compare mode honest.
#
# Runs `hud2-proof --self-test` against the committed 4-combination sample
# baseline (docs/reports/P980_assets/sample-baseline: Classic + Modern at
# 800x480 and 3840x2160). The self-test proves, in both directions, that
# `--compare` detects
#   1. an untouched baseline            -> 0 differences,
#   2. one hand-edited golden pixel     -> caught by the manifest hash,
#   3. one changed pixel, re-signed     -> "1 changed pixel(s)" + a diff image,
#   4. an edited semantic snapshot      -> caught by the snapshot hash/content diff,
#   5. the restored baseline            -> 0 differences again.
#
# Usage: tools/hud2-proof/golden-selftest.sh
#   DOTNET=...            dotnet host to use (default: ~/.dotnet/dotnet when present, else dotnet on PATH)
#   HUD2_OUT=<dir>        scratch output (default .scratch/p980/golden-selftest)
set -euo pipefail
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-$( [[ -x "$HOME/.dotnet/dotnet" ]] && echo "$HOME/.dotnet/dotnet" || command -v dotnet)}"
OUT="${HUD2_OUT:-.scratch/p980/golden-selftest}"
BASELINE=docs/reports/P980_assets/sample-baseline
[[ -f "$BASELINE/manifest.json" ]] || { echo "missing committed sample baseline $BASELINE" >&2; exit 2; }
"$DOTNET" build tools/hud2-proof/hud2-proof.csproj -v:quiet -nologo -c Debug
SDL_VIDEODRIVER="${SDL_VIDEODRIVER:-offscreen}" ALSOFT_DRIVERS="${ALSOFT_DRIVERS:-null}" \
  "$DOTNET" tools/hud2-proof/bin/Debug/net10.0/hud2-proof.dll --self-test "$BASELINE" "$OUT"
echo "P980_GOLDEN_SELFTEST_PASS"
