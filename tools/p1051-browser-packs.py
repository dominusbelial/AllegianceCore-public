#!/usr/bin/env python3
"""Stage a measured first-sector join closure from both real desktop flight traces.
Original geometry/core remain exact. The explicit rock-only surface cap is the
budget fallback; its source hashes, pixel transform and byte savings are receipted.
"""
import argparse, gzip, hashlib, json, re, struct
from pathlib import Path

def strings(data):
    if len(data) < 24 or struct.unpack_from('<I', data)[0] != 0xDEBADF00:
        return []
    count = struct.unpack_from('<I', data, 8)[0]
    p, result = 24, []
    for _ in range(count):
        end = data.index(0, p)
        result.append(data[p:end].decode('ascii'))
        p = (end + 4) & ~3
    return result

def cap_surface(data, maximum=256):
    # BinarySurfaceInfo layout is shared with MdlTextureDecoder.Decode.
    _, _, namespaces, imports, exports, _ = struct.unpack_from('<6I', data)
    pos = 24
    def skip(p): return (data.index(0, p) + 4) & ~3
    for _ in range(namespaces): pos = skip(pos)
    for _ in range(imports): pos = skip(pos + 4)
    for _ in range(exports): pos = skip(pos)
    pos += 16
    w, h, pitch, bits = struct.unpack_from('<4i', data, pos)
    if bits != 16 or w <= 0 or h <= 0 or pitch < w * 2: raise ValueError('Expected RGB565 rock surface')
    if max(w, h) <= maximum: return data, (w,h), (w,h)
    scale = max(w, h) / maximum
    nw, nh = max(1, int(w/scale)), max(1, int(h/scale)); np = (nw*2+3)&~3
    pixel = pos + 36
    if pixel + pitch*h > len(data): raise ValueError('Truncated rock surface')
    target = bytearray(np*nh)
    for y in range(nh):
        for x in range(nw):
            a = pixel + int(y*h/nh)*pitch + int(x*w/nw)*2
            target[y*np+x*2:y*np+x*2+2] = data[a:a+2]
    header = bytearray(data[:pixel]); struct.pack_into('<3i',header,pos,nw,nh,np)
    result = bytes(header) + bytes(target) + data[pixel+pitch*h:]
    # Independent raw RGB565 point-sampling equality, including first/last rows.
    for y in range(nh):
        for x in range(nw):
            assert result[pixel+y*np+x*2:pixel+y*np+x*2+2] == data[pixel+int(y*h/nh)*pitch+int(x*w/nw)*2:pixel+int(y*h/nh)*pitch+int(x*w/nw)*2+2]
    return result, (w,h), (nw,nh)

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--traces',nargs='+',required=True,type=Path);ap.add_argument('--artwork',required=True,type=Path)
    ap.add_argument('--content',required=True,type=Path);ap.add_argument('--output',required=True,type=Path)
    ap.add_argument('--receipt',required=True,type=Path); args=ap.parse_args()
    traces=[json.loads(p.read_text()) for p in args.traces]
    if {t['style'] for t in traces}!={'Classic','Modern'}: raise ValueError('Both styles required')
    for t in traces:
        if t.get('flightSeconds',0)<60 or not {t['style']+'/'+s for s in ['Docked','Flight']}.issubset(t['Scenes']): raise ValueError('Incomplete flight recording')
    observed={o['Source'].lower():o for t in traces for o in t['Opens']}
    recorded={k: v for k,v in observed.items()}
    entry_sector=traces[0]['FirstSector']['Name']
    required=set(); deferred=[]
    for trace in traces:
        for source in trace['Required']:
            background=(source.lower()==trace['FirstSector']['PlanetName'].lower()+'.mdl' or re.fullmatch(r'(?:globe[0-9]+|environment[0-9]+bmp)\.mdl',source.lower()))
            if trace['FirstSector']['Name']!=entry_sector and background: deferred.append(source.lower())
            else: required.add(source.lower())
    rocks={r.lower() for t in traces for r in t['RockSurfaces']}
    sources={p.relative_to(args.artwork).as_posix().lower():p for p in args.artwork.rglob('*') if p.is_file()}
    starter=json.loads((args.output/'starter-manifest.json').read_text())
    starter['assets']=[a for a in starter['assets'] if not a['source'].startswith('runtime/brotli/')]
    entries={a['source']:a for a in starter['assets']}; data_by_hash={}
    # Runtime object files are staged with the initial verified starter snapshot.
    decoder_root=Path(__file__).resolve().parents[1]/'src/Allegiance.ClientCore.Browser/wwwroot/brotli'
    for module in json.loads((decoder_root/'modules.json').read_text())['modules']:
        data=(decoder_root/module).read_bytes();sha=hashlib.sha256(data).hexdigest();source='runtime/brotli/'+module
        entry=dict(source=source,path='packs/'+sha,byteSize=len(data),sha256=sha,contentType='application/octet-stream',provenance='Google Brotli; MIT; .NET browser-wasm runtime 10.0.15; relocated data/callback table, debug sections removed',dependencies=[])
        entries[source]=entry;starter['assets'].append(entry);data_by_hash[sha]=data
    (args.output/'starter-manifest.json').write_text(json.dumps(starter,indent=2)+'\n')
    # All small recorded native namespaces support the shared HUD/status/contacts.
    # Large surfaces are selected only through the observed first-sector roots.
    required.update(k for k,o in recorded.items() if k.endswith('bmp.mdl') and o['ByteSize']<=1024*1024)
    required.update(k for k,o in recorded.items() if k.endswith('.cvh'))
    # Real native miners appear when the mission rotates after voluntary leave.
    # Both models were opened by the desktop's boot-time native hull catalogue;
    # their dependencies must pass the same observed size/hash closure checks.
    companion_models=['dminr.mdl','wc_gtmnr.mdl']
    if any(k not in recorded for k in companion_models): raise ValueError('Missing recorded native companion model')
    required.update(companion_models)
    required.update(k for k,o in recorded.items() if k.endswith('.png') and o['Scene']=='Boot' and o['ByteSize']<=1024*1024)
    selected=set(); pending=list(required); dependencies={}
    while pending:
        key=pending.pop()
        if key in selected or key not in sources: continue # modeler built-in namespaces have no artwork file
        path=sources[key]; data=path.read_bytes(); sha=hashlib.sha256(data).hexdigest()
        if key not in recorded: raise ValueError('Dependency was not actually opened by desktop: '+key)
        witness=recorded[key]
        if len(data)!=witness['ByteSize'] or sha!=witness['Sha256']: raise ValueError('Recording changed: '+key)
        selected.add(key)
        deps=[n.lower()+'.mdl' for n in strings(data) if n.lower()+'.mdl' in sources]
        if not data.startswith(b'\x00\xdf\xba\xde') and key.endswith('.mdl'):
            deps.extend(n.lower() for n in re.findall(r'ImportImageFromFile\s*\(\s*"([^"]+)"',data.decode('latin1')) if n.lower() in sources)
        dependencies[key]=deps;pending.extend(deps)
        if re.fullmatch(r'bgrnd[0-9]+\.mdl',key): rocks.update(d for d in deps if d.endswith('bmp.mdl'))
    fallbacks=[]
    for key in sorted(selected):
        data=sources[key].read_bytes(); original=data
        if key in rocks:
            data,before,after=cap_surface(data)
            if data != original: fallbacks.append(dict(file=key,originalBytes=len(original),originalGzipBytes=len(gzip.compress(original,9,mtime=0)),bytes=len(data),gzipBytes=len(gzip.compress(data,9,mtime=0)),sourceSha256=hashlib.sha256(original).hexdigest(),before=before,after=after,transform='RGB565 nearest point sampling, original UVs, geometry untouched'))
        sha=hashlib.sha256(data).hexdigest();data_by_hash[sha]=data
        provenance='FreeAllegiance Artwork; MIT (2017 FreeAllegiance)'
        if data != original: provenance += '; explicit P1051 256-pixel rock surface budget fallback; source SHA256 '+hashlib.sha256(original).hexdigest()
        entries[key]=dict(source=key,path='packs/'+sha,byteSize=len(data),sha256=sha,contentType='application/octet-stream',provenance=provenance,dependencies=dependencies[key])
    for sha,data in data_by_hash.items():
        (args.output/'packs'/sha).write_bytes(data);(args.output/'packs'/(sha+'.gz')).write_bytes(gzip.compress(data,9,mtime=0))
    manifest=dict(version=2,assets=list(entries.values()));(args.output/'join-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    files={a['sha256']:a for a in entries.values()}
    raw=sum(a['byteSize'] for a in files.values()); transfer=sum(len(gzip.compress((args.output/a['path']).read_bytes(),9,mtime=0)) for a in files.values())
    runtime=[p for p in (args.output/'_framework').rglob('*') if p.is_file() and p.suffix not in ('.gz','.br')]
    runtime_transfer=sum(len(gzip.compress(p.read_bytes(),9,mtime=0)) for p in runtime)
    receipt=dict(recordedCompanionModels=companion_models,entrySector=entry_sector,deferredBackgrounds=sorted(set(deferred)),recordedOpens=[len(t['Opens']) for t in traces],firstSectors=[t['FirstSector'] for t in traces],files=len(entries),rawBytes=raw,gzipBytes=transfer,runtimeGzipBytes=runtime_transfer,totalTransferBytes=transfer+runtime_transfer,budgetBytes=20*1024*1024,fallbacks=fallbacks,assets=manifest['assets'])
    args.receipt.write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({k:v for k,v in receipt.items() if k not in ['assets','firstSectors']},indent=2))
    if raw>64*1024*1024: raise ValueError('Resident pack exceeds 64 MiB')
    if transfer+runtime_transfer>20*1024*1024: raise ValueError('Join + runtime exceeds 20 MiB transfer')
if __name__=='__main__':main()
