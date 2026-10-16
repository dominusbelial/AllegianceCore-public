#!/usr/bin/env python3
"""Extract authoritative flight outcomes from portable TRX evidence."""
import collections, datetime, gzip, hashlib, json, pathlib, statistics, xml.etree.ElementTree as ET
P = pathlib.Path(__file__).resolve().parent
summary = []
for row in json.loads((P / 'final-focused-results.json').read_text()):
    if not row['filter'].startswith('Allegiance.Core.Tests.P699bPodLoadTests.Hundred_pods_per_side_'): continue
    path = P / row['trx']
    root = ET.fromstring(gzip.decompress(path.read_bytes()))
    stdout = '\n'.join(x.text or '' for x in root.findall('.//{*}StdOut'))
    trips = [json.loads(line[11:]) for line in stdout.splitlines() if line.startswith('[pod-trip] {')]
    trips = list({(r['Ship'], r['EjectAt']): r for r in trips}.values())
    seconds = sorted(r['Seconds'] for r in trips)
    wait = sorted(r['WaitedAtBay'] for r in trips)
    summary.append({'trx': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
        'method': row['filter'], 'trips': len(trips), 'outcomes': dict(collections.Counter(r['Outcome'] for r in trips)),
        'home_arrivals': sum(r['HomeAt'] is not None for r in trips),
        'bay_entries': sum(r['BayAt'] is not None for r in trips),
        'routes': dict(collections.Counter(' -> '.join(str(v['Sector']) for v in r['Route']) for r in trips)),
        'median_seconds': statistics.median(seconds), 'p95_seconds': seconds[int(.95 * len(seconds)) - 1],
        'max_seconds': seconds[-1], 'p95_waited_at_bay': wait[int(.95 * len(wait)) - 1],
        'summary': next(line for line in stdout.splitlines() if 'baseline=False' in line)})
(P / 'flight-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
for row in summary:
    print(row['summary'])
    print('nearest-rank p95', round(row['p95_seconds'], 2), 'max', round(row['max_seconds'], 2),
        'bay entries', row['bay_entries'], 'p95 bay wait', round(row['p95_waited_at_bay'], 2))
