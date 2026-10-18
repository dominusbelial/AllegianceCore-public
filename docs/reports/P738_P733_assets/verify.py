"""P738 gates: fresh isolated methods three times, showcase five, suites once."""
import fcntl,json,os,re,signal,subprocess,time,uuid,xml.etree.ElementTree as ET
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
ENV=dict(os.environ)
for line in (ROOT/'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key,value=line.split('=',1);ENV[key]=value
for flag in ('ALLEGIANCE_LIVE_P660_SHOWCASE', 'ALLEGIANCE_LIVE_P542_PROOF', 'ALLEGIANCE_LIVE_CAPTURE_PROOF', 'ALLEGIANCE_LIVE_P539_BASELINE', 'ALLEGIANCE_LIVE_P539C_REPRO'):
    ENV[flag]='1'
# Historical 221/200v200 optional proof drivers are delegated by the brief.
for flag in ('ALLEGIANCE_LIVE_P539_PROOF','ALLEGIANCE_LIVE_P539B_PROOF','ALLEGIANCE_LIVE_P539D','ALLEGIANCE_LIVE_P553','ALLEGIANCE_LIVE_P610','ALLEGIANCE_LIVE_P622','ALLEGIANCE_LIVE_P632','ALLEGIANCE_LIVE_P632_ASSERT','ALLEGIANCE_LIVE_P634','ALLEGIANCE_LIVE_P635'):
    ENV.pop(flag,None)
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=json.loads((OUT/'verification.json').read_text()) if (OUT/'verification.json').exists() else []
CONTROL=OUT/'runner-control.txt'
RUN_ID=None

def test(label,project,filter_text=None):
    prior=next((r for r in records if r['label']==label),None)
    if prior:return prior
    assembly=ROOT/f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
    command=['dotnet','test',str(assembly),'--nologo','--logger',f'trx;LogFileName={label}.trx','--results-directory',str(OUT)]
    if filter_text:command+=['--filter',filter_text]
    proof=OUT/('proof-'+label)
    ENV['ALLEGIANCE_P733_PROOF_DIRECTORY']=str(proof)
    ENV['ALLEGIANCE_P705_PROOF_DIRECTORY']=str(proof)
    (OUT/'active-run.json').write_text(json.dumps(dict(label=label,command=command,started=time.time())))
    start=time.time()
    with (OUT/f'{label}.log').open('w') as log:
        run=subprocess.Popen(command,cwd=ROOT,env=ENV,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        while run.poll() is None:
            if RUN_ID is not None and CONTROL.read_text().strip()!=RUN_ID:
                os.killpg(run.pid,signal.SIGTERM)
                try: run.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(run.pid,signal.SIGKILL)
                    run.wait()
                (OUT/f'interrupted-{label}.json').write_text(json.dumps(dict(label=label,command=command,exit_code=run.returncode,reason='Explicit runner control change'),indent=2)+'\n')
                raise SystemExit('Verification stopped by control file')
            time.sleep(1)
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
    (OUT/'active-run.json').write_text(json.dumps(dict(label=label,finished=True,exit_code=run.returncode)))
    print(json.dumps({k:row[k] for k in ('label','exit_code','seconds','counters') if k in row}),flush=True)
    return row

def main():
    global RUN_ID
    # Cross-namespace file lock prevents accidental duplicate drivers.
    lock=(OUT/'runner.lock').open('w')
    fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    RUN_ID=uuid.uuid4().hex
    CONTROL.write_text(RUN_ID+'\n')
    original=json.loads((OUT.parent/'P733_assets/focused-methods.json').read_text())
    selected={('Core' if group=='core' else 'ClientCore', method)
              for group,methods in original.items() for method in methods}
    classes={
        'Core':['P674ParityScorecardTests'],
        'ClientCore':['P496CaptureProofLiveTests','P705TechModeLiveTests','ClientWorldSoundEventTests','ShipStatusAlarmSoundTests','P732VoiceOverTests'],
        'ClientCore.MonoGame':['P714AnnounceSoundTests','P717DeploySoundTests','P719StationEconomySoundTests','P721ThrusterLoopSoundTests','P721ShipLoopBedsTests','P723SoundAssetTests','P723DroneChatterCueTests','P732VoiceCatalogTests','P735SoundAssetTests','P736SoundAssetTests','P737SoundAssetTests','P664StationEventSoundTests','VoiceBudgetTests','CountdownSoundTests','TurnThrusterSoundTests','EngineSoundCurveTests','HitSoundSelectorTests','WeaponSoundMapTests','ProjectileSoundPlannerTests','MissileSoundPlannerTests','StationSoundPlannerTests','WarpSoundPlannerTests','ProbeSoundPlannerTests'],
    }
    for project,names in classes.items():
        for cls in names:
            source=(ROOT/f'tests/Allegiance.{project}.Tests/{cls}.cs').read_text()
            namespace=re.search(r'namespace ([\w.]+);',source)[1]
            for method in re.findall(r'^    public (?:async )?(?:void|Task) (\w+)\(',source,re.M):
                if method!='Dispose': selected.add((project,f'{namespace}.{cls}.{method}'))
    selected.add(('Core','Allegiance.Core.Tests.BuildingConstructionLiveTests.Completed_build_replaces_asteroid_with_station_and_consumes_builder'))
    selected=sorted(selected)
    (OUT/'focused-methods.json').write_text(json.dumps(selected,indent=2)+'\n')
    print('Selected methods: '+str(len(selected)),flush=True)
    # Discover first, so a misspelled method cannot count as a passing invocation.
    for project in ('Core','ClientCore','ClientCore.MonoGame'):
        assembly=ROOT/f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
        result=subprocess.run(['dotnet','test',str(assembly),'--list-tests','--nologo'],cwd=ROOT,env=ENV,capture_output=True,text=True)
        (OUT/f'discovered-{project}.txt').write_text(result.stdout+result.stderr)
        assert result.returncode==0
        for proj,method in selected:
            if proj==project: assert method in result.stdout,method
    for index,(project,method) in enumerate(selected):
        for repeat in range(1,4):
            row=test(f'focused-{index:03}-{repeat}',project,'FullyQualifiedName='+method)
            if 'counters' not in row or int(row['counters']['passed'])==0:
                raise RuntimeError('Focused method absent, skipped or interrupted: '+method)
    for repeat in range(1,6):
        test(f'showcase-{repeat}','ClientCore','FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    for project in ('Core','ClientCore','ClientCore.MonoGame'):
        filt='FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests' if project=='Core' else None
        test('suite-'+project,project,filt)
    print('Scheduled gates complete; inspect failures for required isolation.',flush=True)

if __name__=='__main__':main()

