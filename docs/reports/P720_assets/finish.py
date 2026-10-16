#!/usr/bin/env python3
"""Complete the final-build certificate, then parking and exactly two sweeps."""
import json
from datetime import datetime
import verify
verify.acquire_lock()

cohort = json.loads((verify.ASSETS/'affected-methods.json').read_text())
new_method = ['ClientCore.MonoGame', 'Allegiance.ClientCore.MonoGame.Tests.P720EquipmentSwapTests.Reload_echo_moves_orphan_launchers_and_applies_partial_and_whole_packs_in_wire_order']
if new_method not in cohort: cohort.append(new_method)
(verify.ASSETS/'affected-methods.json').write_text(json.dumps(cohort,indent=2)+'\n')
build_finished = (verify.ASSETS/'build.log').stat().st_mtime
errors=0
for project, method in cohort:
    matches=[r for r in verify.records if r['project']==project and r['filter']=='FullyQualifiedName='+method
             and datetime.fromisoformat(r['started_utc'].replace('Z','+00:00')).timestamp() >= build_finished]
    if len(matches)<3:
        for i in range(1,4): errors+=bool(verify.run(f'finalisolated-{method.split(".")[-1]}-{i}',project,'FullyQualifiedName='+method))
if errors: raise SystemExit('final-build isolated failures; inspect before parking/suite gates')
certificate=[]
for project, method in cohort:
    selected=[(i+1,r) for i,r in enumerate(verify.records) if r['project']==project and r['filter']=='FullyQualifiedName='+method
              and datetime.fromisoformat(r['started_utc'].replace('Z','+00:00')).timestamp()>=build_finished][-3:]
    assert len(selected)==3 and all(r['exit_code']==0 and int(r.get('counters',{}).get('total',0))>0 for _,r in selected),method
    certificate.append(dict(project=project,method=method,invocations=[i for i,_ in selected],
                            case_passes=sum(int(r['counters']['passed']) for _,r in selected)))
(verify.ASSETS/'affected-certificate.json').write_text(json.dumps(certificate,indent=2)+'\n')
if verify.showcase(): raise SystemExit('parking failure: compare base before continuing')
raise SystemExit(bool(verify.suites()))
