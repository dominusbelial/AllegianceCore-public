#!/usr/bin/env python3
"""Offline publish regressions; no runtime/package/host required."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('publish', Path(__file__).with_name('p1053-browser-publish.py'))
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)


class PublishTests(unittest.TestCase):
    def fixture(self, root):
        (root / '_framework').mkdir()
        for name in ['blazor.webassembly.js', 'dotnet.js', 'dotnet.runtime.abcdefghij.js', 'dotnet.native.abcdefghij.wasm', 'icudt_EFIGS.abcdefghij.dat']:
            (root / '_framework' / name).write_bytes(b'boot ' + name.encode())
        for name in ['preview.js', 'game-platform.js', 'menu.css', 'font-NOTICE.txt']:
            (root / name).write_bytes(b'content ' + name.encode())
        menu = []
        (root / 'packs').mkdir()
        for name in ['content/hud.xnb', 'content/hudlarge.xnb', 'content/sounds/mousedown.xnb']:
            data = name.encode(); digest = hashlib.sha256(data).hexdigest()
            (root / 'packs' / digest).write_bytes(data)
            menu.append({'source':name,'path':'packs/'+digest,'sha256':digest,'byteSize':len(data)})
        (root / 'starter-manifest.json').write_text(json.dumps({'version':2,'assets':menu}))
        (root / 'join-manifest.json').write_text(json.dumps({'version':2,'assets':menu}))
        (root / 'brotli').mkdir()
        (root / 'brotli/module.wasm').write_bytes(b'wasm')
        (root / 'brotli/modules.json').write_text('{"modules":["module.wasm"]}')
        (root / 'menu.html').write_text('<head><link href="menu.css"></head><script src="preview.js"></script>')
        (root / 'menu.html.gz').write_bytes(b'stale')
        (root / 'preview.js.gz').write_bytes(b'original gzip')

    def test_content_names_map_html_and_boot_preloads(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); self.fixture(root); mapping = publish.finalize(root)
            for old, new in mapping.items():
                self.assertFalse((root / old).exists())
                self.assertIn(hashlib.sha256((root / new).read_bytes()).hexdigest()[:20], new)
            self.assertTrue((root / '_framework/dotnet.native.abcdefghij.wasm').exists())
            self.assertNotIn('_framework/dotnet.native.abcdefghij.wasm', mapping)
            self.assertNotIn('_framework/icudt_EFIGS.abcdefghij.dat', mapping)
            self.assertTrue((root / '_framework/icudt_EFIGS.abcdefghij.dat').exists())
            text = (root / 'menu.html').read_text()
            self.assertIn('data-assets=', text); self.assertIn(mapping['preview.js'], text)
            self.assertIn('<template id="boot-preloads">', text)  # gated before network capability fallback
            self.assertIn('rel="modulepreload"', text); self.assertIn('as="fetch"', text)
            self.assertFalse((root / 'menu.html.gz').exists())
            self.assertEqual((root / (mapping['preview.js'] + '.gz')).read_bytes(), b'original gzip')
            self.assertEqual(json.loads((root / 'app-manifest.json').read_text())['files'], mapping)
            self.assertTrue((root / 'starter-manifest.json').exists())
            self.assertEqual(json.loads((root / 'brotli/modules.json').read_text())['modules'], [Path(mapping['brotli/module.wasm']).name])

    def menu_fixture(self, root):
        self.fixture(root)
        names = ['content/hud.xnb', 'content/hudlarge.xnb', 'content/sounds/mousedown.xnb',
                 'content/fonts/nativeb8.xnb', 'content/hudmodern.xnb', 'pcore001.igc', 'runtime/brotli/module.wasm']
        assets = []
        (root / 'packs').mkdir(exist_ok=True)
        for index, name in enumerate(names):
            data = bytes([index + 1]); digest = hashlib.sha256(data).hexdigest()
            (root / 'packs' / digest).write_bytes(data)
            assets.append({'source': name, 'path': 'packs/' + digest, 'sha256': digest, 'byteSize': 1})
        (root / 'starter-manifest.json').write_text(json.dumps({'version': 2, 'assets': assets}))
        (root / 'starter-manifest.json.gz').write_bytes(b'stale full starter')
        (root / 'join-manifest.json').write_text(json.dumps({'version': 2, 'assets': assets}))
        (root / 'starter-receipt.json').write_text(json.dumps({'selection': 'recorded desktop opens', 'starter': {'files': len(assets)}}))
        return assets

    def test_first_menu_defers_only_byte_identical_join_assets(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); assets = self.menu_fixture(root); publish.finalize(root)
            starter = json.loads((root / 'starter-manifest.json').read_text())['assets']
            self.assertEqual(starter, assets[:3])
            self.assertEqual(json.loads((root / 'join-manifest.json').read_text())['assets'], assets)
            self.assertFalse((root / 'starter-manifest.json.gz').exists())
            receipt = json.loads((root / 'starter-receipt.json').read_text())
            self.assertEqual(receipt['starter']['files'], 3)
            self.assertEqual(receipt['recordedStarter']['files'], 7)
            self.assertEqual(receipt['deferredUntilJoin'], [a['source'] for a in assets[3:]])

    def test_deferred_assets_must_still_match_verified_join_entries(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); assets = self.menu_fixture(root)
            assets[-1]['sha256'] = '0' * 64
            (root / 'join-manifest.json').write_text(json.dumps({'version': 2, 'assets': assets}))
            with self.assertRaisesRegex(ValueError, 'missing or changed'):
                publish.finalize(root)

    def test_changed_bytes_produce_a_new_url(self):
        with tempfile.TemporaryDirectory() as left, tempfile.TemporaryDirectory() as right:
            a, b = Path(left), Path(right); self.fixture(a); self.fixture(b)
            (b / 'game-platform.js').write_bytes(b'new content')
            first, second = publish.finalize(a), publish.finalize(b)
            self.assertNotEqual(first['game-platform.js'], second['game-platform.js'])
            self.assertEqual(first['preview.js'], second['preview.js'])


if __name__ == '__main__':
    unittest.main()
