#!/usr/bin/env python3
"""P1063 evidence sheets (stdlib only; this lane has no imaging package).

  p1063-pair-images.py OUT.png --height H IMAGE [IMAGE ...] [--crop X,Y,W,H] [--columns N]

Places the images left to right (pairs: browser | desktop), each scaled to the same height
with an area-average (box) filter, separated by a 4 px white divider; --columns N wraps
into rows of N (frame strips and contact sheets). With --crop the same
rectangle is cut from every input first (given as fractions 0..1 of each image, so inputs of
different sizes are cut alike). No other pixel processing. 8-bit RGB/RGBA non-interlaced PNG.
"""
import argparse
import struct
import zlib
from pathlib import Path


def read_png(path):
    data = Path(path).read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', path
    pos, packed = 8, bytearray()
    while pos < len(data):
        length, kind = struct.unpack('>I4s', data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if kind == b'IHDR':
            width, height, bits, colour, _, _, interlace = struct.unpack('>IIBBBBB', chunk)
            assert bits == 8 and colour in (2, 6) and interlace == 0, path
        if kind == b'IDAT':
            packed.extend(chunk)
        pos += length + 12
    channels = 4 if colour == 6 else 3
    stride = width * channels
    raw, previous, rows = zlib.decompress(bytes(packed)), bytearray(stride), []
    for y in range(height):
        mode = raw[y * (stride + 1)]
        row = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        if mode == 1:
            for x in range(channels, stride):
                row[x] = (row[x] + row[x - channels]) & 255
        elif mode == 2:
            for x in range(stride):
                row[x] = (row[x] + previous[x]) & 255
        elif mode == 3:
            for x in range(stride):
                left = row[x - channels] if x >= channels else 0
                row[x] = (row[x] + ((left + previous[x]) >> 1)) & 255
        elif mode == 4:
            for x in range(stride):
                a = row[x - channels] if x >= channels else 0
                b = previous[x]
                c = previous[x - channels] if x >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                row[x] = (row[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        previous = row
        rows.append(bytes(row[i] for x in range(width) for i in (x * channels, x * channels + 1, x * channels + 2))
                    if channels == 4 else bytes(row))
    return width, height, rows


def crop(image, rect):
    width, height, rows = image
    x, y, w, h = (int(round(rect[0] * width)), int(round(rect[1] * height)),
                  int(round(rect[2] * width)), int(round(rect[3] * height)))
    return w, h, [row[x * 3:(x + w) * 3] for row in rows[y:y + h]]


def scale(image, target_height):
    width, height, rows = image
    if height == target_height:
        return image
    target_width = max(1, round(width * target_height / height))
    xs = [(i * width // target_width, max(i * width // target_width + 1, (i + 1) * width // target_width)) for i in range(target_width)]
    out = []
    for j in range(target_height):
        y0 = j * height // target_height
        y1 = max(y0 + 1, (j + 1) * height // target_height)
        sums = [0] * (width * 3)
        for y in range(y0, y1):
            row = rows[y]
            for i in range(width * 3):
                sums[i] += row[i]
        line = bytearray()
        for x0, x1 in xs:
            count = (x1 - x0) * (y1 - y0)
            for c in range(3):
                line.append(sum(sums[x * 3 + c] for x in range(x0, x1)) // count)
        out.append(bytes(line))
    return target_width, target_height, out


def write_png(path, width, height, rows):
    def chunk(kind, body):
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body))
    raw = b''.join(b'\x00' + row for row in rows)
    Path(path).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                           + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('out')
    parser.add_argument('images', nargs='+')
    parser.add_argument('--height', type=int, default=540)
    parser.add_argument('--crop')
    parser.add_argument('--columns', type=int, default=0)
    args = parser.parse_args()
    rect = [float(v) for v in args.crop.split(',')] if args.crop else None
    images = []
    for name in args.images:
        image = read_png(name)
        if rect:
            image = crop(image, rect)
        images.append(scale(image, args.height))
    divider = b'\xff' * 12
    columns = args.columns or len(images)
    width = max(sum(i[0] for i in images[k:k + columns]) + 4 * (len(images[k:k + columns]) - 1)
                for k in range(0, len(images), columns))
    rows = []
    for k in range(0, len(images), columns):
        if rows:
            rows.extend([b'\xff' * (width * 3)] * 4)
        for y in range(args.height):
            line = divider.join(image[2][y] for image in images[k:k + columns])
            rows.append(line + b'\x00' * (width * 3 - len(line)))
    write_png(args.out, width, len(rows), rows)
    print(args.out, Path(args.out).stat().st_size)


if __name__ == '__main__':
    main()
