# P99 — Probe sim object: minimal static existence + same-side broadcast

Branch `master`; top commit `aa298fd` (P98). Native source read from the
sibling checkout `/home/domingo/Development/Allegiance/src/Igc/` (this repo's
`src/` holds only the managed port; the C# files cite `src/Igc/...` for that
checkout's layout).

## What is real (implemented and verified)

- **`Probe` sim class** (`src/Allegiance.Core/Sim/Probe.cs`) — a
  `Model` living in a `Sector`, constructed
  `(short id, Sector sector, DataProbeTypeIGC probeType, Vector3 position,
  double now, short? sideId, Model? launcher = null)`. No velocity/forward —
  it sits exactly where it was deployed (the native probe is `c_mtStatic` with
  `SetMass(0.0f)`, `src/Igc/probeigc.cpp:115,142`).
  `TimeExpire = now + probeType.Lifespan` (`probeigc.cpp:133`).
  `OnUpdate` does **only** the expiry check — no position change, no ripcord,
  no firing (`probeigc.cpp:333-337`: the real native event it mirrors is
  `KillProbeEvent`). On expiry: `sector.Remove(this)` then raise
  `ProbeDestroyedEvent` (named for this project's `XxxDestroyedEvent`
  sibling convention; its native intent is `KillProbeEvent`).
- **A real `SideId` from day one.** Carried from the deploying ship at
  construction (mirroring `DataProbeExport.sideID` / `ExportProbe.FromProbe`'s
  `sideID`). This is the deliberate design improvement over P94's `Mine`,
  which shipped side-less and needed P95's retrofit — so the destructibility
  follow-up needs no retrofit.
- **`Fraction` placeholder** defaulting to `1.0f` — stored, consulted by
  nothing this slice (standing exactly where `Mine.Fraction` sat in its own
  P94 era, before P95/P96 gave it meaning). In place now *for* destructibility.
- **`Ship.DeployProbe(DataProbeTypeIGC, double now)`** — directly-callable,
  same style as `Ship.DeployMine`. Places the probe at the ship's current
  position, no target. Not wired into `ControlData`/`StateM` dispatch — the
  same explicit deferral every prior deploy/fire method in this arc makes
  (`FireMissile`, `DeployMine`).
- **Two new `IIgcSite` events**, no-op defaults mirroring
  `MineCreatedEvent`/`MineDestroyedEvent` exactly:
  `ProbeCreatedEvent(Probe)` (raised inside `Ship.DeployProbe`) and
  `ProbeDestroyedEvent(Probe)` (raised inside `Probe`'s own expiry handling).
  Bridged through `ServerHost` the same way (`ProbeCreated`/`ProbeDestroyed`
  hooks, registered in `Server.cs` boot).
- **Same-side broadcast, both directions**, mirroring the exact
  `_teammateExportSent`-gated idiom every prior ordnance broadcast uses:
  on creation, a real `ExportProbe` to the deploying ship's own peer plus
  same-side teammates; on destruction (expiry), a real `ProbeDestroyed`(85)
  to the same audience.
- **Factory `ExportProbe.FromProbe(Probe, uint, short, short)`** — the
  analogue of `ExportMine.FromMine`, mirroring `CprobeIGC::Export`
  (`probeigc.cpp:535-568`) field-for-field, with `otTarget`/`oidTarget` =
  NA/-1 (the no-target case, `probeigc.cpp:558-562`).

## What is deferred (real, separate, later work)

- **Ripcord/teleport activation** (`ActivateTeleportProbe`) — the
  `GetRipcordDelay()`-gated timer, `probeigc.cpp:340-349`. Unexplored mechanic.
- **Autonomous weapon firing** (`m_projectileType`/`m_ammo`/targeting
  sequence, `probeigc.cpp:352-528`) — comparable in size to `Weapon.FireWeapon`;
  the same deferral P98 made for the wire layer.
- **Scanning/detection** (`c_mtScanner`/`c_mtSeenBySide`/`c_mtPredictable`,
  `probeigc.cpp:115`) and `PROBES_UPDATE(92)` — the actual point of a
  reconnaissance probe; a separate later message + mechanic.
- **Cross-side/enemy visibility** of the probe itself — same-side broadcast
  only, matching every prior ordnance broadcast in this project.
- **A real "deploy probe" control input** into `ControlData`/`StateM`/live
  dispatch.

## Most natural next follow-up

**Combat destructibility** — a probe being shot down by direct weapon/missile
fire. Unlike mines (which explode on proximity contact), probes are genuinely
`c_mtDamagable` *and* `c_mtHitable` (`probeigc.cpp:115`), so this needs
`ResolveCombatCollisions` extended with a real `Probe` target type — a
separate, later task, not this one. That follow-up is why this task added
`SideId` (ownership/friendly-fire gate) and `Fraction` (damage pool) now,
before they are consumed. Whether it is literally the *next* task is the
user's call.

## Verification

- Full suite `Passed! 2375` (baseline was 2373; +2 new tests: `ProbeDomainTests`,
  `ProbeBroadcastTests`).
- 3 consecutive clean runs confirmed (see commit message / run log).

## Files

- `src/Allegiance.Core/Sim/Probe.cs` — new
- `src/Allegiance.Core/Sim/Ship.cs` — `DeployProbe`
- `src/Allegiance.Core/Sim/IIgcSite.cs` — two new no-op events
- `src/Allegiance.Core/Wire/ExportProbe.cs` — `FromProbe` factory
- `src/Allegiance.Server/ServerHost.cs` — two new host hooks/bridges
- `src/Allegiance.Server/Server.cs` — hook registration + two broadcasts
- `src/Allegiance.Bot/Bot.cs` — probe decode instrumentation
- `tests/Allegiance.Core.Tests/ProbeDomainTests.cs` — new
- `tests/Allegiance.Core.Tests/ProbeBroadcastTests.cs` — new
- `PROBE_SIM_OBJECT_P99.md` — this
