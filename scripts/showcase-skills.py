#!/usr/bin/env python3
"""Read SC1 server telemetry, keeping the final block (or latest snapshot) per match/side."""
import argparse
import csv
import json
from pathlib import Path
import re

CATALOGUE = Path(__file__).resolve().parents[1] / 'docs/showcase/SKILLS.csv'
LINE = re.compile(r'\[skills\] match (\d+) side (-?\d+)(.*)')


def parse(lines, catalogue):
    rows, states = {}, {}
    for line in lines:
        found = LINE.search(line)
        if not found:
            continue
        match, side, tail = found.groups()
        key = (int(match), int(side))
        if tail.strip() in ('final', 'snapshot'):
            states[key] = tail.strip()
        elif tail.startswith(':'):
            # A later query must not overwrite a frozen final result.
            if key in rows and rows[key]['state'] == 'final':
                continue
            tokens = tail[1:].split()
            if len(tokens) % 2:
                raise ValueError(f'malformed counter pairs: {line.strip()}')
            counts = {tokens[i]: int(tokens[i + 1]) for i in range(0, len(tokens), 2)}
            if any(value < 0 for value in counts.values()):
                raise ValueError('negative counter')
            missing = [c['id'] for c in catalogue if c['id'] not in counts]
            gaps = [dict(id=c['id'], count=counts.get(c['id'], 0), minimum=int(c['minimum']))
                    for c in catalogue if counts.get(c['id'], 0) < int(c['minimum'])]
            rows[key] = dict(match=key[0], side=key[1], state=states.get(key, 'snapshot'), counts=counts,
                             covered=len(catalogue) - len(gaps), total=len(catalogue), gaps=gaps, missing=missing)
    return [rows[key] for key in sorted(rows)]


def markdown(rows):
    output = ['| Match | Side | State | Covered | Coverage |', '|---:|---:|---|---:|---:|']
    for row in rows:
        output.append(f"| {row['match']} | {row['side']} | {row['state']} | {row['covered']}/{row['total']} | {100 * row['covered'] / row['total']:.1f}% |")
    for row in rows:
        output.append(f"\nMatch {row['match']}, side {row['side']} — zero/under minimum: " +
                      ', '.join(f"{g['id']} {g['count']}/{g['minimum']}" for g in row['gaps']))
        if row['missing']:
            output.append('Missing counters (incomplete block): ' + ', '.join(row['missing']))
    return '\n'.join(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    parser.add_argument('--json', action='store_true', help='emit structured evidence instead of Markdown')
    args = parser.parse_args()
    with CATALOGUE.open(newline='') as stream:
        catalogue = list(csv.DictReader(stream))
    with args.log.open(errors='replace') as stream:
        rows = parse(stream, catalogue)
    if not rows:
        parser.error('no [skills] counter blocks found')
    print(json.dumps(rows, indent=2) if args.json else markdown(rows))


if __name__ == '__main__':
    main()
