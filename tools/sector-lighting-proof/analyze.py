#!/usr/bin/env python3
"""Analyse the P530 proof cell PNGs (in .verify_p530/) and print the numbers
the report cites:
  - gold-specular flash pixels on each row (present under the OLD stock rig's
    warm specular key light, expected ~gone under NEW sector lighting), and
  - object readability on the NEW rows (mean object luminance + % crushed to
    near-black), so we can judge whether ships/stations/missiles/probes stay
    readable in dim sectors without the P355 headlight / P337 ambient lift.

Pure-PIL; run from the repo root.
"""
import os
import numpy as np
from PIL import Image

SRC = ".verify_p530"
BG = np.array([0.008, 0.012, 0.021]) * 255  # proof background
STEMS = ["station", "missile", "probe",
         "ship_bios-bomber", "ship_bios-carrier", "ship_bios-fighter"]
ROWS = ["OLD", "NEW bright", "NEW dark"]
COLS = ["front", "side", "back"]

def load(stem, row, col):
    return np.asarray(Image.open(os.path.join(SRC, f"{stem}_r{row}_c{col}.png")).convert("RGB")).astype(np.float32)

def metrics(arr):
    h, w, _ = arr.shape
    # object mask: any pixel notably different from the flat background
    dist = np.abs(arr - BG).sum(axis=2)
    obj = dist > 18.0
    if obj.sum() == 0:
        return dict(px=0, gold=0, mean_lum=0.0, dark_frac=0.0, blue_frac=0.0)
    r, g, b = arr[..., 0], arr[..., 1], arr[..., 2]
    lum = 0.299 * r + 0.587 * g + 0.114 * b
    # gold / warm specular flash: very bright AND warm (red clearly dominates blue)
    gold = obj & (lum > 200) & ((r - b) > 40)
    # blue back-light: cooler blue channel dominates (native alt light signature)
    blue = obj & ((b - r) > 25)
    dark = obj & (lum < 25)
    return dict(px=int(obj.sum()),
                gold=int(gold.sum()),
                mean_lum=float(lum[obj].mean()),
                dark_frac=float(dark.sum() / obj.sum()),
                blue_frac=float(blue.sum() / obj.sum()))

print(f"{'cell':42s} {'px':>6s} {'gold':>5s} {'meanLum':>8s} {'dark%':>6s} {'blue%':>6s}")
rows_out = []
for stem in STEMS:
    row_gold = {}
    for row in range(3):
        for col in range(3):
            m = metrics(load(stem, row, col))
            tag = f"{stem} {ROWS[row]:10s}/{COLS[col]}"
            rows_out.append((tag, stem, row, m))
            print(f"{tag:42s} {m['px']:6d} {m['gold']:5d} {m['mean_lum']:8.1f} {m['dark_frac']*100:5.1f}% {m['blue_frac']*100:5.1f}%")

print("\n-- aggregate per object / row (sum over the 3 views) --")
for stem in STEMS:
    agg = {}
    for row in range(3):
        tot_gold = sum(m['gold'] for (t, s, rr, m) in rows_out if s == stem and rr == row)
        agg[ROWS[row]] = tot_gold
    print(f"{stem:20s} gold-specular pixels  OLD={agg['OLD']:4d}  NEWbright={agg['NEW bright']:4d}  NEWdark={agg['NEW dark']:4d}")
