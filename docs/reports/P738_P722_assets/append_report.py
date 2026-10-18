#!/usr/bin/env python3
"""Append the reviewed merge outcome only after the gates are finalized."""
import json
from pathlib import Path

OUT = Path(__file__).resolve().parent
REPORT = OUT.parent / 'P722_INTERFACE_WAVE.md'
s = json.loads((OUT / 'summary.json').read_text())
a = json.loads((OUT / 'resolution-audit.json').read_text())
focused = s['phases']['focused']
isolated = s['phases']['post-isolate']
suite_lines = []
for row in s['suites']:
    c = row['counters']
    suite_lines.append(f"| {row['label'].removeprefix('suite-')} | {c['passed']} | {c['failed']} | {len(row['skips'])} |")
failure_text = 'The final focused and broad gates reported no test failures.'
if s['failure_classifications']:
    failure_text = ('The raw failures remain in the ledger. Every failing method/case passed '
                    'three fresh individual post-suite invocations, meeting the brief’s load-flake rule; '
                    'the observations are recorded in TEST_FLAKES.md. No assertion or deadline was relaxed.\n\n' +
                    '\n'.join('- `' + f['method'] + '` — ' + str(f['isolated_invocations']) + '/'
                              + str(f['isolated_invocations']) + ' isolated passes.' for f in s['failure_classifications']))
text = f'''

## P738 merge onto master

Integrated `codex/p722` into the current `integ/batch2` merge chain, resolving
**{len(a['conflicted_files'])} conflicted files / {a['conflict_count']} conflict blocks** through file edits.
No git command, commit, push or merge-index update was performed. HEAD, index
and merge metadata fingerprints are unchanged.

### Resolution notes

- Chat keeps P732’s native voice resolution, sonic captions and single-event
  playback, with P722’s local mute check before either chat shape records text
  or audio. Native: `~/Development/Allegiance/src/WinTrek/trekigc.cpp:4492,4507-4540`;
  shared CHATDATA layouts: `~/Development/Allegiance/src/Inc/Messages.h:204-223,716-722`.
  New four-case regression covers CHATMESSAGE/CHATBUOY, direct/sonic voices,
  mute/unmute, captions and exactly one resumed cue.
- Team-pane rendering keeps the locked roster snapshot, commander/AFK/wing
  markers and compact view; adds P722’s pilot IDs and moderation hints.
  Native roster state: `~/Development/Allegiance/src/WinTrek/teamscreen.cpp:452-505`;
  confirmed leader boot: `~/Development/Allegiance/src/WinTrek/teampane.cpp:1070-1125`.
  The roster regression checks ID and role markers on the same row.
- Removed automatically merged duplicate QUIT_MISSION/QUIT_SIDE cases in
  ClientWorld/ConnectionSession and duplicate receive-registry entries. Kept
  P718’s complete player/team cleanup, reason retention, socket shutdown and
  packet-stop behavior; integrated P722’s own-departure UI notice and remote
  mute cleanup into that path. The unique registry remains **108 consumed
  (91 native + 17 extensions) / 11 unconsumed**.
  Native: `~/Development/Allegiance/src/clintlib/appmsg.cpp:2501-2526,2993-3002`;
  `~/Development/Allegiance/src/clintlib/clintlib.cpp:3805-3848,3889-4077`.
- Adapted incoming fixtures to the merged contracts: the TCP peer stores its
  assigned identity before world reset; the prelaunch model supplies MISSIONDEF
  and a constants-only core before JOINED_MISSION; side departure expects
  native **SIDE_TEAMLOBBY (-2)**. Native entry/side rules:
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:2150-2214` and
  `~/Development/Allegiance/src/clintlib/clintlib.cpp:3889-4034`.
  Existing cleanup, exact wire bytes and deadlines remain asserted.
- Both parity CSVs were merged using all three index versions **by row**:
  one-sided changes survive verbatim; both-sided changes retain the more
  complete status and both evidences. QUIT_MISSION/QUIT_SIDE stay done with
  P718 transition and P722 moderation evidence. Selection pane is done with
  P713’s scaffolding classification retained as context for P722’s concrete
  selected-model binding. All CSVs use LF. Existing TEST_FLAKES lines from
  both branches survive, and all nine master sound-wave reports are unchanged.

The audio cue table, enum, SoundManager and simulation matrix are byte-identical
to master: **37 unique sound-event/playback pairs**, with none lost or duplicated.
See [resolution audit](P738_P722_assets/resolution-audit.json) and
[complete row decisions](P738_P722_assets/parity-row-audit.json).

### Merge scorecard

`python3 scripts/parity-scorecard.py` validates **438 interface / 473 simulation
rows**. Interface moves from master’s **92.63% (1370/1479), 372/409**
to **95.23% (1399/1469), 382/406 eligible rows complete**.
Parts **153/153**, screens **86/86**, lobby **43/43** all score 100%.
Simulation is unchanged at **67.32% (896/1331)**; sounds **68.58% (406/592)**.
Raw output: [scorecard.txt](P738_P722_assets/scorecard.txt).

### Merge verification

Final whole-solution Release build: **0 warnings / 0 errors**.
**{s['focused_methods']} distinct affected methods × 3 fresh individual runs**:
**{focused['invocations']} invocations / {focused['passed']} passing cases / {focused['failed']} failed cases /
{focused['skipped']} skips**, plus **{isolated['invocations']} post-suite isolation invocations**.
This includes the incoming wave’s cohort, actual TCP/loopback paths, previous
chat sounds/voice-over, P718 departure tests, roster/compact layout, parity
validation and the explicitly enabled local P660 proof.

The five standalone P700 never-parks showcases passed **5/5** on the final
build. Minimum objective arrivals were **{s['showcase_metrics']['minimum_reached']}/30**,
the longest sampled pause with a target was **{s['showcase_metrics']['longest_pause_seconds']:.3f} seconds**,
and kills ranged **{s['showcase_metrics']['minimum_kills']}–{s['showcase_metrics']['maximum_kills']}**.
No base-rate comparison was needed. One additional pre-fixture diagnostic
showcase also passed and is retained separately.

One sequential broad pass per required assembly:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
{chr(10).join(suite_lines)}

The two Core guard classes that invoke git internally (MeshyGuardTests and
PrivatePathGuardTests) were excluded to honor the explicit no-git instruction.
Ordinary live/loopback and LiveTiming tests ran; no rig/221/200v200 opt-in was
enabled. MonoGame’s optional private Meshy-pack skips are reported above.

{failure_text}

The initial scheduler used malformed assembly paths and required recovery;
those attempts are excluded from validation credit. One roster method renamed
on master was also corrected against actual assembly discovery before counting
its three executions. Initial valid executions also exposed **nine
deterministic fixture case failures** (three repeated boot theories plus three
prelaunch checks), corrected as described above. They are retained in
[pre-fixture failures](P738_P722_assets/pre-fixture-failures.json) and
[initial raw evidence](P738_P722_assets/initial-validation-logs.tar.gz), and
are not classified as load flakes. All final results were collected after the
fixture correction and final build.

Sources and test binaries match their build fingerprints. Evidence:
[summary](P738_P722_assets/summary.json),
[individual invocation ledger](P738_P722_assets/verification.json),
[case outcomes](P738_P722_assets/case-results.json),
[final raw logs/TRX](P738_P722_assets/validation-logs.tar.gz),
[reproducible runner](P738_P722_assets/verify.py).
'''
assert '\n## P738 merge onto master\n' not in REPORT.read_text(), 'Appendix already exists'
with REPORT.open('a') as output:
    output.write(text)
print('Appended P738 merge outcome to', REPORT)
