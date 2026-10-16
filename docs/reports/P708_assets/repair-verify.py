#!/usr/bin/env python3
"""Isolate the consumers of the new NPC missile input after its repair release fix."""
import gzip,json,os,subprocess,time,xml.etree.ElementTree as E
from pathlib import Path
out=Path(__file__).resolve().parent;root=out.parents[2];os.chdir(root)
for line in (root/'local.env').read_text().splitlines():
    if line.strip() and not line.lstrip().startswith('#') and '=' in line:
        key,value=line.split('=',1);os.environ[key.strip()]=value.strip().strip('"').strip("'")
methods=['Repair_requires_nan_damage_missing_health_and_native_angle','Repair_mine_decision_checks_pilot_distance_amount_and_load_time','Repair_order_fires_real_projectiles_that_restore_friendly_hull','Repair_station_uses_station_geometry_and_keeps_firing_at_full_station_health']
results=[];ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
for i,method in enumerate(methods):
    for n in range(1,4):
        stem=f'repair-final-{i}-{n}';trx=out/(stem+'.trx');fq='Allegiance.Core.Tests.P676SimRulesTests.'+method
        cmd=['dotnet','test','tests/Allegiance.Core.Tests','--no-build','--nologo','--filter','FullyQualifiedName='+fq,'--logger','trx;LogFileName='+trx.name,'--results-directory',str(out)]
        started=time.time()
        with (out/(stem+'.log')).open('w') as log:r=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT)
        tree=E.parse(trx);row={'name':stem,'method':fq,'exit':r.returncode,'seconds':round(time.time()-started,3),'counters':tree.find('.//t:Counters',ns).attrib,'evidence':trx.name+'.gz'}
        with gzip.open(str(trx)+'.gz','wb') as f:f.write(trx.read_bytes())
        trx.unlink();results.append(row);(out/'repair-verification.json').write_text(json.dumps(results,indent=2)+'\n');print(json.dumps(row),flush=True)
assert all(r['exit']==0 for r in results)
