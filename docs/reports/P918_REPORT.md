# P918 — training order slots and buoy bookkeeping

All five assigned rows are implemented in production, adding **23 points**.
Training increases from **774/901 (85.90%) to 797/901 (88.46%)**. Changes remain
in the working tree; no commits, pushes, stashes or remote-host access.

## Scorecard

| Metric | Before | After |
|---|---:|---:|
| Training framework | 227/298 (76.17%) | 250/298 (83.89%) |
| All training | 774/901 (85.90%) | 797/901 (88.46%) |
| Completed eligible training rows | 199/235 | 204/235 |

`python3 scripts/parity-scorecard.py` ran before and after. Full receipts are
`.scratch/p918/scorecard-before.txt` and `.scratch/p918/scorecard-after.txt`.
Only TRN-0010, TRN-0013, TRN-0063, TRN-0084 and TRN-0085 changed; no other
matrix row earns different credit.

## Row verdicts and evidence

All test paths in this table are under `tests/Allegiance.ClientCore.MonoGame.Tests/`.
Every order test has Classic and Modern cases, an actual embedded TrainingHost,
loopback ConnectionSession, production Game1 facilities and a native Mission2
or Mission6 graph. The window proof additionally runs the shipping renderer.

| Row | Verdict | Native citation | Production implementation | Tests |
|---|---|---|---|---|
| TRN-0010 CreateDroneAction | Implemented | `training/CreateDroneAction.cpp:60-207`; `Igc/shipIGC.h:731-782`; `clintlib/clintlib.h:1437-1477,1638-1724` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:664`; `TrainingHost.Loadouts.cs:24`; `src/Allegiance.Core/Sim/Ship.CommandSlots.cs:17` | `P918TrainingOrderTests.cs`: `Real_death_repeated_spawn_and_recipe_removal_balance_native_holds`, `Tech_cache_survives_drone_death_and_restores_same_hull_at_real_carrier_site`, `Trainee_side_departure_and_mission_exit_retire_slots_and_registered_recipes` |
| TRN-0013 CreateWaypointAction | Implemented | `training/CreateWaypointAction.cpp:37-47`; `Igc/buoyigc.h:67-88`; `Igc/buoyigc.cpp:39-54` | `src/Allegiance.Core/Sim/Buoy.cs:14`; `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs:22`; `src/Allegiance.ClientCore.Game/Game1.TrainingRendering.cs:14` | `P918TrainingOrderTests.cs:40`; `P918TrainingWindowTests.cs:12`, both styles |
| TRN-0063 SetCommandAction | Implemented | `training/setcommandaction.cpp:71-95`; `Igc/shipIGC.h:731-782`; `WinTrek/trekigc.cpp:2746-2749,2773-2785` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:740`; `src/Allegiance.Core/Sim/Ship.CommandSlots.cs:17` | `P918TrainingOrderTests.cs:72,116,149,214,243`; `tests/Allegiance.Core.Tests/P918CommandSlotsTests.cs` |
| TRN-0084 CreateDrone tech loadouts | Implemented | `training/CreateDroneAction.cpp:91-192`; `clintlib/clintlib.h:1437-1477,1638-1724`; `WinTrek/trekigc.cpp:4174-4189` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs:97`; `TrainingHost.Loadouts.cs:24,29`; `src/Allegiance.Server/Server.Docking.cs` (`PerformRelaunch`) | `P918TrainingOrderTests.cs:184`; existing `P909TrainingBindingsTests.cs:149` |
| TRN-0085 CreateDrone accepted command | Implemented | `training/CreateDroneAction.cpp:201-207`; `Igc/shipIGC.h:731-782`; `training/trainingmission.cpp:97-103,304-324` | `src/Allegiance.Core/Sim/Ship.CommandSlots.cs:17`; `src/Allegiance.ClientCore/Training/TrainingMission.cs:416` | `P918TrainingOrderTests.cs:91,116,214`; existing registered-recipe test in `P911TrainingArenaTests.cs` |

No assigned row retains a port gap. The explicitly rejected undefined native
tech input below -3 is classified as a native bug, not counted as a supported
loadout or silently substituted.

## Ownership and native boundaries

`Ship.CommandSlots.cs` owns buoy transitions for current, accepted, queued and
plan targets. Both SetCommand and direct slot property writes use it. A
replacement is acquired before the previous target is released; identical
writes preserve the live buoy. The last consumer retires its mission registry
entry. TrainingHost no longer maintains a second slot/consumer dictionary.
Native references: `Igc/shipIGC.h:731-782`, `Igc/buoyigc.h:76-88`.

Accepted autopilot NPC commands also fill current and plan, so they own three
slot holds. Canonical plan writes also rebuild execution goals without changing independent
selection, accepted or queued slots (`Igc/shipIGC.cpp:3984-4151`). This prevents
a server reorder from retaining a Goto to its retired buoy. Repeated plan
writes reset the native damage/retreat latch, even for identical target/id
(`Igc/shipIGC.h:750-763`).

A queued command owns only its queued slot and never starts a
Goto. A current selection also leaves the execution plan alone; accepting a
player-pilot drone order does not create an NPC plan. Turning autopilot off releases only the plan hold; turning it back on
copies current into plan (`Igc/shipIGC.cpp:3974-3981`). Native local drone death calls RemovePlayerFromSide, whose Reset(false) clears
the slots before termination (`clintlib/clintlib.cpp:3067-3069,3904-3906`).
Death/termination,
replacement of a live drone id and mission disposal clear slots through the
same path. Pre-held respawn recipes retain their independent hold through a
death, and release it on RemoveRespawn or lesson-owner cleanup
(`training/trainingmission.cpp:97-103,304-324`; `CreateDroneAction.cpp:48-55`).

Side and sector transitions need the native distinction: direct IGC SetSide
and unrelated travel do not discard ordinary waypoint commands
(`Igc/shipIGC.cpp:3394-3440`). A real player side departure calls Reset(false)
before changing side (`FedSrv/fsmission.cpp:1139-1143`), and Reset clears all
four slots (`Igc/shipIGC.h:503-506`); the server departure path now invokes the
central training reset. The local trainee's arrival clears matching **cluster**
buoys (`WinTrek/trekigc.cpp:2746-2749,2773-2785`). Mission.TrainingPlayerShipId
identifies that local GetShip callback without clearing NPC orders merely
because they entered a sector. Ordinary drone waypoints survive travel; the
live travel test then replaces one slot and proves exactly one hold retires.
Native Goto completion clears matching slots (`Igc/shipIGC.cpp:2795-2807`),
including an NPC cluster-Goto after sector travel. A red regression reproduced
its retained current/accepted holds; the planner completion now invokes the
same retirement helper. Both-style real-host travel cases also cover this path. The Mission2 legacy
movement adapter now calls Ship.CompleteGotoOrder as well; a red real-host test
reproduced two retained holds in each style before that wiring correction.

## SaveLoadout is observable in training

It cannot be classified as inert: native local carrier landing invokes
RestoreLoadoutOnCarrier (`WinTrek/trekigc.cpp:4174-4189`). The cache records
part types and signed mounts, keyed by base hull and shared across ships and
sides (`clintlib/clintlib.h:1437-1477,1638-1724`). Tech 1 does not overwrite it;
other tech recipes save after substitutions (`CreateDroneAction.cpp:91,192`).
Native clears the client cache at TrainingMission::Start
(`training/trainingmission.cpp:153-155`; `clintlib/clintlib.h:1381-1386`), so
its lifetime is one training mission, rather than one drone.

The port now preserves/refills present parts, matches duplicates by type and
mount, restores missing mountable modules and fills remaining cargo slots.
CargoHold records use MountId=0 internally, so the saved signed cargo mount
comes from the hold index. The regression test fails if those cargo records
are accidentally saved as equipped mount zero. It also checks persistence
after the source drone dies, preservation of an existing gun, and overwrite by
a later same-hull drone on another side. The real carrier IGC site dispatches
to PerformRelaunch and the LocalTraining callback, not a test cache accessor.

## Visual proof

The shared scene pass uses `visible && consumers > 0`; geometry is radius 20
for visible authored buoys and radius 2 for hidden authored buoys, regardless
of whether either currently has a consumer. It loads the native `buoybmp.mdl`
decal, tinted white and rotating at 0.5 rad/s
(`Igc/buoyigc.cpp:39-54`; `buoyigc.h:67-69`). No HUD-style branch duplicates
this behavior; no native 2-D layout conventions were changed.

The window tool adds three ordinary action sequences during actual Mission2:
a held visible buoy, a held hidden buoy and an unheld visible buoy. It checks
the actual scene snapshot and native texture, then clears the final visible
slot and checks retirement. It changes camera presentation through the normal
camera facility, and changes no lesson condition, time or outcome. The separate
loopback test advances native Mission2 to its authored commander waypoint.

The captures were refreshed from the final frozen-source repeated window tests
(`.scratch/p918/capture-sources-final.json`). I opened all four captures. In both visible images, the green native ring is
above/right of the center, near pixel (588,120). It disappears in both retired
images. Hidden and unheld controls produce no decal. Classic shows its circular
reticle and text inventory; Modern shows its percentage gauges and tactical
panel. The scene also contains the commander and base; their presence is not
counted as buoy proof.

- [Classic visible](P918_assets/p918-classic-visible.png), [Classic retired](P918_assets/p918-classic-retired.png).
- [Modern visible](P918_assets/p918-modern-visible.png), [Modern retired](P918_assets/p918-modern-retired.png).

These are native-artwork screenshots, below the 2 MB task evidence budget.
They remain uncommitted for media review before any future push.

## Multiplayer behavior and local cost

New ownership/cascade/lifecycle behavior is guarded by Mission.IsTraining;
the carrier cache callback additionally requires LocalTraining. Ordinary
multiplayer keeps its existing two SetCommand slots and lazy generated Goto
buoy ownership. P918CommandSlotsTests pins that contract, including unchanged
queued handling, authored buoy retention and lazy navigation-buoy retirement.
The server's station-order, ordinary command-chat and side-exit additions route
training through the same central slot API and are inert for multiplayer.
CommandToDrone finalizes its legacy execution helper through accepted SetCommand
(`FedSrv/FedSrv.CPP:3281-3283`). The loopback test bypasses the client's local
training shortcut to prove the actual server handler retires all old holds.

`tools/p918-training-proof/SimCost.cs` runs the same source against the saved
dispatch Core assembly and the changed assembly. The final scenario has 32
multiplayer wingmen with both point and typed-ship GotoPlan goals, and current/
accepted replacements every ten ticks. Two ABBA rounds give four batches and
36 measured samples per version after warmup. Both versions produce fingerprint
`A76D6C74200891693399EA51D5F819C564E138E5BBEFA97C17F8560230BFE6F7`.
The fingerprint covers poses/velocities/fractions, command slots, command event
count and lazy buoy count. Median time is **97.108 ms before / 96.228 ms after**
(-0.91%); ranges overlap (92.670–112.726 / 91.934–111.734 ms).

Median allocation is **11,310,952 bytes per 250-tick sample** in both versions.
All 36 baseline samples and 34 changed samples allocate that amount; two changed
samples allocate 6,192 additional bytes (0.055%). The first outlier prompted the
second round, and both rounds remain in the summary; its cause is unestablished.
The final round's one-minute shared-host load was 1.04, with this lane's window
qualification also active (`.scratch/p918/perf-final-load.json`). These samples
show flat local tick timing and median allocation, with the small allocation
outliers disclosed; they are not a rig throughput claim.
[Cost summary](P918_assets/multiplayer-cost.json).
There is no new tick sweep or per-tick consumer reconciliation.

No rig run is required to exercise these training-only rules. The dispatcher
can include this change in its normal rig regression batch; this lane has made
no remote performance claim or changed the winning baseline. Window/server
proofs use pool transport, parallel broadcast build, native ship-update model,
native GotoPlan and server GC; experimental interest/native-broadcast/decoupled
receive options remain off.

## Validation record

Artwork is available through the existing local.env. Raw build logs, TRX,
window logs, captures and cost receipts remain under `.scratch/p918/`, the
writable evidence location allowed by this sandbox.

Explicit red regressions reproduced stale execution after server replacement,
loss of the canonical plan command id, retained current/accepted holds on NPC
cluster arrival, ordinary server chat bypassing accepted storage, and two
retained holds on Mission2 legacy arrival. The final nine Core and sixteen
real-host cases pass. Existing fixtures now use the native current+accepted
pair, acquire a real consumer before releasing it, promote Mission4's actual
queued model instead of nonexistent Ship/1030, and check the native new-order
damage anchor (`WinTrek/WinTrek.cpp:10682-10696`; `Igc/shipIGC.h:750-763`).

The M8 loss proof also needed a real-input correction. Its final pilot diverts
all three friendly fighters through command-view picks and waypoint orders,
then restores attacks after the miner dies. The observer requires all three
actual accepted diversion orders, actual miner death and exact `tm_8_15`.
Native can omit the death message when the miner dies around the transition
out of the combat goal (`training/mission8.cpp:361-392`), while selecting its
loss voice in the following verdict (`:394-401`). That legal transition no
longer fails the observer. Survival still requires its cheer. No actor, loadout,
damage, clock, condition or outcome is assigned by these proof corrections.
The escort pilot now cycles command-camera recipients with the normal key,
because overlapping fighter icons could repeatedly select the wrong escort
and stall the escort proof. The early-mount observer pins miner survival in
lesson 2. Its unrelated demand
for an outpost win in lesson 3 was removed: native ends that lesson on either
bomber or outpost destruction (`training/mission8.cpp:600-654`). Both endings
still require their exact native message sequence. A Modern replay reproduced
the legal bomber-loss ending after preserving the miner. No sim rule or combat
outcome was changed to pass this observer. No additional parity rows change.

The feature code was frozen in `.scratch/p918/final-source-v2/`, with
**3,736 source/test/tool files** recorded by SHA-256. The final working-tree
manifest is `.scratch/p918/final-source-v5-manifest.json`; its differences
from v2 are only the opt-in Mission8 proof pilot recipient selection and the
proof observer corrections described above.
The loss pilot also orders the native repair wingman home through ordinary
keys and backs away from the bomber to avoid shielding it. The forced bomber-loss
assertion remains. The authored attack-order guard applies only while both
combatants live: native model retirement can clear the slot before the graph
observes a death. An earlier replay reproduced that legal transition.
Core, server, host, loadout, rendering and all P918 regression-test sources are
identical between those cohorts. Receipts identify their source cohort. Earlier
attempts and prototypes do not count as final passes. The one earlier M4
window-child exit passes 3/3 in isolation; its cause remains unestablished and
its receipt is recorded in `docs/TEST_FLAKES.md`. An intermediate Classic M3
shooting-goal stall also passes 3/3 in isolation (174.149 / 195.932 / 188.547 s),
and the fresh final gate completes that course; its cause remains open in the
same ledger. An intermediate M4 Modern departure replay timed out at goal 8.7;
its exact case passes 3/3 fresh filtered invocations (658.793 / 671.724 /
668.911 s), and both full passing gates complete it. Those three processes ran
concurrently, one parameter case each, at one-minute host load 0.55–0.76;
`.scratch/p918/m4-departure-isolation/summary.json` records them. Its cause also
remains open in the ledger. These isolation results do not turn earlier failed
or timed-out gates into passes.

## Qualification receipts

- Debug solution build: **PASS, 0 warnings, 0 errors** (`.scratch/p918/build-handoff.log`, normal working-tree output; source-matched snapshot build also passes).
- `tools/build-tools.sh`: **PASS, all 113 tool projects**, with no warnings/errors (`.scratch/p918/build-tools-handoff.log`; snapshot also passes).
- Native AOT server publish: **PASS**, with no IL2026/IL3050 or other warnings (`.scratch/p918/aot-handoff.log`; snapshot also passes).
- Final frozen-source Core, new real-host/window, P909 and affected legacy framework tests: **PASS, 93 isolated invocations / 144 cases, zero failures/skips**, three invocations per method (`.scratch/p918/isolated-final-v2/summary.json`). All 27 new parameter cases execute three times (81 passes); the additional cases cover affected bindings and legacy adapters. The longer affected M8 window cohort is recorded separately.
- Final working-tree Mission8 whole-course cohort: **PASS 9/9, zero skips**, three isolated invocations covering Classic default, Modern default and the forced miner/bomber-loss policy (`.scratch/p918/isolated-final-v5/summary.json`).
- Final working-tree Mission8 early-mount cohort: **PASS 6/6, zero skips**, Classic and Modern in three isolated invocations (`.scratch/p918/isolated-final-v5/summary.json`). The miner-survival branch and exact native final message sequence are checked.
- Final working-tree Mission8 lesson-prefix cohort: **PASS 6/6, zero skips**, lessons 2 and 3 in three isolated invocations. Combined isolated qualification is **102 invocations / 165 passed cases / 0 failures / 0 skips** (`.scratch/p918/isolated-final-v5/summary.json`).
- Final working-tree artwork training gate (`--no-build --repeat 1`): **PASS**, headless **100/100**, MonoGame **227 passed / 4 skipped / 0 failed** (231 cases); exit 0, MonoGame duration about 2 h 18 m. The four skips are the existing opt-in hunt/sacrifice branches. Receipt: `.scratch/p918/training-final-v5/`; this run uses the exact final source manifest.
- Full frozen-feature artwork training gate (`--no-build --repeat 1`): **PASS**, headless **100/100**, MonoGame **227 passed / 4 skipped / 0 failed** (231 cases). The four skips are the explicitly opt-in P917 hunt/sacrifice window branches, not missing artwork. Receipt: `.scratch/p918/training-final-v2/`; feature sources match v5 as described above.
- Sole Fast sweep: **FAIL**, 12,989 cases: 12,984 passed, two failed, three optional-pack skips; nine infrastructure checks passed. The persistent defend-target failure also fails **3/3 against the saved dispatch assembly**, with the same line-179 assertion. The sweep-only wingman attack failure passes **3/3 alone** on final source. Warp-exit passes 3/3 but remains just over the Fast time limit. Exact assertions, timings and raw paths are in `docs/TEST_FLAKES.md` (P918); `.scratch/p918/fast/summary.json` preserves the failed sweep. The sweep preceded the final training-only execution-plan correction; final isolated and full training runs cover that correction. Per the brief, no second Fast sweep was run.
- Final-source showcase never-parks: **PASS 5/5, zero skips**, serial wall times 237.397 / 255.643 / 214.169 / 224.650 / 231.550 s (`.scratch/p918/showcase-final-v2/summary.json`). Dispatch P912 also recorded 5/5 (207–376 s; `docs/reports/P912_REPORT.md`); these are behavior checks, not a throughput comparison.
- Native inventory audit: **241 rules, 82 primitive types/macros, 0 errors** (`.scratch/p918/native-audit-final.log`).
- Glossary paths and `git diff --check`: **PASS**. CSV remains LF. Exactly the five assigned parity rows changed.

[Machine-readable qualification summary](P918_assets/qualification.json).

## Native intent

- **Design:** command slots own independent model/consumer holds. Cached
  loadouts are client-wide per base hull; carrier resupply observes a previous
  recipe's save. Visibility depends on consumers as well as the authored flag.
- **Era constraint:** native training shares one client-side IGC world. This
  port uses an embedded server and a loopback mirror, so the authoritative
  model supplies one locked draw snapshot to both HUD styles. Managed
  references do not replace native last-consumer retirement semantics.
- **Bug:** negative tech below -3 leaves native `pprox` uninitialized
  (`training/CreateDroneAction.cpp:177-184`). Mine-capable inputs in that range
  remain explicitly rejected. Native's known cache ambiguity across same-hull
  ships with different tech levels is retained: the latest save wins, as its
  own comment acknowledges (`clintlib/clintlib.h:1643`). Native also leaves
  `b_addedConsumer` unset when Execute supplies the extra buoy hold, including
  a first `spawnOnAdd` spawn (`CreateDroneAction.cpp:201-207`;
  `trainingmission.cpp:292-312`). Repeated execution in that path can accumulate
  action consumers; its destructor releases only a marked pre-held recipe
  (`CreateDroneAction.cpp:48-55`). That native quirk is retained. Ship-slot
  retirement and the ordinary pre-held respawn path remain balanced. Loadout
  cache lifetime and action holds are distinct from command-slot ownership.
- **Modern approach:** typed slot leases and immutable, explicitly versioned
  loadout recipes would make ownership and cache scope easier to inspect.
  Choosing per-drone or per-tech caches would change native carrier behavior,
  so this port deliberately keeps the native base-hull key.
