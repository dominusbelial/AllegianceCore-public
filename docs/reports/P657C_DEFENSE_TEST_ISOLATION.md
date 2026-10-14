# P657c — defense test isolation

## Result

The order dependency is a captured copy of a mutable flag, not a shared defense
incident or roster. `BotManager.DecisionSnapshotEnabled` was a `static readonly`
copy of `ClientWorld.DecisionSnapshotEnabled`. The world flag is deliberately
mutable for scoped test overrides, so that copy recorded whichever test's mode
was active when the CLR first initialized `BotManager` and retained it for the
rest of the process.

A deterministic reproduction on the original controller failed **exactly the
seven defense cases in the brief** (6 passed / 7 failed), without opening sockets.
After the fix, all 151 selected bot/snapshot cases passed in six explicit orders
and initialization-mode combinations. The real xUnit executor also passed all
709 cases outside the `LiveNetwork` collection with both initialization modes.
Whole-solution build: **0 warnings, 0 errors**. No commits or pushes were made.

The two named live tests were inspected but cannot be executed in this sandbox:
creating a loopback socket fails with `SocketException (13): Permission denied`.
The regular VSTest runner itself fails on its control socket before executing any
test, including socket-free tests. This report does **not** claim a fresh full
807-case/live pass.

## Root cause and failure path

The old declaration in `tools/allegiance-mcp/BotManager.cs` was:

```csharp
internal static readonly bool DecisionSnapshotEnabled = ClientWorld.DecisionSnapshotEnabled;
```

The snapshot test classes set the world flag to `true` in their constructors and
restore it in `Dispose`. Bot regression tests also enable it temporarily for
snapshot probes. If `BotManager` initializes while one of those scopes is active,
its copy stays `true` even after the world flag is restored to the default `false`.
`DefenseLiveReads` then sets the world flag to `false` for its wire-applied,
socket-free worlds, correctly suppressing snapshot publication, while the
controller continues choosing snapshot reads.

`Session` applies an own ship/sector to a fresh world. The unpublished
`DecisionSnapshot.Empty` does not have that world's mission generation.
`DriveCombat` returns at its snapshot generation guard before defense, pose,
travel, or warp-hop logic. This explains the immediate failures. The bounded-wing
test's selection succeeds through live world reads, but its final `DriveCombat`
never resumes staging. Tests exercising only alert assignment/settings do not
need this combat read path and still pass.

If initialization captures `false`, those same defense tests pass. Serializing
flag writers in one collection cannot undo a value already frozen into another
type's readonly field. This accounts for the isolated/full-suite difference and
the process/order-dependent pass rate.

For `ClientReadyUpUnfreezesNormalMapLiveTests` and
`BotAcceptsAndEngagesOrderLiveTests`, real receive dispatch uses the restored world
flag and stops publishing snapshots, while the old controller can continue
reading snapshots for movement and engagement. That is the same read/write
mismatch on their production `BotManager` instances. Its connection to the two
reported live failures follows from code inspection; their live outcomes were
not remeasured here.

## Changes

- The bot-side flag is now a forwarding property:
  `internal static bool DecisionSnapshotEnabled => ClientWorld.DecisionSnapshotEnabled`.
  All existing bot read branches follow the same current setting as publication.
  Production still initializes the world setting once from the environment;
  its default and A/B configuration semantics are unchanged.
- Added a `DecisionSnapshotFlag` collection definition with
  `DisableParallelization = true`. All existing flag writers already belong to
  that collection and save/restore their overrides. The definition also excludes
  concurrent readers/publishers in unrelated unit-test collections, such as the
  P657 receive-dispatch tests. `LiveNetwork` already has the same nonparallel
  guarantee, so neither named live class needs a second collection or a new flag
  override.
- The per-test, uninitialized `BotManager` helper now injects default
  `DefenseSettings` with `Read(_ => null)`. Tests no longer inherit an operator's
  `ALLEGIANCE_BOT_DEFEND`, share, or cap environment settings. Individual tests
  still override that instance for disable/clamp/cap coverage. All incidents,
  bot rosters, sessions, objectives, and route state remain per-test instances.
- Added `DecisionReadMode_FollowsWorldFlag_AfterBotManagerInitialization`, with
  two theory rows. It initializes the controller, publishes sector 1, moves the
  live ship to sector 2/x=200 while retaining the old frame, and switches modes
  repeatedly. It checks both sector and pose reads, plus zero publication while
  off, and restores the flag in `finally`. Its two rows failed with the old code.
  A process can initialize a type only once; the verification helper additionally
  forces each cold initialization mode in separate processes.
- Added `scripts/verify-defense-isolation`, a diagnostic in-process helper so the
  ordering checks are reproducible without VSTest's control socket. Explicit
  order mode runs the existing test bodies/assertions with fresh instances,
  expanded `InlineData`, numeric argument conversion, and `IDisposable` cleanup.
  `xunit` mode uses xUnit discovery/execution, including its collection scheduling,
  fixtures, and theory handling, for every non-`LiveNetwork` collection.

No existing assertion was weakened, test removed, retry added, or test quarantined.
The helper is separate from the solution and does not change normal CI discovery.

## Shared-state inventory

This inventory covers the defense tests and their `BotFixRegressionTests` partial
class helpers, the snapshot classes that supply the initialization trigger, and
the two named live classes' controller/session/server paths. Mutable contents of
readonly references and captured environment settings are included. Lock objects,
stateless comparers, and lookup tables never modified by these paths are identified
separately so a readonly reference is not mistaken for isolated runtime state.

### Controller, world, and tests

| Owner / exact names | Access and isolation assessment |
|---|---|
| `ClientWorld.DecisionSnapshotEnabled` (auto-property backing field) | Read by publishers and controller; written by snapshot-test constructor/Dispose scopes, bot snapshot probes, and `DefenseLiveReads`. All writers use `DecisionSnapshotFlag` and restore their saved value. Now globally excluded from parallel collection execution. **Root-cause source flag.** |
| Old `BotManager.DecisionSnapshotEnabled` | Read by combat, sector, pose, warp, and spatial-index paths. Its one-time copy survives a correct scope restore. **Root-cause captured state; replaced with forwarding property.** |
| `ClientWorld.DecisionSnapPublishIntervalMs` | Static mutable publication floor, initialized from `ALLEGIANCE_BOT_SNAPSHOT_PUBLISH_MS`. These tests read it indirectly through publication; no ClientCore test reassigns it. The similarly named constant in `DecisionSnapshotTests` is a local constant, not a mutation of this field. |
| `BotManager._frameSession`, `_frame`, `_frameNow` | Thread-static decision frame/time cache. `DecisionFrameScope` saves all three and restores them on disposal, including nested scopes and early returns. The snapshot-frame regression disposes its reflected scope. No abandoned scope found. |
| `BotManager._snapAgesMs`, `_snapAgeCount`, `_snapAgeNext` | Shared, locked observability ring and cursors. Live aim records ages; the partial-class bounded-age test resets cursors before checking exact ring contents. No defense or named live assertion depends on these samples, and samples do not select movement/targets. `SnapAgeGate` is its shared lock. |
| `BotManager.TtkFirstHit`, `TtkSamples` | Shared, locked combat timing telemetry. `SampleCombatStats` can update them from hits/kills; `ResetCombatTiming` clears them for measured arena runs. They do not feed defense eligibility, read mode, routes, or engagement. Neither named live test asserts TTK; defense hit assertions inspect per-bot targets/attackers instead. `TtkLock` is the shared lock. |
| `BotManager._podDockBays` | Mutable station-type/landing-geometry cache, consulted on pod docking paths (also possible after a live death). Geometry is derived from the fixed representative core/artwork. Defense combat fixtures are fighters, not docking pods. No target/read-mode setting lives here. |
| `BotManager.HarnessCore` | `Lazy<StaticCore>` memoization. Grant validation and combat hull/weapon lookup can initialize it. Tests read the fixed corpus and never modify its type tables. |
| `Catalog.Core`, `Catalog.Fight` | Lazy memoized core and default hull/weapon selection; shared readonly references with initialization state. Read by defense target classification and live spawn resolution. No tested path modifies the cached core/loadout selection. Returned default-part lists are built per call. |
| `BotManager.IgnoreMinerTargets`, `LegacyNearestFlag`, `SnapshotDeadReckonEnabled`, `SyncWings`; `CombatTargetSelector.SpreadFirst` | Readonly settings captured from `ALLEGIANCE_BOT_TARGET_MINERS`, `ALLEGIANCE_P622_LEGACY_NEAREST`, `ALLEGIANCE_BOT_SNAPSHOT_DEADRECKON`, `ALLEGIANCE_BOT_SYNC_WINGS`, and `ALLEGIANCE_P622_LEAST_ENGAGED`, respectively. No test in this assembly mutates those environment keys. They do not copy a test-mutated flag. |
| `Catalog.ArtRoot`; each named live class's `ArtRoot` | Readonly artwork-path captures. No implicated test changes `ALLEGIANCE_ARTWORK_PATH`; they resolve the same local corpus. |
| `BotFixRegressionTests.BotType` | Readonly reflection `Type`, not a shared bot/fixture. `Manager`, `Bot`, and `Session` create new instances each invocation. The nested `DefenseLiveReads._saved` is an instance field. |
| `ClientWorld.EmptySideFunding`, `ShipIdComparer.Instance` | Shared empty funding dictionary exposed read-only, and stateless comparer. No write path in these tests. World ships, events, graph, generation, decisions, and snapshot publication counters are instance fields. |

`BotManager._defenseSettings` and `_defenseIncidents` are **instance fields**, not
statics. `DefenseConfig` lazily reads environment settings per manager; the test
helper now supplies the instance setting. `StallMetrics` has no static runtime
state. `StallWatchdog` owns its metrics, sessions, logs, sampler, and threads per
instance; its only static object is `JsonOptions`, configured once and not mutated
by tests. `ObservedSession` has only static accessor methods that return fields on
the supplied instance, not static storage. `SpawnBatch` owns results, failures,
and refill state per instance. `SpawnLaunch.TimeoutMs` reads the environment on
each access and holds no cached static timeout.

### Dependencies reached by the live paths

| Owner / exact names | Access and isolation assessment |
|---|---|
| `TcpTransport.DefaultMode`, `SendTimeoutMs`, `OverflowTimeout`, `NoWriteProgressTimeout`, `HumanGraceTimeout`, `CapacityBytes`, `HardCapBytes`, `GreetTimeout` | Mutable transport defaults read during construction/send/timeout handling. Server boot binds `HumanGraceTimeout` from configuration. The implicated tests do not shrink these settings; live classes use the serial `LiveNetwork` collection. Socket-free defense fixtures use a disconnected sender, bypassing socket/queue work. |
| `TcpTransport._wouldBlockCatches`, `_wouldBlockWaits` | Interlocked socket observability counters, never used to decide bot eligibility or targets. |
| `ServerHost.MaxBufferedBytesPerPeer`, `DrainBudgetEvents`, `DrainFairShareEvents`, `MinPingReplyIntervalMs` | Mutable host limits consumed by the real server pump; neither named live test reassigns them. They do not participate in socket-free defense fixtures. |
| `Sector.WarpReEntryGuardEnabled` | Mutable simulation option set explicitly at every server boot (false for both named configurations); no defense fixture uses server sectors. |
| `Server._rand`; runtime `Random.Shared` | Mutable random-generator state. Server jitter/direction and bot seed generation advance them. Neither named test expects a particular random position/seed; movement and specific commanded target are the asserted behaviors. |
| `Collider.SupportCache` | Shared `ConditionalWeakTable` of hull support geometry keyed by the actual `CvhFile`, not by ship/mission identifiers. Core collision simulation can populate it. Other hull/landing/muzzle caches are instance caches. |
| `TickProfiler.Instance`, `SlowTickCaptureActive`, `_label`, `WindowReported` | Shared profiler instance/control/label/event state. Server boot configures the profiler and sim paths consult it. These tests do not install event subscribers or assert profiler data. `StaticGate` protects configuration. No path from profiler output to combat read mode. |
| `ConsoleLog.Verbose`, `_queue`, `_drain`, `_writer`, `_installed`, `TotalWrites`, `EmittedWrites`, `DroppedWrites` | Process-wide console setup and counters; server boot sets verbosity and bot/server logging touches counters/queue as applicable. Neither named test installs a console writer or asserts output text. These do not control movement/targets. |

Additional shared reference objects reached during initialization/lookup are
`Server._debugGarrisonStationIds`, `Separate.Directions`, `NarrowPhase.Subset2_3`,
`Subset2_4`, `Subset3_4`, `IdsFace4`, the wire Latin-1 encodings, and
`FedPacket.EmptyPacket`. These are fixed lookup/encoding/packet data and are not
mutated by the implicated tests. There is no static mission roster, ship world,
observed-session registry, or spawn-batch result storage on these paths.

The P632/P634/P635 live comparison tests set and restore
`ALLEGIANCE_SHIP_UPDATE_MODEL` inside the serial `LiveNetwork` collection. That
setting selects server update construction and does not explain unpublished
snapshot reads in the socket-free defense tests. No implicated test mutates the
defense environment variables; injecting them still removes ambient configuration
as an independent test premise.

## Verification and reproduction

Environment: .NET SDK 10.0.400, Linux, local artwork corpus available. MSBuild
multiprocess mode could not start here; using `-m:1 -nr:false` built successfully.
No external downloads, network-dependent test execution, or approval escalation
was needed.

Failing-first evidence, captured before replacing the readonly copy:

| Run | Result |
|---|---|
| Force controller initialization with flag on, restore world flag off, run only `Defense*` | **6 passed / 7 failed** |
| New `DecisionReadMode*` theory, controller initialized off | **0 passed / 2 failed**, expected snapshot sector 1 but got live sector 2 |

The seven defense failures were `DefenseAfterArrival`,
`DefenseAlert_BoundedNearestWing`, `DefenseDetour`, `DefenseDisableSwitch`,
`DefenseRelaunch`, and both `DefenseTravellingFighter` rows, matching the brief.
The copied flag reproduction does not depend on xUnit's random test order.
Logs: `/tmp/p657c-defense-red.log`, `/tmp/p657c-mode-red.log`.

Final-tree verification:

| Executor / ordering | Controller initialized on | Controller initialized off |
|---|---:|---:|
| Explicit ascending class/method/row order | 151/151 | 151/151 |
| Explicit descending class/method/row order | 151/151 | 151/151 |
| Seed 657 shuffle across all selected cases | 151/151 | 151/151 |
| Real xUnit, all non-`LiveNetwork` collections | 709/709 | 709/709 |

All runs restored the world flag to false; xUnit reported zero skipped tests and
zero infrastructure errors. Discovery found 807 cases, including the two new
regression rows. The 151 ordered cases cover `BotFixRegressionTests` (all 13
defense cases), P657 spawn reliability, P657b stall telemetry, bot spawner
resolution, decision snapshot/allocation, and P636b/c/d snapshot tests. The
explicit runner shuffles individual cases too, so it exercises different ordering
inside the shared partial regression class as well as between classes.

A further shuffled run with `ALLEGIANCE_BOT_DEFEND=off`,
`ALLEGIANCE_BOT_DEFEND_SHARE=0`, and `ALLEGIANCE_BOT_DEFEND_CAP=0` passed **151/151**,
confirming that defense fixture expectations are independent of ambient settings.
Logs are `/tmp/p657c-final-<order>-<on|off>.log` and
`/tmp/p657c-final-hostile-settings.log`.

Reproduce on the final working tree:

```bash
dotnet build AllegianceCore.slnx -m:1 -nr:false -v:minimal
dotnet build scripts/verify-defense-isolation/verify-defense-isolation.csproj -m:1 -nr:false -v:minimal
runner=scripts/verify-defense-isolation/bin/Debug/net10.0/verify-defense-isolation.dll
for order in ascending descending seed-657 xunit; do
  for mode in on off; do
    dotnet "$runner" "$order" "$mode" || exit 1
  done
done
ALLEGIANCE_BOT_DEFEND=off ALLEGIANCE_BOT_DEFEND_SHARE=0 ALLEGIANCE_BOT_DEFEND_CAP=0 \
  dotnet "$runner" seed-657 on
```

Both solution and verification-helper builds finished with **0 warnings / 0 errors**.
Build logs: `/tmp/p657c-build-final.log`, `/tmp/p657c-verifier-build.log`.

On a host that permits loopback sockets, the remaining full/live verification is:

```bash
dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj --no-build
```

The production change removes the captured flag from those live paths too, but a
socket-permitted full run remains the validation limit of this environment.
