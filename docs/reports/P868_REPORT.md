# P868 — native n/a ruling audit

## Decisions for Domingo

- **Configured world lifetime (SIM-0146):** native deletes a default-core,
  non-admin empty mission after ten seconds. Recommend retaining explicitly
  configured/admin worlds and using native reaping for player-created sessions.
  The current difference remains **partial** pending the decision.
- **Steam (SIM-0148, SIM-0472):** decide whether authentication callbacks and
  external achievement delivery belong in the supported product. Recommend an
  optional adapter; score ingestion remains a separate gameplay requirement.
- **ICQ (general matrix, `107 ICQ_CHAT_ACK`):** recommend explicitly retiring
  this external service integration. Its former standalone-build rationale
  does not establish n/a for the port's online product.
- **Voice chat (general matrix):** establish whether the uncited row means
  microphone VoIP or native prerecorded quickcomms. Recommend certifying native
  quickcomms; no native microphone transport implementation was located.
  The audit cites the prerecorded-chat context without claiming it proves VoIP.

These are five rows and four decisions. None earns completion credit or remains
n/a. No decision was inferred from lack of a reply.

## Scope and result

All **164 simulation** and **35 general-matrix** original `n/a`/`na` rows are
recorded in [p868-na-audit.csv](../parity/p868-na-audit.csv). General-matrix
identities use `area::native_item`, since that matrix has no `row_id` column.
Every entry identifies its class, native source, rationale and action.

This is an audit with bounded implementation, **not a claim that all parity
gaps are closed**. Every unfinished B row is partial with its remaining branch;
the machine-readable audit is the complete backlog. Existing implementation
names alone do not earn whole-body certification.

Final classification: **76 A**, **118 B**, **5 C**. Of the B rows, **11 are
implemented** (eight repaired/added rules and three existing rules recertified)
and **107 remain partial**. The simulation matrix contributes 49 A / 112 B /
3 C; the general matrix contributes 27 A / 6 B / 2 C. All B/C rows previously
labeled “era constraint” are now native `design` intent rather than exclusions.

| Matrix | Before weighted | After weighted | Before complete/eligible | After complete/eligible |
|---|---:|---:|---:|---:|
| General | 99.91% (1856.332/1858) | 98.69% (1856.332/1881) | 648/649 | 648/657 |
| Simulation | 98.63% (1877/1903) | 89.30% (1903/2131) | 671/687 | 682/802 |

The lower percentages expose formerly excluded work. The scorecard validates;
non-production reachability continues to earn no headline credit.

## Implemented behavior

- **Wingman default-order host override:** offer
  `IIgcSite.HandlePickDefaultOrder` before ordinary enemy acquisition. A true
  result preserves the host's order; false falls through. Native:
  `Igc/shipIGC.h:2438-2447`. Tests: `P868DefaultOrderTests`.
- **Departure sound switch:** preserve the individual quit/boot, MIA and
  side-change cues, silence native's shutdown/destroyed-side/local-duplicate/
  CD-key/Steam/randomize branches, and use native's MIA fallback for unknown
  reasons. The five formerly excluded live
  audible rows are SIM-0455..0458 and SIM-0462. SIM-0459's referenced
  duplicate-local sound call is commented out, so that row is A and its silence
  is tested. Native `salQuitSound` actually uses `vo_sal_mia` at -20 dB through
  `MutexSal`; the previous deliberately different wave was removed. The event
  carries the native template into the shared Game1 serialized voice player. Native:
  `clintlib/clintlib.cpp:3934-4001`. Tests drive actual QUIT_SIDE and
  QUIT_MISSION handlers, native stage/human/own guards and duplicate suppression.
- **Encrypted native core loading:** implement cumulative DWORD XOR before
  record parsing. Preserve plain version/size prefixes, partial final DWORD
  bytes and caller-owned input. `ServerConfig.StaticCoreEncrypted` (JSON `staticCoreEncrypted`, default false)
  explicitly selects encrypted server loading, including renamed files. Generic
  file loading keeps its plain default even for a file named `zone_core.igc`.
  Existing one-argument APIs remain usable as method-group delegates. Native:
  `FedSrv/FedSrv.CPP:31-56,9304,9763`, `Igc/missionigc.cpp:94-109`,
  `Igc/igc.h:1000-1001`. An independently calculated encrypted word vector
  proves both file wrappers and real file loading; a ciphertext with a valid
  plain record header proves why format guessing would be wrong. A full
  server boot with an independently encrypted real core proves production
  reachability. The Docker example advertises the false default, and Docker
  configuration validates.
- **Payday mission scope:** restrict player count, recipients and wallet
  changes to the side's owning mission. Native's mission-local side enumeration
  is `FedSrv/fsmission.cpp:2294-2326`. The two-mission regression shares side
  IDs and verifies payout, guaranteed message recipients, integer remainder,
  zero-player and zero-share branches.

Three previously excluded bodies were already present and are now certified
as B/implemented, rather than excluded as aliases:

- **SIM-0413:** own-only RELAUNCH carrier-repair voice/cookie/camera, with remote
  suppression (`P740FlightInterfaceTests`).
- **SIM-0698:** mission stage/time guards, side pass, damage phase and native
  substep boundaries (`MissionUpdateTests`, `P739PeriodicTests`).
- **SIM-0753:** strict ballot expiration, early completion, cancellation without
  callbacks, pass/fail effects and retirement (`P739PeriodicTests`,
  `BallotVotingLiveTests`).

## Native exclusions and reopened branches

The audit distinguishes declaration-only/dead native paths from portable
behavior. Examples rechecked against the local source:

- `GetDroneFromShip` has only two declarations and one definition across the
  native tree; no caller. Its opaque pointer return is not an omitted live NPC
  decision (`Igc/igc.h:4872`, `FedSrv/FedSrv.H:118`,
  `FedSrv/FedSrv.CPP:13313-13317`).
- Side creation/destruction callbacks do run. Following them into
  `CFSSide` shows only DirectPlay group allocation/deletion, wrapper registration
  and private-pointer linkage (`Igc/sideigc.cpp:37-50`,
  `FedSrv/FedSrv.CPP:13953-13968`, `FedSrv/fsmission.cpp:5576-5604`,
  `FedSrv/fsside.cpp:10-27`). Side gameplay lifecycle is not excluded by that
  finding.
- The healthy-miner enemy-scope flee predicate is commented out; the live
  healthy branch forces `bRunAway=false`. Do not invent its unreachable voice
  producer (`Igc/shipIGC.cpp:1792-1865,1893-1902`).
- The `createdrones` and `jpd` cheat dispatch branches are inside a literal
  comment (`FedSrv/FedSrv.CPP:2980-3020`). The prior generic “debug-only”
  rationale was replaced by this actual source evidence.
- The static `Igc/missionigc.cpp:64-74` decryption helper has no caller. The
  separate FedSrv decryption helper is live and is now implemented.

The native core text tools are live operator functionality: `Core2Text` and
`Text2Core` call their loaders/importers/exporters at
`FedSrv/FedSrv.CPP:10385-10480`. Windows path handling does not make their
portable file-format behavior n/a.

Reopened B branches include global ship phases and collision-time ordering,
the shared first-500 explosion recipient snapshot, native roll-call boot
timing, fixed two-update quotas, force feedback, named drone chatter, gunner
interior audio, sound-source gates, portable core-authoring tools, registry
mutation/lookup, mission provisioning/catalogs, directory chat and server-side
client-file validation. Each has its exact remaining action in the CSV.

The existing MISSIONPARAMS codec and owner/stage/validation/relay handlers
already cover much of that row. The concrete remaining branch is actual
side-list growth beyond boot-time count (`Server.MissionParams` currently
clamps it; native `FedSrv/fsmission.cpp:1846-1852` creates sides). The stale
“MissionParams declaration is missing” claim was removed from
`NotSupportedFedMsgs`: native declares it at `Igc/igc.h:1109-1251`.

## Verification

Build: **0 warnings / 0 errors**. All **91 tool projects** build with 0/0.
All **28 new cases pass three isolated runs** (84 case executions, zero skips).
The recertification selection passes **63 core + 2 client cases**.
`tools/p868-na-audit.py` validates all 199 rows and native citation bounds/text
in 42 files. Every cited native range was re-grepped against the read-only tree;
this integrity check is not used as a substitute for behavioral evidence.
Glossary links and `git diff --check` pass. `docker compose config --quiet`
passes. No new service, port, store or environment variable was introduced.

The requested showcase method
`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
passes **5/5**, covering **ten complete local matches**, with zero skips:

| Run | Result | TRX duration |
|---|---|---|
| 1 | pass | 00:03:39.8868092 |
| 2 | pass | 00:03:58.5886946 |
| 3 | pass | 00:03:36.6615041 |
| 4 | pass | 00:03:29.2223172 |
| 5 | pass | 00:03:09.5962531 |

The **one** Fast sweep is retained: **11,404 pass / 39 fail / 3 optional skip**
over **11,446 tests**, **480.5 seconds**. Infrastructure **9/9** and the timing
guard pass. It was not rerun. Thirty-five failing methods arose from my first
filename-based decryption selection, which incorrectly treated the corpus's
already-decrypted `zone_core.igc` as encrypted. The final implementation restores
plain defaults and selects encryption explicitly; the regression is repaired,
not classified as a flake. Exact-method recovery and remaining unrelated
failures are recorded below and in `docs/TEST_FLAKES.md`.

The repaired 35 corpus-dependent methods pass **3/3 alone each** (105
exact-method invocations). The four remaining failed methods are recorded
without relabeling their outcomes:

| Method | Three isolated outcomes | Classification |
|---|---|---|
| Radar off-screen highlight | fail / fail / fail | Existing placement-premise regression; P865b lane untouched |
| Profile editor chat ownership | fail / fail / fail | Read-only default user config; scratch XDG config passes 3/3 |
| Direct missile broadcast/trajectory | pass / pass / fail | Intermittent assertion; root cause not established; source untouched |
| Declined part pickup | pass / fail / pass | Existing multiple-treasure fixture assumption; automatic quarantine retry passed |

[P868_VERIFICATION.csv](P868_VERIFICATION.csv) records every Fast failed
method and its three isolated outcomes. The Fast sweep retains its original
failures even though the P868 decryption regression is fixed. The newly added
encrypted-server boot case was tested separately after Fast discovery.

Local command logs and TRX files are under the git-excluded `.scratch/p868/`
directory. NuGet's HTTP cache is redirected there
because the default cache is outside the writable workspace. No audit/security
checks were disabled.

No commits or pushes were made. No rig, mini or Mac was contacted.

## Native intent and modern approach

**Native intent:** A means proven native dead/empty/platform plumbing or an
explicitly compared forwarding-only seam; B means observable behavior requiring
implementation/certification; C means an explicit product decision. Architecture
preferences and “managed replacement” do not establish n/a.

**Modern approach:** retain native timing, audiences and state transitions in
shared models while adapting devices, asset loading and transport at the edges.
For example, native XOR is a compatibility file transform, not cryptographic
security, and explicit format selection avoids ambiguous content guessing.
Future haptic support should use a device adapter with native producer guards;
global collision scheduling needs an efficient ordered queue without changing
the selected outcomes. Pending decisions remain visible rather than inflating
the score.
