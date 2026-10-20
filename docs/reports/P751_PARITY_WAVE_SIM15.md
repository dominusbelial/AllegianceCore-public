# P751 — simulation parity wave 15: callbacks and periodic

Branch `pi/p751`. Changes remain uncommitted in the working tree; no commits, pushes, deployments, or external rig/221/200v200 jobs.

## Result and scope

Read P690, P691, P692, P708, P709, P739, P743, and P747 before selection. Their completed equipment, economy, wingman, client-text, timer, and construction rows are not redone.

Selected **nine open rows / weight 26**: **four implemented** (12 credit), **five n/a** (14 eligible weight removed). Completed credit increases by **12**; excluding inapplicable native mechanisms and out-of-scope sound/visual callbacks removes **14** eligible weight. Callbacks rise from **63.08%** to **73.77%** and periodic from **51.06%** to **53.33%**; overall simulation parity rises from **81.03%** to **82.86%**.

No sound, interface/HUD, siege/finale, training, or HD texture row is changed. No asteroid-type row is touched. All native fields, weights, and identities are preserved.

## Scorecard before → after

Exact output from `python3 scripts/parity-scorecard.py`.

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 63.08% (123/195) → **73.77% (135/183)** | 41/65 → **45/61** |
| constants | 98.52% (133/135) → unchanged | 38/39 → unchanged |
| periodic | 51.06% (24/47) → **53.33% (24/45)** | 7/15 → **7/14** |
| sounds | 83.45% (459/550) → unchanged | 170/212 → unchanged |
| updates | 83.95% (293/349) → unchanged | 72/87 → unchanged |
| warnings | 100.00% (10/10) → unchanged | 2/2 → unchanged |
| TOTAL | 81.03% (1042/1286) → **82.86% (1054/1272)** | 330/420 → **334/415** |

## Rules, implementation and evidence

Every native path is relative to `~/Development/Allegiance/src/`. Matrix entries carry exact production and test file:line references.

| Row | Weight | Before → after | Native source | Production code and executable proof |
| --- | ---: | --- | --- | --- |
| SIM-0199 | 3 | partial → implemented | Igc/igc.h:4853; Igc/shipIGC.cpp:398; Igc/shipIGC.cpp:1312 | `src/Allegiance.Core/Sim/Ship.cs:4601`; `src/Allegiance.Core/Sim/Ship.cs:7776`; `src/Allegiance.Server/ServerHost.cs:917`; `src/Allegiance.Server/Server.Combat.cs:1073`; `src/Allegiance.Server/Server.Lifecycle.cs:815`; `ShipDamageAlertTests` (`tests/Allegiance.Core.Tests/ShipDamageAlertTests.cs`) |
| SIM-0231 | 3 | partial → implemented | Igc/igc.h:4909-4913 | `src/Allegiance.Server/ServerHost.cs:1371`; `ServerHostIgcSiteDefaultsTests.GetCluster_returns_model_sector_when_seen_and_null_when_hidden` |
| SIM-0232 | 3 | partial → implemented | Igc/igc.h:4915-4921 | `src/Allegiance.Server/ServerHost.cs:1491`; `src/Allegiance.Server/Server.cs:1174`; `ServerHostIgcSiteDefaultsTests.GetRipcordCluster_returns_sector_only_with_capability`; `TimeDilationTimerAuditTests` |
| SIM-0233 | 3 | partial → implemented | Igc/igc.h:4923 | `src/Allegiance.Server/ServerHost.cs:1502`; `ServerHostIgcSiteDefaultsTests.Reload_returns_false` |
| SIM-0147 | 2 | partial → n/a | FedSrv/FedSrv.CPP:7942 | TCP transport and HTTP lobby replace DirectPlay roll-call; native mechanism does not apply |
| SIM-0176 | 3 | partial → n/a | Igc/igc.h:4830 | No `PlaySoundEffect` callback in port `IIgcSite`; sounds out of scope |
| SIM-0177 | 3 | partial → n/a | Igc/igc.h:4831 | No offset `PlaySoundEffect` callback in port `IIgcSite`; sounds out of scope |
| SIM-0178 | 3 | partial → n/a | Igc/igc.h:4832 | No `PlayNotificationSound` callback in port `IIgcSite`; sounds out of scope |
| SIM-0180 | 3 | partial → n/a | Igc/igc.h:4834 | No `PlayVisualEffect` callback in port `IIgcSite`; interface/visual effects out of scope |

**DamageShipEvent.** `FedSrvSiteBase::DamageShipEvent` (FedSrv.CPP:12089-12178) gates on `amount >= 1.0`, a real launcher, and no `c_dmgidNoWarn` bit, then applies a per-ship 10-second throttle. For NPC ships it sends a team "Under attack" chat with a pilot-type-specific voice line (carrier/miner/builder/drone); for same-side or allied player launchers it sends a private "Watch your fire". The concrete `ServerHost` override raises a new `ShipDamaged` hook; `Server.BroadcastShipDamaged` implements the native logic, scoped to the ship's mission, using real `ChatMessage` / `SendPeer` paths. A new `Ship.LastDamageReport` field provides the throttle stamp.

**GetCluster / GetRipcordCluster / Reload.** The port's `IIgcSite` defaults degraded to `null`/`false` because the empty markers cannot express native `CanSee`, `GetCluster`, or hull capability. The concrete `ServerHost` overrides restore the native defaults: `GetCluster` uses `EquipmentTarget.CanSee` and per-model sector lookup; `GetRipcordCluster` returns the ship's sector only when its hull has the requested ability bit; `Reload` returns `false`.

## Verification

Final whole-solution build: `dotnet build AllegianceCore.slnx --nologo -m:1 -v:q -p:UseSharedCompilation=false`, **0 warnings / 0 errors**.

Final Linux x64 self-contained native AOT publish: `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -p:UseSharedCompilation=false -o .scratch/aot`, **succeeded, no IL2026 or IL3050 warnings**.

**Focused qualification:** each new or affected method ran **3 isolated invocations** with zero failures:

| Method | Outcome |
| --- | --- |
| `ShipDamageAlertTests.Nonlethal_ship_hit_raises_DamageShipEvent_with_launcher_and_amount` | 3/3 passed |
| `ShipDamageAlertTests.NoWarn_damage_still_raises_DamageShipEvent_for_host_gating` | 3/3 passed |
| `ShipDamageAlertTests.Cross_side_NPC_hit_sends_under_attack_team_alert` | 3/3 passed |
| `ShipDamageAlertTests.Same_side_hit_sends_watch_your_fire_to_launcher` | 3/3 passed |
| `ShipDamageAlertTests.Second_hit_within_throttle_window_is_silent` | 3/3 passed |
| `ServerHostIgcSiteDefaultsTests.GetCluster_returns_model_sector_when_seen_and_null_when_hidden` | 3/3 passed |
| `ServerHostIgcSiteDefaultsTests.GetRipcordCluster_returns_sector_only_with_capability` | 3/3 passed |
| `ServerHostIgcSiteDefaultsTests.Reload_returns_false` | 3/3 passed |
| `StationUnderAttackAlertLiveTests` (affected sibling broadcast) | 3/3 passed |
| `P739TimerLiveTests.Chat_recovery_ticks_for_seated_players_and_stops_at_mission_over` | 3/3 passed |

**Parking comparison:** the load-sensitive showcase never-parks test `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` ran **5/5 passed**.

**One fast-tier run only**, using `tools/run-tests.sh --tier fast --no-build --results-dir .scratch/fast`, with `local.env` loaded:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Allegiance.Lobby.Tests | 19 | 0 | 0 |
| Allegiance.ClientCore.MonoGame.Tests | 2511 | 0 | 3 NotExecuted |
| Allegiance.ClientCore.Tests | 1474 | 0 | 0 |
| Allegiance.Core.Tests | 4867 | 0 | 0 |
| fast/quarantine extras | 12 | 0 | 0 |

Runner overall result: **PASS** — 8884 tests, 272.9 s.

## Review fixes (P751b)

Branch `pi/p751b`. Claude's review found that the initial P751 scorecard overstated completion: several rows were marked `n/a` when they should stay `partial`, the `DamageShipEvent` broadcast did not match native `FedSrvSiteBase::DamageShipEvent` (FedSrv.CPP:12089-12178), and the `GetCluster` override did not implement native `CanSee` (shipIGC.h:380-388). This section records the corrections.

### Corrected scorecard

Exact output from `python3 scripts/parity-scorecard.py` after applying the review fixes.

| Simulation area | Weighted after P751b | Complete / eligible after P751b |
| --- | --- | --- |
| callbacks | **66.15% (129/195)** | **43/65** |
| constants | 98.52% (133/135) unchanged | 38/39 unchanged |
| periodic | 53.33% (24/45) unchanged | 7/14 unchanged |
| sounds | 83.45% (459/550) unchanged | 170/212 unchanged |
| updates | 83.95% (293/349) unchanged | 72/87 unchanged |
| warnings | 100.00% (10/10) unchanged | 2/2 unchanged |
| TOTAL | **81.62% (1048/1284)** | **332/419** |

### What changed

| # | Rule | Native source | Production change | Test |
| --- | --- | --- | --- | --- |
| 1 | Builder under-attack voice uses constructor sound | FedSrv.CPP:12124 | `BroadcastShipDamaged` reads `ship.BuilderStationType.ConstructorUnderAttackSound` instead of `UnderAttackSound` | `Builder_under_attack_uses_constructor_sound` |
| 2 | Same-side "Watch your fire" only for NPC victims; player victims get it only from allied other side | FedSrv.CPP:12098-12185 | Victim type gated on `PilotType`; player-victim branch whispers only when cross-side allied | `Same_side_player_victim_is_silent`, `Allied_cross_side_player_victim_sends_watch_your_fire` |
| 3 | Allied "(Ally)" prefix + "Watch your fire" for NPC and player allied hits | FedSrv.CPP:12120-12145, 12174-12185 | Team alert includes `"(Ally) "` prefix; allied player launcher also receives private whisper | `Allied_NPC_hit_sends_under_attack_with_ally_prefix_and_watch_your_fire`, `Allied_cross_side_player_victim_sends_watch_your_fire` |
| 4 | Non-ship launchers trigger the alert | FedSrv.CPP:12095 | Side read via `EquipmentTarget.SideOf(launcher)`; `Ship` required only for peer lookup | `Probe_launcher_triggers_under_attack_alert` |
| 5 | Chat sender is the victim ship, text matches native format | FedSrv.CPP:12120-12128 | `SidSender = ship.Id`; message is `"Under attack by {allyPrefix}{launcherName} in {sectorName}"` | `Cross_side_NPC_hit_sends_under_attack_team_alert` |
| 6 | Tests for rules 1-5 | — | Added 5 new live tests plus assertions on sender/voice | `ShipDamageAlertTests` (10 methods) |
| 7 | `GetCluster` matches native `CanSee` | shipIGC.h:380-388 | Own side OR `SeenBySide` OR `InScannerRange`; observer sector not required; warps/treasures/missiles covered; no LINQ | `ServerHostIgcSiteDefaultsTests.GetCluster_*` |
| 8 | `GetRipcordCluster` uses base hull | igc.h:4915-4921 | Comment clarified; `Ship.HullType` is the port's base hull, capability check unchanged | `ServerHostIgcSiteDefaultsTests.GetRipcordCluster_returns_sector_only_with_capability` |
| 9 | Production callers use the site overrides | common.cpp:811,824; shipIGC.cpp:3741,4262 | `Server.FindRipcordModel` (Server.cs:1174) now calls `_host.GetRipcordCluster(s, habm)` | `TimeDilationTimerAuditTests` |
| 10 | Corrected scorecard rows | — | SIM-0176/0177/0178/0180 back to `partial`; SIM-0231/0233 back to `partial`; SIM-0199 `implemented`; SIM-0232 `implemented` | `scripts/parity-scorecard.py` |

### Verification (P751b)

Final whole-solution build: `dotnet build AllegianceCore.slnx --nologo -m:1 -v:q -p:UseSharedCompilation=false`, **0 warnings / 0 errors**.

Final Linux x64 self-contained native AOT publish: `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -p:UseSharedCompilation=false -o .scratch/aot`, **succeeded, no IL2026 or IL3050 warnings**.

**Focused qualification:** each new or affected test ran **3 isolated invocations** with zero failures:

| Method / class | Outcome |
| --- | --- |
| `ShipDamageAlertTests` (10 methods) | 3/3 passed |
| `ServerHostIgcSiteDefaultsTests` (7 methods) | 3/3 passed |
| `TimeDilationTimerAuditTests` (ripcord search now routes through site) | 3/3 passed |

**One fast-tier run only**, using `tools/run-tests.sh --tier fast --no-build --results-dir .scratch/fast`:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Allegiance.Lobby.Tests | 19 | 0 | 0 |
| Allegiance.ClientCore.MonoGame.Tests | 2511 | 0 | 3 NotExecuted |
| Allegiance.ClientCore.Tests | 1472 | 0 | 0 |
| Allegiance.Core.Tests | 4876 | 0 | 0 |
| fast/quarantine extras | 12 | 0 | 0 |

Runner overall result: **PASS** — 8893 tests, 269.9 s.

### Corrected row inventory

| Weight | Row | Area | P751 status | P751b status | Rationale |
| ---: | --- | --- | --- | --- | --- |
| 3 | SIM-0176 | callbacks | n/a | **partial** | `PlaySoundEffect` is declared in `IIgcSite`; port plays through client cue planners, so partial rather than n/a |
| 3 | SIM-0177 | callbacks | n/a | **partial** | Same as SIM-0176 for the offset overload |
| 3 | SIM-0178 | callbacks | n/a | **partial** | `PlayNotificationSound` is declared in `IIgcSite`; port uses other seams |
| 3 | SIM-0180 | callbacks | n/a | **partial** | `PlayVisualEffect` is declared in `IIgcSite`; port derives visuals from client events |
| 3 | SIM-0199 | callbacks | implemented | **implemented** | All review items 1-6 fixed and tested |
| 3 | SIM-0231 | callbacks | implemented | **partial** | Override exists but no production caller routed through site yet |
| 3 | SIM-0232 | callbacks | implemented | **implemented** | Override plus production caller in `FindRipcordModel` |
| 3 | SIM-0233 | callbacks | implemented | **partial** | Override equals native default `false`; no reload-feedback caller |

## Scratch and evidence handling

Scratch space is `.scratch/` inside the worktree, listed in `.git/info/exclude`. No build, test result, cache, or temporary artifact was placed in `/tmp`. The AOT publish output, fast-tier results, and focused test logs remain in `.scratch/` for inspection; compact summaries are retained in `docs/reports/P751_assets/`. Both `.scratch/` and `docs/reports/P751_assets/` are worktree-local and will not be committed. Raw TRX/logs beyond the 2 MB report-asset limit stay in `.scratch/` and are not committed.

## Complete entry inventory

All missing/partial callbacks and periodic rows at entry, with final dispositions for the selected bundle. Previously completed wave rows are excluded.

| Weight | Row | Area | Entry status | Native item | Final status |
| ---: | --- | --- | --- | --- | --- |
| 4 | SIM-0142 | periodic | partial | auto_start_restart_ready_rules | partial |
| 3 | SIM-0141 | periodic | partial | server_sim_broadcast_cadence | partial |
| 3 | SIM-0144 | periodic | partial | station_state_docked_broadcast | partial |
| 3 | SIM-0150 | periodic | partial | warp_reentry_timer | partial |
| 3 | SIM-0470 | periodic | partial | ship_status_delta_and_turret_updates | partial |
| 3 | SIM-0471 | periodic | partial | target_hull_shield_fraction_updates | partial |
| 2 | SIM-0145 | periodic | partial | chat_budget_recovery | partial |
| 2 | SIM-0147 | periodic | partial | network_roll_call_and_disconnect | **n/a** |
| 3 | SIM-0172 | callbacks | partial | ReceiveChat@4792 | partial |
| 3 | SIM-0173 | callbacks | partial | SendChat@4801 | partial |
| 3 | SIM-0176 | callbacks | partial | PlaySoundEffect@4830 | **partial** |
| 3 | SIM-0177 | callbacks | partial | PlaySoundEffect@4831 | **partial** |
| 3 | SIM-0178 | callbacks | partial | PlayNotificationSound@4832 | **partial** |
| 3 | SIM-0180 | callbacks | partial | PlayVisualEffect@4834 | **partial** |
| 3 | SIM-0182 | callbacks | partial | TerminateModelEvent@4836 | partial |
| 3 | SIM-0183 | callbacks | partial | TerminateMissionEvent@4837 | partial |
| 3 | SIM-0195 | callbacks | partial | PostNotificationText@4849 | partial |
| 3 | SIM-0197 | callbacks | partial | TargetKilled@4851 | partial |
| 3 | SIM-0199 | callbacks | partial | DamageShipEvent@4853 | **implemented** |
| 3 | SIM-0205 | callbacks | partial | LandOnCarrierEvent@4859 | partial |
| 3 | SIM-0206 | callbacks | partial | RescueShipEvent@4860 | partial |
| 3 | SIM-0208 | callbacks | partial | HitWarpEvent@4862 | partial |
| 3 | SIM-0209 | callbacks | partial | HitTreasureEvent@4863 | partial |
| 3 | SIM-0212 | callbacks | partial | ContinueRipcord@4866 | partial |
| 3 | SIM-0213 | callbacks | partial | UseRipcord@4867 | partial |
| 3 | SIM-0214 | callbacks | partial | FireMissile@4868 | partial |
| 3 | SIM-0219 | callbacks | partial | ChangeStation@4876 | partial |
| 3 | SIM-0220 | callbacks | partial | ChangeCluster@4879 | partial |
| 3 | SIM-0221 | callbacks | partial | CommandChangedEvent@4882 | partial |
| 3 | SIM-0231 | callbacks | partial | GetCluster@4909 | **partial** |
| 3 | SIM-0232 | callbacks | partial | GetRipcordCluster@4915 | **implemented** |
| 3 | SIM-0233 | callbacks | partial | Reload@4923 | **partial** |

The remaining open rows stay visible for future waves.
