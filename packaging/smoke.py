#!/usr/bin/env python3
"""Extract a trusted locally built archive outside the repo and check it."""
import argparse
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('archive', type=Path)
args = parser.parse_args()
with tempfile.TemporaryDirectory(prefix='allegiance-smoke-') as staging:
    root = Path(staging)
    if args.archive.suffix == '.zip':
        with zipfile.ZipFile(args.archive) as z:
            z.extractall(root)
    else:
        with tarfile.open(args.archive) as tar:
            tar.extractall(root, filter='data')
    bundle, = root.iterdir()
    env = {k: v for k, v in os.environ.items() if not k.startswith('ALLEGIANCE_')}
    exe = bundle / ('Allegiance.exe' if args.archive.suffix == '.zip' else 'Allegiance')
    subprocess.run([str(exe), '--check-assets', '--asset-manifest', str(root / 'census.json')],
                   cwd=root, env=env, check=True)
