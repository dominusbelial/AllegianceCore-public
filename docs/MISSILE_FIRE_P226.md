# P226 — Wire missile fire (TK_FireMissile) in the MonoGame client

Stage landing: P-series master, not pushed (Domingo pushes).

## ⚠️ Central correction to the task brief (read first)

The brief asserted the mechanism twice, and both assertions are FALSE against the
live native source:

1. "FIRE_MISSILE(70) is purely an outbound broadcast ... there is no
   client→server 'fire missile' request message at all — that's correct, not a
   gap, don't add one."
   **Disproven:** native declares `DEFINE_FEDMSG(CS, FIRE_MISSILE, 70)`
   (`src/Inc/Messages.h:547-557`) — the *CS* = **C→S and S→C**. `FedSrv` has a
   full inbound `case FM_CS_FIRE_MISSILE` (**`FedSrv.CPP:5413-5507`**) that the
   *client* sends: it gates on STAGE_STARTED + a mounted `ET_Magazine`@0 +
   `Amount>0`, ignores the client-supplied IDs ("Ignore the IDs provided by the
   client and generate one ourselves", `FedSrv.CPP:5444`), creates the real
   `Missile`, decrements ammo, and forwards the FIRE_MISSILE sector-wide. The
   port's `Wire/FireMissile.cs` codec already documents exactly this ("the
   client→server launch request, rebroadcast by the server ... a separate
   follow-up" — `FireMissile.cs:3-9,55-58`), i.e. this task.
2. "missile fire is a held control-state bit (missileFireIGC) ... the server's
   own per-tick magazine logic reads that bit every tick."
   The held-bit path *exists* in native (`magazineigc.cpp:111`), **but** the
   header annotates the high word "these are not transmitted as part of the
   state mask" (`igc.h:829`), and the wire `stateM` slot is a 16-bit
   `CompactState` (`ClientShipUpdate`, `igc.h:2457-2465`). `missileFireIGC =
   0x10000` (65536) cannot fit — it can never reach the server for a networked
   player. The held-bit auto-fire is the NPC/local-magazine path; a PLAYER's
   missile is launched via the C→S FIRE_MISSILE message above.

**Consequence for this port:** P226 wires the *faithful* player path — the
inbound `FM_CS_FIRE_MISSILE(70)` message (server-authoritative), with the
server-side lock built per-tick in `Ship.OnUpdate` (the brief's lock design,
kept) and the same lock gate driving the fire. The brief's "don't add an
inbound case" is documented as the correction, not followed. This matches the
port's own established precedent of verifying a brief's claims and porting the
real thing (cf. P181/P187/P191/P203 in the workflow skill).

## What was wired

- **`ShipControlState.MissileFire = 0x10000`** (`Sim/ShipControlState.cs`) —
  `missileFireIGC` (igc.h:830), the next power-of-two after `AllWeapons=0x8000`;
  cites the high-word / 16-bit-wire caveat. Used by the `OnUpdate` held/auto
  fire leg and retained as the semantic marker; a player's fire travels as the
  FIRE_MISSILE message instead.
- **`DataPartTypeIGC.MissileLauncher(short ExpendableTypeId)`** (`Data/DataPartTypeIGC.cs`)
  — a construction-only subtype on **`EquipmentType.Magazine`**, mirroring the
  `ChaffLauncher`/`MineLauncher`/`ProbeLauncher` recipe. Native's missile type
  reports `ET_Magazine` (`CmissileTypeIGC::GetEquipmentType`,
  `src/Igc/missiletypeigc.h:60-63`); the server resolves it at mount 0
  (`GetMountedPart(ET_Magazine,0)`, FedSrv.CPP:5425).
- **`Ship.UpdateMountedMissileLock` + `Ship.TryFireMissile`** (`Sim/Ship.cs`),
  and the **`OnUpdate` per-tick leg** (in the existing weapon-loop region,
  `Ship.cs` ~5046): every tick, for a mounted launcher with ammo, build/decay
  `MissileLock` toward `CommandTarget`; then, when `StateM & MissileFire` is
  held, attempt a launch. NPCs' weapon-only `UpdateWingmanCombat` is untouched —
  it never sets `MissileFire`, so this stays inert for them.
- **`Server.HandleFireMissile` + inbound `case FireMissile.Fmid`** (`Server.cs`)
  — the port of `FedSrv.CPP:5413-5507`: gates on Started + unboarded +
  mounted `ET_Magazine`@0 + `Amount>0`, then routes through the shared
  `Ship.TryFireMissile` (server-authoritative; ignores client IDs). Creating the
  `Missile` raises `MissileCreatedEvent` → `BroadcastFireMissile`, the outbound
  FIRE_MISSILE(70) sector broadcast (already wired since P90, `Server.cs:439`).
- **`ConnectionSession.SendFireMissile()`** (`ClientCore/Session/ConnectionSession.cs`)
  — the client's outbound `FM_CS_FIRE_MISSILE(70)`, mirror of
  `SendFireExpendable`; sends a minimal syntactically-valid request (server
  authoritative).
- **`Game1` Left-Ctrl binding** (`Game1.cs`) — **held/continuous** (like
  Shift-afterburner / Space-fire, not edge-triggered), re-sending FIRE_MISSILE
  every ~250 ms while held, gated behind the composing check. Left Ctrl is free
  (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/Enter/Back/Escape/Space/K/C/N/OemTilde/
  [ / ] all taken) and is the common flight-sim missile-fire convention — the
  brief's own suggestion.

## Lock-gating logic (with citation)

The fire gate lives in `Ship.TryFireMissile` and is intentionally NOT a new
rule — it reuses `UpdateMissileLock`'s own threshold (gate 5):

```
lockReady = missileType.MaxLock == 0f || MissileLock >= 1f;
```

- A `MaxLock == 0` missile type needs **no** lock: `UpdateMissileLock` gate 5
  already snaps it to full instantly (`magazineigc.cpp:145-146`, ported at
  `Ship.cs:2435`). Such types fire immediately — this is what the live proof uses.
- A `MaxLock > 0` type must reach full lock (`>= 1f`) — built by the per-tick
  `UpdateMountedMissileLock` leg (the port of the magazine's lock block,
  `magazineigc.cpp:134-176`, incl. the target-hull ECM denominator).

On a successful launch the lock is consumed (reset to 0 — native's
`SetLock(0.0f)` after fire, `FedSrv.CPP:5507`), so it must rebuild before the
next shot: the lock rebuild *is* the natural fire-rate limiter (the port has no
separate per-launcher load-time tracker). Ammo decrements via `SetPartAmount`
(the `FedSrv.CPP:5744-5745` decrement).

Scope boundaries honored: no missile-type selection UI, no HUD lock indicator
(document as a natural follow-up), no wingman-NPC missiles, no touch of the
native reference source.

## Live proof (real creation + real homing, not just creation)

Ran the real `Allegiance.Server` class over the real TCP transport with a real
`ConnectionSession` (the `MissileFireLiveTests` live-loopback, `LiveNetwork`
collection). Captured server console:

```
[firemissile] 1 ship 8096 launched -> FIRE_MISSILE(70) lock 0.000 ammo 3 -> 2
[missile] ship 8096 fired -> FIRE_MISSILE(70) type 8 pos (0,0,0) vel (50,0,0) fwd (1,0,0) life 5 -> 1 same-side peer(s)
MISSILEFIRE-CLIENT-LIVE: missile 5464 launched + homing toward target 8873 after 2 ticks, ammo 3 -> 2 (d0=247.4)
```

- A real `Missile` (id 5464) was created in the firing ship's sector, with
  launch velocity (50,0,0) = missile type 8's `InitialSpeed` 50, inheriting the
  launcher course (launcher aimed +X at the target).
- It **homed**: the test pumped subsequent ticks and asserted the missile's
  position narrowed toward target 8873 (starting separation 247.4 → strictly
  decreasing below d0−5), i.e. real per-tick homing (P179's turn/acceleration),
  not mere creation.
- Ammo decremented 3 → 2. `lock 0.000` is expected: type 8 is `MaxLock == 0`
  (no lock needed, gate 5), and the logged value is post-fire (consumed to 0).

`[missile]` is `BroadcastFireMissile` (Server.cs:9548); `[firemissile]` is
`HandleFireMissile`'s launch log.

## Build / test status

- Whole solution (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- `Allegiance.Core.Tests`: **3044** total (baseline 3040 + 4 new
  `MissileFireTests`). Ran 2×; both runs **3043 passed / 1 failed** — the only
  failure is the documented pre-existing `LiveTurretGunnerBindingTests`
  machine-state flake (fails in isolation; zero refs to P226 symbols).
- `Allegiance.ClientCore.Tests`: **55** (was 54, +1 `MissileFireLiveTests`),
  all green, ran 2×.
- `Allegiance.ClientCore.MonoGame.Tests`: **138**, all green (Game1 key wiring
  is the game loop, not unit-tested; no `FlightInput` change needed since the
  bit can't transit stateM).

New tests (all discriminating, verified must-fail-if-removed):
- `TryFireMissile_refuses_when_lock_not_yet_sufficient` — MaxLock>0, no lock →
  refused (no missile, ammo untouched). Reddens if the lock gate is removed.
- `TryFireMissile_fires_when_lock_has_built_and_homes_at_target` — full lock →
  missile homes at CommandTarget, ammo 2→1, lock reset.
- `OnUpdate_held_missile_fire_bit_fires_on_next_tick` — StateM&MissileFire +
  MaxLock==0 fires on the next tick. Reddens if the OnUpdate fire line is removed.
- `OnUpdate_held_bit_does_not_fire_without_built_lock` — MaxLock>0 held-bit but
  no lock → no fire.
- `SendFireMissile_LaunchesHomingMissileAndDecrementsAmmo_OverTheWire`
  (live loopback) — real wire round-trip + homing + ammo. Reddens if the
  inbound handler is disabled.

## Natural follow-ups (out of scope here)

- HUD lock-quality indicator (reuse `HudFrame`/`HudRenderer`).
- Standalone real-client (`Allegiance.ClientCore.MonoGame`) hold-Left-Ctrl run:
  the wire path is the `SendFireMissile` proven live above and the Game1 binding
  is code-reviewed; a full display/key run is a follow-up (Wayland has no input
  injection; the P-series temp auto-fire diagnostic would be needed).
- Missile-type selection UI / multi-launcher, salvo (multi-`MissileLaunchData`).
