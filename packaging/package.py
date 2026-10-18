#!/usr/bin/env python3
"""Build a self-contained Linux/Windows/macOS DesktopGL player bundle (Python 3.11+)."""
import argparse
import hashlib
import json
import plistlib
import re
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

REPO = Path(__file__).resolve().parents[1]
PROJECT = REPO / 'src/Allegiance.ClientCore.MonoGame/Allegiance.ClientCore.MonoGame.csproj'
LIMIT = 2 * 1024**3


def mac_app(bundle):
    """Keep runtime and assets together in Resources for executable-relative lookup."""
    contents = bundle / 'AllegianceCore.app/Contents'
    resources = contents / 'Resources'
    resources.mkdir(parents=True)
    for path in list(bundle.iterdir()):
        if path.name != 'AllegianceCore.app':
            shutil.move(str(path), resources / path.name)
    macos = contents / 'MacOS'
    macos.mkdir()
    launcher = macos / 'Allegiance'
    launcher.write_text('#!/bin/sh\nset -eu\n'
                        'cd -- "$(dirname -- "$0")/../Resources"\n'
                        'exec ./Allegiance "$@"\n')
    launcher.chmod(0o755)
    (resources / 'Allegiance').chmod(0o755)
    shutil.copyfile(REPO / 'packaging/allegiance.icns', resources / 'allegiance.icns')
    with (contents / 'Info.plist').open('wb') as stream:
        plistlib.dump({
            'CFBundleName': 'AllegianceCore',
            'CFBundleDisplayName': 'AllegianceCore',
            'CFBundleIdentifier': 'org.allegiancecore.client',
            'CFBundleExecutable': 'Allegiance',
            'CFBundlePackageType': 'APPL',
            'CFBundleIconFile': 'allegiance.icns',
            'CFBundleShortVersionString': '1.0',
            'CFBundleVersion': '1',
            'LSMinimumSystemVersion': '14.0',
            'NSHighResolutionCapable': True,
        }, stream)
    # Instructions are also visible without opening the application package.
    shutil.copyfile(resources / 'README-PLAYERS.txt', bundle / 'README-PLAYERS.txt')


def archive_bundle(bundle, archive, rid):
    if rid in ('win-x64', 'osx-arm64'):
        # Unix modes in external_attr survive Finder/ditto/unzip extraction.
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
            for path in sorted(bundle.rglob('*')):
                z.write(path, path.relative_to(bundle.parent))
    else:
        with tarfile.open(archive, 'w:gz', compresslevel=9) as tar:
            tar.add(bundle, arcname=bundle.name)


def required_content():
    # Fixed SoundManager beds/cues must all load: one missing XNB prevents its
    # remaining initialization. Music's optional real beds are intentionally
    # excluded. Scanning the live manager catches new references after merges.
    sound_manager = REPO / 'src/Allegiance.ClientCore.Game/Audio/SoundManager.cs'
    sounds = set(re.findall(r'"Sounds/([A-Za-z0-9_]+)"', sound_manager.read_text()))
    return ['HUD.xnb', 'HUDLarge.xnb', 'quickchat.mdl', 'sounddef.mdl'] + [
        f'Sounds/{name}.xnb' for name in sorted(sounds)]


def validate_content(bundle):
    for name in required_content():
        if not (bundle / 'Content' / name).is_file():
            raise ValueError(f'Compiled content missing: Content/{name}')


def run(*args, **kwargs):
    subprocess.run([str(a) for a in args], check=True, **kwargs)


def paths(manifest):
    result = []
    for line in manifest.read_text().splitlines():
        name = line.strip()
        if not name or name.startswith('#'):
            continue
        p = PurePosixPath(name)
        if p.is_absolute() or '..' in p.parts or '\\' in name or ':' in name:
            raise ValueError(f'Unsafe manifest path: {name}')
        if name in result:
            raise ValueError(f'Duplicate manifest path: {name}')
        result.append(name)
    if not result:
        raise ValueError('Empty artwork manifest')
    return result


def source(name, override, cache, pins):
    pin = pins[name]
    root = override.resolve() if override else cache / name
    if not override:
        if not (root / '.git').exists():
            root.mkdir(parents=True, exist_ok=True)
            run('git', 'init', root)
            run('git', '-C', root, 'remote', 'add', 'origin', pin['url'])
        run('git', '-C', root, 'fetch', '--depth=1', 'origin', pin['commit'])
        run('git', '-C', root, 'checkout', '--detach', pin['commit'])
    head = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
    if head != pin['commit']:
        raise ValueError(f'{name} checkout must be at {pin["commit"]}, got {head}')
    run('git', '-C', root, 'diff', '--quiet', 'HEAD', '--')
    tracked = subprocess.check_output(['git', '-C', str(root), 'ls-tree', '-r', '--name-only', '-z', 'HEAD']).decode().split('\0')
    return root, set(filter(None, tracked))


def copy(root, name, dest, tracked):
    if name not in tracked or not (root / name).is_file() or (root / name).is_symlink():
        raise ValueError(f'Missing or untracked pinned asset: {name}')
    target = dest / name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(root / name, target)


def checksum(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def build(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    pins = json.loads((REPO / 'packaging/sources.json').read_text())
    art, art_files = source('artwork', args.artwork_path, output / 'sources', pins)
    sky, sky_files = source('sky', args.sky_path, output / 'sources', pins)
    run(sys.executable, REPO / 'packaging/generate-manifest.py', '--artwork', art, '--check')
    manifest = paths(REPO / 'packaging/artwork-manifest.txt')
    with tempfile.TemporaryDirectory(prefix='player-bundle-', dir=output) as staging:
        bundle = Path(staging) / f'Allegiance-{args.rid}'
        # The server is a managed embedded library here. Disable the server
        # executable's AOT default throughout this graph, including cross-RID builds.
        publish = ['dotnet', 'publish', str(PROJECT), '-c', 'Release', '-r', args.rid,
                   '--self-contained', 'true', '-o', str(bundle), '-m:1',
                   '-p:AutoRestoreMGCBTool=false', '-p:NuGetAudit=false', '-p:PublishAot=false']
        if args.nuget_source:
            publish += [f'-p:RestoreSources={args.nuget_source}']
        tool = ['dotnet', 'tool', 'restore']
        if args.nuget_source:
            tool += ['--add-source', args.nuget_source, '--ignore-failed-sources']
        run(*tool, cwd=REPO)
        run(*publish, cwd=REPO)
        # P727: the embedded server arrives through the game project reference;
        # publish the headless supervisor and all of its dependencies beside it.
        bots = ['dotnet', 'publish', str(REPO / 'tools/allegiance-mcp/allegiance-mcp.csproj'),
                '-c', 'Release', '-r', args.rid, '--self-contained', 'true',
                '-o', str(bundle / 'bot-driver'), '-m:1', '-p:NuGetAudit=false', '-p:PublishAot=false']
        if args.nuget_source:
            bots += [f'-p:RestoreSources={args.nuget_source}']
        run(*bots, cwd=REPO)
        for name in ('Allegiance.Server.dll', 'Allegiance.SinglePlayer.dll'):
            if not (bundle / name).is_file():
                raise ValueError(f'Skirmish assembly missing: {name}')
        # Rename only the apphost; its embedded managed entry point stays intact.
        original = bundle / ('Allegiance.ClientCore.MonoGame.exe' if args.rid == 'win-x64' else 'Allegiance.ClientCore.MonoGame')
        original.rename(bundle / ('Allegiance.exe' if args.rid == 'win-x64' else 'Allegiance'))
        for name in manifest:
            copy(art, name, bundle / 'artwork', art_files)
        copy(art, 'LICENSE', bundle / 'artwork', art_files)
        for name in sorted(sky_files):
            copy(sky, name, bundle / 'sky', sky_files)
        for name in ('LICENSE', 'NOTICE', 'CREDITS.md'):
            shutil.copyfile(REPO / name, bundle / name)
        for name in ('README-PLAYERS.txt', 'artwork-manifest.txt', 'artwork-roots.txt'):
            shutil.copyfile(REPO / 'packaging' / name, bundle / name)
        shutil.copyfile(REPO / 'packaging/sources.json', bundle / 'sources.json')
        for name in ('catalogue.json', 'ship-art.json'):
            target = bundle / 'assets/ships' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / 'assets/ships' / name, target)
        files = {'win-x64': ['play.bat', 'play.ps1'],
                 'linux-x64': ['allegiance.sh', 'install-desktop.sh', 'allegiance.desktop', 'allegiance.svg'],
                 'osx-arm64': []}[args.rid]
        for name in files:
            shutil.copyfile(REPO / 'packaging' / name, bundle / name)
        if args.rid == 'linux-x64':
            (bundle / 'allegiance.sh').chmod(0o755)
            (bundle / 'install-desktop.sh').chmod(0o755)
            (bundle / 'Allegiance').chmod(0o755)
        # Validate that compiled content actually made it through dotnet publish.
        validate_content(bundle)
        inventory = []
        for folder in ('artwork', 'sky', 'Content', 'bot-driver', 'showcase-plans'):
            for p in sorted((bundle / folder).rglob('*')):
                if p.is_file():
                    inventory.append(f'{checksum(p)}  {p.relative_to(bundle).as_posix()}\n')
        (bundle / 'bundle-files.sha256').write_text(''.join(inventory))
        if args.rid == 'osx-arm64':
            mac_app(bundle)
        unpacked = sum(p.stat().st_size for p in bundle.rglob('*') if p.is_file())
        archive = output / (bundle.name + ('.tar.gz' if args.rid == 'linux-x64' else '.zip'))
        pending_archive = Path(staging) / archive.name
        archive_bundle(bundle, pending_archive, args.rid)
        if pending_archive.stat().st_size >= LIMIT:
            pending_archive.unlink()
            raise ValueError('Archive exceeds GitHub 2 GiB limit; split before publishing')
        pending_archive.replace(archive)
        (output / (archive.name + '.sha256')).write_text(f'{checksum(archive)}  {archive.name}\n')
        (output / (archive.name + '.json')).write_text(json.dumps({
            'rid': args.rid, 'archiveBytes': archive.stat().st_size, 'unpackedBytes': unpacked,
            'artworkFiles': len(manifest), 'sources': pins}, indent=2) + '\n')
        print(f'Created {archive}: {archive.stat().st_size:,} bytes; unpacked {unpacked:,} bytes')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', choices=['linux-x64', 'win-x64', 'osx-arm64'], required=True)
    parser.add_argument('--output', type=Path, default=REPO / 'dist')
    parser.add_argument('--artwork-path', type=Path)
    parser.add_argument('--sky-path', type=Path)
    parser.add_argument('--nuget-source', help='Offline NuGet source, e.g. ~/.nuget/packages')
    build(parser.parse_args())
