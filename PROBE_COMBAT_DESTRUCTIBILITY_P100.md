# PROBE COMBAT DESTRUCTIBILITY — P100

## What this task did

Made a deployed probe a valid **direct-hit target** of real ordnance in the
existing swept collision pass. A probe carries `c_mtDamagable | c_mtHitable`
(`src/Igc/probeigc.cpp:115`), and — unlike a mine, which the native explicitly
excludes from direct fire (`projectileIGC.cpp:166-169` ignores only `OT_mine`;
`missileigc.cpp:446-447` likewise) — **nothing special-cases `OT_probe` out of
the generic damage dispatch**. So the collision/impact mechanics themselves
needed no new code; a probe just became a second valid target type in
`Sector.ResolveCombatCollisions`, dispatching to its own
`Probe.ReceiveDamage`. This is exactly the follow-up P98/P99 both named.

Two files changed in `src/`; two new test files added. The ship combat path is
behaviorally byte-for-byte identical (see "Ship path untouched" below).

## Changes

### 1. `Probe.ReceiveDamage` + `Probe.Dead` — `src/Allegiance.Core/Sim/Probe.cs`

Added `public DamageResult ReceiveDamage(byte type, float amount, double now,
Vector3 position1, Vector3 position2, Model? launcher)` — the port of
`CprobeIGC::ReceiveDamage` (`src/Igc/probeigc.h:65-146`), and
`public bool Dead => Fraction <= 0.0f;` (mirrors `Ship.Dead`,
`Ship.cs:97`).

Formula, cited to `probeigc.h`:
- `amount *= DamageConstant(type, ProbeType.DefenseType)` (`probeigc.h:95`);
  `Probe` carries its **own private copy** of `Ship.DamageConstant`
  (`Ship.cs:668-677`) since it has no shared base with `Ship`. Derives the
  `Mission`/`Core` exactly as the class already derives its host seam:
  `Launcher is Ship s ? s.Mission : null` (`Probe.cs`, the same route the
  expiry path's `launcherShip.Mission?.Site` uses).
- `maxHP = ProbeType.HitPoints` (`probeigc.h:94`); `_fraction −= amount/maxHP`
  (`probeigc.h:116`); if `≤ 0` clamp to `0` (`probeigc.h:119`) and — **only the
  first crossing**, `oldFraction > 0` (`probeigc.h:121`) — remove from the
  sector and raise `IIgcSite.ProbeDestroyedEvent` exactly once (the identical
  pair `Probe.OnUpdate`'s expiry branch makes, `Probe.cs`), returning
  `DamageResult.Killed` (`probeigc.h:139`). Otherwise return
  `DamageResult.HullDamage`.
- **No friendly-fire gate.** The native gate only blocks damage inside a
  nested condition whose inner `return c_drNoDamage` requires an **armed**
  probe (`probeigc.h:74-89`: `if (m_probeType->GetProjectileType() != NULL)
  return c_drNoDamage`). The two authorial comments directly above it —
  *"Always damage probes. No more friendly probes blocking base exits and
  alephs !!!"* (`probeigc.h:80`) and *"Allow friendly probes to be damaged"*
  (`probeigc.h:86`) — match the code, not contradict it. `Ship.DeployProbe`
  (P99) never sets a projectile type, so every probe this port can deploy is
  unarmed and the inner branch never fires. **Therefore damage always applies,
  regardless of side** — a real, cited difference from `Ship`'s gate
  (`shipIGC.cpp:1222-1228`). **Boundary condition (stated plainly):** if a
  future task ever arms a probe (sets `DataProbeTypeIGC.ProjectileTypeId`),
  this simplification needs revisiting. No `EabmCapabilities`/
  `HasCapability(c_eabmRescueAny)` parsing — dead code for an unarmed probe.
- **No shield branch.** A probe never mounts a `Shield` part — damage goes
  straight to the hull fraction, no `ApplyShieldDamage` analogue needed.
- **Assume `amount >= 0`** — the native negative-`amount` (repair) branch
  (`probeigc.h:101-110`) is dead code everywhere (no repair source exists for
  anything; `Ship.ReceiveDamage` only keeps it structurally, `Ship.cs:608-616`).
  Stated plainly rather than silently dropped.
- Achievement-mask side effect (`probeigc.h:126-135`) **not ported** — no
  achievement system in this port.
- Experience multiplier (`probeigc.h:98-99`) and debris
  (`probeigc.h:142-143`) **not ported** — matching `Ship`'s own omissions
  (`Ship.cs:583`, `Ship.cs:621`).

### 2. `Sector.ResolveCombatCollisions` — `src/Allegiance.Core/Model/Sector.cs`

Extended the **same** swept detector, no new pass:
- Collider-building `switch` now has a third leaf:
  ```csharp
  Probe p => new Collider(p, p.Position, Vector3.Zero, Orientation.Identity,
      hull: null, p.ProbeType.Radius, isStatic: true, HitShape.Sphere),
  ```
  `isStatic: true` and zero velocity because a probe never moves (the fact
  `Probe.cs`'s remarks already establish); `ProbeType.Radius` is the exact
  field the native sets the probe's collision radius from
  (`probeigc.cpp:118`, `SetRadius(dataProbeType->radius)`).
- Generalized the event-resolution loop so the non-ordnance side of a combat
  event can be **either** a `Ship` or a `Probe`, dispatched to whichever's own
  `ReceiveDamage`/friendly-fire rule applies (a small `if (ship is not null)`
  / `else` pattern-match — the project's preferred "little duplication over a
  forced shared abstraction", since the two genuinely differ):
  - **Ship branch** — logic **unchanged and preserved verbatim**: the
    `ship.Dead` check, the launcher self-exclusion, the friendly-fire gate
    (`shipIGC.cpp:1222-1228`), the impact positions, the `ReceiveDamage` call,
    the ordnance terminate/remove, the blast-on-impact block (P97), the
    `if (ship.Dead) _models.Remove(ship)` death bookkeeping.
  - **Probe branch (new)** — **no friendly-fire gate** (unarmed probe takes
    direct fire from any side), its own `probe.Dead` skip and self-guard, the
    same `ReceiveDamage`/terminate/blast/death-remove shape. The
    missile-destroyed consistency call (P91) is target-independent and hoisted
    once after the branch (ordnance-relative, unchanged for a ship target).
  - **Blast-on-impact fires for a probe target exactly as for a ship** — the
    P97 trigger is unconditional ("Area effect weapon: create an explosion (no
    matter what)", `projectileIGC.cpp:206`), so the probe branch recreates the
    real `Explosion` at the impact position. `ResolveExplosion` itself is
    untouched — it still affects only `Ship`+`Mine`.

### No `Server.cs` / `ServerHost.cs` / `IIgcSite.cs` changes — verified true

I confirmed the "already wired" claim myself before relying on it:
`Probe.ReceiveDamage`'s destroy path raises the **same**
`IIgcSite.ProbeDestroyedEvent` the expiry path already raises
(`Probe.cs`), and `Server.cs` already bridges it: `_host.ProbeDestroyed =
BroadcastProbeDestroyed` (`Server.cs:277`) → `FM_S_PROBE_DESTROYED(85)`
(`Server.cs:2371-2406`), delivered to the same same-side audience that saw the
probe deploy (`_teammateExportSent`-gated loop). No cause field distinguishes
expiry from a kill on the wire — the one event reaches both. **Zero
`Server.cs`/`ServerHost.cs`/`IIgcSite.cs` changes were needed, confirmed by
real test output (below).**

## Out of scope (next follow-up, named)

- **Extending `ResolveExplosion` (P96) to also damage nearby probes.** Real and
  native-evidenced (the P96 delegation log's summary: "every damagable model in
  range (ships/asteroids/stations/missiles/probes...) gets the standard
  `ReceiveDamage`"; native blast list `clusterIGC.cpp:378-379`). This task
  deliberately does NOT do it — a second, separately-sized change to a different
  method. It is the clear next step in this arc, though not necessarily the next
  task (the user's call). Consequence proven in this task's blast test: a probe
  directly hit by a blast round takes only its direct hit; its own blast still
  splashes nearby ships but not nearby probes.
- Armed-probe friendly-fire protection / `EabmCapabilities` parsing — deferred
  (no armed probes exist yet).
- Achievement-mask side effect — no achievement system.
- Repair path (dead code).
- Scanning/detection, ripcord/teleport, autonomous firing — unchanged deferrals.

## Verification

### Domain — `ProbeCombatTests` (`tests/Allegiance.Core.Tests/ProbeCombatTests.cs`)
Real `PCore001.igc` data, real ship launcher + mission (probe derives the damage
matrix the production way):
- **Partial:** a hit worth exactly 40% of maxHP returns `HullDamage`, drives
  `Fraction` 1.0 → **0.600**, probe stays in its sector, no destroy event.
- **Lethal:** returns `Killed`, clamps `Fraction` to 0, removes the probe from
  the sector, raises `ProbeDestroyedEvent` **exactly once**.
- **Post-death call:** does **not** double-raise — second call past death
  returns `HullDamage` and the destroy count stays 1 (native
  `probeigc.h:91,121`), the same discipline the ship path's `Dead` check gets.
- **Blast-on-impact through the probe path:** a blast-capable round (real type
  71, `BlastRadius` 250) whose **direct target is a probe** still creates a real
  `Explosion` at the impact position: the probe took **direct-only** 0.2500
  (= 1 − 0.75 direct), the nearby cross-side ship took the exact squared-falloff
  splash **0.3479** (d = 79.7), the launcher took nothing.

Measured:
```
P100-DOMAIN: partial hit 0.40 @ dtm 1.000 → fraction 0.600; lethal → Killed, removed, 1 destroy event (once, no double-raise)
P100-BLAST: probe 1.00→0.2500 (direct-only 0.2500); splash ship 1.00→0.6521 = 1 − 0.3479 (d=79.7); shooter 1.0
```

### Live loopback — `ProbeCombatLiveTests` (`tests/Allegiance.Core.Tests/ProbeCombatLiveTests.cs`)
Mirrors `MineCombatTests`/`MissileCombatTests`' 3-bot/2-side construction
(guaranteed same-side pair + cross-side enemy); deploys a real probe via
`Ship.DeployProbe` on the server-side ship, fires real missiles via
`Ship.FireMissile`:
1. **Cross-side hit** reduces `Fraction` by the exact decoded `0.075` (= 1 →
   **0.925**), and the probe survives (still in sector, no destroy). ✅
2. **Same-side (friendly) fire also damages the probe** — a teammate's own
   missile deals the identical `0.075` (probe 1.0 → 0.925), a real,
   deliberately-asserted difference from every ship/mine combat test in this
   project, proving the no-FF-gate finding. ✅
3. **Lethal hit really Killed it:** the probe is removed from the sector and the
   same-side peer who saw it deploy receives a real **`ProbeDestroyed`(85)**
   naming that same probe id — confirming the "no `Server.cs` changes needed"
   claim is actually true. ✅
4. **Survival:** the non-lethal cross-side probe stayed in the sector, not
   destroyed. ✅
5. **Blast-on-impact through the probe path** — a blast round directly hitting a
   probe creates the real Explosion (proven by the exact splash on a nearby
   ship), reuse of `BlastOnImpactTests`' recipe. ✅
6. **Un-fired probe still expires via `Lifespan`** — `ProbeBroadcastTests` /
   `ProbeDomainTests` pass unmodified (below); the expiry path is untouched by
   this change.

Measured:
```
P100-CROSS: probe 1.00→0.925 (expect 1 − 0.075); survived, still in sector
P100-FRIENDLY: same-side missile still dealt 0.075 (probe 1.00→0.925); NO friendly-fire gate for a probe (ship path would pass through)
P100-KILL: probe 5464 killed (fraction 1.0→0.0), removed from sector, PROBE_DESTROYED(85) to same-side peer at 1051
```

### The ship combat path is byte-for-byte behaviorally identical

Every existing suite passes unmodified: `CombatDamageTests`,
`CombatWeaponFiringTests`, `CombatShieldTests`, `MissileCombatTests`,
`MineCombatTests`, `ExplosionPropagationTests`, `BlastOnImpactTests`,
`ProbeBroadcastTests`, `ProbeDomainTests`, `LiveCombatWireTests`.

**Baseline in:** 2375 tests passing (P99 checkpoint).
**Total now:** 2380 (5 new). Three consecutive clean runs below.

---

*Report: PROBE_COMBAT_DESTRUCTIBILITY_P100.md — every claim cites the file:line
actually read; friendly fire is real, cited (`probeigc.h:74-89`), not a bug.*
