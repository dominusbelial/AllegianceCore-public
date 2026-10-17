#!/usr/bin/env bash
# Run by Claude after review; requires a running server and a GL desktop/Xvfb.
set -euo pipefail
bundle_dir="$(cd -- "${1:?bundle directory required}" && pwd)"
server_host="${2:?server hostname required}"
server_port="${3:-6073}"
proof_dir="$(mktemp -d -t p644-live-XXXXXX)"
unset ALLEGIANCE_ARTWORK_PATH ALLEGIANCE_SKY_PACK_PATH
cd -- "$proof_dir"
"$bundle_dir/Allegiance" --capture "$server_host" "$server_port" P644Smoke 30 "$proof_dir/frames" \
  --sky hd --auto-side 0 --auto-ready --auto-launch --asset-manifest "$proof_dir/session.json" 2>&1 | tee "$proof_dir/client.log"
if rg -i 'asset.*unavailable|asset not found|unlisted artwork|absent from artwork-manifest|sky.*(missing|unavailable)' "$proof_dir/client.log"; then
  printf 'Asset failure; inspect %s\n' "$proof_dir" >&2
  exit 1
fi
printf 'Inspect connection, frames and sky layers in %s\n' "$proof_dir"
