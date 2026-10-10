#!/usr/bin/env python3
"""Independently verify part-type layout across the whole corpus (both roots, dedup by content)."""
import struct, os, hashlib, collections

def f32(b,o): return struct.unpack_from("<f",b,o)[0]
def i16(b,o): return struct.unpack_from("<h",b,o)[0]
def u8(b,o): return b[o]
def fix(b,o,n):
    return b[o:o+n].split(b"\0")[0].decode("latin1")

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
            if not f.endswith(".igc"): continue
            p=os.path.join(dp,f)
            try: h=hashlib.md5(open(p,"rb").read()).hexdigest()
            except: continue
            if h not in by_hash or p<by_hash[h]: by_hash[h]=p

EXPECT={1:424,4:412,5:416,7:416,6:400}  # equipmentType->size
equip_count=collections.Counter()
prob_proj=collections.Counter()  # weapon projTypeId sign
problem=collections.Counter()

# known projectile ids and part ids and expendable ids for cross-ref, per file
per_file_ids=collections.defaultdict(lambda: {"proj":set(),"part":set(),"exp":set()})

# pass 1: collect id space per file
for p in by_hash.values():
    raw=open(p,"rb").read(); dsize=struct.unpack_from("<i",raw,4)[0]
    bs=8 if dsize==len(raw)-8 else 4
    pf=os.path.basename(p)
    recs=list(iter_records(raw,bs))
    for typ,pay in recs:
        if typ==22: per_file_ids[pf]["proj"].add(i16(pay,28))  # projectileTypeId
        if typ==30:
            if len(pay)==24:
                per_file_ids[pf]["exp"].add(i16(pay,8))
                per_file_ids[pf]["part"].add(i16(pay,2))
            else:
                per_file_ids[pf]["part"].add(i16(pay,372))
                per_file_ids[pf]["part"].add(i16(pay,374))  # successor (may be -1)
                e=i16(pay,376)
                if e==1: per_file_ids[pf]["proj"].add(i16(pay,414))
        if typ in (23,24,25,26): per_file_ids[pf]["exp"].add(i16(pay,446))

bad_eq=0; total_part=0; total_launch=0
nan_floats=0; bad_str=0; bad_xref_proj=0; bad_xref_exp=0; bad_xref_succ=0
bad_amount=0; bad_packtype=0; bad_defense=0
for p in by_hash.values():
    raw=open(p,"rb").read(); dsize=struct.unpack_from("<i",raw,4)[0]
    bs=8 if dsize==len(raw)-8 else 4
    pf=os.path.basename(p)
    for typ,pay in iter_records(raw,bs):
        if typ!=30: continue
        if len(pay)==24:
            total_launch+=1
            # launcher cross-ref: expendabletypeID in per-file exp set
            if i16(pay,8) not in per_file_ids[pf]["exp"]: bad_xref_exp+=1
            if i16(pay,8)<=0 or i16(pay,8)>40000: bad_xref_exp+=1
            amt=i16(pay,0)
            if amt<0: bad_amount+=1
            if not fix(pay,10,13).isascii(): bad_str+=1
            continue
        total_part+=1
        e=i16(pay,376)
        equip_count[e]+=1
        if EXPECT.get(e)!=len(pay):
            bad_eq+=1
            if bad_eq<10: print("  MISMATCH",pf,e,len(pay))
        # floats finite
        # floats finite, per-subtype actual float offsets only
        float_off = {424:[396,400,404,408],412:[396,400],416:[396,400,404,408],400:[],}[len(pay)]
        for o in float_off:
            v=f32(pay,o)
            if v!=v or abs(v)>1e9:
                nan_floats+=1
                if nan_floats<=10:
                    print("  NONFINITE",pf,"size",len(pay),"eq",e,"offset",o,"val",v)
        # common base floats mass/signature
        for o in (364,368):
            v=f32(pay,o)
            if v!=v or abs(v)>1e9: nan_floats+=1
        # defense type (shield): byte valid 0..40
        if e==4:
            d=u8(pay,404)
            if d>40: bad_defense+=1
        # pack amount
        if e==6:
            if i16(pay,398)<0: bad_amount+=1
            if u8(pay,396) not in (0,1): bad_packtype+=1
        # strings
        for o,n in ((8,14),(22,13),(35,25),(60,201),(380,13)):
            s=fix(pay,o,n)
            if not s.isascii() or any(ord(c)<32 for c in s): bad_str+=1
        # xrefs
        if e==1:  # weapon projTypeId
            pid=i16(pay,414)
            if pid>=0 and pid not in per_file_ids[pf]["proj"]: bad_xref_proj+=1
        succ=i16(pay,374)
        if succ>=0 and succ not in per_file_ids[pf]["part"]: bad_xref_succ+=1

print("part records:",total_part,"launcher records:",total_launch)
print("equipmentType histogram:",dict(sorted(equip_count.items())))
print("bad_eq(size mismatch):",bad_eq," bad_defense:",bad_defense," bad_packtype:",bad_packtype)
print("NaN/floats:",nan_floats," bad_str:",bad_str," bad_amount:",bad_amount)
print("bad_xref_proj(weapon):",bad_xref_proj," bad_xref_exp(launcher):",bad_xref_exp," bad_xref_succ(part successor):",bad_xref_succ)
