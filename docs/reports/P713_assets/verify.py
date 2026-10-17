import os, sys, re, json, time, subprocess, hashlib, xml.etree.ElementTree as E
from pathlib import Path
root=Path.cwd(); out=root/'.verify-p713'; ledger=root/'docs/reports/P713_assets/verification.json'
env=os.environ.copy()
for line in (root/'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        k,v=line.split('=',1);env[k]=v
for k in ['ALLEGIANCE_LIVE_P542_PROOF','ALLEGIANCE_LIVE_P660_SHOWCASE','ALLEGIANCE_LIVE_CAPTURE_PROOF','ALLEGIANCE_LIVE_P539_BASELINE','ALLEGIANCE_LIVE_P539C_REPRO']: env[k]='1'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def run(label, project, filt=None):
    cohort_key=label
    attempt=1
    while (out/label/'results.trx').exists():
        attempt+=1;label=cohort_key+f'-attempt{attempt}'
    directory=out/label;directory.mkdir(parents=True,exist_ok=True)
    command=['dotnet','test',str(root/'tests'/project),'--no-build','--nologo','--logger','trx;LogFileName=results.trx','--results-directory',str(directory)]
    if filt:command+=['--filter',filt]
    started=time.time()
    with (directory/'output.log').open('w') as f:
        rc=subprocess.run(command,env=env,stdout=f,stderr=subprocess.STDOUT).returncode
    result={'label':label,'cohort_key':cohort_key,'project':project,'filter':filt,'command':command,'started_unix':started,'seconds':round(time.time()-started,3),'exit':rc,'failures':[],'environment':{k:v for k,v in env.items() if k.startswith('ALLEGIANCE_LIVE_')}}
    trx=directory/'results.trx'
    if trx.exists():
        doc=E.parse(trx); counters=doc.find('.//t:Counters',ns)
        result['counters']=counters.attrib if counters is not None else {}
        methods={x.get('id'):x.find('t:TestMethod',ns).attrib for x in doc.findall('.//t:UnitTest',ns)}
        for x in doc.findall('.//t:UnitTestResult',ns):
            if x.get('outcome')=='Failed':
                method=methods[x.get('testId')];result['failures'].append({'method':method['className'].split(',')[0]+'.'+method['name'],'display':x.get('testName'),'detail':''.join(x.itertext())})
        result['trx_sha256']=hashlib.sha256(trx.read_bytes()).hexdigest()
    rows=json.loads(ledger.read_text()) if ledger.exists() else []
    rows.append(result);ledger.write_text(json.dumps(rows,indent=2)+'\n')
    print(label,rc,result.get('counters'),flush=True)
    if result['failures']:print(json.dumps(result['failures'],indent=2),flush=True)
    return rc
classes={
'Allegiance.Core.Tests':['LiveTeamLobbyTests','LiveTeamLeadershipTests','LivePositionRequestTests','PlayerReadyTests','LiveMissionStageTests','LiveEnterGameTests','LiveTeamLobbyToggleTests','SetWingIdTests','Wire/MissionDefJoinedMissionWireTests','Wire/P680CountdownWireTests','Wire/P713LobbyWireTests'],
'Allegiance.ClientCore.Tests':['P713LobbyScreenStateTests','P675MissionDiscoveryTests','P675CommandLiveTests','ReadyCheckLiveTests','PlayerReadyLiveTests','LeaderSameSideBotClientLiveTests'],
'Allegiance.ClientCore.MonoGame.Tests':['ReadyCheckLayoutTests','ReadyCheckRosterTests','TeamPaneLayoutTests','SideSelectLayoutTests','P713LobbyScreenLiveTests']}
mode=sys.argv[1]
if mode=='showcase' and (out/'defer-showcase').exists():
    print('Showcase deferred for final wing snapshot correction',flush=True);sys.exit(0)
if mode=='affected':
    existing={r.get('cohort_key',r['label']) for r in json.loads(ledger.read_text()) if r['exit']==0} if ledger.exists() else set()
    methods=[]
    for p, cs in classes.items():
        for c in cs:
            s=(root/'tests'/p/(c+'.cs')).read_text()
            for name in re.findall(r'public (?:async )?(?:void|Task) (\w+)\(',s):
                methods.append((p,re.search(r'namespace ([\w.]+);',s).group(1)+'.'+c.split('/')[-1]+'.'+name))
    methods.append(('Allegiance.ClientCore.MonoGame.Tests','Allegiance.ClientCore.MonoGame.Tests.P675CommandActionTests.Join_order_changes_wing_without_targeting_or_overwriting_accepted_or_engaging'))
    (out/'affected-methods.json').write_text(json.dumps(methods,indent=2))
    for index,(p,m) in enumerate(methods):
        for n in range(1,4):
            label=os.environ.get('P713_AFFECTED_PREFIX','final-affected')+f'-{index:03}-{n}'
            if label in existing:continue
            if run(label,p,'FullyQualifiedName='+m):sys.exit(1)
elif mode=='certify':
    rows=json.loads(ledger.read_text())
    cutoff=max(r['started_unix']+r['seconds'] for r in rows if r['label'].startswith('affected-'))+1
    methods=json.loads((out/'affected-methods.json').read_text())
    chosen=[]
    for index,(p,m) in enumerate(methods):
        candidates=[r for r in rows if r.get('cohort_key',r['label']).startswith(f'final-affected-{index:03}-')]
        valid=len(candidates)==3 and all(r['exit']==0 and r['started_unix']>=cutoff for r in candidates)
        if not valid:
            for n in range(1,4):
                if run(f'serial-affected-{index:03}-{n}',p,'FullyQualifiedName='+m):sys.exit(1)
            rows=json.loads(ledger.read_text())
            candidates=[r for r in rows if r['label'].startswith(f'serial-affected-{index:03}-')]
        assert len(candidates)==3 and all(int(r['counters']['passed'])>0 and r['exit']==0 for r in candidates)
        chosen.append({'method':m,'project':p,'labels':[r['label'] for r in candidates],
            'case_passes':sum(int(r['counters']['passed']) for r in candidates)})
    subprocess.run(['git','diff','--check'],check=True)
    files=subprocess.check_output(['git','diff','--name-only'],text=True).splitlines()+subprocess.check_output(['git','ls-files','--others','--exclude-standard'],text=True).splitlines()
    hashes={f:hashlib.sha256((root/f).read_bytes()).hexdigest() for f in sorted(set(files)) if f.endswith('.cs')}
    certificate={'methods':len(chosen),'individual_invocations':len(chosen)*3,'case_passes':sum(r['case_passes'] for r in chosen),
        'earlier_runner_end_cutoff_unix':cutoff,'source_sha256':hashes,'cohort':chosen}
    (root/'docs/reports/P713_assets/affected-certificate.json').write_text(json.dumps(certificate,indent=2)+'\n')
    print('CERTIFIED',certificate['methods'],certificate['individual_invocations'],certificate['case_passes'],flush=True)
elif mode=='showcase':
    for n in range(1,11):run(f'showcase-{n:02}','Allegiance.ClientCore.Tests','FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
elif mode=='suites':
    for n in ([int(sys.argv[2])] if len(sys.argv)>2 else range(1,3)):
        for p in ['Allegiance.Core.Tests','Allegiance.ClientCore.Tests','Allegiance.ClientCore.MonoGame.Tests','Allegiance.Lobby.Tests']:
            run(f'suite-{n}-{p}',p)
elif mode=='retry':
    rows=json.loads(ledger.read_text()); failures={(r['project'],f['method']) for r in rows if r['label'].startswith('suite-') for f in r['failures']}
    for index,(p,m) in enumerate(sorted(failures)):
        for n in range(1,4):run(f'retry-{index:02}-{n}',p,'FullyQualifiedName='+m)
