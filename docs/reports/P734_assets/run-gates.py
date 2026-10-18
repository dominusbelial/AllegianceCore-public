#!/usr/bin/env python3
"""Continue after the independently launched focus triplets, serializing all gates."""
import json, subprocess, sys, time, signal
from pathlib import Path
from verify import ROOT, OUT, os
expected = len(json.loads((OUT / 'affected-methods.json').read_text())) * 3
while True:
    rows = json.loads((OUT / 'verification.json').read_text())
    focus = [r for r in rows if r['name'].startswith('focus-')]
    if any(r['exit'] != 0 for r in focus):
        raise SystemExit('Focus failure requires investigation; later gates not started')
    if len(focus) == expected: break
    time.sleep(2)
for mode in (sys.argv[1:] or ['parking', 'showcase', 'suites', 'retries']):
    print('Starting ' + mode, flush=True)
    with (OUT / (mode + '-run.log')).open('w') as log:
        if mode == 'showcase':
            command = ['dotnet', str(OUT / 'showcase/bin/Debug/net10.0/Allegiance.ClientCore.Tests.dll'), str(OUT / 'showcase.json')]
        else:
            command = [sys.executable, str(OUT / 'verify.py'), mode]
        child = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
        try:
            code = child.wait()
        except KeyboardInterrupt:
            child.terminate()
            try: child.wait(timeout=10)
            except subprocess.TimeoutExpired: child.kill(); child.wait()
            raise
    print(f'{mode} process exit={code}', flush=True)
print('All planned gates completed', flush=True)
