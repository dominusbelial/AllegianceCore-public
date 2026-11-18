#!/usr/bin/env python3
"""Adapt the installed .NET runtime's MIT Google Brotli objects into isolated WASM modules.

No native code is invented or recompiled. Each object retains its own function,
type and global index space; only data addresses and callback table entries are
relocated. Unsupported relocation kinds fail closed. Debug/linking sections are
removed. p1051-brotli-tests.cjs exercises the resulting production decoder.
"""
import argparse,json,struct,subprocess
from pathlib import Path
class R:
 def __init__(self,b):self.b=b;self.p=0
 def u(self):
  v=s=0
  while True:
   x=self.b[self.p];self.p+=1;v|=(x&127)<<s;s+=7
   if x<128:return v
 def signed(self):
  v=s=0
  while True:
   x=self.b[self.p];self.p+=1;v|=(x&127)<<s;s+=7
   if x<128:return v-(1<<s) if x&64 else v
 def name(self):return self.bytes(self.u()).decode()
 def bytes(self,n):b=self.b[self.p:self.p+n];self.p+=n;return b

def u(v):
 b=bytearray()
 while v>=128:b.append((v&127)|128);v>>=7
 b.append(v);return bytes(b)
def si(v):
 b=bytearray()
 while True:
  q=v&127;v>>=7;done=(v==0 and q&64==0)or(v==-1 and q&64!=0);b.append(q if done else q|128)
  if done:return bytes(b)
def name(n):b=n.encode();return u(len(b))+b
def patch(b,p,v,signed=False):
 # LLVM reserves five bytes in executable 32-bit relocation operands.
 original=p
 while b[p]&128:p+=1
 size=p-original+1
 out=bytearray()
 for i in range(size):out.append((v&127)|(128 if i+1<size else 0));v>>=7
 b[original:original+size]=out

class Module:
 def __init__(self,key,b):
  self.key=key;self.sections=[];self.imports={0:[],1:[],2:[],3:[]};self.syms=[];self.segments=[];self.relocs=[];self.exports=[]
  r=R(b);r.p=8
  while r.p<len(b):sid=r.u();body=bytearray(r.bytes(r.u()));self.sections.append([sid,body])
  for sid,body in self.sections:
   q=R(body)
   if sid==2:
    for _ in range(q.u()):
     mod=q.name();n=q.name();kind=q.u();self.imports[kind].append(n)
     if kind==0:q.u()
     elif kind==3:q.u();q.u()
     elif kind==1:q.u();flags=q.u();q.u();q.u() if flags&1 else None
     elif kind==2:flags=q.u();q.u();q.u() if flags&1 else None
   if sid==0:
    n=q.name()
    if n=='linking':
     assert q.u()==2
     while q.p<len(body):
      sub=q.u();d=R(q.bytes(q.u()))
      if sub==8:
       for _ in range(d.u()):
        kind=d.u();flags=d.u();sym={'kind':kind,'flags':flags,'module':self}
        if kind in [0,2,4,5]:
         index=d.u();sym['index']=index
         sym['name']=d.name() if not flags&16 or flags&64 else self.imports[{0:0,2:3,5:1}.get(kind,0)][index]
        elif kind==1:
         sym['name']=d.name()
         if not flags&16:sym.update(segment=d.u(),offset=d.u(),size=d.u())
        elif kind==3:sym['index']=d.u();sym['name']='section'+str(sym['index'])
        else:raise ValueError(('symbol',kind))
        self.syms.append(sym)
      if sub==5:
       for _ in range(d.u()):self.segments.append({'name':d.name(),'align':d.u(),'flags':d.u()})
    elif n in ['reloc.CODE','reloc.DATA']:
     target=q.u();rel=[]
     for _ in range(q.u()):
      kind=q.u();off=q.u();index=q.u();add=q.signed() if kind in [3,4,5,8,9,11,14,15,16,17,21,22] else 0
      rel.append((kind,off,index,add))
     assert q.p==len(body),(self.key,n,q.p,len(body))
     self.relocs.append((target,rel))
  data=next((body for sid,body in self.sections if sid==11),None)
  self.data=[]
  if data:
   q=R(data)
   for i in range(q.u()):
    mode=q.u();assert mode==0;assert q.u()==65;old=q.signed();assert q.u()==11;size=q.u();blob=q.bytes(size);self.data.append([blob,0]);self.segments[i]['size']=size
   assert q.p==len(data)

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--native-dir',required=True,type=Path)
parser.add_argument('--output',required=True,type=Path)
args=parser.parse_args()
root=args.native_dir
out=args.output;out.mkdir(parents=True,exist_ok=True)
modules=[]
for lib in ['libbrotlicommon.a','libbrotlidec.a']:
 for member in subprocess.check_output(['ar','t',str(root/lib)],text=True).splitlines():
  modules.append(Module(lib[3:-2]+'-'+member,subprocess.check_output(['ar','p',str(root/lib),member])))
# Resolve global symbols while retaining module-local symbol identity.
global_syms={s['name']:s for m in modules for s in m.syms if s['kind'] in [0,1,2,5] and not s['flags']&18}
def resolve(s):return global_syms.get(s['name'],s) if s['flags']&16 else s
cursor=65536
for m in modules:
 for i,seg in enumerate(m.segments):
  align=1<<seg['align'];cursor=(cursor+align-1)&-align;seg['base']=cursor;m.data[i][1]=cursor;cursor+=seg['size']
print('staticDataEnd',cursor)
table=[]
def table_index(sym):
 sym=resolve(sym);key=(sym['module'].key,sym['index']);
 if key not in table:table.append(key)
 return table.index(key)+1
for m in modules:
 for section,relocs in m.relocs:
  b=m.sections[section][1]
  for kind,off,index,add in relocs:
   if kind in [0,6,7,20]:continue # preserve each module's own indices
   sym=resolve(m.syms[index])
   if kind in [1,2]:value=table_index(sym)+add
   elif kind in [3,4,5]:
    assert 'segment'in sym,(m.key,sym)
    value=sym['module'].segments[sym['segment']]['base']+sym['offset']+add
   else:raise ValueError(('reloc',kind))
   if kind in [2,5]:b[off:off+4]=struct.pack('<I',value&0xffffffff)
   else:patch(b,off,value,signed=kind in [1,4])
 # Reparse patched data, then place each segment at its allocated shared address.
 data=next((b for sid,b in m.sections if sid==11),None)
 if data:
  q=R(data);n=q.u();rebuilt=u(n)
  for i in range(n):
   assert q.u()==0 and q.u()==65;q.signed();assert q.u()==11;blob=q.bytes(q.u());rebuilt+=b'\0\x41'+si(m.segments[i]['base'])+b'\x0b'+u(len(blob))+blob
  for sec in m.sections:
   if sec[0]==11:sec[1]=rebuilt
 # Every defined function is available for late-bound imports and table setup.
 exports=[]
 for s in m.syms:
  if s['kind']==0 and not s['flags']&16:
   export='local_'+str(s['index']) if s['flags']&2 else s['name'];exports.append((export,s['index']));s['export']=export
 ex=u(len(exports))+b''.join(name(n)+b'\0'+u(i) for n,i in exports)
 binary=b'\0asm\x01\0\0\0';added=False
 for sid,body in m.sections:
  if sid==0 or sid in [7,9]:continue
  if sid>7 and not added:binary+=b'\x07'+u(len(ex))+ex;added=True
  binary+=u(sid)+u(len(body))+body
 if not added:binary+=b'\x07'+u(len(ex))+ex
 (out/(m.key+'.wasm')).write_bytes(binary)
metadata={'modules':[m.key+'.wasm' for m in modules], 'staticDataEnd':cursor,'table':[]}
for key,index in table:
 m=next(m for m in modules if m.key==key);sym=next(s for s in m.syms if s['kind']==0 and s['index']==index and not s['flags']&16)
 metadata['table'].append({'module':modules.index(m),'export':sym['export']})
(out/'modules.json').write_text(json.dumps(metadata,indent=2))
print(json.dumps(metadata))
