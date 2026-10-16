#!/usr/bin/env python3
"""Reconstruct all preliminary invocation evidence from durable TRX files."""
import hashlib, json, re
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[3]
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=[]
for path in sorted((ROOT/'.verify-p720').glob('*.trx')):
    xml=ET.parse(path)
    counters=xml.find('.//t:Counters',ns).attrib
    times=xml.find('.//t:Times',ns).attrib
    summary=xml.find('.//t:ResultSummary',ns).attrib
    rows=xml.findall('.//t:UnitTestResult',ns)
    records.append(dict(label=path.stem,phase='diagnostic-not-certified',trx=str(path.relative_to(ROOT)),
                        log=str(path.with_suffix('.log').relative_to(ROOT)),times=times,counters=counters,
                        summary=summary,trx_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                        failures=[dict(name=r.attrib['testName'],message=r.findtext('.//t:Message',namespaces=ns),
                                       stack=r.findtext('.//t:StackTrace',namespaces=ns)) for r in rows if r.attrib['outcome']=='Failed']))
records.sort(key=lambda r:r['times']['start'])
(ROOT/'docs/reports/P720_assets/development-verification.json').write_text(json.dumps(records,indent=2)+'\n')
print('Preserved',len(records),'diagnostic invocations,',sum(int(r['counters']['failed']) for r in records),'failed cases')
