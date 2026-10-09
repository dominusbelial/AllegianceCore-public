#!/usr/bin/env python3
"""
P589c — shared object-envelope + field-star detector for the hero-object
sprite pipeline (tools/space-objects/).

Single source of truth used by BOTH process.py (removes the stars) and
check_hero_objects.py (verifies there are none), so processing and checking
always agree on what "the object" is and what a "field star" is.

Definitions (Domingo's rule: "no photo star-backgrounds, just the natural
object"):
  * OBJECT ENVELOPE  = a smoothed luminance threshold of the object. A strong
    Gaussian blur of the luminance bridged over a small fraction of the frame
    (env_sigma_frac) followed by a luminance cutoff (env_thresh). The bright
    object body stays above it, isolated field stars wash out below it, so the
    envelope hugs the object and leaves the surrounding sky outside.
  * FIELD STAR       = a COMPACT BRIGHT point (PSF-like; tiny, sharp, isolated,
    sometimes with diffraction spikes) whose local surroundings are DARK and
    which sits OUTSIDE the object envelope. High peak-to-surrounding contrast
    on dark sky is the shape cue that separates a foreground field star from an
    H II knot / cluster (those sit on the galaxy's own bright emission, so their
    local background is NOT dark). Components are labelled by connectivity; a
    spiky star's diffraction spikes travel with it as part of one component.

Everything is expressed scale-invariantly (envelope sigma as a fraction of the
frame, component-size limits as a fraction of the area) so the check at the
1024-px sprite and the processing at the working resolution agree.

Requires: numpy, scipy.ndimage, PIL (SciPy >= 1.x).
"""
import numpy as np
from scipy import ndimage
from PIL import Image, ImageFilter

# ------------------------------------------------------------------ luminance
def lum(rgb):
    return rgb @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)


def gblur(a, sigma):
    """Gaussian blur a float 0..1 channel via PIL (LANCZOS-exact enough)."""
    pil = Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))
    return np.asarray(pil.filter(ImageFilter.GaussianBlur(sigma)), dtype=np.float32) / 255.0


def minfilt(a, size):
    """Local minimum (grayscale erosion) via PIL MinFilter."""
    pil = Image.fromarray((np.clip(a, 0, 1) * 254).astype(np.uint8))
    out = pil.filter(ImageFilter.MinFilter(size))
    return np.asarray(out, dtype=np.float32) / 254.0


# ------------------------------------------------------------------ envelope
# Per-object (env_sigma_frac, env_thresh). env_sigma_frac = Gaussian sigma as a
# fraction of the LONG edge; env_thresh = luminance cutoff (0..1). Chosen (and
# calibrated on the P589b library) so the visible field stars fall OUTSIDE the
# envelope while the object's own disk/arms/annulus stay inside.
ENVELOPE = {
    "helix-nebula":      (0.010, 0.06),
    "whirlpool-m51":     (0.010, 0.06),
    "ring-nebula-m57":   (0.010, 0.06),
    "sombrero-m104":     (0.010, 0.06),
    "pinwheel-m101":     (0.010, 0.06),
    "lion-nebula-miri":  (0.010, 0.06),
    "lion-nebula-nasa":  (0.010, 0.06),
}


def envelope(l, slug):
    """Object envelope from a smoothed luminance threshold (see module doc)."""
    sf, th = ENVELOPE[slug]
    H, W = l.shape
    sigma = max(3, int(sf * max(H, W)))
    return gblur(l, sigma) > th


# ------------------------------------------------------------------ detection
# Field-star thresholds (all luminance 0..1 or boolean-ish):
#   top_t  : top-hat residual that must be exceeded (compactness cue)
#   peak_t : minimum peak luminance of the component
#   bg_t   : maximum local surrounding median luminance ("dark surroundings" —
#            keeps H II knots, which sit on bright galaxy emission, out)
#   alpha_t: minimum sprite alpha so only VISIBLE (in-game) points count
FIELD_T = {
    "helix-nebula":      (0.012, 0.07, 0.041, 0.02),
    "whirlpool-m51":     (0.012, 0.07, 0.041, 0.02),
    "ring-nebula-m57":   (0.012, 0.07, 0.041, 0.02),
    "sombrero-m104":     (0.012, 0.07, 0.041, 0.02),
    "pinwheel-m101":     (0.012, 0.07, 0.041, 0.02),
    "lion-nebula-miri":  (0.012, 0.07, 0.041, 0.02),
    "lion-nebula-nasa":  (0.012, 0.07, 0.041, 0.02),
}


def _size_bounds(area):
    """Component-size limits scale with the frame area so the detector agrees
    between the working resolution and the final 1024-px sprite."""
    return max(2, int(0.0000022 * area)), max(150, int(0.00012 * area))


def _elongated(ys, xs):
    """Reject long thin ribbons (shell/rim fragments cut by the envelope,
    diffraction-glare streaks): a genuine field star is compact, not a long
    sliver. Aspect ratio of the component's bounding box."""
    hh = int(ys.max()) - int(ys.min()) + 1
    ww = int(xs.max()) - int(xs.min()) + 1
    return max(hh, ww) / max(1, min(hh, ww)) > 2.6


def field_star_mask(l, alpha, env, slug, include_alpha=True):
    """Boolean mask of field-star PIXELS outside the envelope (for inpainting).
    With include_alpha the mask is limited to visible (alpha>=alpha_t) pixels,
    which is what the check wants; the processor passes include_alpha=False so
    it also cleans barely-visible specks that could flicker at the feather.

    A field star must be an ISOLATED compact bright point: small, not elongated,
    sitting on DARK surroundings in an EXTENDED annulus all around it (the
    "high peak-to-surrounding contrast" cue). Requiring the whole extended
    annulus to be mostly dark is what rejects an object's own rim/shell
    fragments (bright emission continues on one side) and an H II knot on a
    bright galaxy arm (bright emission on one side) — both have a non-dark
    fraction of the annulus, whereas a true foreground field star is dark on
    every side."""
    top_t, peak_t, bg_t, alpha_t = FIELD_T[slug]
    tophat = np.maximum(0, l - minfilt(l, 7))
    cand = (tophat > top_t) & (l > peak_t - 0.02) & (~env)
    if include_alpha:
        cand &= alpha >= alpha_t
    lab, n = ndimage.label(cand, structure=np.ones((3, 3), bool))
    sizes = np.bincount(lab.ravel())
    H, W = l.shape
    min_px, max_px = _size_bounds(H * W)
    keep = np.zeros(lab.shape, dtype=bool)
    for i in range(1, n + 1):
        s = sizes[i]
        if s < min_px or s > max_px:
            continue
        ys, xs = np.where(lab == i)
        cy, cx = ys.mean(), xs.mean()
        if env[int(cy), int(cx)]:
            continue  # centroid inside the envelope -> the object, not a star
        if _elongated(ys, xs):
            continue  # a long sliver is a shell/rim fragment, not a star
        rad = int(np.sqrt(s))
        r1 = max(6, 2 * rad)          # extended annulus inner radius
        r2 = max(14, 5 * rad)         # extended annulus outer radius
        y0, y1 = max(0, int(cy) - (r2 + 2)), min(H, int(cy) + (r2 + 3))
        x0, x1 = max(0, int(cx) - (r2 + 2)), min(W, int(cx) + (r2 + 3))
        ly, lx = np.indices((y1 - y0, x1 - x0))
        ly = ly + y0 - cy
        lx = lx + x0 - cx
        rr = np.sqrt(ly * ly + lx * lx)
        ring = (rr >= r1) & (rr < r2)
        if ring.sum() == 0:
            continue
        bg = float(np.median(l[y0:y1, x0:x1][ring]))
        peak = float(l[ys, xs].max())
        if peak > peak_t and bg < bg_t:
            # ISOLATION: the extended annulus must be mostly dark all round.
            dark_frac = float((l[y0:y1, x0:x1][ring] < bg_t).mean())
            if dark_frac >= 0.65:
                keep |= lab == i
    return keep


def count_field_stars(l, alpha, env, slug):
    """Count field-star-like detections OUTSIDE the envelope (the P589c gate:
    must be 0 per kept object)."""
    m = field_star_mask(l, alpha, env, slug, include_alpha=True)
    lab, n = ndimage.label(m, structure=np.ones((3, 3), bool))
    return n
