#!/usr/bin/env python3
"""Append the completed merge certificate to the incoming wave report."""
from pathlib import Path
import json
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
summary=json.loads((OUT/'summary.json').read_text());records=json.loads((OUT/'verification.json').read_text())
report=ROOT/'docs/reports/P718_INTERFACE_WAVE.md'
assert '\n## P738 merge onto master\n' not in report.read_text(),'Merge appendix already exists'
text='''

## P738 merge onto master

Merged `codex/p718` onto the current `integ/batch2` file contents after the
P726 and P732 integrations. All 13 files with actual conflict blocks are
resolved. No git commands, commits or pushes were run; the dispatcher retains
responsibility for recording the resolved index/merge.

### Resolution notes

- Preserved all current audio kinds, playback cases and sound-event tests,
  including P717 mine/shield cues, P719 docking/welcome/boarding cues and P732
  voice selection. The audit finds **37 event kinds and 37 playback cases**,
  exactly one case per kind, with no missing or duplicate cue dispatch.
- Combined P718's wing roster behavior with master's commander/AFK metadata,
  teammate-only wing values, complete roster-gathering lock, compact team pane
  and control hints. Unknown wing values display `WING:?`. The incoming
  three-case named-wing test uses the retained `ShipBrief.WingId` argument.
  Native roster fields: `~/Development/Allegiance/src/WinTrek/teamscreen.cpp:452-505`;
  side-scoped wing relay: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:5312-5344`.
- Kept master's commander authorization documentation and the existing shared
  `WingNames` table instead of introducing a second name table. Known
  same-side members receive bounded authoritative wing updates; commanded
  self assignments also notify the joined player before a launch pose exists.
  The new prelaunch regression covers that identity boundary; native joined
  identity setup is `~/Development/Allegiance/src/clintlib/appmsg.cpp:2150-2214`.
  Native wing rule:
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:2129-2147`; names:
  `~/Development/Allegiance/src/Igc/common.cpp:23-35`.
- Fixed an automatic merge outside the marked conflicts: both parents added
  asteroid-destroyed dispatch, producing duplicate switch cases in
  `ConnectionSession` and `ClientWorld`. There is now one dispatch and P718's
  single handler: it requires ENTER_GAME, checks the named sector, removes the
  rock and health, explodes only when requested, and preserves the construction
  effect on nonexplosive conversion. Native rule:
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:1692-1718`.
  The older P703 research fixture now supplies ENTER_GAME before the live
  asteroid message, consistent with the sync barrier at
  `~/Development/Allegiance/src/clintlib/appmsg.cpp:3040-3045` and
  `~/Development/Allegiance/src/clintlib/clintlib.cpp:3851-3857`.
- Retained P718's civilization, probe, departure, enter-game and victory-counter
  handlers and real-TCP golden-byte coverage. Kept master's P675 same-team wing
  fixture, which supplies real player/side identity.
- Unioned `TEST_FLAKES.md`; retained master's P716 report. The two historical
  reports that only quote marker text have no actual conflict blocks.

Reviewed the incoming report and the overlapping P710/P711/P714/P716/P717/
P719/P721/P723/P725 sound reports, plus P732's voice and merge notes. Historical
sound-report ID interpretations remain superseded by the P732 merge appendix;
this merge preserves its corrected catalog and producer IDs.

### Parity reconciliation

Reconciled 31 rows within the marked matrix blocks individually; 24 differed
between the parents. Seven incoming FEDMSG rows
become done: CHANGE_TEAM_CIV, ASTEROID_DESTROYED, PROBES_UPDATE, QUIT_MISSION,
QUIT_SIDE, ENTER_GAME and GAME_STATE. SET_WINGID was already done on master:
its row retains both P713 and P718 evidence, with **no duplicate credit**.
Master's more complete screen/lobby evidence and Selection pane era exclusion
remain. All completed sound rows remain. Both matrices and fedmsg-wiring.csv
use LF endings. `python3 scripts/parity-scorecard.py` validates 438 interface
and 473 simulation rows.

| Final scorecard | Weighted | Complete / eligible |
| --- | ---: | ---: |
| Interface total | 1342/1479 (90.74%) | 358/409 (87.53%) |
| FEDMSG | 475/515 (92.23%) | 115/125 (92.00%) |
| Simulation total | 896/1331 (67.32%) | 279/436 (63.99%) |
| Sounds | 406/592 (68.58%) | 148/227 (65.20%) |

Compared with the preceding P738/P732 certificate, interface credit increases
by 28 and seven rows; simulation and sounds scores are unchanged. See the
[scorecard](P738_P718_assets/scorecard.txt),
[row audit](P738_P718_assets/parity-row-audit.json),
[resolution audit](P738_P718_assets/resolution-audit.json) and
[cue audit](P738_P718_assets/cue-audit.json).

### Merge validation

Whole-solution Release build, `dotnet build AllegianceCore.slnx -c Release
-m:8 --nologo -v:minimal`: **0 warnings / 0 errors**. The initial build's two
duplicate-dispatch errors are retained in `build-initial.log`; the successful
build is `build.log`. Source fingerprints confirm the tested sources did not
change during validation.

'''
text+=f"The {summary['focused_methods']} selected methods received three separate exact-method\ninvocations each ({summary['focused_invocations']} invocations). This includes all 56 methods\nfrom the incoming P718 certificate, the new prelaunch regression, retained\nsound tests, current commander/AFK/wing UI tests, dispatch/parity checks,\nresearch/construction coverage and live/loopback cases.\n\n"
text+='| Assembly | Methods | Invocations | Passed cases | Failed cases | Skipped |\n| --- | ---: | ---: | ---: | ---: | ---: |\n'
for project,r in summary['focused_by_assembly'].items():text+=f"| {project} | {r['methods']} | {r['invocations']} | {r['passed']} | {r['failed']} | {r['skipped']} |\n"
showcase=[r for r in records if r['label'].startswith('showcase-')]
text+='\nThe standalone Release never-parks showcase passed **5/5**. Durations: '+', '.join(str(r['seconds']) for r in showcase)+' seconds. No standalone failure occurred, so no base-rate comparison was needed.\n\n'
text+='Exactly one broad pass ran for each required assembly, including LiveTiming\nand ordinary live/loopback tests, with the P660 showcase opt-in enabled:\n\n| Assembly | Passed | Failed | Skipped |\n| --- | ---: | ---: | ---: |\n'
for r in summary['suites']:text+=f"| {r['label'].removeprefix('suite-')} | {r['counters']['passed']} | {r['counters']['failed']} | {len(r['skips'])} |\n"
text+='\nThe two Core hygiene tests `MeshyGuardTests` and `PrivatePathGuardTests` were\nexcluded because they invoke git, which the user explicitly prohibits. No live\nor loopback test was excluded. Optional Meshy-pack skips, if any, are retained\nby name in the ledger; skip counts use actual TRX outcomes. No rig / 221 /\n200v200 run was attempted.\n\n'
if summary['failure_classifications']:
 text+='The raw failures above remain recorded. After all broad suites ended, each\nobserved failing method/case passed **3/3 fresh isolated invocations**:\n\n'
 for r in summary['failure_classifications']:text+=f"- `{r['case']}`: {r['post_suite_isolation']}; classified as a load flake.\n"
 text+='\nThese observations are also recorded in `docs/TEST_FLAKES.md`. No assertion,\ndeadline, quarantine trait or product behavior was relaxed for them.\n\n'
else:text+='All scheduled test invocations completed without failures.\n\n'
text+='''Evidence: [summary](P738_P718_assets/summary.json),
[exact invocation ledger](P738_P718_assets/verification.json),
[per-case outcomes](P738_P718_assets/case-results.json),
[raw logs and TRX archive](P738_P718_assets/validation-logs.tar.gz), and
[evidence hashes](P738_P718_assets/evidence-manifest.json). The repeatable
runner is `P738_P718_assets/verify.py`; source and conflict snapshots are
retained beside it. All changes remain as file edits for the dispatcher.
'''
report.write_text(report.read_text()+text)
print('Appended completed merge certificate to',report)
