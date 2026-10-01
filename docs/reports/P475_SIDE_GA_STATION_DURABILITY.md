# P475 — side Global Attributes scale station armor, shields and regeneration

Date: 2026-10-01
Branch: master (committed, not pushed)
Repo: `/home/domingo/Development/AllegianceCore`

## Summary

Gameplay parity gap: the port computed and broadcast the station-durability
global attributes but never applied them. Native wraps every station durability
value in the owning side's `GlobalAttributes[...]` through its `MyStationType`
wrapper (`stationIGC.cpp:596,607,617,622`); the port's `Station` runtime
(`src/Allegiance.Core/Sim/Station.cs`) read the raw `DataStationTypeIGC`
capacities and regen rates.

P475 adds four named index constants to `GlobalAttributeSet` and four GA-aware
effective getters on `Station`, and routes every durability consumer through
them. It reuses the exact pattern P446 established (a `SideGlobalAttributes()`
resolution + `Effective…()` accessors that multiply the raw value by the owning
side's GA), giving `Station` a `Mission` back-ref (set in `Mission.Assemble` for
production stations and in `Mission.PlaceStation` for the synthetic/test seam)
so the side can be resolved by the live `SideId`.

## Change (server)

### `GlobalAttributeSet` — four named index constants

Added, citing `src/Igc/igc.h:591-595`:

- `MaxArmorStation = 4` (`c_gaMaxArmorStation`)
- `ArmorRegenerationStation = 5` (`c_gaArmorRegenerationStation`)
- `MaxShieldStation = 6` (`c_gaMaxShieldStation`)
- `ShieldRegenerationStation = 7` (`c_gaShieldRegenerationStation`)

### `Station` — Mission back-ref, side resolution and effective getters

- `Mission? Mission { get; set; }` — the station's mission (the analogue of
  `Ship.Mission`, `Ship.cs:645`). Set for every production runtime station in
  `Mission.Assemble` (parallel to the side back-ref there) and in
  `Mission.PlaceStation` (parallel to `PlaceShip` setting `ship.Mission`).
- `private GlobalAttributeSet SideGlobalAttributes()` — the port of native
  `m_pstation->GetSide()->GetGlobalAttributeSet()`
  (`stationIGC.cpp:596,607,617,622`); resolves `SideId` against `Mission.Sides`,
  returning `GlobalAttributeSet.Identity` when the mission is unresolved. Mirrors
  P446's `Ship.SideGlobalAttributes` exactly.
- Four effective getters (each a raw value × the side's factor):

| Attribute (index) | Native getter | Port effective getter |
|---|---|---|
| `c_gaMaxArmorStation` (4) | `MyStationType::GetMaxArmorHitPoints` (`stationIGC.cpp:594-597`) | `Station.EffectiveMaxArmor()` |
| `c_gaMaxShieldStation` (6) | `MyStationType::GetMaxShieldHitPoints` (`stationIGC.cpp:603-610`) | `Station.EffectiveMaxShield()` |
| `c_gaArmorRegenerationStation` (5) | `MyStationType::GetArmorRegeneration` (`stationIGC.cpp:614-618`) | `Station.EffectiveArmorRegen()` |
| `c_gaShieldRegenerationStation` (7) | `MyStationType::GetShieldRegeneration` (`stationIGC.cpp:619-623`) | `Station.EffectiveShieldRegen()` |

### Site map: native → port

| Consumer | Native | Port (Station.cs) |
|---|---|---|
| Armor damage/repair denominator | `stationIGC.cpp:137` `maxArmor = GetMaxArmorHitPoints()` | `:351` `float maxArmor = EffectiveMaxArmor()` |
| Shield-absorption divisor | `stationIGC.cpp:171` `maxShield = GetMaxShieldHitPoints()` | `:383` `float maxShield = EffectiveMaxShield()` |
| Armor regen fraction gain | `stationIGC.h:197` `dt·GetArmorRegeneration()/GetMaxArmorHitPoints()` | `:497` `dt·EffectiveArmorRegen()/EffectiveMaxArmor()` |
| Shield regen fraction gain | `stationIGC.h:207` `dt·GetShieldRegeneration()/GetMaxShieldHitPoints()` | `:503` `dt·EffectiveShieldRegen()/EffectiveMaxShield()` |
| Capture heroic-engineer shield | `fsship.cpp:592` `GetShieldRegeneration()/GetMaxShieldHitPoints()` (GA wrapper, OLD owner) | `:463` `EffectiveShieldRegen()/EffectiveMaxShield()` (old owner — see below) |

### Fractions stay the stored state — no client change (scope item 2)

Native stations store **fractions** (`m_hullFraction`, `m_shieldFraction`;
`stationIGC.h:300-372`) and the damage/regen math always works in fraction
units. The GA multipliers wrap only the absolute maxima/rates, so a GA change
rescales capacity **without touching the fraction** — the same invariant P467B
established for ships.

The wire's `STATIONS_UPDATE(57)` carries exactly these stored fractions:
`FromStations` (`Wire/StationsUpdate.cs:127-131`) bakes
`st.ArmorFraction`/`st.ShieldFraction` into the div-240 byte percentages. Nothing
absolute crosses the wire, so the HUD/target readout tracks the stored fraction
and never jumps against the scaled max. Confirmed by the
`Stations_update_fraction_tracks_stored_fraction_under_ga` test and by re-reading
native: the sent fraction is the stored fraction (`fsship.cpp:820-845`). **No
client change is needed** (the client already has the side's GA from P446's
`SIDE_ATTRIBUTE_CHANGE(76)` handling, but the wire is fraction-only anyway).

### Capture switches the owning side's GA (scope item 3)

Native evidence (`FedSrv/fsship.cpp:577-641`):

- The heroic-engineer shield boost is computed from
  `GetStationType()->GetShieldRegeneration() / GetMaxShieldHitPoints()`
  (`fsship.cpp:592`) — where `GetStationType()` returns the **GA wrapper** —
  and this happens **BEFORE** `pstation->SetSide(pside)` (`fsship.cpp:634`). So
  the boost uses the **old owner's** GA for both numerator and denominator.
- Immediately after `SetSide`, every `MyStationType` getter reads
  `m_pstation->GetSide()` = the **new** owner, so a captured station's max and
  regen switch to the new owner's GA from that instant.

The port reproduces both exactly: `Capture` computes the boost from
`EffectiveShieldRegen()/EffectiveMaxShield()` **while `SideId` is still the old
value** (the re-side is the last statement), and because
`SideGlobalAttributes()` resolves the *live* `SideId` on every call, the moment
`Capture` assigns the new owner the station's effective values switch to the new
owner's GA — matching native's `SetSide` semantics.

## Developments and civs in PCore001 that change these attributes

Scanned `PCore001.igc` (11 civs, 305 developments). The four indices (4-7) are
touched by four real (non-tech-only) developments:

| Dev id | Name | gas[4] maxArmor | gas[5] armorRegen | gas[6] maxShield | gas[7] shieldRegen |
|---|---|---|---|---|---|
| 167 | GA: Station Hull 1 | **1.1** | **1.1** | 1.0 | 1.0 |
| 168 | GA: Station Hull 2 | **1.1** | **1.1** | 1.0 | 1.0 |
| 169 | GA: Station Shield 1 | 1.0 | 1.0 | **1.1** | **1.1** |
| 170 | GA: Station Shield 2 | 1.0 | 1.0 | **1.1** | **1.1** |

Civs with a non-identity base in these four (`GasBaseAttributes`):

| Civ | GA[4] maxArmor | GA[5] armorRegen | GA[6] maxShield | GA[7] shieldRegen |
|---|---|---|---|---|
| Technoflux | 1.1 | 1.0 | 1.1 | 1.0 |
| Ga'Taraan | 1.1 | 1.0 | 1.1 | 1.0 |
| Phoenix | 0.9 | 1.0 | 1.25 | 1.0 |
| Omicron | 1.0 | 1.0 | 0.95 | 0.9 |
| Draconium | 1.2 | 1.05 | 0.95 | 0.95 |
| Iron Coalition | 1.15 | 1.0 | 1.15 | 1.0 |
| Bios | 0.9 | 1.25 | 0.9 | 1.25 |

(Dreghklar, Gigacorp, Belters, Rixian are identity for 4-7.)

## Verification

### Fail-first (recorded)

With the production wiring temporarily neutralized (the four getters forced to
return raw so the code still compiles), the new tests ran and failed on the
pre-P475 behavior — then passed after restoring the GA wiring:

| Test | Pre-fix failure |
|---|---|
| `Effective_getters_scale_by_side_ga` | raw values unchanged (multipliers ignored) |
| `MaxArmorStation_scales_the_overflow_damage_denominator` | ratio 1 (GA2 == identity drop 0.25), wanted ~0.5 |
| `MaxShieldStation_raises_shield_absorption_capacity` | ratio 1 (drop 0.5 both), wanted ~0.5 |
| `ArmorRegenerationStation_speeds_armor_regeneration` | ratio 1 (gain 0.1 both), wanted ~2 |
| `ShieldRegenerationStation_speeds_shield_regeneration` | ratio 1 (gain 0.1 both), wanted ~2 |
| `Capture_heroic_shield_uses_old_owners_ga` | boost 0.7 (raw ratio), wanted 0.35 |
| `Capture_switches_max_and_regen_to_new_owners_ga` | EffectiveMaxArmor stayed raw ×1.5, wanted switch to ×3 |
| `Stations_update_fraction_tracks_stored_fraction_under_ga` | stored fraction stayed 0.875 (raw max), wanted 0.75 |
| `P475E2eStationGaLiveTests.Completing_station_hull_dev_scales_real_station_armor` | EffectiveMaxArmor stayed raw 2500 after dev, wanted 2750 |

8 of the 9 domain tests failed on the neutralized tree (the plumbing test
`Station_indices_match_native_and_no_mission_is_raw` passes by construction —
it asserts raw identity, which holds either way), plus the E2E failed; all pass
on the wired tree.

### End-to-end through a real development

Completed **real dev 167 'GA: Station Hull 1'** (`gas[4] = 1.1` =
`c_gaMaxArmorStation`, `gas[5] = 1.1` = `c_gaArmorRegenerationStation`) through
the real live funding/completion pipeline (`BucketDonate → bucket completes →
CompleteDevelopment applies GlobalAttributeSet.Apply` → side 0's
`GlobalAttributes[4]`/`[5]` become 1.1× the civ base — Dreghklar, identity). A
real station on that side then read the scaled maxima:

```
P475-E2E: pre-dev EffectiveMaxArmor=2500.0 (raw 2500); GA[4]=1.000
P475-E2E: post-dev side GA[4]=1.100 GA[5]=1.100
P475-E2E: post-dev EffectiveMaxArmor=2750.00 ratio=1.1000 (want ~1.1)
```

### Existing tests that legitimately shifted (updated)

- `SideEliminationLiveTests.Capturing_last_restart_station_eliminates_old_side`:
  side B is civ 3 Ga'Taraan (`MaxShieldStation` base 1.1), so the target
  station's effective shield became 110 HP instead of raw 100 — draining exactly
  100 left a 0.0909 residual. Updated the drain to
  `bas.EffectiveMaxShield()` (depletes to 0 regardless of the owner's GA),
  preserving the test's "shields down before capture" intent. This is the same
  class of legitimate shift P467B documented for ships.

### Pre-existing live-wire flakes (reproduced; NOT P475-attributable)

Across the three full-suite runs the only failures were intermittent live-wire
timing flakes — every one passed in isolation, and the failing test varied run
to run with a stable pass count (Core.Tests 3635/3635/3633 of 3637), which is
the machine-state timing signature the live-network harness is prone to (see
the skill's note on back-to-back live-network runs amplifying socket/ephemeral-
port flakiness). None touches the station-GA durability path:

`ShipDeleteGoneLiveTests.{CrossSide_WarperLeavesSector, DockOut_WatcherLatchesGone}`
(the latter a documented flake, P471), `FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill`,
`LoadoutChangeLiveTests.Boarding_and_leaving_broadcast_passenger_change_on_parent_but_not_enemy`,
`LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`,
`StationUnderAttackAlertLiveTests.{Defending_side_gets_under_attack_alert..., Sub_30_percent_fraction...}`,
`AddPartTests.Part_pickup_accept_into_empty_mount_mounts_fresh_part`,
`AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`,
`WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer...`,
`CargoBayReloadTests.SwapPart_wire_out_of_range_negative_mount_is_rejected...`,
`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`,
`ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`,
`SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone`
(a lethal-missile kill timing test — no fixed shield-drain — passed 2 of 3
runs and in isolation).

Two of these deserve explicit notes: `StationUnderAttackAlertLiveTests` targets a
**shield-less** station (`MaxShieldHitPoints = 0f`), so `EffectiveMaxShield() =
0` and the station-GA wiring is provably inert for it — its intermittent miss is
pure alert/chat-receipt timing. And `SideEliminationLiveTests.Eliminating_side2`
fails only on missile-kill timing, unrelated to the shield-drain change made to
the sibling `Capturing_last_restart...` test. Each of the above was confirmed
green in isolation on the P475 tree.

### Build / test

- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive full-suite runs; no
  P475-attributable failure in any (only the intermittent live-wire flakes
  listed above, each green in isolation).

## Out of scope (unchanged, per brief)

The other still-unwired GA indices are separate follow-ups (P475-P479 family):
visibility `ScanRange` (11) / `Signature` (12), mining `MiningRate` (17) /
`MiningYield` (18) / `MiningCapacity` (19), ripcord `RipcordTime` (20), economy
`DevelopmentCost` (23) / `DevelopmentTime` (24), plus the probe projectile-speed
consumer (`probeigc.cpp:367`). How developments complete / GA accumulates is
already correct (P189).

## Updated remaining-GA ledger (after P475)

Wired: 0-3 flight (P446), 4-7 **stations (P475)**, 8-10 + 13 ship durability
(P467B), 14-16 weapon output (P467), 21-22 damage (P467).

Still unwired (raw): 11 `ScanRange`, 12 `Signature` (visibility); 17
`MiningRate`, 18 `MiningYield`, 19 `MiningCapacity` (mining); 20 `RipcordTime`
(ripcord); 23 `DevelopmentCost`, 24 `DevelopmentTime` (economy); plus the probe
projectile-speed consumer.

## Files changed

- `src/Allegiance.Core/Model/GlobalAttributeSet.cs` — 4 new station indices.
- `src/Allegiance.Core/Sim/Station.cs` — Mission back-ref, `SideGlobalAttributes`,
  4 effective getters + routing (ReceiveDamage, Regenerate, Capture) + docs.
- `src/Allegiance.Core/Model/Mission.cs` — station Mission wiring in Assemble +
  PlaceStation.
- `tests/Allegiance.Core.Tests/SideEliminationLiveTests.cs` — GA-scaled shield
  drain (legit shift).
- `tests/Allegiance.Core.Tests/P475SideGaStationDurabilityTests.cs` — 9 new
  domain tests.
- `tests/Allegiance.Core.Tests/P475E2eStationGaLiveTests.cs` — 1 live E2E
  (real dev 167).
- `docs/reports/P475_SIDE_GA_STATION_DURABILITY.md` — this report.
