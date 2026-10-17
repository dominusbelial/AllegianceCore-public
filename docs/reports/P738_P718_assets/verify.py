#!/usr/bin/env python3
"""P738/P718 gates. Serial fresh test processes; no git commands."""
import json, os, re, subprocess, time
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
ENV=dict(os.environ)
for line in (ROOT/'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key,value=line.split('=',1);ENV[key]=value
ENV['ALLEGIANCE_LIVE_P660_SHOWCASE']='1'
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=[]

def test(label,project,filter_text=None):
    assembly=ROOT/f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
    command=['dotnet','test',str(assembly),'--nologo','--logger',f'trx;LogFileName={label}.trx','--results-directory',str(OUT)]
    if filter_text:command+=['--filter',filter_text]
    start=time.time()
    with (OUT/f'{label}.log').open('w') as log:
        run=subprocess.run(command,cwd=ROOT,env=ENV,stdout=log,stderr=subprocess.STDOUT)
    r=dict(label=label,command=command,exit_code=run.returncode,seconds=round(time.time()-start,3))
    trx=OUT/f'{label}.trx'
    if trx.exists():
        tree=ET.parse(trx)
        r['counters']=tree.find('.//t:Counters',NS).attrib
        methods={}
        for d in tree.findall('.//t:UnitTest',NS):
            m=d.find('t:TestMethod',NS);methods[d.attrib['id']]=m.attrib['className'].split(',')[0]+'.'+m.attrib['name']
        r['failures']=[dict(method=methods[c.attrib['testId']],name=c.attrib['testName'],message=''.join(c.itertext())) for c in tree.findall('.//t:UnitTestResult',NS) if c.attrib['outcome']=='Failed']
        r['skips']=[c.attrib['testName'] for c in tree.findall('.//t:UnitTestResult',NS) if c.attrib['outcome']=='NotExecuted']
    records.append(r)
    (OUT/'verification.json').write_text(json.dumps(records,indent=2)+'\n')
    print(json.dumps({k:r[k] for k in ('label','exit_code','seconds','counters') if k in r}),flush=True)
    return r

def main():
    prior=json.loads((ROOT/'docs/reports/P718_RESULTS.json').read_text())['invocations']
    selected=set()
    for r in prior:
        if r['label'].startswith('isolated-'):
            for method in r['methods']:selected.add((r['project'],method))
    extra={
        'Core':['P674ParityScorecardTests'],
        'ClientCore':['ClientWorldSoundEventTests','P718InterfaceMessageTests','ShipStatusAlarmSoundTests','P713LobbyScreenStateTests','P703ResearchCommanderTests','P703ResearchCommanderLiveTests','P732VoiceOverTests'],
        'ClientCore.MonoGame':['TeamPaneLayoutTests','P713LobbyScreenLiveTests','P717DeploySoundTests','P719StationEconomySoundTests','P714AnnounceSoundTests','P664StationEventSoundTests','P680CountdownSoundTests','P723SoundAssetTests','DockedTeleportAndChatSnapshotConcurrencyTests'],
    }
    for project,classes in extra.items():
        for cls in classes:
            source=(ROOT/f'tests/Allegiance.{project}.Tests/{cls}.cs').read_text()
            namespace=re.search(r'namespace ([\w.]+);',source)[1]
            for method in re.findall(r'public (?:async )?(?:void|Task) (\w+)\(',source):
                if method!='Dispose':selected.add((project,f'{namespace}.{cls}.{method}'))
    selected=sorted(selected)
    (OUT/'focused-methods.json').write_text(json.dumps(selected,indent=2)+'\n')
    print('Selected methods:',len(selected),flush=True)
    for index,(project,method) in enumerate(selected):
        for repeat in range(1,4):
            r=test(f'focused-{index:03}-{repeat}',project,'FullyQualifiedName='+method)
            if r['exit_code'] or int(r.get('counters',{}).get('passed','0'))==0:
                print('Focused failure; continuing to retain all triplets: '+method,flush=True)
    for repeat in range(1,6):
        test(f'showcase-{repeat}','ClientCore','FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    for project in ('Core','ClientCore','ClientCore.MonoGame'):
        # These two guards invoke git, which the user explicitly prohibits.
        f='FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests' if project=='Core' else None
        test('suite-'+project,project,f)
    print('All scheduled gates finished; inspect failures for required isolation.',flush=True)

if __name__=='__main__':main()
