#!/usr/bin/env python3
"""Check final P720 evidence against the source, binaries, and raw TRX files."""
import csv
import hashlib
import io
import json
import subprocess
from datetime import datetime
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
ASSETS = Path(__file__).resolve().parent
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
rows = json.loads((ASSETS / 'verification.json').read_text())
build = json.loads((ASSETS / 'certification-build.json').read_text())
fingerprints = json.loads((ASSETS / 'source-fingerprint.json').read_text())
assert sha(ASSETS / 'source-fingerprint.json') == build['source_fingerprint_sha256']
assert sha(ASSETS / 'build.log') == build['build_log_sha256']
assert '    0 Warning(s)' in (ASSETS / 'build.log').read_text()
assert '    0 Error(s)' in (ASSETS / 'build.log').read_text()
for name, expected in {**fingerprints, **build['test_binaries']}.items():
    assert sha(ROOT / name) == expected, name
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
build_time = datetime.fromisoformat(build['built_utc'].replace('Z', '+00:00'))
for row in rows:
    assert datetime.fromisoformat(row['started_utc'].replace('Z', '+00:00')) >= build_time
    trx = ROOT / row['trx']
    assert sha(trx) == row['trx_sha256'], row['label']
    assert ET.parse(trx).find('.//t:Counters', ns).attrib == row['counters']
    assert (ROOT / row['log']).exists()
cohort = json.loads((ASSETS / 'affected-methods.json').read_text())
certificate = json.loads((ASSETS / 'affected-certificate.json').read_text())
assert len(cohort) == len(certificate) == 78
for (project, method), entry in zip(cohort, certificate):
    selected = [r for r in rows if r['project'] == project and r['filter'] == 'FullyQualifiedName=' + method
                and r['label'].startswith('affected-')]
    assert len(selected) == 3 and all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0
                                    and int(r['counters']['failed']) == int(r['counters']['notExecuted']) == 0 for r in selected), method
    assert entry['project'] == project and entry['method'] == method
    assert [rows[i - 1] for i in entry['invocations']] == selected
    assert entry['case_passes'] == sum(int(r['counters']['passed']) for r in selected)
parking = [r for r in rows if r['label'].startswith('showcase-')]
assert [r['label'] for r in parking] == [f'showcase-{i}' for i in range(1, 11)]
assert all(r['exit_code'] == 0 and r['counters']['passed'] == '1' for r in parking)
suites = [r for r in rows if r['label'].startswith('suite-')]
assert [r['label'] for r in suites] == [f'suite-{i}-{p}' for i in (1, 2)
                                      for p in ('Core', 'ClientCore', 'ClientCore.MonoGame', 'Lobby')]
for row in suites:
    xml = ET.parse(ROOT / row['trx'])
    methods = {u.attrib['id']: m.attrib['className'].split(',')[0] + '.' + m.attrib['name']
               for u in xml.findall('.//t:UnitTest', ns)
               if (m := u.find('t:TestMethod', ns)) is not None}
    failed_methods = {methods[u.attrib['testId']] for u in xml.findall('.//t:UnitTestResult', ns)
                      if u.attrib['outcome'] == 'Failed'}
    assert row['exit_code'] == bool(failed_methods), row['label']
    for method in failed_methods:
        retries = [r for r in rows if r['project'] == row['project'] and r['label'].startswith('retry-')
                   and r['filter'] == 'FullyQualifiedName=' + method]
        assert len(retries) == 3 and all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0 for r in retries)
skips = []
for row in suites:
    omitted = [u.attrib['testName'] for u in ET.parse(ROOT / row['trx']).findall('.//t:UnitTestResult', ns)
               if u.attrib['outcome'] == 'NotExecuted']
    expected = {'Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.' + name for name in
                ('Pack_built_content_directory_exists', 'Pack_catalogue_merge_yields_identical_resolution_to_before',
                 'Pack_catalogue_has_the_full_meshy_set')} if row['project'] == 'ClientCore.MonoGame' else set()
    assert set(omitted) == expected, row['label']
    skips.extend(omitted)
base = subprocess.check_output(['git', 'show', 'bae444b0:docs/parity/parity-matrix.csv'], cwd=ROOT).decode()
path = ROOT / 'docs/parity/parity-matrix.csv'
assert b'\r' not in path.read_bytes()
before = list(csv.DictReader(io.StringIO(base)))
after = list(csv.DictReader(io.StringIO(path.read_text())))
assert len(before) == len(after) == 438
changes = [(a, b) for a, b in zip(before, after) if a != b]
assert len(changes) == 14
for a, b in changes:
    assert a['area'] == b['area'] == 'commands' and a['native_item'].startswith('TK_Swap')
    assert a['port_status'] == 'missing' and b['port_status'] == 'done'
    assert 'native_intent=design' in b['port_cite_or_reason']
    assert {k for k in a if a[k] != b[k]} == {'port_status', 'port_cite_or_reason'}
result = dict(source_files=len(fingerprints), binary_files=len(build['test_binaries']),
              affected_methods=len(cohort), affected_invocations=sum(len(x['invocations']) for x in certificate),
              affected_case_passes=sum(x['case_passes'] for x in certificate), parking_passes=len(parking),
              suite_sweeps=2, suite_projects_per_sweep=4, raw_trx_verified=len(rows),
              suite_case_passes=sum(int(r['counters']['passed']) for r in suites),
              suite_case_failures=sum(int(r['counters']['failed']) for r in suites),
              suite_case_skips=len(skips),
              csv_semantic_changes=len(changes), csv_cr_bytes=0, source_unchanged_since_build=True)
(ASSETS / 'audit.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result, indent=2))
