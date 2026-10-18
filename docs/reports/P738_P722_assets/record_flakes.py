#!/usr/bin/env python3
"""Record only observed failures qualified by post-suite passing triplets."""
import datetime
import json
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
s = json.loads((OUT / 'summary.json').read_text())
failures = s['failure_classifications']
if not failures:
    print('No final validation flakes to append; existing union remains intact.')
    raise SystemExit(0)
records = json.loads((OUT / 'verification.json').read_text())
ledger = ROOT / 'docs/TEST_FLAKES.md'
date = datetime.datetime.now(ZoneInfo('America/Bogota')).date().isoformat()
heading = f'## P738 / P722 merge observation ({date})'
assert heading not in ledger.read_text(), 'Flake observation already recorded'
lines = ['', '', heading, '', 'Raw broad Release results (passed / failed / skipped):', '']
for row in s['suites']:
    c = row['counters']
    lines.append(f"- {row['label'].removeprefix('suite-')}: **{c['passed']} / {c['failed']} / {len(row['skips'])}**.")
lines += ['', 'After the broad passes, every method/case below passed three fresh',
          'individual invocations on the same binaries. These meet the brief’s',
          'load-flake rule; isolated passes do not establish a cause or fix.', '']
for failure in failures:
    examples = [f for r in records if not r['label'].startswith('post-isolate-')
                for f in r.get('failures', []) if f['method'] == failure['method']]
    symptom = ' '.join(examples[0]['message'].split())[:300]
    lines += [f"- `{failure['method']}` — {symptom}. Post-suite isolation: **{failure['isolated_invocations']}/{failure['isolated_invocations']} passed**."]
lines += ['', 'No product behavior, assertion, deadline or quarantine was changed for',
          'these observations. Core’s two git-invoking guard classes were excluded',
          'to honor the explicit no-git instruction. Ordinary live/loopback and',
          'LiveTiming checks ran; the local P660 opt-in was enabled. MonoGame’s',
          'existing optional private Meshy-pack skips are retained in the counts.', '',
          'See the [P738 appendix](reports/P722_INTERFACE_WAVE.md#p738-merge-onto-master),',
          '[invocation ledger](reports/P738_P722_assets/verification.json),',
          '[case outcomes](reports/P738_P722_assets/case-results.json), and',
          '[raw logs/TRX](reports/P738_P722_assets/validation-logs.tar.gz).', '']
with ledger.open('a') as output:
    output.write('\n'.join(lines))
print('Appended', len(failures), 'qualified flake methods to', ledger)
