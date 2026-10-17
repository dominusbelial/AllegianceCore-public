#!/usr/bin/env python3
"""Reconcile an interrupted wrapper with its complete raw VSTest result.

Run only after the suite/retry writer exits. No test results are inferred from
exit status, and the incomplete attempt remains explicitly archived.
"""
import json, re
from pathlib import Path
OUT = Path(__file__).resolve().parent
if 'Final certification commands completed' not in (OUT/'final-gates.log').read_text():
    raise SystemExit('The suite/retry writer is still active; do not replace its ledger.')
ledger = OUT/'verification-final.json'
rows = json.loads(ledger.read_text())
archive_path = OUT/'verification-incomplete.json'
archive = json.loads(archive_path.read_text()) if archive_path.exists() else []
changed = False
for i, row in enumerate(rows):
    match = re.fullmatch(r'suite-([12])-ClientCore\.MonoGame', row['name'])
    if not match or 'counters' in row: continue
    sweep = int(match[1])
    completion_path = OUT/('verification-mono-completion.json' if sweep == 1 else 'verification-mono-completion-2.json')
    if not completion_path.exists(): raise SystemExit(f'Missing completed MonoGame sweep {sweep}')
    candidates = json.loads(completion_path.read_text())
    candidates = [r for r in candidates if r.get('counters') and int(r['counters']['executed']) > 0]
    if len(candidates) != 1: raise SystemExit(f'Expected one complete MonoGame result for sweep {sweep}')
    replacement = candidates[0]
    if not (OUT/replacement['evidence']).exists(): raise SystemExit('Missing raw completion evidence')
    archived = dict(row)
    archived['name'] = 'incomplete-' + row['name']
    archived['reason'] = 'Wrapper exited without a completed TRX or final summary; not counted as a completed sweep.'
    archived['console'] = row['name'] + '.log'
    if not any(r['name'] == archived['name'] for r in archive): archive.append(archived)
    replacement = dict(replacement)
    replacement['completion_invocation'] = replacement['name']
    replacement['name'] = row['name']
    rows[i] = replacement
    changed = True
if changed:
    archive_path.write_text(json.dumps(archive, indent=2)+'\n')
    ledger.write_text(json.dumps(rows, indent=2)+'\n')
print('Completed suite rows:', sum(bool(re.fullmatch(r'suite-[12]-.+',r['name'])) and 'counters' in r for r in rows))
print('Archived incomplete attempts:',len(archive))
