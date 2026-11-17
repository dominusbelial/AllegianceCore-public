#!/usr/bin/env python3
"""Generate a resident intro pack from actual desktop Game1 opens; measure (never stage) P1045's join candidates.

Build/run tools/p1047-menu-opens for Classic and Modern first. This tool requires
those recordings, verifies source hashes, and refuses a guessed/absent opening.
Original artwork/content bytes go only into the caller's ignored generation.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

def safe_path(root, relative):
    if not re.fullmatch(r'[A-Za-z0-9_@./-]+', relative) or any(p in ('', '.', '..') for p in relative.split('/')):
        raise ValueError('Invalid recorded source name')
    path = root / relative
    if path.is_symlink() or not path.is_file():
        raise ValueError(f'Missing/nonregular recorded file: {relative}')
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError('Source escapes its root')
    return path

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--traces', nargs=2, required=True)
    parser.add_argument('--artwork', type=Path, required=True)
    parser.add_argument('--content', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--receipt', type=Path, required=True)
    args = parser.parse_args()
    traces = [json.loads(Path(p).read_text()) for p in args.traces]
    if {t['style'] for t in traces} != {'Classic', 'Modern'}:
        raise ValueError('Both desktop HUD styles must be recorded')
    opens = []
    for trace in traces:
        expected = {trace['style'] + '/' + scene for scene in ('Intro', 'Options', 'GameOptions')}
        if not expected.issubset(trace['Scenes']):
            raise ValueError('Recording did not visit the required actual Game1 scenes')
        opens.extend(trace['Opens'])
    # The native menu event closure determines the cue, and it must occur in the recording.
    # sounddef.mdl:321: mouseclickSound = GainSound(SFXGain, ImportWave("mousedown")).
    sounddef = safe_path(args.artwork, 'sounddef.mdl').read_text(errors='strict')
    cue = re.search(r'\bmouseclickSound\s*=\s*GainSound\([^,]+,\s*ImportWave\("([A-Za-z0-9_]+)"\)\)', sounddef)
    if not cue:
        raise ValueError('Cannot resolve the native menu cue')
    menu_sound = f'content/sounds/{cue[1]}.xnb'
    selected = [o for o in opens if o['Purpose'] == 'SpriteFont' or
                o['Source'].lower() == 'pcore001.igc' or o['Source'].lower() == menu_sound]
    if not any(o['Source'].lower() == menu_sound for o in selected):
        raise ValueError('Desktop did not actually open the native menu cue')
    if not any(o['Source'].lower() == 'pcore001.igc' for o in selected):
        raise ValueError('Desktop did not actually open the binary core')
    entries, fingerprints = {}, {}
    for opened in selected:
        source = opened['Source']
        root = args.artwork if opened['Origin'] == 'artwork' else args.content
        relative = source if opened['Origin'] == 'artwork' else source.removeprefix('Content/')
        path = safe_path(root, relative)
        data = path.read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        if len(data) != opened['ByteSize'] or sha != opened['Sha256']:
            raise ValueError(f'Recorded source changed: {source}')
        canonical = source.lower()
        if canonical in entries and entries[canonical]['sha256'] != sha:
            raise ValueError(f'Case-colliding recorded assets: {source}')
        provenance = ('FreeAllegiance Artwork; MIT (2017 FreeAllegiance)' if opened['Origin'] == 'artwork'
                      or canonical == menu_sound else 'Bundled DejaVu fonts; Bitstream Vera/DejaVu licence; desktop MonoGame XNB bake')
        entries[canonical] = dict(source=canonical, path='packs/' + sha, byteSize=len(data), sha256=sha,
                                  contentType='application/octet-stream', provenance=provenance, dependencies=[])
        fingerprints[sha] = data
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / 'packs').mkdir(exist_ok=True)
    for sha, data in fingerprints.items():
        target = args.output / 'packs' / sha
        if target.exists() and target.read_bytes() != data:
            raise ValueError('Immutable generation contains different bytes')
        target.write_bytes(data)
        target.with_suffix('.gz').write_bytes(gzip.compress(data, compresslevel=9, mtime=0))
    manifest = dict(version=2, assets=[entries[k] for k in sorted(entries)])
    (args.output / 'starter-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    # Font licence accompanies bakes; the original artwork notice is already in the browser shell.
    (args.output / 'font-NOTICE.txt').write_bytes((ROOT / 'src/Allegiance.ClientCore.MonoGame/Content/Fonts/LICENSE-DejaVu.txt').read_bytes())
    planning = json.loads((ROOT / 'docs/reports/BROWSER_CLIENT_P1045_assets/artwork-inventory.json').read_text())
    join = {}
    for name, group in planning['groups'].items():
        for entry in group['entries']:
            key = entry['path']
            existing = join.get(key)
            if existing and existing['sha256'] != entry['sha256']:
                raise ValueError('Ambiguous study candidate')
            join[key] = entry
    # Re-measure the archived candidates against this lane's actual read-only bytes.
    for key, entry in list(join.items()):
        if key.startswith('artwork/'):
            path = safe_path(args.artwork, key.removeprefix('artwork/'))
        elif key.startswith('Content/'):
            path = safe_path(ROOT / 'src/Allegiance.ClientCore.MonoGame', key)
        else:
            path = safe_path(args.content, key)
        data = path.read_bytes()
        if (entry.get('sha256') and hashlib.sha256(data).hexdigest() != entry['sha256']) or len(data) != entry['rawBytes']:
            raise ValueError(f'P1045 candidate has changed: {key}')
        join[key] = dict(entry, sha256=hashlib.sha256(data).hexdigest(), rawBytes=len(data), gzipBytes=len(gzip.compress(data, compresslevel=9, mtime=0)))
    # Stage the unchanged v1 preview allow-list as well, for local static proof.
    # The lobby continues to supply these same paths using its existing v1 registry.
    preview = json.loads((args.output / 'asset-manifest.json').read_text())
    for entry in preview['assets']:
        source = entry['source']
        if not re.fullmatch(r'[a-z0-9]+\.mdl', source):
            raise ValueError('Invalid v1 preview source')
        data = safe_path(args.artwork, source).read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        if sha != entry['sha256'] or len(data) != entry['byteSize'] or entry['path'] != f'art/{source[:-4]}.{sha}.mdl':
            raise ValueError('Changed v1 preview asset')
        target = args.output / entry['path']
        target.parent.mkdir(exist_ok=True)
        target.write_bytes(data)
        Path(str(target) + '.gz').write_bytes(gzip.compress(data, compresslevel=9, mtime=0))
    # A proposed join-layer cut, still measure-only: keep the first-sector sky and
    # XNB bakes; defer full-map skies, original high-resolution rocks and TTF sources.
    cut = {}
    for name, group in planning['groups'].items():
        if name in ('map-sky', 'rock-surfaces', 'font-sources'):
            continue
        for entry in group['entries']:
            cut[entry['path']] = join[entry['path']]
    receipt = dict(
        traceScenes=[s for t in traces for s in t['Scenes']], recordedOpens=len(opens),
        selection='All recorded SpriteFont opens, recorded PCore001 binary, and recorded native mouseclickSound closure. Intro/Options use procedural fills; flight assets eagerly opened by desktop boot are deferred.',
        starter=dict(files=len(entries), rawBytes=sum(a['byteSize'] for a in entries.values()),
                     gzipBytes=sum(len(gzip.compress(b, compresslevel=9, mtime=0)) for b in fingerprints.values()),
                     assets=manifest['assets']),
        proposedJoinCut=dict(files=len(cut), rawBytes=sum(e['rawBytes'] for e in cut.values()), gzipBytes=sum(e['gzipBytes'] for e in cut.values()), deferredGroups=['map-sky except first-sector-sky', 'rock-surfaces', 'font-sources'], note='Planning only: needs capped/shared rock surfaces and tested emergency geometry before joining; no playable-pack certification.'),
        projectedJoin=dict(note='Measure-only union of every P1045 candidate group, including high-resolution rocks/all-map skies and alternative TTF/XNB fonts; not a certified playable closure and not staged.',
                           files=len(join), rawBytes=sum(e['rawBytes'] for e in join.values()),
                           gzipBytes=sum(e['gzipBytes'] for e in join.values()), entries=[join[k] for k in sorted(join)]))
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({k: {a: b for a, b in v.items() if a not in ('assets', 'entries')} for k, v in receipt.items() if isinstance(v, dict)}, indent=2))

if __name__ == '__main__':
    main()
