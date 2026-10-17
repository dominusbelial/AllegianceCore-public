import importlib.util
from pathlib import Path
import tempfile
import os
import plistlib
import struct
import subprocess
import zipfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('player_package', Path(__file__).with_name('package.py'))
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)

spec = importlib.util.spec_from_file_location('player_smoke', Path(__file__).with_name('smoke.py'))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class PackagingTests(unittest.TestCase):
    def test_manifest_rejects_unsafe_duplicate_and_empty_entries(self):
        with tempfile.TemporaryDirectory() as temp:
            manifest = Path(temp) / 'manifest.txt'
            for text in ('../escape', '/absolute', 'C:/windows', 'folder\\file', 'x\nx', '# only comment'):
                with self.subTest(text=text):
                    manifest.write_text(text)
                    with self.assertRaises(ValueError):
                        package.paths(manifest)
            manifest.write_text('# comment\n\nCaseSensitive.mdl\nfolder/asset.png\n')
            self.assertEqual(['CaseSensitive.mdl', 'folder/asset.png'], package.paths(manifest))

    def test_copy_never_includes_untracked_or_missing_assets(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'local.png').write_text('private')
            for name, tracked in [('local.png', set()), ('missing.png', {'missing.png'})]:
                with self.assertRaises(ValueError):
                    package.copy(root, name, root / 'bundle', tracked)
            package.copy(root, 'local.png', root / 'bundle', {'local.png'})
            self.assertEqual('private', (root / 'bundle/local.png').read_text())

    def test_local_override_must_match_pin(self):
        with patch.object(package.subprocess, 'check_output', return_value='wrong\n'):
            with self.assertRaisesRegex(ValueError, 'must be at'):
                package.source('artwork', Path('/tmp'), Path('/tmp/cache'),
                               {'artwork': {'commit': 'expected'}})

    def test_missing_runtime_sound_prevents_packaging(self):
        with tempfile.TemporaryDirectory() as temp:
            bundle = Path(temp)
            for name in package.required_content():
                path = bundle / 'Content' / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.touch()
            package.validate_content(bundle)
            (bundle / 'Content/Sounds/payday.xnb').unlink()
            with self.assertRaisesRegex(ValueError, 'Content/Sounds/payday.xnb'):
                package.validate_content(bundle)

    def mac_fixture(self, root):
        bundle = root / 'Allegiance-osx-arm64'
        bundle.mkdir()
        # A real executable stand-in lets us test cwd and argument forwarding on Linux.
        (bundle / 'Allegiance').write_text('#!/bin/sh\npwd\nprintf "%s\\n" "$@"\n')
        (bundle / 'README-PLAYERS.txt').write_text('players')
        for folder in ('artwork', 'sky', 'Content'):
            (bundle / folder).mkdir()
            (bundle / folder / 'asset').write_text(folder)
        package.mac_app(bundle)
        return bundle

    def test_mac_app_launcher_runs_from_resources_and_forwards_arguments(self):
        with tempfile.TemporaryDirectory(prefix='player space ') as temp:
            root = Path(temp)
            bundle = self.mac_fixture(root)
            exe = smoke.executable(bundle)
            resources = bundle / 'AllegianceCore.app/Contents/Resources'
            output = subprocess.check_output([str(exe), '--check-assets', 'pilot with spaces'],
                                             cwd=root, text=True).splitlines()
            self.assertEqual([str(resources), '--check-assets', 'pilot with spaces'], output)
            self.assertEqual('players', (bundle / 'README-PLAYERS.txt').read_text())
            with (bundle / 'AllegianceCore.app/Contents/Info.plist').open('rb') as stream:
                plist = plistlib.load(stream)
            self.assertEqual('org.allegiancecore.client', plist['CFBundleIdentifier'])
            self.assertTrue(plist['NSHighResolutionCapable'])

    def test_mac_zip_preserves_executable_bits_resources_and_icon(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            bundle = self.mac_fixture(root)
            archive = root / 'player.zip'
            package.archive_bundle(bundle, archive, 'osx-arm64')
            with zipfile.ZipFile(archive) as z:
                for name in ('MacOS/Allegiance', 'Resources/Allegiance'):
                    info = z.getinfo(bundle.name + '/AllegianceCore.app/Contents/' + name)
                    self.assertEqual(3, info.create_system)
                    self.assertEqual(0o755, info.external_attr >> 16 & 0o777)
            extracted = smoke.extract(archive, root / 'unpacked')
            self.assertTrue(os.access(smoke.executable(extracted), os.X_OK))
            resources = extracted / 'AllegianceCore.app/Contents/Resources'
            self.assertEqual('artwork', (resources / 'artwork/asset').read_text())
            icon = (resources / 'allegiance.icns').read_bytes()
            self.assertEqual(len(icon), struct.unpack('>I', icon[4:8])[0])
            self.assertIn(b'ic10', icon)

    def test_mac_smoke_rejects_lost_executable_permission(self):
        with tempfile.TemporaryDirectory() as temp:
            bundle = self.mac_fixture(Path(temp))
            (bundle / 'AllegianceCore.app/Contents/Resources/Allegiance').chmod(0o644)
            with self.assertRaisesRegex(ValueError, 'executable bit'):
                smoke.executable(bundle)

    def test_smoke_detects_windows_and_linux_by_layout_not_archive_suffix(self):
        with tempfile.TemporaryDirectory() as temp:
            bundle = Path(temp)
            self.assertEqual(bundle / 'Allegiance', smoke.executable(bundle))
            (bundle / 'Allegiance.exe').touch()
            self.assertEqual(bundle / 'Allegiance.exe', smoke.executable(bundle))



if __name__ == '__main__':
    unittest.main()
