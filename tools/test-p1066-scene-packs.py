#!/usr/bin/env python3
"""B1 publication originals, closure, provenance and unchanged join regressions."""
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value)
    return value


packs = module('scene_packs', 'p1066-scene-packs.py')
provenance = module('scene_provenance', 'p1061-browser-provenance.py')


class ScenePacksTests(unittest.TestCase):
    def test_native_and_processed_sources_retain_their_specific_licenses(self):
        self.assertIn('MIT', packs.provenance('f104bmp.mdl'))
        self.assertIn('CC BY 4.0', packs.provenance('p589obj/helix-nebula.png'))
        nasa = packs.provenance('p589obj/lion-nebula-nasa.png')
        self.assertIn('Public Domain', nasa)
        self.assertNotIn('CC BY', nasa)
        self.assertIn('CC BY 4.0', packs.provenance('p589gal/000.png'))
        with self.assertRaisesRegex(ValueError, 'Missing scene artwork attribution'):
            packs.provenance('p589obj/uncredited.png')

    def test_verified_original_closure_join_unchanged_and_hashed_notice_attribution(self):
        with tempfile.TemporaryDirectory(prefix='p1066-packs-') as directory:
            root, art = Path(directory) / 'generation', Path(directory) / 'art'
            (root / 'packs').mkdir(parents=True); art.mkdir()
            for name in ['moonbmp', 'moon7bmp', 'moonnebbmp', 'f104bmp', 'f105bmp', 'f106bmp', 'f101bmp', 'debris1bmp', 'build']:
                (art / (name + '.mdl')).write_text('fixture ' + name)
            (art / 'e_telebmp.mdl').write_text('use "model"; ImageFromFile("e_tele.png", false);')
            (art / 'e_tele.png').write_bytes(b'fixture original teleport')
            (art / 'p589obj').mkdir(); (art / 'p589obj/helix-nebula.png').write_bytes(b'fixture processed painting')
            (art / 'giantobj').mkdir(); (art / 'giantobj/milkyway_terra.png').write_bytes(b'fixture processed giant')
            (art / 'giant-objects.json').write_text('{"objects":[{"image":"milkyway_terra.png","cluster_id":1057}]}')
            join = b'{"version":2,"assets":[]}'
            (root / 'join-manifest.json').write_bytes(join)
            (root / 'battle-manifest.json').write_text('{"version":1,"assets":[],"packs":[]}')
            receipt = packs.build(root, art)
            self.assertTrue(receipt['joinUnchanged'])
            self.assertEqual(join, (root / 'join-manifest.json').read_bytes())
            index = json.loads((root / 'battle-manifest.json').read_text())
            native = next(p for p in index['packs'] if p['key'] == 'scene-b1')
            names = {n for p in index['packs'] for n in p['sources']}
            self.assertNotIn('build.mdl', native['sources'])
            self.assertFalse(any(n.startswith('p589') for n in native['sources']))
            self.assertEqual([1057], index['sceneGiantSectors'])
            self.assertIn('e_tele.png', names); self.assertIn('giant-objects.json', names)
            for a in index['assets']:
                data = (root / a['path']).read_bytes()
                self.assertEqual(data, (art / a['source']).read_bytes())
                self.assertEqual(hashlib.sha256(data).hexdigest(), a['sha256'])
                self.assertEqual(data, gzip.decompress((root / (a['path'] + '.gz')).read_bytes()))
            painting = next(a for a in index['assets'] if a['source'].startswith('p589obj/'))
            self.assertIn('CC BY 4.0', painting['provenance'])
            # Publisher fingerprints notices before emitting final provenance.
            hashed = 'scene-NOTICE.012345.txt'
            (root / 'scene-NOTICE.txt').rename(root / hashed)
            (root / 'app-manifest.json').write_text(json.dumps({'files': {'scene-NOTICE.txt': hashed}}))
            self.assertEqual(hashed, provenance.notice_path(root, 'scene-NOTICE.txt'))
            saved = provenance.load(root)
            self.assertTrue(any(hashed in a['provenance'] for p in saved for a in p['assets']))
            self.assertIn('NASA', (root / hashed).read_text())

    def test_missing_required_scene_art_fails_closed(self):
        with tempfile.TemporaryDirectory(prefix='p1066-missing-') as directory:
            root, art = Path(directory) / 'generation', Path(directory) / 'art'
            (root / 'packs').mkdir(parents=True); art.mkdir()
            (root / 'join-manifest.json').write_text('{"version":2,"assets":[]}')
            (root / 'battle-manifest.json').write_text('{"version":1,"assets":[],"packs":[]}')
            with self.assertRaisesRegex(ValueError, 'Missing desktop scene artwork'):
                packs.build(root, art)


if __name__ == '__main__':
    unittest.main()
