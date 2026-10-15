"""P679: method-isolated repeats, real assemblies, and Release TCP measurements.
Run from the repository root after a solution build. No external rig lane.
"""
import json, os, pathlib, shutil, subprocess, xml.etree.ElementTree as ET
root=pathlib.Path.cwd(); assets=pathlib.Path(os.environ.get('P679_ASSETS',root/'docs/reports/P679_assets'))
work=pathlib.Path(os.environ.get('P679_WORK','/tmp/p679-validation')); work.mkdir(exist_ok=True)
env=os.environ.copy(); env['P669_MEASURE']='1'; env['NUGET_HTTP_CACHE_PATH']='/tmp/p679-nuget-http'
# Isolated output trees do not inherit the usual ancestor-based local.env lookup.
for line in (root/'local.env').read_text().splitlines():
    if line.startswith('ALLEGIANCE_') and '=' in line:
        key,value=line.split('=',1);env.setdefault(key,value)

def save(name,value): (assets/name).write_text(json.dumps(value,indent=2))
def run(tag,project,method=None,release=False,extra=None,cwd=None):
    if (work/'STOP').exists(): raise SystemExit('Validation stopped at a test boundary')
    trx=work/(tag+'.trx'); log=work/(tag+'.log')
    cmd=['dotnet','test',project,'--no-build','--logger',f'trx;LogFileName={trx}']
    if method: cmd+=['--filter','FullyQualifiedName='+method]
    if release: cmd+=['-c','Release']
    if env.get('P679_BUILD_ARTIFACTS'): cmd+=['--artifacts-path',env['P679_BUILD_ARTIFACTS']]
    e=env.copy();e.update(extra or {})
    with log.open('w') as f: p=subprocess.run(cmd,cwd=cwd,env=e,stdout=f,stderr=subprocess.STDOUT)
    record={'tag':tag,'exitCode':p.returncode,'log':str(log),'counts':{},'failures':[],'skips':[]}
    if trx.exists():
        doc=ET.parse(trx);record['counts']=doc.find('.//{*}Counters').attrib
        definitions={r.attrib['id']:r.find('{*}TestMethod') for r in doc.findall('.//{*}UnitTest')}
        for r in doc.findall('.//{*}UnitTestResult'):
            if r.attrib.get('outcome') not in ['Failed','NotExecuted']: continue
            d=definitions[r.attrib['testId']]
            item={'name':r.attrib['testName'],'method':d.attrib['className'].split(',')[0]+'.'+d.attrib['name']}
            if r.attrib.get('outcome')=='Failed':
                item['message']=r.findtext('{*}Output/{*}ErrorInfo/{*}Message',default='')
                record['failures'].append(item)
            else: record['skips'].append(item)
    print(tag+': '+str(p.returncode)+' '+str(record['counts']),flush=True)
    return record

def artifacts(prefix,count,dest):
    src=pathlib.Path(f'/tmp/{prefix}-loopback-{count}.json')
    if src.exists(): shutil.copy(src,assets/(dest+'.json'))
    for mode in range(3):
        src=pathlib.Path(f'/tmp/{prefix}-loopback-{count}-{mode}.csv')
        if src.exists():shutil.copy(src,assets/f'{dest}-{mode}.csv')

benchmark='Allegiance.Core.Tests.BroadcastSnapshotLoopbackTests.Crowded_TCP_pipeline_cost_three_configurations'
measurements=[r for r in json.loads((assets/'measurements.json').read_text()) if r['tag'].startswith('before-')] if (assets/'measurements.json').exists() else []
# Identical size-only observer and replay driver on the unmodified HEAD source.
# Optional baseline checkout is created with git archive, never a commit/worktree.
if os.environ.get('P679_INCLUDE_BEFORE') == '1' and pathlib.Path('/tmp/p679-baseline/tests/Allegiance.Core.Tests/bin/Release/net10.0/Allegiance.Core.Tests.dll').exists():
    for count in [256,512]:
        r=run('before-'+str(count),'tests/Allegiance.Core.Tests',benchmark,True,{'P668_PEERS':str(count)},'/tmp/p679-baseline')
        measurements.append(r);artifacts('p679',count,'before-'+str(count))
        save('measurements.json',measurements)
for count in [256,512]:
    for repeat in range(3):
        r=run(f'after-{count}-{repeat+1}','tests/Allegiance.Core.Tests',benchmark,True,{'P668_PEERS':str(count)})
        measurements.append(r);artifacts('p679',count,f'after-{count}-{repeat+1}')
        save('measurements.json',measurements)

isolated=[]
for index,method in enumerate(json.loads((assets/'methods.json').read_text())):
    project='tests/Allegiance.ClientCore.Tests' if '.ClientCore.Tests.' in method else 'tests/Allegiance.Core.Tests'
    runs=[run(f'isolated-{index:03}-{repeat+1}',project,method) for repeat in range(3)]
    item={'method':method,'runs':runs}
    if any(r['exitCode'] for r in runs):
        item['audit']=[run(f'audit-{index:03}-{repeat+1}',project,method) for repeat in range(3)]
    isolated.append(item);save('isolated.json',isolated)
    if method.endswith('Steady_512_peer_pipeline_allocation_budget'):
        shutil.copy('/tmp/p679-allocation.json',assets/'allocation.json')
    if method.endswith('Six_bot_firing_lanes_keep_kills_and_target_freshness_with_tiers'):
        shutil.copy('/tmp/p677-combat.json',assets/'combat.json')

suites=[]
for name in ['Core','ClientCore','ClientCore.MonoGame']:
    project='tests/Allegiance.'+name+'.Tests'
    record=run('suite-'+name,project)
    for index,failure in enumerate(record['failures']):
        failure['audit']=[run(f'suite-audit-{name}-{index}-{repeat+1}',project,failure['method']) for repeat in range(3)]
    suites.append(record);save('suites.json',suites)
enabled=[]
for method in ['Allegiance.ClientCore.Tests.P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms',
               'Allegiance.ClientCore.Tests.FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill']:
    for repeat in range(3):
        enabled.append(run('enabled-'+method.split('.')[-2]+'-'+str(repeat+1),'tests/Allegiance.ClientCore.Tests',method,
            extra={'ALLEGIANCE_BROADCAST_TIERS':'1','ALLEGIANCE_LIVE_P660_SHOWCASE':'1'}))
        save('enabled.json',enabled)
for src in pathlib.Path('/tmp').glob('p669-identity-*.json'):shutil.copy(src,assets/src.name)
for name in ['p679-debug-build.log','p679-release-build.log']:
    shutil.copy('/tmp/'+name,assets/(name.removesuffix('.log')+'.txt'))
print('VALIDATION COMPLETE',flush=True)
