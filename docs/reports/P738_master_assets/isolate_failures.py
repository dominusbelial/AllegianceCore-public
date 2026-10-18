"""Run three fresh isolated processes for each failed gate method."""
import json,runpy
from pathlib import Path
OUT=Path(__file__).resolve().parent
runner=runpy.run_path(str(OUT/'verify.py'));test=runner['test'];records=runner['records']
assert len([r for r in records if r['label'].startswith('suite-')])==3,'Finish the single scheduled suite passes first.'
failed={}
for index,row in enumerate(records):
    for failure in row.get('failures',[]):
        project=Path(row['command'][2]).name.removeprefix('Allegiance.').removesuffix('.Tests.dll')
        failed[failure['method']]=(index,project)
for index,(method,(last_failure,project)) in enumerate(sorted(failed.items())):
    recovery=[r for i,r in enumerate(records) if i>last_failure and r['label'].startswith(('focused-isolate-','post-isolate-')) and 'FullyQualifiedName='+method in r['command']]
    if len(recovery)==3 and all(r['exit_code']==0 and int(r['counters']['passed'])>0 and not r['skips'] for r in recovery):continue
    print('Isolating '+method,flush=True)
    for repeat in range(1,4):test(f'post-isolate-{index:02}-{repeat}',project,'FullyQualifiedName='+method)
print('Required isolation triplets finished; inspect all recorded outcomes.',flush=True)
