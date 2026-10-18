"""Append final measured integration notes to each incoming wave report."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
s=json.loads((OUT/'summary.json').read_text());f=s['focused'];show=s['showcase']
def append(name,text):
    p=ROOT/'docs/reports'/name
    assert '\n## P738 merge onto master\n' not in p.read_text()
    with p.open('a') as stream:stream.write('\n## P738 merge onto master\n\n'+text.strip()+'\n')
append('P735_PARITY_WAVE22.md',f'''
Integrated the `master` merge target (P735–P737) into `integ/batch2` with
file edits only. Cloak-engage and part-pickup producers, event kinds, playback
cases and waves survive alongside the earlier sound waves. New eager loads use
P732's `LoadSound` registration so available native serialized templates join
the existing mutex/priority path.

The incoming P652 test's `341` assertion is superseded by the already verified
P732 native enum correction. Retained the stronger existing `DroneChatEvent`
assertions with SoundID **1342** and exactly two crowded-miner complaints.
Native `~/Development/Allegiance/src/Igc/igc.h:456-464` starts the included
sound enum after 1000; `~/Development/Allegiance/src/Igc/sounds.h:395` names
`droneTooCrowded`. The P725 delivery seam is preserved.

Solution Release build: **0 warnings, 0 errors**. Incoming/affected coverage:
**{f['methods']} methods, each in three fresh isolated processes**, with
**three passes after the last failure for every method**. Showcase: **{show['passed']}/5 passed**.
Full measured suite results, flake qualifications and the combined scorecard
are recorded in [P737's integration notes](P737_PARITY_WAVE24.md#p738-merge-onto-master)
and `P738_master_assets/summary.json`.
''')
append('P736_PARITY_WAVE23.md',f'''
Retained all six salutation kinds, their playback cases and waves, while
combining them with P718/P722's existing departure lifecycle. Removed the
automatically duplicated `QuitMission`/`QuitSide` switches and `Apply` overloads.
The cue is classified **before** side cleanup changes the player's side or
mission cleanup removes the player. `QUIT_MISSION` retains its actual reason
for both the cue and departure feedback; local departure still resets the
world, disconnects the session and stops the remaining packet. Remote cleanup
still removes roster/request/mute state. Repeated departures do not refire a
cue, covered by strengthened sound-event assertions.

Native side cleanup and salutation decisions:
`~/Development/Allegiance/src/clintlib/clintlib.cpp:3889-4034`; local mission
shutdown: `~/Development/Allegiance/src/clintlib/clintlib.cpp:3805-3848`.
The port intentionally uses the received `QUIT_MISSION` reason for its sound,
as P736 did; native's defensive fallback for a mission departure from a real
side instead supplies `QSR_Quit`
(`~/Development/Allegiance/src/clintlib/appmsg.cpp:2516-2524`).

The earlier report's "only LinkDead and SwitchingSides" blocker predates P722:
this integration also has authorized leader-boot and ordinary-quit broadcasts.
Matrix rows are preserved according to their three-way row changes, without
claiming additional newly verified completion for those older missing rows.

Release build: **0 warnings, 0 errors**. All incoming/affected methods run **3×
alone**; showcase **{show['passed']}/5**. See [P737's integration notes](P737_PARITY_WAVE24.md#p738-merge-onto-master)
for the complete measured gate and `P738_master_assets/` for raw evidence.
''')
table=[]
for label,c in s['suites'].items():
    table.append(f"| {label.removeprefix('suite-')} | {c['passed']} | {c['failed']} | {c['outcome_skipped']} |")
if s['failure_qualification']:
    failures='\n'.join(f"- `{method}`: failed once in the measured gate; **3/3 isolated passes**, recorded as a load flake in `docs/TEST_FLAKES.md`." for method in s['failure_qualification'])
else:failures='No suite or showcase failures required isolation.'
append('P737_PARITY_WAVE24.md',f'''
Merge target: **`master`**, containing P735, P736 and P737. Resolved the eight
marked blocks in five files, plus the automatically duplicated departure
handlers in `ClientWorld` and `ConnectionSession`. No git commands, staging,
commit or push; merge metadata remains byte-for-byte unchanged.

### Resolution

- **Reload:** preserve P726's counter and authoritative inventory transfer,
  while P737 records the local type-specific cue and generic reload sound
  **before** that transfer consumes/moves the source cargo. Strengthened the
  ammo test to require exactly two cues, consumed cargo, incremented counter,
  and silence on a subsequent relay with an absent source slot. Native cargo
  consumption: `~/Development/Allegiance/src/clintlib/appmsg.cpp:1468-1566`;
  own-ship reload cues:
  `~/Development/Allegiance/src/clintlib/clintlib.cpp:3216-3225,3394-3417` and
  `~/Development/Allegiance/src/Igc/shipIGC.h:605-650`.
- **Presence:** use P718/P722 cleanup and session shutdown, with P736 cue
  classification before mutation and the actual mission-departure reason.
  Strengthened tests verify both cleanup and a single cue across repeated
  departures. P736's appended notes explain the native fallback deviation and
  the now-outdated server-broadcast blocker in the original report.
- **SoundManager:** retain every cue and P732's serialized-template
  registration through `LoadSound`. Preserve the P732 native enum ID 1342 in
  the P652 test instead of reverting to the older incoming 341 assertion;
  native enum basis: `~/Development/Allegiance/src/Igc/igc.h:456-464` and
  `~/Development/Allegiance/src/Igc/sounds.h:395`.
- **Content:** union both MGCB tables and remove nine duplicate registrations.
  Audit confirms **622 unique build entries**, **51 unique event/playback
  pairs**, and all **12 incoming waves compiled** to Release `.xnb` files.
- **Documents:** compare all parity/wiring matrices by row against the common
  base. Across differing rows, 53 take ours and 31 take incoming; no row was
  independently changed on both branches. Every matrix has LF endings. All
  pre-existing nonempty flake-report lines from both sides survive. Earlier
  sound-wave reports remain unchanged; incoming reports are append-only.

### Scorecard

`scripts/parity-scorecard.py` validates both matrices; full output is
`P738_master_assets/scorecard.txt`.

| matrix/area | weighted | complete/eligible |
|---|---|---|
| parity total | **1399/1469 (95.23%)** | **382/406 (94.09%)** |
| simulation total | **935/1331 (70.25%)** | **296/436 (67.89%)** |
| sounds | **445/592 (75.17%)** | **165/227 (72.69%)** |

Before integration, ours had 406/592 sounds credit and 148/227 complete rows;
incoming had 443/592 and 164/227. The result preserves both branches' gains.

### Measured gate

- Whole-solution Release build: **0 warnings, 0 errors**.
- Incoming/affected tests: **{f['methods']} methods ×3 fresh isolated processes
  ({f['invocations']} invocations; {f['passed']} passing parameter cases,
  {f['failed']} initial timing failure, {f['skipped']} skipped; every method
  has three passes after its last failure)**. Coverage includes P735–P737
  world/assets, P652/P725 AI/wire, reload simulation/tracking, native voice
  catalog and queue, prior sound assets/alarms, P718/P722 interface receive and
  real loopbacks, flight inventory receive and parity validation.
- Showcase never-parks: **{show['passed']}/5 passed**, {show['failed']} failed,
  {show['skipped']} skipped.
- Core, ClientCore and MonoGame assemblies run once each after the build,
  including ordinary live/loopback tests and LiveTiming tests. Two repository
  guards (`MeshyGuardTests`, `PrivatePathGuardTests`) are excluded because they
  launch git, honoring the explicit prohibition. Rig-only opt-in harnesses
  remain disabled; corpus-backed tests use `local.env`. An equivalent read-only
  index scan found no private path leaks. The broad model-location guard also
  flags two manifest documents (`MODELS_MANIFEST_P287.json`/`.md`) already
  identical in the base and both heads; neither is a model output. Details:
  `P738_master_assets/hygiene-checks.json`.

| suite | passed | failed | skipped |
|---|---:|---:|---:|
{chr(10).join(table)}

{failures}

Commands, timings, counters and failures: `P738_master_assets/verification.json`;
per-case outcomes: `case-results.json`; compact results: `summary.json`.
Complete raw logs/TRX: `validation-logs.tar.gz`; original conflict inputs:
`conflict-inputs.tar.gz`. Row decisions, cue/content audits, source hashes and
merge-metadata fingerprints are retained in the same evidence directory.
''')
print('Appended measured P738 notes to P735, P736 and P737 reports.')
