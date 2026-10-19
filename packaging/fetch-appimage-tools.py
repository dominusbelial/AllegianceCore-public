#!/usr/bin/env python3
"""Download and verify the versioned AppImage build tools; never run unverified bytes."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request


def fetch(destination):
    destination.mkdir(parents=True, exist_ok=True)
    pins = json.loads(Path(__file__).with_name('appimage-tools.json').read_text())
    for name, pin in pins.items():
        target = destination / name
        if not target.exists() or hashlib.sha256(target.read_bytes()).hexdigest() != pin['sha256']:
            pending = target.with_suffix('.download')
            try:
                urllib.request.urlretrieve(pin['url'], pending)
                if hashlib.sha256(pending.read_bytes()).hexdigest() != pin['sha256']:
                    raise ValueError(f'Build tool checksum mismatch: {name}')
                pending.replace(target)
            finally:
                pending.unlink(missing_ok=True)
        target.chmod(0o755)
        print(target)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    fetch(parser.parse_args().destination)
