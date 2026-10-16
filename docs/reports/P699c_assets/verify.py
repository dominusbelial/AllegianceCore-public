#!/usr/bin/env python3
"""Audit final exact-method triplets, rebuilt suites and portable TRX evidence."""
import ast, collections, datetime, gzip, hashlib, json, pathlib, re, xml.etree.ElementTree as ET
P = pathlib.Path(__file__).resolve().parent
ROOT = P.parents[2]
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
files = next(ast.literal_eval(n.value) for n in ast.walk(ast.parse((P/'validate.py').read_text()))
             if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'files' for t in n.targets))
expected = set()
for project, names in files.items():
    for name in names:
        source = (ROOT/f'tests/Allegiance.{project}.Tests/{name}.cs').read_text()
        namespace = re.search(r'namespace ([\w.]+);', source)[1]
        cls = re.search(r'public (?:sealed |static |partial )*class (\w+)', source)[1]
        methods = re.findall(r'(?:\[[^\]]+\]\s*)+public (?:async Task|void|Task) (\w+)\(', source)
        expected.update((project, f'{namespace}.{cls}.{m}') for m in methods)
expected.update(tuple(x) for x in json.loads((P/'aligned-extra-methods.json').read_text()))
rows = json.loads((P/'accepted-focused-results.json').read_text())
rows += json.loads((P/'isolate-accepted-extra-results.json').read_text())
coverage = collections.Counter((r['project'],r['filter']) for r in rows if not r['exit'] and not r['failed'] and r['passed'])
assert set(coverage) == expected, (set(coverage)-expected, expected-set(coverage))
assert all(coverage[k] == 3 for k in expected), coverage
for source in json.loads((P/'source-manifest.json').read_text()):
    assert hashlib.sha256((ROOT/source['file']).read_bytes()).hexdigest() == source['sha256'], source['file']
for source in json.loads((P/'inputs.json').read_text()):
    assert hashlib.sha256(pathlib.Path(source['file']).read_bytes()).hexdigest() == source['sha256'], source['file']
after = json.loads((P/'accepted-binaries.json').read_text())
for file,digest in after.items():
    assert hashlib.sha256((ROOT/file).read_bytes()).hexdigest() == digest, file
suites = json.loads((P/'accepted-suites-results.json').read_text())
assert [r['project'] for r in suites] == ['Core','ClientCore','ClientCore.MonoGame','Lobby']
build = json.loads((P/'accepted-build.json').read_text())
assert build['exit'] == 0
last = build['finished']
for row in rows:
    assert row['started'] >= last, (row['project'], row['filter'], 'overlapping or pre-build invocation')
    last = row['finished']
for suite in suites:
    assert suite['started'] >= last, suite['project']
    assert suite['passed'] > 0
    assert not suite['exit'] or suite['failed'], suite
    last = suite['finished']
failures = {(r['project'],f['method']) for r in suites for f in r['failed']}
retry_path = P/'isolate-accepted-suite-failures-results.json'
retries = json.loads(retry_path.read_text()) if retry_path.exists() else []
retry_passes = collections.Counter((r['project'],r['filter']) for r in retries if not r['exit'] and not r['failed'] and r['passed'])
assert all(retry_passes[k] >= 3 for k in failures), (failures,retry_passes)
artifacts = []
for r in rows+suites+retries:
    path = P/r['trx']; data = gzip.decompress(path.read_bytes()); tree = ET.fromstring(data)
    results = tree.findall('.//t:UnitTestResult',NS)
    times = tree.find('t:Times', NS)
    assert times is not None, path.name
    trx_started = datetime.datetime.fromisoformat(times.get('start').replace('Z', '+00:00')).timestamp()
    assert r['started'] - 5 <= trx_started <= r['finished'], (path.name, 'TRX belongs to another invocation')
    assert results, path.name
    assert sum(x.get('outcome') == 'Passed' for x in results) == r['passed'], path.name
    assert not any('test run was aborted' in (x.text or '').lower() for x in tree.findall('.//t:RunInfo/t:Text',NS)),path.name
    artifacts.append({'file':path.name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
summary = {'affected_methods':len(expected),'isolated_invocations':len(rows),'isolated_data_cases':sum(r['passed'] for r in rows),
           'final_build':build,
           'suites':[{k:r[k] for k in ['project','passed','failed','skipped','seconds']} for r in suites],
           'suite_failures_passing_three_isolated_retries':sorted(failures),'artifacts':artifacts}
(P/'validation-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps({k:v for k,v in summary.items() if k != 'artifacts'},indent=2))
