#!/usr/bin/env bash
# Rebuild the browser generation that the public host serves, from committed traces.
# Usage: BROWSER_CLIENT_VERSION=<release> scripts/publish-browser-generation.sh OUTPUT_DIR
# Needs ALLEGIANCE_ARTWORK_PATH (or local.env) and a Debug build of the MonoGame client
# (its Content/ folder). OUTPUT_DIR must not exist. No hosting or deployment changes.
set -euo pipefail
cd "$(dirname "$0")/.."
(( $# == 1 )) || { echo "Usage: BROWSER_CLIENT_VERSION=<release> $0 OUTPUT_DIR" >&2; exit 2; }
: "${BROWSER_CLIENT_VERSION:?set BROWSER_CLIENT_VERSION to the server minimumClientVersion or later}"
if [[ -z "${ALLEGIANCE_ARTWORK_PATH:-}" && -f local.env ]]; then
  ALLEGIANCE_ARTWORK_PATH=$(grep '^ALLEGIANCE_ARTWORK_PATH=' local.env | cut -d= -f2-); export ALLEGIANCE_ARTWORK_PATH
fi
T=tools/browser-traces/flight-2026-11
exec scripts/publish-browser-flight.sh "$1" "$T/opens-Classic.json" "$T/opens-Modern.json" \
  "$T/flight-opens3-Classic.json" "$T/flight-opens3-Modern.json" "$T/flight-opens4-Classic.json" "$T/flight-opens4-Modern.json" \
  "$T/flight-opens-enemy-Modern.json" "$T/flight-miners0-Classic.json" "$T/flight-miners1-Modern.json"
