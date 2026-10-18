#!/usr/bin/env python3
"""Check the completed P708 invocation ledger and enrich actual skip outcomes."""
import collections,gzip,hashlib,json,subprocess,xml.etree.ElementTree as E
from pathlib import Path
out=Path(__file__).resolve().parent;root=out.parents[2]
rows=json.loads((out/'verification.json').read_text());ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
for row in rows:
    if 'evidence' not in row:continue
    tree=E.fromstring(gzip.open(out/row['evidence'],'rb').read())
    results=tree.findall('.//t:UnitTestResult',ns)
    row['observed_skipped']=sum(r.attrib['outcome']=='NotExecuted' for r in results)
    row['skipped_tests']=[r.attrib['testName'] for r in results if r.attrib['outcome']=='NotExecuted']
(out/'verification.json').write_text(json.dumps(rows,indent=2)+'\n')
repair=json.loads((out/'repair-verification.json').read_text())
main=[r for r in rows if r['name'].startswith('focus-final-')]
affected=collections.Counter(r['filter'].removeprefix('FullyQualifiedName=') for r in main)
affected.update(r['method'] for r in repair)
assert len(affected)==79 and set(affected.values())=={3}
assert all(r['exit']==0 and int(r['counters']['total'])>0 for r in main+repair)
gate=[r for r in rows if r['name'].startswith('parking-final-')]
baseline=[r for r in rows if r['name'].startswith('entry-baseline-')]
recovery=[r for r in rows if r['name'].startswith('parking-retry-')]
assert len(gate)==10 and len(baseline)==3 and len(recovery)==3
for row in gate+baseline+recovery:assert int(row['counters']['total'])==1 and row['observed_skipped']==0
suites={r['name']:r for r in rows if r['name'].startswith('suite-') and not r['name'].startswith('suite-retry-')}
assert set(suites)=={'suite-Core','suite-Core-final','suite-ClientCore','suite-ClientCore.MonoGame'}
failures=[(r['project'],f['method']) for r in suites.values() for f in r.get('failures',[])]
classifications=[]
for project,method in sorted(set(failures)):
    retries=[r for r in rows if r['name'].startswith('suite-retry-') and r['project']==project and r['filter']=='FullyQualifiedName='+method]
    assert len(retries)==3 and all(int(r['counters']['total'])>0 for r in retries)
    classifications.append({'project':project,'method':method,'isolated_passes':sum(r['exit']==0 for r in retries),
        'classification':'load flake under brief rule' if all(r['exit']==0 for r in retries) else 'unresolved',
        'evidence':[r['evidence'] for r in retries]})
manifest=json.loads((out/'source-manifest.json').read_text())
assert all(hashlib.sha256((root/p).read_bytes()).hexdigest()==h for p,h in manifest['source'].items())
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()==manifest['base']
summary={'affected_methods':79,'affected_invocations':len(main)+len(repair),
    'affected_cases':sum(int(r['counters']['passed']) for r in main+repair),
    'parking_gate':{'passes':sum(r['exit']==0 for r in gate),'failures':sum(r['exit']!=0 for r in gate),'clean':all(r['exit']==0 for r in gate),'version_manifest':'source-manifest-before-repair-release.json'},
    'entry_revision_parking':{'passes':sum(r['exit']==0 for r in baseline),'failures':sum(r['exit']!=0 for r in baseline),'manifest':'baseline-manifest.json'},
    'final_build_parking_recovery':{'passes':sum(r['exit']==0 for r in recovery),'failures':sum(r['exit']!=0 for r in recovery)},
    'suite_load_classifications':classifications,'suites':{k:{'exit':r['exit'],'counters':r['counters'],'skipped_tests':r['skipped_tests'],'evidence':r['evidence']} for k,r in suites.items()},
    'source_unchanged_since_final_build':True,'git_head_unchanged':True}
(out/'final-verification.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary,indent=2))
