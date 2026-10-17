#!/usr/bin/env python3
"""Extract a trusted locally built archive outside the repo and check it."""
import argparse
import os
from pathlib import Path
import plistlib
import subprocess
import tarfile
import tempfile
import zipfile


def extract(archive, root):
    if archive.suffix == '.zip':
        with zipfile.ZipFile(archive) as z:
            z.extractall(root)
            # Python drops Unix modes; Finder/ditto retain them.
            for info in z.infolist():
                mode = (info.external_attr >> 16) & 0o777
                if info.create_system == 3 and mode:
                    (root / info.filename).chmod(mode)
    else:
        with tarfile.open(archive) as tar:
            tar.extractall(root, filter='data')
    bundle, = root.iterdir()
    return bundle


def executable(bundle):
    app = bundle / 'AllegianceCore.app'
    if app.exists():
        contents = app / 'Contents'
        with (contents / 'Info.plist').open('rb') as stream:
            plist = plistlib.load(stream)
        if plist['CFBundlePackageType'] != 'APPL':
            raise ValueError('Not a macOS application bundle')
        exe = contents / 'MacOS' / plist['CFBundleExecutable']
        resources = contents / 'Resources'
        icon = resources / plist['CFBundleIconFile']
        if icon.read_bytes()[:4] != b'icns':
            raise ValueError('Missing or invalid macOS icon')
        for path in (exe, resources / 'Allegiance'):
            if not path.is_file() or not path.stat().st_mode & 0o111:
                raise ValueError(f'Missing executable bit: {path}')
        for folder in ('artwork', 'sky', 'Content'):
            if not (resources / folder).is_dir():
                raise ValueError(f'Missing macOS resource folder: {folder}')
        return exe
    return bundle / ('Allegiance.exe' if (bundle / 'Allegiance.exe').exists() else 'Allegiance')


def smoke(archive):
    with tempfile.TemporaryDirectory(prefix='allegiance-smoke-') as staging:
        root = Path(staging)
        bundle = extract(archive, root)
        exe = executable(bundle)
        env = {k: v for k, v in os.environ.items() if not k.startswith('ALLEGIANCE_')}
        subprocess.run([str(exe), '--check-assets', '--asset-manifest', str(root / 'census.json')],
                       cwd=root, env=env, check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path)
    smoke(parser.parse_args().archive.resolve())
