import argparse
import hashlib
import importlib.util
import json
import sys
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

    def installer_args(self, **overrides):
        values = dict(rid='linux-x64', format='all', version='1.2.3',
                      from_portable=None, appimagetool='appimagetool',
                      appimage_runtime=Path(__file__), iscc='iscc')
        return argparse.Namespace(**(values | overrides))

    def test_appdir_retains_payload_and_launches_intro_with_writable_user_cwd(self):
        with tempfile.TemporaryDirectory(prefix='app image space ') as temp:
            root = Path(temp)
            bundle = root / 'Allegiance-linux-x64'
            bundle.mkdir()
            (bundle / 'Allegiance').write_text('#!/bin/sh\npwd\nprintf "%s\\n" "$@"\n')
            (bundle / 'Allegiance').chmod(0o755)
            (bundle / 'bundle-files.sha256').write_text('same portable inventory')
            (bundle / 'artwork-manifest.txt').write_text('same manifest')
            appdir = root / 'game.AppDir'
            package.app_dir(bundle, appdir)
            payload = appdir / 'usr/lib/AllegianceCore'
            for name in ('bundle-files.sha256', 'artwork-manifest.txt'):
                self.assertEqual((bundle / name).read_bytes(), (payload / name).read_bytes())
            config = root / 'user config'
            env = dict(os.environ, XDG_CONFIG_HOME=str(config))
            exe = appdir / 'AppRun'
            # No arguments reaches the app with no synthetic direct-connect args.
            output = subprocess.check_output([str(exe)], cwd=root, env=env, text=True)
            self.assertEqual(str(config / 'AllegianceCore'), output.strip())
            output = subprocess.check_output([str(exe), '--check-assets', 'pilot with spaces'],
                                             cwd=root, env=env, text=True).splitlines()
            self.assertEqual([str(config / 'AllegianceCore'), '--check-assets', 'pilot with spaces'], output)
            player = config / 'AllegianceCore/player.json'
            player.write_text('{"Pilot":"remembered"}')
            subprocess.check_call([str(exe)], cwd=root, env=env, stdout=subprocess.DEVNULL)
            self.assertEqual('{"Pilot":"remembered"}', player.read_text())
            self.assertFalse((payload / 'player.json').exists())
            self.assertEqual('allegiance.svg', os.readlink(appdir / '.DirIcon'))
            self.assertIn('Exec=AppRun', (appdir / 'allegiance.desktop').read_text())

    def test_installer_compilers_receive_same_bundle_and_explicit_runtime(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            bundle = root / 'bundle'
            bundle.mkdir()
            for name in ('LICENSE', 'NOTICE', 'CREDITS.md', 'README-PLAYERS.txt'):
                (bundle / name).write_text('contents of ' + name)
            artifact = root / 'game.AppImage'
            def compile_image(*args, **kwargs):
                artifact.touch()
            with patch.object(package, 'run', side_effect=compile_image) as run:
                package.installer(bundle, artifact, self.installer_args())
                args, kwargs = run.call_args
                self.assertEqual('--runtime-file', args[1])
                self.assertEqual(Path(__file__).resolve(), args[2])
                self.assertEqual('1', kwargs['env']['APPIMAGE_EXTRACT_AND_RUN'])
                self.assertTrue(os.access(artifact, os.X_OK))
            with patch.object(package, 'run') as run:
                package.installer(bundle, root / 'game-setup.exe', self.installer_args(rid='win-x64'))
                self.assertIn('/DBundleDir=' + str(bundle), run.call_args.args)
                self.assertIn('/DAppVersion=1.2.3', run.call_args.args)
                page = (root / 'installer-license.txt').read_text(encoding='utf-8-sig')
                for name in ('LICENSE', 'NOTICE', 'CREDITS.md', 'README-PLAYERS.txt'):
                    self.assertIn('contents of ' + name, page)

    def test_format_selection_and_macos_app_zip(self):
        for rid in ('linux-x64', 'win-x64', 'osx-arm64'):
            for fmt in ('portable', 'installer', 'all'):
                with self.subTest(rid=rid, format=fmt), tempfile.TemporaryDirectory() as temp:
                    root = Path(temp)
                    bundle = root / ('Allegiance-' + rid)
                    bundle.mkdir()
                    with patch.object(package, 'archive_bundle') as archive, \
                         patch.object(package, 'installer') as installer, \
                         patch.object(package, 'record_artifact'):
                        package.emit_artifacts(bundle, root, root, self.installer_args(rid=rid, format=fmt), 3, {})
                        self.assertEqual(fmt != 'installer' or rid == 'osx-arm64', archive.called)
                        self.assertEqual(fmt != 'portable' and rid != 'osx-arm64', installer.called)

    def test_every_artifact_has_checksum_metadata_and_enforces_limit(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / 'output'
            output.mkdir()
            for name in ('game.zip', 'game.AppImage', 'game-setup.exe'):
                pending = root / name
                pending.write_bytes(b'payload')
                with patch.object(package, 'LIMIT', 7):
                    with self.assertRaisesRegex(ValueError, '2 GiB'):
                        package.record_artifact(pending, output, self.installer_args(), 99, 3, {})
                self.assertFalse((output / name).exists())
                package.record_artifact(pending, output, self.installer_args(), 99, 3, {})
                self.assertEqual(hashlib.sha256(b'payload').hexdigest() + '  ' + name + '\n',
                                 (output / (name + '.sha256')).read_text())
                metadata = json.loads((output / (name + '.json')).read_text())
                self.assertEqual(7, metadata['archiveBytes'])
                self.assertEqual('1.2.3', metadata['version'])

    def test_invalid_version_and_missing_runtime_fail_before_publish(self):
        for version in ('v1.2.3', '1.2.3-beta', '1.2', '1.2.3/escape', '1.2.65536'):
            with self.subTest(version=version), self.assertRaisesRegex(ValueError, 'version'):
                package.validate_args(self.installer_args(version=version))
        with self.assertRaisesRegex(ValueError, 'runtime'):
            package.validate_args(self.installer_args(appimage_runtime=None))
        with self.assertRaisesRegex(ValueError, 'from-portable'):
            package.validate_args(self.installer_args(from_portable=Path('game.zip')))

    def test_repackaging_rejects_changed_portable_archive_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / 'player.zip'
            archive.write_bytes(b'changed')
            (root / 'player.zip.sha256').write_text('0' * 64 + '  player.zip\n')
            with patch.dict(sys.modules, smoke=smoke), self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                package.from_portable(self.installer_args(from_portable=archive, format='installer'), root)

    def test_repackaging_verifies_payload_before_calling_installer(self):
        for corrupt in (False, True):
            with self.subTest(corrupt=corrupt), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                bundle = root / 'Allegiance-win-x64'
                bundle.mkdir()
                for name in package.required_content():
                    path = bundle / 'Content' / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.touch()
                (bundle / 'artwork-manifest.txt').write_text('asset.mdl\n')
                (bundle / 'sources.json').write_text('{}')
                (bundle / 'asset.mdl').write_bytes(b'art')
                digest = '0' * 64 if corrupt else package.checksum(bundle / 'asset.mdl')
                (bundle / 'bundle-files.sha256').write_text(digest + '  asset.mdl\n')
                archive = root / 'portable.zip'
                package.archive_bundle(bundle, archive, 'win-x64')
                (root / 'portable.zip.sha256').write_text(package.checksum(archive) + '  portable.zip\n')
                args = self.installer_args(rid='win-x64', from_portable=archive, format='installer')
                with patch.dict(sys.modules, smoke=smoke), patch.object(package, 'emit_artifacts') as emit:
                    if corrupt:
                        with self.assertRaisesRegex(ValueError, 'Payload checksum mismatch'):
                            package.from_portable(args, root)
                        emit.assert_not_called()
                    else:
                        package.from_portable(args, root)
                        self.assertEqual(1, emit.call_count)
                        self.assertEqual(1, emit.call_args.args[4])



if __name__ == '__main__':
    unittest.main()
