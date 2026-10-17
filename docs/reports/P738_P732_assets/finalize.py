#!/usr/bin/env python3
"""Aggregate the completed P738/P732 ledger without rerunning tests."""
import hashlib,json,tarfile
import xml.etree.ElementTree as ET
from pathlib import Path
OUT=Path(__file__).resolve().parent
ROOT=OUT.parents[2]
records=json.loads((OUT/'verification.json').read_text())
methods=json.loads((OUT/'focused-methods.json').read_text())
focused=[r for r in records if r['label'].startswith('focused-')]
showcase=[r for r in records if r['label'].startswith('showcase-')]
suites=[r for r in records if r['label'].startswith('suite-')]
isolated=[r for r in records if r['label'].startswith('post-isolate-')]
assert len(focused)==len(methods)*3
assert all(r['exit_code']==0 and int(r['counters']['passed'])>0 and int(r['counters']['failed'])==0 and int(r['counters']['notExecuted'])==0 for r in focused)
assert len(showcase)==5 and len(suites)==3

def totals(rows):
    return {**{key:sum(int(r.get('counters',{}).get(key,0)) for r in rows) for key in ('total','passed','failed','notExecuted')},'skipped':sum(len(r.get('skips',[])) for r in rows)}
summary={'focused':{'methods':len(methods),'invocations':len(focused),**totals(focused)},'showcase':totals(showcase),'suites':{r['label']:{**r['counters'],'outcome_skipped':len(r['skips'])} for r in suites},'suite_failures':[f for r in suites for f in r.get('failures',[])],'post_suite_isolation':totals(isolated)}
failed_methods={f['method'] for f in summary['suite_failures']}
qualified={}
case_records=[]
def cases(record):
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    return [{'name':r.attrib['testName'],'outcome':r.attrib['outcome']}
            for r in ET.parse(OUT/(record['label']+'.trx')).findall('.//t:UnitTestResult',ns)]
for method in sorted(failed_methods):
    matching=[r for r in isolated if ('FullyQualifiedName='+method) in r['command']]
    qualified[method]={'invocations':len(matching),'all_passed':len(matching)==3 and all(r['exit_code']==0 and int(r['counters']['passed'])>0 and int(r['counters']['failed'])==0 and int(r['counters']['notExecuted'])==0 for r in matching),'counts':totals(matching)}
    if '.P703ResearchCommanderLiveTests.' in method:
        authored=[c for r in matching for c in cases(r) if 'generated: False' in c['name']]
        generated=[r for r in isolated if r['label'].startswith('post-isolate-P703-generated-')]
        generated_cases=[c for r in generated for c in cases(r)]
        authored_ok=len(authored)==3 and all(c['outcome']=='Passed' for c in authored)
        generated_ok=(len(generated)==3 and len(generated_cases)==3
                      and all(c['outcome']=='Passed' and 'generated: True' in c['name'] for c in generated_cases)
                      and all(r['exit_code']==0 for r in generated))
        qualified[method]={'all_passed':authored_ok and generated_ok,
                           'qualification':'Each parameter case has three isolated passes after its own failure.',
                           'authored_cases':authored,'generated_followup_cases':generated_cases,
                           'initial_triplet_counts':totals(matching),'generated_followup_counts':totals(generated)}
        case_records=[{'label':r['label'],'command':r['command'],'cases':cases(r)}
                      for r in matching+generated]
summary['failure_qualification']=qualified
assert all(q['all_passed'] for q in qualified.values()),qualified
(OUT/'case-results.json').write_text(json.dumps(case_records,indent=2)+'\n')
(OUT/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
files=sorted(p for folder in ('src','tests','scripts') for p in (ROOT/folder).rglob('*') if p.is_file() and p.suffix in ('.cs','.csproj','.props','.targets','.slnx','.py','.mgcb') and not any(x in p.parts for x in ('bin','obj','__pycache__')))
(OUT/'source-fingerprints.json').write_text(json.dumps({str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files},indent=2)+'\n')
logs=sorted([*OUT.glob('*.log'),*OUT.glob('*.trx')])
with tarfile.open(OUT/'validation-logs.tar.gz','w:gz') as tar:
    for p in logs:tar.add(p,arcname=p.name)
# Keep compact ledgers and the complete raw archive in the repository.
for p in logs:
    if p.name not in ('build.log','drone-regression-before.log','progress.log'):p.unlink()
(OUT/'evidence-manifest.json').write_text(json.dumps({p.name:{'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(OUT.iterdir()) if p.is_file() and p.name!='evidence-manifest.json'},indent=2)+'\n')
print(json.dumps(summary,indent=2))
