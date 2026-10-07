#!/usr/bin/env python3
"""Compose P564 readability contact sheets: for each object, rows = bright/dark
light, cols = front/side/back, with labels. Reads the individual cells written by
tools/readability-proof."""
import os
from PIL import Image, ImageDraw

SRC = os.path.join(os.path.dirname(__file__), "..", "..", "docs", "reports", "P564_assets")
CELL = 240
LABEL = 22

OBJECTS = ["ship_fighter", "ship_carrier", "station", "asteroid"]
VIEWS = ["front", "side", "back"]

def cell(obj, light, view):
    return Image.open(os.path.join(SRC, f"{obj}_{light}_{view}.png")).convert("RGB")

def compose():
    for obj in OBJECTS:
        rows, cols = 2, len(VIEWS)
        sheet = Image.new("RGB", (cols * CELL, LABEL + rows * CELL), (8, 12, 20))
        d = ImageDraw.Draw(sheet)
        for li, light in enumerate(["bright", "dark"]):
            y0 = LABEL + li * CELL
            d.text((6, y0 - LABEL + 5), f"{obj}  {light}", fill=(220, 220, 220))
            for ci, view in enumerate(VIEWS):
                sheet.paste(cell(obj, light, view), (ci * CELL, y0))
        out = os.path.join(SRC, f"{obj}_sheet.png")
        sheet.save(out)
        print("wrote", out)

if __name__ == "__main__":
    compose()
