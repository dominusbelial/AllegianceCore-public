# P464 — A boarded turret gunner can aim and fire from the MonoGame client

## TL;DR

Crewing a turret is a signature Allegiance role, and in the port it was a
dead end **client-side**: the server's entire gunner stack (boarding, slot
selection, `Weapon.SetGunner`, `HandleActiveTurretUpdate`, gunner-aimed
firing) was complete and live-tested, but the MonoGame client **never sent
anything** — `ConnectionSession.SendActiveTurret`/`SendInactiveTurret`
existed but had no caller. A seated gunner could look at the turret camera but
could not aim or fire.

This task wires the dead end shut: the gunner's look input now drives a
locally-integrated turret orientation (the pure extraction of the server's
`ExecuteTurretMove`, so turn-rate/arc limits match), that orientation feeds
the turret camera and a dedicated turret reticle, and a control-cadence send
loop emits `ACTIVE_TURRET_UPDATE(99)` while firing / `INACTIVE_TURRET_UPDATE(100)`
otherwise — with a server-clock estimate derived from the server times the
client already carries, satisfying the server's ±1.5 s staleness gate.

---

## 1. Pre-fix evidence (the dead end, recorded before touching code)

On the unfixed `master` (top commit `846544d6`):

```
$ git grep -n -e ".SendActiveTurret(" -e ".SendInactiveTurret(" HEAD
HEAD:tests/Allegiance.Core.Tests/LiveTurretGunnerTests.cs:238:  boarder.SendActiveTurret(...)
HEAD:tests/Allegiance.Core.Tests/LiveTurretGunnerTests.cs:262:  boarder.SendActiveTurret(...)
HEAD:tests/Allegiance.Core.Tests/LiveTurretGunnerTests.cs:279:  boarder.SendInactiveTurret();
```

The **only** callers of the turret senders are the Core test harness's private
`WireClient` (`LiveTurretGunnerTests.cs`) — a manual wire sender, not the
production client path. Grepping the whole `src/Allegiance.ClientCore.MonoGame`
client, `SendActiveTurret` / `SendInactiveTurret` had **zero callers**. A
seated gunner's `IsTurretGunner()` view existed, but nothing aimed or fired.

**Pre-fix behaviour (must-fail baseline, recorded):** with no send loop, the
gunner never transmits an `ACTIVE_TURRET_UPDATE`, so the server never sets the
gunner's `WeaponsMask` (`HandleActiveTurretUpdate` is never reached) and the
gunned turret weapon is never selected — **zero turret projectiles are created**.
Confirmed by the must-fail experiment in §5 (disabling the ACTIVE transmission
reproduces exactly this: "no turret projectiles were created by the server").

---

## 2. What was already done (server, untouched)

Out of scope, verified complete, **not modified**:

- Boarding — `PerformBoard` (`Server.Docking.cs:74`); slot selection
  `ChooseTurretSlot` (`:1263`); gunner binding `Weapon.SetGunner` (`:147-153`).
- `HandleActiveTurretUpdate` (`Server.ShipCommands.cs:202-240`) — faithful port
  of `ProcessShipUpdate(ClientActiveTurretUpdate)` (`shipIGC.cpp:3349-3392`):
  sets `gunner.StateM = WeaponsMask`, installs the controls, catches up the
  orientation via `ExecuteTurretMove`, `SetOrientation`. **This is the ±1.5 s
  staleness gate the client's clock estimate must satisfy** (`:212-213`,
  `shipIGC.cpp:3353-3354`: `deltaT = gunner.LastUpdate − atu.time`, drop unless
  within ±1.5 s).
- Gunner-aimed firing — `Weapon.cs:212-221` (aim follows `_gunner.Orientation`),
  driven by the carrier's per-tick loop (`Ship.cs:6734-6754`).
- Wire codecs — `ActiveTurretUpdate.cs` / `InactiveTurretUpdate.cs` and the
  `ClientActiveTurretUpdate.From(time, controls, orientation)` producer already
  existed and were byte-exact (used by the P273 proof).
- Camera foundation — `Game1.Camera.cs` `IsTurretGunner()`/`TryGetGunnerCarrierPose`.

---

## 3. What was built

### 3.1 Pure turret-look math — `Flight/TurretLook.cs`

A deliberate line-for-line extraction of `Ship.ExecuteTurretMove`
(`CshipIGC::ExecuteTurretMove`, `shipIGC.cpp:1618-1702`): spherical constraint
on the controls, throttle-scaled slew between `c_minRate` 7.5°/s and
`c_maxRate` 75°/s, yaw/pitch/roll applied in place then re-orthonormalized.
The client uses this to integrate the gunner's local aim so turn-rate and arc
limits match the server exactly. Proven by a unit test that drives BOTH
`TurretLook.Integrate` and a real `Ship.ExecuteTurretMove` over a grid of
controls/deltas and asserts numerical agreement (`TurretLookTests`).

### 3.2 Server-clock estimate — `Flight/ServerClockEstimator.cs`

The client had no notion of the server clock (grep found none), yet the
`ACTIVE_TURRET_UPDATE` needs a `time` within ±1.5 s of the gunner's
`LastUpdate`. The estimator reconstructs it from the server `Time` slot
**already carried on the peer's own pose updates** (`ServerShipUpdate.Time`,
the mission sim clock in seconds): a least-squares fit over a sliding window of
`(serverTime, localRecvTime)` samples, mapping local monotonic time onto server
time. The fit both absorbs per-packet latency jitter and extrapolates the
present from the newest sample — exactly the robustness a single "last sample +
elapsed" tracker lacks under a late/early packet. Documented in the class, and
proven by a pure test (`ServerClockEstimatorTests`) that simulates a ticking
server streaming poses under ±0.3 s latency jitter and asserts the estimate
never leaves the 1.5 s gate.

### 3.3 Send cadence — `Flight/TurretGunnerCadence.cs`

Reproduces native's `BaseClient::SendUpdate` parented branch
(`clintlib.cpp:1870-1905`): exactly one turret update per
`c_dsecUpdateClient` (0.15 s, `Messages.h:226`), choosing
`ACTIVE_TURRET_UPDATE` while `stateM & weaponsMaskIGC` (fire held) else
`INACTIVE_TURRET_UPDATE`. An ACTIVE is gated on a ready clock estimate (its
`time` must be stampable), an INACTIVE carries no timestamp and is always safe.

### 3.4 Client controller — `Game1.TurretGunner.cs` + `Game1.FlightInput.cs`

New partial `UpdateTurretGunner(lookAxes, flightStateM, dt)`, called from
`UpdateFlying` **in place of the flight `CommandControls` send** whenever
`IsTurretGunner()`. Each frame it:

1. feeds the clock estimator from the own ship's latest pose `Time`;
2. seeds the aim once per gunning episode (from the gunner's last-known
   orientation — the port transmits no turret resting facing), then integrates
   `_turretOrientation` forward via `TurretLook.Integrate` using the **same look
   axes flight uses** (keyboard arrows + mouse virtual-joystick + joystick, all
   folded into `cmd.Controls`), with throttle overridden to +1 for full-rate aim;
3. at the cadence, sends `SendActiveTurret(time, ClientActiveTurretUpdate.From(time, controls, orientation))`
   while the fire button is held, else `SendInactiveTurret()` — the production
   `ConnectionSession` methods that previously had no caller.

**Flight gating (the brief's "don't send any flight CommandControls while
seated as a gunner"):** the `UpdateFlying` send site now branches on
`IsTurretGunner()` — a gunner runs the turret loop and never sends a
`SHIP_UPDATE`, matching native's parented branch which sends no ship update for
a boarded ship (`clintlib.cpp:1818-1905`). Leaving the turret resets the seed
and re-arms the cadence.

### 3.5 Camera + reticle (feedback) — `Game1.Rendering.cs` + `Rendering/HudRenderer.cs`

- **Turret view aims.** The `CockpitCamera` mode, while gunning, looks along the
  integrated `_turretOrientation` (instead of the own ship's stale broadcast
  attitude) from the gunner's position, so the player sees exactly where the
  turret points. Non-gunners keep the normal cockpit attitude.
- **Turret reticle.** `HudRenderer` gained a `turretView` flag (passed as
  `IsTurretGunner()`); in turret view the centre crosshair is replaced by a
  distinct larger double-ring turret reticle that turns the warning colour and
  shows a **TURRET** tag while firing — the explicit aim marker at screen
  centre (§3 feedback requirement).

---

## 4. Input mapping (same sources as flight)

`UpdateFlying` already folds every look source into a single `ControlData`:
keyboard arrows (yaw/pitch), the `M` mouse virtual-joystick (yaw/pitch from
cursor deflection), and the P380 joystick axes (deadzone-overriding
yaw/pitch/roll). `UpdateTurretGunner` reuses that exact `cmd.Controls` for the
turret axes — so a gunner aims with the identical controls they fly with —
with only the throttle forced to +1 (full slew). Fire is the same held-button
sense (Space / joystick fire folding into `stateM & WeaponsMask`).

---

## 5. Verification

### Build

```
$ /home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug
Build succeeded.  0 Warning(s), 0 Error(s)
```

### New tests (all in Allegiance.ClientCore.MonoGame.Tests)

- `TurretLookTests` (7) — exact numerical match vs `Ship.ExecuteTurretMove`
  over a controls/delta grid; zero-controls no-op; max-slew at full throttle;
  circular (unit-sphere) constraint.
- `ServerClockEstimatorTests` (4) — stays within the ±1.5 s gate under simulated
  latency jitter; no-samples/zero; non-monotonic rejection; stable-latency advance.
- `TurretGunnerCadenceTests` (5) — cadence gating, ACTIVE-vs-INACTIVE branch,
  clock-readiness rule, repeated hold, reset re-arm.
- `TurretGunnerFireLiveTests` (1 live, `[Collection("LiveNetwork")]`) — the P109/
  P140-style loopback: gunner boards a docked carrier over the wire, both launch
  (carrier undocks with the gunner aboard), the client-side send loop (the
  production `TurretGunnerCadence` + `TurretLook` + `ClientActiveTurretUpdate.From`
  primitives) emits `ACTIVE_TURRET_UPDATE`s while the fire button is held, and
  the server's gunned turret weapon creates projectiles.

  **Live proof output (green):**
  ```
  TURRET-FIRE-LIVE: 44 ACTIVE updates while firing; server created 674 projectiles,
  first flies fw=(-0.5646274, -0, -0.825346)  (gunner fw=(-0.56462735, -0, -0.82534593))
  ```
  The first projectile's unit velocity equals the gunner's live server orientation
  to 7 decimals (X = −0.5646274 vs −0.56462735), and |X| = 0.565 ≫ 0 proves it is
  the yawed gunner aim, not the carrier's −z.

  **Must-fail proof (recorded):** the identical test with the `SendActiveTurret`
  call disabled (simulating "no send loop") fails:
  ```
  no turret projectiles were created by the server despite ACTIVE updates — send loop / firing broken
  ```
  i.e. without the send loop the server creates **zero** turret projectiles —
  reproducing the pre-fix state. Reverted afterwards; test green again.

### Full suite

- `Allegiance.ClientCore.MonoGame.Tests` — **1007/1007 green, 3 consecutive runs**
  (the project that changed; includes the 17 new P464 tests).
- `Allegiance.Core.Tests` (3603 total) — the only failures across runs are the
  documented **rotating live-wire timing flakes** (`ConquestTerritoryLiveTests`,
  `GameOverPlayersWireTests`, `LiveMoneyTests`, `LiveRipcordTests`,
  `NpcShipFoundationLiveTests`, and the persistent
  `LiveTurretGunnerBindingTests` machine-state flake). Core.Tests does **not**
  reference the changed MonoGame assembly, so none of these can be caused by this
  task (no Core source was touched).
- `Allegiance.ClientCore.Tests` (333 total) — the 2 failures are the two
  documented pre-existing deterministic failures
  (`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`), unchanged by this
  task (ClientCore source untouched).

---

## 6. Decisions

- **Pure extraction over calling `Ship`.** `TurretLook` replicates
  `ExecuteTurretMove` rather than instantiating a heavyweight sim `Ship` to run
  one method; correctness is guaranteed by the numerical exact-match test against
  the real method.
- **Turret view = the cockpit camera while gunning.** The port's "turret view"
  is the gunner-specific external camera cycle; making the cockpit look along the
  integrated aim (from the gunner's position) gives the first-person aiming view
  the reticle anchors, while the existing `ExternalTurret` chase view is left
  intact.
- **Server clock from existing pose times.** No new wire; the estimator reuses
  the server `Time` slot already on the client's own pose updates, as the brief
  directed ("derive one from the server times already carried").
- **Aim seed.** The port transmits no turret resting facing, so the first frame's
  aim is seeded from the gunner's last-known orientation; the server is
  authoritative on the applied facing regardless.
- **Out of scope (unchanged):** in-flight seat switching / boarding-while-not-
  docked (P416 follow-ups), local gunner shot echo (P462 pilots only), own-ship
  prediction (P447). No server source was modified.

## Files changed

- `src/Allegiance.ClientCore.MonoGame/Flight/TurretLook.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Flight/ServerClockEstimator.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Flight/TurretGunnerCadence.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Game1.TurretGunner.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` *(gate the flight send)*
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` *(turret cockpit view + HUD flag)*
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` *(turret reticle)*
- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretLookTests.cs` *(new)*
- `tests/Allegiance.ClientCore.MonoGame.Tests/ServerClockEstimatorTests.cs` *(new)*
- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretGunnerCadenceTests.cs` *(new)*
- `tests/Allegiance.ClientCore.MonoGame.Tests/TurretGunnerFireLiveTests.cs` *(new)*

Committed on `master` (not pushed).
