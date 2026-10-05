#!/usr/bin/env python3
"""Compose the per-cell PNGs produced by the P530 proof tool into labelled
contact sheets. Each object gets one 3-row x 3-col sheet:
  rows    = [OLD stock rig, NEW sector light (bright sector), NEW sector light (dark sector)]
  columns = [front, side, back]

Pure-PIL; run from the repo root with the cell PNGs in .verify_p530/.
"""
import os
from PIL import Image, ImageDraw

SRC = ".verify_p530"
CELL = 200
LABEL_W, LABEL_H = 96, 34
BG = (2, 3, 6)

ROW_LABELS = ["OLD", "NEW bright", "NEW dark"]
COL_LABELS = ["front", "side", "back"]

def compose(stem, title):
    width = LABEL_W + 3 * CELL + 10
    height = LABEL_H + 3 * CELL + 12
    sheet = Image.new("RGB", (width, height), BG)
    d = ImageDraw.Draw(sheet)
    d.text((6, 5), title, fill=(255, 200, 80))
    for row, rlab in enumerate(ROW_LABELS):
        d.text((8, LABEL_H + row * CELL + CELL // 2 - 6), rlab, fill=(255, 255, 255))
        for c in range(3):
            img = Image.open(os.path.join(SRC, f"{stem}_r{row}_c{c}.png"))
            sheet.paste(img, (LABEL_W + c * CELL + 4, LABEL_H + row * CELL + 5))
    for c, lab in enumerate(COL_LABELS):
        d.text((LABEL_W + c * CELL + 4 + CELL // 2 - len(lab) * 3, LABEL_H - 22), lab, fill=(255, 255, 255))
    out = os.path.join(SRC, f"{stem}_sheet.png")
    sheet.save(out)
    print(f"composed {out} ({sheet.size[0]}x{sheet.size[1]})")

compose("station", "STATION (procedural): OLD stock rig vs NEW sector light (bright/dark)")
compose("missile", "MISSILE (real .mdl): OLD stock rig vs NEW sector light (bright/dark)")
compose("probe", "PROBE (real .mdl): OLD stock rig vs NEW sector light (bright/dark)")
for asset in ("bios-bomber", "bios-carrier", "bios-fighter"):
    compose(f"ship_{asset}", f"SHIP {asset} (real Meshy model): OLD stock rig vs NEW sector light (bright/dark)")
