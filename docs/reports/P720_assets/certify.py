#!/usr/bin/env python3
"""One locked serial run: affected triplets, parking 10x, two full sweeps."""
import hashlib
import json
import verify

verify.acquire_lock()
if verify.affected(): raise SystemExit('isolated test failures; inspect before continuing')
cohort=json.loads((verify.ASSETS/'affected-methods.json').read_text())
certificate=[]
for project,method in cohort:
    rows=[(i+1,r) for i,r in enumerate(verify.records)
          if r['project']==project and r['filter']=='FullyQualifiedName='+method][-3:]
    assert len(rows)==3 and all(r['exit_code']==0 and int(r.get('counters',{}).get('total',0))>0 for _,r in rows),method
    certificate.append(dict(project=project,method=method,invocations=[i for i,_ in rows],
                            case_passes=sum(int(r['counters']['passed']) for _,r in rows)))
(verify.ASSETS/'affected-certificate.json').write_text(json.dumps(certificate,indent=2)+'\n')
if verify.showcase(): raise SystemExit('parking failure: compare base before continuing')
raise SystemExit(bool(verify.suites()))
