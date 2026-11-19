#!/usr/bin/env python3
"""Split the verified P1051 join into a gameplay base and recorded visual packs.
Only recorded RGB565 surfaces and sky namespaces may be deferred. Entry rocks
retain their P1051 256-square first-frame copy; every streamed file is original.
"""
import argparse
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import re

_spec = importlib.util.spec_from_file_location('flight_packs', Path(__file__).with_name('p1051-browser-packs.py'))
flight = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(flight)


def visual(name):
    return bool(re.fullmatch(r'(?:bgrnd[0-9]+bmp|environment[0-9]+bmp|nebplnt[0-9]+(?:bmp)?)\.(?:mdl|png)', name))


def split(root, traces, artwork):
    if {t['style'] for t in traces} != {'Classic', 'Modern'}:
        raise ValueError('Both sector recordings required')
    if any(t.get('map') != traces[0].get('map') or t.get('seed') != traces[0].get('seed') for t in traces):
        raise ValueError('Map/seed mismatch')
    sectors = {s['ClusterId']: s for t in traces for s in t['Sectors']}
    for t in traces:
        if {v['SectorId'] for v in t['Visits'] if v['Seconds'] >= 30} != sectors.keys():
            raise ValueError('Incomplete sector recording')
    entry = traces[0]['FirstSector']['ClusterId']
    if any(t['FirstSector']['ClusterId'] != entry for t in traces):
        raise ValueError('Entry sector mismatch')
    sources = {p.relative_to(artwork).as_posix().lower(): p for p in artwork.rglob('*') if p.is_file()}
    observed = {o['Source'].lower(): o for t in traces for o in t['Opens'] if o['Origin'] == 'artwork'}
    base = json.loads((root / 'join-manifest.json').read_text())
    entries = {a['source']: a for a in base['assets']}
    originals, closures = {}, {}

    def original(name):
        if name in originals:
            return originals[name]
        data = sources[name].read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        witness = observed.get(name)
        if witness is None or witness['ByteSize'] != len(data) or witness['Sha256'] != digest:
            raise ValueError('Unverified desktop dependency: ' + name)
        deps = [n.lower() + '.mdl' for n in flight.strings(data) if n.lower() + '.mdl' in sources]
        if name.endswith('.mdl') and not data.startswith(b'\x00\xdf\xba\xde'):
            deps += [n.lower() for n in re.findall(r'ImportImageFromFile\s*\(\s*"([^"]+)"', data.decode('latin1')) if n.lower() in sources]
        asset = dict(source=name, path='packs/' + digest, byteSize=len(data), sha256=digest,
                     contentType='image/png' if name.endswith('.png') else 'application/octet-stream',
                     provenance='FreeAllegiance Artwork; MIT (2017 FreeAllegiance); original recorded bytes', dependencies=deps)
        originals[name] = asset
        (root / asset['path']).write_bytes(data)
        (root / (asset['path'] + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
        return asset

    for sector in sorted(sectors):
        pending = [n.lower() for t in traces for n in t['SectorRequired'][str(sector)]]
        closure = set()
        while pending:
            name = pending.pop()
            if name in closure or name not in sources:
                continue
            closure.add(name)
            pending.extend(original(name)['dependencies'])
        closures[sector] = closure
    # All geometry/model namespaces, collision and gameplay data remain in base.
    # The allow-list is intentionally narrower than "all textures".
    for closure in closures.values():
        for name in closure:
            if not visual(name):
                entries[name] = originals[name]
    entry_visuals = {n for n in closures[entry] if visual(n)}
    for name in list(entries):
        if visual(name) and name not in entry_visuals:
            del entries[name]
    for name in entry_visuals:
        source = original(name)
        if re.fullmatch(r'bgrnd[0-9]+bmp\.mdl', name):
            # Exactly the tested P1051 transform, used only for first-frame entry.
            data, before, after = flight.cap_surface(sources[name].read_bytes())
            digest = hashlib.sha256(data).hexdigest()
            entries[name] = dict(source, path='packs/' + digest, byteSize=len(data), sha256=digest,
                                 provenance=source['provenance'] + '; entry 256-square first-frame copy')
            (root / entries[name]['path']).write_bytes(data)
            (root / (entries[name]['path'] + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
        else:
            entries[name] = source
    for asset in entries.values():
        deferred = [d for d in asset['dependencies'] if d not in entries]
        if any(not visual(d) for d in deferred):
            raise ValueError('Gameplay dependency missing from base')
        if deferred:
            asset['visualDependencies'] = deferred
            asset['dependencies'] = [d for d in asset['dependencies'] if d in entries]
    base = dict(version=2, assets=sorted(entries.values(), key=lambda a: a['source']))
    (root / 'join-manifest.json').write_text(json.dumps(base, indent=2) + '\n')
    packs = []
    for sector, closure in closures.items():
        assets = [original(n) for n in sorted(closure) if visual(n) and (n not in entries or originals[n]['sha256'] != entries[n]['sha256'])]
        # Dependencies may refer to already resident geometry or other base files.
        available = set(entries) | {a['source'] for a in assets}
        if any(d not in available for a in assets for d in a['dependencies']):
            raise ValueError('Sector closure has a missing dependency')
        packs.append(dict(sectorId=sector, name=sectors[sector]['Name'], assets=assets))
    index = dict(version=1, map=traces[0]['map'], seed=traces[0]['seed'], entrySectorId=entry, sectors=packs)
    (root / 'sector-manifest.json').write_text(json.dumps(index, indent=2) + '\n')
    # A generation contains only referenced immutable files, not discarded caps.
    starter = json.loads((root / 'starter-manifest.json').read_text())
    retained = {a['sha256'] for a in base['assets'] + starter['assets'] + [a for pack in packs for a in pack['assets']]}
    for path in (root / 'packs').iterdir():
        if path.name.split('.')[0] not in retained:
            path.unlink()

    def sizes(assets):
        unique = {a['sha256']: a for a in assets}
        return dict(files=len(assets), uniqueFiles=len(unique), rawBytes=sum(a['byteSize'] for a in unique.values()),
                    gzipBytes=sum((root / (a['path'] + '.gz')).stat().st_size for a in unique.values()))
    runtime = [p for p in (root / '_framework').rglob('*') if p.is_file() and p.suffix not in ('.br', '.gz')]
    runtime_gzip = sum(len(gzip.compress(p.read_bytes(), 9, mtime=0)) for p in runtime)
    receipt = dict(map=index['map'], seed=index['seed'], entrySectorId=entry, base=sizes(base['assets']),
                   runtimeGzipBytes=runtime_gzip, budgetBytes=14 * 1024 * 1024,
                   totalTransferBytes=sizes(base['assets'])['gzipBytes'] + runtime_gzip,
                   entryFirstFrameCopies=[a['source'] for a in entries.values() if 'entry 256-square' in a['provenance']],
                   sectors=[dict(sectorId=p['sectorId'], name=p['name'], **sizes(p['assets'])) for p in packs],
                   originalFiles=[dict(source=n, sha256=a['sha256'], byteSize=a['byteSize'], byteIdentical=True) for n, a in sorted(originals.items()) if visual(n)],
                   deferralRule='Only recorded bgrnd*bmp RGB565 surfaces, planet/nebula textures and sky namespaces; every other file stays in base.')
    (root / 'sector-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    # Remove obsolete fallback receipt rows: the only capped copies are the ruling's entry copies.
    old_receipt = root / 'join-receipt.json'
    if old_receipt.exists():
        old_receipt.write_text(json.dumps(dict(version=2, selection='P1054 gameplay base; see sector-receipt.json', assets=base['assets'], **sizes(base['assets'])), indent=2) + '\n')
    if receipt['totalTransferBytes'] > receipt['budgetBytes']:
        raise ValueError('Base + runtime exceeds 14 MiB')
    if receipt['base']['rawBytes'] > 64 * 1024 * 1024:
        raise ValueError('Base exceeds residency bound')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--traces', type=Path, nargs=2, required=True)
    parser.add_argument('--artwork', type=Path, required=True)
    args = parser.parse_args()
    receipt = split(args.root, [json.loads(p.read_text()) for p in args.traces], args.artwork)
    print(json.dumps({k: v for k, v in receipt.items() if k != 'originalFiles'}, indent=2))
