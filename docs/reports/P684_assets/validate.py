#!/usr/bin/env python3
"""P684 verification; run from repository root after the documented build."""
import json, os, subprocess, time, xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / 'docs/reports/P684_assets/final'
OUT.mkdir(parents=True, exist_ok=True)
os.chdir(ROOT)
ENV = os.environ.copy()
ENV.update(ALLEGIANCE_ARTWORK_PATH='/home/domingo/Development/Artwork',
           ALLEGIANCE_NATIVE_SRC_PATH='/home/domingo/Development/Allegiance',
           NUGET_HTTP_CACHE_PATH='/tmp/p684-nuget-cache', ALLEGIANCE_LIVE_P660_SHOWCASE='1')
for flag in ('ALLEGIANCE_BROADCAST_SNAPSHOT','ALLEGIANCE_BROADCAST_ENCODE_ONCE','ALLEGIANCE_BROADCAST_TIERS'):
    ENV.pop(flag, None)
NS = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records = []
def run(label, project, filt=None, overrides=None):
    cmd=['dotnet','test',f'tests/Allegiance.{project}.Tests','--no-build','--nologo','-v:m',
         '--logger',f'trx;LogFileName={label}.trx','--results-directory',str(OUT)]
    if filt: cmd += ['--filter',filt]
    start=time.monotonic()
    with (OUT / f'{label}.log').open('w') as log:
        process=subprocess.run(cmd,env=ENV | (overrides or {}),stdout=log,stderr=subprocess.STDOUT)
    r={'label':label,'project':project,'filter':filt,'overrides':overrides or {},
       'exit':process.returncode,'seconds':round(time.monotonic()-start,2)}
    trx=OUT / f'{label}.trx'
    if trx.exists():
        xml=ET.parse(trx)
        counters=xml.find('.//t:Counters',NS)
        r['counts']=dict(counters.attrib) if counters is not None else {}
        methods={t.attrib['id']: t.find('t:TestMethod',NS).attrib for t in xml.findall('.//t:UnitTest',NS)}
        r['failures']=[]
        for t in xml.findall('.//t:UnitTestResult',NS):
            if t.attrib.get('outcome') == 'Failed':
                method=methods[t.attrib['testId']]
                r['failures'].append(method['className'].split(',')[0]+'.'+method['name'])
    if project == 'Core' and filt == pipeline:
        import shutil
        for artifact in Path('/tmp').glob('p669-identity-*.json'):
            shutil.copyfile(artifact, OUT / f'{label}-{artifact.name}')
        allocation = Path('/tmp/p679-allocation.json')
        if allocation.exists(): shutil.copyfile(allocation, OUT / f'{label}-allocation.json')
    records.append(r)
    (OUT / 'results.json').write_text(json.dumps(records,indent=2)+'\n')
    print(json.dumps(r),flush=True)
    return r

pipeline='|'.join('FullyQualifiedName~'+s for s in [
    'BroadcastSnapshotTests','BroadcastEncodeOnceTests','BroadcastSnapshotLoopbackTests',
    'ParallelBroadcastByteIdentityTests','InterestTierTests','InterestTierCombatTests','PipelineCostTests','P684MergeTests'])
if __name__ == '__main__':
    import sys
    phase=sys.argv[1] if len(sys.argv)>1 else 'all'
    if (OUT / 'results.json').exists(): records.extend(json.loads((OUT / 'results.json').read_text()))
    if phase in ('focus','all'):
        for rep in range(1,4):
            run(f'pipeline-{rep}', 'Core',pipeline)
            run(f'rotation-{rep}', 'ClientCore','FullyQualifiedName~P682')
            run(f'client-tiers-{rep}', 'ClientCore','FullyQualifiedName~Interest_tier_TCP')
    if phase in ('preserved','integration','all'):
        filt='|'.join('FullyQualifiedName~'+s for s in [
            'P655Game','P670SendStall','P671KeepAlive','P673ReceiveDecouple','UpdateCadenceTests',
            'ScoreRoster','Pooled','PartialWrite'])
        run('preserved-features','Core',filt)
        if phase != 'preserved':
            run('showcase-pipeline','ClientCore','FullyQualifiedName~P660ShowcaseLiveTests',
                {k:'1' for k in ('ALLEGIANCE_BROADCAST_SNAPSHOT','ALLEGIANCE_BROADCAST_ENCODE_ONCE','ALLEGIANCE_BROADCAST_TIERS')})
    if phase in ('suites','all'):
        suites=[run('suite-'+project.lower(),project) for project in ('Lobby','Core','ClientCore','ClientCore.MonoGame')]
        for r in suites:
            for i, method in enumerate(sorted(set(r.get('failures',[])))):
                for rep in range(1,4):
                    run(f"isolation-{r['project'].lower()}-{i+1}-{rep}",r['project'],'FullyQualifiedName='+method)

    # Suite receipt flakes qualify only after three exact-method isolated passes.
    uncleared = []
    for record in records:
        if not record['exit']: continue
        methods = set(record.get('failures', []))
        cleared = record['label'].startswith('suite-') and methods and all(
            len([r for r in records if r['label'].startswith('isolation-')
                 and r['project'] == record['project']
                 and r['filter'] == 'FullyQualifiedName=' + method and r['exit'] == 0]) >= 3
            for method in methods)
        if not cleared: uncleared.append(record['label'])
    print(json.dumps({'uncleared': uncleared}), flush=True)
    raise SystemExit(bool(uncleared))
