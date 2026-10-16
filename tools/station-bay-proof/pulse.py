#!/usr/bin/env python3
"""Build blink proof from real station inspector captures at fixed light times.
Capture station 110 at distance 1000, native-only, four angles at each phase.
"""
import argparse, pathlib, json
from PIL import Image, ImageChops, ImageDraw
p = argparse.ArgumentParser()
p.add_argument('--captures', type=pathlib.Path, required=True)
p.add_argument('--output', type=pathlib.Path, required=True)
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
phases = [0, .025, .05, .075, .1, .125, .15, .5]
frames = [Image.open(next((a.captures / f'{t:.3f}' / 'screenshots').glob('inspect_angle3_110_*.png'))).convert('RGB') for t in phases]
ref = frames[0].crop((220, 180, 1060, 700))
metrics = []
for phase, frame in zip(phases, frames):
    difference = ImageChops.difference(ref, frame.crop((220, 180, 1060, 700)))
    pixels = difference.tobytes()
    changed = sum(bool(pixels[i] | pixels[i+1] | pixels[i+2]) for i in range(0, len(pixels), 3))
    metrics.append({'time': phase, 'changed_pixels_vs_t0': changed})
frames[4].save(a.output / 'ss27-lights-peak.png')
frames[6].save(a.output / 'ss27-lights-off.png')
gif = []
for phase, frame in zip(phases, frames):
    crop = frame.crop((220, 180, 1060, 700))
    ImageDraw.Draw(crop).text((10, 10), f'Native SS27 lights: t={phase:.3f} s', fill='white')
    gif.append(crop)
gif[0].save(a.output / 'ss27-bay-lights.gif', save_all=True, append_images=gif[1:],
            duration=[25,25,25,25,25,25,350,750], loop=0, optimize=False)
(a.output / 'light-pulse-pixels.json').write_text(json.dumps(metrics, indent=2) + '\n')
print(metrics)
