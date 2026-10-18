"""Read-only merge/hygiene audits against both parents; never execute git."""
import collections, csv, hashlib, io, json, re, sys
from pathlib import Path
sys.dont_write_bytecode = True
from merge_data import ROOT, OUT, gitdir, objects, B, O, T

def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
metadata = json.loads((OUT / 'metadata-fingerprints.json').read_text())
assert all(digest(gitdir / name) == value for name, value in metadata.items())

def rows(blob):
    reader = csv.DictReader(io.StringIO(blob.decode()))
    return {r.get('row_id') or r.get('id') or (r['area'], r['native_item']): r for r in reader}

matrix_audit = {}
for name in ('docs/parity/parity-matrix.csv', 'docs/parity/sim-rules-matrix.csv', 'docs/parity/fedmsg-wiring.csv'):
    content = (ROOT / name).read_bytes()
    assert b'\r' not in content
    merged, ours, theirs, base = map(rows, (content, objects.read(O[name]), objects.read(T[name]), objects.read(B[name])))
    assert merged.keys() == ours.keys() == theirs.keys() == base.keys()
    decisions = {(tuple(r['key']) if isinstance(r['key'], list) else r['key']): r
                 for r in json.loads((OUT / 'parity-row-audit.json').read_text()) if r['file'] == name}
    for key, row in merged.items():
        assert row == (decisions[key]['resolved'] if key in decisions else ours[key]), (name, key)
    matrix_audit[name] = dict(rows=len(merged), changed_from_ours=[key for key in merged if merged[key] != ours[key]], lf_only=True)

cue_path = 'src/Allegiance.ClientCore/State/ClientSoundEvent.cs'
audio_path = 'src/Allegiance.ClientCore.Game/Game1.Audio.cs'
def kinds(text):
    body = text.split('public enum ClientSoundEventKind', 1)[1].split('}', 1)[0]
    return re.findall(r'^    ([A-Za-z]\w*)(?:\s*=\s*\d+)?,', body, re.M)
current_kinds = kinds((ROOT / cue_path).read_text())
playback = re.findall(r'case ClientSoundEventKind\.(\w+)\s*:', (ROOT / audio_path).read_text())
assert len(set(current_kinds)) == len(current_kinds) == len(playback) == len(set(playback))
assert set(current_kinds) == set(playback)
for tree in (O, T): assert set(kinds(objects.read(tree[cue_path]).decode())) <= set(current_kinds)
for name in (cue_path, audio_path, 'src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb'):
    assert (ROOT / name).read_bytes() == objects.read(O[name]), name
(OUT / 'cue-audit.json').write_text(json.dumps(dict(kinds=current_kinds, playback_cases=playback,
    one_case_per_kind=True, both_parent_kinds_retained=True, master_audio_and_content_unchanged=True), indent=2) + '\n')

allowed = {name for name in T if T[name] != B.get(name)}
source_files = [p for folder in ('src', 'tests', 'tools') for p in (ROOT / folder).rglob('*')
                if p.is_file() and p.suffix in ('.cs', '.csproj', '.fx', '.mgcb')
                and not {'bin', 'obj'}.intersection(p.relative_to(ROOT).parts)]
changed = [str(p.relative_to(ROOT)) for p in source_files if str(p.relative_to(ROOT)) in O
           and p.read_bytes() != objects.read(O[str(p.relative_to(ROOT))])]
assert set(changed) <= allowed, 'Unexpected source mutation: ' + repr(set(changed) - allowed)
assert all((ROOT / name).exists() for name in allowed if name.startswith(('src/', 'tests/', 'tools/')))
markers = [str(p.relative_to(ROOT)) for p in source_files if re.search(rb'^(?:<<<<<<< |=======\r?$|>>>>>>> )', p.read_bytes(), re.M)]
for name in ('docs/TEST_FLAKES.md', 'docs/parity/sim-rules-matrix.csv', 'docs/parity/parity-matrix.csv'):
    if re.search(rb'^(?:<<<<<<< |=======\r?$|>>>>>>> )', (ROOT / name).read_bytes(), re.M): markers.append(name)
assert not markers

def subsequence(needle, haystack):
    iterator = iter(haystack)
    return all(any(line == candidate for candidate in iterator) for line in needle)
flakes = (ROOT / 'docs/TEST_FLAKES.md').read_text().splitlines()
assert all(subsequence(objects.read(tree['docs/TEST_FLAKES.md']).decode().splitlines(), flakes) for tree in (O, T))
shared_reports = [name for name in O.keys() & T.keys() if name.startswith('docs/reports/') and name.endswith('.md') and name not in B]
assert all((ROOT / name).read_bytes() == objects.read(O[name]) for name in shared_reports)

# Check the two git-invoking guards' hygiene policies using parent path manifests
# plus the incoming/edited worktree. These are supplementary audits, not claims
# that the excluded .NET methods executed.
paths = O.keys() | T.keys()
model = {'.fbx', '.fbm', '.obj', '.glb', '.gltf', '.blend', '.mtl'}
media = model | {'.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.tga', '.mp4', '.webm'}
manifest_documents = {
    'src/Allegiance.ClientCore.MonoGame/Content/Models/MODELS_MANIFEST_P287.json',
    'src/Allegiance.ClientCore.MonoGame/Content/Models/MODELS_MANIFEST_P287.md',
}
forbidden_locations = ('src/Allegiance.ClientCore.MonoGame/Content/Models/',
                       'src/Allegiance.ClientCore.MonoGame/Content/Textures/ships/', 'assets/ships/thumbs/')
media_offenders = [name for name in paths if name not in manifest_documents and (ROOT / name).is_file() and (
    name.startswith(forbidden_locations) or Path(name).suffix.lower() in model or
    (Path(name).suffix.lower() in media and 'meshy' in name.lower() and
     not name.startswith(('docs/meshy_hints/', 'tools/meshy-repair/'))))]
inventory = ROOT / 'docs/release/media-inventory.csv'
if inventory.exists():
    for row in csv.reader(io.StringIO(inventory.read_text())):
        if len(row) >= 3 and row[2].strip() == 'a' and row[0].strip() in paths:
            media_offenders.append(row[0].strip())
binary = {'.dll', '.exe', '.png', '.jpg', '.jpeg', '.gif', '.ico', '.woff2', '.ogg', '.wav', '.mp3', '.ttf', '.mgfxo', '.dat', '.bin', '.pdb', '.cov'}
private_offenders = [name for name in paths if name.split('/')[0] in ('src', 'tests', 'scripts', 'tools', 'docker', '.github')
    and Path(name).suffix.lower() not in binary and (ROOT / name).is_file()
    and b'/home/' + b'domingo' in (ROOT / name).read_bytes()]
assert not media_offenders and not private_offenders, (media_offenders, private_offenders)
(OUT / 'hygiene-audit.json').write_text(json.dumps(dict(parent_manifest_paths=len(paths),
    supplementary_file_audit_only=True, media_offenders=media_offenders, private_path_offenders=private_offenders,
    excluded_manifest_documents=sorted(manifest_documents),
    excluded_git_invoking_tests=['MeshyGuardTests', 'PrivatePathGuardTests']), indent=2) + '\n')

audit = dict(merge_target='codex/p734b', conflicted_files=json.loads((OUT / 'index-stages.json').read_text()),
    metadata_unchanged=True, conflict_markers=markers, matrices=matrix_audit,
    unique_sound_event_playback_pairs=len(current_kinds), master_source_files_changed=changed,
    no_source_changes_outside_incoming_scope=True, both_flake_histories_preserved=True,
    master_versions_of_both_added_reports_preserved=shared_reports)
(OUT / 'resolution-audit.json').write_text(json.dumps(audit, indent=2) + '\n')
print(json.dumps({k: audit[k] for k in ('metadata_unchanged', 'unique_sound_event_playback_pairs',
    'no_source_changes_outside_incoming_scope', 'both_flake_histories_preserved')}, indent=2))
