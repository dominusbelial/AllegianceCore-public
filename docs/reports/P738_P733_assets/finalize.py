"""Finish the report only after the scheduled suites and failure qualification."""
import fcntl, hashlib, json, shutil, subprocess, sys, tarfile
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
lock = (OUT / 'runner.lock').open('a')
fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
ledger = OUT / 'verification.json'
records = json.loads(ledger.read_text())
assert all(any(r['label'] == 'suite-' + p for r in records) for p in ('Core', 'ClientCore', 'ClientCore.MonoGame'))

# The early diagnostic used a separate ledger to avoid racing the active driver.
early = OUT / 'early-isolation'
early_lock = (early / 'runner.lock').open('a')
fcntl.flock(early_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
for row in json.loads((early / 'verification.json').read_text()):
    if any(r['label'] == row['label'] for r in records): continue
    for suffix in ('.log', '.trx'):
        shutil.copy2(early / (row['label'] + suffix), OUT / (row['label'] + suffix))
    records.append(row)
ledger.write_text(json.dumps(records, indent=2) + '\n')
subprocess.run([sys.executable, str(OUT / 'isolate_failures.py')], cwd=ROOT, check=True)
subprocess.run([sys.executable, str(OUT / 'summarize.py')], cwd=ROOT, check=True)
summary = json.loads((OUT / 'summary.json').read_text())
qualification = json.loads((OUT / 'flake-qualification.json').read_text())
records = json.loads(ledger.read_text())
assert not summary['missing_invocations'] and not summary['source_changes_since_build']
assert all(q['three_clean_passes'] for q in qualification), 'A failure needs investigation; report remains in progress.'
qualified = {q['method'] for q in qualification}
for row in records:
    assert 'counters' in row and int(row['counters']['executed']) > 0, row['label']
    if row['exit_code']:
        assert row.get('failures') and all(f['method'] in qualified for f in row['failures']), row['label']
assert summary['focused']['invocations'] == 774 and summary['focused']['failed'] == summary['focused']['notExecuted'] == 0
assert summary['showcase']['invocations'] == summary['showcase']['passed'] == 5
assert summary['showcase']['failed'] == summary['showcase']['notExecuted'] == 0

suites = summary['suites']
table = '| Assembly | Passed | Failed | Skipped |\n|---|---:|---:|---:|\n'
for name in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
    counters = suites['suite-' + name]['counters']
    skipped = len(suites['suite-' + name].get('skips', []))
    table += f"| {name} | {counters['passed']} | {counters['failed']} | {skipped} |\n"
body = 'The once-per-assembly Core → ClientCore → MonoGame sweep completed:\n\n' + table
body += '''
The two Core repository guards that launch git (`MeshyGuardTests` and
`PrivatePathGuardTests`) were excluded to honor the user's explicit prohibition.
Ordinary local/loopback and LiveTiming tests ran. The local P542, P660, P496,
P539 baseline and P539c opt-ins were enabled; historical 221/200v200 opt-in
proof drivers remained off as directed. No assertion, timeout or quarantine
trait was changed.

'''
if qualification:
    body += "Each observed failing case passed **3/3 fresh single-method invocations** under the brief's load-flake rule. The failing suite outcomes remain in the ledger. "
    body += 'See the [qualification record](P738_P733_assets/flake-qualification.json) and [flake notes](../TEST_FLAKES.md#p738--p733-merge-observation-2026-10-17); isolated passes do not establish the underlying causes.\n\n'
skips = [case for row in suites.values() for case in row.get('skips', [])]
if skips:
    body += 'Recorded skips: ' + '; '.join('`' + name + '`' for name in skips) + '. The individual skip reasons are retained in the raw TRX.\n\n'
body += '''The [complete summary](P738_P733_assets/summary.json) retains every completed
qualifying invocation, raw failures, skips and source-fingerprint verification.
The [raw logs, TRX and research timelines](P738_P733_assets/validation-logs.tar.gz)
include the excluded overlapping capture evidence separately. Final checks
confirm no conflict markers, LF matrices, unchanged source since the build,
and unchanged repository metadata. Changes remain in files for the dispatcher;
no git command, commit or push was performed.
'''
report = ROOT / 'docs/reports/P733_TECH_FINALE_AND_SPEED.md'
pending = (OUT / 'validation-pending.md').read_text()
text = report.read_text()
if pending in text:
    assert text.count(pending) == 1
    report.write_text(text.replace(pending, body))
else:
    assert 'The once-per-assembly Core → ClientCore → MonoGame sweep completed:' in text

flakes = ROOT / 'docs/TEST_FLAKES.md'
marker = '\n### P738 / P733 completed assembly sweep\n'
if marker not in flakes.read_text():
    extra = marker + '\n' + table + '\n'
    section = flakes.read_text().split('## P738 / P733 merge observation (2026-10-17)', 1)[1]
    remaining = [q for q in qualification if q['method'] not in section]
    if remaining:
        extra += '| Additional observed case | Symptom | Qualification |\n|---|---|---|\n'
        for q in remaining:
            symptoms = [f['message'].splitlines()[0] for r in records for f in r.get('failures', []) if f['method'] == q['method']]
            symptom = '; '.join(dict.fromkeys(symptoms)).replace('|', '\\|')[:400]
            extra += f"| `{q['original_case']}` | {symptom} | 3/3 fresh single-method invocations |\n"
    extra += '\nAll observed failing methods met the brief\'s 3/3 single-method criterion. Original failures remain in the [ledger](reports/P738_P733_assets/verification.json); [qualification details](reports/P738_P733_assets/flake-qualification.json) and [raw logs/TRX](reports/P738_P733_assets/validation-logs.tar.gz) are retained. No assertion, timeout, quarantine trait or product code was changed for these observations.\n'
    with flakes.open('a') as stream: stream.write(extra)

subprocess.run([sys.executable, str(OUT / 'audit.py')], cwd=ROOT, check=True)
assert not summary['source_changes_since_build']
(OUT / 'final-checks.json').write_text(json.dumps(dict(
    completed_utc=datetime.now(timezone.utc).isoformat(),
    all_required_invocations_completed=True, focused_three_repetitions=True,
    showcase_five_passes=True, all_observed_failures_qualified=True,
    observed_failure_methods=sorted(qualified),
    source_unchanged_since_build=True, metadata_unchanged=True,
    excluded_git_invoking_guards=['MeshyGuardTests', 'PrivatePathGuardTests'],
    excluded_historical_rig_opt_ins=True), indent=2) + '\n')

# Keep readable build/progress logs and structured ledgers outside the archive.
raw = [p for p in OUT.glob('*.trx')]
raw += [p for p in OUT.glob('*.log') if p.name not in ('build.log', 'progress.log')]
raw += list(OUT.glob('proof-*'))
raw += [OUT / 'overlapping-attempts']
raw += [p for p in early.iterdir() if p.suffix in ('.log', '.trx') or p.is_dir()]
with tarfile.open(OUT / 'validation-logs.tar.gz', 'w:gz') as archive:
    for path in raw:
        if path.exists(): archive.add(path, arcname=str(path.relative_to(OUT)))
for path in raw:
    if path.is_dir(): shutil.rmtree(path)
    elif path.exists(): path.unlink()
for path in OUT.rglob('__pycache__'):
    if path.is_dir(): shutil.rmtree(path)
manifest = {str(p.relative_to(OUT)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in OUT.rglob('*') if p.is_file() and p.name != 'evidence-manifest.json'}
(OUT / 'evidence-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('P738/P733 report and evidence complete.', flush=True)
