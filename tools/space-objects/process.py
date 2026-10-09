#!/usr/bin/env python3
"""
Deterministic cut-out space-object sprite pipeline for P589.

Loads an astronomical image, isolates the main object (luminance -> alpha with a
black-point lift), removes scattered point-source stars OUTSIDE the object mask
(via top-hat detection + neighbour-fill inpainting), feathers the image border
radially so no rectangle/corner/seam can ever show, resizes to <=1024px long
edge, and writes a straight-alpha RGBA PNG.

No hand editing anywhere: every knob is a number in CONFIGS and every run is
fully reproducible.
"""
import os, sys, json
import numpy as np
from scipy import ndimage
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None  # allow the 16000px Helix source

SRC_DIR = "/tmp/spaceobj_src"
OUT_DIR = "/home/domingo/Development/Artwork/p589obj"


# ---------------------------------------------------------------- primitives
def load_rgb(path, work_max=2800):
    im = Image.open(path).convert("RGB")
    w, h = im.size
    scale = min(1.0, work_max / max(w, h))
    if scale < 1.0:
        im = im.resize((max(1, int(w * scale)), max(1, int(h * scale))), Image.LANCZOS)
    arr = np.asarray(im, dtype=np.float32) / 255.0
    return arr, im.size


def lum(rgb):
    return rgb @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)


def gblur(a, sigma):
    """Gaussian blur a float channel/array (0..1) via PIL."""
    pil = Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))
    return np.asarray(pil.filter(ImageFilter.GaussianBlur(sigma)), dtype=np.float32) / 255.0


def minfilt(a, size):
    """Local minimum filter (grayscale erosion) via PIL MinFilter."""
    pil = Image.fromarray((np.clip(a, 0, 1) * 254).astype(np.uint8))
    out = pil.filter(ImageFilter.MinFilter(size))
    return np.asarray(out, dtype=np.float32) / 254.0


def dilate(mask, radius):
    """Binary dilation using MaxFilter."""
    pil = Image.fromarray(((mask > 0.5).astype(np.uint8)) * 255)
    d = pil.filter(ImageFilter.MaxFilter(radius * 2 + 1))
    return np.asarray(d, dtype=np.uint8) > 127


def erode(mask, radius):
    pil = Image.fromarray(((mask > 0.5).astype(np.uint8)) * 255)
    d = pil.filter(ImageFilter.MinFilter(radius * 2 + 1))
    return np.asarray(d, dtype=np.uint8) > 127


def inpaint_neighbour(img, hole, iterations):
    """Fill `hole` pixels from surrounding valid pixels by iterative 4-neighbour
    averaging (deterministic region fill), restricted to the hole's bbox so the
    cost scales with the star area, not the whole frame. img: HxWxC float32."""
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


def soft_alpha_map(bp, wp, gamma=1.0):
    def f(a):
        t = np.clip((a - bp) / (wp - bp), 0, 1)
        return np.power(t, gamma)
    return f


# ------------------------------------------------------------------ pipeline
def process(src, cfg, slug):
    rgb, (W, H) = load_rgb(os.path.join(SRC_DIR, src), cfg.get("work_max", 2800))
    l = lum(rgb)

    # 0) optional low-frequency background / haze subtraction (kills gradient
    #    veils such as galactic cirrus so no faint rectangle can linger)
    if cfg.get("bg_sub"):
        ped = gblur(l, cfg["bg_sub"])
        ped = ped - ped.min()
        subtract = np.clip(ped * cfg.get("bg_gain", 1.0), 0, None)
        rgb = np.clip(rgb - subtract[..., None], 0, 1)
        l = lum(rgb)

    # 1) OBJECT EXTENT: the large connected bright emission (galaxy disk / nebula
    #    body). Built from a STRONG blur so isolated field stars wash out below the
    #    threshold, while the object's real light stays above it.
    bl = gblur(l, cfg["obj_sigma"])
    obj_ext = bl > cfg["obj_thresh"]
    # erode-then-dilate knocks out tiny islands (a stray blurred star) while the
    # big object body survives; no connected-component labelling required.
    ik = cfg.get("island_kill", 2)
    obj_ext = dilate(erode(obj_ext, ik), ik)

    # 2) star removal: remove COMPACT bright point sources in the surrounding
    #    sky / outer halo — i.e. NOT on the object's own bright core. Dual-scale
    #    top-hat catches small and bloated stars.
    star = np.zeros((H, W), dtype=bool)
    for k, th, lm in cfg["star_scales"]:
        tophat = np.maximum(0, l - minfilt(l, k))
        star |= (tophat > th) & (l > lm)
    # never remove the object's bright core region here (its own light is kept)
    star &= ~obj_ext

    # 2b) foreground PSF-star removal ON the object (galaxies): a real foreground
    #     star is a SHARP, TINY, ISOLATED point (Airy disc + spikes) even when
    #     silhouetted on the disk, whereas the object's own knots / H II regions
    #     are extended blobs. So remove only the SMALL connected sharp-bright
    #     components (a handful of pixels); larger knots are kept. Off for
    #     planetary nebulae (Ring/Helix/Lions) whose central white-dwarf star IS
    #     part of the object.
    if cfg.get("onobj_star"):
        ssharp = np.maximum(0, l - minfilt(l, cfg.get("sharp_k", 7)))
        ssharp = ssharp > cfg.get("sharp_th", 0.07)
        ssharp = ndimage.binary_opening(ssharp, np.ones((3, 3), bool))
        slab, sn = ndimage.label(ssharp, structure=np.ones((3, 3), bool))
        ssizes = np.bincount(slab.ravel())
        maxpx = cfg.get("onobj_maxpx", 9)
        for i in range(1, sn + 1):
            if ssizes[i] <= maxpx:
                star |= slab == i

    star = dilate(star, cfg.get("star_dilate", 4))    # cover halos + spikes
    n_star = int(star.sum())
    if n_star:
        # fill each star with its locally-blurred surroundings (fast; a star on
        # black collapses to black, one on faint object blends into it)
        fill = gblur(rgb, cfg.get("fill_sigma", 8))
        rgb = np.where(star[..., None], fill, rgb)

    # 3) luminance -> alpha with black-point lift
    fa = soft_alpha_map(cfg["bp"], cfg["wp"], cfg.get("agamma", 1.0))
    alpha = fa(l)

    # 3b) OBJECT ENVELOPE: multiply alpha by an envelope that is EXACTLY 0 in
    #     the surrounding sky and 1 over the object, with a small smoothstep so
    #     the object's feathered edge still reads naturally. ANYTHING outside
    #     the envelope -> alpha 0, which is what clears the scattered field
    #     stars AROUND the object (Domingo: "we don't want photos with star
    #     backgrounds, but the natural object itself"). The old blurred env
    #     decayed slowly, leaving faint star specks in the outer halo.
    d_in = ndimage.distance_transform_edt(obj_ext)
    margin = max(2, cfg.get("env_margin", int(0.02 * max(H, W))))
    tt = np.clip(d_in / margin, 0, 1)
    hard_env = 0.5 - 0.5 * np.cos(np.pi * tt)   # 0 outside, 1 over the object
    alpha = alpha * hard_env

    # 4) radial border feather: force a cosine fade to zero at every edge so no
    #    rectangle/corner seam can appear.
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    fx = np.minimum(xx, W - 1 - xx)
    fy = np.minimum(yy, H - 1 - yy)
    dd = np.minimum(fx, fy)                       # distance to nearest edge
    fw = cfg.get("feather", 30)
    ramp = np.clip(dd / fw, 0, 1)
    ramp = 0.5 - 0.5 * np.cos(np.pi * np.clip(ramp, 0, 1))  # smoothstep-ish
    alpha = alpha * ramp

    out_rgb = rgb
    # 5) rescale to <=1024 long edge
    long_edge = max(W, H)
    if long_edge > 1024:
        s = 1024 / long_edge
        nw, nh = int(round(W * s)), int(round(H * s))
        out_rgb = np.asarray(
            Image.fromarray((np.clip(out_rgb, 0, 1) * 255).astype(np.uint8)).resize(
                (nw, nh), Image.LANCZOS), dtype=np.float32) / 255.0
        alpha = np.asarray(
            Image.fromarray((np.clip(alpha, 0, 1) * 255).astype(np.uint8)).resize(
                (nw, nh), Image.LANCZOS), dtype=np.float32) / 255.0

    # straight alpha RGBA
    rgba = np.dstack([
        np.clip(out_rgb[..., 0], 0, 1),
        np.clip(out_rgb[..., 1], 0, 1),
        np.clip(out_rgb[..., 2], 0, 1),
        np.clip(alpha, 0, 1),
    ])
    im_out = Image.fromarray((rgba * 255).astype(np.uint8), "RGBA")
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, slug + ".png")
    im_out.save(path)
    return path, im_out.size, n_star


CONFIGS = {
    # ---- compact nebulae: tight object extent, thorough dual-scale star removal
    "helix-nebula": dict(src="helix_noao0307a.jpg", bg_sub=16, bg_gain=0.7,
                         obj_sigma=12, obj_thresh=0.115,
                         star_scales=[(11, 0.023, 0.128), (21, 0.019, 0.128)],
                         star_dilate=5, keep_pad=3,
                         bp=0.007, wp=0.573, agamma=0.904, feather=40, island_kill=2),
    "ring-nebula-m57": dict(src="heic1310a.jpg",
                            obj_sigma=11, obj_thresh=0.078,
                            star_scales=[(11, 0.021, 0.082), (21, 0.017, 0.082)],
                            star_dilate=5, keep_pad=2, env_sigma=26,
                            bp=0.026, wp=0.670, agamma=0.856, feather=34, island_kill=2),
    "lion-nebula-miri": dict(src="lion_webb_miri.png",
                             obj_sigma=10, obj_thresh=0.080,
                             star_scales=[(9, 0.024, 0.128), (17, 0.020, 0.128)],
                             star_dilate=5, keep_pad=5, env_sigma=34,
                             bp=0.034, wp=0.658, agamma=0.866, feather=34, island_kill=2),
    "lion-nebula-nasa": dict(src="lion_nasa_ngc2392.jpg",
                             obj_sigma=10, obj_thresh=0.106,
                             star_scales=[(9, 0.027, 0.103), (17, 0.022, 0.103)],
                             star_dilate=5, keep_pad=5, env_sigma=32,
                             bp=0.064, wp=0.707, agamma=0.880, feather=32, island_kill=2),
    # ---- galaxies: generous object extent protects the disk; smaller keep_pad +
    # ---- lower thresholds let near-edge and fainter field stars be cleared too
    "whirlpool-m51": dict(src="heic0506a.jpg",
                          obj_sigma=38, obj_thresh=0.032,
                          star_scales=[(13, 0.024, 0.090), (23, 0.018, 0.085), (33, 0.011, 0.070)],
                          star_dilate=5, keep_pad=2, env_sigma=22,
                          onobj_star=True, sharp_k=7, sharp_th=0.067, onobj_maxpx=10,
                          bp=0.014, wp=0.489, agamma=1.006, feather=30, island_kill=4),
    "sombrero-m104": dict(src="opo0328a.jpg",
                          obj_sigma=34, obj_thresh=0.033,
                          star_scales=[(13, 0.024, 0.096), (23, 0.018, 0.089), (33, 0.011, 0.074)],
                          star_dilate=5, keep_pad=2, env_sigma=20,
                          onobj_star=True, sharp_k=7, sharp_th=0.056, onobj_maxpx=9,
                          bp=0.017, wp=0.528, agamma=0.938, feather=30, island_kill=4),
    "pinwheel-m101": dict(src="heic0602a.jpg",
                          obj_sigma=36, obj_thresh=0.031,
                          star_scales=[(13, 0.023, 0.087), (23, 0.017, 0.077), (33, 0.010, 0.062)],
                          star_dilate=5, keep_pad=2, env_sigma=24,
                          onobj_star=True, sharp_k=7, sharp_th=0.053, onobj_maxpx=10,
                          bp=0.051, wp=0.608, agamma=0.892, feather=38, island_kill=4),
}


def main():
    only = sys.argv[1:] or list(CONFIGS.keys())
    for slug in only:
        cfg = CONFIGS[slug]
        path, size, ns = process(cfg.pop("src"), cfg, slug)
        print(f"{slug}: {size} stars_removed={ns} -> {path}")


if __name__ == "__main__":
    main()
