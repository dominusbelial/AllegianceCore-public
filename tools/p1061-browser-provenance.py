#!/usr/bin/env python3
"""P1061 — per-pack artwork attribution/provenance manifest.

Reads the completed generation's starter/join/sector manifests and writes one
served JSON document listing every asset's source name, hash, size, pack and the
provenance string the pack builder recorded. Fails closed if any entry or path
names Meshy or an HD pack (both are out of scope for the public browser bundle).
No bytes are changed; the manifest is mutable metadata and the lobby serves it
no-store.
"""
import argparse, json, re
from pathlib import Path

ARTWORK = {'license': 'MIT', 'copyright': '2017 FreeAllegiance', 'notice': 'artwork-NOTICE.txt'}
FRAMEWORK = [
    {'component': 'KNI 4.3.9001', 'license': 'Microsoft Public License (Ms-PL)', 'notice': 'THIRD-PARTY-NOTICES.txt'},
    {'component': 'MonoGame (and Mono.Xna portions)', 'license': 'Ms-PL / MIT per component', 'notice': 'THIRD-PARTY-NOTICES.txt'},
    {'component': 'nkast.Wasm 10.0.3', 'license': 'MIT', 'notice': 'THIRD-PARTY-NOTICES.txt'},
    {'component': '.NET 10 browser runtime and ASP.NET Core / Blazor', 'license': 'MIT', 'notice': 'THIRD-PARTY-NOTICES.txt'},
    {'component': 'Google Brotli (runtime decoder modules)', 'license': 'MIT', 'notice': 'THIRD-PARTY-NOTICES.txt'},
    {'component': 'DejaVu fonts', 'license': 'Bitstream Vera / DejaVu licence', 'notice': 'font-NOTICE.txt'},
]
FORBIDDEN = re.compile(r'meshy|hd[\s-]*pack|hit-pak|hd[-_]pack', re.IGNORECASE)


def manifest(root, name):
    path = root / name
    return json.loads(path.read_text()) if path.exists() else None


def notice_path(root, name):
    mapping = manifest(root, 'app-manifest.json') or {}
    return mapping.get('files', {}).get(name, name)


def load(root):
    packs, seen = [], set()
    sources = [('starter', manifest(root, 'starter-manifest.json'))]
    join = manifest(root, 'join-manifest.json')
    sources.append(('join', join))
    sector = manifest(root, 'sector-manifest.json')
    if sector:
        for pack in sector.get('sectors', []):
            sources.append(('sector ' + str(pack.get('name', pack.get('sectorId'))), {'assets': pack.get('assets', [])}))
    battle = manifest(root, 'battle-manifest.json')
    if battle:
        sources.append(('battle on demand', {'assets': battle.get('assets', [])}))
    for name, data in sources:
        if not data:
            continue
        assets = []
        for asset in data.get('assets', []):
            sha = asset.get('sha256', '')
            if sha in seen:
                continue
            seen.add(sha)
            provenance = asset.get('provenance', '').replace('scene-NOTICE.txt', notice_path(root, 'scene-NOTICE.txt'))
            record = dict(source=asset.get('source'), path=asset.get('path'), sha256=sha,
                          byteSize=asset.get('byteSize'), contentType=asset.get('contentType'),
                          provenance=provenance)
            for field in ('source', 'path', 'provenance'):
                if record[field] and FORBIDDEN.search(str(record[field])):
                    raise SystemExit('Forbidden provenance in ' + name + ': ' + str(record[field]))
            assets.append(record)
        packs.append({'name': name, 'assets': assets})
    return packs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    args = parser.parse_args()
    document = {
        'schema': 1,
        'artwork': dict(ARTWORK, notice=notice_path(args.root, ARTWORK['notice']), packs=load(args.root)),
        'additionalArtwork': ([{'component': 'Existing desktop B1 astronomical paintings',
                                'licenses': ['CC BY 4.0', 'Public domain US Government work'],
                                'notice': notice_path(args.root, 'scene-NOTICE.txt'),
                                'note': 'Per-file provenance overrides the native artwork MIT default.'}]
                              if (args.root / notice_path(args.root, 'scene-NOTICE.txt')).exists() else []),
        'framework': [dict(row, notice=notice_path(args.root, row['notice'])) for row in FRAMEWORK],
        'javascriptThirdParty': [],
        'redistribution': 'Artwork and engine notices are retained in the published bundle; '
                          'conversion or repacking does not change the original licence.',
        'forbiddenContent': {'checked': ['meshy', 'HD pack'], 'hits': []},
    }
    (args.root / 'artwork-provenance.json').write_text(json.dumps(document, indent=2) + '\n')
    print(json.dumps({'packs': len(document['artwork']['packs']),
                      'assets': sum(len(p['assets']) for p in document['artwork']['packs'])}))


if __name__ == '__main__':
    main()
