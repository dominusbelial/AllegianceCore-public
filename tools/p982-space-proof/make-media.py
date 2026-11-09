#!/usr/bin/env python3
"""Assemble production-renderer frames without changing their colour/exposure.
Requires Pillow for the contact sheets, and ffmpeg for the bounded WebM encodes.
Run after p982-space-proof's still and --clips modes.
"""
import argparse
from pathlib import Path
import subprocess
from PIL import Image, ImageDraw

parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path, default=Path('.scratch/p982'))
parser.add_argument('--output', type=Path, default=Path('docs/reports/P982_assets'))
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
modes = ['off', 'subtleDust', 'simStars', 'simGlare', 'starStreaks', 'all']
scenes = ['stationary', 'cruising', 'boosting', 'turning']
for tier in ['black', 'native', 'hd']:
    for style in ['classic', 'modern']:
        sheet = Image.new('RGB', (1920, 744), 'black')
        draw = ImageDraw.Draw(sheet)
        for x, mode in enumerate(modes):
            draw.text((x * 320 + 8, 5), mode, fill='white')
            for y, scene in enumerate(scenes):
                frame = Image.open(args.source / 'stills' / f'{tier}_{style}_{scene}_{mode}.png').convert('RGB')
                sheet.paste(frame.resize((320, 180), Image.Resampling.LANCZOS), (x * 320, 24 + y * 180))
        sheet.save(args.output / f'{tier}_{style}.webp', quality=88, method=6)
for scene in ['cruising', 'turning']:
    for mode in ['off', 'all']:
        subprocess.run(['timeout', '120', 'ffmpeg', '-y', '-v', 'error', '-framerate', '20',
                        '-i', str(args.source / 'clips' / f'{scene}_{mode}' / '%03d.png'),
                        '-an', '-c:v', 'libvpx-vp9', '-b:v', '0', '-crf', '35', '-deadline', 'good',
                        '-cpu-used', '4', '-pix_fmt', 'yuv420p', '-map_metadata', '-1',
                        str(args.output / f'{scene}_{mode}.webm')], check=True)
media = [p for p in args.output.iterdir() if p.suffix in {'.webp', '.webm', '.png'}]
assert all(p.stat().st_size <= 1_000_000 for p in media if p.suffix == '.webm')
assert sum(p.stat().st_size for p in media) <= 3_000_000
for file in media:
    print(f'{file.name}: {file.stat().st_size} bytes')
print(f'Total: {sum(p.stat().st_size for p in media)} bytes')
