"""Document failures only after their required isolated passes are measured."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
records=json.loads((OUT/'verification.json').read_text());qualified={}
for index,row in enumerate(records):
    if not row['label'].startswith(('suite-','showcase-')):continue
    for failure in row.get('failures',[]):
        method=failure['method']
        later=[r for r in records[index+1:] if r['label'].startswith('post-isolate-') and 'FullyQualifiedName='+method in r['command']]
        assert len(later)==3 and all(r['exit_code']==0 and int(r['counters']['passed'])>0 and not r['skips'] for r in later),(method,later)
        qualified[method]=dict(failure=failure,phase=row['label'],isolations=[dict(label=r['label'],seconds=r['seconds'],counters=r['counters']) for r in later])
(OUT/'flake-qualification.json').write_text(json.dumps(qualified,indent=2)+'\n')
if qualified:
    rows=[]
    for method,q in sorted(qualified.items()):
        lines=[line.strip() for line in q['failure']['message'].splitlines() if line.strip()]
        symptom=next((line for line in lines if 'Failure' in line or 'Exception' in line or 'must ' in line),lines[0] if lines else 'See raw TRX')
        rows.append(f"| `{method}` | {q['phase']} | {symptom.replace('|','/')} | 3/3 passed |")
    text='''\n## P738 master merge — full-suite isolation (2026-10-17)\n\nThe Core, ClientCore and MonoGame assemblies ran once each after the clean\nRelease build. Every method below failed in that measured pass and then\npassed in **three fresh isolated processes**, with no source or fixture\nchanges between the failure and recovery. Classified as load/timing flakes\nunder the brief's lane rule; the initial failures are retained, not replaced\nwith the recovery results.\n\n| test | failing phase | observed symptom | post-failure isolation |\n|---|---|---|---|\n'''+ '\n'.join(rows)+'''\n\nExact commands, timing, failure messages and counters are in\n`docs/reports/P738_master_assets/verification.json`; raw logs/TRX are retained\nin `validation-logs.tar.gz`, with compact qualifications in\n`flake-qualification.json`. No full assembly was rerun to obtain a clean total.\nThe two guards that launch git were excluded to honor the explicit no-git\ninstruction; ordinary live/loopback and LiveTiming tests were included.\n'''
    p=ROOT/'docs/TEST_FLAKES.md';assert '## P738 master merge — full-suite isolation' not in p.read_text()
    with p.open('a') as stream:stream.write(text)
print('Documented isolated suite flakes:',len(qualified))
