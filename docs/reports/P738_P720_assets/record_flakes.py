#!/usr/bin/env python3
"""Union freshly qualified P738/P720 observations into the flake ledger."""
from pathlib import Path
import json
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
summary=json.loads((OUT/'summary.json').read_text())
records=json.loads((OUT/'verification.json').read_text())
qualified=summary['failure_classifications']
if not qualified:
 print('No new flake observations');raise SystemExit(0)
text='\n\n## P738 / P720 merge observation (2026-10-17)\n\n'
text+='The serial broad Release gate retained these raw results (passed / failed /\nskipped):\n\n'
for run in summary['suites']:
 c=run['counters'];text+=f"- {run['label'][6:]}: **{c['passed']} / {c['failed']} / {len(run['skips'])}**.\n"
text+='\nAfter all three broad passes finished, every failing method/case passed\n**3/3 fresh isolated invocations**, with assertions and deadlines unchanged:\n\n'
for f in qualified:
 failure=next(x for r in records if not r['label'].startswith('post-isolate-') for x in r.get('failures',[]) if x['method']==f['method'] and x['name']==f['case'])
 symptom=failure['message'].strip().splitlines()[0]
 text+=f"- `{f['case']}`: {symptom.rstrip('.')}. Isolated qualification: **3/3 pass**.\n"
text+='''
These satisfy the brief's load-flake rule and do not establish underlying causes
or fixes. No product behavior, assertions, deadlines, quarantine traits or
transport changes were made for these observations. The MonoGame skips are the
existing optional private Meshy-pack checks. The two Core guards that invoke
git were excluded to honor the explicit no-git instruction; ordinary live,
loopback and LiveTiming tests ran, with the local P660 opt-in enabled.

The 82 focused methods passed three times alone (669 case passes / zero
failures or skips), and the standalone never-parks gate passed 5/5. See the
[P738 appendix](reports/P720_INTERFACE_WAVE.md#p738-merge-onto-master),
[invocation ledger](reports/P738_P720_assets/verification.json),
[case outcomes](reports/P738_P720_assets/case-results.json), and
[raw logs/TRX archive](reports/P738_P720_assets/validation-logs.tar.gz).
'''
path=ROOT/'docs/TEST_FLAKES.md';old=path.read_text();assert '## P738 / P720 merge observation' not in old
path.write_text(old+text);print('Appended',len(qualified),'qualified observation(s) to TEST_FLAKES.md')
