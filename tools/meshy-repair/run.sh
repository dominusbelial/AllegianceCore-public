#!/usr/bin/env bash
#
# P581 — Meshy ship repair pipeline wrapper (Adv Scout pilot pass).
# Deterministic Blender-headless drivers for analyze / repair / render on a
# single Meshy ship model. Runs on dreg-scout2 by default (P581 pilot scope —
# the other hulls are P581b).
#
# Usage:
#   ./run.sh analyze  [model]      welded audit -> <OUT>/audit/<model>.json
#   ./run.sh repair   [model]      snap detached pieces onto the hull, drop
#                                  floating crumbs, export to
#                                  Content/Models/repaired/<model>.fbx (originals
#                                  never touched)
#   ./run.sh render   [model]      colored per-piece 4-view renders (before or
#                                  after; point MODEL at the file you want)
#
# All three operate on the private Meshy pack's Content/Models/<model>.fbx by
# default (P601 — Meshy assets no longer live in the public repo). The pack root
# comes from ALLEGIANCE_MESHY_PACK_PATH (or local.env); falls back to the
# conventional local pack location.
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"
PACK="${ALLEGIANCE_MESHY_PACK_PATH:-}"
if [ -z "$PACK" ] || [ ! -d "$PACK" ]; then
  PACK="$HOME/Development/AllegianceCore-meshy-pack"
fi
MODELS="$PACK/Content/Models"
OUT="$PACK/screenshots/meshy-repair-output"
CMD="${1:-help}"; MODEL="${2:-dreg-scout2}"
SRC="$MODELS/$MODEL.fbx"
mkdir -p "$OUT/audit" "$OUT/renders"

case "$CMD" in
  analyze)
    blender --background --factory-startup --python tools/meshy-repair/analyze.py \
      -- "$OUT/audit/${MODEL}_before.json" "$SRC"
    ;;
  repair)
    mkdir -p "$MODELS/repaired"
    blender --background --factory-startup --python tools/meshy-repair/repair.py \
      -- "$SRC" "$MODELS/repaired/$MODEL.fbx"
    blender --background --factory-startup --python tools/meshy-repair/analyze.py \
      -- "$OUT/audit/${MODEL}_after.json" "$MODELS/repaired/$MODEL.fbx"
    ;;
  render)
    blender --background --factory-startup --python tools/meshy-repair/render.py \
      -- "$SRC" "$OUT/renders/${MODEL}.png"
    ;;
  *) echo "usage: $0 analyze|repair|render [model]"; exit 1;;
esac
