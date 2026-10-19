#!/usr/bin/env python3
"""Compare P729 benchmark medians. Positive FPS deltas and negative ms deltas improve.
Exit 1 with --fail-percent N when any comparable metric regresses beyond N percent.
"""
import argparse
import json
import math
from pathlib import Path

METRICS = (
    ('FPS avg', 'FpsAverage', True), ('FPS 1% low', 'FpsOnePercentLow', True),
    ('Frame p50 ms', 'FrameMs.P50', False), ('Frame p99 ms', 'FrameMs.P99', False),
    ('CPU ms', 'CpuMs', False), ('GPU ms', 'GpuMs', False),
    ('Server mean ms', 'ServerTickMs.Mean', False), ('Server p95 ms', 'ServerTickMs.P95', False),
    ('Server worst ms', 'ServerTickMs.Worst', False), ('Memory bytes', 'WorkingSetBytes', False),
    ('GC pause ms', 'GcPauseMs', False), ('Bot CPU ms/bot/s', 'AmortizedCpuMsPerBotSecond', False),
)


def lookup(obj, path):
    for part in path.split('.'):
        obj = obj.get(part) if isinstance(obj, dict) else None
    return obj if isinstance(obj, (float, int)) and not isinstance(obj, bool) and math.isfinite(obj) else None


def compare(a, b, fail_percent=None, allow_mismatch=False):
    if a.get('SchemaVersion') != 1 or b.get('SchemaVersion') != 1:
        raise ValueError('Expected P729 schema version 1')
    keys = ('Mode', 'Preset', 'Seed', 'Map', 'Civs', 'DurationSeconds', 'ReplaySha256', 'Width', 'Height', 'TestedSettings')
    different = [key for key in keys if a.get(key) != b.get(key)]
    if a['Median'].get('Rendered') != b['Median'].get('Rendered'):
        different.append('Rendered')
    if different and not allow_mismatch:
        raise ValueError('Incompatible workload: ' + ', '.join(different))
    print('Metric                       A             B       Delta')
    failed = False
    for name, path, higher in METRICS:
        old, new = lookup(a['Median'], path), lookup(b['Median'], path)
        if old is None or new is None:
            print(f'{name:24} unavailable')
            continue
        delta = (new / old - 1) * 100 if old != 0 else None
        change = f'{delta:+.2f}%' if delta is not None else 'n/a (zero baseline)'
        print(f'{name:24} {old:12.3f} {new:12.3f} {change:>11}')
        if delta is not None and fail_percent is not None:
            failed |= (-delta if higher else delta) > fail_percent
    print(f"Bottleneck: {a.get('Bottleneck')} -> {b.get('Bottleneck')}")
    return int(failed)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('a', type=Path)
    parser.add_argument('b', type=Path)
    parser.add_argument('--fail-percent', type=float)
    parser.add_argument('--allow-mismatch', action='store_true')
    args = parser.parse_args()
    if args.fail_percent is not None and (not math.isfinite(args.fail_percent) or args.fail_percent < 0):
        parser.error('--fail-percent must be finite and nonnegative')
    try:
        return compare(json.loads(args.a.read_text()), json.loads(args.b.read_text()), args.fail_percent, args.allow_mismatch)
    except (OSError, ValueError, KeyError) as exc:
        parser.error(str(exc))


if __name__ == '__main__':
    raise SystemExit(main())
