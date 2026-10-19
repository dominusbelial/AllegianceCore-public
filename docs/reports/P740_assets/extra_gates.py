#!/usr/bin/env python3
"""After the single broad sweep: showcase isolation and real HEAD comparison."""
import fcntl, hashlib, importlib.util, json, subprocess, sys, tarfile
sys.dont_write_bytecode=True
from pathlib import Path
OUT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('p740_verify',OUT/'verify.py')
v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v)
raw=Path('/tmp/allegiance-evidence/P740')
method='Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds'
with (raw/'task.lock').open('r+') as lock:
    fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    assert len([r for r in v.records if r['label'].startswith('suite-')])==3
    existing=[r for r in v.records if r['label'].startswith('isolation-') and r['command'][-1]=='FullyQualifiedName='+method]
    if not existing:
        for repeat in range(1,4):v.test(f'parking-retry-{repeat}','ClientCore','FullyQualifiedName='+method)
    base=raw/'base-head';base.mkdir(exist_ok=True)
    archive=raw/'base-source.tar'
    if not (base/'AllegianceCore.slnx').exists():
        with archive.open('wb') as target:
            subprocess.run(['git','archive','HEAD','src','tests','tools','assets','data','scripts','Directory.Build.props','AllegianceCore.slnx','README.md','LICENSE','NOTICE','CREDITS.md'],cwd=v.ROOT,stdout=target,check=True)
        with tarfile.open(archive) as bundle:bundle.extractall(base,filter='data')
    command=['dotnet','build','tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj','-c','Release','--nologo']
    env=dict(v.ENV);env['NUGET_HTTP_CACHE_PATH']=str(raw/'baseline-nuget-cache')
    with (raw/'base-build.log').open('w') as log:build=subprocess.run(command,cwd=base,env=env,stdout=log,stderr=subprocess.STDOUT)
    assert build.returncode==0,'Baseline build failed: '+str(raw/'base-build.log')
    v.ROOT=base;v.RAW=raw/'baseline';v.RAW.mkdir(exist_ok=True)
    for repeat in range(1,6):v.test(f'baseline-parking-{repeat}','ClientCore','FullyQualifiedName='+method)
    current=[r for r in v.records if r['label'].startswith('showcase-')]
    retry=[r for r in v.records if r['label'].startswith('parking-retry-')] or existing
    baseline=[r for r in v.records if r['label'].startswith('baseline-parking-')]
    result={'method':method,'head':json.loads((OUT/'build-fingerprints.json').read_text())['head'],
        'current':{'runs':len(current),'failed':sum(r['exit_code']!=0 for r in current)},
        'fresh_isolation':{'runs':len(retry),'failed':sum(r['exit_code']!=0 for r in retry)},
        'base_head':{'runs':len(baseline),'failed':sum(r['exit_code']!=0 for r in baseline)},
        'baseline_source':str(base),'source_archive_sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'test_binary_sha256':hashlib.sha256((base/'tests/Allegiance.ClientCore.Tests/bin/Release/net10.0/Allegiance.ClientCore.Tests.dll').read_bytes()).hexdigest(),'baseline_build_command':command,'baseline_build_log':str(raw/'base-build.log'),
        'interpretation':'Small-sample observed rates, not a causal claim. Classify the current cleanup failure under the brief only if all three fresh isolated invocations pass. No assertions or deadlines changed.'}
    (OUT/'baseline-comparison.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2),flush=True)
