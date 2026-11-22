#!/usr/bin/env python3
"""Aggregate completed sessions, by UTC start day and ISO week (stdlib only)."""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import glob
import json
import math
from pathlib import Path
import statistics
import sys


def stamp(value):
    parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None:
        raise ValueError('timestamps must include a UTC offset')
    return parsed.astimezone(timezone.utc)


def percentile(values, p):
    """Nearest-rank percentile, including the maximum for small samples."""
    return sorted(values)[max(0, math.ceil(len(values) * p) - 1)] if values else 0


def summarize(sessions, matches, history=None):
    names = {s['callsign'].casefold() for s in sessions}
    days = defaultdict(set)
    for s in sessions:
        days[s['callsign'].casefold()].add(stamp(s['start']).date())
    history = history if history is not None else days
    last_day = max((stamp(s['start']).date() for s in sessions), default=None)
    returning = sum(sum(day <= last_day for day in history[name]) >= 2 for name in names) if last_day else 0
    minutes = [s['seconds'] / 60 for s in sessions]
    return {
        'unique_callsigns': len(names),
        'guests': len({s['callsign'].casefold() for s in sessions if s['kind'] == 'guest'}),
        'accounts': len({s['callsign'].casefold() for s in sessions if s['kind'] == 'account'}),
        'sessions': len(sessions),
        'median_minutes': statistics.median(minutes) if minutes else 0,
        'p90_minutes': percentile(minutes, .9),
        'returning_players': returning,
        'browser': sum(s['client'] == 'browser' for s in sessions),
        'desktop': sum(s['client'] == 'desktop' for s in sessions),
        'unknown': sum(s['client'] == 'unknown' for s in sessions),
        'launched': sum(s['launched'] for s in sessions),
        'only_joined': sum(not s['launched'] for s in sessions),
        'matches_with_humans': sum(m['humans'] >= 1 for m in matches),
    }


def aggregate(records):
    buckets = {'days': defaultdict(lambda: ([], [])), 'weeks': defaultdict(lambda: ([], []))}
    sessions, matches = [], []
    for row in records:
        match = row.get('type') == 'match'
        start = stamp(row['start'])
        if match:
            if not isinstance(row['humans'], int) or row['humans'] < 0:
                raise ValueError('invalid human count')
            matches.append(row)
        else:
            if (not isinstance(row['callsign'], str) or not row['callsign']
                    or row['kind'] not in ('guest', 'account')
                    or row['client'] not in ('browser', 'desktop', 'unknown')
                    or not isinstance(row['launched'], bool)
                    or not isinstance(row['seconds'], (int, float))
                    or isinstance(row['seconds'], bool)
                    or not math.isfinite(row['seconds']) or row['seconds'] < 0):
                raise ValueError('invalid session')
            sessions.append(row)
        iso = start.isocalendar()
        for period, key in [('days', start.date().isoformat()), ('weeks', f'{iso.year}-W{iso.week:02d}')]:
            buckets[period][key][1 if match else 0].append(row)
    history = defaultdict(set)
    for s in sessions:
        history[s['callsign'].casefold()].add(stamp(s['start']).date())
    return {'total': summarize(sessions, matches, history), **{
        period: {key: summarize(*rows, history) for key, rows in sorted(groups.items())}
        for period, groups in buckets.items()
    }}


def read_records(paths):
    for path in paths:
        with path.open(encoding='utf-8') as stream:
            for number, line in enumerate(stream, 1):
                if not line.strip():
                    continue
                try:
                    row = json.loads(line)
                    # Validate here for a useful file/line error rather than a traceback.
                    aggregate([row])
                    yield row
                except (ValueError, KeyError, TypeError, AttributeError) as ex:
                    raise ValueError(f'{path}:{number}: invalid play record ({ex})') from ex


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('paths', nargs='+', help='JSONL files, directories, or glob patterns')
    parser.add_argument('--json', action='store_true', help='emit machine-readable JSON')
    args = parser.parse_args(argv)
    files = set()
    for value in args.paths:
        p = Path(value)
        found = sorted(p.glob('*.jsonl')) if p.is_dir() else [Path(x) for x in glob.glob(value)]
        if not found:
            parser.error(f'no JSONL files matched {value}')
        files.update(f.resolve() for f in found if f.is_file())
    try:
        report = aggregate(read_records(sorted(files)))
    except (OSError, ValueError) as ex:
        print(str(ex), file=sys.stderr)
        return 1
    if args.json:
        print(json.dumps(report, indent=2, sort_keys=True))
    else:
        print('UTC start dates; completed session segments; callsigns are not unique people.')
        print('Returning players: active callsigns seen on two dates by the end of the period (within supplied files).')
        print('ISO weeks use their ISO week-year; p90 uses nearest rank. Identity splits may overlap.')
        for period in ('days', 'weeks'):
            print(f'\n{period.upper()}')
            for key, row in report[period].items():
                print(key + ' ' + ' '.join(f'{k}={v:.2f}' if isinstance(v, float) else f'{k}={v}' for k, v in row.items()))
        print('\nTOTAL ' + json.dumps(report['total'], sort_keys=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())
