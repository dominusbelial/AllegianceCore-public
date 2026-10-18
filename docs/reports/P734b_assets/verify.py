#!/usr/bin/env python3
"""P734b light gate: exact-method triplets, five parking runs, ONE suite sweep.
Run after building the solution. Each test process is awaited serially.
"""
import gzip, json, os, re, subprocess, sys, time
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
os.chdir(ROOT)
if (ROOT/'local.env').exists():
    for line in (ROOT/'local.env').read_text().splitlines():
        if line.strip() and not line.lstrip().startswith('#') and '=' in line:
            k,v=line.split('=',1);os.environ[k.strip()]=v.strip().strip('"').strip("'")
os.environ.update(NUGET_HTTP_CACHE_PATH='/tmp/p734b-nuget-http',ALLEGIANCE_LIVE_P542_PROOF='1',ALLEGIANCE_LIVE_P660_SHOWCASE='1')
LEDGER=OUT/'verification.json'
rows=json.loads(LEDGER.read_text()) if LEDGER.exists() else []
def test(project,name,method=None):
    if any(r['name']==name for r in rows): raise RuntimeError('Refusing to overwrite an invocation: '+name)
    dll=ROOT/f'tests/Allegiance.{project}.Tests/bin/Debug/net10.0/Allegiance.{project}.Tests.dll'
    cmd=['dotnet','vstest',str(dll),'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(OUT)]
    if method:cmd+=['--TestCaseFilter:FullyQualifiedName='+method]
    start=time.time()
    with (OUT/(name+'.log')).open('w') as log: proc=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT)
    row=dict(name=name,project=project,method=method,command=cmd,started=start,seconds=round(time.time()-start,3),exit=proc.returncode)
    trx=OUT/(name+'.trx')
    if trx.exists():
        tree=ET.parse(trx); row['counters']=tree.find('.//t:Counters',NS).attrib
        defs={d.attrib['id']:d.find('t:TestMethod',NS).attrib for d in tree.findall('.//t:UnitTest',NS)}
        results=tree.findall('.//t:UnitTestResult',NS)
        row['skipped']=[r.attrib['testName'] for r in results if r.attrib['outcome']=='NotExecuted']
        row['failures']=[]
        for r in results:
            if r.attrib['outcome']=='Failed':
                m=defs[r.attrib['testId']]
                row['failures'].append(dict(method=m['className']+'.'+m['name'],message=r.findtext('.//t:Message','',NS),stack=r.findtext('.//t:StackTrace','',NS)))
        row['output']=[r.findtext('t:Output/t:StdOut','',NS) for r in results if r.findtext('t:Output/t:StdOut','',NS)]
        row['evidence']=trx.name+'.gz'
        with gzip.open(str(trx)+'.gz','wb') as z:z.write(trx.read_bytes())
        trx.unlink()
    rows.append(row);LEDGER.write_text(json.dumps(rows,indent=2)+'\n')
    valid='counters' in row and int(row['counters']['executed'])>0
    print(f"{name}: exit={row['exit']} counters={row.get('counters',{})} ({row['seconds']}s)",flush=True)
    if not valid:raise RuntimeError('Incomplete or empty invocation: '+name)
    return row

def cohort():
    methods=[]
    # Put the merge regression first, then retain both waves' whole consumer cohorts.
    methods.append(['ClientCore','Allegiance.ClientCore.Tests.P709ClientCallbacksTests.Chat_callback_observes_sound_and_notification_once_on_wire_and_direct_delivery'])
    for path in ['P709_assets/cohort.json','P734_assets/affected-methods-final.json']:
        methods+=json.loads((OUT.parent/path).read_text())
    src=(ROOT/'tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs').read_text()
    methods += [['ClientCore','Allegiance.ClientCore.Tests.ClientWorldSoundEventTests.'+m] for m in re.findall(r'^    public void (\w+)\(',src,re.M)]
    methods=list(dict.fromkeys(tuple(m) for m in methods))
    (OUT/'cohort.json').write_text(json.dumps(methods,indent=2)+'\n')
    return methods

if __name__=='__main__':
    mode=sys.argv[1]
    if mode=='focus':
        for idx,(project,method) in enumerate(cohort()):
            for run in range(1,4):
                name=f'focus-{idx:03}-{run}'
                if any(r['name']==name for r in rows):continue
                test(project,name,method)
    elif mode=='parking':
        for run in range(1,6):
            test('ClientCore',f'parking-{run:02}','Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    elif mode=='suites':
        if any(r['name'].startswith('suite-') and not r['name'].startswith('suite-retry-') for r in rows):raise RuntimeError('One sweep only')
        for project in ['Core','ClientCore','ClientCore.MonoGame','Lobby']:test(project,'suite-'+project)
    elif mode=='retries':
        cases=sorted({(r['project'],f['method']) for r in rows if r['name'].startswith(('suite-','focus-')) and not r['name'].startswith('suite-retry-') for f in r.get('failures',[])})
        for idx,(project,method) in enumerate(cases):
            for run in range(1,4):test(project,f'suite-retry-{idx:03}-{run}',method)
    else:raise ValueError(mode)
