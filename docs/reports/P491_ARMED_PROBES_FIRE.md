# P491 — armed probes (gun towers) actually fire

**Landing:** `master` (committed, not pushed). Repo:
`/home/domingo/Development/AllegianceCore`. Branch verified with
`git branch --show-current` → `master`.

## Problem

The shop sells armed probes and they did nothing. `Sim/Probe.cs` explicitly
deferred "autonomous weapon firing" (native `Igc/probeigc.cpp:352-528`), and
the wire codec carried `otTarget`/`oidTarget` without acting on them
(`Wire/ExportProbe.cs`). P479 already wired the side-GA scaling into the
probe-projectile seam (`Probe.ResolveProjectileType` /
`EffectiveProjectileSpeed` / `EffectiveProjectileLifespan`) and named this exact
follow-up ("Armed probes still don't fire"). P491 implements the targeting and
firing sequence.

## What native does (read exactly, not guessed)

`CprobeIGC::Update`'s weapon branch (`probeigc.cpp:352-529`), gated on
`m_projectileType != NULL` (`:352`) and the fire-rate gate `m_nextFire < now`
(`:354`):

1. **Pre-shot bookkeeping** (`:359-379`): `lifespan = GetProjectileLifespan()`;
   `speed = projectileType->GetSpeed()`, scaled by `c_gaSpeedAmmo` when the
   probe is ammo-based (`:366-368`); `accuracy`, `dtimeBurst`, `dispersion` from
   the probe type; clamp `m_nextFire` up to `lastUpdate`; `dtUpdate =
   m_nextFire - lastUpdate`.
2. **Target selection** (`:381-437`): an initial engagement radius
   `distance2Min = (speed · lifespan / 1.05)²`; then, branched on the probe's
   capabilities:
   - `c_eabmShootOnlyTarget` (`0x1000`) → `ValidTarget(m_target, …)` when
     `m_target` is in the same cluster (`:390-399`);
   - else `c_eabmShootStations` (`0x20`) → `GetTarget(stations)`; if the round
     is AoE (`blastRadius > 1`) and `c_eabmShootMissiles` (`0x80`) → missiles;
     then `c_eabmShootShips` (`0x40`) → ships; then (non-AoE only) missiles
     again (`:402-436`).
   `ValidTarget`/`GetTarget` (`probeigc.cpp:255-330`) reject: non-hostile sides
   (`:268`), undamagable/out-of-scan models (`:272`), lifepod ships
   (`:274`), anything beyond the squared-distance bound (`:283`), and anything
   whose lead intercept `t = solveForImpact(...)` exceeds the round's lifespan
   (`:285-296`).
3. **Fire the burst** (`:467-511`): orient to the lead direction
   (`o.TurnTo(directionMin)`), spawn point `position = myPosition +
   emissionPt · o` (`:471`), and per round perturb `forward` by a random
   dispersion in the orientation's right/up plane, normalise, set
   `velocity = speed · forward`, `lifespan`, and `CreateObject(OT_projectile,…)`
   (`:496`) with the launcher set to `m_launcher ? m_launcher : this` (`:500`),
   repeating while `m_nextFire < now` (`:509-511`).
4. **Ammo** (`:513-521`): `if (m_ammo > 0) { m_ammo -= nShots; if (m_ammo <= 0)
   { m_ammo = 0; KillProbeEvent(this); } }`.
5. Else no shot: `m_nextFire = now` (`:526`).

The initial `m_nextFire = m_time0 + (c_eabmQuickReady ? 5 : 30)` is seeded in
`Initialize` (`probeigc.cpp:137-139`).

## The change

### `Sim/Probe.cs` — the armed-probe firing loop (`FireIfArmed`)

Added `c_eabm*` capability bit constants (`igc.h:770-781`), armed-probe state
(`_nextFire`, `_ammo`, `_target`, an RNG), and the full
`FireIfArmed(double now)` port called from `OnUpdate`'s else branch (native
only fires when not expired). It follows native verbatim:

- **Seed** in the constructor (`probeigc.cpp:93,137-139`): `_ammo =
  ProbeType.Ammo`; `_nextFire = now + (c_eabmQuickReady ? 5 : 30)`.
- **Gate** (`probeigc.cpp:352-354`): `!IsArmed` or `_nextFire >= now` → return.
  `IsArmed` = `ResolveProjectileType() is not null` (`m_projectileType != NULL`).
- **Speed/lifespan/accuracy/burst/dispersion** via the P479 seam:
  `EffectiveProjectileSpeed` / `EffectiveProjectileLifespan`, plus
  `ProbeType.Accuracy/DtimeBurst/Dispersion`.
- **Engagement radius** `distance2Min = (speed · lifespan / 1.05)²`
  (`probeigc.cpp:383-384`).
- **Target scan** — a local `ConsiderModel` (ships/missiles/probes) and a
  `ConsiderStation` (stations are a separate `IStationIgc` type in this port,
  not a `Model`), replicating the capability-branch order exactly
  (`probeigc.cpp:390-437`). `ConsiderModel` applies the native conjuncts:
  hostile side (different side — this port has no alliance system, the standing
  simplification), no lifepods (`c_habmLifepod`), squared-distance bound, and
  `ShipDodgeLaw.SolveForImpact` lead with `t <= lifespan`. A station target is
  evaluated the same way (station never moves).
- **Fire** (`probeigc.cpp:467-511`): orient `Orientation.Set(directionMin)`,
  spawn at `myPosition` (the port has no hull-frame `wepemt` emission point —
  native derives it from the probe model's frame, `probetypeigc.cpp:43-47` —
  so the offset is zero, the documented historic-centre stand-in the weapon
  path already uses), perturb `forward` by dispersion, build
  `velocity = speed · forward`, and create a real `Projectile` through the same
  path ships use (`Weapon.cs:334-351`): `new Projectile(…)` + `sector.Add` +
  `mission.Site.ProjectileCreatedEvent`. The launcher is the probe's deployer
  ship (`Probe.Launcher`), so the ship-based broadcast path recognises the round
  (see the OriginProbe note below).
- **Ammo** (`probeigc.cpp:513-521`): decrement by the burst size; on exhaustion
  set 0, remove from the sector and raise `ProbeDestroyedEvent` (native
  `KillProbeEvent`). Negative ammo (an inexhaustible energy pool) never
  decrements — native only does so for `m_ammo > 0`.

**Documented divergences (honest):**

- **Cross-side `m_bSeenByAll` reveal on first shot** (`probeigc.cpp:444-465`)
  is not ported — probe visibility rules are explicitly out of scope (P491
  brief; P454 likewise).
- **No armed-probe friendly-fire immunity** (native `probeigc.h:74-89`, where an
  *armed* probe returns `c_drNoDamage` to a same-side hit). P491 deliberately
  keeps the P100 no-gate for every probe (armed or not): P100's entrenched test
  contract asserts a probe takes same-side damage, reversing it would break that
  contract for no gain to the firing deliverable. The armed probe's own rounds
  are kept off itself by `Projectile.OriginProbe` (below), not by a damage gate.
- The **emission-point offset** and the **projectile's own-side GA multiplier**
  are zero/identity (the port has no hull frames for `wepemt`, and the round's
  launcher is a ship so `Sector.ResolveCombatCollisions`'s GA factor resolves
  from the deployer's side as normal).

### `Sim/Projectile.cs` — `OriginProbe`

Native launches a probe's round with the probe itself as the launcher
(`probeigc.cpp:500`), which the collision pass excludes via the self-guard
(`if (ReferenceEquals(ord.Launcher, ship/probe)) continue`). This port keeps the
deployer ship as the round's `Launcher` so the ship-based `PROJECTILE_INSTANCE`
broadcast (same-side + P375 cross-side legs) works unchanged — so a round that
emerges from the armed probe's own body would otherwise collide with the probe
that fired it. `Projectile.OriginProbe` tags the originating probe; the probe
branch of `Sector.ResolveCombatCollisions` passes that round through its own
body to the real target (`if (ReferenceEquals((ord as Projectile)?.OriginProbe,
probe)) continue`), reproducing native's self-exclusion faithfully without
touching the broadcast path. Only the armed probe's *own* rounds carry the tag,
so no ship-fired round is affected (the P100 point-blank probe tests stay green).

### `Wire/ExportProbe.cs` — `otTarget`/`oidTarget` from the probe's target

`ExportProbe.FromProbe` now writes the probe's persisted `Target`'s object
type/id into `otTarget`/`oidTarget` — the exact native
`if (m_target) { otTarget = m_target->GetObjectType(); oidTarget =
m_target->GetObjectID(); } else { NA; NA; }` (`probeigc.cpp:553-562`). A
free-targeting sentry (and every `DeployProbe`-created probe) has no persisted
target, so it stays NA/-1 — native behaves identically (`m_target` is only
persisted for `c_eabmShootOnlyTarget` probes). A target is mapped to its native
`ObjectType` short (`Ship=0`, `Missile=2`, `Probe=4`); `IgcObjectType` omits
`OT_missile`, so it is returned as the literal native value 2.

## Real armed probes in PCore001 (measured, not guessed)

Scanned all 53 probe types of `PCore001.igc`. The armed set (a real
`ProjectileTypeId`) splits by capability:

- **Free-targeting sentries** (`c_eabmShootShips`, no `ShootOnlyTarget`):
  'Plas Gen 1/2/3' (ids 30/38/47), 'Hvy Plas Gen' (48/49), 'Laser Blossom' (97/98)
  — all `c_eabmQuickReady` (fire 5 s after deploy);
  'Tower AC/SC Drone' (85+) — **not** quick-ready (30 s delay).
- **`c_eabmShootOnlyTarget` drones** (Combat Drone / Station Drone families,
  ids 800+, 1000+): require an externally-pinned target (native sets it from the
  wire export); P491 supports them via `Probe.Target` but they remain dormant
  when no target is commanded (faithful).
- **Teleport Probes 1/2** (ids 6/600): armed with a fast, weak anti-missile
  round.

The live test uses **'Plas Gen 1' (id 30)** — the cleanest discriminator:
`c_eabmQuickReady` (fires at 5 s, not 30), `c_eabmShootShips`, ammo 5,
dispersion 0, accuracy 1, projectile speed 1000 / life 0.218 (reach ~218 m).
It is ammo-based, so the ammo decrement is observable.

## Fail-first evidence

Each fix has a test that fails on unfixed code (verified by temporarily
reverting the wiring and watching the test go red):

- Disabling the `FireIfArmed` call in `OnUpdate` → all three firing SIM tests
  and the live test fail (ammo stuck at 5, no projectile, no enemy damage);
  the `ignores-same-side` and `unarmed-never-fires` tests correctly still pass
  (they assert the *absence* of firing).
- Neutralising `FromProbe`'s `probe.Target` read (forcing NA/-1) →
  `ExportProbe_carries_the_persisted_target_on_otTarget_oidTarget` fails.

## Verification (numbers actually measured)

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`):
  **0 warnings, 0 errors**.
- New SIM tests `ArmedProbeFiringTests` (6) + live test `ArmedProbeLiveTests`
  (1): all green.
- **Live wire proof** (measured): deployed the real 'Plas Gen 1' via the P435
  `Ship.DeployProbe` dispenser seam; flew a cross-side enemy into range;
  the probe acquired it and fired:
  ```
  P491-LIVE: armed probe 'Plas Gen 1' ammo 5→4; enemy 1→0.6000; PROJ_INST(12)
             type 16 vel (0,0,1000) on deployer peer after 103 pumps
  ```
  → real round spawned and broadcast as `FM_CS_PROJECTILE_INSTANCE(12)` on the
  deployer's peer (launcher = deployer ship, so the same-side and P375
  cross-side legs ride the existing ship path), the enemy ship took real damage
  (1.0 → 0.6), and the probe's ammo decremented (5 → 4).
- **Full suite via `scripts/run-tests.sh` (P471 runner), 3 consecutive runs**
  (all four assemblies: Lobby 12, ClientCore.MonoGame 1079, ClientCore 401,
  Core 3694 — the full totals). Additionally the `Allegiance.Core.Tests`
  assembly was run 3 consecutive times standalone (3694 each). Across all runs
  the only failures were unrelated, pre-existing environmental live-network
  flakes, every one confirmed green in isolation and none referencing P491
  symbols (see `docs/TEST_FLAKES.md`, whose documented entries cover
  `GameOverPlayersWireTests…Conquest_win_in_one_mission` (line 30),
  `LiveRipcordTests…` (line 36), `CargoBayReloadTests…` (lines 53-54), and
  `FullSessionKillIntegration…` (line 122)):
  - Standalone Core runs 1/2/3: 2/2/1 flakes —
    `LiveRipcordTests.First_activation…`, `CargoBayReloadTests.SwapPart_…` /
    `Reload_fuel_pack…`, `P366LeaderBotCommandLiveTests…`,
    `WarpBombTests.Live_whole_mission…`, `GameOverPlayersWireTests…`.
  - `scripts/run-tests.sh` run 1: Core 4 flakes (`GameOverPlayersWireTests…`,
    `LiveRipcordTests.Re_target…`, `LiveStationRescueTests…`,
    `NpcShipFoundationLiveTests…`) + ClientCore 1
    (`ReadyCheckLiveTests…`, the P188 ready-up race).
  - run 2: Core 1 (`NpcShipFoundationLiveTests…`) + ClientCore 1
    (`FullSessionKillIntegration…`).
  - run 3: Core 2 (`LiveTreasurePickupTests…`,
    `ConquestTerritoryLiveTests.Conquest_win_in_one_mission…`) + ClientCore 1
    (`FullSessionKillIntegration…`).
  Each flapping test passes when re-run in isolation; no deterministic
  regression.

## Out of scope (unchanged)

- Teleport probes (P454) and probe visibility rules (incl. the native
  `m_bSeenByAll` cross-side reveal and `FM_S_PROBES_UPDATE(92)` scanning).
- New probe types; `c_eabmShootOnlyTarget` drones remain dormant until a target
  is commanded (native requires the wire `otTarget`/`oidTarget` import).
- Native's armed-probe same-side friendly-fire immunity (deliberately not
  ported; see the divergence note above).
