import json, re, subprocess, os, pathlib, xml.etree.ElementTree as ET, shutil, time
root=pathlib.Path.cwd(); work=pathlib.Path('/tmp/p677-final-validation');work.mkdir(exist_ok=True)
methods=[a['method'] for a in json.load(open('docs/reports/P669_assets/validation.json'))['isolated']]
for path,cls in [('tests/Allegiance.Core.Tests/InterestTierTests.cs','Allegiance.Core.Tests.InterestTierTests'),('tests/Allegiance.Core.Tests/InterestTierCombatTests.cs','Allegiance.Core.Tests.InterestTierCombatTests'),('tests/Allegiance.ClientCore.Tests/ShipDeadReckonerTests.cs','Allegiance.ClientCore.Tests.ShipDeadReckonerTests')]:
 methods += [cls+'.'+m for m in re.findall(r'public void (\w+)\(',pathlib.Path(path).read_text())]
methods += ['Allegiance.Core.Tests.BroadcastSnapshotLoopbackTests.Crowded_TCP_interest_tiers_reduce_bytes_and_report_ages']
results=[];env=os.environ.copy();env['P669_MEASURE']='1'
for i,method in enumerate(methods):
 project='tests/Allegiance.ClientCore.Tests' if '.ClientCore.Tests.' in method else 'tests/Allegiance.Core.Tests'
 runs=[]
 for repeat in range(3):
  tag=f'isolated-{i:03}-{repeat+1}';trx=work/(tag+'.trx')
  with open(work/(tag+'.log'),'w') as log:
   p=subprocess.run(['dotnet','test',project,'--no-build','--filter','FullyQualifiedName='+method,'--logger',f'trx;LogFileName={trx}'],stdout=log,stderr=subprocess.STDOUT,env=env)
  counts={}
  if trx.exists():
   doc=ET.parse(trx); c=doc.find('.//{*}Counters'); counts=c.attrib if c is not None else {}
  runs.append(dict(exitCode=p.returncode,counts=counts))
  if method.endswith('Crowded_TCP_interest_tiers_reduce_bytes_and_report_ages'):
   src=pathlib.Path('/tmp/p677-loopback-256.json')
   if src.exists(): shutil.copy(src,root/f'docs/reports/P677_assets/debug-256-{repeat+1}.json')
  if method.endswith('Six_bot_firing_lanes_keep_kills_and_target_freshness_with_tiers'):
   src=pathlib.Path('/tmp/p677-combat.json')
   if src.exists(): shutil.copy(src,root/f'docs/reports/P677_assets/combat-{repeat+1}.json')
 results.append(dict(method=method,runs=runs))
 (root/'docs/reports/P677_assets/isolated.json').write_text(json.dumps(results,indent=2))
 print(f'{i+1}/{len(methods)} {method}: {[r["exitCode"] for r in runs]}',flush=True)
 if any(r['exitCode'] for r in runs): print('FAILURE: '+str(work/(tag+'.log')),flush=True)
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def run_test(tag,project,filter=None,configuration=None,extra=None):
 trx=work/(tag+'.trx');cmd=['dotnet','test',project,'--no-build','--logger',f'trx;LogFileName={trx}']
 if filter: cmd+=['--filter',filter]
 if configuration: cmd+=['-c',configuration]
 runenv=env.copy(); runenv.update(extra or {})
 with open(work/(tag+'.log'),'w') as log:
  p=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,env=runenv)
 doc=ET.parse(trx); counter=doc.find('.//t:Counters',ns)
 failures=[];skips=[]
 for r in doc.findall('.//t:UnitTestResult',ns):
  item={'name':r.attrib['testName'],'testId':r.attrib['testId']}
  if r.attrib.get('outcome')=='Failed':
   item['message']=r.findtext('t:Output/t:ErrorInfo/t:Message',default='',namespaces=ns);failures.append(item)
  elif r.attrib.get('outcome')=='NotExecuted':skips.append(item)
 record=dict(tag=tag,exitCode=p.returncode,counts=counter.attrib,failures=failures,skips=skips)
 print(tag+': '+json.dumps(record),flush=True)
 return record,doc
suites=[]
for name in ['Core','ClientCore','ClientCore.MonoGame']:
 project='tests/Allegiance.'+name+'.Tests'
 record,doc=run_test('suite-'+name,project)
 suites.append(record)
 (root/'docs/reports/P677_assets/suites.json').write_text(json.dumps(suites,indent=2))
 if record['failures']:
  defs={r.attrib['id']:r.find('t:TestMethod',ns) for r in doc.findall('.//t:UnitTest',ns)}
  for index,f in enumerate(record['failures']):
   d=defs[f['testId']];method=d.attrib['className'].split(',')[0]+'.'+d.attrib['name']
   f['isolatedReruns']=[]
   for repeat in range(3):
    outcome,_=run_test(f'failure-{name}-{index}-{repeat+1}',project,'FullyQualifiedName='+method)
    f['isolatedReruns'].append(outcome)
  (root/'docs/reports/P677_assets/suites.json').write_text(json.dumps(suites,indent=2))
# Explicitly enable the optional local showcase and exercise actual client
# composition and lifecycle with tiers on. The normal full suites stay unfiltered.
enabled=[]
for method in ['Allegiance.ClientCore.Tests.P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms',
               'Allegiance.ClientCore.Tests.FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill']:
 for repeat in range(3):
  record,_=run_test('enabled-'+method.split('.')[-2]+'-'+str(repeat+1),'tests/Allegiance.ClientCore.Tests','FullyQualifiedName='+method,
   extra={'ALLEGIANCE_BROADCAST_TIERS':'1','ALLEGIANCE_LIVE_P660_SHOWCASE':'1'})
  enabled.append(record)
  (root/'docs/reports/P677_assets/enabled.json').write_text(json.dumps(enabled,indent=2))
with open(work/'release-build.log','w') as log:
 b=subprocess.run(['dotnet','build','tests/Allegiance.Core.Tests','-c','Release','-m:8','-p:UseSharedCompilation=false','-v:q'],stdout=log,stderr=subprocess.STDOUT,env=env)
print('release build: '+str(b.returncode),flush=True)
shutil.copy(work/'release-build.log',root/'docs/reports/P677_assets/release-build.txt')
measurements=[]
if b.returncode==0:
 for count in [256,512]:
  for repeat in range(3):
   record,_=run_test(f'release-{count}-{repeat+1}','tests/Allegiance.Core.Tests',
    'FullyQualifiedName=Allegiance.Core.Tests.BroadcastSnapshotLoopbackTests.Crowded_TCP_interest_tiers_reduce_bytes_and_report_ages',
    'Release',{'P668_PEERS':str(count)})
   measurements.append(record)
   src=pathlib.Path(f'/tmp/p677-loopback-{count}.json')
   if src.exists():shutil.copy(src,root/f'docs/reports/P677_assets/release-{count}-{repeat+1}.json')
   for mode in ['False','True']:
    src=pathlib.Path(f'/tmp/p677-loopback-{count}-{mode}.csv')
    if src.exists():shutil.copy(src,root/f'docs/reports/P677_assets/release-{count}-{repeat+1}-{mode}.csv')
   (root/'docs/reports/P677_assets/measurements.json').write_text(json.dumps(measurements,indent=2))
for src in pathlib.Path('/tmp').glob('p677-prediction-*.json'): shutil.copy(src,root/'docs/reports/P677_assets'/src.name)
identities=[json.load(open(p)) for p in pathlib.Path('/tmp').glob('p669-identity-*.json')]
(root/'docs/reports/P677_assets/identity.json').write_text(json.dumps(identities,indent=2))
print('VALIDATION COMPLETE',flush=True)
