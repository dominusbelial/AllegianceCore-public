#!/usr/bin/env bash
# Training is a Fast + Live gate: a Fast-only sweep misses full lesson completion.
set -euo pipefail
cd "$(dirname "$0")/.."
repeat=1
build=1
while (( $# )); do
  case "$1" in
    --no-build) build=0; shift ;;
    --repeat) repeat="${2:?--repeat requires a positive integer}"; shift 2 ;;
    *) echo "Usage: tools/run-training-tests.sh [--no-build] [--repeat N]" >&2; exit 2 ;;
  esac
done
[[ $repeat =~ ^[1-9][0-9]*$ ]] || { echo "--repeat requires a positive integer" >&2; exit 2; }
if [[ -f local.env ]]; then
  set -a
  # shellcheck disable=SC1091
  source ./local.env
  set +a
fi
if [[ -z ${DOTNET:-} ]]; then
  if [[ -x $HOME/.dotnet/dotnet ]]; then
    DOTNET="$HOME/.dotnet/dotnet"
  else
    DOTNET="$(command -v dotnet)" # actions/setup-dotnet on clean CI runners
  fi
fi
results="$PWD/.scratch/training-$(date -u +%Y%m%dT%H%M%S)-$$"
mkdir -p "$results/xdg"
export XDG_CONFIG_HOME="$results/xdg"
echo "Training evidence: $results"
if (( build )); then "$DOTNET" build AllegianceCore.slnx -v:minimal; fi
# All training classes, including TrainingHostMission2Tests and the framework
# binding class whose name has no Training token, plus training crash guards.
# Deliberately no Tier filter and no exclusion for the historical Nanite failure.
filter='FullyQualifiedName~Training|FullyQualifiedName~P880aFrameworkBindingTests|FullyQualifiedName~P749CrashSafetyTests.ModeWithMissingCoreReturnsToMenuWithMessage|FullyQualifiedName~P749CrashSafetyTests.BundleCheckCatchesDeliberatelyRemovedTrainingMap'
status=0
for (( run=1; run<=repeat; run++ )); do
  for project in Allegiance.ClientCore.Tests Allegiance.ClientCore.MonoGame.Tests; do
    directory="$results/$run/$project"
    mkdir -p "$directory"
    if "$DOTNET" test "tests/$project" --no-build --filter "$filter" \
      --settings tools/test-tiers/sequential.runsettings \
      --logger 'trx;LogFileName=training.trx' --results-directory "$directory" \
      > "$directory/output.log" 2>&1; then
      echo "Training run $run: $project PASS"
    else
      echo "Training run $run: $project FAIL"
      status=1
    fi
    cat "$directory/output.log"
  done
done
exit "$status"
