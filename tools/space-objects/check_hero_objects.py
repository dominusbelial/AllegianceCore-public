#!/usr/bin/env python3
"""
P589c — automated field-star check for the hero-object sprites (Sombrero,
Whirlpool, Ring, Pinwheel, Helix, Lions).

Domingo's rule: "we don't want photos with star backgrounds, but the natural
object itself, like a painting against the sky." A FIELD STAR is a compact
bright point (PSF-like, tiny/sharp/isolated, sometimes with diffraction spikes)
that sits OUTSIDE the object envelope on DARK local surroundings — a foreground
star in the sky around/behind the object, NOT one of the object's own H II
regions / clusters (which sit on the galaxy's bright emission and are inside
the envelope, kept unchanged).

The gate is STRICT (P589c): every kept hero object must have ZERO star-like
detections outside its envelope after processing. Any object that cannot reach
0 without damaging the object is DROPPED from the set (its sprite removed +
manifest/credits edited), never tolerated with a ceiling.

The envelope + detector are the SAME shared implementation process.py uses
(starclean.starclean), so processing and checking always agree. Usage:
    python3 check_hero_objects.py [--dir DIR]
Exit 0 only if every present object is at 0; 1 otherwise.
"""
import os, sys, argparse
import numpy as np
from PIL import Image
from scipy import ndimage
import starclean

DEFAULT_DIR = "/home/domingo/Development/Artwork/p589obj"
SLUGS = ["helix-nebula", "whirlpool-m51", "ring-nebula-m57", "sombrero-m104",
         "pinwheel-m101", "lion-nebula-miri", "lion-nebula-nasa"]


def count_field_stars(l, alpha, env, slug):
    m = starclean.field_star_mask(l, alpha, env, slug, include_alpha=True)
    lab, n = ndimage.label(m, structure=np.ones((3, 3), bool))
    return n, int(m.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=DEFAULT_DIR)
    args = ap.parse_args()

    # Dropped objects must be declared EXPLICITLY here (empty for P589c: all
    # seven reached 0). When a object can't be cleaned without damage it is
    # listed here and the caller removes its sprite + manifest/credits.
    DROPPED = []

    failed = []
    for slug in SLUGS:
        p = os.path.join(args.dir, slug + ".png")
        if not os.path.exists(p):
            if slug in DROPPED:
                print(f"  DROPPED {slug} (removed from the set)")
                continue
            print(f"MISSING {slug}")
            failed.append((slug, "missing"))
            continue
        im = np.asarray(Image.open(p).convert("RGBA")).astype(np.float32) / 255.0
        a = im[..., 3]
        l = im[..., :3] @ np.array([0.2126, 0.7152, 0.0722])
        env = starclean.envelope(l, slug)
        n, px = count_field_stars(l, a, env, slug)
        status = "OK" if n == 0 else "FAIL"
        print(f"  {status} {slug:16s} outside_field_stars={n} (px={px}, env_area={env.mean():.2f})")
        if n != 0:
            failed.append((slug, n))
    if failed:
        print(f"FAIL: {len(failed)} object(s) still have field stars outside their envelope")
        return 1
    print("OK: every hero object is clean (0 field stars outside its envelope)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
