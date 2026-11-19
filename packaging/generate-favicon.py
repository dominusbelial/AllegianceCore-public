#!/usr/bin/env python3
"""Regenerate the browser favicon from allegiance.svg (no external renderer).

The SVG uses only a rounded background tile and two flat facets, so this
script rasterizes those exact shapes with 4x4 supersampling and writes a
32x32 RGBA PNG. Source artwork: packaging/allegiance.svg (the same file
packaging/generate-icon.py uses for the macOS icon).
"""
from pathlib import Path
import struct
import zlib

SIZE = 32
SUPERSAMPLE = 4
BACKGROUND = (0x0b, 0x16, 0x30)
FACET_LIGHT = (0x75, 0xd8, 0xff)
FACET_DARK = (0x23, 0x77, 0xab)
CORNER_RADIUS = 24.0


def inside_rounded_square(x: float, y: float) -> bool:
    """The SVG's <rect width="128" height="128" rx="24">."""
    radius = CORNER_RADIUS
    cx = min(max(x, radius), 128.0 - radius)
    cy = min(max(y, radius), 128.0 - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius


def inside_polygon(x: float, y: float, points: list[tuple[float, float]]) -> bool:
    """Even-odd point-in-polygon test for the SVG path outlines."""
    inside = False
    for index in range(len(points)):
        x1, y1 = points[index]
        x2, y2 = points[(index + 1) % len(points)]
        if (y1 > y) != (y2 > y):
            crossing = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if x < crossing:
                inside = not inside
    return inside


def render() -> bytes:
    # <path d="M64 14 108 108 64 86 20 108Z"> then
    # <path d="M64 38 64 86 44 96Z"> on top of it (source order).
    arrow = [(64.0, 14.0), (108.0, 108.0), (64.0, 86.0), (20.0, 108.0)]
    facet = [(64.0, 38.0), (64.0, 86.0), (44.0, 96.0)]
    scale = 128.0 / SIZE
    samples = SUPERSAMPLE * SUPERSAMPLE
    rows = []
    for row in range(SIZE):
        pixels = bytearray()
        for column in range(SIZE):
            red = green = blue = alpha = 0
            for srow in range(SUPERSAMPLE):
                for scolumn in range(SUPERSAMPLE):
                    x = (column + (scolumn + 0.5) / SUPERSAMPLE) * scale
                    y = (row + (srow + 0.5) / SUPERSAMPLE) * scale
                    if not inside_rounded_square(x, y):
                        continue
                    colour = BACKGROUND
                    if inside_polygon(x, y, arrow):
                        colour = FACET_LIGHT
                        if inside_polygon(x, y, facet):
                            colour = FACET_DARK
                    red += colour[0]
                    green += colour[1]
                    blue += colour[2]
                    alpha += 255
            pixels.extend((red // samples, green // samples, blue // samples, alpha // samples))
        rows.append(b'\x00' + bytes(pixels))
    return b''.join(rows)


def chunk(kind: bytes, payload: bytes) -> bytes:
    return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload) & 0xFFFFFFFF)


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    target = root / 'src/Allegiance.ClientCore.Browser/wwwroot/favicon.png'
    header = struct.pack('>IIBBBBB', SIZE, SIZE, 8, 6, 0, 0, 0)  # RGBA, no interlace
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'IDAT', zlib.compress(render(), 9)) + chunk(b'IEND', b'')
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(png)
    print(f'{target} {len(png)} bytes')


if __name__ == '__main__':
    main()
