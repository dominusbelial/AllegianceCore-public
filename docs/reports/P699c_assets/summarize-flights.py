#!/usr/bin/env python3
import gzip, json, pathlib, xml.etree.ElementTree as E
P = pathlib.Path(__file__).resolve().parent
ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
rows = json.loads((P/'accepted-focused-results.json').read_text())
loads, flights, realtime, pod_cases = [], [], [], []
for r in rows:
    if not any(c in r['filter'] for c in ['P699bPodLoadTests.Hundred_pods_per_side', 'P699PodHomeLiveTests', 'P699HumanPodHomeLiveTests', 'P540bPodAutopilotCrossSectorLiveTests']): continue
    root = E.fromstring(gzip.decompress((P/r['trx']).read_bytes()))
    for stdout in root.findall('.//t:StdOut',ns):
        for line in stdout.text.splitlines():
            row = {'trace':r['trx'],'case':r['filter'],'summary':line}
            if line.startswith('baseline='): loads.append(row)
            if line.startswith(('MCP route=', 'HUMAN route=')): flights.append(row)
            if line.startswith('[p540b] transits'): realtime.append(row)
            if line.startswith('[pod-trip] {'):
                detail = json.loads(line.removeprefix('[pod-trip] '))
                if 'P699bPodLoadTests.Hundred_pods_per_side' in r['filter']:
                    pod_cases.append(dict(trace=r['trx'], **detail))
(P/'flight-results.json').write_text(json.dumps({'loads':loads,'network':flights,'real_time':realtime},indent=2)+'\n')
# Exact per-side counts and actual model identities from authoritative telemetry.
from collections import Counter
(P/'pod-counts.json').write_text(json.dumps([{'trace':t,'side':s,'outcome':o,'model':m,'pod':h,'count':n}
    for (t,s,o,m,h),n in Counter((r['trace'],r['Side'],r['Outcome'],r['StationModel'],r['PodHull'])for r in pod_cases).items()],indent=2)+'\n')
(P/'load-podtrips.jsonl.gz').write_bytes(gzip.compress((''.join(json.dumps(r, separators=(',', ':'))+'\n' for r in pod_cases)).encode(),mtime=0))
print(f'loads={len(loads)} accelerated/TCP={len(flights)} real-time={len(realtime)} pod-results={len(pod_cases)}')
