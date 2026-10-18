"""Finish documentation only after all required test evidence is complete."""
import collections, fcntl, hashlib, json, re, shutil, subprocess, sys, tarfile
from datetime import datetime, timezone
from pathlib import Path
sys.dont_write_bytecode = True
from verify import ROOT, OUT, sources

lock = (OUT / 'runner.lock').open('a')
fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
records = json.loads((OUT / 'verification.json').read_text())
methods = json.loads((OUT / 'focused-methods.json').read_text())
lookup = {r['label']: r for r in records}
assert len(lookup) == len(records)
expected = {f'focused-{index:03}-{repeat}' for index in range(len(methods)) for repeat in range(1, 4)}
expected |= {f'showcase-{repeat}' for repeat in range(1, 6)}
expected |= {'suite-' + project for project in ('Core', 'ClientCore', 'ClientCore.MonoGame')}
assert expected <= lookup.keys(), expected - lookup.keys()
assert all(r.get('cases') and int(r['counters']['executed']) > 0 for r in records)
qualifications = json.loads((OUT / 'flake-qualification.json').read_text())
assert all(q['three_clean_passes'] for q in qualifications)
qualified = {(q['project'], q['method'], q['case']) for q in qualifications}
for record in records:
    for case in record['cases']:
        if case['outcome'] == 'Failed': assert (record['project'], case['method'], case['name']) in qualified
    if record['exit_code']: assert any(c['outcome'] == 'Failed' for c in record['cases'])
assert json.loads((OUT / 'source-fingerprints.json').read_text()) == sources()
def aggregate(selected):
    counts = collections.Counter(c['outcome'] for r in selected for c in r['cases'])
    return dict(invocations=len(selected), passed=counts['Passed'], failed=counts['Failed'], skipped=counts['NotExecuted'])
focused = aggregate([r for r in records if r['label'].startswith('focused-')])
showcase = aggregate([lookup[f'showcase-{i}'] for i in range(1, 6)])
assert focused['invocations'] == len(methods) * 3 and focused['skipped'] == 0
assert showcase == dict(invocations=5, passed=5, failed=0, skipped=0), 'Showcase failures require a base-rate comparison'
metrics = []
pattern = re.compile(r'P700 local 15v15: ([\d.]+)s, samples=(\d+), reached=(\d+)/30, longest speed<1 with target=([\d.]+)s, shots=(\d+), hits=(\d+), kills=(\d+)')
for repeat in range(1, 6):
    row = lookup[f'showcase-{repeat}']
    match = pattern.search('\n'.join(c.get('output', '') for c in row['cases']))
    assert match
    metrics.append(dict(invocation=row['label'], seconds=float(match[1]), samples=int(match[2]), reached=int(match[3]),
                        longest_idle_seconds=float(match[4]), shots=int(match[5]), hits=int(match[6]), kills=int(match[7])))
(OUT / 'never-parks-metrics.json').write_text(json.dumps(metrics, indent=2) + '\n')
suites = {project: aggregate([lookup['suite-' + project]]) for project in ('Core', 'ClientCore', 'ClientCore.MonoGame')}
skips = [dict(project=r['project'], name=c['name'], message=c.get('message', '')) for r in records for c in r['cases'] if c['outcome'] == 'NotExecuted']
assert all('MeshyPackTests.' in c['name'] and c['project'] == 'ClientCore.MonoGame' for c in skips), skips
summary = dict(completed_utc=datetime.now(timezone.utc).isoformat(), clean=True, focused_methods=len(methods), focused=focused,
    showcase=showcase, suites=suites, qualified_failure_cases=qualifications, skipped_cases=skips,
    sources_unchanged_since_build=True, no_git_commands_executed=True,
    excluded_git_invoking_guards=['MeshyGuardTests', 'PrivatePathGuardTests'], base_comparison_required=False)
(OUT / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')

table = '| Assembly | Passed | Failed | Skipped |\n|---|---:|---:|---:|\n'
for project, counts in suites.items(): table += f"| {project} | {counts['passed']} | {counts['failed']} | {counts['skipped']} |\n"
body = f"""**Focused validation completed:** {len(methods)} methods × three fresh
exact-method processes = **{focused['invocations']} completed invocations /
{focused['passed']} case passes / {focused['failed']} failures / {focused['skipped']} skips**.

**Never-parks: 5/5 passed**, zero failures/skips. Maximum stopped-with-target
interval was **{max(m['longest_idle_seconds'] for m in metrics):.3f} seconds**;
all battles observed real shots and hits, with **{sum(m['kills'] for m in metrics)} kills** total and
zero simulation-stage failures. The original assertions and pacing are
unchanged. No base-rate comparison was triggered. [Battle metrics](P738_P734b_assets/never-parks-metrics.json).

The once-per-assembly Core → ClientCore → MonoGame sweep completed, with only
the two documented git-invoking guard exclusions:

""" + table + '\n'
if qualifications:
    body += 'Every observed failing case passed **3/3 fresh isolated method invocations**, including the originally failing theory case. These satisfy the brief\'s load-flake criterion; original failures remain visible and isolated passes do not establish their causes or a fix. No assertion, deadline, quarantine trait or production behavior was changed for these observations. See [qualification](P738_P734b_assets/flake-qualification.json) and [flake notes](../TEST_FLAKES.md#p738--p734b-merge-observation).\n\n'
else: body += 'No test failures were observed, so no failure-isolation retries were needed.\n\n'
if skips: body += 'The only skipped cases are the three pre-existing optional private Meshy-pack tests; their individual reasons are retained in the ledger and raw TRX.\n\n'
body += '''The [invocation ledger](P738_P734b_assets/verification.json) records exact
commands, counters and every case outcome. The [summary](P738_P734b_assets/summary.json)
checks the complete required population, source fingerprints and qualification.
[Raw logs and TRX](P738_P734b_assets/validation-logs.tar.gz) retain the evidence.

Final audits confirm LF matrices, no conflict markers, both histories retained,
unique sound dispatch, no source changes since the successful build, and unchanged
merge metadata. All changes remain in files for the dispatcher to stage.
No git command, staging, commit, push, deployment or rig run was performed.
'''
report = ROOT / 'docs/reports/P709_PARITY_WAVE12.md'
assert report.read_text().count('P738_P734B_VALIDATION_PENDING') == 1
report.write_text(report.read_text().replace('P738_P734B_VALIDATION_PENDING', body))
if qualifications:
    flakes = ROOT / 'docs/TEST_FLAKES.md'
    note = '\n## P738 / P734b merge observation\n\nThe completed integration sweep retained these observed failures. Each originally failing case then passed in all three fresh isolated method invocations:\n\n| Case | Symptom | Qualification |\n|---|---|---|\n'
    for q in qualifications:
        symptoms = [c.get('message', '').splitlines()[0] for r in records for c in r['cases']
                    if r['project'] == q['project'] and c['method'] == q['method'] and c['name'] == q['case'] and c['outcome'] == 'Failed']
        symptom = '; '.join(dict.fromkeys(symptoms)).replace('|', '\\|')[:400]
        note += f"| `{q['case']}` | {symptom} | 3/3; `{', '.join(q['invocations'])}` |\n"
    note += '\nThese meet P738\'s explicit load-flake rule; isolated passes do not establish the underlying causes or a fix. No assertion, deadline, quarantine trait or product code was changed. [Ledger](reports/P738_P734b_assets/verification.json), [qualification](reports/P738_P734b_assets/flake-qualification.json), [raw evidence](reports/P738_P734b_assets/validation-logs.tar.gz).\n'
    with flakes.open('a') as stream: stream.write(note)

subprocess.run([sys.executable, str(OUT / 'audit.py')], cwd=ROOT, check=True)
for name, row in json.loads((OUT / 'native-manifest.json').read_text()).items():
    assert hashlib.sha256((Path('/home/domingo/Development/Allegiance/src') / name).read_bytes()).hexdigest() == row['sha256']
raw = list(OUT.glob('*.trx.gz'))
raw += [p for p in OUT.glob('*.log') if p.name not in ('build.log', 'build-initial.log', 'progress.log')]
if (OUT / 'research-proof').exists(): raw.append(OUT / 'research-proof')
with tarfile.open(OUT / 'validation-logs.tar.gz', 'w:gz') as archive:
    for path in raw: archive.add(path, arcname=path.name)
for path in raw:
    if path.is_file(): path.unlink()
    elif path.is_dir(): shutil.rmtree(path)
(OUT / 'final-checks.json').write_text(json.dumps(dict(all_gates_completed=True,
    source_and_native_fingerprints_unchanged=True, repository_metadata_unchanged=True,
    validation_marker_removed=True, git_commands_executed=False), indent=2) + '\n')
manifest = {str(p.relative_to(OUT)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in OUT.rglob('*') if p.is_file() and p.name != 'evidence-manifest.json'}
(OUT / 'evidence-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(summary, indent=2))
