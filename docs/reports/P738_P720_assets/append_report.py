#!/usr/bin/env python3
"""Append the completed P738 gate to the incoming wave's report."""
from pathlib import Path
import json
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
summary=json.loads((OUT/'summary.json').read_text());audit=json.loads((OUT/'resolution-audit.json').read_text())
focused=summary['phases']['focused'];showcase=summary['phases']['showcase']
text='''

## P738 merge onto master

Resolved the file contents for incoming `codex/p720` onto `integ/batch2`, after
P726, P732 and P718 in this merge chain. No git commands, commits or pushes
were run. The index and merge metadata remain for the dispatcher to record.

Only `docs/parity/parity-matrix.csv` (ten blocks) and `docs/TEST_FLAKES.md`
(one block) were conflicted. Source and test files had merged automatically;
reviewed their resulting inventory, input and wire changes against master.

- **Parity:** compared all 438 rows against the base, master and incoming blobs
  through read-only object access. All 32 differing rows were one-sided:
  retained 18 master-only FEDMSG/screen/lobby changes and P720's 14 newly
  completed swap commands. No row changed independently on both sides.
  The final matrix differs semantically from master in exactly those fourteen
  command status/evidence rows, adding weight 28 without changing eligibility.
  Both parity matrices and `fedmsg-wiring.csv` use LF (zero CR bytes).
- **Flakes:** unioned the entire master chain history with P720's incoming
  observation. Neither side's observation was discarded.
- **Sounds and reports:** read P710/P711/P714/P716/P717/P719/P721/P723/P725 and
  the incoming P720 report. Master `Game1.Audio.cs`, `ClientSoundEvent.cs`,
  `SoundManager.cs`, the simulation matrix and all nine sound-wave reports
  are byte-identical to master. All 37 event kinds retain exactly one playback
  case. Existing P719 command cue sites in `Game1.FlightParts.cs` remain intact.
- **P720 intent:** retained all fourteen discoverable/rebindable actions,
  cargo/socket selection, ordered swap/reload requests, authoritative echo
  inventory updates and the existing successful-reload counter. Retained the
  native ten-byte SWAP_PART layout; native definitions are
  `~/Development/Allegiance/src/Inc/Messages.h:273-278` and
  `~/Development/Allegiance/src/Inc/messagecore.h:43,59,77-87`.
  Selection and reload rules remain those cited in this report at
  `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:9075-9150`,
  `~/Development/Allegiance/src/WinTrek/trekigc.cpp:2603-2719` and
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:1430-1566`.

The recorded [scorecard](P738_P720_assets/scorecard.txt) validates **438 interface
rows / 473 simulation rows**:

| Area | Weighted completion | Completed / eligible |
| --- | --- | --- |
| commands | 561/591 (94.92%) | 176/190 |
| commander | 81/81 (100%) | 19/19 |
| fedmsg | 475/515 (92.23%) | 115/125 |
| lobby | 39/51 (76.47%) | 9/12 |
| parts | 135/153 (88.24%) | 34/40 |
| screens | 79/88 (89.77%) | 19/23 |
| **Interface total** | **1370/1479 (92.63%)** | **372/409 (90.95%)** |
| **Simulation total** | **896/1331 (67.32%)** | **279/436 (63.99%)** |

Whole-solution Release build (`dotnet build AllegianceCore.slnx -c Release
--nologo`) passed with **0 warnings / 0 errors**, in 13.60 seconds.
'''
text+=f"\nThe focused cohort contains **{summary['focused_methods']} methods**, each run in three\nfresh exact-method processes: P720's 78 affected methods, the parity validator's\nthree methods and P719's loadout sound asset test. Raw focused results:\n**{focused['passed']} passed / {focused['failed']} failed / {focused['skipped']} skipped** across\n**{focused['invocations']} invocations**. The standalone never-parks gate passed\n**{showcase['passed']}/5**, so no base-rate comparison was needed.\n"
text+='''
After the focused and showcase gates, Core, ClientCore and MonoGame each ran
once, serially, with ordinary live/loopback and LiveTiming cases included.
The local P660 showcase opt-in was enabled. Two Core guard classes that invoke
`git ls-files` were excluded to honor the explicit no-git instruction; no
live/loopback class was filtered out. No remote rig/221 or 200v200 run was made.

| Broad Release pass | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
'''
for run in summary['suites']:
 c=run['counters'];text+=f"| {run['label'][6:]} | {c['passed']} | {c['failed']} | {len(run['skips'])} |\n"
if summary['failure_classifications']:
 text+='\nEvery failing case qualified as a load flake after **3/3 fresh post-suite\nisolated passes** with unchanged assertions and deadlines:\n\n'
 for f in summary['failure_classifications']:text+=f"- `{f['case']}` — {f['post_suite_isolation']}.\n"
 text+='\nThese passes do not establish underlying causes or fixes. Raw failures remain\nin the ledger and TRX archive; the observations are unioned into\n`docs/TEST_FLAKES.md`.\n'
else:text+='\nAll three broad passes completed without failures.\n'
text+='''
The three MonoGame skips are the existing private Meshy-pack checks. No source, test assertion, deadline, quarantine trait, transport or
simulation change was needed for this merge resolution.

Evidence: [resolution audit](P738_P720_assets/resolution-audit.json),
[per-row three-way audit](P738_P720_assets/parity-row-audit.json),
[focused methods](P738_P720_assets/focused-methods.json),
[build metadata](P738_P720_assets/build-final.json),
[invocation ledger](P738_P720_assets/verification.json),
[summary](P738_P720_assets/summary.json),
[case outcomes](P738_P720_assets/case-results.json), and
[raw logs/TRX](P738_P720_assets/validation-logs.tar.gz).
Source and test-binary fingerprints confirm the tested build remained unchanged.
'''
path=ROOT/'docs/reports/P720_INTERFACE_WAVE.md';old=path.read_text();marker='\n\n## P738 merge onto master\n'
assert marker not in old,'P738 appendix already exists'
path.write_text(old+text)
print('Appended P738 merge gate to',path.name)
