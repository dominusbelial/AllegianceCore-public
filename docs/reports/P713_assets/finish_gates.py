import json,subprocess,time,os,sys,hashlib
from pathlib import Path
root=Path.cwd(); scratch=root/'.verify-p713'; assets=root/'docs/reports/P713_assets'
while True:
    try:
        rows=json.loads((assets/'verification.json').read_text())
        if any(r['label']=='showcase-10' for r in rows):break
    except json.JSONDecodeError:pass
    time.sleep(2)
print('Initial showcase driver finished; certifying final revision serially',flush=True)
env=os.environ.copy();env['P713_AFFECTED_PREFIX']='accepted-affected'
with (scratch/'accepted-affected.log').open('w') as log:
    rc=subprocess.run(['python3',str(scratch/'verify.py'),'affected'],env=env,stdout=log,stderr=subprocess.STDOUT).returncode
if rc:sys.exit(rc)
rows=json.loads((assets/'verification.json').read_text()); build=json.loads((assets/'final-build-fingerprint.json').read_text())
methods=json.loads((scratch/'affected-methods.json').read_text()); cohort=[]
for i,(p,m) in enumerate(methods):
    selected=[r for r in rows if r.get('cohort_key',r['label']).startswith(f'accepted-affected-{i:03}-') and r['exit']==0]
    assert len(selected)==3 and all(r['started_unix']>build['completed_unix'] for r in selected)
    cohort.append({'method':m,'project':p,'labels':[r['label'] for r in selected], 'case_passes':sum(int(r['counters']['passed']) for r in selected)})
assert all(hashlib.sha256((root/f).read_bytes()).hexdigest()==v for f,v in build['source_sha256'].items())
certificate={'methods':len(cohort),'individual_invocations':len(cohort)*3,'case_passes':sum(r['case_passes'] for r in cohort),'source_sha256':build['source_sha256'],'cohort':cohort}
(assets/'affected-certificate.json').write_text(json.dumps(certificate,indent=2)+'\n')
print('Final affected certificate:',certificate['methods'],certificate['individual_invocations'],certificate['case_passes'],flush=True)
# Count only complete showcase invocations started after the final build.
selected=[r for r in rows if r['label'].startswith('showcase-') and r['started_unix']>build['completed_unix']]
import runpy
# Load run() without starting a mode's test loop.
sys.argv=['verify.py','library']; module=runpy.run_path(str(scratch/'verify.py'));run=module['run']
for n in range(len(selected)+1,11):
    run(f'accepted-showcase-{n:02}','Allegiance.ClientCore.Tests','FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
rows=json.loads((assets/'verification.json').read_text())
selected=[r for r in rows if (r['label'].startswith('showcase-') or r['label'].startswith('accepted-showcase-')) and r['started_unix']>build['completed_unix']]
assert len(selected)==10
(assets/'showcase-certificate.json').write_text(json.dumps({'final_build_completed_unix':build['completed_unix'],'labels':[r['label'] for r in selected],'passed':sum(r['exit']==0 for r in selected),'failed':sum(r['exit']!=0 for r in selected)},indent=2)+'\n')
print('Final showcase:',sum(r['exit']==0 for r in selected),'/ 10',flush=True)
