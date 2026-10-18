#!/usr/bin/env python3
"""Audit P722 integration from read-only object data and working files."""
import csv
import hashlib
import io
import json
from pathlib import Path
import re
import runpy
import sys

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(OUT.parent / 'P738_P720_assets'))
from object_reader import ObjectReader

meta = ROOT / '.git'
gitdir = (ROOT / meta.read_text().strip().split(': ', 1)[1]).resolve() if meta.is_file() else meta
common = (gitdir / (gitdir / 'commondir').read_text().strip()).resolve() if (gitdir / 'commondir').exists() else gitdir
objects = ObjectReader(common)
trees = json.loads((OUT / 'tree-blobs.json').read_text())
ours, theirs = trees['ours_files'], trees['theirs_files']
critical = ['src/Allegiance.ClientCore.Game/Game1.Audio.cs',
            'src/Allegiance.ClientCore/State/ClientSoundEvent.cs',
            'src/Allegiance.ClientCore.Game/Audio/SoundManager.cs',
            'docs/parity/sim-rules-matrix.csv',
            'tests/Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests.cs']
reports = [f'docs/reports/P{n}_PARITY_WAVE{w}.md' for n, w in
           [(710, 13), (711, 14), (714, 15), (716, 16), (717, 17), (719, 18), (721, 19), (723, 20), (725, 21)]]
for path in critical + reports:
    assert (ROOT / path).read_bytes() == objects.read(ours[path]), path
enum = (ROOT / critical[1]).read_text().split('public enum ClientSoundEventKind', 1)[1].split('}', 1)[0]
kinds = re.findall(r'^    ([A-Za-z]\w*)(?:\s*=\s*\d+)?,', enum, re.M)
cases = re.findall(r'case ClientSoundEventKind\.(\w+)\s*:', (ROOT / critical[0]).read_text())
assert len(kinds) == len(set(kinds)) and len(cases) == len(set(cases)) and set(kinds) == set(cases)
rows = json.loads((OUT / 'parity-row-audit.json').read_text())
for name in ('docs/parity/parity-matrix.csv', 'docs/parity/fedmsg-wiring.csv'):
    resolved = list(csv.DictReader(io.StringIO((ROOT / name).read_text())))
    expected = [row['resolved'] for row in rows if row['file'] == name]
    assert resolved == expected, name
    assert b'\r' not in (ROOT / name).read_bytes(), name
assert b'\r' not in (ROOT / 'docs/parity/sim-rules-matrix.csv').read_bytes()
flakes = (ROOT / 'docs/TEST_FLAKES.md').read_text()
# Every existing nonempty line survives from both branches.
for version in (ours, theirs):
    assert set(objects.read(version['docs/TEST_FLAKES.md']).decode().splitlines()) <= set(flakes.splitlines())
for top in ('src', 'tests', 'docs'):
    for path in (ROOT / top).rglob('*'):
        if not path.is_file() or path.suffix not in ('.cs', '.md', '.csv') or {'bin', 'obj'} & set(path.parts):
            continue
        assert not re.search(r'^(<<<<<<< |=======$|>>>>>>> )', path.read_text(), re.M), str(path)
score = runpy.run_path(str(ROOT / 'scripts/parity-scorecard.py'))
before = list(csv.DictReader(io.StringIO(objects.read(ours['docs/parity/parity-matrix.csv']).decode())))
after = score['read_matrix'](ROOT / 'docs/parity/parity-matrix.csv')
result = dict(merge_target='codex/p722', ours=trees['ours'], theirs=trees['theirs'],
              conflicted_files=list(json.loads((OUT / 'index-stages.json').read_text())),
              conflict_count=sum(len(v) for v in json.loads((OUT / 'conflicts-before.json').read_text()).values()),
              unchanged_master_files=critical, unchanged_sound_reports=reports,
              sound_events=kinds, playback_cases=cases, unique_complete_playback=True,
              flakes_union_preserved=True, LF_matrices=True, conflict_markers_remaining=False,
              score_before=[str(v) for v in score['score'](before)],
              score_after=[str(v) for v in score['score'](after)],
              changed_parity_rows=[r for r in rows if r['resolved'] != r['ours']],
              resolutions=[
                  'Mute both chat shapes before voice/caption/event resolution; retain native voice-over precedence and one cue.',
                  'Retain locked roster snapshot, commander/AFK/wing state and compact layout; append ship IDs and moderation hints.',
                  'Remove auto-merge duplicate departure switches and registry entries; preserve P718 full cleanup/shutdown and P722 own-departure notice/mute cleanup.',
                  'P722 side-departure fixture follows native SIDE_TEAMLOBBY=-2, preserving P718 behavior.',
                  'Selection pane now done with both P713 scaffolding classification and P722 concrete binding evidence.',
                  'QUIT_MISSION and QUIT_SIDE remain done with P718 transition evidence plus P722 moderation evidence.',
              ])
(OUT / 'resolution-audit.json').write_text(json.dumps(result, indent=2) + '\n')
print(f'Audit passed: {len(kinds)} unique event/playback pairs; {len(result["changed_parity_rows"])} matrix/wiring rows updated from master; all existing flake lines preserved.')
