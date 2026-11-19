#!/usr/bin/env python3
"""Independently audit a completed local sector publish against original bytes."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re


def audit(root, artwork, content, decoder):
    base = json.loads((root / 'join-manifest.json').read_text())
    index = json.loads((root / 'sector-manifest.json').read_text())
    starter = json.loads((root / 'starter-manifest.json').read_text())
    paths = {}
    for prefix, directory in [('', artwork), ('content/', content), ('runtime/brotli/', decoder)]:
        paths.update({prefix + p.relative_to(directory).as_posix().lower(): p for p in directory.rglob('*') if p.is_file()})
    copies = set(json.loads((root / 'sector-receipt.json').read_text())['entryFirstFrameCopies'])
    checked, original_surfaces = [], {}
    for entry in base['assets'] + [a for sector in index['sectors'] for a in sector['assets']]:
        name = entry['source']
        if not re.fullmatch(r'[a-z0-9_@./-]+', name) or any(p in ('', '.', '..') for p in name.split('/')):
            raise ValueError('Invalid source')
        data = (root / entry['path']).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if len(data) != entry['byteSize'] or digest != entry['sha256'] or entry['path'] != 'packs/' + digest:
            raise ValueError('Published hash/size mismatch: ' + name)
        original = paths[name].read_bytes()
        if name in copies and entry in base['assets']:
            if len(data) != 131188 or len(original) != 8388724:
                raise ValueError('Unexpected entry first-frame dimensions: ' + name)
            checked.append((name, 'entry-copy'))
        else:
            if data != original:
                raise ValueError('Published bytes differ from source: ' + name)
            checked.append((name, 'original'))
            if name.startswith(('bgrnd', 'environment', 'nebplnt')) and name.endswith('bmp.mdl'):
                original_surfaces[name] = dict(source=name, byteSize=len(data), sha256=digest, byteIdentical=True)
    # All streamed files must be original, including any entry name shared with a base copy.
    for sector in index['sectors']:
        for entry in sector['assets']:
            if (root / entry['path']).read_bytes() != paths[entry['source']].read_bytes():
                raise ValueError('A streamed visual was transformed')
            if not re.fullmatch(r'(bgrnd[0-9]+bmp|environment[0-9]+bmp|nebplnt[0-9]+(?:bmp)?)\.(mdl|png)', entry['source']):
                raise ValueError('Gameplay file deferred')
    referenced = {a['sha256'] for a in base['assets'] + starter['assets'] + [a for s in index['sectors'] for a in s['assets']]}
    published = {p.name for p in (root / 'packs').iterdir() if p.suffix not in ('.gz', '.br')}
    if published != referenced:
        raise ValueError('Generation contains missing or unreferenced pack files')
    def transfer(path):
        return len(gzip.compress(path.read_bytes(), 9, mtime=0))
    base_unique = {a['sha256']: a for a in base['assets']}
    base_gzip = sum(transfer(root / a['path']) for a in base_unique.values())
    nonpack = [p for p in root.rglob('*') if p.is_file() and p.suffix not in ('.br', '.gz') and p.relative_to(root).parts[0] != 'packs']
    nonpack_gzip = sum(transfer(p) for p in nonpack)
    framework_gzip = sum(transfer(p) for p in nonpack if p.relative_to(root).parts[0] == '_framework')
    total = base_gzip + nonpack_gzip
    if total > 14 * 1024 * 1024:
        raise ValueError('Entire base/application inventory exceeds 14 MiB')
    return dict(map=index['map'], seed=index['seed'], baseLogicalFiles=len(base['assets']), baseUniqueFiles=len(base_unique),
                baseGzipBytes=base_gzip, frameworkGzipBytes=framework_gzip,
                allNonpackFiles=len(nonpack), allNonpackGzipBytes=nonpack_gzip,
                strictBasePlusApplicationGzipBytes=total, budgetBytes=14 * 1024 * 1024,
                marginBytes=14 * 1024 * 1024 - total, publishedUniquePackFiles=len(published), unreferencedPackFiles=0,
                entryFirstFrameCopies=sorted(copies), sourceChecks=len(checked), originalSurfaces=list(original_surfaces.values()))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['root', 'artwork', 'content', 'decoder', 'output']: parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.root, args.artwork, args.content, args.decoder)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({k: v for k, v in result.items() if k != 'originalSurfaces'}, indent=2))
