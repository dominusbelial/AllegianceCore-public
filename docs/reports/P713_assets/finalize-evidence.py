import json,hashlib,xml.etree.ElementTree as E
from pathlib import Path
root=Path.cwd();assets=root/'docs/reports/P713_assets';scratch=root/'.verify-p713'
rows=json.loads((assets/'verification.json').read_text());ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
projects=['Allegiance.Core.Tests','Allegiance.ClientCore.Tests','Allegiance.ClientCore.MonoGame.Tests','Allegiance.Lobby.Tests']
suites=[r for r in rows if r['label'].startswith('suite-')]
assert len(suites)==8 and len({r['label'] for r in suites})==8
assert {r['label'] for r in suites}=={f'suite-{i}-{p}' for i in (1,2) for p in projects}
summary=[]
for i in (1,2):
    for project in projects:
        r=next(r for r in suites if r['label']==f'suite-{i}-{project}')
        trx=scratch/r['label']/'results.trx';doc=E.parse(trx)
        results=doc.findall('.//t:UnitTestResult',ns)
        skipped=[x.get('testName') for x in results if x.get('outcome')=='NotExecuted']
        summary.append({'sweep':i,'project':project,'label':r['label'],'passed':int(r['counters']['passed']),
            'failed':int(r['counters']['failed']),'skipped':len(skipped),'skip_names':skipped,'seconds':r['seconds'],
            'failures':r['failures'],'trx_sha256':hashlib.sha256(trx.read_bytes()).hexdigest()})
failures=sorted({(r['project'],f['method']) for r in suites for f in r['failures']})
isolations=[]
for project,method in failures:
    selected=[r for r in rows if r['label'].startswith('retry-') and r['project']==project and r['filter']=='FullyQualifiedName='+method]
    assert len(selected)==3,(method,len(selected))
    assert all(r['exit']==0 and int(r['counters']['passed'])>0 and not r['failures'] for r in selected),method
    isolations.append({'project':project,'method':method,'labels':[r['label'] for r in selected],
        'case_passes':sum(int(r['counters']['passed']) for r in selected),
        'classification':'corrected fixture' if '.LeaderSameSideBotClientLiveTests.' in method else 'load/timing flake under P713 triplet criterion'})
affected=json.loads((assets/'affected-certificate.json').read_text());showcase=json.loads((assets/'showcase-certificate.json').read_text())
assert affected['methods']==115 and affected['individual_invocations']==345 and affected['case_passes']==384
assert all(hashlib.sha256((root/f).read_bytes()).hexdigest()==v for f,v in affected['source_sha256'].items())
assert showcase['passed']==10 and showcase['failed']==0 and len(showcase['labels'])==10
assert json.loads((assets/'final-build-fingerprint.json').read_text())['exit']==0
assert json.loads((assets/'post-fixture-build-fingerprint.json').read_text())['exit']==0
result={'base_revision':'f3948b57','full_sweeps':2,'suites':summary,
    'totals':{k:sum(s[k] for s in summary) for k in ('passed','failed','skipped')},
    'affected':{'methods':115,'individual_invocations':345,'case_passes':384},
    'showcase':showcase,'post_suite_isolations':isolations,'final_source_sha256':affected['source_sha256']}
(assets/'validation-summary.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({'totals':result['totals'],'isolations':isolations},indent=2))
