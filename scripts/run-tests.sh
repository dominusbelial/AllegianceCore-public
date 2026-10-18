#!/usr/bin/env bash
# Compatibility entry point; queued runs and local callers now use TT-1 tiers.
set -euo pipefail
exec "$(dirname "$0")/../tools/run-tests.sh" "$@"
