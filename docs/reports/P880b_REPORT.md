# P880b — training framework batch 2 (training-matrix rows with queue = P880b)

Framework batch 2 ports the twenty `queue=P880b` rows (GetShipHasCargoCondition through
SetRadarLODAction) against the **real** production path — `ClientCore` scripts → `ITrainingSite` →
the embedded `Allegiance.SinglePlayer.TrainingHost` with ordinary replication — with real model
reads and tests that drive the production binding. **Eight** of the twenty rows are now
**implemented** (five were missing, three promoted from partial) with failing-on-divergence tests
that boot the real embedded host; the rest remain **partial** with precisely what is left. No
commits, pushes, stashes, remote-machine contact or synthetic CPU load. Changes remain in the
working tree. Native citations are relative to native `src/training/`.

## What changed

### Real-model equipment/state/radar/buoy seams on the shared host

`ITrainingSite` gained six real-world operations the framework rows read/write, and
`ITrainingHost`/`TrainingHost` the matching real-model implementations so the client site and the
host site share one source of truth:

- **`HasPartOfEquipmentType(ship, part)`** — ``GetShipHasCargoCondition``
  (getshiphascargocondition.cpp:29-36): walks the ship's real `Ship.Parts` (mounted list; the
  native `GetParts()` does not include the cargo hold) comparing each part's `EquipmentType`.
- **`CaptureShip` / `RestoreShip`** — ``ResetShipAction`` (resetshipaction.cpp:22-36):
  snapshot/restore of real position, velocity, orientation, fuel, shield fraction, hull fraction,
  ammo and energy via `Ship.Position/Velocity/SetOrientation/SetFuel/SetShieldFraction/SetFraction/
  Ammo/Energy`. Native `rotation` (angular velocity) has no modelled C# counterpart and is
  deliberately omitted (`ShipSnapshot.cs`).
- **`ReleaseConsumer(buoyId)`** — ``ReleaseConsumerAction`` (releaseconsumeraction.cpp:25-28,
  `IbuoyIGC::ReleaseConsumer`): the released waypoint buoy no longer resolves through
  `ResolveWaypoint` (the client `Resolve` and the host-site `Resolve` both honour it).
- **`SetRadarLod(lod)`** — ``SetRadarLODAction`` (setradarlodaction.cpp:31-36): the client
  realises it through the shared `TrainingRadarLod` tracker (the mirror `GetRadarLod` reads, used by
  both HUD styles); the host site keeps its scriptable mode.
- **`SetCargoNan(ship)`** — ``SetCargoNanAction`` (setcargonanaction.cpp:22-52): a real host
  cargo recipe (best-effort on core part availability).

Three existing seams were upgraded from diagnostic side-tables to real-model writes/reads:

- **`MountPart`** (behind ``SetDroneNanAction``, setdronenanaction.cpp:36-52) now resolves the
  part type from the mission core (`mission.Core.PartTypes`, with expenditure-launcher
  construction as a fallback), terminates any existing part on the target mount, and mounts a real
  `Ship.Parts` entry (`CanMountPart`-guarded). The scripted set is retained only as a tolerance for
  unresolvable parts.
- **`HasPartMounted`** (behind ``GetShipHasMountedCondition``, getshiphasmountedcondition.cpp:36-45)
  now reads the real `Ship.Parts` for a mounted part id (`MountId >= 0`), OR-ing the tolerance set.
- **`GetFraction`** (behind ``GetShipIsDamagedCondition``, getshipisdamagedcondition.cpp:49-59)
  now reads the real `Ship.Fraction` for a live ship (absent ⇒ 0, the native "assume fraction 0"
  rule), and `DamageShip` writes that real fraction too; the client's `IsShipDestroyed` now also
  treats a retained zero-armour model as destroyed (the `GetShipIsDestroyedCondition` =
  `Not(GetShipIsDamaged(ship, 0.0))` expansion, getshipisdestroyedcondition.h:27-28).
- **`RepairAndRearm`** (behind ``RepairRearmAction``, repairrearraction.cpp:37-74) now restores
  the player's real shield/hull/energy/ammo/fuel (GA-scaled max energy via
  `Ship.EffectiveMaxEnergy`) and fills the first empty cargo slots with missiles/ammo instead of
  logging a no-op.

New framework classes: `GetShipHasCargoCondition`, `ReleaseConsumerAction`, `ResetShipAction`,
`SetRadarLODAction`, `SetCargoNanAction` (`src/Allegiance.ClientCore/Training/P880bPrimitives.cs`)
and the `ShipSnapshot` state record.

## Per-row table

| Row | Native | Status | What is real now | What remains |
|---|---|---|---|---|
| TRN-0030 | GetShipHasCargoCondition | **implemented** | Real mounted part-list equipment-type read (getshiphascargocondition.cpp:29-36) | — |
| TRN-0031 | GetShipHasMountedCondition | **implemented** | Real mounted-part read + real mount (getshiphasmountedcondition.cpp:36-45, setdronenanaction.cpp:36-52) | — |
| TRN-0032 | GetShipIsDamagedCondition | partial | Ship armour reads the real `Ship.Fraction` (getshipisdamagedcondition.cpp:49-59) | Live station real-fraction binding remains |
| TRN-0033 | GetShipIsDestroyedCondition | partial | Client treats a retained zero-armour model as destroyed (getshipisdestroyedcondition.h:27-28) | Incomplete lazy target binding |
| TRN-0036 | GetTargetCondition | **implemented** | Real command target via host (gettargetcondition.cpp:50-53) | — |
| TRN-0037 | GetViewSectorCondition | partial | reads host ViewSector | Real client command-camera sector binding |
| TRN-0041 | MessageAction | partial | text delivered | Native individual commander-chat attribution |
| TRN-0042 | ModifyCreateDroneActionAction | partial | recipe mutation | Live drone behaviour-equipment / non-ship command application |
| TRN-0046 | ObjectMovingTowardsCondition | partial | formula ported | Non-ship target resolution + real hull radius |
| TRN-0047 | ObjectPointingAtCondition | partial | formula ported | Non-ship resolution; radius hard-coded 20 |
| TRN-0048 | ObjectWithinRadiusCondition | partial | formula ported | Station/buoy/probe resolution + fixed ship radius |
| TRN-0057 | ReleaseConsumerAction | **implemented** | Buoy consumer release — released buoy no longer resolves (releaseconsumeraction.cpp:25-28) | Native waypoint AddConsumer lifetime not fully reproduced |
| TRN-0058 | RepairRearmAction | **implemented** | Real shield/hull/energy/ammo/fuel restore + cargo fill (repairrearraction.cpp:37-74) | — |
| TRN-0060 | ResetShipAction | **implemented** | Snapshot-first/restore-later on real ship (resetshipaction.cpp:22-36) | Native rotation not modelled |
| TRN-0061 | SetAutopilotAction | partial | host flag + waypoint motion | Client autopilot mode / replicated command state |
| TRN-0062 | SetCargoNanAction | partial | Primitive + host cargo recipe (setcargonanaction.cpp:22-52) | No mission instantiates it; recipe best-effort on core parts 190/33/59 + hull mountability — no deterministic production-binding test |
| TRN-0063 | SetCommandAction | partial | host Goto + ship/buoy/station | Accepted/current/queued wire state + consumer lifetimes |
| TRN-0066 | SetDisplayModeAction | partial | — | Live SetDisplayMode is a client camera binding |
| TRN-0067 | SetDroneNanAction | **implemented** | Real front-weapon terminate + nanite mount (setdronenanaction.cpp:36-52) | — |
| TRN-0069 | SetRadarLODAction | **implemented** | Window radar mode set when it differs (setradarlodaction.cpp:31-36) | — |

## Verification (light gate)

- **Solution build: 0 warnings / 0 errors.**
- **`tools/build-tools.sh`: all 93 tool projects built, 0/0.**
- **New + affected suite 3× alone**: `P880bFrameworkBindingTests` (13) + `P880aFrameworkBindingTests`
  (12) + `TR5TrainingMissions810Tests` (3) — **28/28, three times alone**. Eight of the thirteen are
  `[Tier("Live")]` production-binding tests that boot the real embedded `TrainingHost` through
  `TrainingHostSite` (the same host the client's `ClientTrainingSite` forwards to) and assert real
  ship parts, real fraction, real snapshot/restore, real buoy release, real radar LOD and real
  resource restore.
- **Fast tier** (`tools/run-tests.sh --tier fast`): **11,682 tests, 464.5 s**. Red only on
  pre-existing, unrelated, already-ledgered failures — no `Allegiance.Core` source changed here:
  `P660NextMatchLiveTests.Two_consecutive_matches…` (10.759 s exceeded the 10 s Fast budget — timing
  observation) and `NpcWingmanDefendLiveTests.Defender_disengages…` (the P872-ledgered deterministic
  dispatch-base failure; unchanged code). `Allegiance.ClientCore.Tests` passed **1966/1966** of my
  touched area including the new `P880bFrameworkBindingTests`. The quarantine re-runs passed.
- **Scorecard**: `python3 scripts/parity-scorecard.py` validates the matrix (0 CRLF). Training total
  is now **30.97 % weighted (280/904), 32.49 % unweighted (77/237)** — up from 26.11 %/27.85 % at
  P882a; this batch implemented 8 rows (weight 29).
- **Showcase never-parks 5×**: waived under the Pi-lane rule — no bots/flight/sim code changed (this
  batch is confined to the training-host seam), so the load-sensitive ordinary-showcase test is not
  affected; running it would only consume the shared host during concurrent agent runs.
- **Real-window check: BLOCKED (environmental).** `xvfb-run`/`Xvfb` is not installed on this box and
  there is no passwordless sudo to install it, so the mandated headless (`xvfb-run -a`) lesson launch
  could not be executed (it would otherwise open a window on Domingo's desktop, which the rules
  forbid). Substitute evidence: the eight production-binding tests drive the **same** embedded
  `TrainingHost` the real client drives, and the fast-tier `Allegiance.ClientCore.MonoGame.Tests`
  (3758 passed) exercise the real client/host path. The P882a real-window cohort already certified
  launch and goal progress for all seven lessons at the immediately-prior commit; this batch only
  adds framework bindings on that already-certified host. A reviewer with `xvfb` available should run
  `ALLEGIANCE_TRAINING_START=<lesson>` (Nanite 8 exercises the mounted-part / nanite seams) to add a
  live-window confirmation.

Raw task logs remain under `.scratch/p880b/` (fast-tier log, HUD settings, M8 scratch config) —
git-excluded, none committed. No new images are committed.

## Native intent

**Design:** preserve equipment-type vs. part-id distinction (GetShipHasCargoCondition checks the
equipment *type* across mounted parts; GetShipHasMountedCondition checks a specific *part id* on a
mounted slot), the snapshot-first/restore-later ResetShipAction latch, the waypoint consumer-release
semantic (released buoy no longer resolves), the repair/rearm cargo recipe, and the 
set-radar-mode-if-different LOD action. Both HUD styles are served by the shared radar tracker and
shared host state; no rendering code changed.

**Era constraints:** native direct `trekClient`/IGC writes become in-process host mutations on the
real `Ship`/world plus ordinary loopback replication. No new protocol, service, port or environment
variable was added, so `docker-compose.yml` is unchanged. Native `rotation` (angular velocity) has no
modelled C# counterpart in `ResetShipAction` and is intentionally absent.

**Bugs:** the existing disconnected-dictionary seams for ship *fractions* were replaced by a real
`Ship.Fraction` read; the scripted set remains only as a tolerance for parts the universe cannot
represent (e.g. nanite 190 on a hull that cannot mount it). `SetCargoNanAction` is retained as a
modding primitive (native ships no mission instantiates it), and its real-host recipe is best-effort
on core part availability — hence `partial`, not `implemented`.
