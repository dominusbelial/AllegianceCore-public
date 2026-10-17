# P722 — parts, screens and lobby interface parity

P722 closes the **14 rows that were open in these three areas**: ten are
implemented/verified and four are excluded with explicit runtime/infrastructure
reasons. Parts reaches **153/153**, screens **86/86**, lobby **43/43**.
The overall interface score moves **88.39% → 91.15%**. These are practical-scope
scores, not a claim that offline tutorials, hosted game creation or Steam
moderator bans now exist.

All changes remain in the working tree. No commits or pushes were made.

## Scope and baseline

The starting checkout is `codex/p722`. The requested
`docs/reports/P713_INTERFACE_WAVE.md` and any `P7*_INTERFACE_WAVE*.md` reports
are absent in this checkout (searched the repository, including reports).
Consequently the checked-in matrix, code and native source were used as the
starting evidence. No previously completed row was reopened/reimplemented.
The brief's approximate 88.8% was not used as a baseline: the actual scorecard
at dispatch is **88.39% (1310/1482)**.

The coherent bundle is all 14 remaining rows, rather than selecting a subset:
the six ability flags feed the same selected-target/threat/lead/ripcord state;
the eight screens/lobby leftovers share client session/roster/UI state or depend
on native launch infrastructure that this client does not have. This also
corrects stale descriptions such as “capture-capable probe” and “animated intro.”

Initial open rows, ordered by weight (partial unless stated missing):

| Weight | Area | Row |
| --- | --- | --- |
| 4 | lobby | Create game / new game wizard |
| 4 | lobby | Kick / ban / mute (moderation) |
| 4 | lobby | Training missions (tutorial) |
| 3 | screens | Training screen |
| 3 | screens | Selection pane |
| 3 | parts | c_habmCaptureThreat |
| 3 | parts | c_habmRemoteLeadIndicator |
| 3 | parts | c_habmThreatToStation |
| 3 | parts | c_habmIsLtRipcordTarget |
| 3 | parts | c_habmCanLtRipcord |
| 3 | parts | c_eabmCapture |
| 2 | screens | Intro screen |
| 2 | screens | Splash / loading screen |
| 2 | screens | Video screen (cinematic), **missing** |

## Native intent and row evidence

Native citations below are relative to **`~/Development/Allegiance/src/`**.
“Design” preserves a native rule; “era constraint” identifies the unsupported
runtime/platform/service; “bug” identifies a native behavior explicitly present
in the source. The brief's referenced classification footer is not present beyond
its instructions; these three requested categories are used directly.

| Row | Result / native intent | Native rule and implementation evidence |
| --- | --- | --- |
| c_habmCaptureThreat | done / design | `Igc/igc.h:729,5384-5389`; `Igc/common.cpp:2490-2495`: the flag contributes EnemyAPC to the asset mask and produces StationCaptureThreat when a station is present. It does **not** authorize boarding/capture. Existing `ClusterWarnings.GetAssetMask/GetWarning` implements the complete flag consumer. New `P722InterfaceModelTests.Threat_abilities_produce_the_native_client_warning_text` verifies classification through ClientWorld warning/status text. |
| c_habmThreatToStation | done / design | `Igc/igc.h:735,5390-5394`; `WinTrek/WinTrek.cpp:10166,10299`; `Igc/shipIGC.cpp:1262`: bomber asset classification, bomber targeting and doubled repair credit. Existing `ClusterWarnings`, `Game1.ClassRoleBits` and `Ship` repair scoring cover these consumers. New model warning test plus `NaniteThreatBonusLiveTests` verify them. Generic wingman order behavior is a separate simulation concern, not another missing consumer of this bit. |
| c_habmRemoteLeadIndicator | done / design | `WinTrek/indicator.cpp:296-320`: same-side unboarded relay ship with this hull flag and scanner contact grants lead when the local hull lacks its own grant; allies do not qualify. Existing `Server.CanShowLeadIndicator` implements it. New TCP theory tests same-side/range/boarded rejection and grant removal, including ClientWorld reticle clearing. `LeadIndicatorUpdateWireTests` pins the complete 34-byte extension layout. Native computes this locally: **there is no native DEFINE_FEDMSG for FMID 210**. |
| c_habmIsLtRipcordTarget | done / design | `Igc/igc.h:738`; `FedSrv/FedSrv.CPP:13393-13429`; `Igc/shipIGC.cpp:3727-3729`: light-only anchor requires a light-capable requester. Existing server anchor mask and sector anchor revalidation implement the flag. New TCP theory exercises legal/illegal requests and exact replies. |
| c_habmCanLtRipcord | done / design | `Igc/igc.h:739`; `FedSrv/FedSrv.CPP:13400-13402`: adds the light anchor bit by OR; ordinary anchors remain legal. New four-case TCP theory verifies both masks, full activation bytes, header-only denial, abort bytes and activation-before-abort order. Existing `P676SimRulesTests.Reduced_light_anchor_is_valid_only_for_a_light_capable_ripper` verifies ongoing validation. General ripcord target-search breadth is not re-scoped here. |
| c_eabmCapture | done / design | `Igc/igc.h:771`; `Igc/missileigc.cpp:471-496`; `FedSrv/fsship.cpp:619-631`: this is a **missile**, not probe, ability. Apply direct damage first, then check the downed shields; reject own-side and pedestal stations; capture via the launcher's event; suppress impact explosion even if capture is refused. New `Sector` impact branch uses the existing station capture seam, reads the old side before re-siding, synchronizes runtime/static ownership, and runs existing conquest/side-loss bookkeeping without docking the launcher. Six TCP cases test capability-off, friendly, pedestal, shields-up/down and damage-on-impact lowering shields; a nearby witness proves blast suppression. Both owner and enemy receive byte-identical STATION_CAPTURE(58), and ClientWorld changes the station side. The port's existing mission-wide station-invulnerability parameter remains unsupported, as documented in Station.cs; no simulation-matrix claim is made for that parameter. |
| Selection pane | done / design | `WinTrek/selpane.cpp:81-120` is a selected-model binding container, not a separate command protocol. The values come from `WinTrek/consoledata.cpp:65-87,202-279,449-460`: name, damageable hull/shield, asteroid-only ore. New `SelectionPaneModel`, used by `Game1.ComputeTargetSelectionBracket`, reads one locked ClientWorld snapshot, supplies station names as well as pilot names, excludes hidden/dead/departed ships and respects alliances. New headless model tests verify station live percentages, asteroid ore, allied relation and departure/death clearing. Existing bracket style/radar selection tests are repeated too. |
| Intro screen | done / design + era constraint | Current `WinTrek/introscreen.cpp:34-99` defines logged-out/logging-in/logged-in states, directory server/core/mission lists and mission creation. An animation is not the completion requirement of this source. The supported equivalent is the existing browser/connect state in `Game1.Session` and `Game1.ConnectScreen`, with profile, filtering and shared hit-test/layout tests. Zone account login and hosted mission creation are separately excluded below. |
| Splash / loading screen | done / bug | `WinTrek/splashscreen.cpp:20-46` tests `g_bQuickstart || true`, unconditionally scheduling transition to Intro after 0.01 seconds; input also skips it at `:64-79`. The port's immediate browser/direct-connect entry and existing `DrawConnecting` status preserve the practical behavior without inventing a mandatory timed splash. Connect layout/profile tests exercise the destination UI. This is a native unconditional-quickstart bug, not evidence of a missing loading protocol. |
| Kick / ban / mute | done for supported runtime / design + era constraints | `WinTrek/teampane.cpp:1070-1125` confirms a leader boot; with no defections it emits QUIT_MISSION(124). `Inc/Messages.h:787-818` fixes reason values and field layout; `FedSrv/FedSrv.CPP:6749-6818` permits self-quit or team-leader boot of a same-mission teammate. New `Server.Moderation`, receive-worker decode and session sender implement that path; normal release/succession cleanup is reused with the correct reason. The client consumes both QUIT_MISSION and QUIT_SIDE, removes departed identity/side and returns an own mission departure to browsing. `/boot ID` then `/confirmboot` (or `/cancelboot`) exposes confirmation; team-pane pilot rows show IDs and the command hint. `/mute ID` and `/unmute ID` implement native's local preference (`WinTrek/WinTrek.cpp:3731-3734`; `WinTrek/trekigc.cpp:4492`), filtering incoming player chat/buoy chat without a MUTE wire message. TCP tests verify self-quit/boot exact bytes and cross-side/nonleader refusal; UI-model tests verify confirmation, cancellation, reset, live leadership recheck, local mute/unmute and side transitions. Steam-group moderator kick/ban (`WinTrek.cpp:3737-3761`; `FedSrv.CPP:6754-6786`) and defection-side boots are unavailable infrastructure; no substitute administrator privilege or persistent ban system is invented. FMID117 itself has no native sender/consumer and remains n/a. |

The moderation implementation also required keeping the executable client receive
registry and `docs/parity/fedmsg-wiring.csv` accurate: 124/126 move into the
consumed registry, now **103 total consumed / 16 known unconsumed**, including
**86 native consumed / 17 extensions**. Their *fedmsg-area matrix scores remain
partial* for a separate audit of all native transitions; P722 does not claim two
extra message-row completions outside its selected area. Their old “no client
consumer” reasons were corrected.

## Excluded rows and actual limits

| Row | Classification | Specific reason |
| --- | --- | --- |
| Create game / new game wizard | n/a / era infrastructure | `WinTrek/introscreen.cpp:78-99` selects remote server/core/name; `clintlib/clintlib.cpp:2013-2041` sends CREATE_MISSION_REQ through **m_fmLobby** and waits for CREATE_MISSION_ACK. This port's lobby is an HTTP directory of already boot-configured servers, without native game-host provisioning. `newgamescreen.cpp:1034-1041` additionally sends MISSIONPARAMS for settings; that separate unsupported message is not implemented or scored here. ServerConfig remains the provisioning mechanism. This exclusion does not claim a GUI create-game wizard was added. |
| Training screen | n/a / era runtime | `WinTrek/trainingscreen.cpp:175-230` selects slideshow/local mission launches; `WinTrek/WinTrek.cpp:7401-7421` calls Training::StartMission and installs its local ship/camera; `training/trainingmission.cpp:651-788` resets/loads a local IGC universe. The network-only client has no offline WinTrek training simulation/control-script runtime. A selectable screen that cannot run those missions would not be faithful. Implementing that runtime is simulation work outside this lane. |
| Training missions (tutorial) | n/a / era runtime | Same native local-runtime dependency as Training screen; the two rows describe the same absent subsystem, not two implemented features. `docs/NATIVE_TRAINING_SCRIPT.md` is a transcript, not playable server scripting. **PCore014.igc, trainingscreen.mdl, tutorial MDL art and voice assets are present in the external corpus**; the reason is the unsupported local runtime, not missing assets. A future offline/supported hosted training runtime should reopen both rows. |
| Video screen (cinematic) | n/a / era platform | `WinTrek/videoscreen.cpp:72-99` reads Windows registry CDPath; `:135-181` has the whole AVI/VideoImage sequence commented out under BUILD_DX9. The named msr_games/logo1/logo2/intro AVI files are also absent from the available artwork. This is a disabled Windows cinematic route, not a network or UI-model feature to fabricate. |

These four exclusions remove **13 weight**, while ten completions add **29
credit**. The training/runtime and creation/provisioning exclusions are explicitly
architecture-dependent and should be revisited if those runtimes are added.
No native assets were asserted absent without checking the external corpus.

## Scorecard before → after

| Area | Weighted before | Weighted after | Complete/eligible before → after |
| --- | --- | --- | --- |
| parts | 88.24% (135/153) | **100.00% (153/153)** | 34/40 → 40/40 |
| screens | 86.81% (79/91) | **100.00% (86/86)** | 19/24 → 22/22 |
| lobby | 76.47% (39/51) | **100.00% (43/43)** | 9/12 → 10/10 |
| all interface areas | 88.39% (1310/1482) | **91.15% (1339/1469)** | 350/410 → 360/406 |

Commands, fedmsg and commander credit is unchanged. The simulation matrix is
unchanged at **59.19% (789/1333)**. No missing/partial rows remain in the selected
three areas. Both touched CSVs use **Unix LF**, with all rows validated by
`scripts/parity-scorecard.py` (438 interface rows, 473 unchanged simulation rows).
Raw before/after scorecards are in [P722_assets](P722_assets/).

## Verification

The reproducible runner is [P722_assets/verify.py](P722_assets/verify.py).
It runs each affected method three times with an exact FullyQualifiedName filter,
then ten independent local never-parks showcases, then **exactly two sequential,
unfiltered sweeps** of Core, ClientCore, MonoGame and Lobby. LiveTiming tests are
included. No rig/221/200v200 opt-ins are enabled. The invocation ledger records
commands, exit status, TRX counters and failure messages/stacks; raw logs/TRX are
under `/tmp/p722/verification/`.

The initial isolated phase contained one deterministic **test-fixture** defect:
`Selection_treats_allies_as_friendly_and_drops_dead_or_departed_targets` supplied
KILL_SHIP with its default `BDeathCredit=false`, which native uses for eject,
then incorrectly expected a dead target. It failed all three original runs
(`isolated-130-1..3`). The fixture now sets `BDeathCredit=true` for an actual
death; the assertion is unchanged and the corrected method passed **3/3**.
This is not classified as a load flake or a product fix. All three receive
registry methods also passed three exact-method invocations. The twelve extra
runs are recorded by [verify-extra.py](P722_assets/verify-extra.py) and
[supplemental-results.json](P722_assets/supplemental-results.json); the original
three failures remain in [results.json](P722_assets/results.json).

The initial selected cohort contains **158 methods / 208 data cases**, each verified
three times individually: **474 passing invocations / 624 case passes**.
Including the rejected fixture's three runs, the complete focused ledger has
477 invocations, 624 passes and 3 documented fixture failures.

A cleanup compatibility review also found that the original one-argument
`ReleasePeer(ConnectionId)` is invoked by the existing duplicate-identity
fixture through reflection. Optional C# arguments do not preserve that metadata
contract. The original entry point is retained and delegates to
`ReleasePeerWithReason(..., LinkDead)`; moderation uses the reason-aware helper.
No cleanup or identity assertion was changed. The added compatibility cohort
passed **14 methods / 42 invocations / 48 cases**, including the existing
pending/seated identity-release cases, cache-drain and logoff checks, plus the
boot/self-quit TCP cases in both receive modes. Commands and outcomes are in
[verify-cleanup.py](P722_assets/verify-cleanup.py) and
[cleanup-results.json](P722_assets/cleanup-results.json). This brings the total
distinct focused methods to **160**, all with three passing individual runs;
some cleanup methods were deliberately repeated after the helper correction.
The source fingerprint before that correction is retained in
[source-before-release-fix.json](P722_assets/source-before-release-fix.json).

The never-parks gate passed **10/10 independent local 15v15 runs**. Every run
had real shots, hits and kills; minimum objective arrivals were **24/30**, kills
ranged **13–16**, and the longest sampled speed-below-1 pause with a target was
**3.710 seconds**, below the unchanged five-second assertion. No showcase failed,
so no base-rate comparison was required. Per-run metrics are in
[showcase-summary.json](P722_assets/showcase-summary.json).

Whole-solution build: **0 warnings / 0 errors**, recorded in
[build.log](P722_assets/build.log). The source fingerprint and starting HEAD are
in [source-sha256.json](P722_assets/source-sha256.json).

<!-- P722-SUITES-BEGIN -->
| Sweep | Project | Passed | Failed | Skipped |
| --- | --- | ---: | ---: | ---: |
| 1 | Core | 4723 | 2 | 0 |
| 1 | ClientCore | 1142 | 0 | 1 |
| 1 | ClientCore.MonoGame | 2250 | 0 | 3 |
| 1 | Lobby | 19 | 0 | 0 |
| 2 | Core | 4725 | 0 | 0 |
| 2 | ClientCore | 1142 | 0 | 1 |
| 2 | ClientCore.MonoGame | 2250 | 0 | 3 |
| 2 | Lobby | 19 | 0 | 0 |
<!-- P722-SUITES-END -->

The first Core process had already loaded the assembly with the changed
ReleasePeer signature before the compatibility correction. Its two
`TargetParameterCountException` failures are retained above, not classified as
load flakes. Both pending/seated cases passed 3/3 after restoring the original
entry point and passed in the second unfiltered Core sweep (**4725/4725**).

ClientCore’s default skip is the local P660 opt-in proof. It subsequently passed
**3/3 explicit enabled runs**, after both sweeps, with
`ALLEGIANCE_LIVE_P660_SHOWCASE=1`: [verify-optin.py](P722_assets/verify-optin.py),
[optin-results.json](P722_assets/optin-results.json).
MonoGame’s three skips are the existing unavailable private Meshy-pack cases.
Skipped counts above come from the TRX result outcomes rather than the raw
`notExecuted` counter, which is zero in these files despite the skip outcomes.

Exactly **two complete, unfiltered solution sweeps** were run; no third sweep
was used to hide an earlier result. The corrected second sweep is clean across
all four projects (**8,136 passes**, four explicitly accounted-for skips).
The first sweep’s two deterministic compatibility failures are fixed, and there
were **no remaining load flakes** to classify in TEST_FLAKES.md.

The complete focused/extra ledger contains **522 invocations / 675 case passes**,
plus the three retained, corrected-fixture failures. **161 distinct methods**
(including the enabled local P660 proof) each have three individual passing runs.
No assertion, deadline or quarantine was relaxed. No rig-only test was enabled.

Final audit: source fingerprints match; HEAD is unchanged; the simulation CSV
is byte-identical to base; both touched CSVs contain zero carriage returns;
`git diff --check` is clean. See
[verification-summary.json](P722_assets/verification-summary.json) and
[matrix-audit.json](P722_assets/matrix-audit.json).
