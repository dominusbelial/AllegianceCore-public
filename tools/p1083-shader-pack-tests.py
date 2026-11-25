#!/usr/bin/env python3
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('shader_pack', Path(__file__).with_name('p1083-shader-pack.py'))
pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack)
EFFECT = Path(__file__).resolve().parents[1] / 'src/Allegiance.ClientCore.Game.Browser/Content/Shaders/Volumetric.xnb'


class ShaderPackTests(unittest.TestCase):
    def test_all_nine_exact_effects_have_independent_verified_packs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root / 'packs').mkdir()
            (root / 'join-manifest.json').write_text('{"version":2,"assets":[]}')
            (root / 'battle-manifest.json').write_text('{"version":1,"assets":[],"packs":[]}')
            for name in pack.NAMES:
                receipt = pack.build(root, EFFECT.with_name(name + '.xnb'), name)
                self.assertTrue(receipt['joinUnchanged'])
                self.assertFalse(receipt['defaultRequested'])
            manifest = json.loads((root / 'battle-manifest.json').read_text())
            self.assertEqual(len(manifest['assets']), 9)
            self.assertEqual({p['key'] for p in manifest['packs']}, {'shader-' + n.lower() for n in pack.NAMES})
            for asset in manifest['assets']:
                self.assertEqual(hashlib.sha256((root / asset['path']).read_bytes()).hexdigest(), asset['sha256'])
            before = (root / 'battle-manifest.json').read_bytes()
            with self.assertRaisesRegex(ValueError, 'Unknown shader'):
                pack.build(root, EFFECT, '../other')
            self.assertEqual((root / 'battle-manifest.json').read_bytes(), before)

    def test_shader_is_verified_deferred_and_join_unchanged(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root / 'packs').mkdir()
            join = b'{"version":2,"assets":[]}'
            (root / 'join-manifest.json').write_bytes(join)
            (root / 'battle-manifest.json').write_text('{"version":1,"assets":[],"packs":[]}')
            receipt = pack.build(root, EFFECT)
            self.assertTrue(receipt['joinUnchanged'])
            self.assertFalse(receipt['defaultRequested'])
            manifest = json.loads((root / 'battle-manifest.json').read_text())
            self.assertEqual(manifest['packs'], [dict(key='shader-volumetric', sources=[pack.SOURCE])])
            asset = manifest['assets'][0]
            self.assertEqual(asset['sha256'], hashlib.sha256(EFFECT.read_bytes()).hexdigest())
            self.assertEqual((root / asset['path']).read_bytes(), EFFECT.read_bytes())
            self.assertEqual((root / 'join-manifest.json').read_bytes(), join)
            self.assertEqual(receipt, pack.build(root, EFFECT))

    def test_corrupt_build_receipt_rejected_before_manifest_mutation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); effect = root / 'Volumetric.xnb'
            effect.write_bytes(EFFECT.read_bytes())
            effect.with_suffix('.xnb.json').write_text(json.dumps(dict(outputSha256='0'*64, outputBytes=effect.stat().st_size)))
            with self.assertRaisesRegex(ValueError, 'receipt'):
                pack.build(root, effect)
            self.assertFalse((root / 'battle-manifest.json').exists())

    def test_desktop_bytecode_is_not_accepted_as_a_blazor_xnb(self):
        with tempfile.TemporaryDirectory() as temp:
            effect = Path(temp) / 'Volumetric.xnb'; effect.write_bytes(b'MGFX\x0b')
            with self.assertRaisesRegex(ValueError, 'BlazorGL XNB'):
                pack.build(Path(temp), effect)


if __name__ == '__main__':
    unittest.main()
