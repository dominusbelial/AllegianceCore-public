#!/usr/bin/env python3
"""Final layout confirmation for the four map-file objects.

Anchors:
- station:  id fields @52..59, name@62-86, pad@87           (total 88)
- cluster:  name@34-58, poster@59-71, planet@72-84,          (total 92)
            pcts@85-87, activeF@88 (=0 forced), home@89, pad@90-91
- warp:     warpDef@0-31, name@32-56, pad@57-59,             (total 108)
            pos@60, fwd@72, rot@84, sig@100, clusterID@104, pad@106-107
- asteroid: clusterID@116, name@118-142, fraction@143,        (total 144)
            bytes113-115 uninterpreted (see note)

Cross-references are the decisive check.
"""
import struct, os, collections

def f32(b,o):return struct.unpack_from("<f",b,o)[0]
def i16(b,o):return struct.unpack_from("<h",b,o)[0]
def u32(b,o):return struct.unpack_from("<I",b,o)[0]
def s(b,o,n):return b[o:o+n].split(b"\0")[0].decode("latin1")
def vec(b,o):return [f32(b,o+4*i) for i in range(3)]

ROOTS=["/home/domingo/Development/Artwork","/home/domingo/Development/Allegiance"]
files=[];seen=set()
for root in ROOTS:
  if not os.path.isdir(root):continue
  for dp,_,fns in os.walk(root):
    if "/.git/" in dp:continue
    for fn in fns:
      if not fn.endswith(".igc"):continue
      raw=open(os.path.join(dp,fn),"rb").read()
      if hash(raw) in seen:continue
      seen.add(hash(raw));files.append(raw)

recs=collections.defaultdict(list)
for fi,raw in enumerate(files):
  pos=4;v,d=struct.unpack_from("<ii",raw,0)
  if d==len(raw)-8:pos=8
  end=len(raw)
  while pos<end:
    t,siz=struct.unpack_from("<hi",raw,pos);pay=raw[pos+6:pos+6+siz];pos+=6+siz
    recs[t].append((fi,pay))

maxc=1e7
def fin(v):return -maxc<v<maxc
def ctrlstr(p,o,n):return any(ord(c)<32 and c not in "\0" for c in s(p,o,n))

clsids=collections.defaultdict(set)
for fi,pay in recs.get(13,[]): clsids[fi].add(i16(pay,28))
stypes=collections.Counter()  # all OT31 stationTypeIDs in corpus (from static cores)
for fi,pay in recs.get(31,[]): stypes[i16(pay,446)]+=1
known_st = set(stypes.keys())

probs=collections.Counter()
def P(k):probs[k]+=1

# ---------------- STATION ----------------
sn=0
for fi,pay in recs.get(1,[]):
  sn+=1
  for off in (0,12,24):
    for v in vec(pay,off):
      if not fin(v):P("station vec@%d"%off)
  for o in (36,):  # rotation floats
    for v in vec(pay,o)+[f32(pay,o+12)]:
      if not fin(v):P("station rot")
  cid=i16(pay,52); sid=i16(pay,54); stid_i=i16(pay,56); stype=i16(pay,58)
  if cid not in clsids[fi] and cid!=-1:P("station clusterID invalid")
  if stype not in known_st:P("stationTypeID unknown (%d)"%stype)
  for pc in (pay[60],pay[61]):
    if not(0<=pc<=255):P("station bp range")
  name=s(pay,62,25)
  if ctrlstr(pay,62,25):P("station ctl name")

# ---------------- CLUSTER ----------------
cn=0
for fi,pay in recs.get(13,[]):
  cn+=1
  if len(pay)!=92:P("cluster len")
  for v in vec(pay,4):
    if not fin(v):P("cluster lightdir")
  for v in (f32(pay,20),f32(pay,24)):
    if not(-5e4<v<5e4):P("cluster screen")
  if i16(pay,28)<0:P("cluster id<0")
  for off,n in ((34,25),(59,13),(72,13)):
    if ctrlstr(pay,off,n):P("cluster ctl@%d"%off)
  for pc in (pay[85],pay[86],pay[87]):
    if not(0<=pc<=255):P("cluster planet range")
  if pay[88]!=0:P("cluster activeF nonzero!")     # export forces false
  # homeSector can be 0/1 in generated files; note noncanonical elsewhere

# ---------------- WARP ----------------
wn=0
for fi,pay in recs.get(7,[]):
  wn+=1
  for off,n in ((6,13),(19,13),(32,25)):
    if ctrlstr(pay,off,n):P("warp ctl@%d"%off)
  for off in (60,72):
    for v in vec(pay,off):
      if not fin(v):P("warp vec@%d"%off)
  for o in (84,):
    for v in vec(pay,o)+[f32(pay,o+12)]:
      if not fin(v):P("warp rot")
  if not fin(f32(pay,100)):P("warp sig")
  if i16(pay,104) not in clsids[fi]:P("warp clusterID invalid")

# ---------------- ASTEROID ----------------
an=0
for fi,pay in recs.get(5,[]):
  an+=1
  if len(pay)!=144:P("asteroid len")
  if not fin(f32(pay,0)):P("asteroid sig")
  for off in (4,16,28):
    for v in vec(pay,off):
      if not fin(v):P("asteroid vec@%d"%off)
  for o in (40,):
    for v in vec(pay,o)+[f32(pay,o+12)]:
      if not fin(v):P("asteroid rot")
  if not fin(f32(pay,56)) or not fin(f32(pay,60)):P("asteroid ore")
  if not fin(f32(pay,68)):P("asteroid hp")
  for off,n in ((74,13),(87,13),(100,13),(118,25)):
    if ctrlstr(pay,off,n):P("asteroid ctl@%d"%off)
  if i16(pay,116) not in clsids[fi]:P("asteroid clusterID invalid")
  if not(0<=pay[143]<=255):P("asteroid fraction")

print("records: station=%d cluster=%d warp=%d asteroid=%d"%(sn,cn,wn,an))
print("known stationTypeIDs:",len(known_st),"distinct OT31 stationTypeId values in corpus")
print("PROBLEMS:",dict(probs) if probs else "NONE")
# sanity on bHomeSector
hc=collections.Counter()
for fi,pay in recs.get(13,[]): hc[pay[89]]+=1
print("cluster homeSector@89 histogram:",dict(sorted(hc.items())))
# which files have homeSector>1
sel=set()
for fi,pay in recs.get(13,[]):
  if pay[89]>1:
    # map file -> just count how many map files
    sel.add("map")
print("notes: activeF@88 forced 0 everywhere:", all(pay[88]==0 for fi,pay in recs.get(13,[])))
