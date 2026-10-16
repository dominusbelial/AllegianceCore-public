#!/usr/bin/env python3
"""Build a self-contained Linux/Windows DesktopGL player bundle (Python 3.11+)."""
import argparse
import hashlib
import json
import os
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
    with tempfile.TemporaryDirectory(prefix='p644-', dir=output) as staging:
        bundle = Path(staging) / f'Allegiance-{args.rid}'
        publish = ['dotnet', 'publish', str(PROJECT), '-c', 'Release', '-r', args.rid,
                   '--self-contained', 'true', '-o', str(bundle), '-m:1',
                   '-p:AutoRestoreMGCBTool=false', '-p:NuGetAudit=false']
        if args.nuget_source:
            publish += [f'-p:RestoreSources={args.nuget_source}']
        tool = ['dotnet', 'tool', 'restore']
        if args.nuget_source:
            tool += ['--add-source', args.nuget_source, '--ignore-failed-sources']
        run(*tool, cwd=REPO)
        run(*publish, cwd=REPO)
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
        files = ['play.bat', 'play.ps1'] if args.rid == 'win-x64' else ['allegiance.sh', 'install-desktop.sh', 'allegiance.desktop', 'allegiance.svg']
        for name in files:
            shutil.copyfile(REPO / 'packaging' / name, bundle / name)
        if args.rid == 'linux-x64':
            (bundle / 'allegiance.sh').chmod(0o755)
            (bundle / 'install-desktop.sh').chmod(0o755)
            (bundle / 'Allegiance').chmod(0o755)
        # Validate that compiled content actually made it through dotnet publish.
        for name in ('HUD.xnb', 'HUDLarge.xnb', 'quickchat.mdl', 'sounddef.mdl'):
            if not (bundle / 'Content' / name).is_file():
                raise ValueError(f'Compiled content missing: Content/{name}')
        inventory = []
        for folder in ('artwork', 'sky', 'Content'):
            for p in sorted((bundle / folder).rglob('*')):
                if p.is_file():
                    inventory.append(f'{checksum(p)}  {p.relative_to(bundle).as_posix()}\n')
        (bundle / 'bundle-files.sha256').write_text(''.join(inventory))
        unpacked = sum(p.stat().st_size for p in bundle.rglob('*') if p.is_file())
        archive = output / (bundle.name + ('.zip' if args.rid == 'win-x64' else '.tar.gz'))
        pending_archive = Path(staging) / archive.name
        if args.rid == 'win-x64':
            with zipfile.ZipFile(pending_archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
                for p in sorted(bundle.rglob('*')):
                    if p.is_file():
                        z.write(p, p.relative_to(bundle.parent))
        else:
            with tarfile.open(pending_archive, 'w:gz', compresslevel=9) as tar:
                tar.add(bundle, arcname=bundle.name)
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
    parser.add_argument('--rid', choices=['linux-x64', 'win-x64'], required=True)
    parser.add_argument('--output', type=Path, default=REPO / 'dist')
    parser.add_argument('--artwork-path', type=Path)
    parser.add_argument('--sky-path', type=Path)
    parser.add_argument('--nuget-source', help='Offline NuGet source, e.g. ~/.nuget/packages')
    build(parser.parse_args())
