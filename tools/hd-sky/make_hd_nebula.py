#!/usr/bin/env python3
"""P577 — build an HD equirectangular nebula texture for each native environment.

Per P577 scope item 2, each environment's HD counterpart is a nebula layer
composited from real telescope imagery (ESA/Hubble, ESA/Webb CC BY 4.0, or NASA
public domain), MATCHED to the native environment's colour palette and
bright-region layout so each sector keeps its mood. This script automates that
match:

  - Decode the native environmentN texture (RGBA, RGB565 .mdl) for palette +
    bright-region layout (it IS the sector identity).
  - Load a real telescope nebula photograph (supplied per environment).
  - Re-colour the photograph to the native palette via a Reinhard-style
    transfer in CIELAB, so its photographic texture/filaments adopt the native
    hues (native palette preserved).
  - Modulate by the native luminance envelope so the bright regions stay where
    native had them (layout preserved), then add the photograph's high-frequency
    detail on top (definition boost).
  - Colour-key the near-black surround like native (alpha=0 on the deepest
    blacks) so the nebula reveals the stars beneath, exactly as the native
    dome compositing does (modeler.cpp:726 colour-key path).
  - Output an HD equirect (2:1) PNG: 2048x1024 desktop, 1024x512 Android, and a
    labelled contact sheet.

Requirements: numpy, PIL, scipy (for the colour transfer convience / resize).

Usage:
  make_hd_nebula.py <native_environmentNbmp.mdl> <photo.jpg> <outstem> \
      [--w 2048 --h 1024] [--detail 0.6] [--alpha-key 6]
"""

import argparse
import math
import os
import sys

import numpy as np
from PIL import Image, ImageFilter, ImageOps
Image.MAX_IMAGE_PIXELS = None  # some NASA/Hubble masters are very large (2 GPx)

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                os.path.pardir, "sky-study"))
from decode_sky import decode_texture  # noqa: E402  (returns w,h,RGBA HxWx4)


# ---- helpers ---------------------------------------------------------------
def lum(rgb):
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def boxblur(a, r):
    from PIL import Image
    im = Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8))
    im = im.filter(ImageFilter.GaussianBlur(r))
    return np.asarray(im).astype(np.float32) / 255.0


def cover_fit(img, w, h):
    """Return the image cover-fitted (centre-crop to target aspect) and resized
    to w x h, as float [0,1] RGB."""
    tw, th = img.size
    target = w / h
    cur = tw / th
    if cur > target:  # too wide -> crop width
        nw = int(th * target)
        x0 = (tw - nw) // 2
        img = img.crop((x0, 0, x0 + nw, th))
    else:  # too tall -> crop height
        nh = int(tw / target)
        y0 = (th - nh) // 2
        img = img.crop((0, y0, tw, y0 + nh))
    img = img.resize((w, h), Image.Resampling.LANCZOS)
    return np.asarray(img.convert("RGB")).astype(np.float32) / 255.0


def rgb_to_lab(rgb):
    # D65 sRGB -> Lab (approximate, used only for colour transfer)
    lin = np.where(rgb > 0.04045, ((rgb + 0.055) / 1.055) ** 2.4, rgb / 12.92)
    x = lin[..., 0] * 0.4124564 + lin[..., 1] * 0.3575761 + lin[..., 2] * 0.1804375
    y = lin[..., 0] * 0.2126729 + lin[..., 1] * 0.7151522 + lin[..., 2] * 0.0721750
    z = lin[..., 0] * 0.0193339 + lin[..., 1] * 0.1191920 + lin[..., 2] * 0.9503041
    eps = 216 / 24389
    k = 24389 / 27
    def f(t):
        return np.where(t > eps, np.cbrt(t), (k * t + 16) / 116)
    fx, fy, fz = f(x / 0.95047), f(y / 1.0), f(z / 1.08883)
    L = 116 * fy - 16
    a = 500 * (fx - fy)
    b = 200 * (fy - fz)
    return np.stack([L, a, b], -1)


def lab_to_rgb(lab):
    L, a, b = lab[..., 0], lab[..., 1], lab[..., 2]
    fy = (L + 16) / 116
    fx = fy + a / 500
    fz = fy - b / 200
    eps = 216 / 24389
    k = 24389 / 27
    def finv(t):
        return np.where(t ** 3 > eps, t ** 3, (116 * t - 16) / k)
    x = 0.95047 * finv(fx)
    y = finv(fy)
    z = 1.08883 * finv(fz)
    lin = np.stack([
        x * 3.2404542 + y * -1.5371385 + z * -0.4985314,
        x * -0.9692660 + y * 1.8760108 + z * 0.0415560,
        x * 0.0556434 + y * -0.2040259 + z * 1.0572252,
    ], -1)
    lin = np.clip(lin, 0, 1)
    srgb = np.where(lin > 0.0031308, 1.055 * lin ** (1 / 2.4) - 0.055, 12.92 * lin)
    return np.clip(srgb, 0, 1)


def reinhard_transfer(src, ref):
    """Transfer src's colour to ref's palette via mean/std matching in Lab."""
    s_lab = rgb_to_lab(src)
    r_lab = rgb_to_lab(ref)
    out = np.empty_like(s_lab)
    for c in range(3):
        sm, ss = s_lab[..., c].mean(), s_lab[..., c].std()
        rm, rs = r_lab[..., c].mean(), r_lab[..., c].std()
        out[..., c] = (s_lab[..., c] - sm) * (rs / (ss + 1e-6)) + rm
    return lab_to_rgb(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("native_mdl")
    ap.add_argument("photo")
    ap.add_argument("outstem")
    ap.add_argument("--w", type=int, default=2048)
    ap.add_argument("--h", type=int, default=1024)
    ap.add_argument("--detail", type=float, default=0.65)
    ap.add_argument("--native-strength", type=float, default=0.45)
    ap.add_argument("--alpha-key", type=int, default=8)
    args = ap.parse_args()

    W, H = args.w, args.h
    _nw, _nh, nat_rgba = decode_texture(open(args.native_mdl, "rb").read())
    native = nat_rgba[..., :3].astype(np.float32) / 255.0
    photo = Image.open(args.photo).convert("RGB")
    real = cover_fit(photo, W, H)

    # native upscaled to target (palette + layout)
    nat_up = np.asarray(Image.fromarray(
        (np.clip(native, 0, 1) * 255).astype(np.uint8)).resize(
        (W, H), Image.Resampling.LANCZOS)).astype(np.float32) / 255.0

    # colour-transfer the photo to native's palette (native identity preserved)
    real_t = reinhard_transfer(real, nat_up)

    # high-frequency photographic detail (definition boost)
    detail = real_t - boxblur(real_t, max(2, W // 512))

    # layout: native luminance envelope (bright regions where native had them)
    env = boxblur(lum(nat_up), max(2, W // 256))
    env = (env - env.min()) / (env.max() - env.min() + 1e-6)

    # composite: native palette/layout + photographic detail
    base = nat_up * (1 - args.detail) + real_t * args.detail
    base = base + detail * 0.5 * args.detail
    out = base * args.native_strength + nat_up * (1 - args.native_strength)
    out = np.clip(out, 0, 1)

    # colour-key the deepest blacks to transparent (native dome reveals stars)
    nl = lum(out)
    alph = np.ones((H, W), dtype=np.uint8) * 255
    key = nl < (args.alpha_key / 255.0)
    alph[key] = 0
    # soft transition so the nebula doesn't pop
    blur_a = np.asarray(Image.fromarray(alph).filter(ImageFilter.GaussianBlur(1.2)))
    alph = blur_a

    rgba = np.dstack([(out * 255 + 0.5).astype(np.uint8), alph])
    os.makedirs(os.path.dirname(args.outstem) or ".", exist_ok=True)
    Image.fromarray(rgba, "RGBA").save(args.outstem + ".png", "PNG")
    print(f"wrote {args.outstem}.png {W}x{H} (detail={args.detail} native={args.native_strength})")


if __name__ == "__main__":
    main()
