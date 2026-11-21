#!/usr/bin/env python3
"""Build content-addressed, original-byte hull/missile/effect visual closures.
The resident join and P1054 sector files are never changed. Core IDs are read
from the same fixed native records as DataHullTypeIGC/DataMissileTypeIGC. No
network or optional packages. Run before publish finalization/provenance.
"""
import argparse
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct

_spec = importlib.util.spec_from_file_location('flight_packs', Path(__file__).with_name('p1051-browser-packs.py'))
flight = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(flight)


def core_roots(data):
    """Version+size native container, six-byte record headers; fail closed."""
    if len(data) < 8 or struct.unpack_from('<i', data, 4)[0] != len(data) - 8:
        raise ValueError('Expected exact versioned static core')
    position = 8
    roots = []
    while position < len(data):
        if position + 6 > len(data):
            raise ValueError('Truncated core record')
        kind, size = struct.unpack_from('<hi', data, position)
        position += 6
        if size < 0 or position + size > len(data):
            raise ValueError('Truncated core payload')
        record = data[position:position + size]
        position += size
        def text(offset, count):
            return record[offset:offset + count].split(b'\0')[0].decode('ascii').strip().lower()
        if kind == 29:
            if size < 540:
                raise ValueError('Truncated hull')
            roots.append(dict(key='hull-' + str(struct.unpack_from('<h', record, 446)[0]), model=text(8, 14), texture=text(496, 13)))
        elif kind == 23:
            if size != 524:
                raise ValueError('Invalid missile record')
            roots.append(dict(key='missile-' + str(struct.unpack_from('<h', record, 446)[0]), model=text(24, 13), texture=text(37, 13)))
    return roots


def build(root, artwork):
    join_path = root / 'join-manifest.json'
    join_bytes = join_path.read_bytes()
    resident = {a['source'].lower(): a for a in json.loads(join_bytes)['assets']}
    sources = {}
    for path in artwork.rglob('*'):
        if path.is_file():
            name = path.relative_to(artwork).as_posix().lower()
            if name in sources and sources[name].read_bytes() != path.read_bytes():
                raise ValueError('Case-colliding artwork: ' + name)
            sources[name] = path
    roots = core_roots(sources['pcore001.igc'].read_bytes())
    roots.extend(dict(key='effect-' + stem, model=stem, texture='') for stem in ('exp22bmp', 'fx18bmp'))
    entries, packs, missing = {}, [], []

    def original(name):
        if name in resident:
            return resident[name]
        if name in entries:
            return entries[name]
        data = sources[name].read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        dependencies = []
        for stem in flight.strings(data):
            candidate = stem.lower() + '.mdl'
            if candidate in sources:
                dependencies.append(candidate)
            elif candidate.endswith('bbmp.mdl') and candidate[:-8] + 'bmp.mdl' in sources:
                dependencies.append(candidate[:-8] + 'bmp.mdl')
        if name.endswith('.mdl') and not data.startswith(b'\x00\xdf\xba\xde'):
            dependencies += [n.lower() for n in re.findall(r'ImportImageFromFile\s*\(\s*"([^"]+)"', data.decode('latin1')) if n.lower() in sources]
        if any(not d.endswith(('.mdl', '.png')) for d in dependencies):
            raise ValueError('Nonvisual dependency: ' + name)
        asset = dict(source=name, path='packs/' + digest, byteSize=len(data), sha256=digest,
                     # The lobby serves every extensionless content-addressed pack as application/octet-stream
                     # (BrowserPreviewHosting.cs) and the stager requires that exact type, so PNGs are no exception.
                     contentType='application/octet-stream',
                     provenance='FreeAllegiance Artwork; MIT (2017 FreeAllegiance); original core-selected battle visual bytes',
                     dependencies=sorted(set(dependencies)))
        entries[name] = asset
        (root / asset['path']).write_bytes(data)
        (root / (asset['path'] + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
        return asset

    for row in roots:
        model = row['model'] + '.mdl'
        if model not in sources:
            missing.append(row)
            continue
        pending = [model]
        texture = row['texture']
        if texture and texture[0].isalpha() and texture != row['model'] and texture + '.mdl' in sources:
            pending.append(texture + '.mdl')
        closure = set()
        while pending:
            name = pending.pop()
            if name in closure:
                continue
            closure.add(name)
            asset = original(name)
            pending.extend(asset['dependencies'])
        packs.append(dict(key=row['key'], sources=sorted(closure)))
    assets = sorted(entries.values(), key=lambda a: a['source'])
    manifest = dict(version=1, assets=assets, packs=packs)
    (root / 'battle-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    unique = {a['sha256']: a for a in assets}
    def sizes(selected):
        items = {a['sha256']: a for a in selected}.values()
        return dict(rawBytes=sum(a['byteSize'] for a in items), gzipBytes=sum((root / (a['path'] + '.gz')).stat().st_size for a in items))
    receipt = dict(version=1, coreSha256=hashlib.sha256(sources['pcore001.igc'].read_bytes()).hexdigest(),
                   joinUnchanged=join_path.read_bytes() == join_bytes,
                   join=sizes(resident.values()), onDemand=dict(files=len(assets), uniqueFiles=len(unique), **sizes(assets)),
                   packs=[dict(key=p['key'], **sizes([entries[n] for n in p['sources'] if n in entries])) for p in packs],
                   hullModels=len({r['model'] for r in roots if r['key'].startswith('hull-')}),
                   coveredHullModels=len({r['model'] for r in roots if r['key'].startswith('hull-') and r not in missing}),
                   missing=missing,
                   effects='Original bytes only. exp22bmp is 15360 pixels wide: task B must tile it before GPU upload. No effect renderer is added here.')
    (root / 'battle-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--artwork', type=Path, required=True)
    args = parser.parse_args()
    receipt = build(args.root, args.artwork)
    print(json.dumps({k: v for k, v in receipt.items() if k != 'packs'}, indent=2))
