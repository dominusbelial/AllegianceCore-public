#!/usr/bin/env python3
"""One serial controller. Never starts a second sweep or overlaps local gates."""
import json, os, subprocess, sys, time
from pathlib import Path
OUT=Path(__file__).resolve().parent
ROOT=OUT.parents[2]
os.chdir(ROOT)
while True:
    rows=json.loads((OUT/'verification.json').read_text())
    cohort=json.loads((OUT/'cohort.json').read_text())
    focus=[r for r in rows if r['name'].startswith('focus-')]
    if len(focus)==3*len(cohort):break
    time.sleep(2)
if any(r['exit'] or r.get('skipped') or int(r['counters']['failed']) for r in focus):
    raise SystemExit('Focused failures need review before further gates')
print('Focus complete; starting five parking invocations',flush=True)
for mode in ['parking','showcase','suites','retries']:
    if mode=='showcase':
        # P733 is absent from the P734 base; use the authorized unlocked fallback.
        env=os.environ.copy()
        for line in (ROOT/'local.env').read_text().splitlines():
            if line.strip() and not line.lstrip().startswith('#') and '=' in line:
                k,v=line.split('=',1);env[k.strip()]=v.strip().strip('"').strip("'")
        command=['dotnet',str(OUT/'showcase/bin/Debug/net10.0/Allegiance.ClientCore.Tests.dll'),str(OUT/'showcase.json')]
    else:env=os.environ.copy();command=[sys.executable,str(OUT/'verify.py'),mode]
    print('Starting '+mode,flush=True)
    with (OUT/(mode+'-run.log')).open('w') as log:
        result=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,env=env)
    print(f'{mode} exit={result.returncode}',flush=True)
    if result.returncode:raise SystemExit(result.returncode)
    if mode=='parking':
        rows=json.loads((OUT/'verification.json').read_text())
        if any(r['exit'] for r in rows if r['name'].startswith('parking-')):
            raise SystemExit('Parking failure: base-rate comparison required before suites')
print('All light-gate commands completed',flush=True)
