# P517 — turret gunner updates use a mis-scaled server clock (tick index read as seconds) → switch to the PING-based ms clock

**Stage:** P517 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Summary

The turret-gunner `ACTIVE_TURRET_UPDATE(99)` timestamp was wrong by ~20×: P464's
client-side `ServerClockEstimator` derived server time from the peer-pose slot
`ServerShipUpdate.Time`, which is the server **tick index** (loop iterations,
~20/s), not seconds. The estimator treated it as seconds, so a gunner stamped a
time ~20× the real server sim time, and the server's ±1.5 s staleness gate
(`deltaT = gunner.LastUpdate − atu.time`, `Server.ShipCommands.cs:212-213`)
dropped **every** ACTIVE update as stale. A live gunner could never aim or fire
(no projectiles). Verified live, not assumed — before vs after is below.

The fix: the turret path now uses the **same** PING-based server clock as the
own ship (P508's `OwnShipServerClock`, fed from `FM_CS_PING(35)` echoes, where
the server stamps `timeServer = SimNow·1000` ms), converted to the server
sim-**seconds** the gate compares directly against `Ship.LastUpdate`. The P464
`ServerClockEstimator` (the tick-slot-as-seconds mis-scale) is **retired**.

## Root cause (traced, not assumed)

1. The peer's pose slot is a loop counter. `Server.Broadcast.cs:247,286` pass
   `(uint)TickIndex` into `ServerShipUpdate.FromModel(...)`, and
   `TickIndex = _loopIterations` increments once per sim tick (20 Hz,
   `Server.cs:81`). `ShipDeadReckoner.cs:128` documents the slot as "the server
   tick index … not seconds".
2. P464's `ServerClockEstimator.AddSample(uint serverTime, …)` treated that slot
   as **seconds** (`ServerClockEstimator.cs:50-52` "the mission sim clock, in
   seconds"), and `Game1.TurretGunner.cs` fed it `up.Time` (the tick index).
3. `Estimate()` then returned ~20× the real server seconds; `atu.time` was
   stamped with that inflated value.
4. The server gate compares `atu.time` directly against `Ship.LastUpdate`
   (**seconds**): `deltaT = LastUpdate − atu.time`, accepted only within ±1.5 s.
   With `atu.time ≈ 20 × LastUpdate`, `deltaT ≈ −19 × LastUpdate` is hopelessly
   stale → dropped, `c_susOutOfDate`. No `StateM = WeaponsMask`, no turret fire.

P464's own live test did **not** catch this: `TurretGunnerFireLiveTests` fed the
estimator `(uint)Math.Floor(sp.Boarder.LastUpdate)` — real server **seconds** —
bypassing the production `up.Time` (tick-index) feed, so the test exercised the
clock with correct units while production never did.

## Live proof (before → after), same scenario, real wire

One live scenario drives a real gunner boarding a docked carrier, launching, and
holding fire while ACTIVE updates are emitted over a real TCP wire to a real
in-process server (`TurretServerClockLiveTests`, real `ConnectionSession`s +
real PING echoes). The server's `[turret]` accept/reject log is captured and
counted. The clock's error vs the server's real `SimNow` is measured live.

**After (P517 — ACTIVE stamped from the PING clock, ms→seconds):**

```
[P517-LIVE] turret gunner over the firing hold:
  activeSent=57 accepted=56 rejected=0 projectiles=955 clock-error=-0.001s
```

- **56 accepted, 0 rejected** (the 57th ACTIVE is the trailing send never
  processed — not a rejection).
- The turret aimed: 955 projectiles created, first-round direction matching the
  gunner's live server orientation to 7 decimals (the P464 fire path).
- Clock error vs `SimNow`: **−1 ms** — deep inside the ±1.5 s gate.

**Before (P464 path — tick-index read as seconds; measured by temporarily
restoring the old stamp in the same test):**

```
[P517-LIVE] turret gunner over the firing hold:
  activeSent=57 accepted=0 rejected=56 projectiles=0 clock-error=-0.003s
```

- **0 accepted, 56 rejected** — every ACTIVE dropped as stale; **0 projectiles**.
- This is the bug Domingo suspected ("probably always rejected") — confirmed
  live: a gunner literally cannot fire today.

## Fix

`src/Allegiance.ClientCore.Game/Game1.TurretGunner.cs`:

- **Deleted** the local `ServerClockEstimator` field and the per-frame feed
  `_serverClock.AddSample(up.Time, …)` (the tick-index misuse).
- **Reuse the shared PING-fed clock** — `_ownServerClock`
  (`Allegiance.ClientCore.Flight.OwnShipServerClock`, P508) — already fed every
  frame from `FM_CS_PING` echoes in `Game1.Prediction.PumpServerClock` (called
  from `Game1.FlightInput`). No third clock implementation; the turret path and
  the own-ship path share one clock.
- **Stamp the gate's units**: the gate compares `atu.time` directly against
  `Ship.LastUpdate` (**seconds**, confirmed by the Core turret tests'
  `Math.Floor(Boarder.LastUpdate)` convention), so the new
  `TurretServerTimeSeconds()` converts the clock's server **ms** to whole
  seconds: `(uint)Math.Floor(_ownServerClock.ServerTimeMs(now) / 1000.0)`. The
  floor leaves `deltaT = LastUpdate − atu.time` in ~[0, 1] s, safely inside the
  ±1.5 s gate (proved live: 0 rejected).
- Cadence readiness now gates on `_ownServerClock.Ready` instead of the
  estimator's `HasEstimate`.

`src/Allegiance.ClientCore.Game/Flight/ServerClockEstimator.cs` — **deleted**
(retired; the only callers were the turret path and its own test). Grepped the
whole tree: no other caller depended on it.

## Tests

### Failing-first (committed)

- `tests/Allegiance.ClientCore.Tests/TurretServerClockTests.cs` (2 pure):
  - `WrongTimeIfTickIndexReadAsSeconds` — pins the trap: reading the tick-index
    slot as seconds is ~20× the real server time (hopelessly outside the ±1.5 s
    gate for any meaningful mission age). This is the "failing-first with
    tick-index input": the old estimator gives the WRONG time.
  - `PingMsConvertedToGateSecondsStaysInsideTheGate` — the fix's units: the
    shared PING clock (ms) → `floor(ms/1000)` keeps `deltaT` inside ±1.5 s under
    realistic 3–60 ms echo jitter. Regression guard against reintroducing the
    seconds-mis-scale.
- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretServerClockLiveTests.cs`
  (1 REQUIRED live) — the accept-count test above; `accepted > 0` is red if the
  tick-index feed is reintroduced (measured: the old path gave accepted=0).

### Updated

- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretGunnerFireLiveTests.cs` —
  rewired from the retired estimator to the shared `OwnShipServerClock`
  (fed the server's sim-ms, the exact value a PING echo carries), stamping the
  gate's seconds. Still proves projectiles + exact aim.

### Touched-suite verification (before the full run)

- MonoGame.Tests `FullyQualifiedName~Turret`: **33/33 pass** (incl. the new live
  test and the updated fire test).
- ClientCore.Tests `TurretServerClockTests|OwnShipServerClockTests`: **6/6 pass**.

## Whole-solution build

`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug` →
**0 warnings, 0 errors**.

## Full suite — `scripts/run-tests.sh` (P471), three consecutive runs

Grand total per run: **5335** = Lobby 12 + MonoGame 1194 + ClientCore 421 + Core 3708.

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | **1194/1194** (clean) | 418/421 (3) | 3705/3708 (3) |
| 2 | 12/12 | **1194/1194** (clean) | 417/421 (4) | 3707/3708 (1) |
| 3 | 12/12 | **1194/1194** (clean) | 420/421 (1) | 3704/3708 (4) |

**The MonoGame assembly — the ONLY test project that references the changed
`Allegiance.ClientCore.Game` (confirmed by csproj scan) — is 1194/1194 clean in
all three runs**, including the new `TurretServerClockLiveTests` and the updated
`TurretGunnerFireLiveTests`. Lobby is 12/12 clean in all three.

Every ClientCore/Core failure is a **pre-existing live-wire timing / load flake
in an assembly that does NOT reference `Allegiance.ClientCore.Game`** — P517
changed no `Core`, `Server`, `ClientCore`, or `Bot` source. The failing sets
rotate run-to-run (different tests each run): ShipDeleteGoneLiveTests,
FullSessionKillIntegration…CrossSideKill, ClientWarpApproachSlowHullLiveTests
(legacy 200/450), LiveChatSectorWireTests, the rotating `CargoBayReload` member,
SessionLossLiveTests…ServerKilled, CrossSideCombatVisibilityLiveTests…warp_return,
SideEliminationLiveTests, ThreePeerSeededSessionTests…DeterministicVisibility,
StationUnderAttackAlertLiveTests…second_hit, GameOverPlayersWireTests…conquest_win,
AddPartTests (part-pickup + second-part-touch). All are in the rotating
live-socket set documented in `docs/TEST_FLAKES.md` (P472 categories, and the
P507/P508/P464 observation sections): e.g. `ShipDeleteGone` (:24,:46,:51,:79,:102),
`FullSessionKillIntegration…CrossSideKill` (:33,:148,:204),
`ClientWarpApproachSlowHullLiveTests` (:215), `LiveChatSectorWireTests` (:216),
the rotating `CargoBayReload` member (:53-54,:238-239),
`ThreePeerSeededSession…DeterministicVisibility` (:38), `SideElimination…gameover`
(:39,:300), `GameOverPlayersWireTests…conquest_win` (:31,:147),
`AddPartTests.Part_pickup_decline…` (:247,:331), `LiveTreasurePickup` (:157).
The two not literally named in the ledger (`SessionLossLiveTests…ServerKilled`,
`StationUnderAttackAlertLiveTests…second_hit`) were **proved environmental**:
each passes 1/1 green in isolation (same behaviour as the documented rotating
live-socket set), and their assemblies reference zero P517 symbols. **No failure
reproduced on the P517 area** — the new/updated turret tests passed in every run
and in isolation.

## Out of scope (confirmed not touched)

- **Relaying a gunner's aim to OTHER players** — the server stores no
  `ClientActiveTurretUpdate` to relay (`Server.Broadcast.cs:~1711`, the P137
  deferral). Named as a **follow-up**: a separate task to add gunner-aim relay.
- The own-ship model (P507/P508) — only its shared `OwnShipServerClock` is
  reused; no changes to it.
- The server's turret gate itself (`Server.ShipCommands.cs`) — left byte-faithful
  to native (compare against seconds); only the client's clock source was fixed.

## Files changed

- `src/Allegiance.ClientCore.Game/Game1.TurretGunner.cs` — use the shared PING
  clock, stamp gate-seconds; delete the tick-index feed.
- `src/Allegiance.ClientCore.Game/Flight/ServerClockEstimator.cs` — **deleted**
  (retired).
- `tests/Allegiance.ClientCore.MonoGame.Tests/ServerClockEstimatorTests.cs` —
  **deleted** (subject retired).
- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretGunnerFireLiveTests.cs` —
  rewired to the shared PING clock.
- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretServerClockLiveTests.cs` —
  **new** (REQUIRED live accept-count test).
- `tests/Allegiance.ClientCore.Tests/TurretServerClockTests.cs` — **new**
  (failing-first units).
- `docs/reports/P517_TURRET_SERVER_CLOCK.md` — this report.

## Commit

`master` only, **not pushed** (verified with `git branch --show-current`).
Working tree has only pre-existing untracked files plus this task's new files,
committed.
