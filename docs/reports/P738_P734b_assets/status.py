"""Print completed evidence and the current bounded test process."""
import collections, json, sys, time
from pathlib import Path
out = Path(__file__).resolve().parent
records = json.loads((out / 'verification.json').read_text()) if (out / 'verification.json').exists() else []
summary = {}
for kind in ('focused', 'showcase', 'suite', 'isolation'):
    selected = [r for r in records if r['label'].startswith(kind + '-')]
    counts = collections.Counter(c['outcome'] for r in selected for c in r.get('cases', []))
    summary[kind] = dict(invocations=len(selected), cases=dict(counts))
summary['failures'] = [dict(invocation=r['label'], method=c['method'], case=c['name'], message=c.get('message', ''))
                       for r in records for c in r.get('cases', []) if c['outcome'] == 'Failed']
if (out / 'active-run.json').exists():
    active = json.loads((out / 'active-run.json').read_text())
    summary['active'] = {k: active[k] for k in ('label', 'finished') if k in active}
    if 'started' in active: summary['active']['elapsed_seconds'] = round(time.time() - active['started'])
if '--short' in sys.argv:
    print(json.dumps(dict(active=summary.get('active'), completed_invocations=len(records),
                          failed_cases=len(summary['failures']), completed_suites=summary['suite']['invocations'])))
else:
    print(json.dumps(summary, indent=2))
