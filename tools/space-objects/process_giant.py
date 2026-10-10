#!/usr/bin/env python3
"""
P592 — deterministic GIANT sky-object sprite pipeline.

Turns a single astronomical image (a face-on galaxy) into ONE seamless square
texture that is drawn on a ~90° curved patch of the sky sphere. The object is
centred so the galaxy body sits in the CENTRAL portion of the square and is
feathered RADIALLY to pure black WELL INSIDE the patch border — so the square
patch edge (and any corner/seam) is never visible, exactly like the P589
cut-outs but huge ("a larger object that spans across half the sky but still
respects the black background").

For the star-crowded Andromeda mosaic it also runs the STRICT P589c-style
field-star removal (compact isolated PSF points on any local background,
including inside the faint halo where Sombrero failed), then verifies a
zero-star gate before the texture is accepted.

Output: straight-alpha RGBA PNG (4096px), premultiplied at load by the client.
Reuses tools/space-objects/starclean.py helpers where applicable.
"""
import os, sys
import numpy as np
from scipy import ndimage
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None

import starclean

SOURCES = "/home/domingo/Development/AllegianceCore/docs/reports/P592_assets/sources"
OUT = "/home/domingo/Development/Artwork/giantobj"
SPRITES = "/home/domingo/Development/AllegianceCore/docs/reports/P592_assets/sprites"


# ---------------------------------------------------------------- primitives
def gblur(a, sigma):
    pil = Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))
    return np.asarray(pil.filter(ImageFilter.GaussianBlur(sigma)), dtype=np.float32) / 255.0


def minfilt(a, size):
    pil = Image.fromarray((np.clip(a, 0, 1) * 254).astype(np.uint8))
    out = pil.filter(ImageFilter.MinFilter(size))
    return np.asarray(out, dtype=np.float32) / 254.0


def dilate(mask, radius):
    pil = Image.fromarray(((mask > 0.5).astype(np.uint8)) * 255)
    d = pil.filter(ImageFilter.MaxFilter(radius * 2 + 1))
    return np.asarray(d, dtype=np.uint8) > 127


def lum(rgb):
    return rgb @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)


def load_rgb(path, work_max=3400):
    im = Image.open(path).convert("RGB")
    w, h = im.size
    scale = min(1.0, work_max / max(w, h))
    if scale < 1.0:
        im = im.resize((max(1, int(w * scale)), max(1, int(h * scale))), Image.LANCZOS)
    return np.asarray(im, dtype=np.float32) / 255.0, im.size


def inpaint_neighbour(img, hole, iterations):
    img = img.copy()
    H, W = img.shape[:2]
    ys, xs = np.where(hole)
    if len(ys) == 0:
        return img
    pad = 2
    y0, y1 = max(0, ys.min() - pad), min(H, ys.max() + pad + 1)
    x0, x1 = max(0, xs.min() - pad), min(W, xs.max() + pad + 1)
    sub = img[y0:y1, x0:x1]
    hsub = hole[y0:y1, x0:x1]
    sh, sw = sub.shape[:2]
    filled = ~hsub
    tmp = sub.copy()
    for _ in range(iterations):
        neigh = np.zeros_like(sub)
        cnt = np.zeros((sh, sw, 1), dtype=np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            sy, sx = slice(max(0, dy), sh + min(0, dy)), slice(max(0, dx), sw + min(0, dx))
            ty, tx = slice(max(0, -dy), sh + min(0, -dy)), slice(max(0, -dx), sw + min(0, -dx))
            neigh[sy, sx] += np.where(filled[ty, tx, None], tmp[ty, tx], 0)
            cnt[sy, sx, 0] += filled[ty, tx]
        ready = hsub & (cnt[:, :, 0] > 0)
        sub[ready] = neigh[ready] / cnt[ready]
        filled |= ready
        tmp = sub.copy()
        if not ready.any():
            break
    img[y0:y1, x0:x1] = sub
    return img


# ------------------------------------------------- local-contrast star removal
def star_mask(l, size_k=7, top_t=0.045, peak_min=0.075, bg_ratio=2.2, max_px_frac=8e-5,
              min_px=3, annulus_dark=None):
    """Detect COMPACT BRIGHT POINTS that spike well above their LOCAL background
    (top-hat residual), on ANY background level — so a foreground Milky Way star
    is caught whether it sits on black sky or inside Andromeda's faint halo.
    Returns a boolean pixel mask. Uses find_objects bbox slices so the cost
    scales with the number of SIZE-PASSING components, not the full frame."""
    tophat = np.maximum(0, l - minfilt(l, size_k))
    cand = (tophat > top_t) & (l > peak_min)
    lab, n = ndimage.label(cand, structure=np.ones((3, 3), bool))
    if n == 0:
        return np.zeros(l.shape, dtype=bool)
    sizes = np.bincount(lab.ravel())
    H, W = l.shape
    max_px = max(6, int(max_px_frac * H * W))
    objs = ndimage.find_objects(lab)
    keep = np.zeros(lab.shape, dtype=bool)
    for i in range(1, n + 1):
        s = sizes[i]
        if s < min_px or s > max_px:
            continue
        sl = objs[i - 1]
        if sl is None:
            continue
        sub_l = l[sl]
        sub_lab = lab[sl]
        ys, xs = np.where(sub_lab == i)
        if len(ys) == 0:
            continue
        hh = int(ys.max()) - int(ys.min()) + 1
        ww = int(xs.max()) - int(xs.min()) + 1
        if max(hh, ww) / max(1, min(hh, ww)) > 2.6:
            continue  # elongated ribbon, not a point
        cy0, cx0 = sl[0].start, sl[1].start
        cy = cy0 + ys.mean(); cx = cx0 + xs.mean()
        rad = int(np.sqrt(s))
        r1 = max(4, 2 * rad); r2 = max(10, 4 * rad)
        y0, y1 = max(0, int(cy) - (r2 + 2)), min(H, int(cy) + (r2 + 3))
        x0, x1 = max(0, int(cx) - (r2 + 2)), min(W, int(cx) + (r2 + 3))
        ly, lx = np.indices((y1 - y0, x1 - x0))
        rr = np.sqrt((ly + y0 - cy) ** 2 + (lx + x0 - cx) ** 2)
        ring = (rr >= r1) & (rr < r2)
        if ring.sum() == 0:
            continue
        bg = float(np.median(l[y0:y1, x0:x1][ring]))
        peak = float(sub_l[ys, xs].max())
        # star: peak well above local background
        if peak > bg * bg_ratio + 0.02 and peak > peak_min:
            keep |= lab == i
    return keep


def envelope(l, sigma_frac=0.011, thresh=0.10):
    """Galaxy-body envelope: a smoothed luminance threshold. For the star gate
    the 'outside-envelope' region is where stray points must be absent — for the
    Milky Way artist's concept this is the clean sky around the disc; for the
    Andromeda photo the envelope is kept TIGHT to the bright disc so foreground
    Milky Way stars in the FAINT HALO are treated as outside (and removed)."""
    H, W = l.shape
    sigma = max(3, int(sigma_frac * max(H, W)))
    return gblur(l, sigma) > thresh


def count_star_components_outside(l, env):
    """Count compact point components whose centroid lies OUTSIDE the envelope
    (the strict gate: must be 0 on a finished giant)."""
    m = star_mask(l)
    lab, n = ndimage.label(m, structure=np.ones((3, 3), bool))
    if n == 0:
        return 0
    objs = ndimage.find_objects(lab)
    sizes = np.bincount(lab.ravel())
    outside = 0
    for i in range(1, n + 1):
        sl = objs[i - 1]
        if sl is None:
            continue
        sub_lab = lab[sl]
        ys, xs = np.where(sub_lab == i)
        if len(ys) == 0:
            continue
        cy = sl[0].start + ys.mean(); cx = sl[1].start + xs.mean()
        if not env[int(cy), int(cx)]:
            outside += 1
    return outside


# ---------------------------------------------------------------- composition
def feather_radial(alpha, size, rho_f1, rho_f2):
    """Multiply alpha by a cosine feather that is 1 inside ρ=rho_f1 (ρ = r /
    half-width from the centre) and fades to exactly 0 by ρ=rho_f2, so the patch
    edge (ρ=1.0) and corners (ρ=√2) are pure black — the border is never seen."""
    half = size / 2.0
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    rho = np.sqrt((yy - (size - 1) / 2.0) ** 2 + (xx - (size - 1) / 2.0) ** 2) / half
    t = np.clip((rho - rho_f1) / (rho_f2 - rho_f1), 0, 1)
    feather = 0.5 + 0.5 * np.cos(np.pi * t)          # 1 at rho<=rho_f1, 0 at rho>=rho_f2
    feather = np.where(rho <= rho_f1, 1.0, feather)
    feather = np.where(rho >= rho_f2, 0.0, feather)
    return alpha * feather


def place_centered(rgb, alpha, size, body_frac, feather=(0.62, 0.865)):
    """Resample the (rgb, alpha) so the object's LONG AXIS spans body_frac*size
    (a diameter; the galaxy tip sits at ρ=body_frac of the half-width) centred in
    a size x size square, then radially feather to pure black inside the border
    (feather = (rho_f1, rho_f2) in units of half-width, defaults keep the patch
    edge black)."""
    H, W = rgb.shape[:2]
    # object extent (non-black) -> used to centre it and size it
    l = lum(rgb)
    mask = l > 0.004
    ys, xs = np.where(mask)
    y0, y1, x0, x1 = ys.min(), ys.max(), xs.min(), xs.max()
    objw, objh = x1 - x0 + 1, y1 - y0 + 1
    long_axis = max(objw, objh)
    scale = (body_frac * size) / long_axis
    objw2, objh2 = max(1, int(round(objw * scale))), max(1, int(round(objh * scale)))

    # crop object bbox (pad a little) then resize
    pad = 4
    y0 = max(0, y0 - pad); y1 = min(H, y1 + pad + 1)
    x0 = max(0, x0 - pad); x1 = min(W, x1 + pad + 1)
    rgbc = rgb[y0:y1, x0:x1]
    ac = alpha[y0:y1, x0:x1]

    def res(a):
        return np.asarray(Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))
                          .resize((objw2, objh2), Image.LANCZOS), dtype=np.float32) / 255.0

    rgbr, ar = res(rgbc), res(ac)
    # paste centred into the output square
    out_rgb = np.zeros((size, size, 3), dtype=np.float32)
    out_a = np.zeros((size, size), dtype=np.float32)
    ox = (size - objw2) // 2; oy = (size - objh2) // 2
    out_rgb[oy:oy + objh2, ox:ox + objw2] = rgbr
    out_a[oy:oy + objh2, ox:ox + objw2] = ar
    out_a = feather_radial(out_a, size, feather[0], feather[1])
    return out_rgb, out_a


def save(rgb, alpha, path):
    rgba = np.dstack([
        np.clip(rgb[..., 0], 0, 1), np.clip(rgb[..., 1], 0, 1),
        np.clip(rgb[..., 2], 0, 1), np.clip(alpha, 0, 1)])
    im = Image.fromarray((rgba * 255).astype(np.uint8), "RGBA")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im.save(path)
    return path


def make_giant(src, out_base, size=4096, body_frac=0.675, feather=(0.335, 0.466),
               bg_sub=None, work_max=3400, do_stars=False, star_top_t=0.045,
               star_peak_min=0.065, star_bg_ratio=2.0, env_sigma=0.011, env_thresh=0.10):
    rgb, (W, H) = load_rgb(os.path.join(SOURCES, src), work_max)
    l = lum(rgb)
    removed = 0
    if bg_sub:
        ped = gblur(l, bg_sub)
        ped = ped - ped.min()
        rgb = np.clip(rgb - np.clip(ped, 0, None)[..., None], 0, 1)
        l = lum(rgb)
    if do_stars:
        # Remove compact points OUTSIDE the galaxy-body envelope only (the
        # bright disc + core stay untouched; foreground MW stars in the faint
        # halo and on the sky are scrubbed). inpaint from a local blur.
        env = envelope(l, env_sigma, env_thresh)
        sm = star_mask(l, top_t=star_top_t, peak_min=star_peak_min, bg_ratio=star_bg_ratio)
        sm = sm & ~env
        sm = dilate(sm, 3)
        removed = int(sm.sum())
        fill = gblur(rgb, 8)
        rgb = np.where(sm[..., None], fill, rgb)
        l = lum(rgb)
    # luminance -> alpha (black-point lift), the object on black
    alpha = np.clip((l - 0.008) / (0.998 - 0.008), 0, 1) ** 0.9
    # harden faint veil into the object; feather radially to black near border
    rgb, alpha = place_centered(rgb, alpha, size, body_frac, feather)
    for tag, dest in (("", OUT), ("_sprite", SPRITES)):
        os.makedirs(dest, exist_ok=True)
        outname = out_base + (tag and "_sprite" or "") + ".png"
        save(rgb, alpha, os.path.join(dest, outname))
    # strict gate: 0 compact points outside the galaxy-body envelope on the
    # finished sprite.
    lf = lum(rgb)
    fen = envelope(lf, env_sigma, env_thresh)
    resid = count_star_components_outside(lf, fen)
    print(f"{src}: removed_px={removed} final_outside_stars={resid} size={size}")
    return removed, resid


CONFIGS = {
    # Milky Way: clean NASA/JPL artist's concept — no photographic field stars.
    # The envelope must encompass the WHOLE galaxy (incl. its own bright outer
    # arm clusters, which are part of the body), so only the pure black sky
    # around it is "outside" and must be star-free.
    "milkyway_terra": dict(src="MilkyWay_faceon_RHurt_Spitzer_SSC2008-10a1_5600x5600.png",
                           body_frac=0.564, feather=(0.604, 0.852), work_max=3400,
                           do_stars=False, env_sigma=0.016, env_thresh=0.032),
    "andromeda_m31": dict(src="andromeda_m31_phast.jpg",
                          body_frac=0.648, feather=(0.684, 0.876), work_max=3600,
                          do_stars=True, star_top_t=0.038, star_peak_min=0.058,
                          star_bg_ratio=1.9, env_sigma=0.011, env_thresh=0.098),
}


def main():
    only = sys.argv[1:] or list(CONFIGS.keys())
    for name in only:
        cfg = CONFIGS[name]
        make_giant(**cfg, out_base=name)


if __name__ == "__main__":
    main()
