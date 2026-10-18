"""Resolve this merge with file edits and read-only object access; no git commands."""
import csv, hashlib, io, json, re, struct, sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
sys.dont_write_bytecode=True
sys.path.insert(0,str(OUT.parent/'P738_P720_assets'))
from object_reader import ObjectReader
meta=ROOT/'.git'
gitdir=Path(meta.read_text().strip().split(': ',1)[1])
common=(gitdir/(gitdir/'commondir').read_text().strip()).resolve()
objects=ObjectReader(common)
b=(gitdir/'index').read_bytes();signature,version,count=struct.unpack_from('!4sII',b);assert signature==b'DIRC' and version==2
pos=12;stages={}
for _ in range(count):
    start=pos;fields=struct.unpack_from('!10I20sH',b,pos);flags=fields[-1];pos+=62
    if flags&0x4000:pos+=2
    end=b.index(b'\0',pos);name=b[pos:end].decode();stage=(flags>>12)&3
    pos=start+((end-start+8)//8)*8
    if stage:stages.setdefault(name,{})[str(stage)]=fields[-2].hex()
(OUT/'index-stages.json').write_text(json.dumps(stages,indent=2)+'\n')
(OUT/'metadata-fingerprints.json').write_text(json.dumps({p:hashlib.sha256((gitdir/p).read_bytes()).hexdigest() for p in ('index','HEAD','MERGE_HEAD','MERGE_MSG')},indent=2)+'\n')
pattern=re.compile(r'^<<<<<<<[^\n]*\n(.*?)^=======\n(.*?)^>>>>>>>[^\n]*(?:\n|$)',re.M|re.S)
conflicts={name:[dict(ours=m[1],theirs=m[2]) for m in pattern.finditer((ROOT/name).read_text())] for name in stages}
(OUT/'conflicts-before.json').write_text(json.dumps(conflicts,indent=2)+'\n')
# Load source snapshots for every conflicted file for review and row auditing.
for name,versions in stages.items():
    for stage,oid in versions.items():
        path=OUT/'before'/stage/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(objects.read(oid))
# P737 must inspect cargo before the P726 inventory transfer consumes/moves it.
name='src/Allegiance.ClientCore/State/ClientWorld.cs'
s=(ROOT/name).read_text()
def reload_resolution(m):
    incoming=m[2]
    incoming=incoming.replace('        if (_ships.TryGetValue(reload.ShipID, out var ent))\n            ent.ReloadCount++;\n        RecordReloadFeedback(reload);',
        '        if (!_ships.TryGetValue(reload.ShipID, out var ent)) return;\n        ent.ReloadCount++;\n        // Classify the source cargo before applying the authoritative transfer.\n        RecordReloadFeedback(reload);\n        ApplyReloadInventory(ent, reload);')
    return incoming
s=pattern.sub(reload_resolution,s)
# Auto-merge duplicated P718 departure dispatch/handlers. Keep the complete
# cleanup/session-shutdown paths and invoke the new cue before side mutation.
s=re.sub(r'            case QuitMission quitMission:.*?            case ShipWarningUpdate warning:', '            case ShipWarningUpdate warning:',s,flags=re.S)
start=s.index('    /// <summary>\n    /// P736 — <c>FM_S_QUIT_MISSION')
end=s.index('    private void ApplySideLeave',start)
# Keep the helper's own documentation; remove only the duplicate Apply overloads.
helper_doc=s.rfind('    /// <summary>',start,end)
s=s[:start]+s[helper_doc:]
(ROOT/name).write_text(s)
name='src/Allegiance.ClientCore/State/ClientWorld.Interface.cs'
s=(ROOT/name).read_text().replace('        short oldSide = ship.SideId;', '        ApplySideLeave(message.ShipId, message.Reason);\n        short oldSide = ship.SideId;')
s=s.replace('Apply(new QuitSide { ShipId = message.ShipId, Reason = (QuitSideReason)8 });','Apply(new QuitSide { ShipId = message.ShipId, Reason = message.Reason, Message = message.Message });')
(ROOT/name).write_text(s)
name='src/Allegiance.ClientCore/Session/ConnectionSession.cs'
s=(ROOT/name).read_text()
s=re.sub(r'                    case QuitMission.Fmid:\n                        // P736.*?(?=                    case ClusterWarningUpdate.Fmid:)', '',s,flags=re.S)
(ROOT/name).write_text(s)
name='src/Allegiance.ClientCore.Game/Audio/SoundManager.cs'
s=(ROOT/name).read_text();s=pattern.sub(lambda m:m[2].replace('_content.Load<SoundEffect>(', 'LoadSound('),s);(ROOT/name).write_text(s)
name='src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb'
s=pattern.sub(lambda m:m[1]+m[2],(ROOT/name).read_text())
parts=re.split(r'(?=^#begin )',s,flags=re.M);seen={};result=[parts[0]];duplicates=[]
for block in parts[1:]:
    asset=block.splitlines()[0][7:]
    if asset in seen:
        assert block.strip()==seen[asset].strip(),asset
        duplicates.append(asset)
    else:seen[asset]=block;result.append(block)
(ROOT/name).write_text(''.join(result))
(OUT/'mgcb-deduplicated.json').write_text(json.dumps(duplicates,indent=2)+'\n')
name='tests/Allegiance.Core.Tests/P652EconomyDroneAiTests.cs'
s=pattern.sub(lambda m:m[1],(ROOT/name).read_text());(ROOT/name).write_text(s)
# Resolve the whole simulation matrix by row, retaining the P732 ID correction.
name='docs/parity/sim-rules-matrix.csv'
versions=[]
for stage in ('1','2','3'):
    reader=csv.DictReader(io.StringIO(objects.read(stages[name][stage]).decode()));header=reader.fieldnames
    versions.append({r['row_id']:r for r in reader})
base,ours,theirs=versions;assert base.keys()==ours.keys()==theirs.keys()
rows=[];audit=[]
rank={'missing':0,'partial':1,'na':2,'n/a':2,'done':3,'implemented':3}
for key,b in base.items():
    o,t=ours[key],theirs[key]
    if o==t:r=dict(o);decision='identical'
    elif o==b:r=dict(t);decision='incoming only'
    elif t==b:r=dict(o);decision='ours only'
    else:
        r=dict(o if rank[o['port_status']]>=rank[t['port_status']] else t)
        for field in header:
            if field not in ('row_id','port_status','weight') and o[field]!=t[field]:
                if o[field]==b[field]:r[field]=t[field]
                elif t[field]==b[field]:r[field]=o[field]
                else:r[field]='; '.join(dict.fromkeys(v for v in (o[field],t[field]) if v))
        decision='both changed: more complete status with both evidences'
        if key in ('SIM-0249','SIM-0250'):
            r['port_cite_or_reason']=o['port_cite_or_reason']
            r['notes']=o['notes']+' Historical incoming evidence: '+t['notes']+' P738: incoming ordinal claims are superseded by the P732 native enum/catalog verification.'
    rows.append(r)
    if o!=t:audit.append(dict(key=key,decision=decision,base=b,ours=o,theirs=t,resolved=r))
with (ROOT/name).open('w',newline='') as f:
    writer=csv.DictWriter(f,header,lineterminator='\n');writer.writeheader();writer.writerows(rows)
(OUT/'parity-row-audit.json').write_text(json.dumps(audit,indent=2)+'\n')
for name in stages:
    if name.startswith('docs/reports/'):
        (ROOT/name).write_bytes(objects.read(stages[name]['3']))
    elif name=='docs/TEST_FLAKES.md':
        (ROOT/name).write_text(pattern.sub(lambda m:m[1]+m[2],(ROOT/name).read_text()))
print(json.dumps(dict(conflicts={n:len(v) for n,v in conflicts.items()},mgcb_duplicates_removed=len(duplicates),matrix_changed_rows=len(audit)),indent=2))
