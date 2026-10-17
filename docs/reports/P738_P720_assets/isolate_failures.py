#!/usr/bin/env python3
"""Run fresh post-suite triplets for every observed failing method/case."""
import importlib.util,json,re
from pathlib import Path
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('p738_verify',HERE/'verify.py')
v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v)
v.records=json.loads((HERE/'verification.json').read_text())
failed=set()
for r in v.records:
 if r['label'].startswith('post-isolate-'):continue
 for f in r.get('failures',[]):
  assembly=Path(r['command'][2]).name
  project=assembly.removeprefix('Allegiance.').removesuffix('.Tests.dll')
  selector='FullyQualifiedName='+f['method']
  # The two long research cases are independent and have an existing case-level
  # isolation precedent in the P738/P732 certificate. Select only the failing case.
  if 'P703ResearchCommanderLiveTests.Both_sides_complete_' in f['method']:
   civ=re.search(r'civA: (\d+),',f['name'])[1]
   selector+='&DisplayName~civA: '+civ+','
  failed.add((project,selector))
for index,(project,selector) in enumerate(sorted(failed)):
 for repeat in range(1,4):
  label=f'post-isolate-{index:03}-{repeat}'
  if any(r['label']==label for r in v.records):continue
  v.test(label,project,selector)
print('Post-suite isolation complete:',len(failed),'methods/cases',flush=True)
