#!/usr/bin/env python3
"""Regression check for native eject-panel text being overprinted.

Usage: python3 verify-pod-captures.py <real post-eject PNG> [...]
Requires Pillow. Use original backbuffer PNGs, not compressed report JPEGs.
The classic panel's upper half contains yellow native instructions. A large
white fallback title over that area is a failure. This also rejects a missing
panel, so an empty black screenshot cannot pass the no-overprint check.
"""
import json
import sys
from pathlib import Path

from PIL import Image


def check(path):
    with Image.open(path) as source:
        image = source.convert("RGB")
    width, height = image.size
    if (width, height) not in ((1920, 1080), (2560, 1440)):
        raise ValueError(f"Unexpected backbuffer resolution: {image.size}")
    panel_height = height * 0.075
    panel_width = panel_height * 172 / 44  # native conejectpoddisplaybmp
    left = int((width - panel_width) / 2)
    top = int(height * 0.855)
    band = image.crop((left, top, int(left + panel_width), int(top + panel_height / 2)))
    pixels = band.load()
    white = yellow = 0
    for y in range(band.height):
        for x in range(band.width):
            red, green, blue = pixels[x, y]
            white += red > 170 and green > 170 and blue > 170
            yellow += red > 140 and green > 110 and blue < 80
    return {
        "source": str(Path(path).resolve()), "width": width, "height": height,
        "white_title_pixels": white, "native_yellow_pixels": yellow,
        "passed": white < 32 and yellow > 100,
    }


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit("Supply at least one real post-eject PNG.")
    results = [check(path) for path in sys.argv[1:]]
    print(json.dumps(results, indent=2))
    sys.exit(0 if all(result["passed"] for result in results) else 1)
