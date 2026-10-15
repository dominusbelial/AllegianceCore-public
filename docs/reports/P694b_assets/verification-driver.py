import os,sys,json,time,subprocess,pathlib,xml.etree.ElementTree as E
ROOT=pathlib.Path.cwd(); ART=ROOT/'docs/reports/P694b_assets'; TMP=pathlib.Path('/tmp/p694b/verification');TMP.mkdir(parents=True,exist_ok=True)
env=os.environ.copy();env.update(NUGET_HTTP_CACHE_PATH='/tmp/p694b-nuget-http',ALLEGIANCE_LIVE_P660_SHOWCASE='1',ALLEGIANCE_LIVE_P539_BASELINE='1')
ledger={'environment':{k:env[k] for k in ['NUGET_HTTP_CACHE_PATH','ALLEGIANCE_LIVE_P660_SHOWCASE','ALLEGIANCE_LIVE_P539_BASELINE']},'runs':[]}
core=json.load(open('docs/reports/P694_assets/isolated-methods.json'))
new=['Allegiance.Core.Tests.P694bCollisionWorkTests.'+m for m in ['Dense_and_sparse_candidates_keep_filtered_brute_force_order','Repeated_geometry_keeps_all_identities_with_direct_and_sparse_result_caches','Ordinal_event_sort_matches_general_comparator_for_common_and_mixed_times']]
core=sorted(set(core+new)); clients=json.load(open('docs/reports/P694_assets/isolated-client-methods.json'))
(ART/'isolated-methods.json').write_text(json.dumps(core,indent=2)+'\n')
def save(): (ART/'verification.json').write_text(json.dumps(ledger,indent=2)+'\n')
def run(label,project,config='Release',method=None):
 cmd=['dotnet','test','tests/'+project,'-c',config,'--no-build','--logger','console;verbosity=detailed','--logger','trx;LogFileName='+label+'.trx','--results-directory',str(TMP)]
 if method:cmd+=['--filter','FullyQualifiedName='+method]
 t=time.monotonic()
 with (TMP/(label+'.log')).open('w') as log:p=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,env=env)
 item={'label':label,'project':project,'configuration':config,'filter':method,'command':cmd,'exit':p.returncode,'seconds':round(time.monotonic()-t,3),'load':os.getloadavg(),'log':str(TMP/(label+'.log')),'nonpassing':[],'performance':[]}
 trx=TMP/(label+'.trx')
 if trx.exists():
  root=E.parse(trx).getroot();ns={'t':root.tag.split('}')[0][1:]}
  counters=root.find('t:ResultSummary/t:Counters',ns)
  if counters is not None:item['counters']=dict(counters.attrib)
  definitions={d.get('id'):d.find('t:TestMethod',ns).attrib for d in root.findall('t:TestDefinitions/t:UnitTest',ns)}
  for u in root.findall('t:Results/t:UnitTestResult',ns):
   out=u.find('t:Output/t:StdOut',ns)
   if out is not None and 'P694 count=' in (out.text or ''):item['performance'].append({'name':u.get('testName'),'stdout':out.text})
   if u.get('outcome') not in ['Passed']:
    d=definitions.get(u.get('testId'),{});error=u.find('t:Output/t:ErrorInfo/t:Message',ns);stack=u.find('t:Output/t:ErrorInfo/t:StackTrace',ns)
    item['nonpassing'].append({'name':u.get('testName'),'method':d.get('className','')+'.'+d.get('name',''),'outcome':u.get('outcome'),'message':error.text if error is not None else '', 'stack':stack.text if stack is not None else ''})
 ledger['runs'].append(item);save()
 print(label,'exit',p.returncode,'seconds',item['seconds'],'counts',item.get('counters',{}).get('passed'),item.get('counters',{}).get('failed'),flush=True)
 return item
# Start with the numeric and timing gates before the wider original P694 cohort.
priority=[m for m in core if any(k in m for k in ['P694bCollision','P694CollisionGeometry','P667Parallel','P689CollisionPair','P694MassEvent'])]
rest=[m for m in core if m not in priority]
for index,method in enumerate(priority+rest):
 for attempt in range(1,4):run(f'isolated-core-{index:03}-{attempt}','Allegiance.Core.Tests',method=method)
for index,method in enumerate(clients):
 for attempt in range(1,4):run(f'isolated-client-{index:03}-{attempt}','Allegiance.ClientCore.Tests',method=method)
for attempt in range(1,4):run(f'isolated-debug-perf-{attempt}','Allegiance.Core.Tests','Debug','Allegiance.Core.Tests.P694MassEventTickTests.Launch_point_mass_events')
for project in ['Allegiance.Core.Tests','Allegiance.ClientCore.Tests','Allegiance.ClientCore.MonoGame.Tests']:
 run('suite-'+project,project,'Debug')
# Audit every observed failure with three exact-method fresh invocations once
# all suite activity is over. Record each attempt; never hide the suite result.
failures=sorted({(r['project'],r['configuration'],x['method']) for r in ledger['runs'] for x in r['nonpassing'] if x['outcome']=='Failed'})
for index,(project,config,method) in enumerate(failures):
 for attempt in range(1,4):run(f'failure-audit-{index:03}-{attempt}',project,config,method)
print('DONE',len(ledger['runs']),'runs',flush=True)
