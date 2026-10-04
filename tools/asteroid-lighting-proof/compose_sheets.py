#!/usr/bin/env python3
"""Compose the per-cell PNGs produced by the P529 proof tool into labelled
contact sheets. Two sheets per resource type:
  spin_<type>.png  2 rows (OLD stock / NEW sector) x 6 columns (0..300 deg)
  dist_<type>.png  2 rows (OLD stock / NEW sector) x 2 columns (close / far)

Pure-PIL; run from the repo root with the cell PNGs in .verify_p529/.
"""
import glob, os
from PIL import Image, ImageDraw

SRC = ".verify_p529"
CELL = 160
LABEL_W, LABEL_H = 74, 30
BG = (2, 3, 6)

def compose(prefix, ncols, col_labels, title):
    for t in ("Carbon", "Helium"):
        width = LABEL_W + ncols * CELL + 8
        height = LABEL_H + 2 * CELL + 10
        sheet = Image.new("RGB", (width, height), BG)
        d = ImageDraw.Draw(sheet)
        d.text((6, 4), f"{title} :: {t}", fill=(255, 200, 80))
        for row, rlab in enumerate(("OLD", "NEW")):
            d.text((8, LABEL_H + row * CELL + CELL // 2 - 6), rlab, fill=(255, 255, 255))
            for c in range(ncols):
                img = Image.open(os.path.join(SRC, f"{prefix}_{t}_r{row}_c{c}.png"))
                sheet.paste(img, (LABEL_W + c * CELL + 4, LABEL_H + row * CELL + 5))
        for c, lab in enumerate(col_labels):
            d.text((LABEL_W + c * CELL + 4 + CELL // 2 - len(lab) * 3, LABEL_H - 20), lab, fill=(255, 255, 255))
        out = os.path.join(SRC, f"{prefix}_{t}_sheet.png")
        sheet.save(out)
        print(f"composed {out} ({sheet.size[0]}x{sheet.size[1]})")

compose("spin", 6, ["0deg", "60", "120", "180", "240", "300"],
        "SPIN close-up: OLD stock rig vs NEW sector lighting")
compose("dist", 2, ["close", "far"],
        "DISTANCE: OLD stock rig vs NEW sector lighting (mipmapped)")
