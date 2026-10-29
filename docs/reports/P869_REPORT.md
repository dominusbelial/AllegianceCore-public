# P869 — ownership, registries and native lifetime

## Scope and classification

The dispatch brief contains conflicting historical n/a wording. This work follows
its explicit 2026-10-28 ruling: portable observable behavior closes as
**implemented**, and managed memory alone never earns an exclusion.

The 48 requested rows have individual native-effect, observable-contract and test
entries in [p869-ownership.csv](../parity/p869-ownership.csv). **47 rows are
implemented; SIM-0788 remains partial** for the live text-core fallback described
below. All 48 are native **B / design**. No row was relabeled n/a, no product
approval was inferred, and unrelated exclusions were preserved.

## Implemented behavior

- Mission-wide registries now retain strong references in insertion order,
  preserve duplicate identities and IDs, delete the first pointer-identical
  reference, return the first matching ID and expose stable live views. Real
  assembly/placement/termination paths maintain them; sector removal for transit
  or docking preserves mission membership. Warps, stations, treasures,
  asteroids, probes, mines, sides, clusters, ships and buoys are covered.
  Native: `Igc/missionigc.cpp:3506-3652,3683-3732`,
  `Igc/igc.h:5130-5197`. The parameterized registry test certifies each of the
  forty rows; additional tests exercise actual placement, movement and death.
  Weak site associations add no ownership: GC probes prove that a registry
  retains its model and that deletion relinquishes the last reference. Deleting
  a registry reference preserves a site's lifetime while a cluster still owns
  the model; actual termination detaches the site. Native model/site ownership:
  `Igc/modelIGC.h:149-170`.
- The separate `SIDE_TEAMLOBBY=-2` side uses the first civilization, identity
  global attributes, empty tech masks and counters, white color, active=true,
  no squad/alliance and no ordinary-list membership. Native:
  `Igc/missionigc.cpp:3608-3648`. Waypoint commands own real buoy identities
  until replacement, cancellation or ship retirement; native buoy IDs have a
  separate mission counter. Native: `Igc/missionigc.h:389,852-855`,
  `Igc/shipIGC.h:736,782`, `Igc/buoyigc.h:76-88`.
- The actual server site factory creates a per-model site carrying owner,
  Predictable and SeenBySide attributes and sharing the model's real visibility
  state. The default `IIgcSite` factory retains native's empty default shim.
  Missile/chaff/buoy flags are distinguished. Native:
  `Igc/modelIGC.cpp:117-118`, `FedSrv/FedSrv.CPP:10692-10709,11223-11226`,
  `Igc/missileigc.cpp:51,67`, `Igc/chaffigc.cpp:46`.
  Native scanner storage belongs to the cluster site, not the ThingSite
  constructor. Existing scanner/spotting rules remain in the shared sector.
- Binary cores have explicit named shared leases, case-insensitive cache hits
  before file IO, version-only prefix reads without consumer ownership and
  eviction only when the last lease retires. Server boot and assembled missions
  hold leases. Old worlds release them after their final migration burst;
  Client worlds acquire the same kind of lease and release it on reset or
  session/world disposal; their previous unbounded cache is removed.
  Shutdown unregisters worlds, detaches retained objects, stops ticks and
  publication, and releases transport/core ownership idempotently. Native:
  `Igc/missionigc.cpp:2390-2431,3033-3053,3085-3125`,
  `FedSrv/FedSrv.CPP:13976-13979`. A caller retaining managed core data may
  still read it after cache eviction; that does not retain cache ownership.
- `SET_MISSION_OWNER(123)` now requires a separately authenticated bot role,
  bound only after validated logon through `botAuthenticationSecret` and
  `LOGONREQ.CDKey`. Its default is disabled. Admin, harness and character-name
  prefixes do not grant the role. Real sender-side and target-leader guards
  precede transfer. The broadcast uses the target side's actual leader, ignores
  the request's ship ID and reaches only that mission. Native:
  `FedSrv/FedSrv.CPP:7690-7715`, `FedSrv/fsmission.cpp:1495-1507`.
  Docker documents the JSON setting with a null default; no new port, external
  store, service or environment variable is introduced.
- Standalone numeric character IDs are retained for the process lifetime using
  exact, case-sensitive names, bound at validated standalone logon and carried
  by the seated/retained score. They remain distinct from verified online
  account subjects. Native: `FedSrv/FedSrv.CPP:65-84,5881-5886`. Existing
  old-player score lookup remains case-insensitive, as native specifies at
  `FedSrv/fsmission.h:390-398`; this is a separate contract from GetStateID.
- Native `maps.txt` is loaded in authored order. Validated logon sends
  `STATIC_MAP_INFO(11)`; failed logon sends none. Its 44-byte records preserve
  file[13], friendly-name[25], two padding bytes and team-count int. Session
  intake replaces/copies the catalog, including empty catalogs, and selection
  uses the exact file/team pair. Server provisioning resolves matching catalog
  selections to artwork files. The unsupported-message registry no longer
  includes FMID 11. Native: `Inc/Messages.h:120-131`,
  `FedSrv/FedSrv.CPP:211-244,990-998`, `clintlib/appmsg.cpp:3353-3373`.
  An empty var-item ignores its IB as native `Inc/messagecore.h:780` specifies.
  No presentation/HUD changes were made.

## Remaining native branch — SIM-0788

Binary version reads, cache hits, consumer increments, last-consumer eviction,
replacement and absent-binary behavior are implemented and tested. The full native
`LoadIGCStaticCore` body also checks `<name>/Constants.csv` when the binary is
missing, returns version **1337**, and calls `ImportStaticIGCObjs` for a full
load (`Igc/missionigc.cpp:2631-2669`). The port has no text-core importer.
That live portable branch is not an exclusion. SIM-0788 therefore remains
**partial** with this exact dependency; its cache tests do not claim whole-body
certification. The separate text-authoring rows remain visible in the P868 audit.

## Scorecard

| Matrix | Before weighted | After weighted | Before complete/eligible | After complete/eligible |
|---|---:|---:|---:|---:|
| General | 98.69% (1861.832/1886.500) | 98.80% (1863.832/1886.500) | 651/660 | 652/660 |
| Simulation | 89.31% (1905/2133) | 93.62% (1997/2133) | 683/803 | 729/803 |

The original denominators are retained. SIM-0634 and SIM-0685 now have actual
production bindings rather than test-only reachability. The P868 audit retains
76 A / 118 B / 5 C, with **58 B implemented / 60 B partial** after these changes.
The P867 dispatch inventory now certifies the formerly absent owner handler.

## Verification

- Final whole-solution build: **0 warnings / 0 errors**.
- All **92** tool projects build; server **native AOT publish** succeeds without
  IL2026/IL3050 diagnostics.
- All **36 final new cases pass 3/3 alone** (108 executions, zero skips).
- The affected client mission-entry, feedback and civilization-picker selection
  passes **27/27 in each of three runs**. The shared-core mutation and flight
  selection passes **64/64 in each of three runs**, including the affected Live
  mining test. Core/site/dispatch coverage and every failed Fast method were
  also exercised separately.
- Docker Compose configuration, glossary links and `git diff --check` validate.
- The parity scorecard, P867/P868 audits and P869 evidence audit validate.
  The latter re-reads **98 native citation ranges**, re-finding named definitions
  at their cited lines. Citation validity alone is not behavioral proof.

The **one** Fast sweep retains its original **11,457 passed / 33 failed /
3 optional Meshy skips** over **11,493 executions**, **778.8 seconds**. Its
infrastructure checks are **9/9**, including the infrastructure timing guard.
Eight failing flight cases exceeded the calibrated ten-second Fast limit.
The sweep was not repeated. Later focused regression cases and fixture repairs
were verified separately; the original receipt is preserved.

All **32 failing methods** have three isolated results in
[P869_VERIFICATION.csv](P869_VERIFICATION.csv). **31 methods pass 3/3 after
recovery**; the remaining radar method records **fail/pass/fail**, exactly as
three runs on a clean **HEAD source copy** in scratch. Its moving-enemy,
time-based off-screen premise is the already-ledgered radar fixture issue;
radar source was not edited.

The failures were handled without relabeling this lane's fixture defects as
load flakes:

| Original Fast cases | Resolution |
|---:|---|
| 13 | Retain side state before world retirement; keep all winner, score, leader and wire assertions. A retired mission's live registry is now empty. |
| 13 | Restore test edits to shared core constants. The sweep directly observed launch speed **123** where a later test expected the loaded **50**. The mutators previously relied on a private load per server. A final 64-case shared-process selection passes 3/3. |
| 1 | Fix the new shutdown fixture's ownership premise: use a unique core path and dispose every acquired test lease, so another live host cannot invalidate its last-consumer assertion. |
| 5 | Run the unchanged bundle-path fixture through a lexical `/proc` alias to scratch, preventing repository `local.env` discovery while retaining all actual writes inside the worktree. |
| 1 | Existing radar-placement issue: final candidate and HEAD each fail/pass/fail; retained as an unresolved fixture failure. |

The temporary constant-override change initially restored the mine fixture's
universe radius when its seat factory returned. It now belongs to the fixture
until disposal; the corrected mine case and the complete shared-core selection
pass three runs. This was repaired, not called a flake. The shutdown publication
check covers both array and direct lease/span sends after shutdown.

The requested P730 method passes **5/5**, **ten complete local matches**, with
one executed case and zero skips verified in each TRX:

| Run | Result | TRX duration |
|---|---|---|
| 1 | pass | 00:03:33.5557223 |
| 2 | pass | 00:04:34.1654136 |
| 3 | pass | 00:02:45.5062376 |
| 4 | pass | 00:02:55.9316958 |
| 5 | pass | 00:02:48.9525013 |

An initial command targeted the Core test project and executed zero cases.
Those attempts do not count. All five counted executions target the actual
ClientCore test project and validate `total=1`, `executed=1`, `passed=1`.

Small receipts are in [P869_assets/verification.json](P869_assets/verification.json),
with before/after scorecards and native-range hashes alongside it. Raw logs/TRX
remain in git-ignored `.scratch/p869/` and
`TestResults/tiers-20261028T225203.304325Z/`. Build outputs, baseline source copies
and temporary caches are removed at handoff. The measured host load during
recovery was about 6–8 on 24 CPUs; no synthetic load was introduced.

No commits or pushes were made. No rig, mini, Mac or other remote host was
contacted.

## Native intent and modern approach

**Native intent:** all requested rows are B/design behavior. Explicit ordered
registries, stable identity, authenticated ownership, catalog intake and
last-consumer cache retirement are real state contracts. C++ allocation syntax
and managed GC do not supply an equivalence argument by themselves. The remaining
text-core branch stays partial and earns no completion credit.

**Modern approach:** retain those contracts through read-only live views,
reference-identity removal, explicit disposable leases and separate authenticated
roles. Retained data can stay memory-safe after eviction while a new
load still observes native cache retirement. A future text-core importer should
feed the same loader/cache lifecycle rather than bypassing it.

## P869b review fixes — 2026-10-28

Applied WIP `113f3bd6` without committing onto dispatch `30e814e4`. The first
literal `git cherry-pick --no-commit` failed because this worktree's real Git
metadata is read-only. A temporary Git directory in `.scratch/p869b-git/`,
using read-only object alternates, performed the same cherry-pick successfully;
the real worktree/index was not committed or pushed. The earlier P869 findings
above are historical; this section supersedes their implementation and gate
claims where stated below. No remote hosts were contacted.

### Allocation and registry behavior

The unused `ConditionalWeakTable` is gone. Models, stations, asteroids and warps
hold their own nullable site/owner fields. Registration initializes ownership;
`GetThingSite` alone allocates the wrapper and binds the model's existing
visibility. The actual server factory only constructs that wrapper
(`ServerHost.cs:63`); native's constructor only stores owner and attribute flags
(`FedSrv/FedSrv.CPP:10692-10709`). Scanner/visibility state continues to live on
the model and in the sector sweep. Retained site identity survives sector moves;
termination clears it; disposal clears bindings and backlinks. An unused site
costs fields on the model, with no separate object or dependent handle.

The ordered registry uses linked nodes with a reference-identity dictionary.
Membership and deletion of the first identical reference are O(1), including
duplicates; append order, first matching ObjectID lookup and live view identity
are preserved. Indexed view access walks links; production iteration uses the
enumerator. This follows the native list contract
(`Igc/igc.h:5130-5197`), without a fresh LINQ closure/linear membership scan on
every ship or treasure move.

Goto orders reserve the native monotonically allocated buoy ID immediately,
but use intrusive ship links to retain pending order sequence. They allocate no
buoy, position array or registry node until `GetBuoy`/`GetBuoys` (or explicit
buoy insertion) observes the registry. Once observed, live views continue to
reflect each subsequent mutation. Clearing/replacing an unobserved order is
O(1); deleting an observed registry entry is also O(1). Native command ownership
and last-consumer removal remain the contract
(`Igc/shipIGC.h:736,782`, `Igc/buoyigc.h:76-88`), not the timing of an otherwise
unobservable managed allocation.

`P869bReviewTests.Projectile_emission_retirement_moves_and_goto_allocate_no_bytes_after_warmup`
warms 100 logical ticks, then measures three 500-tick windows. Each tick adds and
terminates 240 precreated projectiles through real `Sector.Add/Terminate`, moves
a ship and changes its goto. **Every window allocates zero bytes**. Reusing the
projectile objects isolates the new registration overhead: this does not claim
weapon/projectile construction or a complete combat tick is allocation-free.
The final waypoint materializes correctly after measurement. A second test
checks deferred buoy order, cached live views, replacement and termination.

### Three-arm local performance evidence

Existing `tools/P835Bench`, unchanged in all arms: 400 ships, 240 sustained
projectiles, 60 initial mines, production `PCore001.igc`, `m2xspiderw.igc`,
seed 835, 60 warm-up + 500 measured ticks, Release JIT, 24 logical CPUs.
Master is dispatch `30e814e4`; P869 is the **cherry-picked index on that same
base**, not the older standalone branch tree. Each arm ran in three separate
processes; order was master/P869/P869b, reversed in run 2. All nine finish with
400 ships and 240 projectiles. Other lanes may use this shared host; there was
no synthetic load. Only `verified-bench-*.log` is accepted evidence; preliminary
runs with a missing artwork path or an incorrectly prepared reference tree are
excluded.

Values are median-of-three **amortized mean / conditional p95 milliseconds**.
Nested buckets must not be added to their parents.

| Step | master | P869 | P869b |
|---|---:|---:|---:|
| Mission.Update wall | 1.93 / 5.54 | 1.89 / 5.69 | 1.89 / 5.46 |
| step:update | 1.91 / 5.51 | 1.87 / 5.66 | 1.87 / 5.44 |
| sector:1052 | 1.85 / 5.47 | 1.82 / 5.60 | 1.81 / 5.35 |
| sim.models | 0.63 / 1.42 | 0.64 / 1.56 | 0.60 / 1.47 |
| sim.combat | 0.59 / 1.32 | 0.58 / 1.28 | 0.57 / 1.31 |
| combat.detect | 0.49 / 1.06 | 0.49 / 1.02 | 0.48 / 1.06 |
| sim.visibility | 0.35 / 8.68 | 0.33 / 8.68 | 0.36 / 9.38 |
| sim.mining | 0.16 / 0.39 | 0.15 / 0.38 | 0.15 / 0.38 |
| sim.collision | 0.07 / 0.16 | 0.07 / 0.16 | 0.07 / 0.16 |
| sim.prepare | 0.01 / 0.02 | 0.01 / 0.03 | 0.01 / 0.02 |
| blast.apply | 0.39 / 0.84 | 0.39 / 0.93 | 0.35 / 0.81 |

| Arm | Run 1 mean/p95 | Run 2 mean/p95 | Run 3 mean/p95 | Median KB/tick |
|---|---:|---:|---:|---:|
| master | 1.93 / 5.72 | 1.93 / 5.49 | 1.88 / 5.54 | 929.0 |
| P869 | 1.83 / 5.51 | 1.89 / 5.69 | 1.91 / 5.79 | 930.9 |
| P869b | 1.89 / 5.60 | 1.89 / 5.46 | 1.88 / 5.46 | 929.7 |

The timing differences are small and do not establish a general throughput win.
P869b removes the separate site allocations/handles; its additional model
fields and persistent registry storage are not literally free versus master.
Total allocation remains dominated by existing combat work. GC collections were
28/20/14 (generations 0/1/2) in every measured process. Per-run buckets and
allocation receipts: [P869_assets/p869b-bench.json](P869_assets/p869b-bench.json).

### Disposal boundary and production-reader audit

Both zero-peer rotation and completed migration now enqueue the retired mission.
`SimPass` disposes queued worlds **after `CompleteBroadcastSnapshot`**, once all
simulation callbacks have returned (`Server.Broadcast.cs:513-516`). Pending
multi-tick migrations retain their old mission until their final phase finishes;
shutdown drains both completed and pending worlds after stopping publication and
transport. The new lifetime test proves the old sides/stations survive the
rotation call, then lose their mission links at the next completed tick, while
the fresh mission remains usable. The existing real-socket
`P807_two_consecutive_handoffs_still_work` also passes **3/3**, each carrying
24 peers through two batched rotations and asserting zero simulation-stage
failures.

Native does not run the mission destructor at the middle of `ProcessGameOver`:
`FedSrv/fsmission.cpp:3437,3490` queues the game-over before resetting the same
mission, while `Igc/missionigc.cpp:3033-3053` releases static-core ownership at
actual destruction. Dynamic termination is separate (`:3073-3125`). The port
rebuilds worlds and therefore needs its own explicit last-reader boundary.

| Production reader / retained data | Why disposal is safe |
|---|---|
| Active `Mission.Update`, sector/model updates, victory and termination callbacks | Rotation can be nested in these callbacks. Disposal waits for their return and the remainder of the outer tick, fixing the early P869 disposal. `ServerHost.MissionTerminated` has no production subscriber; its synchronous notification still sees intact state. |
| Skills/MCP match reports | `DeclareGameOver` writes final `mission.Skills` synchronously (`Server.Lobby.cs:549`) before rotating. Requested skills iterate current `_missions` (`Server.Skills.cs:15`). MCP bot reports read client snapshots/wire state, not server `Mission` instances. Public notable events carry strings only. |
| Scores, departed players, career persistence | `FinalizeMissionScores` folds connected/departed/commander scores and the server career dictionary before rotation (`Server.Lobby.cs:556`). Old-player dictionaries are consumed/cleared before retirement; the persisted career values contain no mission reference. |
| Async lobby/career/squad report | `ReportMissionResultsToLobby` constructs player/team/game/squad DTOs before rotation (`Server.Lobby.cs:561,1377`). `LobbyHeartbeat.ReportResultsAsync:349` owns only `LobbyMatchReport`; its async HTTP continuation never dereferences the mission, side, ship or score objects. |
| GAME_OVER / GAME_OVER_PLAYERS / side-only results | Encoded byte packets and recipient IDs are frozen before migration (`Server.Lobby.cs:563,1616`). Encoding is synchronous. No delayed result builder owns the old mission. |
| Broadcast workers, send queues and captures | Snapshot records contain detached wire values/owned orientation copies and encoded bytes. `CompleteBroadcastSnapshot` clears projection caches and submits the snapshot (`Server.BroadcastSnapshot.cs:154-171`). `PublishBytes/PublishMemory` read no live mission data. Existing snapshot graph validation rejects simulation objects (`BroadcastSnapshot.cs:267-286`). |
| Lobby heartbeat, UDP discovery, cross-thread live statistics | Simulation creates value snapshots under `_statsLock`; consumers use `LiveGameParams`, counts/names and discovery DTOs (`Server.Lifecycle.cs:267-298,893-939`, `Server.Broadcast.cs:202-223`). `_lobbySnapshotMission` is only an identity token for dirty-version comparison, replaced on the next snapshot; its old sides are never dereferenced. |
| Rotation preparation/reseating/join bursts | `PendingRotationTail` is the intentional owner of old models through all batched phases. Retirement is queued only after the last burst/readiness/countdown phase (`Server.Lobby.cs:1156`). Rejoin/reconnect state and projection caches are retired before old-world disposal. |
| Skirmish observation, seat changes and relaunch | `SkirmishHost.OnSimulation` and `Observe` read the current mission under `_simGate`, returning detached DTOs (`SkirmishHost.cs:48`, `SkirmishObservation.cs:69-105`). Bot lifecycle/launch queries use `ConnectionSession.World` and mission-generation checks; no old server mission escapes that lock. Training retains its own mission for the training lifetime and does not use match rotation. |
| Client/bot world transitions | `GAME_OVER` is consumed in stream order; client `BeginNextMission` clears entities and releases its own core lease (`ClientWorld.cs:5087`), then the normal join burst reacquires. Bot export accumulators clear at the receive boundary (`Bot.cs:2076`). These are independent client objects, not the disposed server world. |
| Shutdown | Publisher and transport stop before missions are disposed; retired queue and unfinished rotation tails are drained (`Server.Lifecycle.cs:2406-2416`). Retained static-core data stays valid for other leases. |

### Shared core, live consumers and malformed catalog

Client cache keys now include a `client:` namespace, resolved asset path, last-write UTC
ticks and file length. The production `ConnectionSession` test holds two worlds
on one revision, edits a scratch core and its mtime, then proves a third world
loads the new version while the first two retain the original. Server named-core
cache hits still precede file I/O, as native requires
(`Igc/missionigc.cpp:2390-2431`); the client revision key does not replace that
server behavior.

Production-core mutation audit: cache consumers in server boot/rotation,
mission/sector simulation, equipment/ship creation, civilization/research,
client joining and bot views read static records. `StaticCore.Load` builds the
private dictionaries before publication; runtime type adjustments use record
copies and runtime mounted parts. Float/damage constants have no production
write site. `GlobalAttributeSet` clones input/output arrays and `Apply` returns
a new set (`GlobalAttributeSet.cs:170-220`), so research cannot mutate the civ's
shared base attributes. The two-server test compares the serialization of the
**entire loaded core** before/after 100 production simulation ticks per server.
It also proves the test override gives one mission different float and damage
arrays while the other server's constants remain unchanged. `CoreConstantOverride`
now clones the core and both constant-array levels before editing, instead of
mutating and restoring a shared cache entry. Other direct constant-edit fixtures
load their own `StaticCore` or synthetic records; they do not acquire server cache
leases. Read-only APIs still contain arrays, so this audit/test is not a type-level
immutability guarantee for arbitrary future callers.

The rejoin regression now sends real LOGONREQ, POSITIONREQ, advances the normal
seat selection, awards a score, calls the production departure path, and logs on
and seats a new connection with the same name. It checks a new ship ID, reused
numeric connection identity, the same restored score object, consumed old-player
entry and the joiner's actual PLAYERINFO kill count. It no longer seeds
`_oldPlayerScores`. The unused `PlayerScore.StandaloneStateId` marker is removed;
standalone IDs bind at validated logon on the connection. SIM-0837's descriptions
now say that explicitly.

SIM-0739/0740 now have production consumers: `FindShipAnySide` uses `GetShip`
(native `GetModel(OT_ship)`, `Igc/missionigc.cpp:3396-3397,3708-3715`), and join
PLAYERINFO enumerates `GetShips` including docked/unclustered NPCs. A real
ORDER_CHANGE regression targets a peerless, unclustered NPC, observes removal
and reinsertion through command state, and checks the join PLAYERINFO contains
that same NPC. The registry-only unit tests remain as duplicate/order contracts.
SIM-0170/0634 constructor citations now start at `FedSrv.CPP:10692`, excluding
the unrelated receive-thread setup; the citation audit re-read all 98 ranges.

Native `LoadStaticMaps` (`FedSrv/FedSrv.CPP:211-231`) ignores every `fscanf`
return value. It neither throws nor explicitly skips a malformed row; malformed
C++ data can be uninitialized or overflow fixed buffers, so it has no defined
safe output to reproduce. The managed loader continues boot, skips unreadable
records, keeps valid later entries and stops at EOF. Unreadable/negative/oversize
counts yield an empty catalog. Tests boot an actual server for malformed counts,
bad rows and truncation. This is an explicit safe handling of undefined native
input, not a claim that native sanitizes it. Valid catalog order/fields and
missing-file behavior are unchanged.

### P869b verification

- Solution build: **0 warnings, 0 errors**.
- `tools/build-tools.sh`: **all 92 projects pass**, zero warnings/errors.
- Linux x64 self-contained server **NativeAOT publish passes**, with no IL2026/IL3050 or other warnings/errors.
- Ownership/dispatch/review cases: **47/47 pass in each of three isolated class runs**; all 14 affected classes were run three times, totaling **327 case passes / 3 baseline failures** across 42 invocations. The sole failing method is P650's mining fixture, independently failing **3/3 on unchanged master** at the same `Assert.NotNull(drilling)`. It is ledgered in `docs/TEST_FLAKES.md`; this is not an all-green affected-test gate.
- Exactly **one Fast sweep**: **11,492 pass / 22 fail / 3 skip**, 838.33 s, infrastructure checks pass. It is **not a green Fast gate**. Five path-resolution failures come from locating this repo's `local.env` through worktree-local `TMPDIR`; all nine cases in that class pass in each of three reruns using a `/proc/<parent>/fd/<directory>` alias into scratch, with no writes outside the worktree. The 16 NPC/layer/miner failures each reproduce 3/3 on both candidate and unchanged master with identical assertion messages. The warp-bomb case passes 3/3 on both arms in targeted reruns; its single suite failure is unreproduced in isolation. Per-case evidence and classifications: [P869_assets/p869b-failures.csv](P869_assets/p869b-failures.csv). Existing >10 s timing warnings remain visible; their tier tags were not changed.
- The real-socket **24-peer, two-consecutive-rotation regression passes 3/3**, zero simulation-stage failures in all three.
- Showcase never-parks: **5/5 passes** on the final Release server binary. Durations **189.70 / 229.19 / 225.50 / 194.24 / 221.44 seconds**; longest speed<1-with-target samples **0.000 / 0.104 / 1.109 / 1.116 / 0.000 seconds**, all below 5 s. Each run reaches the required 24/30 pilots and records combat hits. No post-measurement source changes were made.
- Native P867/P868/P869 audits, glossary links, parity scorecard and Docker Compose config validate. No new service/env setting beyond the inherited P869 bot-auth configuration; its compose wiring is included.

Final gate receipt: [P869_assets/p869b-verification.json](P869_assets/p869b-verification.json).
Small evidence: [P869_assets/p869b-isolated.json](P869_assets/p869b-isolated.json),
[P869_assets/p869b-native-citations.json](P869_assets/p869b-native-citations.json)
and the benchmark JSON above. Raw logs/TRX and the AOT executable are local under `.scratch/p869b/`; temporary
baseline source/build copies were removed after verification. Only completed,
named verification runs count. No commits or pushes were made.

### P869b native-intent footer

**Native intent:** ordered identity-bearing registries, current model visibility,
command-target ownership, retained rejoin scores and shared-core consumer lifetime
are observable behavior. The port preserves them while deferring allocations
that have no reader and waiting until the last synchronous world reader returns.
The undefined malformed-file case is explicitly classified above. SIM-0788's
text-core import branch remains partial; no additional parity completion is claimed.

**Modern approach:** intrusive pending orders, reference-indexed ordered ownership,
lazy model-owned wrappers, revision-aware client caches and detached report/wire
DTOs make those lifetime boundaries explicit. They avoid a dependent handle per
projectile without removing native identity or retaining an active retired world.

**The two-match 200v200 rig A/B, run by Claude, remains the acceptance test for
items 1–2 (hot-path cost and rotation safety). Local microbenchmarks, zero-byte
registration loops and loopback tests do not certify that rig gate.**
