#!/usr/bin/env bash
#
# P471 — reliable full-suite test runner.
#
# `dotnet test` at the solution root builds in parallel and then runs the four
# test ASSEMBLIES' test execution in parallel too (measured wall-time is far
# below the sum of the per-assembly durations), so live-socket test classes in
# different assemblies boot their loopback `Server`s at the same time. That
# cross-assembly contention is what drives the "green in isolation, flakes
# under full-suite load" failures (e.g. LiveMoneyTests, LiveWinCondition
# Board_refused, LiveTeamLobby) — dozens of real TCP servers ticking at once
# on shared cores.
#
# This runner builds once (in parallel) and then executes the four test
# assemblies SEQUENTIALLY, so only one assembly's live network ticks at a
# time. Within each assembly the existing xunit `LiveNetwork` collection
# (DisableParallelization = true) already serializes the live-socket classes
# against the unit-test collections, and unit tests inside an assembly still
# run fully in parallel (no maxParallelThreads cap), so pure unit-test
# throughput is preserved.
#
# Usage:  scripts/run-tests.sh
# Exit:   0 if every project passed, 1 otherwise (each project's own
#         Passed!/Failed! line is printed so failures are attributable).
#
# Loads the gitignored per-machine local.env (repo root) if present so the
# test processes resolve the artwork corpus via ALLEGIANCE_ARTWORK_PATH /
# ALLEGIANCE_NATIVE_SRC_PATH (see docs/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md).
set -uo pipefail

# Portable dotnet (override with DOTNET=/path/to/dotnet).
DOTNET="${DOTNET:-$(command -v dotnet)}"
cd "$(dirname "$0")/.."

# Source the gitignored local config if it exists (keys are the env-var names).
if [ -f local.env ]; then
  # shellcheck disable=SC1091
  set -a; . ./local.env; set +a
fi

PROJECTS=(
  tests/Allegiance.Lobby.Tests/Allegiance.Lobby.Tests.csproj
  tests/Allegiance.ClientCore.MonoGame.Tests/Allegiance.ClientCore.MonoGame.Tests.csproj
  tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj
  tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj
)

echo "==> building solution (parallel)"
"$DOTNET" build -m:8 -v:m || { echo "BUILD FAILED"; exit 1; }

rc=0
for p in "${PROJECTS[@]}"; do
  echo
  echo "==> $p"
  # `|| rc=1` lets every project run even if one fails; we aggregate at the end.
  "$DOTNET" test "$p" --no-build --nologo -v:m || rc=1
done

echo
if [ "$rc" -eq 0 ]; then
  echo "ALL TEST PROJECTS PASSED"
else
  echo "ONE OR MORE TEST PROJECTS FAILED"
fi
exit "$rc"
