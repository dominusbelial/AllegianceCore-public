#!/usr/bin/env python3
"""
P589b — automated alpha-invariant check for the deep-field galaxy sprite library.

Validates that EVERY sprite in a galaxy cut-out library is a FILLED object on
black — the rule Domingo stated for P589 ("object only, no edges"):

  1. alpha at the tile border is ~0   (no frame edge / box survives)
  2. alpha at the brightest object pixel is ~1  (the galaxy is filled, not a
     hollow ring)
  3. the alpha mass sits away from the border (centre is not transparent)

On the PRE-FIX library this FAILS: the inverted feather mask gave the galaxy
core alpha ~0 while the box margin around it held alpha up to 1 (the
hollow-square / frame-edge sprites). After the build_galaxy_library.py fix the
library passes.

Usage:
    python3 check_galaxy_library.py [--dir DIR] [--target N]
  --dir     sprite directory (default: artwork p589gal, or the arg)
  --target  if supplied, quit with non-zero unless at least N sprites pass
Exit code 0 if every sprite passes; 1 if any fail; 2 on bad arguments.
"""
import os, sys, argparse
import numpy as np
from PIL import Image

DEFAULT_DIR = "/home/domingo/Development/Artwork/p589gal"


def check_alpha_invariants(an, border_tol=0.025, bright_tol=0.55, max_tol=0.795):
    a = an[..., 3]
    h, w = a.shape
    border = np.concatenate([a[0, :], a[-1, :], a[:, 0], a[:, -1]])
    border_mean = float(border.mean())
    if border_mean > border_tol:
        return False, f"border_alpha={border_mean:.3f}"
    lum = 0.2126 * an[..., 0] + 0.7152 * an[..., 1] + 0.0722 * an[..., 2]
    pk = int(np.argmax(lum))
    pk_a = float(a[pk // w, pk % w])
    if pk_a < bright_tol:
        return False, f"bright_pixel_alpha={pk_a:.3f}"
    if float(a.max()) < max_tol:
        return False, f"max_alpha={float(a.max()):.3f}"
    tot = a.sum()
    if tot <= 0:
        return False, "empty_alpha"
    cy = float((np.arange(h)[:, None] * a).sum() / tot)
    cx = float((np.arange(w)[None, :] * a).sum() / tot)
    if min(cy, h - 1 - cy) < 0.045 * h or min(cx, w - 1 - cx) < 0.045 * w:
        return False, f"mass_centroid({cx:.0f},{cy:.0f})"
    return True, ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=DEFAULT_DIR)
    ap.add_argument("--target", type=int, default=None)
    args = ap.parse_args()

    files = sorted(f for f in os.listdir(args.dir) if f.lower().endswith(".png"))
    if not files:
        print(f"check_galaxy_library: no pngs in {args.dir}")
        return 2
    passed = []
    failures = []
    for f in files:
        im = np.asarray(Image.open(os.path.join(args.dir, f)).convert("RGBA")).astype(np.float32) / 255.0
        ok, why = check_alpha_invariants(im)
        if ok:
            passed.append(f)
        else:
            failures.append((f, why))
    print(f"checked {len(files)} sprites: {len(passed)} pass, {len(failures)} fail")
    for f, why in failures[:40]:
        print(f"  FAIL {f}: {why}")
    if failures:
        print(f"...and {max(0, len(failures) - 40)} more failures" if len(failures) > 40 else "")
        return 1
    if args.target is not None and len(passed) < args.target:
        print(f"below target {args.target} passing sprites")
        return 1
    print("OK: every sprite is a filled object on black (border alpha ~0, brightest-pixel alpha ~1)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
