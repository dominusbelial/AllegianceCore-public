#!/usr/bin/env bash
# P539 — run the BEFORE baseline (PRE-fix combat code) via the server-log driver.
# Usage: ./before_baseline.sh [ups] [seconds]
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1   # scripts/ -> repo root
ART="${ALLEGIANCE_ARTWORK_PATH:-$(pwd)/../Artwork}"
UPS="${1:-20}"
SECS="${2:-240}"
TD=tests/Allegiance.ClientCore.Tests
STASH=/tmp/p539_newapi_stash
mkdir -p "$STASH"

echo "==> hiding new-API test files (they reference post-fix symbols)"
mv "$TD/P539CombatDecisionTests.cs" "$STASH/" 2>/dev/null || true
mv "$TD/P539CombatProofLiveTests.cs" "$STASH/" 2>/dev/null || true

echo "==> stashing source (BotManager/McpTools/Catalog)"
SAVED=""
if git stash push -- tools/allegiance-mcp/BotManager.cs tools/allegiance-mcp/McpTools.cs tools/allegiance-mcp/Catalog.cs; then
  SAVED=1
else
  echo "!! stash push failed — aborting before run without restoring (run fixup manually)"
  RC=99
  exit $RC
fi

echo "==> building + running BEFORE log baseline (ups=$UPS secs=$SECS)"
ALLEGIANCE_ARTWORK_PATH="$ART" \
ALLEGIANCE_LIVE_P539_BASELINE=1 \
ALLEGIANCE_LIVE_P539_SECONDS="$SECS" \
"${DOTNET:-dotnet}" test "$TD/Allegiance.ClientCore.Tests.csproj" \
  --filter "FullyQualifiedName~P539LogBaselineLiveTests.Baseline_${UPS}Hz" \
  --nologo > /tmp/p539_before_${UPS}.log 2>&1
RC=$?

if [ -n "$SAVED" ]; then
  echo "==> restoring source"
  git stash pop || echo "!! git stash pop FAILED — manual recovery needed"
fi

echo "==> restoring new-API test files"
mv "$STASH/P539CombatDecisionTests.cs" "$TD/" 2>/dev/null || true
mv "$STASH/P539CombatProofLiveTests.cs" "$TD/" 2>/dev/null || true

echo "==> BEFORE ($UPS Hz) RC=$RC"
grep -E "P539-LOG-BASELINE|Failed!|Passed!" /tmp/p539_before_${UPS}.log | tail -5
exit $RC
