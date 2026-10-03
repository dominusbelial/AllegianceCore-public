#!/usr/bin/env bash
#
# P499 — package the Android head's assets into src/Allegiance.ClientCore.Android/Assets/.
#
# Two inputs, both already in the repo / machine:
#   1. The P498 artwork manifest (docs/reports/P498_ARTWORK_MANIFEST.json) — the
#      exact ~206 Artwork files the client actually opens. They are copied from
#      the manifest's artworkRoot into Assets/artwork/ (POSIX relative paths
#      preserved), where the AndroidAssetSource reads them at runtime.
#   2. The DesktopGL head's COMPILED MGCB Content (the .xnb models/textures/
#      fonts the shared library loads via Content.Load, Content.RootDirectory =
#      "Content"). Copied into Assets/Content/ so MonoGame.Android's
#      ContentManager finds them in the APK's assets.
#
# The generated Assets/artwork and Assets/Content are GITIGNORED (the source of
# truth is the manifest + the MGCB content project); only the packer script,
# the manifest and Assets/connect.json are committed. Run before
# scripts/build-android-head.sh (or let that script invoke this first).
#
# Usage:  scripts/pack-android-assets.sh
# Exit:   0 on success.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
MANIFEST="$REPO/docs/reports/P498_ARTWORK_MANIFEST.json"
CONTENT_DIR="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content"
ASSETS_DIR="$REPO/src/Allegiance.ClientCore.Android/Assets"

[ -f "$MANIFEST" ] || { echo "manifest missing: $MANIFEST"; exit 1; }
[ -d "$CONTENT_DIR" ] || { echo "compiled Content missing; build the DesktopGL head first: $CONTENT_DIR"; exit 1; }

python3 - "$MANIFEST" "$ASSETS_DIR" <<'PY'
import json, os, shutil, sys
manifest_path, assets_dir = sys.argv[1], sys.argv[2]
with open(manifest_path) as f:
    m = json.load(f)
root = m["artworkRoot"]
dst = os.path.join(assets_dir, "artwork")
copied = 0
total = 0
for e in m["files"]:
    rel = e["path"]
    src = os.path.join(root, *rel.split("/"))
    out = os.path.join(dst, *rel.split("/"))
    if os.path.isfile(src):
        os.makedirs(os.path.dirname(out), exist_ok=True)
        shutil.copyfile(src, out)
        copied += 1
        total += os.path.getsize(out)
print(f"artwork: {copied} files, {total:,} bytes -> {dst}")
PY

# Compiled MGCB Content -> Assets/Content/
if [ -d "$CONTENT_DIR" ]; then
  rm -rf "$ASSETS_DIR/Content"
  cp -r "$CONTENT_DIR" "$ASSETS_DIR/Content"
  echo "content: $(find "$ASSETS_DIR/Content" -type f | wc -l) files, $(du -sb "$ASSETS_DIR/Content" | cut -f1) bytes -> $ASSETS_DIR/Content"
fi

echo "done. Assets staged under $ASSETS_DIR (artwork + Content + connect.json)."
