import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile
from PIL import Image, PngImagePlugin

spec = importlib.util.spec_from_file_location('build_pack', Path(__file__).with_name('build_pack.py'))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class PackBuilderTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='hd3-builder-')
        self.root = Path(self.tmp.name)
        self.source, self.art, self.output = [self.root / n for n in ('input', 'art', 'pack')]
        self.art.mkdir(); (self.source / 'atlas').mkdir(parents=True)
        (self.art / 'Atlas.mdl').write_bytes(b'native atlas')
        self.catalog = {'models': [{'kind': 'ship', 'model': 'multi', 'stems': ['atlas', 'missing']}]}
        for name in builder.MAP_SPACES:
            Image.new('RGB', (4, 4), (128, 128, 255)).save(self.source / 'atlas' / (name + '.png'))

    def tearDown(self):
        self.tmp.cleanup()

    def build(self, **kw):
        return builder.build(self.source, self.art, self.output, self.catalog, **kw)

    def test_release_manifest_coverage_hashes_and_reproducible_zip(self):
        coverage = self.build()
        self.assertEqual(['missing'], coverage['missingStems'])
        manifest = json.loads((self.output / 'manifest.json').read_text())
        self.assertEqual(2, manifest['schema'])
        self.assertEqual(builder.sha(self.art / 'Atlas.mdl'), manifest['textures']['atlas']['native']['TextureSha256'])
        self.assertEqual('linear', manifest['textures']['atlas']['maps']['normal']['colorSpace'])
        with zipfile.ZipFile(self.output.with_suffix('.zip')) as z:
            self.assertEqual(7, len(z.namelist()))
            self.assertEqual((self.output / 'manifest.json').read_bytes(), z.read('manifest.json'))
        builder.build(self.source, self.art, self.root / 'pack2', self.catalog)
        self.assertEqual(self.output.with_suffix('.zip').read_bytes(), (self.root / 'pack2.zip').read_bytes())

    def test_cli_with_explicit_model_folder_mapping_and_source_hash(self):
        (self.root / 'catalog.json').write_text(json.dumps(self.catalog))
        (self.root / 'mapping.json').write_text(json.dumps({'atlas': 'ATLAS'}))
        (self.root / 'hashes.json').write_text(json.dumps({'atlas': builder.sha(self.art / 'Atlas.mdl')}))
        result = subprocess.run([sys.executable, str(Path(builder.__file__)), '--input', str(self.source),
                                 '--artwork', str(self.art), '--output', str(self.output), '--catalog', str(self.root / 'catalog.json'),
                                 '--mapping', str(self.root / 'mapping.json'), '--expected-hashes', str(self.root / 'hashes.json')], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, json.loads(result.stdout)['covered'])

    def test_missing_corrupt_or_wrong_sized_map_leaves_no_output(self):
        path = self.source / 'atlas/normal.png'
        for contents in (None, b'corrupt'):
            if contents is None: path.unlink()
            else: path.write_bytes(contents)
            with self.assertRaises((OSError, ValueError)): self.build()
            self.assertFalse(self.output.exists())
        Image.new('RGB', (2, 4)).save(path)
        with self.assertRaisesRegex(ValueError, 'dimensions'): self.build()

    def test_half_resolution_scalar_maps_supported(self):
        for name in ('metallic', 'roughness'):
            Image.new('L', (2, 2)).save(self.source / 'atlas' / (name + '.png'))
        self.build()

    def test_linear_maps_reject_srgb_tags_and_wrong_declared_spaces(self):
        meta = PngImagePlugin.PngInfo(); meta.add(b'sRGB', b'\x00')
        Image.new('RGB', (4, 4)).save(self.source / 'atlas/normal.png', pnginfo=meta)
        with self.assertRaisesRegex(ValueError, 'nonlinear'): self.build()
        Image.new('RGB', (4, 4)).save(self.source / 'atlas/normal.png')
        (self.source / 'atlas/maps.json').write_text(json.dumps({'colorSpaces': {'normal': 'sRGB'}}))
        with self.assertRaisesRegex(ValueError, 'colour spaces'): self.build()

    def test_stale_artwork_hash_rejected(self):
        with self.assertRaisesRegex(ValueError, 'SHA256'): self.build(expected_hashes={'atlas': '0' * 64})
        (self.source / 'atlas/maps.json').write_text(json.dumps({'nativeSha256': '0' * 64}))
        with self.assertRaisesRegex(ValueError, 'stale source'): self.build()

    def test_unsafe_or_duplicate_mapping_and_overwrite_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unsafe'): self.build(mapping={'../escape': 'atlas'})
        with self.assertRaisesRegex(ValueError, 'duplicate'): self.build(mapping={'atlas': 'atlas', 'other': 'Atlas'})
        self.build()
        with self.assertRaisesRegex(ValueError, 'already exists'): self.build()

    def test_class_policy_uses_largest_shared_atlas_class(self):
        catalog = {'models': [
            {'kind': 'ship', 'model': 'fighter', 'stems': ['ATLAS']},
            {'kind': 'ship', 'model': 'capital', 'capital': True, 'stems': ['atlas']},
            {'kind': 'probe', 'model': 'probe', 'stems': ['probe']},
            {'kind': 'station', 'model': 'base', 'stems': ['base']}]}
        for stem, expected in [('atlas', 'capital'), ('probe', 'projectile'), ('base', 'station'), ('unknown', 'small')]:
            self.assertEqual(expected, builder.bc_maps.atlas_class(stem, catalog, builder.bc_maps.DEFAULT_POLICY))
        with self.assertRaisesRegex(ValueError, 'policy'):
            self.build(format='bc', policy={'small': 2049})

    def test_bc_release_has_complete_dds_mips_fallback_and_reproducible_zip(self):
        import struct
        coverage = self.build(format='bc')
        manifest = json.loads((self.output / 'manifest.json').read_text())
        self.assertEqual(3, manifest['schema'])
        for name, expected in [('basecolor', 99), ('normal', 83), ('metallic', 80), ('roughness', 80), ('emission', 99)]:
            path = self.output / 'textures/atlas' / (name + '.dds')
            data = path.read_bytes()
            self.assertEqual(b'DDS ', data[:4])
            self.assertEqual(expected, struct.unpack_from('<I', data, 128)[0])
            self.assertEqual(3, struct.unpack_from('<I', data, 28)[0])
            self.assertEqual(148 + (24 if expected == 80 else 48), len(data))
            self.assertEqual(builder.sha(path), manifest['textures']['atlas']['maps'][name]['sha256'])
        self.assertEqual(192, coverage['sizeReport']['maxResidentBytes'])
        self.assertTrue((self.output / 'textures/atlas/basecolor.fallback.dds').exists())
        with zipfile.ZipFile(self.output.with_suffix('.zip')) as z:
            self.assertFalse(any('fallback' in n for n in z.namelist()))
        with zipfile.ZipFile(self.output.with_suffix('.bc3.zip')) as z:
            self.assertEqual(7, len(z.namelist()))
            self.assertEqual(78, struct.unpack_from('<I', z.read('textures/atlas/basecolor.dds'), 128)[0])
            alternate = json.loads(z.read('manifest.json'))
            self.assertEqual('BC3', alternate['textures']['atlas']['maps']['basecolor']['format'])
            import hashlib
            self.assertEqual(hashlib.sha256(z.read('textures/atlas/basecolor.dds')).hexdigest(), alternate['textures']['atlas']['maps']['basecolor']['sha256'])
        builder.build(self.source, self.art, self.root / 'pack2', self.catalog, format='bc')
        self.assertEqual(self.output.with_suffix('.zip').read_bytes(), (self.root / 'pack2.zip').read_bytes())
        self.assertEqual(self.output.with_suffix('.bc3.zip').read_bytes(), (self.root / 'pack2.bc3.zip').read_bytes())

    def test_bc_data_channels_and_linear_light_mips(self):
        import numpy as np
        import bc_maps
        source = np.array([[[0., 0., 0., 1.], [1., 1., 1., 1.]]], dtype=np.float32)
        averaged = bc_maps.samples(bc_maps.resize(source, (1, 1)), 'basecolor')
        self.assertTrue(186 <= int(averaged[0, 0, 0]) <= 189)
        # Deliberately distinct channels detect passing RGBA to the codec's
        # tightly packed R8/RG8 inputs (which would mix neighboring channels).
        Image.new('RGB', (4, 4), (64, 192, 220)).save(self.source / 'atlas/normal.png')
        Image.new('RGB', (4, 4), (31, 170, 240)).save(self.source / 'atlas/metallic.png')
        self.build(format='bc')
        metal = (self.output / 'textures/atlas/metallic.dds').read_bytes()[148:156]
        self.assertEqual((31, 31), tuple(metal[:2]))
        normal = (self.output / 'textures/atlas/normal.dds').read_bytes()[148:164]
        self.assertLess(normal[0], 128)
        self.assertGreater(normal[8], 128)
        self.assertGreater(normal[8] - normal[0], 100)


if __name__ == '__main__': unittest.main()
