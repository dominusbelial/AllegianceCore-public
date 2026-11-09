import argparse
import datetime
import hashlib
import importlib.util
import json
import sys
from pathlib import Path
import tempfile
import os
import plistlib
import struct
import shutil
import time
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
    def test_join_desktop_install_escapes_exec_and_registers_per_user(self):
        with tempfile.TemporaryDirectory(prefix='join test ') as temp:
            root = Path(temp)
            bundle = root / 'space % dollar$ quote" backtick`'
            bundle.mkdir()
            for name in ('install-desktop.sh', 'allegiance.sh', 'allegiance.svg'):
                (bundle / name).write_bytes((package.REPO / 'packaging' / name).read_bytes())
            (bundle / 'allegiance.sh').chmod(0o755)
            # Stub xdg-mime so the test never changes the user's real associations.
            bin_dir = root / 'bin'
            bin_dir.mkdir()
            mime = bin_dir / 'xdg-mime'
            mime.write_text('#!/bin/sh\nprintf "%s\\n" "$@" > "$XDG_DATA_HOME/mime-call"\n')
            mime.chmod(0o755)
            data = root / 'data'
            env = dict(os.environ, XDG_DATA_HOME=str(data), PATH=str(bin_dir) + os.pathsep + os.environ['PATH'])
            subprocess.check_call(['bash', str(bundle / 'install-desktop.sh')], env=env, stdout=subprocess.DEVNULL)
            desktop = data / 'applications/allegiance.desktop'
            text = desktop.read_text()
            self.assertIn('MimeType=x-scheme-handler/allegiance;', text)
            exec_line = next(line for line in text.splitlines() if line.startswith('Exec='))
            self.assertTrue(exec_line.endswith('" %u'))
            self.assertIn('%%', exec_line)
            self.assertIn('\\\\$', exec_line)
            self.assertIn('\\\\`', exec_line)
            self.assertEqual(['default', 'allegiance.desktop', 'x-scheme-handler/allegiance'],
                             (data / 'mime-call').read_text().splitlines())
            # The real shell launcher preserves even hostile URL text as one argument.
            exe = bundle / 'Allegiance'
            exe.write_text('#!/bin/sh\nprintf "%s\\n" "$@"\n')
            exe.chmod(0o755)
            url = 'allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=one&v=1'
            config = root / 'config'
            (config / 'AllegianceCore').mkdir(parents=True)
            (config / 'AllegianceCore/player.json').write_text('{}')
            output = subprocess.check_output(['bash', str(bundle / 'allegiance.sh'), url],
                env=dict(env, XDG_CONFIG_HOME=str(config)), text=True).splitlines()
            self.assertEqual([url], output)
            if shutil.which('gio'):
                # Exercise the actual Desktop Entry parser too: it probes the
                # executable before %% expansion. Newer GLib (2.88) normalizes join? to
                # join/?, older GLib (2.80, Ubuntu 24.04 CI) passes it unchanged; the
                # client accepts both forms (JoinLink.TryParse).
                receipt = root / 'argv'
                exe.write_text('#!/bin/sh\nprintf "%s\\n" "$@" > "$P944_ARGV_RECEIPT"\n')
                subprocess.check_call(['gio', 'launch', str(desktop), url],
                    env=dict(env, XDG_CONFIG_HOME=str(config), P944_ARGV_RECEIPT=str(receipt)))
                for _ in range(30):
                    if receipt.exists():
                        break
                    time.sleep(0.1)
                self.assertIn(receipt.read_text().splitlines(), ([url], [url.replace('join?', 'join/?')]))

    def test_join_desktop_templates_and_windows_registry_are_well_formed(self):
        for name in ('allegiance.desktop', 'allegiance-appimage.desktop'):
            smoke.validate_join_registration(package.REPO / 'packaging')
            text = (package.REPO / 'packaging' / name).read_text()
            self.assertIn('MimeType=x-scheme-handler/allegiance;', text)
            self.assertTrue(next(line for line in text.splitlines() if line.startswith('Exec=')).endswith(' %u'))
        installer = (package.REPO / 'packaging/windows.iss').read_text()
        registry = installer.split('[Registry]')[1].split('[Run]')[0]
        self.assertEqual(3, registry.count('Root: HKCU;'))
        self.assertIn('ValueName: "URL Protocol"; ValueData: ""', registry)
        self.assertIn('Flags: uninsdeletekey', registry)
        self.assertIn('ValueData: """{app}\\Allegiance.exe"" ""%1"""', registry)
        batch = (package.REPO / 'packaging/play.bat').read_text()
        self.assertIn('"%~dp0Allegiance.exe" %*', batch)
        self.assertNotIn('powershell.exe', batch)
        self.assertIn("'Allegiance.exe') @args", (package.REPO / 'packaging/play.ps1').read_text())

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
            self.assertEqual(['allegiance'], plist['CFBundleURLTypes'][0]['CFBundleURLSchemes'])
            url = 'allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=one&v=1'
            output = subprocess.check_output([str(exe), url], cwd=root, text=True).splitlines()
            self.assertEqual([str(resources), url], output)

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
            self.assertIn('Exec=AppRun %u', (appdir / 'allegiance.desktop').read_text())
            smoke.validate_join_registration(appdir)
            url = 'allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=one&v=1'
            output = subprocess.check_output([str(exe), url], cwd=root, env=env, text=True).splitlines()
            self.assertEqual([str(config / 'AllegianceCore'), url], output)

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



    def test_bundle_writes_installed_version_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            bundle = root / 'Allegiance-linux-x64'
            bundle.mkdir()
            (bundle / 'Allegiance').write_text('#!/bin/sh\necho ok\n')
            (bundle / 'Allegiance').chmod(0o755)
            (bundle / 'Content').mkdir(parents=True)
            (bundle / 'Content' / 'HUD.xnb').write_text('xnb')
            package.write_installed_manifest(bundle, '0.1.6-test')
            installed = bundle / 'autoupdate' / 'installed.json'
            self.assertTrue(installed.is_file())
            manifest = json.loads(installed.read_text())
            self.assertEqual('0.1.6-test', manifest['version'])
            self.assertIn('generatedAtUtc', manifest)
            self.assertIn('files', manifest)
            paths = [entry['path'] for entry in manifest['files']]
            self.assertIn('Allegiance', paths)
            self.assertIn('Content/HUD.xnb', paths)
            self.assertNotIn('autoupdate/installed.json', paths)
            for entry in manifest['files']:
                self.assertEqual(package.checksum(bundle / entry['path']), entry['sha256'])
                self.assertIsInstance(entry['size'], int)
                self.assertRegex(entry['unixMode'], r'^[0-7]{3}$')
            allegiance = next(entry for entry in manifest['files'] if entry['path'] == 'Allegiance')
            self.assertEqual('755' if os.name != 'nt' else '644', allegiance['unixMode'])
            generated = manifest['generatedAtUtc'].replace('Z', '+00:00')
            self.assertIsNotNone(datetime.datetime.fromisoformat(generated).tzinfo)

    def test_installed_manifest_excludes_state_and_hd_packs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            bundle = root / 'Allegiance-linux-x64'
            bundle.mkdir()
            (bundle / 'game.dll').write_text('app')
            (bundle / 'autoupdate').mkdir(parents=True)
            (bundle / 'autoupdate' / 'installed.json').write_text('{}')
            (bundle / 'packs' / 'hd').mkdir(parents=True)
            (bundle / 'packs' / 'hd' / 'tex.pbr').write_text('huge')
            (bundle / 'hd').mkdir(parents=True)
            (bundle / 'hd' / 'bonus.tex').write_text('huge')
            package.write_installed_manifest(bundle, '1.0.0')
            manifest = json.loads((bundle / 'autoupdate' / 'installed.json').read_text())
            self.assertEqual(['game.dll'], [entry['path'] for entry in manifest['files']])

    def test_dev_bundle_reports_zero_version(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            bundle = root / 'Allegiance-linux-x64'
            bundle.mkdir()
            (bundle / 'Allegiance').write_text('app')
            package.write_installed_manifest(bundle, '0.0.0')
            manifest = json.loads((bundle / 'autoupdate' / 'installed.json').read_text())
            self.assertEqual('0.0.0', manifest['version'])
            self.assertEqual(['Allegiance'], [entry['path'] for entry in manifest['files']])


if __name__ == '__main__':
    unittest.main()
