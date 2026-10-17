#!/usr/bin/env python3
"""Resolve P720 document conflicts by row, using read-only index/blob access."""
import csv, io, json, re, struct, zlib
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
meta=ROOT/'.git'
gitdir=Path(meta.read_text().strip().split(': ',1)[1]) if meta.is_file() else meta
common=(gitdir/(gitdir/'commondir').read_text().strip()).resolve() if (gitdir/'commondir').exists() else gitdir
b=(gitdir/'index').read_bytes(); signature,version,count=struct.unpack_from('!4sII',b);assert version==2
pos=12; stages={}
for _ in range(count):
    start=pos; fields=struct.unpack_from('!10I20sH',b,pos);flags=fields[-1];pos+=62
    if flags&0x4000:pos+=2
    end=b.index(b'\0',pos);name=b[pos:end].decode();stage=(flags>>12)&3
    pos=start+((end-start+8)//8)*8
    if stage:stages.setdefault(name,{})[stage]=fields[-2].hex()
from object_reader import ObjectReader
objects=ObjectReader(common)
def blob(oid):return objects.read(oid).decode()
pattern=re.compile(r'^<<<<<<<[^\n]*\n(.*?)^=======\n(.*?)^>>>>>>>[^\n]*(?:\n|$)',re.M|re.S)
conflicts={}
for name in stages:
    text=(ROOT/name).read_text()
    conflicts[name]=[{'ours':m[1],'theirs':m[2]} for m in pattern.finditer(text)]
(OUT/'conflicts-before.json').write_text(json.dumps(conflicts,indent=2)+'\n')
name='docs/parity/parity-matrix.csv'
versions=[]
for stage in (1,2,3):
    reader=csv.DictReader(io.StringIO(blob(stages[name][stage])));header=reader.fieldnames
    rows=list(reader); versions.append({(r['area'],r['native_item']):r for r in rows})
base,ours,theirs=versions;assert base.keys()==ours.keys()==theirs.keys()
resolved=[];audit=[]
for key,b in base.items():
    o,t=ours[key],theirs[key]
    if o==t:r=o;decision='identical'
    elif o==b:r=t;decision='incoming only'
    elif t==b:r=o;decision='master only'
    else:
        rank={'missing':0,'partial':1,'done':2,'implemented':2,'na':3,'n/a':3}
        r=dict(o if rank[o['port_status']]>=rank[t['port_status']] else t)
        for field in ('native_cite','port_cite_or_reason'):
            r[field]='; '.join(dict.fromkeys([o[field],t[field]])) if o[field]!=t[field] else o[field]
        decision='both changed: more complete status and union evidence'
    resolved.append(r)
    if o!=t:audit.append({'key':key,'decision':decision,'base':b,'ours':o,'theirs':t,'resolved':r})
with (ROOT/name).open('w',newline='') as f:
    writer=csv.DictWriter(f,header,lineterminator='\n');writer.writeheader();writer.writerows(resolved)
name='docs/TEST_FLAKES.md'
text=(ROOT/name).read_text();text=pattern.sub(lambda m:m[1]+m[2],text)
assert all(part in text for block in conflicts[name] for part in block.values())
(ROOT/name).write_text(text)
(OUT/'parity-row-audit.json').write_text(json.dumps(audit,indent=2)+'\n')
print(json.dumps({'conflicts':{p:len(v) for p,v in conflicts.items()},'row_decisions':{d:sum(a['decision']==d for a in audit) for d in {a['decision'] for a in audit}},'incoming_completed_commands':[r['native_item'] for r in resolved if r['area']=='commands' and ours[(r['area'],r['native_item'])]['port_status']!=r['port_status']]},indent=2))
