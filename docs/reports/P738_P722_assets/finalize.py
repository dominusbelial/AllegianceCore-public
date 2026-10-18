#!/usr/bin/env python3
"""Validate completed P722 gates, retain raw evidence and summarize outcomes."""
from pathlib import Path
import datetime
import hashlib
import json
import re
import tarfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RAW = Path('/tmp/p738-p722-final-validation')
records = json.loads((OUT / 'verification.json').read_text())
selected = json.loads((OUT / 'focused-methods.json').read_text())
labels = {r['label'] for r in records}
assert len(labels) == len(records), 'Duplicate invocation labels'
for index, _ in enumerate(selected):
    for repeat in range(1, 4):
        label = f'focused-{index:03}-{repeat}'
        assert label in labels, label
        row = next(r for r in records if r['label'] == label)
        assert int(row.get('counters', {}).get('passed', 0)) > 0 and not row.get('skips'), label
for repeat in range(1, 6):
    assert f'showcase-{repeat}' in labels
for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
    assert 'suite-' + project in labels
for row in records:
    assert 'counters' in row and int(row['counters']['executed']) > 0, row['label']
fingerprints = json.loads((OUT / 'source-fingerprints.json').read_text())
assert all(hashlib.sha256((ROOT / path).read_bytes()).hexdigest() == digest
           for path, digest in fingerprints.items()), 'Sources changed during gates'
build = json.loads((OUT / 'build-final.json').read_text())
assert build['exit_code'] == build['warnings'] == build['errors'] == 0
assert all(hashlib.sha256((ROOT / path).read_bytes()).hexdigest() == digest
           for path, digest in build['assemblies'].items()), 'Binaries changed during gates'
meta = ROOT / '.git'
gitdir = (ROOT / meta.read_text().strip().split(': ', 1)[1]).resolve() if meta.is_file() else meta
assert all(hashlib.sha256((gitdir / path).read_bytes()).hexdigest() == digest for path, digest in
           json.loads((OUT / 'metadata-fingerprints.json').read_text()).items()), 'Merge metadata changed'
failed = {}
for row in records:
    if row['label'].startswith('post-isolate-'):
        continue
    for failure in row.get('failures', []):
        failed[failure['method']] = failure
classifications = []
for method, failure in sorted(failed.items()):
    triplet = [r for r in records if r['label'].startswith('post-isolate-')
               and r['command'][-1].startswith('FullyQualifiedName=' + method)]
    assert len(triplet) >= 3 and all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0
                                   for r in triplet), method + ' requires a fix or further qualification'
    classifications.append(dict(method=method, case=failure['name'], classification='load flake',
                                isolated_invocations=len(triplet), isolated_failed=0))
showcases = [r for r in records if r['label'].startswith('showcase-')]
assert all(r['exit_code'] == 0 and int(r['counters']['passed']) == 1 for r in showcases), 'Showcase failures require base-rate comparison'
summary = dict(task='P738 merge codex/p722 onto master',
               completed_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
               focused_methods=len(selected), focused_invocations=len(selected) * 3,
               full_suite_exclusions=['MeshyGuardTests', 'PrivatePathGuardTests (both invoke git)'],
               failure_classifications=classifications, source_and_binary_fingerprints_match=True,
               merge_metadata_unchanged=True, phases={})
for phase in ('focused', 'showcase', 'suite', 'post-isolate'):
    runs = [r for r in records if r['label'].startswith(phase + '-')]
    summary['phases'][phase] = dict(invocations=len(runs), exit_failures=sum(r['exit_code'] != 0 for r in runs),
        seconds=round(sum(r['seconds'] for r in runs), 3),
        passed=sum(int(r.get('counters', {}).get('passed', 0)) for r in runs),
        failed=sum(int(r.get('counters', {}).get('failed', 0)) for r in runs),
        skipped=sum(len(r.get('skips', [])) for r in runs))
summary['focused_by_assembly'] = {}
for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
    indices = {i for i, (p, _) in enumerate(selected) if p == project}
    runs = [r for r in records if r['label'].startswith('focused-') and int(r['label'].split('-')[1]) in indices]
    summary['focused_by_assembly'][project] = dict(methods=len(indices), invocations=len(runs),
        passed=sum(int(r['counters']['passed']) for r in runs), failed=sum(int(r['counters']['failed']) for r in runs),
        skipped=sum(len(r.get('skips', [])) for r in runs))
summary['suites'] = [{k: r[k] for k in ('label', 'exit_code', 'seconds', 'counters', 'failures', 'skips')}
                     for r in records if r['label'].startswith('suite-')]
metrics = []
for row in showcases:
    content = ''.join(ET.parse(RAW / (row['label'] + '.trx')).getroot().itertext())
    match = re.search(r'P700 local 15v15: ([\d.]+)s, samples=(\d+), reached=(\d+)/30, '
                      r'longest speed<1 with target=([\d.]+)s, shots=(\d+), hits=(\d+), kills=(\d+)', content)
    assert match, row['label'] + ' missing showcase metrics'
    seconds, samples, reached, longest, shots, hits, kills = match.groups()
    metrics.append(dict(invocation=row['label'], seconds=float(seconds), samples=int(samples),
                        reached=int(reached), longest_pause_seconds=float(longest), shots=int(shots),
                        hits=int(hits), kills=int(kills)))
summary['showcase_metrics'] = dict(minimum_reached=min(m['reached'] for m in metrics),
    longest_pause_seconds=max(m['longest_pause_seconds'] for m in metrics),
    minimum_kills=min(m['kills'] for m in metrics), maximum_kills=max(m['kills'] for m in metrics))
(OUT / 'showcase-metrics.json').write_text(json.dumps(metrics, indent=2) + '\n')
(OUT / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
cases = []
for row in records:
    for case in ET.parse(RAW / (row['label'] + '.trx')).findall('.//t:UnitTestResult', ns):
        cases.append(dict(invocation=row['label'], name=case.attrib['testName'], outcome=case.attrib['outcome'],
                          duration=case.attrib.get('duration')))
(OUT / 'case-results.json').write_text(json.dumps(cases, indent=2) + '\n')
with tarfile.open(OUT / 'validation-logs.tar.gz', 'w:gz') as archive:
    for path in sorted(RAW.rglob('*')):
        if path.is_file() and path.suffix in ('.log', '.trx'):
            archive.add(path, arcname=str(path.relative_to(RAW)))
manifest = {p.name: dict(bytes=p.stat().st_size, sha256=hashlib.sha256(p.read_bytes()).hexdigest())
            for p in OUT.iterdir() if p.is_file() and p.name != 'evidence-manifest.json'}
(OUT / 'evidence-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(summary, indent=2))
