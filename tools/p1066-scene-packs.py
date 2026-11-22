#!/usr/bin/env python3
"""Add the default desktop B1 scene artwork as verified on-demand closures.
Never alters the join/sector manifests. Original bytes, no network/packages.
"""
import argparse
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct


def provenance(name):
    if name.startswith('p589obj/'):
        metadata = json.loads((Path(__file__).resolve().parents[1] / 'docs/reports/P589_assets/sky-objects.json').read_text())
        row = next((r for r in metadata if r['slug'] == Path(name).stem), None)
        if row is None:
            raise ValueError('Missing scene artwork attribution: ' + name)
        return row['credit'] + '; ' + row['licence'] + '; cropped/feathered desktop sprite; scene-NOTICE.txt'
    if name.startswith('p589gal/'):
        return 'NASA/ESA Hubble Ultra Deep Field; CC BY 4.0; cropped/feathered desktop sprites; full credits and sources: scene-NOTICE.txt'
    if name == 'giant-objects.json':
        return 'Existing desktop giant-sky placement configuration; AllegianceCore MIT; credits: scene-NOTICE.txt'
    if name.startswith('giantobj/'):
        return 'NASA/JPL-Caltech/R. Hurt (SSC/Caltech); public domain US Government work; processed desktop sprite; scene-NOTICE.txt'
    return 'FreeAllegiance Artwork; MIT (2017 FreeAllegiance); original desktop scene bytes'


def map_asteroid_models(paths):
    """P1073 — every asteroid model/texture a STATIC map record can place.

    Map files carry an ``OT_asteroid`` (5) record per placed rock
    (``DataAsteroidIGC``: modelName @74, textureName @87, 13 bytes each;
    src/Allegiance.Core/Data/DataAsteroidIGC.cs). The generated-map table is
    ``NativeAsteroidTypes``; this catches the authored maps that reuse hull/station
    models as rocks (bgrnd57, ss307, cap101, ...). Parsing is read-only and fails
    closed on malformed files.
    """
    models = {}
    for path in paths.values():
        if path.suffix.lower() != '.igc':
            continue
        data = path.read_bytes()
        if len(data) < 8:
            continue
        start = 8 if struct.unpack_from('<i', data, 4)[0] == len(data) - 8 else (
            4 if struct.unpack_from('<I', data, 0)[0] == len(data) - 4 else None)
        if start is None:
            continue
        position = start
        while position + 6 <= len(data):
            kind, size = struct.unpack_from('<hi', data, position)
            position += 6
            if size < 0 or position + size > len(data):
                break
            record = data[position:position + size]
            position += size
            if kind != 5 or size != 144:
                continue
            model = record[74:87].split(b'\0')[0].decode('ascii').strip().lower()
            texture = record[87:100].split(b'\0')[0].decode('ascii').strip().lower()
            if model:
                models.setdefault(model, set()).add(texture)
    return models


def notices(root):
    repo = Path(__file__).resolve().parents[1]
    parts = ['B1 scene artwork. Native Allegiance art: MIT (2017 FreeAllegiance); see artwork-NOTICE.txt.\n'
             'The following processed astronomical paintings retain their original licenses.\n'
             'Changes from the source photographs: object extraction, star removal, contrast, feathered alpha and resizing.\n']
    for filename in ['P589_assets/sky-objects.json', 'P589_assets/galaxy-library.json', 'P592_assets/giant-objects-sources.json']:
        data = json.loads((repo / 'docs/reports' / filename).read_text())
        for row in data if isinstance(data, list) else [data]:
            parts.append('\n' + '\n'.join(str(row[key]) for key in ('subject', 'source', 'source_url', 'licence', 'credit') if key in row))
    for path in sorted((repo / 'docs/reports/P589_assets/licenses').glob('*.txt')):
        parts.append('\n\n' + path.name + '\n' + path.read_text())
    (root / 'scene-NOTICE.txt').write_text('\n'.join(parts) + '\n')


def build(root, artwork):
    join = (root / 'join-manifest.json').read_bytes()
    resident = {a['source']: a for a in json.loads(join)['assets']}
    index = json.loads((root / 'battle-manifest.json').read_text())
    entries = {a['source']: a for a in index['assets']}
    paths = {}
    for p in artwork.rglob('*'):
        if p.is_file():
            name = p.relative_to(artwork).as_posix().lower()
            if name in paths and paths[name].read_bytes() != p.read_bytes():
                raise ValueError('Case-colliding scene artwork: ' + name)
            paths[name] = p
    native = ['moonbmp.mdl', 'moon7bmp.mdl', 'moonnebbmp.mdl', 'f104bmp.mdl',
              'f105bmp.mdl', 'f106bmp.mdl', 'f101bmp.mdl', 'debris1bmp.mdl', 'e_telebmp.mdl']
    # P1069 B2 — the afterburner fire/smoke art the desktop exhaust renderer
    # decodes (e_firebmp.mdl / e_smokebmp.mdl scripts import e_fire/e_smoke.png).
    # The nozzle glow already uses f101bmp.mdl from scene-b1.
    engines = ['e_smokebmp.mdl', 'e_firebmp.mdl']
    paintings = sorted(n for n in paths if n.startswith(('p589obj/', 'p589gal/')) and n.endswith('.png'))
    giant_sectors = []
    giants = []
    if 'giant-objects.json' in paths:
        config = json.loads(paths['giant-objects.json'].read_text())
        giants = ['giant-objects.json']
        giant_sectors = sorted({r['cluster_id'] for r in config.get('objects', [])})
    spec = importlib.util.spec_from_file_location('flight_packs', Path(__file__).with_name('p1051-browser-packs.py'))
    flight = importlib.util.module_from_spec(spec); spec.loader.exec_module(flight)

    def closure(roots):
        pending = roots[:]; selected = set()
        while pending:
            name = pending.pop()
            if name in selected:
                continue
            if name not in paths:
                raise ValueError('Missing desktop scene artwork: ' + name)
            selected.add(name)
            if name in resident or name in entries:
                asset = resident.get(name) or entries[name]
                # P1073 — a resident file from the P1054 split may carry its deferred
                # visual dependencies in `visualDependencies` (e.g. a model whose
                # texture was streamed by a sector pack). The closure must follow
                # them, or an on-demand scene pack would ship the mesh without its
                # surface (the white-asteroid failure mode).
                pending.extend(asset['dependencies'])
                pending.extend(asset.get('visualDependencies') or [])
                continue
            data = paths[name].read_bytes()
            dependencies = []
            if name.endswith('.mdl'):
                dependencies += [v.lower() + '.mdl' for v in flight.strings(data) if v.lower() + '.mdl' in paths]
                if not data.startswith(b'\x00\xdf\xba\xde'):
                    dependencies += [v.lower() for v in re.findall(r'(?:ImportImageFromFile|ImageFromFile)\s*\(\s*"([^"]+)"', data.decode('latin1'))]
            if name == 'giant-objects.json':
                dependencies += ['giantobj/' + r['image'].lower() for r in json.loads(data).get('objects', [])]
            digest = hashlib.sha256(data).hexdigest()
            asset = dict(source=name, path='packs/' + digest, sha256=digest, byteSize=len(data),
                         # The lobby serves every extensionless content-addressed pack as application/octet-stream
                         # (BrowserPreviewHosting.cs) and the stager requires that exact type, so PNGs are no exception.
                         contentType='application/octet-stream',
                         provenance=provenance(name), dependencies=sorted(set(dependencies)))
            entries[name] = asset; pending.extend(asset['dependencies'])
            (root / asset['path']).write_bytes(data)
            (root / (asset['path'] + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
        if len(selected) > 1024:
            raise ValueError('Scene closure exceeds manifest bound')
        return selected

    # P1073 — generated-map artwork and the native hangar launch animation. The
    # planet/globe/rock families are per-family on-demand packs so a generated
    # sector fetches only the art it actually uses; the join manifest is untouched.
    # The names mirror Allegiance.Core.MapGen.GeneratedMapArt (checked by
    # tools/p1073-pack-coverage-tests.py).
    mapgen_planets = ['nebplnt36bmp', 'nebplnt35bmp', 'nebplnt02bmp', 'nebplnt23bmp',
                      'nebplnt19bmp', 'nebplnt01bmp', 'nebplnt03bmp', 'nebplnt04bmp',
                      'nebplnt05bmp', 'nebplnt06bmp', 'nebplnt07bmp', 'nebplnt08bmp',
                      'nebplnt09bmp', 'nebplnt10bmp', 'nebplnt11bmp', 'nebplnt12bmp',
                      'nebplnt16bmp', 'nebplnt24bmp', 'nebplnt25bmp', 'nebplnt29bmp',
                      'nebplnt30bmp', 'nebplnt31bmp', 'nebplnt33bmp', 'nebplnt37bmp']
    mapgen_globes = ['globe' + str(n) for n in range(1, 9)]
    mapgen_warp_textures = ['plnt19bmp', 'plnt42bmp', 'alephbmp']
    mapgen_rocks = ['bgrnd03', 'bgrnd05', 'bgrnd51', 'bgrnd52', 'bgrnd53', 'bgrnd55', 'bgrnd56']
    mapgen = [dict(key='mapgen-planet-' + name, sources=sorted(closure([name + '.mdl'])))
              for name in mapgen_planets if name + '.mdl' in paths]
    mapgen += [dict(key='mapgen-globe-' + name.replace('globe', ''), sources=sorted(closure(
                   [name + '.mdl', name.replace('globe', 'environment') + 'bmp.mdl'])))
               for name in mapgen_globes if name + '.mdl' in paths]
    mapgen += [dict(key='mapgen-warp-' + name, sources=sorted(closure([name + '.mdl'])))
               for name in mapgen_warp_textures if name + '.mdl' in paths]
    mapgen += [dict(key='mapgen-rock-' + name, sources=sorted(closure([name + '.mdl'])))
               for name in mapgen_rocks if name + '.mdl' in paths]
    hangar = [dict(key='scene-hangar', sources=sorted(closure(['animlaunch.mdl', 'animlaunchbmp.mdl'])))]
    # P1073 — static maps' authored asteroid records (the generated-map table is
    # NativeAsteroidTypes; maps may reuse any model, e.g. bgrnd57/ss307/cap101).
    static_rocks = []
    for model, textures in sorted(map_asteroid_models(paths).items()):
        roots = [model + '.mdl'] + sorted(t + '.mdl' for t in textures if t and t != model and t + '.mdl' in paths)
        if model + '.mdl' not in paths:
            continue
        static_rocks.append(dict(key='static-rock-' + model, sources=sorted(closure(roots))))
    scene_packs = [dict(key=key, sources=sorted(closure(roots)))
                   for key, roots in [('scene-b1', native), ('scene-engines', engines),
                                      ('scene-paintings', paintings),
                                      ('scene-build', ['build.mdl']), ('scene-giants', giants)] if roots]
    scene_packs += mapgen + hangar + static_rocks
    all_sources = {n for pack in scene_packs for n in pack['sources']}
    index['assets'] = sorted(entries.values(), key=lambda a: a['source'])
    index['packs'] = [p for p in index['packs'] if not p['key'].startswith('scene-')] + scene_packs
    index['sceneGiantSectors'] = giant_sectors
    (root / 'battle-manifest.json').write_text(json.dumps(index, indent=2) + '\n')

    def sizes(names):
        unique = {entries[n]['sha256']: entries[n] for n in names if n not in resident}.values()
        return dict(rawBytes=sum(a['byteSize'] for a in unique),
                    gzipBytes=sum((root / (a['path'] + '.gz')).stat().st_size for a in unique))
    extra = [entries[n] for n in sorted(all_sources) if n not in resident]
    receipt = dict(version=1, joinUnchanged=(root / 'join-manifest.json').read_bytes() == join,
                   sources=len(all_sources), additionalFiles=len(extra), additionalUniqueFiles=len({a['sha256'] for a in extra}),
                   **sizes(all_sources),
                   packs=[dict(key=p['key'], sources=len(p['sources']), **sizes(p['sources'])) for p in scene_packs],
                   sceneGiantSectors=giant_sectors,
                   originalFiles=[dict(source=a['source'], sha256=a['sha256'], byteSize=a['byteSize']) for a in extra])
    notices(root)
    (root / 'scene-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--artwork', required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps({k: v for k, v in build(args.root, args.artwork).items() if k != 'originalFiles'}, indent=2))
