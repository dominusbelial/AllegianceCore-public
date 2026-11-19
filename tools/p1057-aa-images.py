#!/usr/bin/env python3
"""Lossless PNG packing and exact nearest-neighbour evidence crops (stdlib only).

Run after the 16 --aa-scene captures and p1057-browser-aa.cjs. No pixels in a
full capture are changed. Sheet crops use the same rectangles for every image.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zlib


def read_png(path):
    data = Path(path).read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    pos, compressed = 8, bytearray()
    while pos < len(data):
        length, kind = struct.unpack('>I4s', data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if kind == b'IHDR':
            width, height, bits, colour, _, _, interlace = struct.unpack('>IIBBBBB', chunk)
            assert bits == 8 and colour in (2, 6) and interlace == 0
        if kind == b'IDAT':
            compressed.extend(chunk)
        pos += length + 12
    channels = 4 if colour == 6 else 3
    stride = width * channels
    raw, previous, rgb = zlib.decompress(compressed), bytearray(stride), bytearray()
    for y in range(height):
        mode = raw[y * (stride + 1)]
        row = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        if mode:
            for x in range(stride):
                a = row[x - channels] if x >= channels else 0
                b = previous[x]
                c = previous[x - channels] if x >= channels else 0
                if mode == 1:
                    predict = a
                elif mode == 2:
                    predict = b
                elif mode == 3:
                    predict = (a + b) // 2
                else:
                    p = a + b - c
                    distances = (abs(p - a), abs(p - b), abs(p - c))
                    predict = (a, b, c)[distances.index(min(distances))]
                row[x] = (row[x] + predict) & 255
        if channels == 4:
            assert all(row[x] == 255 for x in range(3, stride, 4)), 'non-opaque evidence'
            for x in range(0, stride, 4):
                rgb.extend(row[x:x + 3])
        else:
            rgb.extend(row)
        previous = row
    return width, height, rgb


def write_png(path, width, height, rgb):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    stride = width * 3
    # Up filtering compresses the quiet sky and long smooth edges particularly well.
    raw, previous = bytearray(), bytes(stride)
    for y in range(height):
        row = rgb[y * stride:(y + 1) * stride]
        raw.append(2)
        raw.extend((a - b) & 255 for a, b in zip(row, previous))
        previous = row
    Path(path).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                           + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def recompress_png(source, destination):
    """Keep original filter bytes/colour type/alpha; only recompress IDAT."""
    data = Path(source).read_bytes()
    pos, chunks, compressed = 8, [], bytearray()
    while pos < len(data):
        length, kind = struct.unpack('>I4s', data[pos:pos + 8])
        block = data[pos:pos + length + 12]
        if kind == b'IDAT':
            compressed.extend(data[pos + 8:pos + 8 + length])
        elif kind != b'IEND':
            chunks.append(block)
        pos += length + 12
    packed = zlib.compress(zlib.decompress(compressed), 9)
    kind = b'IDAT'
    Path(destination).write_bytes(data[:8] + b''.join(chunks) + struct.pack('>I', len(packed)) + kind + packed
                                 + struct.pack('>I', zlib.crc32(kind + packed)) + b'\x00\x00\x00\x00IEND\xaeB`\x82')


def crop2(image, rectangle):
    width, _, rgb = image
    left, top, w, h = rectangle
    result = bytearray()
    for y in range(top, top + h):
        row = bytearray()
        for x in range(left, left + w):
            pixel = rgb[(y * width + x) * 3:(y * width + x) * 3 + 3]
            row.extend(pixel * 2)
        result.extend(row * 2)
    return w * 2, h * 2, result


def paste(destination, dw, source, x, y):
    w, h, rgb = source
    for row in range(h):
        start = ((y + row) * dw + x) * 3
        destination[start:start + w * 3] = rgb[row * w * 3:(row + 1) * w * 3]


FONT = {
    'O': ['01110', '10001', '10001', '10001', '10001', '10001', '01110'],
    'F': ['11111', '10000', '10000', '11110', '10000', '10000', '10000'],
    '2': ['01110', '10001', '00001', '00010', '00100', '01000', '11111'],
    '4': ['00010', '00110', '01010', '10010', '11111', '00010', '00010'],
    '8': ['01110', '10001', '10001', '01110', '10001', '10001', '01110'],
    'X': ['10001', '10001', '01010', '00100', '01010', '10001', '10001'],
    'W': ['10001', '10001', '10001', '10101', '10101', '11011', '10001'],
    'E': ['11111', '10000', '10000', '11110', '10000', '10000', '11111'],
    'B': ['11110', '10001', '10001', '11110', '10001', '10001', '11110'],
    'G': ['01110', '10001', '10000', '10111', '10001', '10001', '01110'],
    'L': ['10000', '10000', '10000', '10000', '10000', '10000', '11111'],
}


def label(rgb, width, text, x, y):
    for letter in text:
        for row, bits in enumerate(FONT.get(letter, ['00000'] * 7)):
            for col, bit in enumerate(bits):
                if bit == '1':
                    for dy in range(2):
                        for dx in range(2):
                            index = ((y + row * 2 + dy) * width + x + col * 2 + dx) * 3
                            rgb[index:index + 3] = b'\xe0\xe8\xf0'
        x += 12


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--scratch', type=Path, default=Path('.scratch/p1057'))
    parser.add_argument('--out', type=Path, default=Path('docs/reports/DESKTOP_ANTIALIAS_P1057_assets'))
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    rectangles = [(640, 470, 96, 80), (1020, 425, 96, 80), (860, 650, 96, 80)]
    metadata, captures = [], {}
    for style in ('classic', 'modern'):
        for post in ('off', 'on'):
            for samples in (0, 2, 4, 8):
                key = f'{style}-{post}-{samples}'
                source = args.scratch / 'captures' / key / 'scene.png'
                frame = read_png(source)
                write_png(args.out / f'{key}.png', *frame)
                # A vertical strip: ship silhouette, station structure, asteroid limb.
                strip = bytearray(192 * 480 * 3)
                for i, rectangle in enumerate(rectangles):
                    paste(strip, 192, crop2(frame, rectangle), 0, i * 160)
                write_png(args.out / f'{key}-edges2x.png', 192, 480, strip)
                captures[key] = (192, 480, strip)
                metadata.append({'file': key + '.png', 'rgbSha256': hashlib.sha256(frame[2]).hexdigest()})
    browser = read_png(args.scratch / 'browser/browser-kni-policy.png')
    write_png(args.out / 'browser-kni-policy.png', *browser)
    browser_strip = bytearray(192 * 480 * 3)
    for i, rectangle in enumerate(rectangles):
        paste(browser_strip, 192, crop2(browser, rectangle), 0, i * 160)
    write_png(args.out / 'browser-edges2x.png', 192, 480, browser_strip)
    for post in ('off', 'on'):
        width, height = 192 * 5, 508
        sheet = bytearray(bytes([15, 19, 25]) * width * height)
        for col, samples in enumerate((0, 2, 4, 8)):
            paste(sheet, width, captures[f'classic-{post}-{samples}'], col * 192, 28)
            label(sheet, width, 'OFF' if samples == 0 else f'{samples}X', col * 192 + 8, 7)
        paste(sheet, width, (192, 480, browser_strip), 768, 28)
        label(sheet, width, 'WEBGL OFF', 776, 7)
        write_png(args.out / f'comparison-post-{post}.png', width, height, sheet)
    before = read_png(args.scratch / 'scene-before/scene.png')
    after = read_png(args.scratch / 'captures/classic-off-0/scene.png')
    assert before == after, 'Off scene golden changed'
    browser_off = read_png(args.scratch / 'browser/browser-off.png')
    desktop_clean = read_png(args.scratch / 'captures/classic-off-0/scene-only.png')
    delta = sum(browser_off[2][i:i + 3] != desktop_clean[2][i:i + 3] for i in range(0, len(browser_off[2]), 3))
    for style in ('classic', 'modern'):
        source = args.scratch / f'ui-{style}-small/p979_visuals_{style}_off.png'
        if not source.is_file():
            source = args.scratch / f'ui-{style}-final/p979_visuals_{style}_off.png'
        if source.is_file():
            recompress_png(source, args.out / f'options-{style}.png')
    total = sum(p.stat().st_size for p in args.out.glob('*.png'))
    result = {'pngBytes': total, 'edgeRectangles': rectangles, 'edgeScale': 2, 'filter': 'nearest neighbour',
              'offSceneByteIdentical': (args.scratch / 'scene-before/scene.png').read_bytes() == (args.scratch / 'captures/classic-off-0/scene.png').read_bytes(),
              'browserOffVsDesktopOffChangedRgbPixels': delta, 'captures': metadata}
    (args.out / 'images.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
    assert total <= 2_500_000, f'PNG budget exceeded: {total}'


if __name__ == '__main__':
    main()
