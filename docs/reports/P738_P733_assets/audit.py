"""Verify the file-only merge against both input trees, without git commands."""
import csv, hashlib, io, json, re
from merge_data import ROOT, OUT, gitdir, objects, B, O, T

checks = {}
for name, stages in json.loads((OUT / 'index-stages.json').read_text()).items():
    assert stages['1'] == B[name] and stages['2'] == O[name] and stages['3'] == T[name], name
checks['conflict_stages_match_audited_input_trees'] = True
before = json.loads((OUT / 'metadata-fingerprints.json').read_text())
checks['metadata_unchanged'] = all(hashlib.sha256((gitdir / p).read_bytes()).hexdigest() == h for p, h in before.items())
assert checks['metadata_unchanged']

rows_audited = 0
decisions = json.loads((OUT / 'parity-row-audit.json').read_text())
for name in ('docs/parity/parity-matrix.csv', 'docs/parity/sim-rules-matrix.csv', 'docs/parity/fedmsg-wiring.csv'):
    def read(data):
        rows = list(csv.DictReader(io.StringIO(data.decode())))
        def key(r): return r.get('row_id') or r.get('id') or (r['area'], r['native_item'])
        result = {key(r): r for r in rows}
        assert len(result) == len(rows)
        return result
    b, o, t = (read(objects.read(v[name])) for v in (B, O, T))
    current = read((ROOT / name).read_bytes())
    assert b.keys() == o.keys() == t.keys() == current.keys()
    for key in b:
        expected = o[key] if o[key] == t[key] or t[key] == b[key] else t[key] if o[key] == b[key] else None
        if expected is not None: assert current[key] == expected, (name, key)
        else:
            record = next(r for r in decisions if r['file'] == name and r['key'] == key)
            assert current[key] == record['resolved']
        rows_audited += 1
    assert b'\r' not in (ROOT / name).read_bytes()
checks['matrix_rows_audited'] = rows_audited
checks['matrix_row_differences'] = len(decisions)

name = 'docs/TEST_FLAKES.md'
current_lines = set((ROOT / name).read_text().splitlines())
for v in (O, T): assert set(objects.read(v[name]).decode().splitlines()) <= current_lines
checks['flake_document_is_union'] = True

reports = [p for p in O if re.search(r'docs/reports/P(710|711|714|716|717|719|721|723|725|732|735|736|737)_.*\.md$', p)]
for name in reports: assert (ROOT / name).read_bytes() == objects.read(O[name]), name
name = 'docs/reports/P733_TECH_FINALE_AND_SPEED.md'
assert (ROOT / name).read_bytes().startswith(objects.read(T[name]))
checks['existing_sound_reports_preserved'] = reports

name = 'src/Allegiance.ClientCore.Game/Audio/SoundManager.cs'
assert (ROOT / name).read_bytes() == objects.read(O[name])
checks['sound_manager_preserves_complete_current_implementation'] = True
name = 'src/Allegiance.ClientCore/State/ClientSoundEvent.cs'
def kinds(text):
    enum = text.split('public enum ClientSoundEventKind', 1)[1].split('}', 1)[0]
    return re.findall(r'^    (\w+)(?:\s*=\s*\d+)?,', enum, re.M)
merged = kinds((ROOT / name).read_text())
expected = set(kinds(objects.read(O[name]).decode())) | set(kinds(objects.read(T[name]).decode()))
cases = re.findall(r'case ClientSoundEventKind\.(\w+)\s*:', (ROOT / 'src/Allegiance.ClientCore.Game/Game1.Audio.cs').read_text())
assert len(merged) == len(set(merged)) and len(cases) == len(set(cases)) and set(merged) == set(cases) == expected
checks['unique_sound_kinds_and_dispatches'] = len(merged)

name = 'src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb'
pattern = r'^/build:(.+)$'
assets = re.findall(pattern, (ROOT / name).read_text(), re.M)
expected = set(re.findall(pattern, objects.read(O[name]).decode(), re.M)) | set(re.findall(pattern, objects.read(T[name]).decode(), re.M))
assert len(assets) == len(set(assets)) and set(assets) == expected
for asset in assets: assert (ROOT / name).parent.joinpath(asset.split(';')[0]).exists(), asset
checks['unique_content_registrations'] = len(assets)

incoming = json.loads((OUT / 'incoming-changes.json').read_text())
checks['incoming_only_files_retained'] = []
for name in incoming:
    if name.startswith(('src/', 'tests/', 'tools/')) and O.get(name) == B.get(name):
        assert (ROOT / name).read_bytes() == objects.read(T[name]), name
        checks['incoming_only_files_retained'].append(name)
    elif name.startswith(('src/', 'tests/', 'tools/')) and name in O and (T[name] == O[name] or (name in B and T[name] == B[name])):
        assert (ROOT / name).read_bytes() == objects.read(O[name]), name

for top in ('src', 'tests', 'docs', 'tools', 'scripts', 'data'):
    for p in (ROOT / top).rglob('*'):
        if p.is_file() and p.suffix in ('.cs', '.csv', '.md', '.mgcb', '.json', '.txt', '.py') and not {'bin', 'obj', 'before'} & set(p.parts):
            assert not re.search(r'^(<<<<<<< |=======$|>>>>>>> )', p.read_text(), re.M), p
checks['no_conflict_markers'] = True
(OUT / 'resolution-audit.json').write_text(json.dumps(checks, indent=2) + '\n')
print(json.dumps({k: v for k, v in checks.items() if not isinstance(v, list)}, indent=2))
