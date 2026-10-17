import bisect, struct, zlib
from functools import lru_cache
from pathlib import Path
class ObjectReader:
 def __init__(self, common):
  self.root=common/'objects';self.indices=[]
  for path in (self.root/'pack').glob('*.idx'):
   data=path.read_bytes();assert data[:8]==b'\xfftOc\x00\x00\x00\x02'
   n=struct.unpack_from('!I',data,8+255*4)[0];start=8+1024
   self.indices.append((path,[data[start+i*20:start+(i+1)*20] for i in range(n)],data,start+24*n,n))
 @lru_cache(None)
 def read(self, oid):
  loose=self.root/oid[:2]/oid[2:]
  if loose.exists():return zlib.decompress(loose.read_bytes()).split(b'\0',1)[1]
  key=bytes.fromhex(oid)
  for path,oids,index,offsets,n in self.indices:
   i=bisect.bisect_left(oids,key)
   if i<n and oids[i]==key:
    offset=struct.unpack_from('!I',index,offsets+4*i)[0]
    if offset&0x80000000:offset=struct.unpack_from('!Q',index,offsets+4*n+8*(offset&0x7fffffff))[0]
    return self.unpack(str(path.with_suffix('.pack')),offset)
  raise KeyError(oid)
 @lru_cache(None)
 def unpack(self,path,offset):
  with open(path,'rb') as f:
   f.seek(offset)
   def byte():return f.read(1)[0]
   c=byte();kind=(c>>4)&7;size=c&15;shift=4
   while c&128:c=byte();size|=(c&127)<<shift;shift+=7
   if kind==6:
    c=byte();distance=c&127
    while c&128:c=byte();distance=((distance+1)<<7)|(c&127)
    base=self.unpack(path,offset-distance)
   elif kind==7:base=self.read(f.read(20).hex())
   dec=zlib.decompressobj();parts=[]
   while not dec.eof:parts.append(dec.decompress(f.read(65536)))
   data=b''.join(parts);assert len(data)==size
  if kind not in (6,7):return data
  p=0
  def varint():
   nonlocal p
   val=shift=0
   while True:
    c=data[p];p+=1;val|=(c&127)<<shift;shift+=7
    if not c&128:return val
  assert varint()==len(base);expected=varint();out=bytearray()
  while p<len(data):
   c=data[p];p+=1
   if c&128:
    off=count=0
    for j in range(4):
     if c&(1<<j):off|=data[p]<<(8*j);p+=1
    for j in range(3):
     if c&(1<<(4+j)):count|=data[p]<<(8*j);p+=1
    count=count or 65536;out.extend(base[off:off+count])
   else:
    assert c;out.extend(data[p:p+c]);p+=c
  assert len(out)==expected;return bytes(out)
