#!/usr/bin/env python3
"""
P589 deep-field galaxy sprite library builder.

Source      : NASA/ESA Hubble Ultra Deep Field (HUDF), ESA/Hubble release heic0406a
              Full-depth composite (6200x6200), CC BY 4.0.
Method      : fully deterministic. Loads field -> subtracts local background ->
              thresholds + connected-component labels -> classifies/rejects stars,
              diffraction-spike streaks and frame-edge touches -> cuts each surviving
              galaxy into its own small RGBA sprite with a soft feathered alpha edge
              (single object only, no neighbours, no rectangle seam) -> resizes to a
              24-128px sprite -> emits contact sheet, manifest and licence.

No hand editing anywhere. Re-runnable.
"""

import os, json, math, argparse
import numpy as np
from PIL import Image
from scipy import ndimage

# ----------------------------------------------------------------------------
# configuration
# ----------------------------------------------------------------------------
SRC_JPG        = None   # set from argv
OUT_DIR        = None   # sprite output dir (artwork root)
REPORT_DIR     = None   # provenance/manifest/licence/contact sheet dir
SEED           = 12345
MAX_SPRITES    = 296    # upper cap on library size (target 150-300)
MIN_SPRITES    = 170
WORK_LONG      = 3400   # working resolution (downscale to keep cost sane)
BG_BLOCK       = 401    # local background smoothing window
THR_MULT       = 2.4    # threshold = THR_MULT * residual_rms (floor applied)
THR_FLOOR      = 0.028
PEAK_STAR      = 0.45   # a component peaking above this is a bright-core star (spike host)
POINT_PEAK     = 0.285  # compact point sources above this peak and thin width -> star
POINT_WIDTH    = 3.4    # mean transverse width below this marks an unresolved point source
SPIKE_ASPECT   = 7.0    # thin linear blobs above this aspect treated as spike streaks
MIN_MEANWIDTH  = 2.4    # drop 1-2px thick streak/point remnants
MIN_AREA       = 40     # px^2 at working res
EXTENDED_AREA  = 820    # px^2: above this a component is a resolved galaxy (kept)
CROWD_RADIUS   = 40     # penalise galaxies within this dist of a star centroid
FEATHER_FRAC   = 0.185  # alpha feather depth as fraction of object size
OBJ_OCCUPY     = 0.67   # object longest axis fraction of final sprite
MIN_FINAL, MAX_FINAL = 24, 125

rng = np.random.default_rng(SEED)


def local_background(lum, block):
    """Block-median background approximation."""
    h, w = lum.shape
    bh, bw = max(1, h // block), max(1, w // block)
    ch, cw = h // bh, w // bw
    lum_c = lum[:bh * cw * (bh)]  # unused; keep simple: bin on grid
    # bin into (bh,bw) cells of equal-ish size
    ys = np.linspace(0, h, bh + 1).astype(int)
    xs = np.linspace(0, w, bw + 1).astype(int)
    grid = np.zeros((bh, bw), np.float32)
    for i in range(bh):
        for j in range(bw):
            cell = lum[ys[i]:ys[i + 1], xs[j]:xs[j + 1]]
            grid[i, j] = np.median(cell)
    from scipy.ndimage import map_coordinates
    fy = np.linspace(0, bh - 1, h)
    fx = np.linspace(0, bw - 1, w)
    yy, xx = np.meshgrid(fy, fx, indexing='ij')
    bg = map_coordinates(grid, np.stack([yy, xx]), order=1, mode='nearest')
    return bg


def classify_and_cut(rgb, lum, obj, labels, n):
    """Classify components, reject stars/spikes/edges/crowds, emit sprite data.
    Returns (candidates, rejection_tally)."""
    rej = dict(edge=0, tiny=0, star=0, point=0, spike=0, streak=0)
    # frame-edge exclusion
    edge_mask = np.zeros_like(obj)
    edge_mask[[0, -1], :] = True
    edge_mask[:, [0, -1]] = True
    edge_labels = set(np.unique(labels[edge_mask]).tolist()) - {0}

    candidates = []
    for lab in range(1, n + 1):
        if lab in edge_labels:
            rej['edge'] += 1
            continue
        ys, xs = np.nonzero(labels == lab)
        area = ys.size
        if area < MIN_AREA:
            rej['tiny'] += 1
            continue
        y0, y1 = ys.min(), ys.max() + 1
        x0, x1 = xs.min(), xs.max() + 1
        hgt, wid = y1 - y0, x1 - x0
        vals = lum[ys, xs]
        peak = float(vals.max())
        mean = float(vals.mean())
        core_frac = float(np.mean(vals > 0.42))
        # solidity = area / convex-area approximation via bbox
        solidity = area / float(max(1, hgt * wid))
        # mean transverse width -> distinguishes streaks from blobs/edge-ons
        mean_width = area / float(max(hgt, wid))

        # --- star / spike / point-source rejection ---------------------------
        aspect = max(hgt, wid) / float(max(1, min(hgt, wid)))
        # thin 1-2px linear trails are diffraction spikes / cosmic rays -> always drop
        if mean_width < MIN_MEANWIDTH + 0.6 and aspect > SPIKE_ASPECT:
            rej['spike'] += 1
            continue
        if mean_width < MIN_MEANWIDTH and peak < 0.988:
            rej['streak'] += 1
            continue
        # extended objects are RESOLVED GALAXIES: keep even with a bright nucleus.
        # Only compact components can be foreground stars / point sources.
        if area < EXTENDED_AREA:
            if peak > PEAK_STAR or core_frac > 0.34:
                rej['star'] += 1
                continue                   # compact bright star (spike host)
            if mean_width < POINT_WIDTH and peak > POINT_PEAK:
                rej['point'] += 1
                continue                   # unresolved bright point (star / bare nucleus)

        # isolation within crop (neighbour-light risk)
        pad = int(max(6, 0.30 * max(hgt, wid)))
        cy0, cy1 = max(0, y0 - pad), min(rgb.shape[0], y1 + pad)
        cx0, cx1 = max(0, x0 - pad), min(rgb.shape[1], x1 + pad)
        crop_labels = labels[cy0:cy1, cx0:cx1]
        others = (crop_labels != lab) & (crop_labels != 0)
        other_frac = float(others.mean())

        candidates.append(dict(lab=lab, y0=y0, y1=y1, x0=x0, x1=x1,
                               hgt=hgt, wid=wid, area=area, peak=peak, mean=mean,
                               core_frac=core_frac, solidity=solidity,
                               aspect=aspect, mean_width=mean_width,
                               other_frac=other_frac, ys=ys, xs=xs))
    return candidates, rej


def check_alpha_invariants(an, border_tol=0.012, bright_tol=0.55, max_tol=0.895):
    """P589b — assert the alpha invariants of one final sprite (a float
    HxWx4 array in 0..1) so it is a FILLED object on black (object only, no
    edges):
      * alpha at the tile border is ~0  (no frame edge / box survives),
      * the brightest raw-RGB pixel of the object sits on a real interior
        (alpha clearly high — the core is NOT hollow),
      * a fully-opaque interior exists (max alpha ~1),
      * the alpha mass sits away from the border (centre is not transparent).

    Returns (True, '') or (False, '<reason>'). This FAILS on the old
    inverted-mask library: the galaxy core (brightest RGB pixel) had alpha
    ~0.00 while the box margin held alpha up to 1.
    """
    a = an[..., 3]
    h, w = a.shape
    border = np.concatenate([a[0, :], a[-1, :], a[:, 0], a[:, -1]])
    border_mean = float(border.mean())
    if border_mean > border_tol:
        return False, f"border_alpha={border_mean:.3f}"
    # brightest object pixel: the brightest RAW-RGB pixel of the object — under
    # the old inverted mask this is the galaxy core whose alpha was ~0 (FAIL);
    # after the fix the core sits at high alpha. Premultiplied luminance is NOT
    # used here: the old frame (alpha 1 but dim RGB) would masquerade as bright.
    lum = 0.2126 * an[..., 0] + 0.7152 * an[..., 1] + 0.0722 * an[..., 2]
    pk = int(np.argmax(lum))
    pk_a = float(a[pk // w, pk % w])
    if pk_a < bright_tol:
        return False, f"bright_pixel_alpha={pk_a:.3f}"
    if float(a.max()) < max_tol:
        return False, f"max_alpha={float(a.max()):.3f}"
    # centre must not be hollow: alpha-mass centroid well away from the border
    tot = a.sum()
    if tot <= 0:
        return False, "empty_alpha"
    cy = float((np.arange(h)[:, None] * a).sum() / tot)
    cx = float((np.arange(w)[None, :] * a).sum() / tot)
    if min(cy, h - 1 - cy) < 0.04 * h or min(cx, w - 1 - cx) < 0.04 * w:
        return False, f"mass_centroid({cx:.0f},{cy:.0f})"
    return True, ""


def feather_alpha(mask, feather):
    """Soft alpha: 1 inside the object, raised-cosine falloff to 0 across
    ``feather`` pixels just OUTSIDE the object boundary — the object stays
    filled (alpha 1 at its core) and fades to transparent in the surrounding
    margin, so a sprite is the galaxy itself on black, never a ring/frame.

    ``dm = edt(mask)`` is each pixel's distance to the nearest object pixel:
    0 in the background and on the boundary rim, growing to the object's
    medial axis. Mapping dm -> alpha so alpha=0 at dm=0 and alpha=1 at
    dm>=feather yields: background alpha 0, boundary rim a soft fade, object
    interior alpha 1 — the object stays FILLED.

    (P589b: this formerly used ``~mask`` — distance from the BACKGROUND to
    the object, which is 0 inside the object and grows away from it — so the
    same mapping gave the galaxy interior alpha 0 and the box margin around
    it alpha up to 1: the hollow-square / frame-edge sprites.)"""
    dm = ndimage.distance_transform_edt(mask)
    t = np.clip(dm / float(max(1.0, feather)), 0.0, 1.0)
    alpha = 0.5 * (1.0 - np.cos(np.pi * t))
    return alpha


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)          # sprite dir
    ap.add_argument('--report', required=True)       # manifest/sheet/licence dir
    ap.add_argument('--target', type=int, default=270)
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    # P589b: purge stale sprites so a rebuild never leaves old (inverted-mask)
    # frames alongside the new ones (the library dir must be exactly the new set).
    for f in os.listdir(args.out):
        if f.lower().endswith('.png'):
            try:
                os.remove(os.path.join(args.out, f))
            except OSError:
                pass
    img = Image.open(args.src).convert('RGB')
    W, H = img.size
    scale = WORK_LONG / float(W)
    ws = (int(round(H * scale)), int(round(W * scale)))
    imgw = img.resize((ws[1], ws[0]), Image.LANCZOS)
    rgb = np.asarray(imgw).astype(np.float32) / 255.0
    lum = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]

    bg = local_background(lum, BG_BLOCK)
    resid = lum - bg
    # residual rms (excluding strong sources)
    sel = resid < np.quantile(resid, 0.75)
    rms = float(np.std(resid[sel]))
    thr = max(THR_MULT * rms, THR_FLOOR)
    print(f"work res {rgb.shape[1]}x{rgb.shape[0]} rms={rms:.4f} thr={thr:.4f}")

    obj = resid > thr
    # small closing to bridge thin gaps inside galaxies without bridging the field
    struct = np.ones((3, 3), bool)
    obj = ndimage.binary_opening(obj, struct)
    labels, n = ndimage.label(obj, structure=np.ones((3, 3), bool))
    print(f"initial components {n}")

    cand, rej = classify_and_cut(rgb, lum, obj, labels, n)
    print(f"survivors after star/spike/edge reject: {len(cand)}  rejected={rej}")

    # reject galaxies sitting in a bright star's halo (crowding around stars)
    star_centroids = [(c['x0'] + c['wid'] / 2, c['y0'] + c['hgt'] / 2)
                      for c in cand if c['peak'] > PEAK_STAR]  # none survive; recompute below
    # (stars were already dropped; rebuild star core map from original labels)
    star_mask = lum > PEAK_STAR
    if star_mask.sum() > 0:
        slab, sn = ndimage.label(star_mask, structure=np.ones((3, 3), bool))
        star_positions = ndimage.center_of_mass(star_mask, slab, range(1, sn + 1))
    else:
        star_positions = []

    # score: bigger + less crowded + brighter is better
    def score(c):
        sz = max(c['wid'], c['hgt'])
        prox = min([math.hypot((c['x0']+c['wid']/2)-sx, (c['y0']+c['hgt']/2)-sy)
                    for sx, sy in star_positions] or [1e9])
        crowd_pen = 0.0 if prox > CROWD_RADIUS else 0.85 * (1 - prox / CROWD_RADIUS)
        iso = c['other_frac']
        return sz - crowd_pen * sz - iso * sz * 0.5

    cand.sort(key=score, reverse=True)

    # pick the top-target clean ones
    keep = cand[:args.target]
    print(f"selected for sprites: {len(keep)}")

    def has_second_blob(an):
        """Return True only for a genuinely SEPARATE second galaxy bleeding into the
        margin: a comparably-bright luminous cluster whose centroid sits well apart
        from the main object's core (internal knots of one galaxy are excluded).
        Neighbour cores are already zeroed by the single-label alpha mask, so this
        only guards against faint residual emission at the margin."""
        lum_s = 0.385 * an[..., 0] + 0.594 * an[..., 1] + 0.021 * an[..., 2]
        mask = (an[..., 3] > 0.30) & (lum_s > 0.048)
        if mask.sum() == 0:
            return False
        lab, k = ndimage.label(mask, structure=np.ones((3, 3), bool))
        if k < 2:
            return False
        sizes = np.bincount(lab.ravel())[1:]
        order = np.argsort(sizes)[::-1]
        main_lab = order[0] + 1
        second_size = sizes[order[1]]
        if second_size < 0.34 * sizes[order[0]] or second_size < 14:
            return False
        cy_m, cx_m = ndimage.center_of_mass(mask, lab, main_lab)
        cy_s, cx_s = ndimage.center_of_mass(mask, lab, order[1] + 1)
        h, w = an.shape[:2]
        span = max(h, w)
        return math.hypot(cx_s - cx_m, cy_s - cy_m) > 0.5 * span

    rejected_multi = 0
    rejected_alpha = 0
    alpha_reasons = []
    sprites = []
    for c in keep:
        obj_long = max(c['wid'], c['hgt'])
        feather = max(3.0, FEATHER_FRAC * obj_long)
        pad = int(round(feather + 2))
        y0, y1 = max(0, c['y0'] - pad), min(rgb.shape[0], c['y1'] + pad)
        x0, x1 = max(0, c['x0'] - pad), min(rgb.shape[1], c['x1'] + pad)
        cmask = (labels[y0:y1, x0:x1] == c['lab'])
        alpha = feather_alpha(cmask, feather)
        patch = rgb[y0:y1, x0:x1]
        rgba = np.zeros((y1 - y0, x1 - x0, 4), np.float32)
        rgba[..., :3] = patch
        rgba[..., 3] = alpha

        # target final size: object occupies OBJ_OCCUPY of the sprite, leaving
        # a clear transparent margin all around (no seam, no box). Slightly
        # magnified so small galaxies spread across sizes rather than all 24px.
        target_obj = float(np.clip(obj_long * 1.3, MIN_FINAL, MAX_FINAL))
        crop_long = max(y1 - y0, x1 - x0)
        sprite_side = max(MIN_FINAL, target_obj / OBJ_OCCUPY)
        scale_f = sprite_side / crop_long
        new_w = max(MIN_FINAL, int(round((x1 - x0) * scale_f)))
        new_h = max(MIN_FINAL, int(round((y1 - y0) * scale_f)))
        # keep within the ~24-128 px spec
        new_w, new_h = min(new_w, 128), min(new_h, 128)
        pil = Image.fromarray((np.clip(rgba, 0, 1) * 255).astype(np.uint8), 'RGBA')
        pil = pil.resize((new_w, new_h), Image.LANCZOS)

        an = np.asarray(pil).astype(np.float32) / 255.0
        # light de-ring: smooth RGB a touch to kill LANCZOS/jpeg spikes on hot cores
        an[..., :3] = ndimage.gaussian_filter(an[..., :3], sigma=0.65, mode='constant',
                                              cval=0.0) * (an[..., 3:4] > 0)
        an[..., :3] = np.clip(an[..., :3], 0, 1)
        # per-sprite contrast lift: brighten faint galaxies so they render well,
        # gain capped so no core is blown into a star-like point.
        reg = an[..., 3] > 0.4
        if reg.sum() > 10:
            lum_r = 0.385 * an[..., 0] + 0.594 * an[..., 1] + 0.021 * an[..., 2]
            hi = float(np.percentile(lum_r[reg], 99.5))
            gain = min(2.3, 0.92 / max(hi, 0.075))
            an[..., :3] = an[..., :3] * max(gain, 1.0)
            an[..., :3] = np.clip(an[..., :3], 0, 1) ** 0.93
        # guarantee no seam: a cosine vignette fades alpha smoothly to 0 over the
        # outer ~17% of every sprite, so no opaque edge/box can ever appear.
        ny, nx = an.shape[:2]
        py = np.minimum(np.arange(ny), ny - 1 - np.arange(ny)) / float(max(1, ny - 1))
        px = np.minimum(np.arange(nx), nx - 1 - np.arange(nx)) / float(max(1, nx - 1))
        pos = np.minimum(py[:, None], px[None, :])
        band = 0.172
        t = np.clip(pos / band, 0.0, 1.0)
        window = 0.5 * (1.0 - np.cos(np.pi * t))
        an[..., 3] *= window

        if has_second_blob(an):
            rejected_multi += 1
            continue
        # ---- P589b automated alpha invariant check -------------------------
        # The task's rule (Domingo: "object only, no edges"): every sprite must
        # be a FILLED object on black — alpha ~0 at the tile border, ~1 at the
        # brightest object pixel. This is the check that fails on the old
        # (inverted-mask) library (interior alpha 0, frame alpha 1).
        ok, why = check_alpha_invariants(an)
        if not ok:
            rejected_alpha += 1
            alpha_reasons.append(f"{c['lab']}:{why}")
            continue
        sprites.append(Image.fromarray((np.clip(an, 0, 1) * 255).astype(np.uint8), 'RGBA'))

    print(f"multi-object/blurry rejects in final cut: {rejected_multi}")
    print(f"P589b alpha-invariant rejects (hollow/frame sprites): {rejected_alpha}"
          + (f" e.g. {alpha_reasons[:5]}" if alpha_reasons else ""))
    if len(sprites) > 430:
        sprites = sprites[:430]   # hard cap; selection order favours the largest/niceest
    written = []
    for idx, pil in enumerate(sprites):
        fname = f"{idx:03d}.png"
        pil.save(os.path.join(args.out, fname))
        written.append(fname)

    # ---- manifest ----------------------------------------------------------
    sizes = [int(Image.open(os.path.join(args.out, f)).size[0]) for f in written]
    manifest = {
        "source": "NASA/ESA Hubble Ultra Deep Field (HUDF)",
        "source_url": "https://esahubble.org/images/heic0406a/",
        "download_url": "https://cdn.esahubble.org/archives/images/large/heic0406a.jpg",
        "licence": "CC BY 4.0",
        "licence_file": "licenses/esa_hubble_CC_BY_4.0.txt",
        "credit": ("NASA, ESA, H. Teplitz and M. Rafelski (IPAC/Caltech), "
                   "A. Koekemoer (STScI), R. Windhorst (Arizona State University), "
                   "and Z. Levay (STScI)"),
        "release_page_credit": "NASA, ESA, and S. Beckwith (STScI) and the HUDF Team",
        "count": len(written),
        "sprite_size_range": [min(sizes), max(sizes)],
        "sizes": sorted(set(sizes)),
        "method": "local-background subtraction, connected-component segmentation, "
                  "automatic star/spike/edge rejection, single-object feathered-alpha cut-out",
        "segmentation_stats": {
            "initial_threshold_components": n,
            "rejected_frame_edge": rej['edge'],
            "rejected_sub_min_area": rej['tiny'],
            "rejected_bright_star_core": rej['star'],
            "rejected_point_source": rej['point'],
            "rejected_diffraction_spike": rej['spike'],
            "rejected_streak_remnant": rej['streak'],
            "rejected_neighbour_bleed_final_cut": rejected_multi,
            "rejected_alpha_invariant_p589b": rejected_alpha,
            "retained_sprites": len(written),
        },
        "seed": SEED,
    }
    with open(os.path.join(args.report, "galaxy-library.json"), "w") as f:
        json.dump(manifest, f, indent=2)

    # ---- contact sheet on pure black ---------------------------------------
    grid_cols = 16
    cell = 104
    nrows = math.ceil(len(written) / grid_cols)
    sheet = Image.new('RGB', (grid_cols * cell, nrows * cell), (0, 0, 0))
    for i, f in enumerate(sorted(written)):
        im = Image.open(os.path.join(args.out, f)).convert('RGBA')
        im.thumbnail((cell - 8, cell - 8), Image.LANCZOS)
        canvas = Image.new('RGBA', (cell - 8, cell - 8), (0, 0, 0, 0))
        ox, oy = (canvas.width - im.width) // 2, (canvas.height - im.height) // 2
        canvas.paste(im, (ox, oy), im)
        r, c2 = divmod(i, grid_cols)
        sheet.paste(canvas.convert('RGB'), (c2 * cell + 4, r * cell + 4), canvas)
    sheet_path = os.path.join(args.report, "sheets", "contact_galaxies.png")
    sheet.save(sheet_path)
    print(f"contact sheet -> {sheet_path} ({sheet.size})")

    print(f"DONE sprites={len(written)}")


if __name__ == "__main__":
    main()
