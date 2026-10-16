import subprocess, os, json, pathlib, re, time, xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[3]; out=pathlib.Path(os.environ.get('P697_RESULTS','/tmp/p697-final'));out.mkdir(exist_ok=True)
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
ledger=json.loads((out/'results.json').read_text()) if (out/'results.json').exists() else []
classes={
'Core':['UpdateCadenceTests','UpdateAgeMetricsTests','UpdateCadenceLoopbackTests','ShipUpdateBudgetLiveTests','ShipsUpdateLiveTests','InterestTierTests','InterestTierCombatTests','ProjectileBroadcastTests','MissileBroadcastTests','MineBroadcastTests','ProbeBroadcastTests','CrossSideExportLiveTests','CloakVisibilityGateLiveTests','MineCrossSideVisibilityLiveTests','P591dMinefieldExportAudienceTests','ChaffTests','ParallelBroadcastByteIdentityTests','BroadcastSnapshotTests','BroadcastEncodeOnceTests','BroadcastSnapshotLoopbackTests','NativeShipUpdateModelTests','TickProfilerTests','ArmedProbeLiveTests','ProbeCombatLiveTests','ProbesUpdateLiveTests','P612ServerCinematicTests','LiveViewClusterTests','WarpBombTests'],
'ClientCore':['ClientWorldProjectileTests','ClientWorldProbeTests','P697GhostBattleLiveTests','CrossSideCombatVisibilityLiveTests','P542RemoteProjectilesLiveTests','MissileFireLiveTests','ChaffDropLiveTests','MissileTrackingLiveTests','P660ShowcaseLiveTests','P682NextMatchCombatLiveTests']}

def invoke(suite, filt, label):
 project=f'tests/Allegiance.{suite}.Tests/Allegiance.{suite}.Tests.csproj'
 cmd=['dotnet','test',project,'--no-build','--nologo','-v:q','--logger',f'trx;LogFileName={label}.trx','--results-directory',str(out)]
 if filt:cmd+=['--filter',filt]
 start=time.time()
 with (out/(label+'.log')).open('w') as f:ret=subprocess.run(cmd,cwd=root,stdout=f,stderr=subprocess.STDOUT).returncode
 result={'suite':suite,'filter':filt,'label':label,'exit':ret,'seconds':round(time.time()-start,2)}
 trx=out/(label+'.trx')
 if trx.exists():
  tree=ET.parse(trx);counter=tree.find('.//t:Counters',ns)
  result['counts']=counter.attrib if counter is not None else {}
  result['failures']=[{'name':t.attrib['testName'],'message':t.findtext('.//t:Message',namespaces=ns),'stack':t.findtext('.//t:StackTrace',namespaces=ns)} for t in tree.findall('.//t:UnitTestResult',ns) if t.attrib['outcome']=='Failed']
 ledger.append(result);(out/'results.json').write_text(json.dumps(ledger,indent=2))
 print(label,ret,result.get('counts',{}),flush=True)
 return result

if __name__ == '__main__':
 started_at=len(ledger)
 mode=os.environ.get('P697_MODE','focused')
 if mode=='focused':
  for suite,group in classes.items():
   project=f'tests/Allegiance.{suite}.Tests/Allegiance.{suite}.Tests.csproj'
   filt='|'.join('FullyQualifiedName~.'+c+'.' for c in group)
   listing=subprocess.check_output(['dotnet','test',project,'--no-build','--list-tests','--filter',filt],cwd=root,text=True)
   methods=sorted(set(m.split('(')[0].strip() for m in listing.splitlines() if m.strip().startswith('Allegiance.')))
   (out/f'{suite}-methods.json').write_text(json.dumps(methods,indent=2))
   for i,method in enumerate(methods):
    for run in range(1,4):invoke(suite,'FullyQualifiedName='+method,f'{suite}-{i:03}-{run}')
 elif mode=='full':
  for suite in ['Core','ClientCore','ClientCore.MonoGame','Lobby']:invoke(suite,None,'full-'+suite)
 else:raise ValueError(mode)

if __name__ == "__main__":
 raise SystemExit(any(x["exit"] for x in ledger[started_at:]))
