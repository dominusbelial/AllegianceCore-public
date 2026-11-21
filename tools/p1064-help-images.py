#!/usr/bin/env python3
"""Pack P1064's eight real-window help captures without changing decoded pixels.

Usage: python3 tools/p1064-help-images.py <capture-directory> <report-assets>
Uses the existing opaque PNG decoder from P1057. No resampling/quantization.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys
import zlib


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))


def main():
    source, destination = map(Path, sys.argv[1:])
    spec = importlib.util.spec_from_file_location('pngcodec', Path(__file__).with_name('p1057-aa-images.py'))
    codec = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(codec)
    destination.mkdir(parents=True, exist_ok=True)
    sources = sorted(source.glob('help-*.png'))
    assert len(sources) == 8, f'expected eight captures, got {len(sources)}'
    rows = []
    for path in sources:
        width, height, rgb = codec.read_png(path)
        stride = width * 3
        # Flat backgrounds and repeated bitmap glyphs compress best without
        # differencing rows. Alpha is proven opaque by the shared decoder.
        raw = b''.join(b'\0' + rgb[i:i + stride] for i in range(0, len(rgb), stride))
        packed = (b'\x89PNG\r\n\x1a\n'
                  + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                  + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))
        target = destination / path.name
        target.write_bytes(packed)
        assert (width, height, rgb) == codec.read_png(target), f'pixel delta: {path.name}'
        rows.append(dict(file=path.name, width=width, height=height,
                         rgbSha256=hashlib.sha256(rgb).hexdigest(), bytes=len(packed)))
    (destination / 'captures.json').write_text(json.dumps(rows, indent=2) + '\n')
    total = sum(path.stat().st_size for path in destination.iterdir() if path.is_file())
    assert total <= 1_500_000, f'asset budget exceeded: {total} bytes'
    print(f'{len(rows)} full captures; exact decoded RGB/opaque alpha; assets {total:,} bytes')


if __name__ == '__main__':
    main()
