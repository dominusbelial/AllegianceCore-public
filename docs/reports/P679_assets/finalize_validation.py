"""Recheck the tier-boundary fix, then run all assemblies on the final build.
The earlier three-run ledger is retained for methods whose code did not change.
"""
import os,pathlib,json
path=pathlib.Path(__file__).with_name('validate.py')
exec(compile(path.read_text().split('\nbenchmark=')[0],str(path),'exec'))
old=json.loads((assets/'isolated.json').read_text()); by_method={r['method']:r for r in old}
assert len(old)==len(json.loads((assets/'methods.json').read_text()))
assert all(len(r['runs'])==3 and all(q['exitCode']==0 for q in r['runs']) for r in old)
affected=[m for m in by_method if '.InterestTierTests.' in m or '.InterestTierCombatTests.' in m
          or '.PipelineCostTests.' in m or '.BroadcastSnapshotLoopbackTests.Crowded_TCP_' in m]
for i,method in enumerate(affected):
    runs=[run(f'boundary-{i:02}-{repeat+1}','tests/Allegiance.Core.Tests',method) for repeat in range(3)]
    item={'method':method,'runs':runs}
    if any(r['exitCode'] for r in runs):item['audit']=[run(f'boundary-audit-{i:02}-{repeat+1}','tests/Allegiance.Core.Tests',method) for repeat in range(3)]
    by_method[method]=item
    save('isolated.json',[by_method[m] for m in sorted(by_method)])
    if method.endswith('Steady_512_peer_pipeline_allocation_budget'):shutil.copy('/tmp/p679-allocation.json',assets/'allocation.json')
# Store the narrow final-build rerun set explicitly rather than obscuring it.
save('final-build-reruns.json',affected)
benchmark='Allegiance.Core.Tests.BroadcastSnapshotLoopbackTests.Crowded_TCP_pipeline_cost_three_configurations'
measurements=[r for r in json.loads((assets/'measurements.json').read_text()) if r['tag'].startswith('before-')]
for count in [256,512]:
    for repeat in range(3):
        r=run(f'final-after-{count}-{repeat+1}','tests/Allegiance.Core.Tests',benchmark,True,{'P668_PEERS':str(count)})
        measurements.append(r);artifacts('p679',count,f'after-{count}-{repeat+1}');save('measurements.json',measurements)
suites=[]
for name in ['Core','ClientCore','ClientCore.MonoGame']:
    project='tests/Allegiance.'+name+'.Tests'; record=run('final-suite-'+name,project)
    for i,failure in enumerate(record['failures']):
        failure['audit']=[run(f'final-suite-audit-{name}-{i}-{repeat+1}',project,failure['method']) for repeat in range(3)]
    suites.append(record);save('suites.json',suites)
enabled=[]
for method in ['Allegiance.ClientCore.Tests.P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms',
               'Allegiance.ClientCore.Tests.FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill']:
    for repeat in range(3):
        enabled.append(run('final-enabled-'+method.split('.')[-2]+'-'+str(repeat+1),'tests/Allegiance.ClientCore.Tests',method,
            extra={'ALLEGIANCE_BROADCAST_TIERS':'1','ALLEGIANCE_LIVE_P660_SHOWCASE':'1'}));save('enabled.json',enabled)
for src in pathlib.Path('/tmp').glob('p669-identity-*.json'):shutil.copy(src,assets/src.name)
for name in ['p679-debug-build.log','p679-release-build.log']:shutil.copy('/tmp/'+name,assets/(name.removesuffix('.log')+'.txt'))
print('FINAL VALIDATION COMPLETE',flush=True)
