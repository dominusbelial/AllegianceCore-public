#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT"
if [[ -f local.env ]]; then set -a; source local.env; set +a; fi
DOTNET=${DOTNET:-"$HOME/.dotnet/dotnet"}
SCRATCH="$ROOT/.scratch/p987"
PROJECT=tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj
BUILD_FLAGS=(--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false)
mkdir -p "$SCRATCH"

"$DOTNET" build "$PROJECT" "${BUILD_FLAGS[@]}"
for teams in 2 3; do
  raw="$SCRATCH/raw-${teams}side"
  rm -rf "$raw"
  mkdir -p "$raw" "$SCRATCH/record-${teams}side"
  export UPDATE_CORPUS=1
  export ALLEGIANCE_BOT_RECORD_DECISIONS=1
  export ALLEGIANCE_BOT_RECORD_PATH="$raw"
  export ALLEGIANCE_BOT_RECORD_SCENARIO="seeded-${teams}side"
  timeout 480s "$DOTNET" test "$PROJECT" --no-build --no-restore "${BUILD_FLAGS[@]}" \
    --settings tools/test-tiers/sequential.runsettings \
    --filter "FullyQualifiedName~P987DecisionRecordingLiveTests&DisplayName~teams: ${teams}" \
    --logger "trx;LogFileName=record-${teams}side.trx" \
    --results-directory "$SCRATCH/record-${teams}side" \
    >"$SCRATCH/record-${teams}side.log" 2>&1
  unset ALLEGIANCE_BOT_RECORD_DECISIONS ALLEGIANCE_BOT_RECORD_PATH ALLEGIANCE_BOT_RECORD_SCENARIO
done

export P987_RAW_PATHS="$SCRATCH/raw-2side;$SCRATCH/raw-3side"
timeout 180s "$DOTNET" test "$PROJECT" --no-build --no-restore "${BUILD_FLAGS[@]}" \
  --settings tools/test-tiers/sequential.runsettings \
  --filter "FullyQualifiedName~P987RecordedBotDecisionCorpusTests.Recorded_inputs_replay_to_the_same_outputs" \
  --logger "trx;LogFileName=regenerate.trx" --results-directory "$SCRATCH" \
  >"$SCRATCH/regenerate.log" 2>&1
unset UPDATE_CORPUS P987_RAW_PATHS

timeout 180s "$DOTNET" test "$PROJECT" --no-build --no-restore "${BUILD_FLAGS[@]}" \
  --settings tools/test-tiers/sequential.runsettings \
  --filter "FullyQualifiedName~P987RecordedBotDecisionCorpusTests" \
  --logger "trx;LogFileName=replay.trx" --results-directory "$SCRATCH" \
  >"$SCRATCH/replay.log" 2>&1
printf 'P987 corpus regenerated and replayed. See %s\n' "$SCRATCH/replay.log"
