#!/usr/bin/env python3
"""Final certification, after all diagnostic controllers and triplets finish."""
import json, os, shutil, subprocess, sys, time
from pathlib import Path
OUT=Path(__file__).resolve().parent
ROOT=OUT.parents[2]
os.chdir(ROOT)
os.environ['P734_LEDGER']='verification-final.json'
for line in (ROOT/'local.env').read_text().splitlines():
    if line.strip() and not line.lstrip().startswith('#') and '=' in line:
        k,v=line.split('=',1); os.environ[k.strip()]=v.strip().strip('"').strip("'")
os.environ['NUGET_HTTP_CACHE_PATH']='/tmp/p734-nuget-http'
os.environ['ALLEGIANCE_LIVE_P542_PROOF']='1'
os.environ['ALLEGIANCE_LIVE_P660_SHOWCASE']='1'
expected=len(json.loads((OUT/'affected-methods-final.json').read_text()))*3
print('Waiting for final isolated cohort and diagnostic cleanup',flush=True)
while True:
    rows=json.loads((OUT/'verification-final.json').read_text())
    cohort=[r for r in rows if r['name'].startswith('focus-final-')]
    if any(r['exit']!=0 for r in cohort): raise SystemExit('Post-fix isolated failure')
    logs=['gates-attempt-1.log','gates-attempt-2.log','gates.log']
    stopped=all('All planned gates completed' in (OUT/p).read_text() for p in logs)
    if len(cohort)==expected and stopped: break
    time.sleep(2)
print('Diagnostic controllers stopped; final isolation complete',flush=True)
# No diagnostic controller can start another project after this point.
(OUT/'BLOCK_SUITE_LAUNCH').unlink()
(OUT/'verification.json').rmdir()
initial=json.loads((OUT/'verification-before-diagnostic-cores.json').read_text())
for row in initial:
    if row['name'].startswith('parking-'):
        row['name']=row['name'].replace('parking-','parking-initial-',1)
        row['evidence']=row['evidence'].replace('parking-','parking-initial-',1)
(OUT/'verification-initial.json').write_text(json.dumps(initial,indent=2)+'\n')
(OUT/'verification-before-diagnostic-cores.json').unlink()
for p in list(OUT.glob('parking-*.log'))+list(OUT.glob('parking-*.trx.gz')):
    if p.name.startswith('parking-initial-'): continue
    p.rename(OUT/p.name.replace('parking-','parking-initial-',1))
for old,new in [('suite-1-Core.log','diagnostic-Core-overlapping.log'),('suite-1-Core.trx.gz','diagnostic-Core-last.trx.gz'),('showcase-run.log','showcase-diagnostic-scale3.log'),('gates.log','gates-attempt-3.log')]:
    p=OUT/old
    if p.exists(): p.rename(OUT/new)
(OUT/'showcase.json').rmdir()
for mode in ['parking','showcase','suites','retries']:
    print('Starting final '+mode,flush=True)
    command=(["dotnet",str(OUT/'showcase/bin/Debug/net10.0/Allegiance.ClientCore.Tests.dll'),str(OUT/'showcase.json')]
        if mode=='showcase' else [sys.executable,str(OUT/'verify.py'),mode])
    with (OUT/('final-'+mode+'-run.log')).open('w') as log:
        child=subprocess.Popen(command,cwd=ROOT,stdout=log,stderr=subprocess.STDOUT)
        try: code=child.wait()
        except KeyboardInterrupt:
            child.terminate()
            try: child.wait(timeout=10)
            except subprocess.TimeoutExpired: child.kill(); child.wait()
            raise
    print(f'Final {mode} process exit={code}',flush=True)
print('Final certification commands completed',flush=True)
