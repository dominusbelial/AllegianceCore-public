#!/usr/bin/env python3
"""Validate training primitive/source inventory against an installed native tree."""
import argparse
import csv
import os
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]


def audit(native):
    rows = list(csv.DictReader((ROOT / 'docs/parity/training-matrix.csv').open(newline='')))
    errors = []
    cited = set()
    for row in rows:
        for path, start, end in re.findall(r'([\w/]+\.(?:cpp|h|pl)):(\d+)(?:-(\d+))?', row['native_cite']):
            file = native / path
            if not file.is_file():
                errors.append(f"{row['row_id']}: no native file {path}")
                continue
            length = len(file.read_text(errors='replace').splitlines())
            if not 1 <= int(start) <= int(end or start) <= length:
                errors.append(f"{row['row_id']}: invalid native range {path}:{start}-{end} (length {length})")
            cited.add(path)
    # Every concrete/abstract primitive and macro has a rule, not just a filename.
    items = {row['native_item'] for row in rows}
    expected = set()
    for header in (native / 'training').glob('*.h'):
        if header.stem.lower().startswith(('mission', 'training', 'pch')):
            continue
        text = header.read_text(errors='replace')
        expected.update(re.findall(r'\bclass\s+(\w+)\s*(?::[^;{]+)?\s*\{', text))
        expected.update(re.findall(r'#define\s+(ElapsedTimeCondition|FalseCondition|NotCondition|GetShipIsDestroyedCondition)\b', text))
    errors += [f'missing primitive row {name}' for name in sorted(expected - items)]
    # Pair .h/.cpp implementations as one feature; pch is compile-time plumbing.
    for file in (native / 'training').glob('*'):
        if file.suffix.lower() not in ('.h', '.cpp') or file.stem == 'pch':
            continue
        if not any(Path(path).stem.lower() == file.stem.lower() for path in cited):
            errors.append(f'uninventoried native implementation/header pair {file.name}')
    for tool in ('training/asteroids/main.cpp', 'training/specification/format.pl'):
        if tool not in cited:
            errors.append(f'missing nested authoring tool {tool}')
    for path in sorted({match for row in rows for match in re.findall(r'(?:src|tools|tests)/[\w./-]+\.(?:cs|py)', row['port_cite_or_reason'])}):
        if not (ROOT / path).is_file():
            errors.append(f'invalid port path {path}')
    return rows, expected, errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', type=Path, default=None,
                        help='native src directory (or ALLEGIANCE_NATIVE_SRC_PATH repository)')
    args = parser.parse_args()
    native = args.native
    if native is None:
        configured = os.environ.get('ALLEGIANCE_NATIVE_SRC_PATH')
        if not configured:
            parser.error('supply --native or ALLEGIANCE_NATIVE_SRC_PATH')
        native = Path(configured) / 'src'
    rows, expected, errors = audit(native)
    for error in errors:
        print(error, file=sys.stderr)
    print(f'training audit: {len(rows)} rules; {len(expected)} primitive types/macros; {len(errors)} errors')
    return bool(errors)


if __name__ == '__main__':
    sys.exit(main())
