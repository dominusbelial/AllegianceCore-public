import json,time,subprocess,sys,runpy,hashlib
from pathlib import Path
root=Path.cwd(); assets=root/'docs/reports/P713_assets'; scratch=root/'.verify-p713'
while True:
    try:
        rows=json.loads((assets/'verification.json').read_text())
        if any(r['label']=='suite-1-Allegiance.Lobby.Tests' for r in rows):break
    except json.JSONDecodeError:pass
    time.sleep(2)
print('Sweep1 complete; original leader fixture isolated triplet',flush=True)
sys.argv=['verify.py','library']; module=runpy.run_path(str(scratch/'verify.py')); run=module['run']
method='Allegiance.ClientCore.Tests.LeaderSameSideBotClientLiveTests.Leader_client_sees_same_side_bot_and_gets_command_non_leader_does_not_live'
for n in range(1,4):run(f'fixture-original-{n}','Allegiance.ClientCore.Tests','FullyQualifiedName='+method)
subprocess.run(['python3',str(scratch/'fix-leader-fixture.py')],check=True)
original=json.loads((assets/'final-build-fingerprint.json').read_text())
assert all(hashlib.sha256((root/f).read_bytes()).hexdigest()==v for f,v in original['source_sha256'].items())
start=time.time()
with (assets/'build-post-fixture.log').open('w') as log:
    rc=subprocess.run(['dotnet','build','AllegianceCore.slnx','-m:8','--nologo','-v:minimal'],stdout=log,stderr=subprocess.STDOUT).returncode
fixture='tests/Allegiance.ClientCore.Tests/LeaderSameSideBotClientLiveTests.cs'
proof={'start_unix':start,'completed_unix':time.time(),'exit':rc,'original_certified_sources_unchanged':True,
    'fixture':fixture,'fixture_sha256':hashlib.sha256((root/fixture).read_bytes()).hexdigest()}
(assets/'post-fixture-build-fingerprint.json').write_text(json.dumps(proof,indent=2)+'\n')
if rc:sys.exit(rc)
for n in range(1,4):
    if run(f'fixture-corrected-{n}','Allegiance.ClientCore.Tests','FullyQualifiedName='+method):sys.exit(1)
rows=json.loads((assets/'verification.json').read_text());chosen=[r for r in rows if r['label'].startswith('fixture-corrected-')]
assert len(chosen)==3 and all(r['exit']==0 for r in chosen)
cert=json.loads((assets/'affected-certificate.json').read_text())
assert cert['methods']==114
cert['cohort'].append({'method':method,'project':'Allegiance.ClientCore.Tests','labels':[r['label'] for r in chosen],
    'case_passes':sum(int(r['counters']['passed']) for r in chosen)})
cert['source_sha256'][fixture]=proof['fixture_sha256'];cert['methods']+=1;cert['individual_invocations']+=3;cert['case_passes']+=3
cert['production_unchanged_since_showcase']=True
(assets/'affected-certificate.json').write_text(json.dumps(cert,indent=2)+'\n')
print('Fixture corrected and 3/3 passed; production unchanged; ready for sweep2',flush=True)
