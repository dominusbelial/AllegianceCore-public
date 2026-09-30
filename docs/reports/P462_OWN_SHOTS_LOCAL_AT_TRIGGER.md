# P462 — own shots appear (and sound) at the trigger frame, not a round trip later

Landing date: 2026-09-30 · Landed on `master` (not pushed).

## Summary

Previously the MonoGame client was a pure renderer with no sim: it sent only
controls (`ConnectionSession.CommandControls`), and a projectile entity was
created **only** when the server's `PROJECTILE_INSTANCE(12)` echo came back
(`ClientWorld.Apply(ProjectileInstance)`). The player's own shot (and its fire
sound) therefore appeared a full round trip plus up to one server tick after the
trigger. Native Allegiance fires on the client (`CshipIGC::Update` →
`weapon->Update`, `src/Igc/shipIGC.cpp:312-340`; the weapon creates its
projectiles locally in `CweaponIGC::FireWeapon`, `src/Igc/weaponIGC.cpp:142-322`),
so the own shot appears and sounds on the very frame the trigger is pulled.
`FM_CS_PROJECTILE_INSTANCE(12)` is declared (`Inc/Messages.h:131`) but never sent
client→server; the port just never ran its local weapon sim.

P462 restores the native *feel* client-side without moving any authority: a pure,
unit-tested `OwnShotEcho` emits a visual tracer (and the fire one-shot) at the
trigger frame, rendered through the existing projectile draw path; the server's
echo of our own shots is de-duplicated so each shot renders and sounds exactly
once; damage, energy, ammo and every hit stay server-authoritative.

## What changed (with citations)

| File | Change |
|---|---|
| `src/Allegiance.ClientCore.MonoGame/Flight/OwnShotEcho.cs` (**new**) | Pure, MonoGame-free own-shot echo: cadence mirroring `Weapon.FireWeapon` (`Weapon.cs:200-378`), per-round physics (`weaponIGC.cpp:255-269`), energy/ammo gate, and the `PROJECTILE_INSTANCE` de-dup predicate. |
| `src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs` | Exposes the core's `ProjectileTypes` table (added a nullable record field), so a mounted weapon's real projectile `Speed`/`Lifespan`/`AbsoluteF` are reachable client-side. |
| `src/Allegiance.ClientCore/State/ClientWorld.cs` | `OwnEchoDropPredicate` hook consulted in `Apply(ProjectileInstance)` before creating an entity. |
| `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` | Emits the echo in the same gate-locked pose pass that builds `_poses[me]`, playing one `PlayWeaponFire()` per emitted round; captures the actually-sent fire bits. |
| `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` | Draws the echo's live local rounds through the **same** bolt path (cull, sizing, orientation, tint) as server echoes. |
| `tests/.../OwnShotEchoTests.cs` (**new**) | 11 tests (cadence, first-shot-immediate, energy/ammo gate, dispersion bounds, inherited velocity, 4 de-dup cases). |
| `tests/.../ClientWorldProjectileTests.cs` | +2: the de-dup glue (own dropped / foreign kept) and the pre-fix characterisation. |

## Before / after latency evidence (real server)

A live harness (a real in-process `Allegiance.Server` + the MonoGame client's real
`ConnectionSession` + the real `OwnShotEcho` + the real de-dup predicate, against
real `PROJECTILE_INSTANCE` broadcasts) measured:

```
weapon part -> projectile type 88: speed=400 lifespan=1 abs=False dtime=0.25 eps=30 dispersion=0.01
client pose eFrac=1.000 aFrac=0.125
T_trigger=650644130 ms: local echo emitted 1 round(s), fire sound calls (one per shot) = 1   <- SAME FRAME as the trigger
server fired 1 real projectile(s) after 6 tick(s) (T_server_fire=650644139 ms, delta from trigger = 9 ms)
pilot world projectile entities for our type = ABSENT (dedup OK - own echo dropped); total = 0

[p462-prefix] T_trigger=650644286 ms: at the trigger frame the world has 0 new round(s)
[p462-prefix] the round appeared ONLY via the server echo at T=650644287 ms
```

- **After (fix):** the local tracer is emitted and `PlayWeaponFire()` is invoked
  synchronously at the trigger frame (`T_trigger`). The server's real round (what
  the pre-fix client waited for) lands a round trip later (~9 ms same-host;
  larger on a real network).
- **Before (pre-fix):** at the trigger frame the client world has **0 rounds** —
  the client itself manufactures nothing; the tracer appears only when the server
  echo arrives (the round-trip latency the fix eliminates).
- **Exactly one sound per shot:** the echo emits 1 round → 1 fire sound; the
  server echo is de-duplicated, so it neither re-renders nor re-sounds (world
  projectile count stays 0 for our type).

The committed `PreFix_NoOwnRoundExistsBeforeServerEcho` test captures the pre-fix
behaviour at the `ClientWorld` level: an empty world stays empty until
`Apply(ProjectileInstance)`.

*Scope of the live proof:* it drives the real server, real wire, real
`OwnShotEcho`, real core data and the real `ClientWorld` de-dup, and invokes the
same `PlayWeaponFire` call the trigger path makes. It is headless — no GPU/audio
device — so the claim is that the emit + sound call happen at the trigger frame
(measured), not that a speaker audibly fired.

## Echo-window derivation

`EchoWindowMs = 400` (`OwnShotEcho.cs`). Derivation: the server fires within
≤1 sim tick (~50 ms at 20 ups) of receiving our controls, and the echo returns
over one network round trip (negligible same-host, <150 ms realistic internet).
400 ms covers tick + RTT with generous margin while staying far shorter than any
round's visual lifespan, so a foreign round that merely shares a projectile type
near our position in a later tick is never mis-de-duped.

## De-dup rule and failure modes

`ShouldDropServerEcho(type, spawnPos, serverPos, now)` drops an incoming
`PROJECTILE_INSTANCE` when **all** hold:

1. its projectile type is one we recently emitted,
2. it spawned within `FireOriginRadius` (150 u — the same local-vs-other radius
   `ProjectileAmbientSoundCurve` already uses) of our server-reported position,
3. it arrived within `EchoWindowMs` of that local round.

Otherwise it is kept (renders + sounds normally). Failure modes:

- **We gated on energy/ammo but the server had enough:** the server fires, our
  echo didn't → no matching local round → the server echo is kept (renders +
  sounds). Correct: the server is authoritative and the shot is real.
- **The echo arrives outside the window** (very high latency): not dropped, so it
  renders. A double visual (local + echo) can occur in that extreme case; the
  local round is pruned shortly after (max(lifespan, window)).
- **Dispersion divergence:** the local round's exact spawn/velocity differ from
  the server's (different random seed). The de-dup matches on **type + radius +
  window**, not exact position, so this does not prevent the drop.

## Sound — exactly one per shot

The trigger path plays one `PlayWeaponFire()` (`SoundManager.cs:386`) per emitted
local round. Because our own server echo is dropped, the echo-driven local-fire
sound path (`ProjectileSoundPlanner.IsLocalFire`, which fires when a bolt spawns
within 150 u) never sees our own shots → exactly one sound per shot. A foreign
shot still plays through that echo path normally.

## P447 compatibility seam

The echo reads its pose (`OwnShotContext.Position/Orientation`) from the caller,
who supplies them from the **single render pose the renderer draws the own hull
from** — Game1's `_poses[me]` (`Flight.PredictedPose`, set in the same gate-locked
flight pass). When P447 adds own-ship prediction, that render pose becomes the
predicted one and the echo automatically follows it. No prediction is implemented
here. Velocity comes from `ServerShipUpdate.Velocity` (the same update object the
render pose is built from).

## Out of scope (unchanged)

- No server change, wire change, or damage/hit-authority change. Hits stay
  server-side; local rounds never deal damage and never consume energy/ammo
  client-side (single gate only).
- Own-ship movement prediction (P447), per-weapon sounds / per-type visuals
  (P465/P465b), turret-gunner shots (P464), missiles (untouched — own lock/fire
  path).

## Build & test

- `/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors**.
- New tests green in isolation and in the full suite:
  - `OwnShotEchoTests` — **11/11** (cadence, first-shot immediate, energy/ammo
    gate, dispersion bounds, inherited velocity, 4 de-dup cases).
  - `ClientWorldProjectileTests` — **9/9** (incl. the +2 P462 glue/pre-fix tests).
- Must-fail: temporarily disabling the de-dup predicate consultation in
  `ClientWorld.Apply(ProjectileInstance)` turns
  `OwnEchoPredicate_DropsMatchingEcho_KeepsForeign` red (the own echo was no
  longer dropped) — proven, then reverted.
- Full suite: **MonoGame.Tests 938/938**, **Lobby.Tests 12/12**, and the
  `ClientWorldProjectileTests` class all green. The Core/ClientCore live-network
  failures observed in this run are **environmental machine-state flakes, not
  regressions**: every failing test file has zero references to P462 symbols, the
  two ClientCore failures reproduce on a **pristine** tree (`git stash -u`), and
  the Core ones (`AddPartTests`, `NpcShipFoundationLiveTests`,
  `LiveStationRescueTests`, …) pass **green in isolation**. The machine was under
  heavy concurrent load (the `run-chain` orchestrator + a live `Allegiance.Server`
  were running in parallel), which the repo's test methodology already documents
  causes live/socket flakiness. Named environmental failures from the observed
  runs: `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
  `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `ConquestTerritoryLiveTests.*` (3), `GameOverPlayersWireTests.*` (2),
  `AddPartTests.Second_part_touch_while_offer_pending…`,
  `LiveStationRescueTests.Lifepod_reaching_rescue_station…`,
  `NpcShipFoundationLiveTests.Ownerless_npc_ship…`,
  `LiveTurretGunnerBindingTests.Board_binds_gunner…` (pre-existing documented
  flake).
