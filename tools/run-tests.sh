#!/usr/bin/env bash
# TT-1: shared workstation, mini-runner and CI entry point.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -f local.env ]]; then
  set -a
  # shellcheck disable=SC1091
  source ./local.env
  set +a
fi
exec python3 tools/test-tiers/runner.py "$@"
