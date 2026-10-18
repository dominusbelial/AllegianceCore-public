"""Read-only progress for the long serial P738 gate."""
import json,re
from pathlib import Path
out=Path(__file__).resolve().parent
records=json.loads((out/'verification.json').read_text())
for project in ('Core','ClientCore','ClientCore.MonoGame'):
    label='suite-'+project
    row=next((r for r in records if r['label']==label),None)
    if row:print(project+': finished, '+row['counters']['passed']+' passed, '+row['counters']['failed']+' failed')
    elif (out/(label+'.log')).exists():
        count=len(re.findall(r'\[FAIL\]',(out/(label+'.log')).read_text()))
        print(project+': running, '+str(count)+' failures reported')
