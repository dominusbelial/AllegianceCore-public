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
(OUT/'tree-blobs.json').write_text(json.dumps(dict(base=base,ours=ours,theirs=theirs,base_files=B,ours_files=O,theirs_files=T),indent=2)+'\n')
changes=[p for p in sorted(T) if T[p]!=B.get(p)]
(OUT/'incoming-changes.json').write_text(json.dumps(changes,indent=2)+'\n')
# Every matrix row is assessed against the common base, including auto-merges.
audit=[]
for name in ('docs/parity/parity-matrix.csv','docs/parity/sim-rules-matrix.csv','docs/parity/fedmsg-wiring.csv'):
    def read(data):return list(csv.DictReader(io.StringIO(data.decode())))
    def key(row):return row.get('row_id') or row.get('id') or (row['area'],row['native_item'])
    maps=[{key(r):r for r in read(objects.read(v[name]))} for v in (B,O,T)]
    b,o,t=maps;r={key(row):row for row in read((ROOT/name).read_bytes())}
    assert b.keys()==o.keys()==t.keys()==r.keys(),name
    for k in b:
        decision='both changed'
        if o[k]==t[k]:assert r[k]==o[k],(name,k);decision='identical'
        elif o[k]==b[k]:assert r[k]==t[k],(name,k);decision='incoming only'
        elif t[k]==b[k]:assert r[k]==o[k],(name,k);decision='ours only'
        if o[k]!=t[k]:audit.append(dict(file=name,key=k,decision=decision,base=b[k],ours=o[k],theirs=t[k],resolved=r[k]))
    assert b'\r' not in (ROOT/name).read_bytes()
(OUT/'all-parity-row-audit.json').write_text(json.dumps(audit,indent=2)+'\n')
name='docs/TEST_FLAKES.md';current=set((ROOT/name).read_text().splitlines())
for v in (O,T):assert set(objects.read(v[name]).decode().splitlines())<=current
# Existing sound-wave reports survive exactly; incoming reports are append-only.
reports=[p for p in O if re.search(r'docs/reports/P(710|711|714|716|717|719|721|723|725)_.*\.md$',p)]
for p in reports:assert (ROOT/p).read_bytes()==objects.read(O[p]),p
for p in ('docs/reports/P735_PARITY_WAVE22.md','docs/reports/P736_PARITY_WAVE23.md','docs/reports/P737_PARITY_WAVE24.md'):
    assert (ROOT/p).read_bytes().startswith(objects.read(T[p])),p
# Both enums/case tables are a strict union with one playback dispatch per kind.
name='src/Allegiance.ClientCore/State/ClientSoundEvent.cs'
def kinds(text):
    enum=text.split('public enum ClientSoundEventKind',1)[1].split('}',1)[0]
    return re.findall(r'^    (\w+)(?:\s*=\s*\d+)?,',enum,re.M)
merged=kinds((ROOT/name).read_text());expected=set(kinds(objects.read(O[name]).decode()))|set(kinds(objects.read(T[name]).decode()))
cases=re.findall(r'case ClientSoundEventKind\.(\w+)\s*:',(ROOT/'src/Allegiance.ClientCore.Game/Game1.Audio.cs').read_text())
assert len(merged)==len(set(merged)) and len(cases)==len(set(cases)) and set(merged)==set(cases)==expected
# Every source content asset remains registered exactly once.
name='src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb';pattern=r'^/build:(.+)$'
assets=re.findall(pattern,(ROOT/name).read_text(),re.M)
expected=set(re.findall(pattern,objects.read(O[name]).decode(),re.M))|set(re.findall(pattern,objects.read(T[name]).decode(),re.M))
assert len(assets)==len(set(assets)) and set(assets)==expected
for asset in assets:assert (ROOT/name).parent.joinpath(asset.split(';')[0]).exists(),asset
for name in ('src/Allegiance.ClientCore/State/ClientWorld.cs','src/Allegiance.ClientCore/Session/ConnectionSession.cs'):
    text=(ROOT/name).read_text()
    for msg in ('QuitSide','QuitMission'):
        assert len(re.findall(r'case '+msg+r'(?:\.Fmid|\s)',text))==1,(name,msg)
for top in ('src','tests','docs'):
    for p in (ROOT/top).rglob('*'):
        if p.is_file() and p.suffix in ('.cs','.csv','.md','.mgcb') and not {'bin','obj','before'} & set(p.parts):
            assert not re.search(r'^(<<<<<<< |=======$|>>>>>>> )',p.read_text(),re.M),p
summary=dict(base=base,ours=ours,theirs=theirs,conflicted_files=list(json.loads((OUT/'index-stages.json').read_text())),sound_event_count=len(merged),content_asset_count=len(assets),audio_union_without_duplicate_dispatch=True,content_union_without_duplicate_builds=True,flake_union=True,existing_sound_reports_unchanged=reports,LF_matrices=True,parity_decisions=dict(collections.Counter(r['decision'] for r in audit)))
(OUT/'resolution-audit.json').write_text(json.dumps(summary,indent=2)+'\n');print(json.dumps(summary,indent=2))
