"""Equivalent repository hygiene checks using read-only index names, never git."""
import csv,json,struct
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
meta=Path((ROOT/'.git').read_text().strip().split(': ',1)[1]);b=(meta/'index').read_bytes();sig,version,count=struct.unpack_from('!4sII',b);assert sig==b'DIRC' and version==2
pos=12;tracked=set()
for _ in range(count):
    start=pos;fields=struct.unpack_from('!10I20sH',b,pos);pos+=62
    if fields[-1]&0x4000:pos+=2
    end=b.index(b'\0',pos);tracked.add(b[pos:end].decode());pos=start+((end-start+8)//8)*8
binary={'.dll','.exe','.png','.jpg','.jpeg','.gif','.ico','.woff2','.ogg','.wav','.mp3','.ttf','.mgfxo','.dat','.bin','.pdb','.cov'}
targets={'src','tests','scripts','tools','docker','.github'}
private=[]
for name in sorted(tracked):
    p=ROOT/name
    if Path(name).parts[0] in targets and p.suffix.lower() not in binary and p.is_file():
        if '/home/'+'domingo' in p.read_text(encoding='utf-8-sig',errors='replace'):private.append(name)
models={'.fbx','.fbm','.obj','.glb','.gltf','.blend','.mtl'}
media=models|{'.png','.jpg','.jpeg','.gif','.webp','.bmp','.tga','.mp4','.webm'}
locations=('src/Allegiance.ClientCore.MonoGame/Content/Models/','src/Allegiance.ClientCore.MonoGame/Content/Textures/ships/','assets/ships/thumbs/')
allowed=('docs/meshy_hints/','tools/meshy-repair/')
meshy=[]
for name in sorted(tracked):
    suffix=Path(name).suffix.lower()
    if name.startswith(locations) or suffix in models or suffix in media and 'meshy' in name.lower() and not name.startswith(allowed):meshy.append(name)
with (ROOT/'docs/release/media-inventory.csv').open() as stream:
    for row in csv.DictReader(stream):
        # Match the C# guard's third CSV column, regardless of its title.
        values=list(row.values())
        if len(values)>=3 and values[2].strip()=='a' and values[0].strip() in tracked:meshy.append(values[0].strip())
trees=json.loads((OUT/'tree-blobs.json').read_text())
unchanged_manifests=all(name.endswith(('.json','.md')) and trees['ours_files'].get(name)==trees['theirs_files'].get(name)==trees['base_files'].get(name) for name in meshy)
result=dict(indexed_paths=len(tracked),private_path_offenders=private,meshy_offenders=meshy,meshy_rule_would_pass=not meshy,all_meshy_hits_are_unchanged_manifest_documents=unchanged_manifests,read_only_index_enumeration=True)
(OUT/'hygiene-checks.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2));assert not private and unchanged_manifests
