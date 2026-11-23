#!/usr/bin/env python3
"""P1073 — every station type in the core and every generated-map art stem the
generator can emit must resolve to a verified pack entry. Fixture roots are small
and synthetic; the artwork/core corpus is read-only and the test skips without it.
"""
import importlib.util
import json
import os
import shutil
import struct
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).with_name
def load(name, file):
    spec = importlib.util.spec_from_file_location(name, HERE(file))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module
battle = load('battle_packs', 'p1065-battle-packs.py')
scene = load('scene_packs', 'p1066-scene-packs.py')

ARTWORK = Path(os.environ.get('ALLEGIANCE_ARTWORK_PATH', '')) if os.environ.get('ALLEGIANCE_ARTWORK_PATH') else None
ARTWORK = ARTWORK if ARTWORK and ARTWORK.is_dir() else None


def mdl(words=()):
    data = struct.pack('<6I', 0xDEBADF00, 1, len(words), 0, 0, 1)
    for word in words:
        data += word.encode() + b'\0' * (4 - (len(word) + 1) % 4)
    return data


PLANETS = ['nebplnt36bmp', 'nebplnt35bmp', 'nebplnt02bmp', 'nebplnt23bmp', 'nebplnt19bmp',
           'nebplnt01bmp', 'nebplnt03bmp', 'nebplnt04bmp', 'nebplnt05bmp', 'nebplnt06bmp',
           'nebplnt07bmp', 'nebplnt08bmp', 'nebplnt09bmp', 'nebplnt10bmp', 'nebplnt11bmp',
           'nebplnt12bmp', 'nebplnt16bmp', 'nebplnt24bmp', 'nebplnt25bmp', 'nebplnt29bmp',
           'nebplnt30bmp', 'nebplnt31bmp', 'nebplnt33bmp', 'nebplnt37bmp']
GLOBES = [f'globe{n}' for n in range(1, 9)]
WARPS = ['plnt19bmp', 'plnt42bmp', 'alephbmp']
ROCKS = ['bgrnd03', 'bgrnd05', 'bgrnd51', 'bgrnd52', 'bgrnd53', 'bgrnd55', 'bgrnd56']


@unittest.skipUnless(ARTWORK, 'ALLEGIANCE_ARTWORK_PATH is not set')
class GeneratedMapPackCoverage(unittest.TestCase):
    def test_every_generated_map_stem_has_a_pack_entry(self):
        with tempfile.TemporaryDirectory() as directory:
            root, art = Path(directory) / 'root', Path(directory) / 'art'
            (root / 'packs').mkdir(parents=True); art.mkdir()
            native = ['moonbmp', 'moon7bmp', 'moonnebbmp', 'f104bmp', 'f105bmp', 'f106bmp',
                      'f101bmp', 'debris1bmp', 'e_telebmp', 'e_smokebmp', 'e_firebmp', 'build',
                      'exp20bmp', 'exp22bmp', 'exp23bmp', 'exp24bmp', 'exp25bmp', 'fx18bmp',
                      'fx09bmp', 'fx11bmp', 'f103bmp', 'utl10', 'fxminebmp', 'cc_cmtexbmp']
            (art / 'lens30.x').write_text('fixture cap')
            for name in PLANETS + GLOBES + WARPS + ROCKS + [r + 'bmp' for r in ROCKS] + native + ['animlaunch', 'animlaunchbmp'] + [g.replace('globe', 'environment') + 'bmp' for g in GLOBES]:
                (art / (name + '.mdl')).write_bytes(mdl([name] if name.startswith(('globe', 'bgrnd')) else []))
            # P1054 may leave a resident model whose surface was deferred to a
            # sector pack (visualDependencies). The scene closure must follow it.
            resident = dict(source='bgrnd51.mdl', path='packs/' + '0' * 64, byteSize=1, sha256='0' * 64,
                            contentType='application/octet-stream', provenance='fixture',
                            dependencies=[], visualDependencies=['bgrnd51bmp.mdl'])
            # Static-map asteroid records: every authored model/texture found in the
            # real maps must also have a static-rock-* pack. The fixture map carries
            # one OT_asteroid (5) record per real model name.
            real_paths = {p.relative_to(ARTWORK).as_posix().lower(): p for p in ARTWORK.rglob('*') if p.is_file()}
            static_models = scene.map_asteroid_models(real_paths)
            self.assertGreater(len(static_models), 0)
            records = bytearray()
            for model, textures in sorted(static_models.items()):
                for texture in sorted(textures):
                    self.assertIn(model + '.mdl', real_paths)
                    if texture:
                        self.assertIn(texture + '.mdl', real_paths)
                    record = bytearray(144)
                    record[74:74 + len(model)] = model.encode()
                    record[87:87 + len(texture)] = texture.encode()
                    records += struct.pack('<hi', 5, 144) + record
            for name in sorted({n for m, ts in static_models.items() for n in [m] + sorted(ts) if n}):
                (art / (name + '.mdl')).write_bytes(mdl())
            (art / 'fixture-map.igc').write_bytes(struct.pack('<I', len(records)) + records)
            (root / 'join-manifest.json').write_text(json.dumps({'version': 2, 'assets': [resident]}))
            (root / 'battle-manifest.json').write_text(json.dumps({'version': 1, 'assets': [], 'packs': []}))
            scene.build(root, art)
            manifest = json.loads((root / 'battle-manifest.json').read_text())
            keys = {p['key']: p for p in manifest['packs']}
            sources = {a['source'] for a in manifest['assets']} | {'bgrnd51.mdl'}  # resident join fixture
            expected = ([f'mapgen-planet-{n}' for n in PLANETS] + [f'mapgen-globe-{n.replace("globe", "")}' for n in GLOBES]
                        + [f'mapgen-warp-{n}' for n in WARPS] + [f'mapgen-rock-{n}' for n in ROCKS] + ['scene-hangar'])
            for key in expected:
                self.assertIn(key, keys, f'{key} pack missing')
                for source in keys[key]['sources']:
                    self.assertIn(source, sources)
                    if source == 'bgrnd51.mdl' and not any(a['source'] == source for a in manifest['assets']):
                        continue   # resident join fixture, not a newly packed file
                    self.assertTrue((root / 'packs' / next(a['sha256'] for a in manifest['assets'] if a['source'] == source)).exists())
            for name in ['animlaunch.mdl', 'animlaunchbmp.mdl']:
                self.assertIn(name, keys['scene-hangar']['sources'])
            self.assertIn('bgrnd51bmp.mdl', keys['mapgen-rock-bgrnd51']['sources'])
            for model in static_models:
                self.assertIn('static-rock-' + model, keys)

    def test_the_real_artwork_has_every_generated_map_file(self):
        names = {p.name.lower() for p in ARTWORK.iterdir()} | {
            p.relative_to(ARTWORK).as_posix().lower() for p in ARTWORK.rglob('*') if p.is_file()}
        for stem in PLANETS + GLOBES + WARPS + ROCKS + [g.replace('globe', 'environment') + 'bmp' for g in GLOBES] + ['animlaunch', 'animlaunchbmp']:
            self.assertIn(stem.lower() + '.mdl', names, f'{stem}.mdl missing from artwork')
        for rock in ROCKS:
            self.assertIn(rock + 'bmp.mdl', names, f'{rock} native surface missing from artwork')


@unittest.skipUnless(ARTWORK, 'ALLEGIANCE_ARTWORK_PATH is not set')
class StationPackCoverage(unittest.TestCase):
    def test_every_core_station_type_resolves_to_a_pack(self):
        core = next(p for p in ARTWORK.iterdir() if p.name.lower() == 'pcore001.igc')
        data = core.read_bytes()
        rows = [r for r in battle.core_roots(data) if r['key'].startswith('station-')]
        self.assertGreater(len(rows), 0, 'core has no OT_stationType records')
        # Every civilization's InitialStationTypeId (DataCivilizationIGC @264,
        # src/Allegiance.Core/Data/DataCivilizationIGC.cs) must resolve to a pack.
        position, civs = 8, []
        while position < len(data):
            kind, size = struct.unpack_from('<hi', data, position); position += 6
            if kind == 27:
                civs.append(struct.unpack_from('<h', data, position + 264)[0])
            position += size
        self.assertGreater(len(civs), 0, 'core has no civilizations')
        station_ids = {int(r['key'].split('-')[1]) for r in rows}
        self.assertEqual([], [c for c in civs if c not in station_ids])
        self.assertEqual(len(rows), len({r['key'] for r in rows}), 'duplicate station pack key')
        files = {p.relative_to(ARTWORK).as_posix().lower() for p in ARTWORK.rglob('*') if p.is_file()}
        missing = []
        for row in rows:
            for stem in (row['model'], row['texture']):
                if stem and stem + '.mdl' not in files:
                    missing.append((row['key'], stem))
        self.assertEqual([], missing)
        with tempfile.TemporaryDirectory() as directory:
            root, art = Path(directory) / 'root', Path(directory) / 'art'
            (root / 'packs').mkdir(parents=True); art.mkdir()
            wanted = sorted({r['model'] + '.mdl' for r in rows} | {r['texture'] + '.mdl' for r in rows if r['texture']})
            by_name = {p.relative_to(ARTWORK).as_posix().lower(): p for p in ARTWORK.rglob('*') if p.is_file()}
            for name in wanted:
                shutil.copyfile(by_name[name], art / name)
            shutil.copyfile(core, art / 'pcore001.igc')
            (root / 'join-manifest.json').write_text(json.dumps({'version': 2, 'assets': []}))
            receipt = battle.build(root, art)
            manifest = json.loads((root / 'battle-manifest.json').read_text())
            keys = {p['key']: p for p in manifest['packs']}
            for row in rows:
                self.assertIn(row['key'], keys)
                self.assertIn(row['model'] + '.mdl', keys[row['key']]['sources'])
                if row['texture']:
                    self.assertIn(row['texture'] + '.mdl', keys[row['key']]['sources'])
            self.assertEqual(len(rows), receipt['stationTypes'])
            self.assertEqual(len(rows), receipt['coveredStationTypes'])


if __name__ == '__main__':
    unittest.main()
