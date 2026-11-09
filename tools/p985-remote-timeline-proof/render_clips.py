#!/usr/bin/env python3
"""Caption and encode P985's real NativeShipRenderer replay frames.

Inputs are produced by the C# proof's --render mode. This script does not alter
ship poses: it adds a truth reference cross, an error line and textual labels.
"""
from pathlib import Path
import argparse
import csv
import subprocess
from PIL import Image, ImageDraw, ImageFont, ImageEnhance

p = argparse.ArgumentParser()
p.add_argument('--frames', type=Path, required=True)
p.add_argument('--out', type=Path, required=True)
p.add_argument('--method', choices=['default-reckoner', 'p985-timeline'], required=True)
a = p.parse_args()
rows = list(csv.DictReader((a.frames / 'frames.tsv').open(), delimiter='\t'))
a.out.mkdir(parents=True, exist_ok=True)
font = ImageFont.load_default(size=14)
small = ImageFont.load_default(size=11)
on = a.method == 'p985-timeline'
title = 'P985 ON - server-timestamp timeline' if on else 'P985 OFF - default dead-reckoner'
colour = (90, 220, 255) if on else (255, 170, 90)
for i, row in enumerate(rows):
    source = a.frames / f'frame_{i:04d}.png'
    image = ImageEnhance.Brightness(Image.open(source).convert('RGB')).enhance(1.6)
    draw = ImageDraw.Draw(image, 'RGBA')
    draw.rectangle((0, 0, 640, 52), fill=(0, 0, 0, 205))
    draw.text((10, 6), title, font=font, fill=colour)
    draw.text((10, 29), '0-150 ms jitter | reorder | duplicates | 5% loss | 1 s stall at 7 s', font=small, fill=(220, 225, 235))
    px, py = float(row['poseX']), float(row['poseY'])
    tx, ty = float(row['truthX']), float(row['truthY'])
    # Green cross: truth at this method's effective render instant. The native
    # hull is at the replayed pose. The connector makes correction displacement
    # visible without changing either position.
    draw.line((tx - 6, ty, tx + 6, ty), fill=(70, 255, 130, 230), width=1)
    draw.line((tx, ty - 6, tx, ty + 6), fill=(70, 255, 130, 230), width=1)
    if (px - tx) ** 2 + (py - ty) ** 2 > 4:
        draw.line((tx, ty, px, py), fill=(255, 90, 90, 180), width=1)
    delay = float(row['delay']) * 1000
    draw.rectangle((0, 338, 640, 360), fill=(0, 0, 0, 190))
    label = f"t={float(row['time']):05.2f}s   added latency={delay:03.0f} ms   "
    label += 'interpolate' if row['interpolated'] == '1' else 'capped reckoner fallback'
    draw.text((10, 342), label, font=small, fill=(230, 230, 230))
    image.save(a.out / f'frame_{i:04d}.png', optimize=True)

output = a.out / ('remote-timeline-on.webm' if on else 'remote-timeline-off.webm')
subprocess.run([
    'ffmpeg', '-y', '-loglevel', 'error', '-framerate', '15',
    '-i', str(a.out / 'frame_%04d.png'), '-c:v', 'libvpx-vp9',
    '-crf', '38', '-b:v', '0', '-pix_fmt', 'yuv420p', str(output),
], check=True, timeout=120)
print(f'{output}: {output.stat().st_size} bytes, {len(rows)} frames')
