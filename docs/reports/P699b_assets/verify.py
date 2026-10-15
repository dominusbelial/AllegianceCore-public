#!/usr/bin/env python3
"""Verify build identity, exact-method triplets, fixture correction and suite evidence."""
import ast, collections, datetime, gzip, hashlib, json, pathlib, re, sys, xml.etree.ElementTree as ET
P = pathlib.Path(__file__).resolve().parent
ROOT = P.parents[2]
build = json.loads((P / 'build.json').read_text())
fixture_build = json.loads((P / 'fixture-build.json').read_text())
manifest = json.loads((P / 'source-manifest.json').read_text())
for source in manifest:
    assert hashlib.sha256((ROOT / source['file']).read_bytes()).hexdigest() == source['sha256'], source['file']
    deadline = fixture_build['finished'] if source.get('build') == 'fixture' else build['finished']
    assert source['modified'] <= deadline, source['file']
binaries = json.loads((P / 'binaries-after-fixture.json').read_text())
for binary in binaries:
    assert binary['unchanged']
    assert hashlib.sha256((ROOT / binary['file']).read_bytes()).hexdigest() == binary['sha256'], binary['file']
expected = set()
tree = ast.parse((P / 'validate.py').read_text())
files = next(ast.literal_eval(n.value) for n in ast.walk(tree) if isinstance(n, ast.Assign)
    and any(isinstance(t, ast.Name) and t.id == 'files' for t in n.targets))
for project, names in files.items():
    for name in names:
        text = (ROOT / f'tests/Allegiance.{project}.Tests/{name}.cs').read_text()
        namespace = re.search(r'namespace ([\w.]+);', text)[1]
        cls = re.search(r'public (?:sealed |static |partial )*class (\w+)', text)[1]
        for method in re.findall(r'(?:\[[^\]]+\]\s*)+public (?:async Task|void|Task) (\w+)\(', text):
            expected.add((project, f'{namespace}.{cls}.{method}'))
expected.update(tuple(x) for x in json.loads((P / 'affected-additional.json').read_text()))
fixture_methods = set(tuple(x) for x in json.loads((P / 'fixture-methods.json').read_text()))
expected.update(fixture_methods)
rows, no_matches, artifacts = [], [], []
phases = ['final-focused', 'isolate-additional', 'final-suites', 'isolate-failures',
    'isolate-fixture-fixed', 'corrected-monogame']
for phase in phases:
    ledger = P / f'{phase}-results.json'
    if not ledger.exists() and phase == 'corrected-monogame' and '--coverage-only' in sys.argv: continue
    assert ledger.exists(), phase
    for row in json.loads(ledger.read_text()):
        row = dict(row, phase=phase)
        path = P / row['trx']
        raw = gzip.decompress(path.read_bytes())
        tree = ET.fromstring(raw)
        times = tree.find('{*}Times').attrib
        started, finished = (datetime.datetime.fromisoformat(times[k]).timestamp() for k in ['start', 'finish'])
        deadline = fixture_build['finished'] if phase in ['isolate-fixture-fixed', 'corrected-monogame'] else build['finished']
        assert started >= deadline, (path.name, 'before relevant build')
        row.update(trx_started=started, trx_finished=finished)
        if not tree.findall('.//{*}UnitTestResult'):
            no_matches.append({'phase': phase, 'filter': row['filter'], 'trx': path.name})
        rows.append(row)
        artifacts.append({'file': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'raw_sha256': hashlib.sha256(raw).hexdigest(), 'started': started, 'finished': finished})
cohort_phases = ['final-focused', 'isolate-additional', 'isolate-fixture-fixed']
focused = [r for r in rows if r['phase'] in cohort_phases and r['passed'] > 0 and not r['failed'] and r['exit'] == 0]
coverage = collections.Counter((r['project'], r['filter']) for r in focused)
missing = [{'project': p, 'filter': f, 'passing_invocations': coverage[p, f]} for p, f in sorted(expected) if coverage[p, f] < 3]
assert not missing, missing
if '--coverage-only' in sys.argv:
    print(json.dumps({'affected_methods': len(expected), 'passing_isolated_invocations': sum(coverage[k] for k in expected),
        'passing_data_cases': sum(r['passed'] for r in focused), 'missing_coverage': missing,
        'unchanged_binaries': len(binaries), 'unmatched_filters_excluded': len(no_matches)}, indent=2))
    raise SystemExit(0)
original_suites = [r for r in rows if r['phase'] == 'final-suites']
assert [r['project'] for r in original_suites] == ['Core', 'ClientCore', 'ClientCore.MonoGame', 'Lobby']
previous = max(r['trx_finished'] for r in focused if r['phase'] != 'isolate-fixture-fixed')
for suite in original_suites:
    assert suite['trx_started'] >= previous, (suite['project'], 'overlapping prior verification')
    previous = suite['trx_finished']
original_failures = {(r['project'], f['method']) for r in original_suites for f in r['failed']}
assert original_failures == fixture_methods, original_failures
fixture_proof = []
for project, method in sorted(fixture_methods):
    before = [r for r in rows if r['phase'] == 'isolate-failures' and r['project'] == project and r['filter'] == method]
    after = [r for r in rows if r['phase'] == 'isolate-fixture-fixed' and r['project'] == project and r['filter'] == method]
    assert len(before) == 3 and all(r['failed'] and r['trx_started'] >= previous for r in before), method
    assert len(after) == 3 and all(not r['failed'] and r['exit'] == 0 and r['passed'] > 0 for r in after), method
    assert max(r['trx_finished'] for r in before) <= fixture_build['finished']
    fixture_proof.append({'project': project, 'method': method, 'before_failed': len(before), 'after_passed': len(after),
        'before_trx': [r['trx'] for r in before], 'after_trx': [r['trx'] for r in after]})
corrected = [r for r in rows if r['phase'] == 'corrected-monogame']
assert len(corrected) == 1 and corrected[0]['project'] == 'ClientCore.MonoGame'
assert corrected[0]['trx_started'] >= max(r['trx_finished'] for r in focused)
assert not corrected[0]['failed'] and corrected[0]['exit'] == 0
old_mono = original_suites[2]
assert old_mono['passed'] + len(old_mono['failed']) == corrected[0]['passed']
assert set(old_mono['skipped']) == set(corrected[0]['skipped'])
suites = [original_suites[0], original_suites[1], corrected[0], original_suites[3]]
assert all(not r['failed'] and r['exit'] == 0 for r in suites)
summary = {'production_build': build, 'fixture_only_build': fixture_build, 'source_files': len(manifest),
    'unchanged_production_core_client_binary_count': len(binaries), 'affected_methods': len(expected),
    'passing_isolated_invocations': sum(coverage[k] for k in expected),
    'passing_isolated_data_cases': sum(r['passed'] for r in focused), 'missing_coverage': missing,
    'unmatched_filter_invocations_excluded': no_matches, 'fixture_corrections': fixture_proof,
    'suites': [{k: r[k] for k in ['project', 'passed', 'failed', 'skipped', 'trx', 'trx_started', 'trx_finished']} for r in suites],
    'initial_suites': [{k: r[k] for k in ['project', 'passed', 'failed', 'skipped', 'trx', 'trx_started', 'trx_finished']} for r in original_suites],
    'portable_traces': artifacts}
(P / 'validation-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps({k: summary[k] for k in ['affected_methods', 'passing_isolated_invocations', 'passing_isolated_data_cases', 'missing_coverage']}, indent=2))
for suite in suites: print(suite['project'], suite['passed'], 'pass', len(suite['failed']), 'fail', len(suite['skipped']), 'skip')
