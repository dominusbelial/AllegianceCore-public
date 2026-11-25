#!/usr/bin/env python3
"""Append exact verified effects to a local generation; never expand the join.
The default browser never requests this pack. No CSP or canonical-name changes.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path

SOURCE = 'content/shaders/volumetric.xnb'
KEY = 'shader-volumetric'
NAMES = ('Bloom', 'DisplacedSphere', 'FsrEasu', 'FsrRcas', 'LinearDepth',
         'NativePbr', 'SoftParticle', 'Tonemap', 'Volumetric')


def build(root, effect, name='Volumetric'):
    if name not in NAMES:
        raise ValueError('Unknown shader')
    source, key = 'content/shaders/' + name.lower() + '.xnb', 'shader-' + name.lower()
    data = effect.read_bytes()
    if not data.startswith(b'XNBb') or b'Microsoft.Xna.Framework.Content.EffectReader' not in data or b'ReflectiveReader' in data or len(data) > 16 * 1024 * 1024:
        raise ValueError('Expected bounded BlazorGL XNB effect')
    metadata = json.loads(effect.with_suffix('.xnb.json').read_text())
    digest = hashlib.sha256(data).hexdigest()
    if metadata['outputSha256'] != digest or metadata['outputBytes'] != len(data):
        raise ValueError('Effect build receipt does not match bytes')
    join = (root / 'join-manifest.json').read_bytes()
    manifest = json.loads((root / 'battle-manifest.json').read_text())
    entry = dict(source=source, path='packs/' + digest, byteSize=len(data), sha256=digest,
                 contentType='application/octet-stream', dependencies=[],
                 provenance='AllegianceCore MIT; ' + name + '.fx port enhancement; ' + metadata['format'] + '; source ' + metadata['sourceSha256'])
    existing = next((a for a in manifest['assets'] if a['source'].lower() == source), None)
    if existing is not None and existing != entry:
        raise ValueError('Conflicting shader asset')
    manifest['assets'] = sorted([a for a in manifest['assets'] if a['source'].lower() != source] + [entry], key=lambda a: a['source'])
    manifest['packs'] = [p for p in manifest['packs'] if p['key'] != key] + [dict(key=key, sources=[source])]
    (root / entry['path']).write_bytes(data)
    (root / (entry['path'] + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
    (root / 'battle-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    receipt = dict(version=1, **entry, joinUnchanged=(root / 'join-manifest.json').read_bytes() == join,
                   defaultRequested=False, build=metadata)
    (root / ('shader-receipt.json' if name == 'Volumetric' else 'shader-' + name.lower() + '-receipt.json')).write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--effect', type=Path, help='Build only the Volumetric pack from this XNB (compatibility mode)')
    args = parser.parse_args()
    directory = Path(__file__).resolve().parents[1] / 'src/Allegiance.ClientCore.Game.Browser/Content/Shaders'
    receipts = [build(args.root, args.effect)] if args.effect else [build(args.root, directory / (name + '.xnb'), name) for name in NAMES]
    print(json.dumps(receipts, indent=2))
