#!/usr/bin/env python3
"""Compare original backbuffer PNGs and record CRC/dimension/hash evidence.

No third-party packages and no image modification. Usage:
  compare.py BEFORE_DIRECTORY AFTER_DIRECTORY OUTPUT_JSON
"""
import hashlib
import json
import struct
import sys
import zlib
from pathlib import Path


def read_png(path):
    data = path.read_bytes()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError(f'{path}: PNG signature')
    offset, compressed, info = 8, bytearray(), None
    while offset < len(data):
        size = struct.unpack_from('>I', data, offset)[0]
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + size]
        expected = struct.unpack_from('>I', data, offset + 8 + size)[0]
        if zlib.crc32(kind + payload) != expected:
            raise ValueError(f'{path}: {kind!r} CRC')
        if kind == b'IHDR':
            info = struct.unpack('>IIBBBBB', payload)
        if kind == b'IDAT':
            compressed.extend(payload)
        offset += 12 + size
    width, height, depth, color, _, _, interlace = info
    if depth != 8 or color not in (2, 6) or interlace:
        raise ValueError(f'{path}: unsupported backbuffer PNG format {info}')
    channels = 4 if color == 6 else 3
    stride = width * channels
    source = zlib.decompress(compressed)
    if len(source) != (stride + 1) * height:
        raise ValueError(f'{path}: decompressed size')
    pixels, previous = bytearray(), bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        mode = source[start]
        row = bytearray(source[start + 1:start + 1 + stride])
        for x in range(stride):
            left = row[x - channels] if x >= channels else 0
            up = previous[x]
            upper_left = previous[x - channels] if x >= channels else 0
            if mode == 1:
                predict = left
            elif mode == 2:
                predict = up
            elif mode == 3:
                predict = (left + up) // 2
            elif mode == 4:
                p = left + up - upper_left
                distances = [abs(p - v) for v in (left, up, upper_left)]
                predict = (left, up, upper_left)[distances.index(min(distances))]
            elif mode == 0:
                predict = 0
            else:
                raise ValueError(f'{path}: filter {mode}')
            row[x] = (row[x] + predict) & 255
        pixels.extend(row)
        previous = row
    return dict(width=width, height=height, channels=channels, bytes=len(data),
                sha256=hashlib.sha256(data).hexdigest(),
                pixel_sha256=hashlib.sha256(pixels).hexdigest(), crc='pass'), pixels


def main():
    before, after, output = map(Path, sys.argv[1:4])
    comparisons, captures = [], []
    for path in sorted(after.glob('*.png')):
        metadata, pixels = read_png(path)
        captures.append(dict(name=path.name, **metadata))
        baseline = before / path.name
        if baseline.exists():
            original, original_pixels = read_png(baseline)
            same = original_pixels == pixels
            comparisons.append(dict(name=path.name, identical=same,
                different_bytes=sum(a != b for a, b in zip(original_pixels, pixels)),
                before=original, after=metadata))
            print(path.name, 'IDENTICAL' if same else 'DIFFERENT')
    output.write_text(json.dumps(dict(comparisons=comparisons, captures=captures), indent=2) + '\n')
    if any(not row['identical'] for row in comparisons):
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
