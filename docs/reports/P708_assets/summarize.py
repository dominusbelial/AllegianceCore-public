#!/usr/bin/env python3
import collections, gzip, hashlib, json, re
import xml.etree.ElementTree as ET
from pathlib import Path
OUT = Path(__file__).resolve().parent
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = json.loads((OUT / 'verification.json').read_text())
summary = {'invocations': len(results), 'failed_invocations': [r for r in results if r['exit']], 'groups': {}, 'parking': []}
groups = {
    'focused_preliminary': lambda name: name.startswith('focus-') and not name.startswith('focus-final-'),
    'focused_final': lambda name: name.startswith('focus-final-'),
    'parking_preliminary': lambda name: re.fullmatch(r'parking-\d+', name) is not None,
    'parking_final': lambda name: name.startswith('parking-final-'),
    'parking_retries': lambda name: name.startswith('parking-retry-'),
    'entry_baseline': lambda name: name.startswith('entry-baseline-'),
    'suites': lambda name: name.startswith('suite-') and not name.startswith('suite-retry-'),
}
for label, predicate in groups.items():
    rows = [r for r in results if predicate(r['name'])]
    summary['groups'][label] = {'runs': len(rows), 'passes': sum(r['exit'] == 0 for r in rows),
        'passed_cases': sum(int(r.get('counters', {}).get('passed', 0)) for r in rows),
        'skipped_cases': sum(int(r.get('observed_skipped', max(int(r.get('counters', {}).get('notExecuted', 0)), int(r.get('counters', {}).get('total', 0)) - int(r.get('counters', {}).get('executed', 0))))) for r in rows)}
for r in results:
    if not r['name'].startswith('parking-'): continue
    tree = ET.fromstring(gzip.open(OUT / r['evidence'], 'rb').read())
    for stdout in tree.findall('.//t:StdOut', NS):
        for line in (stdout.text or '').splitlines():
            if line.startswith('P700 local 15v15:'):
                summary['parking'].append({'run': r['name'], 'outcome': r['exit'], 'metrics': line})
repair_path = OUT / 'repair-verification.json'
if repair_path.exists():
    repair = json.loads(repair_path.read_text())
    summary['groups']['repair_final'] = {'runs': len(repair), 'passes': sum(r['exit'] == 0 for r in repair),
        'passed_cases': sum(int(r['counters']['passed']) for r in repair),
        'skipped_cases': sum(int(r['counters']['notExecuted']) for r in repair)}
    main = summary['groups']['focused_final']
    summary['groups']['combined_affected'] = {k: main[k] + summary['groups']['repair_final'][k] for k in main}
manifest = json.loads((OUT / 'source-manifest.json').read_text())
root = OUT.parents[2]
summary['verified_source_hashes_unchanged'] = all(hashlib.sha256((root / p).read_bytes()).hexdigest() == h for p, h in manifest['source'].items())
(OUT / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
