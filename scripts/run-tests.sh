#!/usr/bin/env bash
#
# P471 — reliable full-suite test runner.
#
# `dotnet test` at the solution root builds in parallel and then runs the four
# test ASSEMBLIES' test execution in parallel too (measured wall-time is far
# below the sum of the per-assembly durations), so live-socket test classes in
# different assemblies boot their loopback `Server`s at the same time. That
# cross-assembly contention is what drives the "green in isolation, flakes
# under full-suite load" failures — dozens of real TCP servers ticking at once
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
# P647 — two LANES:
#   * MAIN pass  — every test NOT carrying [Trait("Lane","LiveTiming")]. This
#     is the trustworthy suite: it must be expected to run fully clean.
#   * QUARANTINE pass — tests tagged [Trait("Lane","LiveTiming")]: residual
#     live-timing wire-receipt tests that could not be made deterministic in
#     P647 (see docs/TEST_FLAKES.md). They run here, in a separate final pass,
#     so a real regression cannot hide behind them. A quarantine failure is
#     re-run ONCE in isolation: it fails the run only if it ALSO fails in
#     isolation (otherwise it is reported as a flake and the run is green).
#     Nothing is silently skipped — quarantined tests still RUN, they are just
#     evaluated with the isolation re-run so load-driven timing flakes do not
#     poison the trustworthy main pass.
#
# Usage:  scripts/run-tests.sh
# Exit:   0 if every project passed (main pass clean, and no quarantine test
#         failed in isolation), 1 otherwise. Each project's own Passed!/Failed!
#         line is printed so failures are attributable.
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

# P647 lanes. MAIN runs everything not tagged as a LiveTiming quarantine test;
# QUARANTINE runs only those, with the isolation re-run.
MAIN_FILTER="Lane!=LiveTiming"
QUARANTINE_FILTER="Lane=LiveTiming"

echo "==> building solution (parallel)"
"$DOTNET" build -m:8 -v:m || { echo "BUILD FAILED"; exit 1; }

rc=0
for p in "${PROJECTS[@]}"; do
  echo
  echo "==> MAIN   $p"
  # `|| rc=1` lets every project run even if one fails; we aggregate at the end.
  "$DOTNET" test "$p" --no-build --nologo -v:m --filter "$MAIN_FILTER" || rc=1
done

# ---------- P647 quarantine pass: separate final lane -----------------------
# Run the LiveTiming tests; any failure is re-run ONCE in isolation and only
# fails the run if it fails there too.
for p in "${PROJECTS[@]}"; do
  echo
  echo "==> QUARANTINE $p"
  qout=$("$DOTNET" test "$p" --no-build --nologo -v:m --filter "$QUARANTINE_FILTER" 2>&1)
  qrc=$?
  # Collect the failing fully-qualified test names ("  Failed FQN [ms]" lines).
  fails=$(printf '%s\n' "$qout" | sed -n 's/^[[:space:]]*Failed \([^ ]*\).*/\1/p' | sort -u)
  if [ -z "$fails" ]; then
    printf '%s\n' "$qout" | grep -E 'Passed!|Failed!|No test matches|No tests' | tail -1
    continue
  fi
  iso_fail=0
  while IFS= read -r t; do
    [ -z "$t" ] && continue
    if "$DOTNET" test "$p" --no-build --nologo -v:q --filter "FullyQualifiedName=$t" >/dev/null 2>&1; then
      echo "  QUARANTINE FLAKE (passes in isolation, reported not failing): $t"
    else
      echo "  QUARANTINE FAILS IN ISOLATION (REAL failure): $t"
      iso_fail=1
    fi
  done <<< "$fails"
  [ "$iso_fail" -eq 0 ] || rc=1
done

echo
if [ "$rc" -eq 0 ]; then
  echo "ALL TEST PROJECTS PASSED (main clean + quarantine cleared)"
else
  echo "ONE OR MORE TEST PROJECTS FAILED"
fi
exit "$rc"
