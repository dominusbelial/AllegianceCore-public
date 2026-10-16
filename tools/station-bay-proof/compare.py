#!/usr/bin/env python3
"""Assemble real --inspect-stations captures into matched before/after sheets.
CRC repair is needed only for pre-P701 captures; image payloads never change.
"""
import argparse, csv, pathlib, struct, zlib
from PIL import Image, ImageDraw

p = argparse.ArgumentParser()
p.add_argument('--before', type=pathlib.Path, required=True)
p.add_argument('--after', type=pathlib.Path, required=True)
p.add_argument('--output', type=pathlib.Path, required=True)
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
rows = [r for r in csv.DictReader(open('docs/reports/P615_assets/stations.csv'))
        if 'arrison' in r['name'] or 'utpost' in r['name']]


def capture(root, angle, station):
    matches = list(root.glob(f'inspect_angle{angle}_{station}_*.png'))
    if len(matches) != 1:
        raise ValueError(f'Expected one screenshot: {root}, {angle}, {station}: {matches}')
    path = matches[0]
    data = bytearray(path.read_bytes())
    offset = 8
    while offset < len(data):
        length = struct.unpack_from('>I', data, offset)[0]
        struct.pack_into('>I', data, offset + 8 + length,
                         zlib.crc32(data[offset + 4:offset + 8 + length]))
        offset += length + 12
    # Re-encode checksum bytes in memory; preserve all pixel payloads.
    import io
    return Image.open(io.BytesIO(data)).convert('RGB'), path.name


manifest = []
contact = Image.new('RGB', (2560, 32 * 190 // 4), '#101218')
for number, row in enumerate(rows):
    station, model = row['station_type_id'], row['model']
    sheet = Image.new('RGB', (1536, 3 * 456), '#101218')
    draw = ImageDraw.Draw(sheet)
    # The pre-existing sweep changes yaw after drawing. Angles 1/2/3 thus
    # contain the settled yaw 0/90/180 frames in both runs. Angle 0 differs
    # when the baseline sweep advances from the preceding station; omit it.
    for index, angle in enumerate([1, 2, 3]):
        for side, root in enumerate([a.before, a.after]):
            shot, source = capture(root, angle, station)
            shot = shot.resize((768, 432), Image.Resampling.LANCZOS)
            sheet.paste(shot, (side * 768, index * 456 + 24))
            draw.text((side * 768 + 10, index * 456 + 6),
                      f'{"BEFORE" if side == 0 else "AFTER"} | {station}: {row["name"]} / {model} | yaw {(angle-1)*90}', fill='white')
            manifest.append([station, model, 'before' if side == 0 else 'after', (angle - 1) * 90, source])
    sheet.save(a.output / f'{int(station):03d}-{model}.png', optimize=True)
    # The full comparison remains in its per-type sheet; overview keeps a
    # before/after pair including the native model at yaw zero.
    pair = Image.new('RGB', (640, 190), '#101218')
    pd = ImageDraw.Draw(pair)
    for side, root in enumerate([a.before, a.after]):
        shot, _ = capture(root, 1, station)
        native = shot.crop((320, 240, 640, 510)).resize((216, 162), Image.Resampling.LANCZOS)
        pair.paste(native, (side * 320 + 50, 24))
        pd.text((side * 320 + 8, 5), f'{station} {model} {"before" if side == 0 else "after"}', fill='white')
    contact.paste(pair, ((number % 4) * 640, (number // 4) * 190))
contact.save(a.output.parent / 'garrison-outpost-overview.png', optimize=True)
with open(a.output.parent / 'captures.csv', 'w') as output:
    writer = csv.writer(output)
    writer.writerow(['station_type_id', 'model', 'version', 'yaw_degrees', 'source_filename'])
    writer.writerows(manifest)
print(f'{len(rows)} station types, {len(manifest)} matched real-backbuffer views')
