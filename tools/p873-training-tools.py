#!/usr/bin/env python3
"""Native training authoring utilities (asteroid table and narration comments)."""
import argparse
from pathlib import Path
import random
import re
import struct
import sys


def f32(value):
    return struct.unpack('f', struct.pack('f', value))[0]


def asteroids(count, seed):
    """training/asteroids/main.cpp:16-55; same formulas, portable seeded RNG.

    Native emits floor(sqrt(count)) * (count // floor(sqrt(count))) rows.
    Its Y coordinate uses the WIDTH multiplier too (:44); preserve that bug.
    Native seeds rand from time, so matching libc rand sequences is not promised.
    """
    if count < 1:
        raise ValueError('count must be positive (native divides by zero for zero)')
    rng = random.Random(seed)
    unit = lambda: f32(rng.random())
    width = int(f32(count ** 0.5))
    height = count // width
    multiplier = f32(10000.0 / width)
    for row in range(height):
        for column in range(width):
            radius = f32(f32(unit() * 200.0) + 150.0)
            mass = f32(radius * radius)
            hp = f32(radius * 5.0)
            signature = f32(radius / 10.0)
            rotation = f32(0.1 + f32(unit() * 0.15))
            helium = unit() < f32(0.1)
            ore = f32(1000.0 + f32(unit() * 10000.0))
            x = f32(f32(f32(unit() + column) * multiplier) - 5000.0)
            y = f32(f32(f32(unit() + row) * multiplier) - 5000.0)
            z = f32(f32(unit() * 2000.0) - 1000.0)
            # Native ostream defaults to six significant digits, with blank table fields.
            values = [1024, 1, row * width + column, ore, int(helium), int(hp), int(radius),
                      'bgrnd56' if helium else 'bgrnd03', '', 'meteoricon', mass, signature,
                      x, y, z] + [''] * 9 + [rotation, 'Name']
            yield '\t'.join(format(v, '.6g') if isinstance(v, float) else str(v) for v in values)


def format_comments(lines):
    """training/specification/format.pl:3-35, including first-comma splitting."""
    for line in lines:
        number, _, description = line.partition(',')
        number = re.sub(r'\s', '', number)
        if not number:
            continue
        yield '\t\t// ' + number
        description = description.rstrip('\r\n')
        if description.startswith('"') and description.endswith('"'):
            description = description[1:-1]
        description = '"' + description.replace('"', "'") + '"'
        while len(description) > 60:
            # Native loops forever on an unbroken >60-character word. Emit it intact.
            split = next((i for i in range(60, 0, -1) if description[i-1].isspace()), 0)
            if split == 0:
                break
            yield '\t\t// ' + description[:split]
            description = description[split:]
        yield '\t\t// ' + description
        yield ''


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    subs = parser.add_subparsers(dest='command', required=True)
    asteroid = subs.add_parser('asteroids')
    asteroid.add_argument('--count', '-count', type=int, required=True)
    asteroid.add_argument('--seed', type=int, required=True)
    formatter = subs.add_parser('format-comments')
    formatter.add_argument('csv', type=Path)
    args = parser.parse_args(argv)
    if args.command == 'asteroids':
        print(f'seed={args.seed}', file=sys.stderr)
        try:
            for line in asteroids(args.count, args.seed):
                print(line)
        except ValueError as error:
            parser.error(str(error))
    else:
        with args.csv.open(encoding='utf-8-sig') as source:
            for line in format_comments(source):
                print(line)
    return 0


if __name__ == '__main__':
    sys.exit(main())
