# P375 — extend same-side-only combat/state broadcasts to cross-side now that cross-side EXPORT exists

**Summary.** Two live-confirmed defects rooted in the same historic decision —
every multi-peer *visibility* broadcast (projectile instance, projectile hull
hit, missile fire, missile update, and the ship-identity `SectorId`) was
deliberately **same-side only**, explicitly deferred behind "Phase 5's cross-side
EXPORT" in the doc comments. That prerequisite (P346/347/349 cross-side EXPORT,
fixed by P369's identity-vs-telemetry gate) now exists and is proven correct, so
P375 is the follow-through: it extends each deferred broadcast to the cross-side
audience that already renders the shooter/ship, and fixes the separate `SectorId`
gap that made a cross-side enemy in front of you unselectable with `T`.

Two fixes, both landed on `master`:

1. **Cross-side `ShipEntity.SectorId`** — the P371 target-pick/radar regression
   (an enemy directly in front of you failed `ship.SectorId == mySector`).
2. **Cross-side combat visibility** — projectiles (instance + hull-hit), missile
   fire/update/destroyed, now reach cross-side peers who can see the shooter.

---

## 1. The cross-side `SectorId` fix — derive from the viewer's own sector (no wire change)

### Root cause (why it was permanently 0)
`ShipEntity.SectorId` had exactly TWO setters, both same-side/own-only:

- `SET_CLUSTER(7)` — **own ship only** (`ClientWorld.Apply(SetCluster)`).
- `SHIP_STATUS(90)` — **same-side teammate only** (P58-era scope).

The cross-side identity `EXPORT(26)` — the *only* source of a cross-side ship's
identity (P369) — carries **no sector field** (native `DataShipIGC` has none), and
it is one-shot. So a cross-side ship's `SectorId` was permanently the C# default
`0`. Nothing depended on it until P371's new sector-scoped target-pick filter
(`GatherSectorCandidates`: `ship.SectorId == mySector`) began reading it — the
exact same bug *shape* P369 fixed for `SideId`, just a different field, never
caught because no consumer read a cross-side ship's sector until P371. An enemy
directly in front of you could never be `T`-selected unless you happened to be in
sector 0. (The radar-contact filter `DrawRadar` at `Game1.cs` reads the same field.)

### Mechanism chosen
The cleanest, most faithful fix is **not** to add a field to the EXPORT wire
(which would deviate from native `DataShipIGC`, is one-shot, and could go stale
across a warp). Instead: derive it from the **recipient's own sector** inside
`ClientWorld.Apply(HeavyShipsUpdate)`:

- `HEAVY_SHIPS_UPDATE(176)` is sent by the server for exactly the ships in the
  **recipient's own cluster** (`BroadcastShipsUpdate` gathers candidates from the
  peer's own sector). So every 176 entry — same-side OR cross-side — is *by
  construction* in the sector the local ship is in.
- Native's client runs a local IGC sim and instantiates every 176 object in its
  own cluster; there is no per-ship sector on the wire. So
  `entry.SectorId = ownShip.SectorId` is the native-faithful rule.
- It **stays current**: while a cross-side ship is in our sector, 176 re-flows
  every tick and re-stamps; when it warps out, P370's `IsGone` hides it; when it
  returns, 176 re-stamps the correct value. Never a stale one-time value.

Verified against every consumer (warp-draw loop, P371 target-pick filter, P370
hide/show, radar, minimap): all read either the *own* ship's sector (unchanged)
or `ship.SectorId == mySector` for remote ships (now correct for cross-side).

## 2. Cross-side projectile / missile visibility — reuse P369's audience gate

### The audience helper (`Server.Broadcast.cs`): `CrossSideViewers(subject, subjectSector)`
A single private helper now computes the cross-side audience for a combat effect
produced by `subject` in `subjectSector`, yielding `(connection, seeing side)`
pairs. It applies the **exact P369 cross-side gate** that
`SweepCrossSideExports` uses for identity and that `BroadcastShipsUpdate` uses
for telemetry (`peerSees`), **reused rather than re-derived**:

> a seeing side qualifies iff (a) it has a **seated peer in the subject's
> sector** (the condition under which that side's 176 telemetry flows for the
> subject), and (b) the subject is **not a cloak-hidden enemy** of that side
> (`!subject.IsCloaked || subject.SeenBySide(sideId)`, P270). Grouped per side
> like the sweep: if any seated peer of a side sits in the sector, that whole
> side's connections qualify (shared scanner net). The subject's own side is
> excluded (it is the existing same-side audience).

Sharing one helper between the identity sweep and every ordnance broadcast
guarantees they can never disagree about who sees whom.

### Broadcasts extended (each keeps its existing same-side latch/audience unchanged, ADDING the cross-side leg alongside)
- `BroadcastProjectileInstance(12)` — cross-side peers now get the bolt.
- `BroadcastProjectileHullHit(213)` — cross-side peers now get the impact spark.
- `BroadcastFireMissile(70)` — cross-side peers now get the launch.
- `BroadcastMissileDestroyed(83)` — cross-side peers now get the expiry (and its
  P263 blast, since the same `targets` list feeds `SendExplosionEvent`).
- `BroadcastMissilesUpdate(205)` — the per-tick tracking stream: same-side rule
  unchanged; a cross-side side now receives an enemy missile iff it has a seated
  peer in the missile's sector and the launcher is not cloak-hidden from it.

The 205 + 83 extensions were necessary for the stated verification ("see the
opponent's missile in real time, not just its damage"): without them a cross-side
viewer would see the launch spawn but never track or clear it.

## 3. Client render is already side-agnostic (verified, no change)
Read `Game1.cs` projectile/missile rendering:
- **Projectiles**: `ProjectileEntity` carries no sector/id; the draw loop is a
  **distance-based** visibility test against the local ship (no same-side
  assumption) — a cross-side `PROJECTILE_INSTANCE` renders as soon as it lands.
- **Missiles**: the draw loop sector-filters by `ClusterId == own sector`, which
  holds for any in-sector enemy missile once its `FIRE_MISSILE/205` carry the
  real `ClusterId`. No same-side-only assumption.

So no client render change was required; the server-side audience extension is
sufficient. (Confirmed by the live tests below — the cross-side `ClientWorld`
receives the projectile and tracks the missile.)

## 4. The "fully invisible cross-side ship" observation — investigated, not a current-code defect
The task posed a third live symptom (an opponent "genuinely never rendered — no
model, nothing" despite close-range face-to-face collision working). This was
investigated as its own question, **not** assumed to be explained by the
`SectorId` fix:

- The main ship-render pose gather (`Game1.cs:1958`) has **no sector filter** — it
  draws every entity with a `Latest` pose that is not `IsNotVisible`. A cross-side
  ship in the viewer's sector receives 176 telemetry (set `Latest`) and identity
  (P369 EXPORT), so under current master it *does* render. The P369 live harness
  already demonstrated every cross-side ship rendering with its correct model on
  every viewer.
- There is **no permanent-invisibility latch** in current code for an in-sector,
  non-cloaked, identified cross-side ship: `IsGone` is set only by a `SHIP_DELETE`
  (dock/warp-out/logoff/death) and is cleared by the next `176/EXPORT` when the
  ship is genuinely back in view; a transient `IsGone` self-clears.
- Conclusion: the observation does **not** reproduce on master post-P369/P370/P371
  — it predates them (the pre-P369 identity gate mismatch produced the *wrong
  shape*/fallback, and pre-P370 produced the *frozen ghost*; neither is "nothing"),
  or reflects a peer whose side genuinely had no seated member in the ship's
  sector (in which case withholding is correct fog-of-war). The new live test
  re-asserts the invariant directly (a cross-side ship in the watcher's sector has
  `Latest != null`, `IsNotVisible == false`).

## Verification — real proof (live multi-client, real TCP + real ClientWorld)

New tests (real in-process `Allegiance.Server`, real `ConnectionSession` clients
driving real `ClientWorld`s — the same data plane the MonoGame client uses):

- `tests/Allegiance.ClientCore.Tests/CrossSideSectorIdTests.cs` (3, deterministic):
  the derivation — a cross-side ship in a 176 gets the viewer's own sector; the
  value re-stamps when the viewer's sector changes (stays current, not one-time);
  same-side consistency with SHIP_STATUS.
- `tests/Allegiance.ClientCore.Tests/CrossSideCombatVisibilityLiveTests.cs` (3, live):
  1. **Bullet 1 + 2** — cross-side watcher learns the enemy with
     `SectorId == own sector`, `Latest != null`, `IsNotVisible == false` (exactly
     the P371 `T`-gather conditions), **sees the opponent's projectile fire**
     (`PROJECTILE_INSTANCE`), and **sees + tracks the opponent's missile** in
     real time (`FIRE_MISSILE` in its own sector, then the `205` stream advances
     its position).
  2. **Bullet 3 (no regression)** — a same-side pair still sees each other's
     projectile and missile exactly as before P375.
  3. **Bullet 4** — a cross-side ship warps out (watcher latches `IsGone` via
     P370), warps back; the watcher clears `IsGone` and the re-flowing 176
     re-stamps `SectorId` to the correct current sector.
- Updated `tests/Allegiance.Core.Tests/ProjectileBroadcastTests.cs`: the P88
  "cross-side enemy receives none" contract is now obsolete (P375 deliberately
  reverses it). Co-locating all three bots (controlled spawn) and scoping the
  assertions to the shooter's armed weapon type (a pre-existing ambient/AI
  projectile contaminant made the old unscoped count flaky on the pristine tree
  too), it now asserts the cross-side enemy **does** receive the shooter's shots
  (correct type, per-shot multiplicity) while the same-side contract is unchanged.

### Results
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full `dotnet test` suite run **2×** (`Passed 3348 → 3350`): the only failures are
  the **pre-existing documented live-wire timing flakes that vary run-to-run** —
  run 1: `ShipUpdateBudgetLiveTests`, `LiveWinConditionTests.Board_refused_once_mission_over`,
  `LiveTurretGunnerBindingTests.Board_binds_gunner_...` (plus the pre-existing
  `ProjectileBroadcastTests`, since fixed); run 2: `LiveStationRescueTests.Lifepod_...`
  and one other. None reference P375 symbols; the flakes appear/disappear run-to-run
  exactly as documented for prior stages. `ProjectileBroadcastTests` (a documented
  pristine-tree failure) now passes 5/5 after the P375 update.
- New suites green: `CrossSideSectorIdTests` (3) + `CrossSideCombatVisibilityLiveTests` (3).

## Files changed
- `src/Allegiance.Server/Server.Broadcast.cs` — `CrossSideViewers` (the shared
  P369 cross-side audience helper); `BroadcastMissilesUpdate(205)` extended with
  the cross-side leg.
- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastProjectileInstance`,
  `BroadcastProjectileHullHit`, `BroadcastFireMissile`, `BroadcastMissileDestroyed`
  each gain the cross-side leg via `CrossSideViewers`.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(HeavyShipsUpdate)`
  derives each entry's `SectorId` from the local ship's own sector (fix 1).
- `tests/Allegiance.ClientCore.Tests/CrossSideSectorIdTests.cs` — new (3).
- `tests/Allegiance.ClientCore.Tests/CrossSideCombatVisibilityLiveTests.cs` — new (3).
- `tests/Allegiance.Core.Tests/ProjectileBroadcastTests.cs` — P88 updated for the
  P375 cross-side contract (+ controlled same-sector spawn + robust scoping).

Out of scope, untouched: P372/P373/P374, the cross-side EXPORT/identity system
itself (reused as-is), and any alliance/diplomacy concept.
