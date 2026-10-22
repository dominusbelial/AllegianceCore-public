#!/usr/bin/env bash
# Build every tools/*/*.csproj on its own — the same compile check ci.yml runs.
#
#   tools/build-tools.sh [--drop-bins]
#
# AllegianceCore.slnx references only a subset of tools/; the *-proof harnesses are
# standalone projects that rot silently against API changes (P602; font1c-proof broke
# on P773's KillBonusStreak removal and only ci.yml noticed). The light gate runs this
# after the solution build.
#
# --drop-bins (CI): delete a project's bin/ after it builds, unless the project is in
# the solution or its bin/ path is used under tests/ or .github/ (benchmark,
# hd2-proof, ...). Each proof's bin/ is ~340 MB; all 70 filled the runner's disk
# (ci run 39719003509).
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
drop=0; [[ ${1:-} == --drop-bins ]] && drop=1
failed=()
for f in tools/*/*.csproj; do
  echo "Building $f"
  if ! "$DOTNET" build "$f" -v:quiet -nologo; then failed+=("$f"); continue; fi
  d=$(dirname "$f")
  if [[ $drop == 1 ]] && ! grep -qF "\"$f\"" AllegianceCore.slnx && ! grep -rqF "$d/bin" tests .github; then
    rm -rf "$d/bin"
  fi
done
if [[ $drop == 1 ]]; then df -h /; fi
if (( ${#failed[@]} )); then
  printf 'build-tools: %d project(s) failed:\n' "${#failed[@]}" >&2
  printf '  %s\n' "${failed[@]}" >&2
  exit 1
fi
echo "build-tools: all $(ls tools/*/*.csproj | wc -l) tool projects built"
