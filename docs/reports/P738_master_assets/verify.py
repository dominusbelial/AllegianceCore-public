"""P738 gates: fresh isolated methods three times, showcase five, suites once."""
import json,os,re,subprocess,time,xml.etree.ElementTree as ET
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
ENV=dict(os.environ)
for line in (ROOT/'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key,value=line.split('=',1);ENV[key]=value
ENV['ALLEGIANCE_LIVE_P660_SHOWCASE']='1'
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=json.loads((OUT/'verification.json').read_text()) if (OUT/'verification.json').exists() else []

def test(label,project,filter_text=None):
    prior=next((r for r in records if r['label']==label),None)
    if prior:return prior
    assembly=ROOT/f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
    command=['dotnet','test',str(assembly),'--nologo','--logger',f'trx;LogFileName={label}.trx','--results-directory',str(OUT)]
    if filter_text:command+=['--filter',filter_text]
    start=time.time()
    with (OUT/f'{label}.log').open('w') as log:
        run=subprocess.run(command,cwd=ROOT,env=ENV,stdout=log,stderr=subprocess.STDOUT)
    row=dict(label=label,command=command,exit_code=run.returncode,seconds=round(time.time()-start,3))
    trx=OUT/f'{label}.trx'
    if trx.exists():
        tree=ET.parse(trx);row['counters']=tree.find('.//t:Counters',NS).attrib
        methods={}
        for definition in tree.findall('.//t:UnitTest',NS):
            method=definition.find('t:TestMethod',NS);methods[definition.attrib['id']]=method.attrib['className'].split(',')[0]+'.'+method.attrib['name']
        row['failures']=[dict(method=methods[c.attrib['testId']],name=c.attrib['testName'],message=''.join(c.itertext())) for c in tree.findall('.//t:UnitTestResult',NS) if c.attrib['outcome']=='Failed']
        row['skips']=[c.attrib['testName'] for c in tree.findall('.//t:UnitTestResult',NS) if c.attrib['outcome']=='NotExecuted']
    records.append(row);(OUT/'verification.json').write_text(json.dumps(records,indent=2)+'\n')
    print(json.dumps({k:row[k] for k in ('label','exit_code','seconds','counters') if k in row}),flush=True)
    return row

def main():
    classes={
        'Core':['P652EconomyDroneAiTests','P725DroneChatterSeamTests','P725DroneChatterWireLiveTests','P674ParityScorecardTests','AutoReloadOnEmptyTests','CargoBayReloadTests','LiveTreasurePickupTests'],
        'ClientCore':['ClientWorldSoundEventTests','ShipStatusAlarmSoundTests','P732VoiceOverTests','P641FlightPartsReceiveTests','P718InterfaceMessageTests','P718InterfaceLoopbackTests','P722InterfaceLoopbackTests','FedMsgDispatchCoverageTests'],
        'ClientCore.MonoGame':['P735SoundAssetTests','P736SoundAssetTests','P737SoundAssetTests','P714AnnounceSoundTests','P717DeploySoundTests','P719StationEconomySoundTests','P721ThrusterLoopSoundTests','P721ShipLoopBedsTests','P723SoundAssetTests','P723DroneChatterCueTests','P732VoiceCatalogTests','P664StationEventSoundTests','VoiceBudgetTests','WeaponReloadTrackerTests','CountdownSoundTests'],
    }
    selected=[]
    for project,names in classes.items():
        for cls in names:
            path=ROOT/f'tests/Allegiance.{project}.Tests/{cls}.cs';source=path.read_text()
            namespace=re.search(r'namespace ([\w.]+);',source)[1]
            methods=re.findall(r'^    public (?:async )?(?:void|Task) (\w+)\(',source,re.M)
            for method in methods:
                if method!='Dispose':selected.append((project,f'{namespace}.{cls}.{method}'))
    selected=sorted(set(selected));(OUT/'focused-methods.json').write_text(json.dumps(selected,indent=2)+'\n')
    print('Selected methods: '+str(len(selected)),flush=True)
    original=json.loads((OUT/'initial-method-candidates.json').read_text())
    for project,method in selected:
        index=original.index([project,method])
        # A failed live timing attempt stays in the ledger. Three fresh
        # isolated recovery passes qualify it under the lane's flake rule.
        recovery=[r for r in records if r['label'].startswith('focused-isolate-')
                  and 'FullyQualifiedName='+method in r['command']]
        if len(recovery)==3 and all(r['exit_code']==0 and int(r['counters']['passed'])>0 and not r['skips'] for r in recovery):
            continue
        for repeat in range(1,4):
            row=test(f'focused-{index:03}-{repeat}',project,'FullyQualifiedName='+method)
            if row['exit_code'] or int(row.get('counters',{}).get('passed','0'))==0:
                raise RuntimeError('Focused method failed or not discovered: '+method)
    for repeat in range(1,6):
        test(f'showcase-{repeat}','ClientCore','FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    for project in ('Core','ClientCore','ClientCore.MonoGame'):
        # These two repository guards launch git; honor the explicit prohibition.
        f='FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests' if project=='Core' else None
        test('suite-'+project,project,f)
    print('Scheduled gates complete; inspect failures for required isolation.',flush=True)
if __name__=='__main__':main()
