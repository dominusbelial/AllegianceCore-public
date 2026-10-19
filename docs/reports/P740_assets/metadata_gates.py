#!/usr/bin/env python3
"""After the single broad sweep: correct native-unbound metadata certificate."""
import fcntl, hashlib, importlib.util, json, subprocess, sys, time
from pathlib import Path
sys.dont_write_bytecode=True
OUT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('p740_verify',OUT/'verify.py')
v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v)
RAW=Path('/tmp/allegiance-evidence/P740')
with (RAW/'task.lock').open('r+') as lock:
    fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    assert len([r for r in v.records if r['label'].startswith('suite-')])==3
    before=json.loads((OUT/'build-fingerprints.json').read_text())
    (RAW/'build-fingerprints-before-metadata.json').write_text(json.dumps(before,indent=2)+'\n')
    command=['dotnet','build','AllegianceCore.slnx','-c','Release','--nologo']
    env=dict(v.ENV);env['NUGET_HTTP_CACHE_PATH']='/tmp/p740-nuget-cache'
    log=RAW/'build-final-metadata.log'
    with log.open('w') as output:run=subprocess.run(command,cwd=v.ROOT,env=env,stdout=output,stderr=subprocess.STDOUT)
    assert run.returncode==0 and '0 Warning(s)' in log.read_text() and '0 Error(s)' in log.read_text()
    core=json.loads((OUT/'ui-revalidation.json').read_text())['core_dependencies_byte_identical']
    assert all(hashlib.sha256((v.ROOT/name).read_bytes()).hexdigest()==h for name,h in core.items())
    selected=[]
    for filename in ('P642ConsoleZoomTests','P625KeyMapTests','P740FlightInputTests','P720EquipmentSwapTests'):
        selected+=v.methods('ClientCore.MonoGame',filename)
    selected=sorted(set(selected))
    for index,(project,method) in enumerate(selected):
        for repeat in range(1,4):
            result=v.test(f'metadata-{index:03}-{repeat}',project,'FullyQualifiedName='+method)
            assert result['exit_code']==0 and int(result['counters']['passed'])>0,method
    files=[p for directory in ('src','tests') for p in (v.ROOT/directory).rglob('*.cs') if '/bin/' not in str(p) and '/obj/' not in str(p)]
    files += [v.ROOT/'docs/parity'/name for name in ('parity-matrix.csv','sim-rules-matrix.csv','fedmsg-wiring.csv')]
    after={'head':before['head'],'time':time.time(),'sources':{str(p.relative_to(v.ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(files)},'test_binaries':{}}
    for project in ('Core','ClientCore','ClientCore.MonoGame'):
        p=v.ROOT/f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
        after['test_binaries'][str(p.relative_to(v.ROOT))]=hashlib.sha256(p.read_bytes()).hexdigest()
    after['build']={'command':command,'warnings':0,'errors':0,'raw_log':str(log),'sha256':hashlib.sha256(log.read_bytes()).hexdigest()}
    changed=[name for name,h in after['sources'].items() if before['sources'].get(name)!=h]
    assert set(changed)=={'src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs','tests/Allegiance.ClientCore.MonoGame.Tests/P740FlightInputTests.cs'},changed
    (OUT/'build-fingerprints.json').write_text(json.dumps(after,indent=2)+'\n')
    proof={'fixed_method':'Allegiance.ClientCore.MonoGame.Tests.P642ConsoleZoomTests.Every_TK_action_exposes_the_native_MDL_default_including_unbound_commands','reason':'NativeDefault null represents a native unbound command; Keys.None is the separate port Default binding. Fixed throttle33/66 native-suggestion metadata, preserving their unbound runtime defaults. Added a direct assertion.','methods':selected,'changed_sources':changed,'core_dependencies_unchanged':True,'runtime_impact':'Native-suggestion metadata only; runtime controls, world/wire/server/bot code and all broad test assertions unchanged. Existing broad results and showcase window remain applicable. No broad sweep repeated.','superseded_build_fingerprints':str(RAW/'build-fingerprints-before-metadata.json')}
    (OUT/'metadata-revalidation.json').write_text(json.dumps(proof,indent=2)+'\n')
    print(json.dumps({'methods':len(selected),'invocations':len(selected)*3,'build':'0 warnings /0 errors'},indent=2),flush=True)
