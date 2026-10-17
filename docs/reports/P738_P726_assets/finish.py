#!/usr/bin/env python3
"""Rebuild the corrected Core assertion after its original suite exits."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import time

OUT = Path(__file__).resolve().parent
ROOT = OUT.parents[2]


def ledger():
    try:
        return json.loads((OUT / 'verification.json').read_text())
    except json.JSONDecodeError:
        return []


while not any(r['label'] == 'suite-Core' for r in ledger()):
    time.sleep(10)
print('Original Core suite finished; rebuilding corrected test.', flush=True)
command = ['dotnet', 'build', 'AllegianceCore.slnx', '-c', 'Release', '-m:8',
           '-v:minimal', '-p:NuGetAudit=false']
start = time.time()
with (OUT / 'build-final.log').open('w') as log:
    result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
build = dict(label='build-final', command=command, exit_code=result.returncode,
             seconds=round(time.time() - start, 3))
(OUT / 'build-final.json').write_text(json.dumps(build, indent=2) + '\n')
if result.returncode:
    raise RuntimeError('Final build failed')
print('Final build passed; waiting for the sequential suite/isolation runner.', flush=True)
while 'Completed all gates.' not in (OUT / 'progress.log').read_text():
    time.sleep(10)
spec = importlib.util.spec_from_file_location('p738_verify', OUT / 'verify.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)
verify.records = ledger()
verify.records.append(build)
record = verify.csharp('suite-Core-final', 'Core',
                      'FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests')
for index, failure in enumerate(record.get('failures', [])):
    for repeat in range(1, 4):
        verify.csharp(f'final-core-isolation-{index}-{repeat}', 'Core',
                      'FullyQualifiedName=' + failure['method'])
print('Final Core pass and any required isolation finished.', flush=True)
