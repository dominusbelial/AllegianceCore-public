#!/usr/bin/env python3
"""P577 — bake the NASA SVS Deep Star Maps 2020 (starmap_2020_8k.exr) into a
compressed, tonemapped skybox cubemap (6 face PNGs) for the HD sky tier.

Source: NASA/Goddard Space Flight Center Scientific Visualization Studio,
"Deep Star Maps 2020" (dataset 4851), celestial (ICRF/J2000) plate carree,
8192x4096 EXR. Public domain (NASA). Credited in the sky-hd.json manifest /
licenses folder. Licence page: https://svs.gsfc.nasa.gov/4851/

Pipeline (faithful per P577 scope item 1):
  1. Decode the EXR float channels into numpy.
  2. Max-pool down to a fixed working resolution (--pool; default 2 -> 4096x2048)
     so sparse star points survive downscaling without bilinear dilution.
  3. Enhance stars IN THE WORKING MAP (local maxima grown to a 3x3 bright dot
     and boosted) so every resolution bake sees them consistently — sparse
     1px stars that land between bilinear sample cells would otherwise pop
     in and out with the cube-face resolution.
  4. Tonemap the whole working map with a LUT (keeps empty sky near-black,
     shows the galactic plane as a soft glow, punches stars toward white).
  5. Equirect -> cubemap: bilinear-sample the enhanced, tonemapped working map
     for the six faces at `size` px/face (desktop 2048, Android 1024).
  6. Write each face as a lossless PNG plus a labelled contact sheet.

Usage:
  make_hd_stars.py <starmap_2020_8k.exr> <outdir> [--size N] [--pool F]
                   [--punch F] [--star-floor F]
"""

import argparse
import math
import os

import numpy as np
from PIL import Image, ImageDraw

try:
    import OpenEXR
    import Imath
except ImportError:  # pragma: no cover
    raise SystemExit("need OpenEXR + Imath (pip install OpenEXR)")

FACE_NAMES = ["px", "nx", "py", "ny", "pz", "nz"]

# Source-value -> display-value LUT anchors. Tuned for the starmap_2020_8k
# distribution after 2x max-pool (working map 4096x2048; lum percentiles:
# 50th 0.004, 90th 0.018, 99th 0.075, 99.9th 0.63, ~100th 1.0): empty sky
# near-black, galactic plane a soft glow, stars bright. Shared by both tiers.
DEFAULT_LUT_X = np.array([0.0, 0.004, 0.018, 0.042, 0.085, 0.17, 0.34, 0.66, 1.0])
DEFAULT_LUT_Y = np.array([0.0, 0.015, 0.058, 0.175, 0.350, 0.590, 0.815, 0.962, 1.0])


def load_exr_rgb(path):
    exr = OpenEXR.InputFile(path)
    dw = exr.header()["dataWindow"]
    W = dw.max.x - dw.min.x + 1
    H = dw.max.y - dw.min.y + 1
    pt = Imath.PixelType(Imath.PixelType.FLOAT)
    R = np.frombuffer(exr.channel("R", pt), dtype=np.float32).reshape(H, W)
    G = np.frombuffer(exr.channel("G", pt), dtype=np.float32).reshape(H, W)
    B = np.frombuffer(exr.channel("B", pt), dtype=np.float32).reshape(H, W)
    return np.stack([R, G, B])  # (3,H,W)


def maxpool(map3, factor):
    """Stride-`factor` max-pool so sparse bright star points survive downscaling."""
    C, H, W = map3.shape
    nh, nw = H // factor, W // factor
    return map3[:, :nh * factor, :nw * factor].reshape(C, nh, factor, nw, factor).max(axis=(2, 4))


def dilate3(mask):
    """3x3 morphological dilation (any-neighbour expands)."""
    p = np.pad(mask, 1, mode="edge")
    return (p[0:-2, 0:-2] | p[0:-2, 1:-1] | p[0:-2, 2:]
            | p[1:-1, 0:-2] | p[1:-1, 2:]
            | p[2:, 0:-2] | p[2:, 1:-1] | p[2:, 2:])


def enhance_stars(map3, punch, star_floor):
    """Detect star points in the working map, grow them to 3x3 and boost, so
    every cube-face resolution samples the same visible stars. Returns a copy
    with stars strengthened and the count of stars detected."""
    out = map3.copy()
    lum = map3.max(axis=0)
    p = np.pad(lum, 1, mode="edge")
    nbmax = np.maximum.reduce([
        p[0:-2, 0:-2], p[0:-2, 1:-1], p[0:-2, 2:],
        p[1:-1, 0:-2], p[1:-1, 2:],
        p[2:, 0:-2], p[2:, 1:-1], p[2:, 2:],
    ])
    star = (lum >= star_floor) & (lum >= nbmax * 0.982)
    grown = dilate3(star)
    mult = np.where(grown, np.maximum(1.0, 1.0 + punch), 1.0)
    out *= mult[None, :, :]
    return np.clip(out, 0.0, 1.0), int(star.sum())


def tonemap(map3, lut_x, lut_y):
    return np.interp(map3, lut_x, lut_y).astype(np.float32)


def cube_sample(work, size, face):
    """Bilinear-sample the (enhanced, tonemapped) working map for one face."""
    H, W = work.shape[1], work.shape[2]
    ii, jj = np.meshgrid(np.arange(size), np.arange(size), indexing="ij")
    a = (2 * ii + 1) / size - 1   # [-1,1]
    b = (2 * jj + 1) / size - 1
    one = np.ones_like(a)
    if face == "px":
        d = np.stack([one, -b, -a], -1)
    elif face == "nx":
        d = np.stack([-one, -b, a], -1)
    elif face == "py":
        d = np.stack([a, one, -b], -1)
    elif face == "ny":
        d = np.stack([-a, -one, -b], -1)
    elif face == "pz":
        d = np.stack([a, -b, one], -1)
    else:  # nz
        d = np.stack([-a, -b, -one], -1)
    ln = np.sqrt(np.sum(d * d, -1, keepdims=True))
    d /= ln
    lat = np.arcsin(np.clip(d[..., 1], -1, 1))
    lon = np.arctan2(d[..., 0], d[..., 2])
    u = 0.5 + lon / (2 * np.pi)
    v = 0.5 - lat / np.pi
    x = u * (W - 1)
    y = v * (H - 1)
    x0 = np.floor(x).astype(np.int64); y0 = np.floor(y).astype(np.int64)
    x1 = np.minimum(x0 + 1, W - 1); y1 = np.minimum(y0 + 1, H - 1)
    fx = (x - x0)[None, ...]; fy = (y - y0)[None, ...]
    m = work
    c = (m[:, y0, x0] * (1 - fx) * (1 - fy)
         + m[:, y0, x1] * fx * (1 - fy)
         + m[:, y1, x0] * (1 - fx) * fy
         + m[:, y1, x1] * fx * fy)
    rgb = np.transpose(c, (1, 2, 0))
    return (rgb * 255.0 + 0.5).astype(np.uint8)


def make_contact(face_pngs, outpath):
    ims = [Image.open(p) for p in face_pngs]
    w, h = ims[0].size
    scale = max(1, w // 550)
    sw, sh = w // scale, h // scale
    sheet = Image.new("RGB", (sw * 3 + 20, sh * 2 + 20 + 26), (8, 8, 8))
    draw = ImageDraw.Draw(sheet)
    for idx, im in enumerate(ims):
        small = im.resize((sw, sh), Image.Resampling.BILINEAR)
        col, row = idx % 3, idx // 3
        sheet.paste(small, (10 + col * (sw + 5), 26 + row * (sh + 5)))
    draw.text((10, 5), "Deep Star Maps 2020 skybox faces (NASA SVS 4851)", fill=(255, 255, 255))
    sheet.save(outpath)
    return outpath


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("exr")
    ap.add_argument("outdir")
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--pool", type=int, default=2)
    ap.add_argument("--punch", type=float, default=1.3)
    ap.add_argument("--star-floor", type=float, default=0.16)
    ap.add_argument("--equirect", nargs=2, type=int, metavar=("W", "H"),
                    help="also write a seam-free equirectangular star PNG at W x H")
    args = ap.parse_args()

    os.makedirs(args.outdir, exist_ok=True)
    raw = load_exr_rgb(args.exr)
    work = maxpool(raw, args.pool) if args.pool > 1 else raw
    print(f"working map {work.shape[2]}x{work.shape[1]} (pool {args.pool})")
    del raw
    work, nstars = enhance_stars(work, args.punch, args.star_floor)
    print(f"detected/grew {nstars} star points; tonemapping")
    work = tonemap(work, DEFAULT_LUT_X, DEFAULT_LUT_Y)

    paths = {}
    for face in FACE_NAMES:
        rgb = cube_sample(work, args.size, face)
        p = os.path.join(args.outdir, f"stars_{face}_{args.size}.png")
        Image.fromarray(rgb, "RGB").save(p, "PNG")
        paths[face] = p
        print(f"  {face}: max={int(rgb.max())} mean={rgb.mean():.1f}")
    if args.equirect is not None:
        ew, eh = args.equirect
        eq = np.transpose(work, (1, 2, 0))
        eq = np.ascontiguousarray(eq)
        from PIL import Image as _I
        eqimg = _I.fromarray((np.clip(eq, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB").resize((ew, eh), _I.Resampling.LANCZOS)
        eqp = os.path.join(args.outdir, f"stars_equirect_{ew}x{eh}.png")
        eqimg.save(eqp, "PNG")
        print(f"  equirect: {eqp} ({ew}x{eh})")
    contact = os.path.join(args.outdir, f"stars_cubemap_contact_{args.size}.png")
    make_contact([paths[f] for f in FACE_NAMES], contact)
    print("contact:", contact)


if __name__ == "__main__":
    main()
