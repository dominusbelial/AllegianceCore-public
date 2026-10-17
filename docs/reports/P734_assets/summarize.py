#!/usr/bin/env python3
"""Audit final P734 evidence without invoking Git or running additional suites."""
import hashlib, json, re
from collections import Counter
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
rows = json.loads((OUT / 'verification-final.json').read_text())
methods = json.loads((OUT / 'affected-methods-final.json').read_text())
focus = [r for r in rows if r['name'].startswith('focus-')]
parking = [r for r in rows if r['name'].startswith('parking-')]
suites = [r for r in rows if re.fullmatch(r'suite-[12]-.+', r['name'])]
retries = [r for r in rows if r['name'].startswith('suite-retry-')]
def total(group):
    return dict(invocations=len(group), passed=sum(int(r['counters']['passed']) for r in group),
        failed=sum(int(r['counters']['failed']) for r in group), skipped=sum(r['observed_skipped'] for r in group))
source = json.loads((OUT / 'source-manifest.json').read_text())
source_matches = all(hashlib.sha256((ROOT / p).read_bytes()).hexdigest() == h for p,h in source.items())
gitdir = Path((ROOT / '.git').read_text().strip().split(': ', 1)[1])
metadata = json.loads((OUT / 'merge-metadata.json').read_text())
metadata_matches = all(hashlib.sha256((gitdir / p).read_bytes()).hexdigest() == h for p,h in metadata.items())
nonempty = all(int(r.get('counters', {}).get('executed', '0')) > 0 for r in rows if not r['name'].startswith('selection-audit-'))
expected = Counter('FullyQualifiedName=' + m for _,m in methods for _ in range(3))
triplets = Counter(r['filter'] for r in focus) == expected
failures = sorted({(r['project'], f['method']) for r in suites for f in r['failures']})
recoveries = []
for project, method in failures:
    runs = [r for r in retries if r['project'] == project and r['filter'] == 'FullyQualifiedName=' + method]
    recoveries.append(dict(project=project, method=method, runs=len(runs),
        passed_three=len(runs)==3 and all(r['exit']==0 and int(r['counters']['passed'])>0 for r in runs)))
proof = OUT / ('showcase-ring-final.json' if (OUT / 'showcase-ring-final.json').exists() else 'showcase.json')
showcase = json.loads(proof.read_text()) if proof.exists() else {}
summary = dict(focus=total(focus), methods=len(methods), exact_triplets=triplets,
    parking=total(parking), parking_clean=len(parking)==10 and all(r['exit']==0 for r in parking),
    suites=total(suites), suite_invocations=[{k:r[k] for k in ['name','counters','observed_skipped','failures','skipped_tests']} for r in suites],
    exactly_two_full_sweeps=len(suites)==8 and len({r['name'] for r in suites})==8,
    recoveries=recoveries, showcase={k:showcase.get(k) for k in ['techMode','civs','stageOverCount','simFailures']},
    all_selections_nonempty=nonempty, source_hashes_match=source_matches, merge_metadata_unchanged=metadata_matches,
    conflict_markers_absent=all(not re.search(r'^(<<<<<<<|=======|>>>>>>>)', (ROOT/p).read_text(), re.M) for p in ['src/Allegiance.Core/Sim/Ship.cs','docs/TEST_FLAKES.md']))
groups = {}
for r in retries:
    groups.setdefault((r['project'], r['filter']), []).append(r)
summary['isolated_classifications'] = [dict(project=p, method=f.removeprefix('FullyQualifiedName='), runs=len(rs),
    passed_three=len(rs)==3 and all(r['exit']==0 and int(r.get('counters',{}).get('passed','0'))>0 and not r.get('observed_skipped',0) for r in rs))
    for (p,f),rs in sorted(groups.items())]
summary['showcase']['artifact'] = proof.name
summary['clean'] = (triplets and summary['focus']['failed']==0 and summary['parking_clean'] and summary['exactly_two_full_sweeps']
    and all(r['passed_three'] for r in recoveries) and all(r['passed_three'] for r in summary['isolated_classifications']) and all(r['exit']==0 for r in retries) and showcase.get('stageOverCount')==2 and showcase.get('simFailures')==0
    and nonempty and source_matches and metadata_matches and summary['conflict_markers_absent'])
(OUT / 'summary.json').write_text(json.dumps(summary, indent=2)+'\n')
print(json.dumps(summary, indent=2))
