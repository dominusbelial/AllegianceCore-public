# P476 — apply the side's GlobalAttributeSet to scan range and signature (visibility)

Date: 2026-10-01
Branch: master (committed, not pushed)
Repo: `/home/domingo/Development/AllegianceCore`

## Summary

The parity pass (P446/P467/P467b/P475) wired the side-global-attribute (GA)
multipliers for flight, weapon output, ship durability and station durability,
but the two **visibility** attributes remained unwired: the port's cross-side
detection sweep read raw hull/station/probe scanner ranges and raw signatures,
so a civilization's `GasBaseAttributes` or a completed `GA: Ship Sensors` /
`GA: Ship Signature` development had no effect on who could see whom.

P476 wires both:
- **`c_gaScanRange` (11)** — boosts the **scanner** side's detection radius,
  through three new effective getters
  (`Ship.EffectiveScannerRange()`, `Station.EffectiveScannerRange()`,
  `Probe.EffectiveScannerRange()`) that the sweep now consumes.
- **`c_gaSignature` (12)** — attenuates a **target** side's signature wherever
  detection compares signature against scan range, applied in the sweep via the
  native `m /= targetSide.c_gaSignature` divisor.

It reuses the established P446 pattern verbatim: named index constants in
`GlobalAttributeSet.cs` (with `igc.h` citations) + effective accessors that
multiply the raw data value by `SideGlobalAttributes().Get(index)` (identity
when the owner's side/mission is unresolvable). No second pattern was invented.

## Change

### `GlobalAttributeSet` — two named index constants

- `ScanRange = 11` (`c_gaScanRange`, `src/Igc/igc.h:601`)
- `Signature = 12` (`c_gaSignature`, `src/Igc/igc.h:602`)

Updated the in-file ledger comment (mining 17–19, ripcord 20, economy 23–24 now
remain unwired; see the ledger at the end).

### Effective scanner-range accessors (the scanner-side `c_gaScanRange`)

| Kind | Accessor | Formula | Native citation |
|---|---|---|---|
| Ship | `Ship.EffectiveScannerRange()` | `HullType.ScannerRange × GA[ScanRange]` | `MyHullType::GetScannerRange`, `shipIGC.cpp:4558-4561` |
| Station | `Station.EffectiveScannerRange()` | `Type.ScannerRange × GA[ScanRange]` | `MyStationType::GetScannerRange`, `stationIGC.cpp:591-595` |
| Probe | `Probe.EffectiveScannerRange()` | `ProbeType.ScannerRange × GA[ScanRange]` | `CprobeIGC::InScannerRange`, `probeigc.h:165` |

Each reuses the owning model's `SideGlobalAttributes()` resolver
(ship/station: via their `Mission.Sides`; probe: via its Ship launcher's
`Mission`, the class's established host seam). A sideless / unresolvable-side
model reads `GlobalAttributeSet.Identity` → the raw value, exactly the P446
contract.

### The sweep (`Sector.ResolveCrossSideVisibility`) — both attributes

- Scanner range sources now pass the **effective** value
  (`s.EffectiveScannerRange()`, `p.EffectiveScannerRange()`,
  `st.EffectiveScannerRange()`) instead of the raw type values — the scanner
  side's `c_gaScanRange` boost, matching native ship/station/probe scanners.
- A target side's **`c_gaSignature`** resistance is folded into each
  participant's stored detection signature at add time:
  `effectiveDetSig = rawSignature / targetSide.c_gaSignature` (identity 1.0 for
  a sideless asteroid target, exactly native's `if (pside)` guard). Since a
  participant's stored signature is consumed **only** as a TARGET signature
  (the scanner role uses range/radius/position only), folding it in once per
  participant is equivalent to native's per-pair division
  (`m = scannerRange · targetSig`, then `m /= targetSide.c_gaSignature`).

## Per-site native map (c_gaScanRange / c_gaSignature consumers)

Native reads both attributes in exactly four places each; the port's single
visibility consumer is `Sector.ResolveCrossSideVisibility` (P132), where the
three native per-scanner `InScannerRange` implementations (ship/station/probe)
collapse into one sweep:

| # | Attribute | Native site | Port routing |
|---|---|---|---|
| 1 | `c_gaScanRange` | `shipIGC.cpp:4558` (ship scanner range) | `Ship.EffectiveScannerRange()` → sweep |
| 2 | `c_gaScanRange` | `stationIGC.cpp:591` (station scanner range) | `Station.EffectiveScannerRange()` → sweep |
| 3 | `c_gaScanRange` | `probeigc.h:165` (probe scanner range) | `Probe.EffectiveScannerRange()` → sweep |
| 4 | `c_gaScanRange` | `clusterIGC.h:494` (`bSimpleEye`) | **Not ported** — see below |
| 5 | `c_gaSignature` | `shipIGC.h:357` (target-side divisor) | sweep `SideSignatureFactor` |
| 6 | `c_gaSignature` | `stationIGC.h:322` (target-side divisor) | sweep `SideSignatureFactor` |
| 7 | `c_gaSignature` | `probeigc.h:177` (target-side divisor) | sweep `SideSignatureFactor` |
| 8 | `c_gaSignature` | `clusterIGC.cpp:152` (`SigMod` to `bSimpleEye`) | **Not ported** — see below |

### Sites deliberately not ported (stated, not fabricated)

- **`bSimpleEye` / the station-roid "eye" mechanic** (`clusterIGC.h:486-500`,
  `clusterIGC.cpp:140-165`): a niche Imago feature where a ship scans an
  asteroid *roid* that a station hasn't yet "eyed" (`GetRoidSig`/
  `GetRoidSide`/`SetRoidSide`). The port has **no roid concept at all** — grep
  of the whole port tree for `Roid`/`bSimpleEye`/`GetRoidSig` returns nothing —
  so neither the `bSimpleEye` `c_gaScanRange` multiply nor its `c_gaSignature`
  `SigMod` has a port consumer to wire. The sweep's asteroid-target branch
  (P134) is the visibility treatment the port actually has, and it flows
  through the signature divisor at site 5-7 (asteroid is sideless → identity).
- **`CanSideSeeCluster` / the probe presence reveal** (`Mission.cs:1150-1204`,
  P259/P260): this is the strategic-map **presence** abstraction (a live
  probe *occupying* a cluster reveals it at distance zero), **not** a
  per-object scan-range comparison — the p260 reference established it never
  rescans across clusters and the reveal is presence-anchored. No
  `c_gaScanRange`/`c_gaSignature` arithmetic occurs there, so it is correctly
  left untouched.

## Real developments and civs in PCore001 that change these two

Scanned `PCore001.igc` (all 25 Gas cells for every civ and non-tech-only
development). Developments with a real (≠1.0) delta on an index in scope:

| Dev id | Name | gas[11] scanRange | gas[12] signature |
|---|---|---|---|
| 157 | GA: Ship Sensors 1 | **1.1** | 1.0 |
| 158 | GA: Ship Sensors 2 | **1.1** | 1.0 |
| 163 | GA: Ship Signature 1 | 1.0 | **1.1** |
| 164 | GA: Ship Signature 2 | 1.0 | **1.1** |

(The other ~200 developments are identity 1.0 on both indices.) So **real
developments exist for both attributes** — the end-to-end test below uses
dev 157 'GA: Ship Sensors 1'.

Civs with a non-identity base in these two (`GasBaseAttributes`):

| Civ | GA[11] scanRange | GA[12] signature |
|---|---|---|
| Dreghklar | 0.9 | 0.95 |
| Technoflux | 1.1 | 1.0 |
| Ga'Taraan | 1.0 | 1.0 |
| Phoenix | 1.1 | 1.0 |
| Omicron | 1.1 | 1.05 |
| Draconium | 1.0 | 1.0 |
| Gigacorp | 1.0 | 1.0 |
| Iron Coalition | 1.0 | 0.85 |
| Bios | 1.15 | 1.15 |
| Belters | 1.0 | 1.0 |
| Rixian | 1.0 | 1.0 |

(Ga'Taraan / Draconium / Gigacorp / Belters / Rixian are identity for both.)

## Verification

### Fail-first (recorded)

With the production wiring **temporarily neutralized** (the sweep reverted to
raw scanner ranges and the raw signature — the accessors left in place so the
tree still compiles), the three behavioural tests failed at the assertion
level on the pre-P476 behavior, then passed after restoring the GA wiring:

| Test | Pre-fix failure |
|---|---|
| `Ship_scanRange_ga_extends_the_sweep_detection_radius` | target at 2400 (boosted radius 2852) NOT seen — raw radius 1952 ignored the 1.5 scan-range GA |
| `Target_signature_ga_resistance_shrinks_the_sweep_detection_radius` | target at 1500 WAS seen — raw radius 1952 ignored the target side's 2.0 signature GA (attenuated 1052) |
| `Probe_scanRange_ga_extends_detection_in_the_sweep` | probe's boosted range ignored — target beyond the raw probe radius not seen |

(The four accessor/plumbing tests pass in both states by construction: the
accessors are exactly what the neutralized sweep bypassed, and the plumbing
test asserts identity/raw.)

### New tests

`tests/Allegiance.Core.Tests/P476SideGaVisibilityTests.cs` (8 tests):

- **plumbing** `ScanRange_and_Signature_indices_match_native_and_sideless_is_raw`:
  `ScanRange==11`/`Signature==12` match `igc.h`; sideless ship/station/probe
  read raw (identity).
- **accessor, per consumer** — `Ship_`/`Station_`/`Probe_EffectiveScannerRange_scales_by_side_ga`:
  each sets a synthetic `c_gaScanRange = 1.5` on the side and asserts the
  accessor returns `raw × 1.5` (data-derived from real hulls/probe/station types).
- **behavioural through the sweep**:
  - `Ship_scanRange_ga_extends_the_sweep_detection_radius` — faohshcar scanner
    (raw 3600) boosted to 1.5 → detection radius 1952→2852; a target at 2400
    flips unseen→seen.
  - `Target_signature_ga_resistance_shrinks_the_sweep_detection_radius` — target
    side's `c_gaSignature = 2.0` halves dreghpod's effective signature → radius
    1952→1052; a target at 1500 flips seen→unseen.
  - `Probe_scanRange_ga_extends_detection_in_the_sweep` — the probe is the only
    side-0 scanner (launcher-scout removed, the
    `ProbeClusterVisibilityTests` trick); its boosted range catches a target
    beyond its raw range.
- **end-to-end through a real development**
  `Real_sensor_dev_gas_accumulates_and_scales_effective_scan_range` — applies
  real dev 157 'GA: Ship Sensors 1' (`gas[11] = 1.1`) to a real side with the
  exact accumulation line the server runs on dev completion
  (`Server.Economy.cs:978-979`), asserts the side GA rose ×1.1 and a real
  faohshcar ship on that side's `EffectiveScannerRange()` scaled by it — the
  dev → side-GA → accessor chain end-to-end with real PCore001 data.

All tests use the P475/P467b recipe: the mission's side GAs are fully replaced
with identity-plus-factor so the civ base can't leak into the measurement.

### Existing visibility/cloak tests stay green

The full existing visibility/cloak/probe/mine/cluster group
(`CrossSideVisibilitySweepTests`, `CrossSideVisibilityFoundationTests`,
`CloakSimTests`, `CloakVisibilityGateLiveTests`, `MineVisibilitySweepTests`,
`ProbeClusterVisibilityTests`, `SideClusterVisibilityTests`,
`CrossSideExportLiveTests`) — 47 tests — all pass unchanged. (The first civ
Dreghklar's base `scanRange[11]=0.9` does lower the P132 sweep's real detection
radius 1952→1772, but those tests assert structural in/out-of-range distances,
so no expectation shifted.)

### Build & suite

- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings,
  0 errors**.
- Full suite (`/home/domingo/.dotnet/dotnet test`): 3 consecutive runs; no
  P476-attributable failure in any (details below, citing `docs/TEST_FLAKES.md`
  for the known flakes).

## Updated remaining-GA ledger (what is still unwired after P476)

| Index | Constant | Native use | Status |
|---|---|---|---|
| 0 | `c_gaMaxSpeed` | shipIGC.cpp:4529 | wired **P446** |
| 1 | `c_gaThrust` | shipIGC.cpp:4540 | wired **P446** |
| 2 | `c_gaTurnRate` | shipIGC.cpp:4533 | wired **P446** |
| 3 | `c_gaTurnTorque` | shipIGC.cpp:4538 | wired **P446** |
| 4 | `c_gaMaxArmorStation` | stationIGC.cpp:596 | wired **P475** |
| 5 | `c_gaArmorRegenerationStation` | stationIGC.cpp:617 | wired **P475** |
| 6 | `c_gaMaxShieldStation` | stationIGC.cpp:607 | wired **P475** |
| 7 | `c_gaShieldRegenerationStation` | stationIGC.cpp:622 | wired **P475** |
| 8 | `c_gaMaxArmorShip` | shipIGC.cpp:4572 | wired **P467b** |
| 9 | `c_gaMaxShieldShip` | shieldIGC.h:115 | wired **P467b** |
| 10 | `c_gaShieldRegenerationShip` | shieldIGC.h:109 | wired **P467b** |
| 11 | `c_gaScanRange` | shipIGC.cpp:4558, probeigc.h:165, stationIGC.cpp:591, clusterIGC.h:494 | **wired P476** (clusterIGC bSimpleEye roid site not ported) |
| 12 | `c_gaSignature` | clusterIGC.cpp:152, probeigc.h:177, stationIGC.h:322, shipIGC.h:357 | **wired P476** (clusterIGC bSimpleEye SigMod site not ported) |
| 13 | `c_gaMaxEnergy` | shipIGC.cpp:4563 | wired **P467b** |
| 14 | `c_gaSpeedAmmo` | weaponIGC.cpp:220 | wired **P467** |
| 15 | `c_gaLifespanEnergy` | weaponIGC.h:169 | wired **P467** |
| 16 | `c_gaTurnRateMissile` | missileigc.cpp:389 | wired **P467** |
| 17 | `c_gaMiningRate` | shipIGC.cpp:2212 | **still unwired** (P477) |
| 18 | `c_gaMiningYield` | shipIGC.cpp:1988 | **still unwired** (P477) |
| 19 | `c_gaMiningCapacity` | shipIGC.cpp:2166 | **still unwired** (P477) |
| 20 | `c_gaRipcordTime` | shipIGC.cpp:4665 | **still unwired** (P479) |
| 21 | `c_gaDamageGuns` | projectileIGC.cpp:132 | wired **P467** |
| 22 | `c_gaDamageMissiles` | missileigc.cpp:474 | wired **P467** |
| 23 | `c_gaDevelopmentCost` | bucketigc.cpp:72 | **still unwired** (P478) |
| 24 | `c_gaDevelopmentTime` | bucketigc.cpp:73 | **still unwired** (P478) |

After P476 the only unwired `c_ga…` consumers are **mining (17–19)**, **ripcord
time (20)** and **development cost/time (23–24)** — the remaining P477–P479
tasks. Everything else is wired.

## Out of scope (unchanged)

- GA indices other than `c_gaScanRange`/`c_gaSignature` (P477–P479 are the
  remaining ones; deliberately not pre-empted).
- How developments complete / GA accumulates (already correct, P189).
- The clusterIGC `bSimpleEye`/roid-eye and `CanSideSeeCluster` presence sites
  (absent / non-range-based in this port — see the "not ported" section above).

## Files changed

- `src/Allegiance.Core/Model/GlobalAttributeSet.cs` — 2 new index constants +
  ledger comment.
- `src/Allegiance.Core/Sim/Ship.cs` — `EffectiveScannerRange()` accessor.
- `src/Allegiance.Core/Sim/Station.cs` — `EffectiveScannerRange()` accessor.
- `src/Allegiance.Core/Sim/Probe.cs` — `SideGlobalAttributes()` resolver +
  `EffectiveScannerRange()` accessor.
- `src/Allegiance.Core/Model/Sector.cs` — sweep consumes effective scanner
  ranges; target-side `c_gaSignature` divisor folded into participant signatures.
- `tests/Allegiance.Core.Tests/P476SideGaVisibilityTests.cs` — 8 new tests.
