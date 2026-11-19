#!/usr/bin/env python3
"""Finalize a local flight publish with content-addressed application files.
No serving/hosting change. SDK fingerprints retain their runtime integrity names.
HTML, mutable manifests and receipts remain uncacheable entry points.
"""
import argparse
import hashlib
import gzip
import html
import json
from pathlib import Path
import re


def defer_flight_assets(root):
    """The browser menu opens only HUD/HUDLarge and its optional click sound.
    The desktop boot trace also eagerly opened flight fonts/core/decoder;
    their exact entries must remain in the verified complete join manifest.
    """
    starter_path = root / 'starter-manifest.json'
    starter = json.loads(starter_path.read_text())
    join = json.loads((root / 'join-manifest.json').read_text())
    joined = {a['source']: a for a in join['assets']}
    required = {'content/hud.xnb', 'content/hudlarge.xnb', 'content/sounds/mousedown.xnb'}
    if not required.issubset({a['source'] for a in starter['assets']}):
        raise ValueError('Incomplete first-menu closure')
    deferred, menu = [], []
    for asset in starter['assets']:
        source = asset['source']
        flight_only = source.startswith(('content/fonts/native', 'runtime/brotli/')) or source in {'content/hudmodern.xnb', 'pcore001.igc'}
        if flight_only:
            if joined.get(source) != asset:
                raise ValueError('Deferred startup asset missing or changed in join: ' + source)
            deferred.append(source)
        else:
            menu.append(asset)
    starter['assets'] = menu
    starter_path.write_text(json.dumps(starter, indent=2) + '\n')
    receipt_path = root / 'starter-receipt.json'
    if receipt_path.exists():
        receipt = json.loads(receipt_path.read_text())
        receipt['recordedStarter'] = receipt['starter']
        receipt['selection'] += ' P1053: first-menu loads only actual browser menu opens; listed flight-only files retain byte-identical join entries.'
        receipt['starter'] = {'files': len(menu), 'rawBytes': sum(a['byteSize'] for a in menu),
                              'gzipBytes': sum(len(gzip.compress((root / a['path']).read_bytes(), mtime=0)) for a in menu), 'assets': menu}
        receipt['deferredUntilJoin'] = deferred
        receipt_path.write_text(json.dumps(receipt, indent=2) + '\n')
    for file in (starter_path, receipt_path):
        for suffix in ('.br', '.gz'):
            Path(str(file) + suffix).unlink(missing_ok=True)


def finalize(root):
    defer_flight_assets(root)
    mapping = {}
    for source in sorted(root.rglob('*')):
        if not source.is_file() or source.suffix in ('.br', '.gz', '.html', '.json'):
            continue
        relative = source.relative_to(root).as_posix()
        if relative.startswith(('packs/', 'art/')):
            continue
        # The SDK already fingerprints its managed/native WASM and runtime JS.
        if relative.startswith('_framework/') and re.search(r'\.[a-z0-9]{10}\.(wasm|js|dat)$', source.name):
            continue
        digest = hashlib.sha256(source.read_bytes()).hexdigest()[:20]
        target = source.with_name(source.stem + '.' + digest + source.suffix)
        mapping[relative] = target.relative_to(root).as_posix()
        source.rename(target)
        for suffix in ('.br', '.gz'):
            compressed = Path(str(source) + suffix)
            if compressed.exists():
                compressed.rename(str(target) + suffix)
    modules = root / 'brotli/modules.json'
    if modules.exists():
        metadata = json.loads(modules.read_text())
        metadata['modules'] = [Path(mapping.get('brotli/' + name, name)).name for name in metadata['modules']]
        modules.write_text(json.dumps(metadata, indent=2) + '\n')
        for suffix in ('.br', '.gz'):
            Path(str(modules) + suffix).unlink(missing_ok=True)
    # Stable manifests deliberately remain no-store; every versioned entry has
    # an immutable URL. Also embed the map as HTML data, avoiding a startup RTT.
    (root / 'app-manifest.json').write_text(json.dumps({'version': 1, 'files': mapping}, indent=2) + '\n')
    for page in root.glob('*.html'):
        text = page.read_text()
        text = re.sub(r'((?:src|href)=")([^"]+)(")', lambda m: m[1] + mapping.get(m[2], m[2]) + m[3], text)
        encoded = html.escape(json.dumps(mapping, separators=(',', ':')), quote=True)
        preview = mapping['preview.js']
        text = text.replace('src="' + preview + '"', 'src="' + preview + '" data-assets="' + encoded + '"')
        # Boot files are known from this exact completed publish, never guessed
        # runtime versions. Module and fetch preloads match the runtime requests.
        preload = [f'<link rel="preload" as="script" href="{mapping["_framework/blazor.webassembly.js"]}">',
                   f'<link rel="modulepreload" href="{mapping["_framework/dotnet.js"]}">']
        for runtime in sorted((root / '_framework').glob('dotnet.*')):
            if runtime.suffix == '.js' and ('runtime.' in runtime.name or 'native.' in runtime.name):
                preload.append(f'<link rel="modulepreload" href="{runtime.relative_to(root).as_posix()}">')
            if runtime.suffix == '.wasm':
                preload.append(f'<link rel="preload" as="fetch" type="application/wasm" crossorigin="anonymous" href="{runtime.relative_to(root).as_posix()}">')
        text = text.replace('</head>', '\n  <template id="boot-preloads">' + '\n  '.join(preload) + '</template>\n</head>')
        page.write_text(text)
        # HTML has changed since the SDK compression pass; remove stale copies.
        for suffix in ('.br', '.gz'):
            Path(str(page) + suffix).unlink(missing_ok=True)
    return mapping


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    args = parser.parse_args()
    mapping = finalize(args.root)
    print(json.dumps({'hashedApplicationFiles': len(mapping)}))
