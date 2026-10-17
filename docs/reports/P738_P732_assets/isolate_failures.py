#!/usr/bin/env python3
"""After the three suites, rerun each observed failed method 3x alone."""
import importlib.util,json
from pathlib import Path
OUT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('p738_p732_verify',OUT/'verify.py')
verify=importlib.util.module_from_spec(spec);spec.loader.exec_module(verify)
verify.records=json.loads((OUT/'verification.json').read_text())
suites=[r for r in verify.records if r['label'].startswith('suite-')]
assert len(suites)==3,'Wait for all suites before isolation'
methods=sorted({(r['label'].removeprefix('suite-'),f['method']) for r in suites for f in r.get('failures',[])})
for i,(project,method) in enumerate(methods):
    for repeat in range(1,4):
        verify.test(f'post-isolate-{i:02}-{repeat}',project,'FullyQualifiedName='+method)
print('Post-suite exact-method isolation finished.',flush=True)
