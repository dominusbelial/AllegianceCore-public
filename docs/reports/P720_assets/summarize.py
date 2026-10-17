#!/usr/bin/env python3
import collections, json
from pathlib import Path
import xml.etree.ElementTree as ET
assets=Path(__file__).resolve().parent
root=assets.parents[2]
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=json.loads((assets/'verification.json').read_text())
def skipped(row):
    return [t.attrib['testName'] for t in ET.parse(root/row['trx']).findall('.//t:UnitTestResult',ns)
            if t.attrib['outcome']=='NotExecuted']
summary={}
for label,predicate in [('affected',lambda r:r['label'].startswith(('affected-','finalisolated-'))),
                        ('showcase',lambda r:r['label'].startswith('showcase-')),
                        ('suites',lambda r:r['label'].startswith('suite-')),
                        ('retries',lambda r:r['label'].startswith('retry-'))]:
    rows=[r for r in records if predicate(r)]
    counts=collections.Counter()
    for r in rows:
        for key in ['passed','failed','notExecuted','total']:
            counts[key]+=int(r.get('counters',{}).get(key,0))
        counts['skipped']+=len(skipped(r))
    summary[label]=dict(invocations=len(rows),exit_failures=sum(r['exit_code']!=0 for r in rows),counts=dict(counts))
summary['suites']['by_project']=[dict(label=r['label'],project=r['project'],counters=r.get('counters'),failures=r.get('failures'),skips=skipped(r))
                               for r in records if r['label'].startswith('suite-')]
cert=assets/'affected-certificate.json'
if cert.exists():
    rows=json.loads(cert.read_text())
    summary['certificate']=dict(methods=len(rows),invocations=sum(len(r['invocations']) for r in rows),case_passes=sum(r['case_passes'] for r in rows))
(assets/'validation-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary,indent=2))
