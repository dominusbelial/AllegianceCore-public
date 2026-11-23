#!/usr/bin/env python3
"""P1079 original combat closure coverage, fail-closed records and zero join growth.
No network or additional packages. Set ALLEGIANCE_ARTWORK_PATH for corpus checks.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


battle = load('battle', 'p1065-battle-packs.py')
scene = load('scene', 'p1066-scene-packs.py')
artwork = Path(os.environ['ALLEGIANCE_ARTWORK_PATH']) if os.environ.get('ALLEGIANCE_ARTWORK_PATH') else None


def container(kind, record):
    records = struct.pack('<hi', kind, len(record)) + record
    return struct.pack('<ii', 1, len(records)) + records


class CombatPackRecords(unittest.TestCase):
    def test_probe_and_launcher_treasure_use_native_expendable_offsets(self):
        record = bytearray(492)
        struct.pack_into('<h', record, 446, 72)
        record[24:29] = b'utl02'; record[72:77] = b'utl03'
        self.assertEqual(battle.core_roots(container(25, record)), [
            dict(key='probe-72', model='utl02', texture=''),
            dict(key='treasure-model-utl03', model='utl03', texture='')])
        with self.assertRaisesRegex(ValueError, 'Invalid probe'):
            battle.core_roots(container(25, record[:-1]))

    def test_part_and_development_models_are_covered_without_compact_launcher_confusion(self):
        for kind, size in [(30, 424), (32, 468)]:
            record = bytearray(size); record[8:13] = b'acs14'
            self.assertEqual(battle.core_roots(container(kind, record)), [dict(key='treasure-model-acs14', model='acs14', texture='')])
        self.assertEqual(battle.core_roots(container(30, bytearray(24))), [])
        with self.assertRaises(ValueError):
            battle.core_roots(container(25, bytearray(492))[:-1])


@unittest.skipUnless(artwork and artwork.is_dir(), 'original artwork unavailable')
class OriginalCombatClosure(unittest.TestCase):
    def test_verified_combat_roots_are_original_bytes_and_never_change_join(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); (root / 'packs').mkdir()
            join = b'{"version":2,"assets":[]}'
            (root / 'join-manifest.json').write_bytes(join)
            receipt = battle.build(root, artwork)
            scene_receipt = scene.build(root, artwork)
            self.assertTrue(receipt['joinUnchanged']); self.assertTrue(scene_receipt['joinUnchanged'])
            self.assertEqual((root / 'join-manifest.json').read_bytes(), join)
            manifest = json.loads((root / 'battle-manifest.json').read_text())
            packs = {p['key']: p['sources'] for p in manifest['packs']}
            assets = {a['source']: a for a in manifest['assets']}
            combat = packs['scene-combat']
            for source in ['lens30.x', 'utl10.mdl', 'fx09bmp.mdl', 'fx11bmp.mdl', 'f103bmp.mdl',
                           'fxminebmp.mdl', 'exp20bmp.mdl', 'exp22bmp.mdl', 'exp23bmp.mdl',
                           'exp24bmp.mdl', 'exp25bmp.mdl', 'fx18bmp.mdl', 'e_fire.png', 'e_smoke.png']:
                self.assertIn(source, combat)
            self.assertIn('hull-33', packs); self.assertIn('dreghpod.mdl', packs['hull-33'])
            self.assertIn('utl02.mdl', packs['probe-72']); self.assertIn('utl02bmp.mdl', packs['probe-72'])
            for source in combat:
                asset = assets[source]
                self.assertEqual(hashlib.sha256((root / asset['path']).read_bytes()).hexdigest(), asset['sha256'])
                self.assertEqual(asset['contentType'], 'application/octet-stream')
                for dependency in asset['dependencies']:
                    self.assertIn(dependency, combat)
            # Every available core-selected treasure/probe model has a closure;
            # unavailable original assets retain the desktop gem/stand-in fallback.
            names = {p.relative_to(artwork).as_posix().lower() for p in artwork.rglob('*') if p.is_file()}
            core = next(p for p in artwork.iterdir() if p.name.lower() == 'pcore001.igc')
            for row in battle.core_roots(core.read_bytes()):
                if row['key'].startswith(('probe-', 'treasure-model-')) and row['model'] + '.mdl' in names:
                    self.assertIn(row['key'], packs)
                    self.assertIn(row['model'] + '.mdl', packs[row['key']])


if __name__ == '__main__':
    unittest.main()
