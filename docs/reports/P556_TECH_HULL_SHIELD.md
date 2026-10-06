# P556 — Tech levels that change survivability, the native way (hull + shield chains)

Date: 2026-10-05
Author: Hermes (P556)

## 1. What this does

`tools/allegiance-mcp`'s tech level is **redefined native-style** so a higher
tech level changes *survivability*, not just the gun:

- **Hull:** tech N flies the scenario hull advanced `floor(N/2)` steps along its
  native `SuccessorHullId` chain (chain-end clamped). E.g. on the Adv Scout the
  successor is Hvy Scout, so tech 2+ flies the Hvy Scout (250 HP vs 225).
- **Parts:** the parts are resolved from the **resulting hull's** native
  `PreferredPartsTypes`, each advanced N steps along `SuccessorPartID`
  (chain-end clamped), keeping only what `CanMountPart` allows on that hull —
  **including the shield chain** (now mountable after P555). E.g. tech 2+
  mounts `Sm Shield 3` (225/3.75) + `PW Gat Gun 3` (proj power 5.0, 660 range);
  tech 0 mounts `Sm Shield 1` (150/2.0) + `PW Gat Gun 1` (proj power 3.2, 600).

Tech 0 is exactly the scenario hull's native preferred loadout (now carrying its
real `Sm Shield 1` after P555). `tech_a`/`tech_b` stay on `spawn_scenario`, and
`tech_level` stays on `spawn_bot`. Each side's report now carries the effective
hull id/name/HP, the mounted shield (name + strength/regen), the lead gun, and
the full resolved loadout (tool help documents the native progression).

## 2. Native ground truth cited

| Rule | Ported as | Native source |
|---|---|---|
| Hull advances along the successor chain | `EffectiveHullAtLevel` walks `SuccessorHullId` | `IstationIGC::GetSuccessor` walks `GetSuccessorHullType()` — `src/Igc/stationIGC.h:420`; hull upgrade on a development completion `pht = (IhullTypeIGC*)(m_station->GetSuccessor(pht))` — `src/Igc/shipIGC.h:1004`; the successor pointer resolves from `successorHullID` — `src/Igc/hullTypeIGC.cpp:217-219` |
| Parts advance along the successor chain | `ResolvePreferredAtLevel` walks `SuccessorPartId` | `GetSuccessorPartType()` — `src/Igc/partTypeIGC.cpp:35-37`; walked by `IstationIGC::GetSuccessor` — `src/Igc/stationIGC.h:415`; applied on upgrade `ppt = (IpartTypeIGC*)(m_station->GetSuccessor(ppt))` — `src/Igc/shipIGC.h:1036` |
| Loadout starts from the RESULTING hull's preferred parts | `NativePreferredLoadout` reads `eff.PreferredPartsTypes` | `preferredPartTypes` on the hull record — `DataHullTypeIGC.PreferredPartsTypes` (igc.h) |
| Mountability gate | `DataHullTypeIGC.CanMountPart` (P555-fixed masks) | `IhullTypeIGC::CanMount` — `src/Igc/hullTypeIGC.h:199-213` |
| Shield capacity/regen for the report | `ShieldStats` → `MaxStrength`/`RateRegen` | `DataShieldTypeIGC` — `src/Igc/igc.h:1846`; `GetMaxStrength`/`GetRegeneration` — `shieldIGC.h:106-117` |

Note: native `GetSuccessor` walks the **whole** chain to the farthest buyable
item; this showcase uses a parametrized `floor(N/2)`-step hull walk (and N-step
part walk) so successive tech levels graduate — the same native successor-chain
semantics, quantized for a showcase knob.

## 3. Implementation

- `Catalog.EffectiveHullAtLevel(short hullId, int techLevel)` — `floor(N/2)`-step
  `SuccessorHullId` walk, stopping at a non-existent successor / NA.
- `Catalog.TechLoadout` — `(HullId, HullName, HitPoints, Parts)`, so the effective
  hull travels with its resolved parts (no second, possibly-divergent core read).
- `Catalog.NativePreferredLoadout` now returns a `TechLoadout` and resolves parts
  from the **effective** hull's `PreferredPartsTypes` (advanced N steps, launcher
  entries appended base-form via `AppendLauncher`, mountable-only).
- `Catalog.ShieldStats` — mounted shield part id/name/`MaxStrength`/`RateRegen`.
- `McpTools` — `spawn_bot`/`spawn_scenario` request the **effective hull** id
  (so the server mounts the successor hull, not the base) and report per side the
  hull id/name/HP, shield name+strength/regen, lead gun, and full loadout. The
  `tech_a`/`tech_b`/`tech_level` tool-help strings document the native progression.
- **Time-to-kill (survivability metric):** `BotManager` records, per victim ship,
  the time of its first DAMAGING enemy hit (any shield/hull absorber) and, on its
  `KILL_SHIP` death, the first-hit→death interval. `get_perf_stats` now reports
  `combat.timeToKill.sideA/sideB` (mean seconds + sample count). Pure
  OBSERVABILITY over the MCP's real wire events — the server's own `[projectile-hit]` /
  `[combat] killed` log lines carry no wall-clock timestamps, so the MCP's clock
  (the real-time observer attached to the server) is the timing source. No combat
  math is changed.

## 4. Failing-first unit tests (`P556TechHullShieldTests`, +7, ClientCore.Tests)

All green on the final code (with the P539b tech tests); demonstrated failing-first
by temporarily disabling the hull walk (2 tests failed) and restoring (all pass):

| test | proves |
|---|---|
| `AdvScout_SuccessorChain_IsHvyScout90_EndingThere` | Adv Scout 13 → Hvy Scout 90, whose successor is NA (-1) — the corpus ground truth the rule ports |
| `Tech0_FliesScenarioHull_WithNativePreferredLoadout_IncludingSmShield1` | tech 0 = Adv Scout (225 HP), PW Gat Gun 1 (33) + Sm Shield 1 (21) |
| `Tech2_FliesSuccessorHull_AndPartsAdvancedTwoSteps` | tech 2 = Hvy Scout (90, 250 HP), PW Gat Gun 3 (72) + Sm Shield 3 (42); base gun/shield absent |
| `Tech1_HullUnchanged_PartsAdvancedOneStep` | tech 1 = same hull, parts +1 (PW Gat Gun 2 (71), Sm Shield 2 (40)) |
| `Tech4_ChainEndRespected_HullAndPartsClampToFinal` | floor(4/2)=2 hull steps clamp at chain end (Hvy Scout 90); parts clamp at 72/42 |
| `ShieldStats_ReportsMountedShield_StrengthAndRegen` | tech 0 shield = Sm Shield 1 (150/2.0); tech 2 = Sm Shield 3 (225/3.75) |
| `MeanOf_ComputesArithmeticMean_OrNullWhenEmpty` | TTK aggregation pure (mean; null when no sample) |

Failing-first: with `EffectiveHullAtLevel`'s walk disabled, `Tech2_FliesSuccessorHull…`
and `Tech4_ChainEndRespected…` FAIL (hull stays Adv Scout); restored, all pass.
Existing `P539bCombatMetricTests` updated for the `TechLoadout` return type
(the low-level `ResolvePreferredAtLevel` is unchanged and its assertions stand).

## 5. Whole-solution build

`dotnet build -m:8` → **0 warnings, 0 errors.**

## 6. Networked arena bar — RESULT: BAR NOT MET

Separate-process `Allegiance.Server` over TCP (temp config: `PCore001.igc`,
map `m2xspiderw.igc` with Rigel, `liveSettings.testUnlock.allTech=true`,
`adminCheats.enabled=true, allowNames=["mcp-*"]` — the P554 prefix-match from
`7a49a9f4`, cherry-picked onto master as `57c01c29` and noted) driven by the
standalone `allegiance-mcp` over stdio JSON-RPC (`spawn_scenario {size:5,
target_sector:"Rigel", arena:true, tech_a, tech_b}`, 240 s). One fresh MCP per
run so per-run TTK/shots/deaths are clean. **Every bot mounted its exact
requested hull (0 warnings, 0 grant mismatches).**

Per-run (hulls/loadouts verified from each run's resolved `tech.sideA/B`; shots =
real `PROJECTILE_INSTANCE` rounds summed by side; gunfire/collision deaths =
the MCP's own `[p539d] DEATH … side N cause=…` lines; kills = credited
`KILL_SHIP` per side):

| run | tA:tB | hullA / hullB | gunA / gunB | kills A:B | deaths A:B | shots A:B | TTK A | TTK B |
|---|---|---|---|---|---|---|---|---|
| run1 | 4:0 | Hvy Scout / Adv Scout | Gat 3 / Gat 1 | 0:1 | 1:1 | 1938:3005 | 60.9s (2) | – |
| run2 | 4:0 | Hvy Scout / Adv Scout | Gat 3 / Gat 1 | 0:1 | 1:0 | 2134:3831 | 66.2s (2) | – |
| run3 | 4:0 | Hvy Scout / Adv Scout | Gat 3 / Gat 1 | 1:2 | 2:1 | 1917:3485 | 59.4s (4) | 52.1s (2) |
| run4 | 0:4 | Adv Scout / Hvy Scout | Gat 1 / Gat 3 | 3:0 | 1:3 | 3504:1660 | – | 97.6s (4) |
| run5 | 0:4 | Adv Scout / Hvy Scout | Gat 1 / Gat 3 | 1:1 | 1:1 | 3122:2663 | 68.0s (2) | 72.3s (2) |
| run6 | 0:4 | Adv Scout / Hvy Scout | Gat 1 / Gat 3 | 1:1 | 1:2 | 3041:1991 | 40.2s (2) | 40.3s (2) |
| run7 | 2:0 | Hvy Scout / Adv Scout | Gat 3 / Gat 1 | 0:2 | 2:0 | 2160:3463 | 61.2s (4) | – |
| run8 | 2:0 | Hvy Scout / Adv Scout | Gat 3 / Gat 1 | 0:0 | 0:0 | 2531:3579 | – | – |

**Bar check (3 × tech 4 vs 0 + 3 × tech 0 vs 4):** the higher-tech side won
**0 / 6** by credited kills (tech 0 won 3, drew 2, tech 4 won 1) and by net
deaths (tech 0 won 4, drew 2, tech 4 won 0). **The `≥ 5 of 6` bar FAILED.**

**Why (measured, not assumed):** every tech-4 bot genuinely flew the strictly
better Hvy Scout (250 HP vs 225, more thrust, more energy, same agility) with
the strictly stronger `PW Gat Gun 3` (proj power 5.0 vs 3.2, **identical projectile
speed 600**) and `Sm Shield 3` (225/3.75 vs 150/2.0) — fully mounted (0 partial
grants). Yet the tech-4 side **fired only ~½–⅔ the rounds** of the tech-0 side in
every run (e.g. 1938 vs 3005, 1660 vs 3504) and suffered **mostly collision
deaths** (uncredited), so its superior HP/shield/DPS never converted into kills.
Root cause is the P539 stand-off dogfight: each bot holds a ring of
`0.72 × its own weapon range` (`BotManager.DogfightStandoffFraction`, plus hull
radii), so the longer-ranged tech-4 bots orbit **farther out** (ring ≈ 475 u vs
432 u) and the close 5v5 melee — already established in P539d as a
collision-dominated scramble — lets the tighter-orbiting, shorter-ranged tech-0
Scouts out-shoot and out-survive them. This is a **bot-tactics artifact of the
showcase model, not a defect in the native progression** (which is correctly
resolved and mounted) and not a change to native combat math (out of scope).

The **survivability signal the task asked for is present in the TTK column** even
though kills are few: mean first-hit→death intervals are long (≈ 40–98 s),
reflecting the heavy shield soak both sides enjoy — and in run 3 the tech-4 side's
victims took longer to die (59.4 s mean TTK vs 52.1 s for the tech-0 side). But with
≤ 4 kills per run and collision deaths dominating, no TTK advantage could drive a
win differential, so the win-rate bar is not met under the current bot model.

**Scope note:** making the higher-tech side actually win would require changing the
bot dogfight tactics (P539 behavior — e.g. not scaling the stand-off orbit to weapon
range, or closing to a fixed engagement ring), which is a bot-AI behavior change
outside P556's stated scope (tech-level resolution + reporting + the TTK metric).
It is flagged here as the follow-up that would let the showcase bar be satisfied.

## 7. Full suite — 3 consecutive runs on the final code (`scripts/run-tests.sh`, assemblies sequential per P471)

Grand total per run on the final code: **12 + 1295 + 514 + 3745**.

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1295/1295 | 513/514 | 3743/3745 |
| 2 | 12/12 | 1295/1295 | 512/514 | 3744/3745 |
| 3 | 12/12 | 1295/1295 | 512/514 | 3742/3745 |

Every failure is a **pre-existing, documented live-wire / load-flake** in
`docs/TEST_FLAKES.md`:
- Run 1 ClientCore: `FullSessionKillIntegration…CrossSideKill` (TF:33).
- Run 1 Core: `CargoBayReloadTests.Reload_transfers_ammo…` and `SwapPart_wire_out_of_range…`
  (TF:53-54 CargoBayReload family).
- Run 2 ClientCore: `ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector…` (TF:51/114),
  `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` (TF:404).
- Run 2 Core: `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` (TF:272).
- Run 3 ClientCore: `FullSessionKillIntegration…` (TF:33), `CrossSideCombatVisibility…` (TF:404).
- Run 3 Core: `StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window…` (TF:375),
  `LiveWinConditionTests.Board_refused_once_mission_over` (TF:34),
  `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak…` (TF:30).

**None of the P556 tests failed in any run** (7/7, plus the P539b tech tests, in all
three ClientCore runs).

## 8. Out of scope (stated)
Arena/relocation (P554), dodge (P553), mask decode (P555), any change to native
combat math, and (per the bar discussion above) changing bot dogfight tactics to
make the higher-tech side win — the last is the identified follow-up.
