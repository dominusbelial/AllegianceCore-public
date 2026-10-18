"""Summarize actual TRX results; incomplete gates cannot be certified."""
import hashlib, json, math, re, xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records = json.loads((OUT / 'verification.json').read_text())
selected = json.loads((OUT / 'focused-methods.json').read_text())
by_label = {r['label']: r for r in records}
assert len(by_label) == len(records)
cases = []
metrics = []
for row in records:
    path = OUT / (row['label'] + '.trx')
    if not path.exists(): continue
    doc = ET.parse(path)
    methods = {}
    for unit in doc.findall('.//t:UnitTest', NS):
        method = unit.find('t:TestMethod', NS)
        methods[unit.attrib['id']] = method.attrib['className'].split(',')[0] + '.' + method.attrib['name']
    for result in doc.findall('.//t:UnitTestResult', NS):
        cases.append(dict(invocation=row['label'], method=methods[result.attrib['testId']],
                          name=result.attrib['testName'], outcome=result.attrib['outcome'],
                          duration=result.attrib.get('duration')))
    if row['label'].startswith('showcase-'):
        matches = re.findall(r'P700 local 15v15: ([\d.]+)s, samples=(\d+), reached=(\d+)/30, longest speed<1 with target=([\d.]+)s, shots=(\d+), hits=(\d+), kills=(\d+)', ''.join(doc.getroot().itertext()))
        for values in matches:
            metrics.append(dict(invocation=row['label'], seconds=float(values[0]), samples=int(values[1]), reached=int(values[2]), longestIdleSeconds=float(values[3]), shots=int(values[4]), hits=int(values[5]), kills=int(values[6])))
(OUT / 'case-results.json').write_text(json.dumps(cases, indent=2) + '\n')
(OUT / 'never-parks-metrics.json').write_text(json.dumps(metrics, indent=2) + '\n')
measurements = []
for path in sorted(OUT.glob('proof-focused-*/scale-*.json')):
    match = re.fullmatch(r'scale-([\d.]+)-(\d+)s.json', path.name)
    if match is None: continue
    telemetry = json.loads(path.read_text())
    measurements.append(dict(proof=str(path.relative_to(OUT)), scale=float(match[1]), simulation_seconds=int(match[2]),
                             completed_steps=[math.floor(r['ProgressPercent'] * r['TotalSteps'] / 100 + 1e-6) for r in telemetry]))
(OUT / 'research-measurements.json').write_text(json.dumps(measurements, indent=2) + '\n')

def stats(rows):
    return dict(invocations=len(rows), seconds=round(sum(r['seconds'] for r in rows), 3),
                **{key: sum(int(r.get('counters', {}).get(key, 0)) for r in rows) for key in ('passed', 'failed')},
                # VSTest's aggregate notExecuted can be zero for xUnit skips;
                # the actual per-case TRX outcomes are authoritative.
                notExecuted=sum(len(r.get('skips', [])) for r in rows))

focused = [r for r in records if r['label'].startswith('focused-')]
required = [f'focused-{index:03}-{repeat}' for index in range(len(selected)) for repeat in range(1, 4)]
required += [f'showcase-{repeat}' for repeat in range(1, 6)]
required += ['suite-' + project for project in ('Core', 'ClientCore', 'ClientCore.MonoGame')]
missing = [label for label in required if label not in by_label]
bad = [r['label'] for r in records if r['exit_code'] or 'counters' not in r or int(r['counters']['executed']) == 0]
fingerprints = json.loads((OUT / 'source-fingerprints.json').read_text())
changed = [name for name, digest in fingerprints.items() if hashlib.sha256((ROOT / name).read_bytes()).hexdigest() != digest]
summary = dict(focused_methods=len(selected), focused=stats(focused), showcase=stats([r for r in records if r['label'].startswith('showcase-')]),
               suites={r['label']: r for r in records if r['label'].startswith('suite-')}, total=stats(records),
               missing_invocations=missing, unsuccessful_invocations=bad, source_changes_since_build=changed,
               all_outcomes=Counter(c['outcome'] for c in cases))
(OUT / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps({k: v for k, v in summary.items() if k not in ('suites', 'missing_invocations')}, indent=2))
print('Missing invocations:', len(missing))
