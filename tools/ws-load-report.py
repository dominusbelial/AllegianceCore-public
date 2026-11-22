#!/usr/bin/env python3
"""Read P1072 server/bot records; stdlib only. Inputs must cover the same window."""
import argparse
from collections import Counter
import json
import math
import re


def percentile(values, p):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * p) - 1)]


def histogram_percentile(histogram, p):
    target = math.ceil(sum(histogram.values()) * p)
    count = 0
    for value, n in sorted(histogram.items()):
        count += n
        if count >= target:
            return value / 1000
    return None


def report(lines, window_only=False):
    rows = {p: dict(sends=0, bytes=0, drops=0, droppedBytes=0,
                   queueBytesHighWater=0, queueEntriesHighWater=0,
                   disconnects=Counter(), logon=[], age=[], reconnects=0, bots=set())
            for p in ('tcp', 'ws', 'udp')}
    in_window = not window_only
    saw_window = False
    tick_histogram = Counter()
    samples = 0
    for line in lines:
        if '[comparison-window] start' in line:
            in_window = True
            saw_window = True
            continue
        if '[comparison-window] end' in line:
            in_window = not window_only
            continue
        if '[transport-egress] ' in line and in_window:
            fields = dict(re.findall(r'(\w+)=([^\s]*)', line.split('[transport-egress] ', 1)[1]))
            protocol = fields.get('protocol')
            if protocol not in rows:
                continue
            row = rows[protocol]
            try:
                numbers = {key: int(fields[key]) for key in
                           ('sends', 'bytes', 'drops', 'droppedBytes', 'queueBytesHighWater', 'queueEntriesHighWater')}
                disconnects = {k: int(v) for k, v in
                               (item.split(':') for item in fields.get('disconnects', '').split(',') if item)}
            except (ValueError, KeyError):
                raise ValueError('Malformed transport-egress record: ' + line.strip()) from None
            samples += 1
            for key, value in numbers.items():
                row[key] = max(row[key], value) if key.endswith('HighWater') else row[key] + value
            row['disconnects'].update(disconnects)
        elif '[bot-transport] ' in line:
            data = json.loads(line.split('[bot-transport] ', 1)[1])
            protocol = data['protocol']
            protocol = 'ws' if protocol == 'wss' else protocol
            if protocol not in rows:
                continue
            row = rows[protocol]
            row['bots'].add(data['name'])
            row['reconnects'] += data.get('reconnects', 0) if in_window else 0
            for key, source in [('logon', 'logonMs'), ('age', 'updateAgeMs')]:
                value = data.get(source)
                if value is not None and (in_window or key == 'logon'):
                    if not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                        raise ValueError('Invalid ' + source)
                    row[key].append(value)
        elif '[tick-quantiles] ' in line and in_window:
            match = re.search(r'histogramUs=([\d:,]+)', line)
            if match:
                tick_histogram.update({int(k): int(v) for k, v in
                                       (pair.split(':') for pair in match[1].split(','))})
    if window_only and not saw_window:
        raise ValueError('No comparison-window markers found')
    result = {'serverRecords': samples, 'tickSamples': sum(tick_histogram.values()),
              'tickMs': {f'p{int(p * 100)}': histogram_percentile(tick_histogram, p)
                         for p in (.5, .95, .99)}, 'transports': {}}
    for protocol, row in rows.items():
        result['transports'][protocol] = {key: value for key, value in row.items()
                                         if key not in ('logon', 'age', 'bots', 'disconnects')}
        result['transports'][protocol].update(
            bots=len(row['bots']), disconnects=dict(sorted(row['disconnects'].items())),
            logonSamples=len(row['logon']), updateSamples=len(row['age']),
            logonMs={f'p{int(p * 100)}': percentile(row['logon'], p) for p in (.5, .95, .99)},
            updateAgeMs={f'p{int(p * 100)}': percentile(row['age'], p) for p in (.5, .95, .99)})
    return result


def table(result):
    def display(value):
        return 'n/a' if value is None else f'{value:.3f}' if isinstance(value, float) else str(value)
    metrics = [('bots', lambda r: r['bots']), ('sends (frames)', lambda r: r['sends']),
               ('bytes (carrier)', lambda r: r['bytes']), ('drops (frames)', lambda r: r['drops']),
               ('dropped bytes', lambda r: r['droppedBytes']),
               ('queue bytes high-water', lambda r: r['queueBytesHighWater']),
               ('queue entries high-water', lambda r: r['queueEntriesHighWater']),
               ('logon p50 ms', lambda r: r['logonMs']['p50']),
               ('update age p50 ms', lambda r: r['updateAgeMs']['p50']),
               ('update age p95 ms', lambda r: r['updateAgeMs']['p95']),
               ('update age p99 ms', lambda r: r['updateAgeMs']['p99']),
               ('reconnect attempts', lambda r: r['reconnects']),
               ('disconnect reasons', lambda r: json.dumps(r['disconnects'], sort_keys=True))]
    output = ['| Metric | TCP | WebSocket |', '|---|---:|---:|']
    for label, get in metrics:
        output.append('| ' + label + ' | ' + ' | '.join(display(get(result['transports'][p])) for p in ('tcp', 'ws')) + ' |')
    output.append('Server tick ms (shared across transports): ' + ', '.join(
        key + '=' + display(value) for key, value in result['tickMs'].items()) +
        f"; samples={result['tickSamples']}")
    return '\n'.join(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('logs', nargs='+', help='Server stdout and bot/worker stderr logs')
    parser.add_argument('--json', action='store_true', help='Print machine-readable JSON')
    parser.add_argument('--window-only', action='store_true', help='Use comparison-window boundaries; retain initial logon measurements')
    args = parser.parse_args()
    def lines():
        for path in args.logs:
            with open(path, encoding='utf-8', errors='replace') as stream:
                yield from stream
    result = report(lines(), args.window_only)
    print(json.dumps(result, indent=2, sort_keys=True) if args.json else table(result))


if __name__ == '__main__':
    main()
