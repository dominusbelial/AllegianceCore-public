#!/usr/bin/env python3
"""P577 — produce an HD planet billboard texture for the sky.

Input is a real planet image (NASA public-domain, or CC BY 4.0 — see the
sky-hd.json manifest / licenses folder for the per-asset credit). The output is
a square RGBA PNG: the planet fitted to a disc with a transparent surround so
it reads as a sphere on the sky dome (the native planet is a billboarded
square; using a disc keeps the HD planet from showing box corners).

Heuristics:
  - Cover-fit the image to the square.
  - Estimate the planet disc radius from the brightest/outermost content (the
    luminance crosses a small threshold), masked to a circle, feathered edges.
  - Slightly saturate / retain native brightness so it pops on the sky.

Usage:
  make_hd_planet.py <planet_image> <outstem> [--size 1024]
"""

import argparse
import os

import numpy as np
from PIL import Image, ImageFilter

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("img")
    ap.add_argument("outstem")
    ap.add_argument("--size", type=int, default=1024)
    args = ap.parse_args()

    S = args.size
    im = Image.open(args.img).convert("RGB")
    tw, th = im.size
    # cover-fit square
    side = min(tw, th)
    im = im.crop(((tw - side)//2, (th - side)//2, (tw + side)//2, (th + side)//2))
    im = im.resize((S, S), Image.Resampling.LANCZOS)
    a = np.asarray(im).astype(np.float32) / 255.0
    lum = 0.2126*a[...,0]+0.7152*a[...,1]+0.0722*a[...,2]

    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    cx = cy = (S - 1) / 2
    r = np.sqrt((xx - cx)**2 + (yy - cy)**2)
    # planet disc radius: where content falls off (adaptive)
    # strong-content radius = max row/col where luminance above small threshold
    thr = max(0.02, float(np.percentile(lum, 70)) * 0.3)
    on = lum > thr
    ys, xs = np.nonzero(on)
    R = 1.02 * (max(np.abs(ys - cy).max(), np.abs(xs - cx).max()) + 0.5)
    R = min(R, S * 0.505)
    # smooth alpha falloff at the limb
    alpha = np.clip((R - r) / (max(2.0, R*0.03)), 0, 1)
    # gentle limb darkening for a spherical feel
    limb = np.clip((R - r) / (R + 1e-6), 0, 1)
    a *= (0.55 + 0.45 * limb[..., None])

    rgba = np.dstack([(np.clip(a,0,1)*255+0.5).astype(np.uint8),
                      (alpha*255+0.5).astype(np.uint8)])
    os.makedirs(os.path.dirname(args.outstem) or ".", exist_ok=True)
    Image.fromarray(rgba, "RGBA").save(args.outstem + ".png", "PNG")
    print(f"wrote {args.outstem}.png {S}x{S} (disc R={R:.0f})")


if __name__ == "__main__":
    main()
