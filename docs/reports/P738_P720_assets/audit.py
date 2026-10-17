#!/usr/bin/env python3
"""Audit P720 integration via read-only object access; never invokes git."""
from pathlib import Path
import csv, hashlib, io, json, re
from object_reader import ObjectReader
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
meta=ROOT/'.git';gitdir=Path(meta.read_text().strip().split(': ',1)[1]);common=(gitdir/(gitdir/'commondir').read_text().strip()).resolve();objects=ObjectReader(common)
head=(gitdir/'HEAD').read_text().strip()
if head.startswith('ref: '):
 ref=head[5:];p=common/ref
 if p.exists():head=p.read_text().strip()
 else:head=next(line.split()[0] for line in (common/'packed-refs').read_text().splitlines() if line.endswith(' '+ref))
incoming=(gitdir/'MERGE_HEAD').read_text().strip()
def files(commit):
 result={}
 tree=objects.read(commit).split(b'\n',1)[0].split()[1].decode()
 def visit(oid,prefix=''):
  raw=objects.read(oid);pos=0
  while pos<len(raw):
   end=raw.index(b'\0',pos);mode,name=raw[pos:end].split(b' ',1);child=raw[end+1:end+21].hex();pos=end+21
   path=prefix+name.decode()
   if mode==b'40000':visit(child,path+'/')
   else:result[path]=child
 visit(tree);return result
ours=files(head);theirs=files(incoming)
critical=['src/Allegiance.ClientCore.Game/Game1.Audio.cs','src/Allegiance.ClientCore/State/ClientSoundEvent.cs','src/Allegiance.ClientCore.Game/Audio/SoundManager.cs','docs/parity/sim-rules-matrix.csv']
for path in critical:assert (ROOT/path).read_bytes()==objects.read(ours[path]),path
reports=[f'docs/reports/P{n}_PARITY_WAVE{w}.md' for n,w in [(710,13),(711,14),(714,15),(716,16),(717,17),(719,18),(721,19),(723,20),(725,21)]]
for path in reports:assert (ROOT/path).read_bytes()==objects.read(ours[path]),path
enum=(ROOT/critical[1]).read_text().split('public enum ClientSoundEventKind',1)[1].split('}',1)[0]
kinds=re.findall(r'^    ([A-Za-z]\w*)(?:\s*=\s*\d+)?,',enum,re.M)
cases=re.findall(r'case ClientSoundEventKind\.(\w+)\s*:',(ROOT/critical[0]).read_text())
assert len(kinds)==len(set(kinds)) and len(cases)==len(set(cases)) and set(kinds)==set(cases)
def rows(data):return {(r['area'],r['native_item']):r for r in csv.DictReader(io.StringIO(data))}
path='docs/parity/parity-matrix.csv';o=rows(objects.read(ours[path]).decode());t=rows(objects.read(theirs[path]).decode());r=rows((ROOT/path).read_text());changes=[{'key':k,'before':o[k],'after':v} for k,v in r.items() if v!=o[k]]
assert len(changes)==14 and all(k['key'][0]=='commands' and k['after']['port_status']=='done' for k in changes)
assert all(v==o[k] or v==t[k] for k,v in r.items())
for path in ['docs/parity/parity-matrix.csv','docs/parity/sim-rules-matrix.csv','docs/parity/fedmsg-wiring.csv']:assert b'\r' not in (ROOT/path).read_bytes()
for path in list(ROOT.glob('src/**/*.cs'))+list(ROOT.glob('tests/**/*.cs'))+list(ROOT.glob('docs/**/*.md'))+list(ROOT.glob('docs/parity/*.csv')):
 if {'bin','obj'}&set(path.parts):continue
 assert not re.search(r'^(<<<<<<< |=======\s*$|>>>>>>> )',path.read_text(),re.M),str(path)
fingerprints={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for top in ['src','tests'] for p in (ROOT/top).rglob('*') if p.is_file() and p.suffix in ['.cs','.csproj'] and not {'bin','obj'}&set(p.parts)}
(OUT/'source-fingerprints.json').write_text(json.dumps(fingerprints,indent=2)+'\n')
(OUT/'resolution-audit.json').write_text(json.dumps({'merge_target':'codex/p720','ours':head,'theirs':incoming,'conflicted_files':['docs/TEST_FLAKES.md','docs/parity/parity-matrix.csv'],'master_audio_and_simulation_files_unchanged':critical,'sound_events':kinds,'playback_cases':cases,'unique_complete_playback':True,'master_reports_unchanged':reports,'changes_from_master':changes,'LF_matrices':True},indent=2)+'\n')
print('Audit passed:',len(kinds),'unique sound event/playback pairs;',len(changes),'command row changes; master sound files and reports unchanged.')
if __name__=='__main__':
 import difflib
 for path in sorted(p for p in ours if p.startswith('src/') and p.endswith('.cs') and (ROOT/p).exists() and (ROOT/p).read_bytes()!=objects.read(ours[p])):
  print(path)
  if '--diff' in __import__('sys').argv:
   print(''.join(difflib.unified_diff(objects.read(ours[path]).decode().splitlines(True),(ROOT/path).read_text().splitlines(True),fromfile='master/'+path,tofile=path)))
