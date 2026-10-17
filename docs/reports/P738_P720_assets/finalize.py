#!/usr/bin/env python3
"""Summarize completed P738/P720 gates and archive raw evidence."""
from pathlib import Path
import collections,datetime,hashlib,json,re,tarfile,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
records=json.loads((OUT/'verification.json').read_text())
selected=json.loads((OUT/'focused-methods.json').read_text())
labels={r['label'] for r in records}
assert len(labels)==len(records),'Duplicate invocation labels'
for r in records:
 if r['label'].startswith('focused-'):
  assert int(r.get('counters',{}).get('passed',0))>0 and not r.get('skips'),r['label']+' has missing or skipped cases'
for i,_ in enumerate(selected):
 for rep in range(1,4):assert f'focused-{i:03}-{rep}' in labels
for rep in range(1,6):assert f'showcase-{rep}' in labels
for p in ['Core','ClientCore','ClientCore.MonoGame']:assert 'suite-'+p in labels
fingerprints=json.loads((OUT/'source-fingerprints.json').read_text())
assert all(hashlib.sha256((ROOT/p).read_bytes()).hexdigest()==h for p,h in fingerprints.items()),'Sources changed after build/focused gates'
build=json.loads((OUT/'build-final.json').read_text())
assert all(hashlib.sha256((ROOT/p).read_bytes()).hexdigest()==h for p,h in build['assemblies'].items()),'Test binaries changed after build'
failed={}
for r in records:
 if r['label'].startswith('post-isolate-'):continue
 for f in r.get('failures',[]):
  selector='FullyQualifiedName='+f['method']
  if 'P703ResearchCommanderLiveTests.Both_sides_complete_' in f['method']:
   selector+='&DisplayName~civA: '+re.search(r'civA: (\d+),',f['name'])[1]+','
  failed[selector]=f
classifications=[]
for selector,f in sorted(failed.items()):
 triplet=[r for r in records if r['label'].startswith('post-isolate-') and r['command'][-1]==selector]
 assert len(triplet)==3 and all(r['exit_code']==0 and int(r['counters']['passed'])>0 for r in triplet),selector+' not cleared by 3/3 isolation'
 classifications.append({'method':f['method'],'case':f['name'],'selector':selector,'classification':'load flake','post_suite_isolation':'3/3 passed'})
showcases=[r for r in records if r['label'].startswith('showcase-')]
assert len(showcases)==5 and all(r['exit_code']==0 and int(r['counters']['passed'])==1 for r in showcases),'Showcase failures need base-rate comparison before finalizing'
summary={'task':'P738 merge codex/p720 onto current master','completed_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'focused_methods':len(selected),'focused_invocations':len(selected)*3,'full_suite_exclusions':['MeshyGuardTests','PrivatePathGuardTests (both invoke git)'],'failure_classifications':classifications,'phases':{}}
for phase in ['focused','showcase','suite','post-isolate']:
 runs=[r for r in records if r['label'].startswith(phase+'-')]
 summary['phases'][phase]={'invocations':len(runs),'exit_failures':sum(r['exit_code']!=0 for r in runs),'seconds':round(sum(r['seconds'] for r in runs),3),'passed':sum(int(r.get('counters',{}).get('passed',0)) for r in runs),'failed':sum(int(r.get('counters',{}).get('failed',0)) for r in runs),'skipped':sum(len(r.get('skips',[])) for r in runs)}
summary['focused_by_assembly']={}
for project in ['Core','ClientCore','ClientCore.MonoGame']:
 ids={i for i,(p,m) in enumerate(selected) if p==project}
 runs=[r for r in records if r['label'].startswith('focused-') and int(r['label'].split('-')[1]) in ids]
 summary['focused_by_assembly'][project]={'methods':len(ids),'invocations':len(runs),'passed':sum(int(r['counters']['passed']) for r in runs),'failed':sum(int(r['counters']['failed']) for r in runs),'skipped':sum(len(r.get('skips',[])) for r in runs)}
summary['suites']=[{k:r[k] for k in ['label','exit_code','seconds','counters','failures','skips']} for r in records if r['label'].startswith('suite-')]
(OUT/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};cases=[]
for r in records:
 tree=ET.parse(OUT/(r['label']+'.trx'))
 for c in tree.findall('.//t:UnitTestResult',ns):cases.append({'invocation':r['label'],'name':c.attrib['testName'],'outcome':c.attrib['outcome'],'duration':c.attrib.get('duration')})
(OUT/'case-results.json').write_text(json.dumps(cases,indent=2)+'\n')
with tarfile.open(OUT/'validation-logs.tar.gz','w:gz') as tar:
 for p in sorted(OUT.iterdir()):
  if p.suffix in ['.log','.trx']:tar.add(p,arcname=p.name)
manifest={p.name:{'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in OUT.iterdir() if p.is_file() and p.suffix in ['.json','.txt','.gz','.py'] and p.name!='evidence-manifest.json'}
(OUT/'evidence-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(summary,indent=2))
