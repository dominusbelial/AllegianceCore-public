#!/usr/bin/env python3
"""Augment a LOCAL proof generation with original probe/treasure/projectile art.
Not invoked by publication scripts. The existing BattleManifest/AssetStager verify
this closure normally; no verification, canonical-name or CSP changes.
"""
import argparse,gzip,hashlib,importlib.util,json,re
from pathlib import Path


def build(root,artwork):
    spec=importlib.util.spec_from_file_location('flight',Path(__file__).with_name('p1051-browser-packs.py'))
    flight=importlib.util.module_from_spec(spec);spec.loader.exec_module(flight)
    index=json.loads((root/'battle-manifest.json').read_text())
    resident={a['source']:a for a in json.loads((root/'join-manifest.json').read_text())['assets']}
    entries={a['source']:a for a in index['assets']}
    files={p.relative_to(artwork).as_posix().lower():p for p in artwork.rglob('*') if p.is_file()}
    pending=[n+'.mdl' for n in ['dgarr','bgrnd05','quizscoutv2','mis05','utl02','acs14','bgrnd55']]
    selected=set()
    while pending:
        name=pending.pop()
        if name in selected:continue
        selected.add(name)
        asset=resident.get(name) or entries.get(name)
        if asset is None:
            data=files[name].read_bytes();digest=hashlib.sha256(data).hexdigest();deps=[]
            for stem in flight.strings(data):
                candidate=stem.lower()+'.mdl'
                if candidate in files:deps.append(candidate)
                elif candidate.endswith('bbmp.mdl') and candidate[:-8]+'bmp.mdl' in files:deps.append(candidate[:-8]+'bmp.mdl')
            if name.endswith('.mdl') and not data.startswith(b'\x00\xdf\xba\xde'):
                deps += [n.lower() for n in re.findall(r'ImportImageFromFile\s*\(\s*"([^"]+)"',data.decode('latin1'))]
            asset=dict(source=name,path='packs/'+digest,byteSize=len(data),sha256=digest,contentType='application/octet-stream',
                provenance='FreeAllegiance Artwork; MIT (2017 FreeAllegiance); original local P1075 regression fixture',dependencies=sorted(set(deps)))
            entries[name]=asset;(root/asset['path']).write_bytes(data);(root/(asset['path']+'.gz')).write_bytes(gzip.compress(data,9,mtime=0))
        pending.extend(asset['dependencies']);pending.extend(asset.get('visualDependencies')or[])
    index['assets']=sorted(entries.values(),key=lambda a:a['source'])
    index['packs']=[p for p in index['packs'] if p['key']!='proof-p1075-art']+[dict(key='proof-p1075-art',sources=sorted(selected))]
    (root/'battle-manifest.json').write_text(json.dumps(index,indent=2)+'\n')
    return len(selected)

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--generation',type=Path,required=True);p.add_argument('--artwork',type=Path,required=True);args=p.parse_args()
    print('P1075 proof closure sources:',build(args.generation,args.artwork))
