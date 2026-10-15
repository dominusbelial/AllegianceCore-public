#!/usr/bin/env python3
"""Archive final suite evidence and verify the brief's isolated-rerun rule."""
import collections, datetime, gzip, hashlib, json, pathlib, shutil, xml.etree.ElementTree as ET

assets = pathlib.Path(__file__).resolve().parent
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
human = 'Human_eject_autopilot_flies_two_jumps_and_docks_without_oxygen_respawn'
outcome = 'Pod_outcome_counts_a_true_death_restart_separately_from_flying_home'
affected = json.loads((assets / 'affected-results.json').read_text())
completed = [row for row in affected if not any(row['filter'].endswith('.' + method) for method in [human, outcome])]
completed += json.loads((assets / 'human-complete-results.json').read_text())
completed += json.loads((assets / 'telemetry-results.json').read_text())
assert len(completed) == 240
assert all(row['exit'] == 0 and not row['failed'] and row['passed'] for row in completed)
assert set(collections.Counter(row['filter'] for row in completed).values()) == {3}
final = json.loads((assets / 'final-results.json').read_text())
assert len(final) == 3
failures = {tuple(method) for method in json.loads((assets / 'post-final-methods.json').read_text())}
assert {(row['project'], fail['name'].split('(')[0]) for row in final for fail in row['failed']} <= failures
post = json.loads((assets / 'post-final-results.json').read_text()) if failures else []
for project, method in failures:
    runs = [row for row in post if row['project'] == project and row['filter'] == method]
    assert len(runs) == 3 and all(row['exit'] == 0 and not row['failed'] and row['passed'] for row in runs), method

archives = []
previous_finish = datetime.datetime.fromtimestamp(pathlib.Path('/tmp/p699-complete-build.log').stat().st_mtime, datetime.timezone.utc)
for row in final + [{'project': 'Lobby', 'trx': '/tmp/p699-validation/lobby-final.trx'}]:
    original = pathlib.Path(row['trx'])
    tree = ET.parse(original)
    counters = tree.find('.//t:ResultSummary/t:Counters', ns).attrib
    times = tree.find('t:Times', ns).attrib
    assert datetime.datetime.fromisoformat(times['start']) >= previous_finish, row['project']
    previous_finish = datetime.datetime.fromisoformat(times['finish'])
    if 'passed' in row:
        assert int(counters['passed']) == row['passed']
        assert int(counters['failed']) == len(row['failed'])
    archive = assets / f"{row['project']}-final.trx.gz"
    with original.open('rb') as source, gzip.open(archive, 'wb') as target:
        shutil.copyfileobj(source, target)
    skipped = [r.get('testName') for r in tree.findall('.//t:UnitTestResult', ns) if r.get('outcome') == 'NotExecuted']
    archives.append({'project': row['project'], 'counters': counters, 'skipped_cases': skipped, 'times': times, 'archive': archive.name,
        'trx_sha256': hashlib.sha256(original.read_bytes()).hexdigest()})
build = pathlib.Path('/tmp/p699-complete-build.log').read_text()
assert '0 Warning(s)' in build and '0 Error(s)' in build
(assets / 'build.log').write_text(build)
summary = {'affected_methods': len({row['filter'] for row in completed}),
    'final_isolated_invocations': len(completed), 'final_isolated_cases_passed': sum(row['passed'] for row in completed),
    'suites': archives, 'post_suite_methods': sorted(failures),
    'post_suite_isolated_invocations': len(post), 'post_suite_cases_passed': sum(row['passed'] for row in post)}
(assets / 'validation-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
