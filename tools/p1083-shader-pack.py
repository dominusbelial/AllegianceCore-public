#!/usr/bin/env python3
"""Append one exact verified effect to a local generation; never expand the join.
The default browser never requests this pack. No CSP or canonical-name changes.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path

SOURCE = 'content/shaders/volumetric.xnb'
KEY = 'shader-volumetric'


def build(root, effect):
    data = effect.read_bytes()
    if not data.startswith(b'XNBb') or b'Microsoft.Xna.Framework.Content.EffectReader' not in data or b'ReflectiveReader' in data or len(data) > 16 * 1024 * 1024:
        raise ValueError('Expected bounded BlazorGL XNB effect')
    metadata = json.loads(effect.with_suffix('.xnb.json').read_text())
    digest = hashlib.sha256(data).hexdigest()
    if metadata['outputSha256'] != digest or metadata['outputBytes'] != len(data):
        raise ValueError('Effect build receipt does not match bytes')
    join = (root / 'join-manifest.json').read_bytes()
    manifest = json.loads((root / 'battle-manifest.json').read_text())
    entry = dict(source=SOURCE, path='packs/' + digest, byteSize=len(data), sha256=digest,
                 contentType='application/octet-stream', dependencies=[],
                 provenance='AllegianceCore MIT; Volumetric.fx port enhancement; ' + metadata['format'] + '; source ' + metadata['sourceSha256'])
    existing = next((a for a in manifest['assets'] if a['source'].lower() == SOURCE), None)
    if existing is not None and existing != entry:
        raise ValueError('Conflicting shader asset')
    manifest['assets'] = sorted([a for a in manifest['assets'] if a['source'].lower() != SOURCE] + [entry], key=lambda a: a['source'])
    manifest['packs'] = [p for p in manifest['packs'] if p['key'] != KEY] + [dict(key=KEY, sources=[SOURCE])]
    (root / entry['path']).write_bytes(data)
    (root / (entry['path'] + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
    (root / 'battle-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    receipt = dict(version=1, **entry, joinUnchanged=(root / 'join-manifest.json').read_bytes() == join,
                   defaultRequested=False, build=metadata)
    (root / 'shader-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--effect', type=Path, default=Path(__file__).resolve().parents[1] /
                        'src/Allegiance.ClientCore.Game.Browser/Content/Shaders/Volumetric.xnb')
    args = parser.parse_args()
    print(json.dumps(build(args.root, args.effect), indent=2))
