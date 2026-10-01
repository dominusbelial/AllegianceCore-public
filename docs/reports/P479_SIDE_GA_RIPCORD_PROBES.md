# P479 — the side GA scales ripcord time, and the probe projectile's speed and lifespan

**Landing:** `master` (committed, not pushed). Repo:
`/home/domingo/Development/AllegianceCore`. Branch verified with
`git branch --show-current` → `master`.

## Problem

The parity pass found side global attributes (GA) computed and broadcast but
never applied. P446/P467/P467b/P475/P476/P477/P478 wired flight, weapon output,
ship/station durability, visibility, mining and economy. The last unwired
consumers were the ripcord countdown (`c_gaRipcordTime = 20`) and the
armed-probe projectile seam (`c_gaSpeedAmmo = 14`, `c_gaLifespanEnergy = 15`),
the latter left unwired by P467 because a probe fires its OWN projectile with
its OWN speed — not a one-liner through the shared weapon path.

## What native does (read exactly, not guessed)

**Ripcord — `c_gaRipcordTime` (20).** `MyHullType::GetRipcordSpeed`
(`src/Igc/shipIGC.cpp:4664-4666`) returns:

```cpp
return m_pHullData->ripcordSpeed / m_pship->GetSide()->GetGlobalAttributeSet().GetAttribute(c_gaRipcordTime);
```

So `c_gaRipcordTime` **divides** the hull's ripcord speed (a higher factor is a
FASTER ripcord / shorter countdown). Every ported ripcord consumer maps to a
native `GetRipcordSpeed()` call, so making the port's `Ship.RipcordSpeed`
GA-effective routes them all through the divide.

**Probe projectile — `c_gaSpeedAmmo` (14) and `c_gaLifespanEnergy` (15).** A
probe's fire loop (`probeigc.cpp:366-368`) scales the round's speed only for an
**ammo** probe, and its `GetProjectileLifespan` (`probeigc.h:223-233`) scales
the round's lifespan only for an **energy** probe:

```cpp
// probeigc.cpp:367 (fire)
float speed = m_projectileType->GetSpeed();
if (m_ammo != 0)
    speed *= GetSide()->GetGlobalAttributeSet().GetAttribute(c_gaSpeedAmmo);

// probeigc.h:226 (GetProjectileLifespan)
ls = m_projectileType->GetLifespan();
if (m_ammo == 0)
    ls *= GetSide()->GetGlobalAttributeSet().GetAttribute(c_gaLifespanEnergy);
```

These are the mirror image of the weapon path's gates (ammo → speed, energy →
lifespan; `weaponIGC.cpp:220`, `weaponIGC.h:167-170`).

## Change (reuses P446's single-GA-accessor pattern)

### `GlobalAttributeSet.cs` — named index constants

Added `RipcordTime = 20` (`c_gaRipcordTime`, `igc.h:615`) in the same
named-constant style P446/P467b/P477/P478 established, with its `igc.h`
citation and a note that it DIVIDES (unlike the multiply accessors). `SpeedAmmo`
(14, `igc.h:606`) and `LifespanEnergy` (15, `igc.h:607`) already exist from P467
and are reused for the probe seam.

### `Ship.cs` — `RipcordSpeed` becomes GA-effective

`Ship.RipcordSpeed` now returns `_hull.RipcordSpeed /
SideGlobalAttributes().Get(GlobalAttributeSet.RipcordTime)` (the port of
`GetRipcordSpeed()`, `shipIGC.cpp:4665`), and `ResetRipcordTimeLeft()` seeds the
countdown from `RipcordSpeed` instead of the raw `_hull.RipcordSpeed`. Because
every ported ripcord consumer already read `ship.RipcordSpeed` — the countdown
seed, `IsSafeToRipcord`'s spin-up horizon (`Ship.cs:5437`), `Probe.CanRipcord`'s
expiry margin (`Sector.ResolveRipcordCountdowns`, `Sector.cs:1358`), and the
server's `FindRipcordModel` anchor pick (`Server.cs:816`) — they all now
automatically see the GA-effective value. **P454's teleport-probe tier is
included**: the probe-anchor legality re-check feeds `ship.RipcordSpeed` into
`Probe.CanRipcord`, so a ripcord-accelerated side's countdown also shortens the
probe-expiry margin exactly as native intends.

### `Probe.cs` — the armed-probe projectile seam

Added three members that a future armed-probe firing implementation builds its
round from (probe firing is NOT implemented — see the named follow-up):

- `ResolveProjectileType()` — resolves `ProbeType.ProjectileTypeId` to its
  `DataProjectileTypeIGC` via the launcher ship's mission core
  (`m_projectileType`, `probeigc.cpp:107`).
- `EffectiveProjectileSpeed(DataProjectileTypeIGC)` — `speed × GA[SpeedAmmo]`
  when `ProbeType.Ammo != 0` (`probeigc.cpp:366-368`), else raw.
- `EffectiveProjectileLifespan(DataProjectileTypeIGC)` — `lifespan ×
  GA[LifespanEnergy]` when `ProbeType.Ammo == 0` (`probeigc.h:223-233`), else
  raw.

Both reuse the probe's existing P476 `SideGlobalAttributes()` resolution, so a
probe deployed by a real ship on a side resolves that side's GA.

### `Game1.Rendering.cs` — client ripcord HUD self-expiry (scope 2)

The client self-expires its "RIPCORDING TO …" HUD label after the server's
countdown duration (`RipcordDurationSeconds`). P479 divides that duration by
the owning side's `c_gaRipcordTime` (learned from `ClientWorld.GlobalAttributesFor`
— the SIDE_ATTRIBUTE_CHANGE(76) data P446 already wired in) so the client expiry
matches the now GA-effective server countdown.

## Real developments and civs in PCore001 that touch these three

Scanned all 305 developments and all 10 civs of `PCore001.igc` for
`Gas[14]/[15]/[20]` / `GasBaseAttributes[14]/[15]/[20]`. **Real developments
exist for all three** (unlike P478's economy pair), so the mandated E2E check is
performed:

**Developments (id: name — attribute deltas):**

| Dev | Name | Gas[14] | Gas[15] | Gas[20] |
|---|---|---|---|---|
| 141 | GA: EW Range 1 | 1 | 1.1 | 1 |
| 142 | GA: EW Range 2 | 1 | 1.1 | 1 |
| 153 | GA: PW Range 1 | 1.1 | 1 | 1 |
| 154 | GA: PW Range 2 | 1.1 | 1 | 1 |
| 155 | GA: Ripcord 1 | 1 | 1 | 1.15 |
| 156 | GA: Ripcord 2 | 1 | 1 | 1.15 |

**Civ base attributes (real per-side starting multipliers):**

| Civ | [14] SpeedAmmo | [15] LifespanEnergy | [20] RipcordTime |
|---|---|---|---|
| Dreghklar | 1 | 1 | 1 |
| Technoflux | 1 | 1.2 | 1.3 |
| Ga'Taraan | 1 | 1 | 0.9 |
| Phoenix | 1 | 1 | 1 |
| Omicron | 1.1 | 0.9 | 1 |
| Gigacorp | 1 | 1 | 1 |
| Iron Coalition | 1 | 1 | 1 |
| Bios | 1 | 1 | 1 |
| Belters | 1 | 1 | 1 |
| Rixian | 1.2 | 1.2 | 1 |

The real-production E2E (`Completing_dev_155_GA_Ripcord_scales_the_countdown`)
completes dev 155 'GA: Ripcord 1' (`Gas[20]=1.15`) on a Dreghklar side (base
`Gas[20]=1.0`) through the exact production completion apply
(`Server.Economy.cs:979`: `side.GlobalAttributes.Apply(FromArray(dev.Gas))`),
verifying the ripcord countdown shrinks by 1.15×:

```
P479-E2E: dev 155 'GA: Ripcord 1' (gas[20] 1.150) -> countdown 5.000s -> 4.348s (ratio 1.150, expect ~1.15)
```

The armed-probe projectile cannot be E2E'd live (armed probes do not fire — the
named follow-up below), so the per-consumer synthetic tests are the primary
assertions for indices 14/15.

## Armed probes still don't fire (named follow-up)

Wiring the probe projectile GA did NOT build probe firing — the task forbade it.
Armed/gun-tower probes (those with a real `ProjectileTypeId`) still do not fire
in the port (the parity-pass finding at `Sim/Probe.cs:39-45`: the probe has no
autonomous targeting/firing sequence; `DeployProbe` never sets a projectile
type). **Named follow-up:** implement the armed-probe firing loop
(`probeigc.cpp:352-528`) and, in it, build each round from
`Probe.ResolveProjectileType()` + `Probe.EffectiveProjectileSpeed()` +
`Probe.EffectiveProjectileLifespan()` — the seam this task wired so it is
correct the moment firing lands.

## Verification

### Pre-fix failure (recorded)

Ran the P479 tests with the three GA applications temporarily neutralized (raw
`RipcordSpeed`, raw probe speed/lifespan; accessor signatures kept). **3 of the
6 tests failed** on the unfixed code:

```
P479SideGaRipcordProbesTests.RipcordTime_GA_halves_the_seeded_countdown [FAIL]
  Expected: 2.5 (hull.RipcordSpeed / 2.0), Actual: 5 (raw — GA had no effect)
P479SideGaRipcordProbesTests.SpeedAmmo_GA_scales_ammo_probe_speed_but_not_energy_probe [FAIL]
  Expected: 1500 (1200 × 1.25), Actual: 1200 (raw)
P479SideGaRipcordProbesTests.LifespanEnergy_GA_scales_energy_probe_lifespan_but_not_ammo_probe [FAIL]
  Expected: 1.5 (1.0 × 1.5), Actual: 1 (raw)
```

The other three (`Probe_resolves_its_projectile_type_from_the_launcher_core`,
`Indices_match_native_and_sideless_probe_is_identity`, and the dev-155 E2E)
pass either way because they assert the plumbing/resolver / index identity / the
E2E path (which applies the real dev's gas — GA-agnostic), matching P478's
observation.

Restored, all 6 pass:

```
P479-E2E: dev 155 'GA: Ripcord 1' (gas[20] 1.150) -> countdown 5.000s -> 4.348s (ratio 1.150)
```

### Regression found & fixed (collateral of the ripcord wiring)

Wiring `c_gaRipcordTime` is a REAL behaviour change, and two existing live NPC
tests broke: `NpcRipcordTravelLiveTests.Cross_cluster_npc_rips_…` and
`Unsafe_npc_defers_ripcord_and_retries_…`. Their NPC is on the Ga'Taraan side
(civ 3, `GasBase[20]=0.9`), so the newly-wired countdown grew from 30s to
33.3s (30 / 0.9) — longer than the tests' 600-tick pump window (600 × 0.05s =
30s). Verified these **pass on pre-P479 code and fail on the first wired build**
(the new behaviour, not a flake). Fixed by bumping both ripcord pump budgets
from 600 to 1000 ticks — the test verifies "ripcord-arrives-via-teleport, not
warp-pathing", whose point is the mechanism, not the (now native-correct)
duration. Documented here; no production semantics changed.

### New tests

`tests/Allegiance.Core.Tests/P479SideGaRipcordProbesTests.cs` (6 tests, all
deterministic except noted — no live sockets):

- `RipcordTime_GA_halves_the_seeded_countdown` — the mandated synthetic
  non-1.0 ripcord test (GA[20]=2.0 → countdown halves; identity → raw).
- `SpeedAmmo_GA_scales_ammo_probe_speed_but_not_energy_probe` — ammo probe
  scales speed ×1.25, energy probe raw (probeigc.cpp:366-368).
- `LifespanEnergy_GA_scales_energy_probe_lifespan_but_not_ammo_probe` — energy
  probe scales lifespan ×1.5, ammo probe raw (probeigc.h:223-233).
- `Probe_resolves_its_projectile_type_from_the_launcher_core` — resolver
  returns the real projectile from a real armed probe; null for unarmed.
- `Indices_match_native_and_sideless_probe_is_identity` — indices 14/15/20 match
  native; a sideless probe reads identity.
- `Completing_dev_155_GA_Ripcord_scales_the_countdown` — E2E through real dev
  155 (above).

### Build & suite

- Whole-solution build `/home/domingo/.dotnet/dotnet build`: **0 warnings,
  0 errors**.
- Full `/home/domingo/.dotnet/dotnet test`, **3 consecutive runs** (final,
  all-6-tests state). No failure in any run is attributable to P479; the only
  failures are the documented `docs/TEST_FLAKES.md` (P471) environmental flakes
  and the documented P472 production bug. The complete per-run failure set:

  - **Run 1:** `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_…` (P472
    production bug, recurs every run), `ConquestTerritoryLiveTests.Conquest_win_
    in_one_mission_does_not_leak_to_another` (pre-existing conquest-capture
    path/env failure, already noted in the P455 report as failing on the
    pristine tree too), `CargoBayReloadTests.Reload_fuel_pack_…` (documented
    state-machine load-flake).
  - **Run 2:** `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_…` (flagged
    load-flake), `DockOut_WatcherLatchesGone_…` (P472), `LiveWinConditionTests.
    Board_refused_once_mission_over` (flagged), `LiveRipcordTests.Re_target_
    activates_to_requester_only` (flagged LiveRipcord load-flake — verified
    green in isolation), `BotFlightTests.Abrupt_disconnect_during_broadcast_…`
    (live-wire).
  - **Run 3:** `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_…`,
    `DockOut_WatcherLatchesGone_…` (P472), `FullSessionKillIntegrationTests.
    FullSession_ThreePeers_…` (flagged).

  The 6 P479 tests passed in all three runs (absent from every failure list),
  and no pre-existing ripcord/probe test failed as a result of the change: the
  only ripcord-related full-suite flake (LiveRipcord Re_target) is green in
  isolation, and the `NpcRipcordTravelLiveTests` collateral was fixed
  deterministically above. The dedicated ripcord+probe filter is green 87/87,
  and the MonoGame ripcord live tests 7/7.

## Updated remaining-GA ledger (what is still unwired after P479)

**Nothing — all 25 `c_ga…` indices are now wired** across the parity pass:

| Index | Constant | Task |
|---|---|---|
| 0–3 | MaxSpeed, Thrust, TurnRate, TurnTorque | P446 |
| 4–7 | MaxArmorStation, ArmorRegenerationStation, MaxShieldStation, ShieldRegenerationStation | P475 |
| 8–10, 13 | MaxArmorShip, MaxShieldShip, ShieldRegenerationShip, MaxEnergy | P467b |
| 11–12 | ScanRange, Signature | P476 |
| 14–16, 21–22 | SpeedAmmo, LifespanEnergy, TurnRateMissile, DamageGuns, DamageMissiles | P467 (+ probe seam 14/15 here) |
| 17–19 | MiningRate, MiningYield, MiningCapacity | P477 |
| 20 | RipcordTime | **P479 (this task)** |
| 23–24 | DevelopmentCost, DevelopmentTime | P478 |

The sole remaining open item is the **armed-probe firing loop** itself (the
probe's autonomous target/fire sequence), which is a behaviour gap, not a GA
consumer — the probe projectile GA is wired and correct at the seam, waiting for
that loop. Recorded as the named follow-up above.

## Scope boundaries honored

Only `c_gaRipcordTime` (20), `c_gaSpeedAmmo` (14, probe seam) and
`c_gaLifespanEnergy` (15, probe seam) were wired. No other GA index was touched
(all others are P446–P478). How developments complete / GA accumulates is
unchanged (already correct, P189/P302).
