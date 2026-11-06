# P934 — local training enemy visibility and support audit

Implemented TRN-0189 and corrected/certified TRN-0215. Only these two matrix
rows changed. No commits, pushes or remote-host access. The port uses the
embedded host's real IGC state; both HUD styles receive its ordinary own-ship
status over loopback.

| Row | Verdict | Native | Production port | Regression |
| --- | --- | --- | --- | --- |
| TRN-0189 | Implemented | `WinTrek/trekigc.cpp:1996-2010,2172-2249` | `src/Allegiance.Core/Sim/SideVisibilityState.cs:63`; `src/Allegiance.Core/Model/Sector.TrainingVisibility.cs:26`; training branch in `Sector.ResolveCrossSideVisibility` | `P934TrainingVisibilityTests`; `P934TrainingVisibilityBindingTests` (Classic/Modern) |
| TRN-0215 | **Corrected row; implemented; requires Claude's corrected-row review** | `Igc/shipIGC.cpp:1730-1747,1934-1944,2668-2677`; `Igc/shipIGC.h:2436-2459` | `src/Allegiance.Core/Sim/Ship.cs:7797` (`DecideWingmanDefaultOrder`), `:8017` (`UpdateWingmanRepair`), `:9039` (nanite retreat exemption) | `P934TrainingSupportTests`, six M6/M8/M10 parameter cases plus lost-target/no-enemy recovery |

All native citations were re-read/re-grepped against the read-only native
source for this task.

TRN-0189 keeps two local ThingSite slots. Every side other than the trainee's
reads and explicitly writes the same enemy slot, including arbitrary side
reads. Predictable objects initialize both visibility/eye pairs at cluster
binding (`trekigc.cpp:1996-2010`); subsequent updates retain known visibility
while the current eye decays (`:2167-2169,2195-2197`). Explicit
`SetSideVisibility(false)` clears both flags even for predictable objects
(`:2238-2249`). Non-predictable ship contact decays with its eye.

The enemy update trivially sees a model whose side differs from the player,
otherwise it tries the retained spotter before the enemy scanner pool
(`:2172-2197`). It preserves native's unusual lack of a side check on cached
enemy spotters and retains their identity through loss; retirement plus ID
reuse cannot substitute a new object for the old pointer. Allied scanners
enter the player pool even with allied visualization off (`:283-314`). Ship
scanner geometry includes the native quarter rear range
(`Igc/shipIGC.h:341-376`). The multiplayer branch and its independent side
state are pinned by a regression.

The eleven Core cases cover three sides, arbitrary reads, explicit writes,
predictable retention/clear, real station initialization, loss and reacquisition,
cached-spotter side changes, ID reuse, duplicate sector membership, allied scanner pools, rear range and
multiplayer independence. The two style cases observe the real loopback
client's own-ship `Detected` bit rise and fall with the combined enemy eye.

**TRN-0215's original claim was wrong.** Native `shipIGC.cpp:1883` is a debug
comment naming “Enemy Support” inside generic retreat. The executable wingman
branch at `:1730-1747` explicitly exempts a negative-power weapon in mount 0
and an accepted station target. Neither absence of a repair target nor a fully
healed target creates a restart-base order. A healed ship target merely suppresses
fire (`:2668-2677`); it remains legal (`shipIGC.h:1680-1686`).

The default-plan recovery at `shipIGC.cpp:1934-1944` calls `PickDefaultOrder`
when neither plan nor accepted order is legal. Its wingman branch asks the
training hook, then accepts the nearest enemy attack (`shipIGC.h:2436-2459`).
The miner-only take-cover block at `shipIGC.h:2227-2244` does not apply to
nanite wingmen.

The lesson audit found the following:

- M6 and M10 share the same default policy (`training/mission6.cpp:82-287`,
  `training/mission10.cpp:78-283`): attack/assistance/follow/patrol, with no
  nanite-specific restart-base fallback. “Support ship” selection in these
  policies actually requires **positive** weapon power (`mission6.cpp:101-107`,
  `mission10.cpp:97-103`). Their nanite recipes explicitly assign bomber repair
  (`mission6.cpp:710-711,845,923-924`; `mission10.cpp:706-707,841,919-920`).
- M8 explicitly assigns its nanite to repair the bomber
  (`training/mission8.cpp:606-607`). The ending script idles the enemy defenders
  (`:629-638`); it does not establish a general support flee rule.

The existing retreat exemption and full-health repair retention needed no
production rewrite. A real missing binding was found in the ordinary training
wingman fallback used by M8: it selected the execution target without writing
the native accepted attack slot. The fix calls the existing training
`SetCommand(CmdAccepted, enemy, CidAttack)` path; M6/M10 already did that in
their hook. With no hook order and no enemy, training now clears the stale
accepted slot to None (`shipIGC.h:2458-2459`), including after losing a repair
target. The seventh support case pins this path and the absence of a base
return. Multiplayer retains its existing fallback.

Development red/green evidence:

- Original visibility implementation: **6 failed / 2 passed / 0 skipped**
  (`.scratch/p934/red/visibility-before.trx`). After the port the original eight
  passed; the final expanded eleven-case suite also passed, zero skips.
- Before the accepted-command fix: support **1 failed / 5 passed / 0 skipped**
  (`.scratch/p934/green/support-confirm.trx`). The M8 assertion expected accepted
  Attack (1) and observed None (-1). After the fix: **6 passed / 0 skipped**
  (`.scratch/p934/green/support-after.trx`). This is an executable production
  correction, not credit earned by rewriting a row.
- Both-style loopback binding checks: **2 passed / 0 skipped**
  (`.scratch/p934/green/binding-after.trx`).

| Scorecard | Before | After | Change |
| --- | --- | --- | --- |
| Training weighted | 807/901 (89.57%) | 815/901 (90.46%) | +8 points |
| Training complete/eligible | 206/235 (87.66%) | 208/235 (88.51%) | +2 rows |
| Training integration weighted | 301/326 (92.33%) | 309/326 (94.79%) | +8 points |

Re-measured by Claude after merging onto master bf1978d7 (P932+P933): training **828/901 (91.90%)** before, **836/901 (92.79%), 213/235 (90.64%)** after — the same +8 points.

`python3 scripts/parity-scorecard.py` validates every matrix before and after;
receipts are `.scratch/p934/scorecard-before.txt` and
`.scratch/p934/scorecard-after.txt`. No row in this brief remains partial.
No new service, port, store, environment variable or deployment option was added.

Initial focused qualification: **42 isolated invocations, 57 passed cases,
0 failures, 0 skipped**. At that stage each of the fourteen new methods ran three times on
its own, including both styles of the loopback binding test. Receipt:
`.scratch/p934/isolated-initial/summary.json`; initial source hashes:
`.scratch/p934/source-manifest-initial.json`.

The real shipping window was captured in both styles with
`tools/p934-training-proof/`. All four images were opened and visually
reviewed: Classic has a yellow/red eye icon below-right of the central gauges
while eyed, absent in the clear capture. Modern has an “EYE” label below the
fuel gauge while eyed, absent in the clear capture. Both pairs show the actual
Neptune training cockpit and native station artwork. The harness asserts the
combined side-2/side-99 latch and the replicated own-ship `Detected` bit before
each capture; it does not override that bit or draw a proof overlay.

- [Classic eyed](P934_assets/p934-classic-eyed.png),
  [Classic clear](P934_assets/p934-classic-clear.png).
- [Modern eyed](P934_assets/p934-modern-eyed.png),
  [Modern clear](P934_assets/p934-modern-clear.png).

Screenshot receipts: `.scratch/p934/windows/`; the four report images total
less than 2 MB. No rendering code or native 2-D conventions changed.

Completed build/static checks on final production sources:

- Debug solution build: **PASS, 0 warnings / 0 errors**
  (`.scratch/p934/build-debug-qualification.log`).
- `tools/build-tools.sh`: **PASS, all 117 projects**, zero warnings/errors
  (`.scratch/p934/build-tools-qualification.log`).
- Native AOT server publish, Release/linux-x64/self-contained: **PASS**, no
  IL2026, IL3050 or other warnings (`.scratch/p934/aot-qualification.log`).
- Native training inventory audit: **241 rules / 82 primitive types / 0 errors**
  (`.scratch/p934/training-audit.log`). Glossary links and `git diff --check` pass.


The **single** Fast-tier gate completed: **13,174 cases, 13,158 passed,
13 failed, 3 skipped**, plus nine passing infrastructure checks. All P934 cases present at that sweep passed. The sweep precedes the final
training-only duplicate-scanner guard; the final isolated/training checks cover
that correction and its added regression. The three skips are the optional Meshy pack. The gate stays
failed/qualified: unrelated profile/bundle fixtures, missile feedback,
side-seating and NPC timing failures are ledgered with new three-run isolation
in [TEST_FLAKES.md](../TEST_FLAKES.md#2026-11-06--p934-fast-qualification-local-training-visibilitysupport).

Isolation totals are in `.scratch/p934/fast-isolation/summary.json` (39 exact-
method invocations). Bundle-path and defender failures persist as previously
ledgered. Missile lock also fails **3/3 with the unchanged dispatch Core**, with
the same full-lock/missing-partial-tip assertion at line 150; dispatch receipt:
`.scratch/p934/base-missile/summary.json`. All remaining failed/timing methods
pass 3/3 alone; the two UI fixtures use a writable task-local XDG directory.
Raw sweep receipt: `.scratch/p934/fast/summary.json`. No second Fast sweep ran.


The first artwork training attempt found one P934 failure: the legacy M4
scripted graph can retain duplicate sector entries for one ship. The new
scanner index initially used `Dictionary.Add`, which threw on that identity.
Headless result at discovery: **108 passed / 1 failed / 0 skipped**; the
outer training harness was interrupted; its MonoGame leg is not used as
final-source evidence. Receipts:
`.scratch/p934/training-initial.log` and `.scratch/p934/training/`.

The scanner pool now admits each object identity once (`TryAdd`), matching
native membership replacement (`Igc/shipIGC.h:200-227`) while tolerating the
managed legacy fixture's bookkeeping copies. The new
`Repeated_sector_membership_registers_each_scanner_identity_once` regression
first failed with the same duplicate-key exception
(`.scratch/p934/red/duplicate-before.trx`). This is a task correction, not an
unrelated failure ledger exemption.


Final-source focused qualification after the correction: **48 standalone
invocations / 63 passed cases / 0 failures / 0 skipped**. Every one of the
fifteen new methods ran 3× alone (60 cases), and the affected legacy M4
completion method ran 3× alone (three more cases). This includes both HUD
styles and the failing-then-passing duplicate-membership regression. Receipt:
`.scratch/p934/isolated/summary.json`; final source hashes:
`.scratch/p934/source-manifest.json`. The Debug solution, all 117 tools and
native AOT publish were rebuilt successfully after this correction; the
qualification logs above are those final builds.


The planned interleaved showcase controls completed: **dispatch 5/5 passed,
P934 4/5 passed overall**, zero skips. Every run satisfied the five-second
parking checks: maximum observed target stop was **2.005 s dispatch / 2.007 s
P934**. P934 run 4 failed the separate arrival-coverage assertion after 480 s
(18/30 reached, required 24/30); its maximum target stop was 1.904 s and it
observed real combat. No assertion or time limit was changed.

Controls use the prescribed immutable snapshot/encode-once, parallel build,
pool transport, native ship-update/native GotoPlan and server-GC baseline.
Trials select one test per process and interleave original/current Core builds;
load and binary hashes are recorded. They overlap the real lesson qualification
on the shared host. Elapsed times include scouting and varying discovery paths;
these receipts certify the behavioral criteria at the recorded load.
Raw results: `.scratch/p934/showcase/summary.json`; original/current Core hashes:
`.scratch/p934/showcase-binaries.json`; dispatch source commit:
`.scratch/p934/base-head.txt`.


The arrival-coverage failure's three fresh follow-ups all passed: **3/3, zero
skips**, with scenario durations 401.0 / 171.8 / 473.9 s and maximum target
stops 1.906 / 1.003 / 2.014 s. The original failed trial remains in the report
and failure ledger. Combined outcomes: **dispatch 5/5; P934 7/8 overall**;
the five-second parking checks pass **13/13**, with a maximum observed stop of
2.014 s. Receipt: `.scratch/p934/showcase-followup/summary.json`.


The complete final-source artwork training command exited **0**:
`tools/run-training-tests.sh --no-build --repeat 1`, with `local.env`
artwork configuration loaded after the final Debug build. Headless:
**109 passed / 0 failed / 0 skipped**. MonoGame: **255 passed / 0 failed /
2 skipped**, 257 total, 2 h 25 m. Combined: **364 passed / 0 failed /
2 skipped**, 366 total. The two skips are Classic/Modern M4 hunt branch
proofs that explicitly require `ALLEGIANCE_TRAINING_BRANCH_PROOFS=1`;
neither is an artwork skip. Native Nanite full completion, combined
bomber/outpost loss and early mounting all passed in both styles, including
the Modern loss-policy full run and the assigned lesson prefixes.
Raw receipts: `.scratch/p934/training-final.log` and
`.scratch/p934/training-final/1/`. The TRX counter attributes report
`notExecuted=0` despite two `NotExecuted` test results; the skip total here
uses those individual results and matches the runner summary.

[Compact verification summary](P934_assets/verification.json) records
the final source hashes, builds, exact gate counts, load and all thirteen
showcase receipts. The four screenshots plus this summary remain below
the 2 MB evidence limit.


Raw receipts, command drivers, baseline-source extracts and manifests remain
in the report-linked `.scratch/p934/` directory. The managed workspace does
not permit writing the external evidence directory specified by the lane
rules. The existing real-window harness writes its detailed lesson logs to
`.scratch/p931/windows/` and buoy-window logs to `.scratch/p918/windows/`;
those local receipts are retained too. Disposable
task build/cache artifacts are removed after qualification. Only the four
reviewed screenshots and the compact verification summary belong in the
report assets; new media still needs Domingo's review before any future push.


Native intent: the combined enemy slot is a **design** shortcut for a local
training client, including its imperfect multi-side/allied interpretation.
Independent side visibility would be a better modern general-purpose model,
but changes the native training rules. The nanite retreat exemption is an
intentional **design** choice; the claimed unconditional support flee rule was
an audit error, not a native bug or era constraint. Writing the accepted attack
slot fixes a port binding bug and uses the existing command-ownership design.
For a modern standalone support AI, explicit repair-target reacquisition and
survivability rules would be clearer than the generic attack fallback; that
would change the native lesson behavior.
