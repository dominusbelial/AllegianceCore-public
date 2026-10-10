#!/usr/bin/env python3
"""Validate treasure-set entries against per-core object ids (strongest check)."""
import struct, os, hashlib, collections

def i16(b,o): return struct.unpack_from("<h",b,o)[0]
def u16(b,o): return struct.unpack_from("<H",b,o)[0]
def u8(b,o): return b[o]
def iter_records(raw, body_start):
    pos=body_start; end=len(raw)
    while pos<end:
        typ,size=struct.unpack_from("<hi",raw,pos)
        if size<0: break
        yield typ, raw[pos+6:pos+6+size]
        pos+=6+size

roots=[os.environ.get("ALLEGIANCE_ARTWORK_PATH",""), os.environ.get("ALLEGIANCE_NATIVE_SRC_PATH","")]
by_hash={}
for r in roots:
    for dp,_,fn in os.walk(r):
        if "/.git/" in dp: continue
        for f in fn:
            if f.endswith(".igc"):
                p=os.path.join(dp,f)
                try: h=hashlib.md5(open(p,"rb").read()).hexdigest()
                except: continue
                if h not in by_hash or p<by_hash[h]: by_hash[h]=p

# per-file part ids and hull ids (for code=1 part refs)
part_ids=collections.defaultdict(set)
hull_ids=collections.defaultdict(set)
powerup_refs=collections.defaultdict(lambda: collections.Counter())

for p in by_hash.values():
    raw=open(p,"rb").read(); dsize=struct.unpack_from("<i",raw,4)[0]
    bs=8 if dsize==len(raw)-8 else 4
    pf=os.path.basename(p)
    for typ,pay in iter_records(raw,bs):
        if typ==30:
            if len(pay)==24: part_ids[pf].add(i16(pay,2))
            else: part_ids[pf].add(i16(pay,372))
        elif typ==29:
            hull_ids[pf].add(i16(pay,446))

# decode treasure sets; entries=[tid u16, code u8, chance u8]
results=collections.defaultdict(list)
bad_part_ref=0; total_part=0
bad_powerup=0; total_powerup=0
cash_bad=0; total_cash=0
chance_high=collections.Counter()
for p in by_hash.values():
    raw=open(p,"rb").read(); dsize=struct.unpack_from("<i",raw,4)[0]
    bs=8 if dsize==len(raw)-8 else 4
    pf=os.path.basename(p)
    for typ,pay in iter_records(raw,bs):
        if typ!=28: continue
        size=len(pay); n=(size-32)//4
        name=pay[:25].split(b"\0")[0].decode("latin1")
        tsid=i16(pay,25); zone=u8(pay,29)
        entries=[]
        for i in range(n):
            e=pay[32+4*i:36+4*i]
            tid=u16(e,0); code=u8(e,2); chance=u8(e,3)
            entries.append((tid,code,chance))
            if code==1:
                total_part+=1
                if tid not in part_ids[pf]: bad_part_ref+=1
                if tid>32000: bad_part_ref+=1
            elif code==2:
                total_powerup+=1
                # PowerupCode bitmap 0x01..0x10
                if tid==0 or tid>=256: bad_powerup+=1
            elif code==4:
                total_cash+=1
                if tid not in (0,): cash_bad+=1
        results[(name,tsid)].append(entries)
        if n!=len(entries): print("len err")

print(f"part refs: {total_part} bad={bad_part_ref}")
print(f"powerup refs: {total_powerup} bad={bad_powerup}")
print(f"cash refs: {total_cash} bad={cash_bad}")
print(f"distinct treasure sets (name,tsid): {len(results)}")
