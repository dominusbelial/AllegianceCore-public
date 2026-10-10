#!/usr/bin/env python3
"""Composite every processed sprite onto PURE BLACK (the in-game blend) as a
labelled contact sheet, then save. Deterministic."""
import os
from PIL import Image, ImageDraw, ImageFont

OBJ_DIR = os.path.join(os.environ.get("ALLEGIANCE_ARTWORK_PATH", ""), "p589obj")
OUT = os.path.join(os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")), "docs", "reports", "P589_assets", "sheets", "contact_objects.png")

SLUGS = [
    "helix-nebula", "whirlpool-m51", "ring-nebula-m57",
    "pinwheel-m101", "lion-nebula-miri", "lion-nebula-nasa",
]

CELL = 460          # cell width/height
LABEL_H = 46
COLS = 4

try:
    FONT = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 22)
except Exception:
    FONT = ImageFont.load_default()

rows = (len(SLUGS) + COLS - 1) // COLS
sheet_w = COLS * CELL
sheet_h = rows * (CELL + LABEL_H)
sheet = Image.new("RGB", (sheet_w, sheet_h), (0, 0, 0))
draw = ImageDraw.Draw(sheet)

for i, slug in enumerate(SLUGS):
    r, c = divmod(i, COLS)
    ox, oy = c * CELL, r * (CELL + LABEL_H)
    draw.rectangle([ox, oy, ox + CELL - 1, oy + CELL - 1], outline=(40, 40, 40), width=1)
    p = os.path.join(OBJ_DIR, slug + ".png")
    im = Image.open(p).convert("RGBA")
    bg = Image.new("RGBA", im.size, (0, 0, 0, 255))
    comp = Image.alpha_composite(bg, im).convert("RGB")
    comp.thumbnail((CELL - 12, CELL - 12), Image.LANCZOS)
    sheet.paste(comp, (ox + (CELL - comp.width) // 2, oy + (CELL - comp.height) // 2))
    draw.text((ox + 6, oy + CELL + 6), slug, fill=(230, 210, 130), font=FONT)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
sheet.save(OUT)
print("wrote", OUT, sheet.size)
