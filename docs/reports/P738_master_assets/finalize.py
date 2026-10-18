"""Finalize measured P738 evidence without rerunning a test or invoking git."""
import hashlib,json,re,tarfile,xml.etree.ElementTree as ET
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
records=json.loads((OUT/'verification.json').read_text());methods=json.loads((OUT/'focused-methods.json').read_text())
focused=[r for r in records if r['label'].startswith('focused-')]
showcase=[r for r in records if r['label'].startswith('showcase-')]
suites=[r for r in records if r['label'].startswith('suite-')]
isolated=[r for r in records if r['label'].startswith(('post-isolate-','focused-isolate-'))]
coverage={}
for project,method in methods:
    matching=[r for r in focused if 'FullyQualifiedName='+method in r['command']]
    last_failure=max((i for i,r in enumerate(matching) if r['exit_code']),default=-1)
    passes=[r for r in matching[last_failure+1:] if r['exit_code']==0 and int(r['counters']['passed'])>0 and not r['skips']]
    coverage[method]=len(passes)
assert all(n==3 for n in coverage.values()),{m:n for m,n in coverage.items() if n!=3}
assert len(showcase)==5 and len(suites)==3

def totals(rows):
    return {**{key:sum(int(r.get('counters',{}).get(key,0)) for r in rows) for key in ('total','passed','failed','notExecuted')},'skipped':sum(len(r.get('skips',[])) for r in rows)}
failures=[f for r in suites+showcase+focused for f in r.get('failures',[])]
qualification={}
for method in sorted({f['method'] for f in failures}):
    last_failure=max(i for i,r in enumerate(records) if any(f['method']==method for f in r.get('failures',[])))
    matches=[r for i,r in enumerate(records) if i>last_failure and r in isolated and 'FullyQualifiedName='+method in r['command']]
    qualification[method]={'invocations':len(matches),'all_passed':len(matches)==3 and all(r['exit_code']==0 and int(r['counters']['passed'])>0 and not r['skips'] for r in matches),'counts':totals(matches)}
assert all(q['all_passed'] for q in qualification.values()),qualification
summary=dict(focused=dict(methods=len(methods),invocations=len(focused),three_passes_after_last_failure=True,**totals(focused)),showcase=totals(showcase),suites={r['label']:{**r['counters'],'outcome_skipped':len(r['skips'])} for r in suites},failures=failures,failure_qualification=qualification)
(OUT/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
# Keep case outcomes and test identities independently inspectable after archiving.
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
cases=[]
for r in records:
    for c in ET.parse(OUT/(r['label']+'.trx')).findall('.//t:UnitTestResult',ns):
        cases.append(dict(invocation=r['label'],name=c.attrib['testName'],outcome=c.attrib['outcome'],duration=c.attrib.get('duration')))
(OUT/'case-results.json').write_text(json.dumps(cases,indent=2)+'\n')
# Check no one mutated the merge metadata during this file-only task.
gitdir=Path((ROOT/'.git').read_text().strip().split(': ',1)[1])
assert all(hashlib.sha256((gitdir/name).read_bytes()).hexdigest()==digest for name,digest in json.loads((OUT/'metadata-fingerprints.json').read_text()).items())
(OUT/'final-checks.json').write_text(json.dumps(dict(merge_metadata_unchanged=True,no_git_commands_used=True,guards_excluded_because_they_invoke_git=['MeshyGuardTests','PrivatePathGuardTests']),indent=2)+'\n')
paths=sorted(p for folder in ('src','tests','scripts') for p in (ROOT/folder).rglob('*') if p.is_file() and p.suffix in ('.cs','.csproj','.props','.targets','.slnx','.py','.mgcb') and not {'bin','obj','__pycache__'} & set(p.parts))
(OUT/'source-fingerprints.json').write_text(json.dumps({str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},indent=2)+'\n')
logs=sorted([*OUT.glob('*.log'),*OUT.glob('*.trx')])
with tarfile.open(OUT/'validation-logs.tar.gz','w:gz') as archive:
    for p in logs:archive.add(p,arcname=p.name)
for p in logs:
    if p.name not in ('build.log','build-final.log','progress.log'):p.unlink()
with tarfile.open(OUT/'conflict-inputs.tar.gz','w:gz') as archive:archive.add(OUT/'before',arcname='before')
import shutil
shutil.rmtree(OUT/'before')
(OUT/'evidence-manifest.json').write_text(json.dumps({p.name:dict(bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted(OUT.iterdir()) if p.is_file() and p.name!='evidence-manifest.json'},indent=2)+'\n')
print(json.dumps(summary,indent=2))
