"""Audit both branch intents using read-only object data and working files."""
import collections,csv,hashlib,io,json,re,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
sys.dont_write_bytecode=True
sys.path.insert(0,str(OUT.parent/'P738_P720_assets'))
from object_reader import ObjectReader
meta=ROOT/'.git';gitdir=Path(meta.read_text().strip().split(': ',1)[1]);common=(gitdir/(gitdir/'commondir').read_text().strip()).resolve();objects=ObjectReader(common)
def ref(name):
    for directory in (gitdir,common):
        p=directory/name
        if p.exists():
            value=p.read_text().strip();return ref(value[5:]) if value.startswith('ref: ') else value
    for line in (common/'packed-refs').read_text().splitlines():
        if line.endswith(' '+name):return line.split()[0]
    raise KeyError(name)
def parents(commit):return [line.split()[1].decode() for line in objects.read(commit).split(b'\n\n',1)[0].splitlines() if line.startswith(b'parent ')]
def ancestors(commit):
    seen=set();queue=collections.deque([commit])
    while queue:
        c=queue.popleft()
        if c in seen:continue
        seen.add(c);yield c;queue.extend(parents(c))
ours=ref('HEAD');theirs=ref('MERGE_HEAD');ours_ancestors=set(ancestors(ours));base=next(c for c in ancestors(theirs) if c in ours_ancestors)
def files(commit):
    result={};tree=objects.read(commit).split(b'\n',1)[0].split()[1].decode()
    def walk(oid,prefix=''):
        data=objects.read(oid);pos=0
        while pos<len(data):
            end=data.index(b'\0',pos);mode,name=data[pos:end].decode().split(' ',1);oid=data[end+1:end+21].hex();pos=end+21
            if mode=='40000':walk(oid,prefix+name+'/')
            else:result[prefix+name]=oid
    walk(tree);return result
B,O,T=map(files,(base,ours,theirs))
(OUT/'input-trees.json').write_text(json.dumps(dict(base=base,ours=ours,theirs=theirs,file_counts=dict(base=len(B),ours=len(O),theirs=len(T)),incoming_blobs={p:dict(base=B.get(p),ours=O.get(p),theirs=T[p]) for p in T if T[p]!=B.get(p)}),indent=2)+'\n')
changes=[p for p in sorted(T) if T[p]!=B.get(p)]
(OUT/'incoming-changes.json').write_text(json.dumps(changes,indent=2)+'\n')
