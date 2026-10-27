# P851 — certify the event-driven mission rules: IGC mission & site visibility

Batch: the 20 `FedSrv mission rules (event-driven)` rows SIM-0696, 0697, 0701, 0732, 0745, 0746, 0747, 0748,
0749, 0823–0832, 0834 (weight **44**). Native reference re-grepped this run:
`~/Development/Allegiance/src/Igc/missionigc.cpp`, `Igc/sideigc.cpp`, `Igc/clusterIGC.h`, `Igc/modelIGC.{h,cpp}`,
`Igc/probeigc.cpp`, `Igc/shipIGC.{h,cpp}`, `FedSrv/FedSrv.CPP` (all line numbers below re-verified). Worktree-only
change; nothing committed, nothing pushed; no rig/Mac contact; no `git stash`; `~/.dotnet/dotnet` throughout.

## Result

- **Batch: 19/20 rows `implemented`, 39/44 weighted credit earned (88.6%); before: 0/44.**
  SIM-0748 stays `partial` with two named remaining branches (below).
- **Family (`FedSrv mission rules (event-driven)`): 44.67% → 51.27% weighted** (264/591 → 303/591);
  unweighted 111/252 → 130/252 (production-reachability rows, the scorecard's own rule).
  `python3 scripts/parity-scorecard.py` validates (846 rows); CSV kept LF (`grep -c $'\r'` = 0) and **only this
  batch's 20 lines** were rewritten (`git diff --stat` = 20 insertions / 20 deletions on
  `docs/parity/sim-rules-matrix.csv`).

## Behaviour fixes (each with a test that fails without it)

| # | Native branch (re-grepped cite) | Port fix |
|---|---|---|
| 1 | First-sight score credits inside `ThingSiteImpl::UpdateSideVisibility`'s scanner loop: `AddTargetSpot` for a high-value enemy hull with the 50 s `RecentlySpotted` latch and `MarkPreviouslySpotted` "no matter by whom" (FedSrv.CPP:10952-10982), `SpotWarp`/`SpotSpecialAsteroid` for a ship spotter (FedSrv.CPP:11103-11111) — **missing entirely** | `Ship.MarkPreviouslySpotted`/`RecentlySpotted` (`shipIGC.h:1887-1895`, model-clock based) + `Sector.FirstSightEffects`/`HighValueTargetSpot`/`CreditShipSpotter` raised through the new `IIgcSite.SpotterCreditEvent` seam (`IIgcSite.cs:655`), credited on the spotter's `PlayerScore` by `Server.Combat.cs OnSpotterCredit` (`Server.Lifecycle.cs:804` wires it). Native's player-only gates (`GetPilotType() >= c_ptPlayer`) are the port's `!Ship.IsNpc`; native's unguarded `CFSShip::GetPlayerScoreObject()` null-deref for an NPC spotter is not reproduced (no peer ⇒ no credit). |
| 2 | `CsideIGC::HandleNewEnemyCluster` (`Igc/sideigc.cpp:361-419`), called from the station branch of `UpdateSideVisibility` (FedSrv.CPP:11087-11090) — **missing entirely** | `Sector.HandleNewEnemyCluster`: on the tick a side's own scanner first spots a station, that side's miners clear a queued order into that cluster, clear the plan/accepted order per native's branch table (both-bad ⇒ clear accepted; plan-bad ⇒ clear plan; accepted-bad ⇒ promote the plan into accepted), and keep untouched the orders pointing elsewhere. The port's typed slots (`QueuedShipTarget`/`QueuedTarget`, `AcceptedShipTarget`/`AcceptedTarget`, `PlanTarget`) get native's net effect; the `OT_buoy` AddRef/AddConsumer pair is managed memory. |
| 3 | `ThingSiteImpl::SetCluster`'s hide half (FedSrv.CPP:10711-10728) + `m_bSideVisibility`'s cluster membership: a **docked** ship leaves the cluster (`CstationIGC::AddShip → SetCluster(NULL)`, stationIGC.cpp:282-285) and a **boarded** ship's ThingSite is rebuilt without `c_mtSeenBySide` and unregistered as a scanner (`shipIGC.cpp:3632-3643`); a docked ship is therefore neither a `ClusterSite::GetScanners` entry (`clusterIGC.h:145-148`) nor a visibility target | The sweep excluded neither (the port keeps both kinds in `Sector.Models` for bookkeeping, as `Probe.RevealOnFiring` already documents). `Sector.ResolveCrossSideVisibility` now collects docked/boarded ships (`Sector.cs:4996`) out of the target **and** scanner sets and reconciles their visible flag to false for every non-own, non-allied side (`Sector.cs:5126`) — native's "you always lose contact when a non-static, non-allied object switches sectors", with the `bAllowAlliedViz` allied exemption and the predictable exemption preserved. |
| 4 | `CmissionIGC::GetSeenSides` → each candidate scanner's `InScannerRange(builder)`, which is **true** for a model whose `GetFlag() != NA` (missionigc.cpp:3658-3681 → shipIGC.h:351-369) | `Sector.ConstructionSeenSides` now passes `builderCarriesFlag` into `VisibilityMath.InScannerRange`, so a flag/artifact-carrying builder's construction is announced to a side whose scanner is out of range (and beyond occlusion), as P832c already did for the live sweep. |
| 5 | `CmissionIGC::CreateObject`'s `FAILED/S_FALSE → Terminate + Release + NULL` (missionigc.cpp:3370-3378), whose only port-reachable producer is `CprobeIGC::Initialize`'s "teleport probe dropped inside an asteroid" rejection (probeigc.cpp:200-212) | `Ship.DeployProbe` (`Ship.cs:4685`, now `Probe?`) registers the probe (native's `AddProbe`) then, for a teleport probe inside an asteroid (`distance < asteroidRadius - 5`), terminates it (which fires `TerminateModelEvent` and `DestroyTeleportProbe` exactly as `CprobeIGC::Terminate`, probeigc.cpp:222-245) and returns null so the creation export is skipped. Native's sibling aleph-tip rejection (probeigc.cpp:161-198) is gated on `MissionParams::bExperimental`, a game type this port does not model — era, documented. |

Nothing in this batch reintroduces the P858 spotter-cache flag; the last-spotter short-circuit stays unconditional, and
P832c's flag-carrier visibility is reused (not re-derived).

## Per-row table

Native branches → port sites → verdict → fix/test. Full branch lists live in each CSV row's `notes` column.

| Row | Native body (re-grepped) | Port site(s) | Verdict | Fix / pinning evidence |
|---|---|---|---|---|
| SIM-0696 | `CmissionIGC::Initialize` missionigc.cpp:3055-3072 | `Mission.cs Assemble` (site, `LastUpdate = now`, `DamageClock.Initialize`), `Sim/DamageTrackClock.cs` | equal; hull preload + `m_sideTeamLobby` n/a/design | `MissionUpdateTests.Chunk_count_matches_the_Cpp_boundaries`, `…Unchanged_now_is_a_no_op` |
| SIM-0697 | `CmissionIGC::Terminate` :3074-3125 | `Server.Lobby.cs RotateMissionAfterGameOver` (Site.TerminateMissionEvent → drop → fresh assemble), `ServerHost.TerminateMissionEvent` | equal (managed teardown for the ships/clusters/sides legs); pseudo-side + list asserts n/a | `P754CallbackTests.TerminateMissionEvent_reaches_site_when_invoked`; `MissionRotationLiveTests`; P480 rotation tests |
| SIM-0701 | `CmissionIGC::CreateObject` :3299-3384 | typed constructors + `Mission.NextObjectId` + `Ship.DeployProbe` | equal; **fixed** the S_FALSE rejection | `P851SpottingRulesTests.Teleport_probe_dropped_inside_an_asteroid_is_rejected_by_the_object_factory` (fix 5) |
| SIM-0732 | `CmissionIGC::GetSeenSides` :3658-3681 | `Sector.ConstructionSeenSides` (Sector.cs:5609) | **fixed** (flag bypass) | `P851SpottingRulesTests.Flag_carrying_builder_is_seen_by_a_distant_scanner_in_get_seen_sides` (fix 4) |
| SIM-0745 | `CmissionIGC::UpdateSides` :3734-3793 | `Mission.Assemble` side construction, `SidePalette.cs`, `ResearchSettings.StartingTechs` | equal (add path); delete path n/a (no live team-count shrink — rotation reassembles) | `MissionAssemblyTests.Sides_resolve_their_civilizations_from_the_core`; `ShipTrailSideColorTests.SidePalette_matches_native_table` |
| SIM-0746 | `CmissionIGC::UpdateAllies` :3795-3803 | `Server.Lobby.cs UpdateAlliances` (`sides[i].AllyGroup`, NA→null, every side) | equal | `LiveAllianceTests.Owner_change_alliance_pre_start_sets_and_broadcasts`; `P856FriendlyFireAlliedVizTests` |
| SIM-0747 | `CmissionIGC::ResetMission` :3804-3828 | `Server.Lobby.cs RotateMissionAfterGameOver` + P480 peer migration | equal by rebuild (design); ships→NULL cluster/`SetFlag(NA)` expressed as fresh seats; buckets rebuilt | `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`; P480 rotation test |
| SIM-0748 | `CmissionIGC::GenerateMission` :3830-4123 | `Server.Lifecycle.cs AssembleGameMission` (1220), `Mission.Assemble`, `Mission.GoalObjects`, `MapGen/MissionMapGenerator`, `Mission.Treasure.PopulateTreasures` | **partial** — REMAINING: (a) station-type replacement by the side's available techs (:4011-4049): the port does not decode `DataStationTypeIGC`'s required/local/effect tech masks at all, so the swap cannot be evaluated; (b) the file-map `PopulateCluster` top-up of clusters with no asteroids/treasures (:4051-4055) never runs (population is MapGen-only), so an empty cluster in a hand-made `.igc` stays empty. AGC admin tech overrides (:3885-3900) n/a (no AGC admin protocol) | `MissionAssemblyTests.Every_map_assembles_against_a_real_core`; `P663TreasureParityTests`; `MapGenFoundationTests` |
| SIM-0749 | `CmissionIGC::GenerateTreasure` :4125-4208 | `Mission.Treasure.cs GenerateTreasure` | equal (set/flag selection, cash gate, placement loop + radius growth + tries, clearance, amount, lifespan, id; `c_radiusUniverse` = 5000.0f at missionigc.cpp:2686) | `P663TreasureParityTests` (cash gate, weighted set, placement/clearance, lifespan/artifact payload) |
| SIM-0823 | `ThingSiteImpl::SetCluster` FedSrv.CPP:10711-10748 | `Sector.ResolveCrossSideVisibility` detached reconciliation (Sector.cs:4996/5126) | **fixed** (fix 3); enter-cluster `UpdateSideVisibility` = next sweep tick (design) | `P851SpottingRulesTests.Docked_and_boarded_ships_are_neither_scanners_nor_targets` |
| SIM-0824 | `ThingSiteImpl::ShowObject` :10750-10857 | `Server.Broadcast.cs SweepCrossSideExports` (1766) / `SweepCrossSideDiscoveryExports` (2110), `Server.Research.cs ExportDiscoveredStations`, new `Sector.FirstSightEffects` | equal + **fixed** (the score/tail halves, fix 1) | `P851SpottingRulesTests.High_value_first_sight_credits_the_player_once_per_50s_window`, `…Aleph_and_special_asteroid_first_sights_credit_the_spotting_ship`; `StationsUpdateLiveTests.P745_…` |
| SIM-0825 | `ThingSiteImpl::HideObject` :10859-10876 | `Server.Broadcast.cs SweepCrossSideShipStatuses` + `MakeCrossSideShipStatus` (Unknown = hidden) | equal (allied-viz guard, last-known fields, ships-only) | `P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` |
| SIM-0826 | `ThingSiteImpl::UpdateSideVisibility` :10878-11132 | `Sector.ResolveCrossSideVisibility` + `FirstSightEffects` (5315) / `HighValueTargetSpot` (5359) / `CreditShipSpotter` (5393) / `HandleNewEnemyCluster` (5422), `SideVisibilityState`, `Ship.MarkPreviouslySpotted` | equal + **fixed** (fixes 1, 2); Steam `c_achmGarrSpot` n/a (era); `UpdateTerritory` design (territory computed on the fly) | `P851SpottingRulesTests` (4 tests); `CrossSideVisibilitySweepTests`; `P856FriendlyFireAlliedVizTests`; `P832cFlagVisibilityTests` |
| SIM-0827 | `ThingSiteImpl::GetSideVisibility` :11133-11137 | `SideVisibilityState.SeenBySide` | equal; `m_bSideVisibility` attribute gate design (registration instead) | `CrossSideVisibilitySweepTests` (true→false transitions); `P493EnemySpottingLiveTests` |
| SIM-0828 | `ThingSiteImpl::GetCurrentEye` :11140-11144 | `SideVisibilityState.GetCurrentEye` | equal | `P493EnemySpottingLiveTests`; P851 high-value test (eye edge) |
| SIM-0829 | `ThingSiteImpl::SetSideVisibility` :11146-11169 | `SideVisibilityState.SetSideVisibility`/`RevealToSide`; effects in the host sweeps | equal (`CurrentEyed` always synced; station `UpdateTerritory` = on-the-fly `Sector.IsTerritory`) | `P832cFlagVisibilityTests.Production_sweep_tracks_pickup_drop_and_sector_boundaries`; P797 aleph-reveal tests |
| SIM-0830 | `ClusterSiteImpl::AddScanner` :11188-11195 | `Sector.ResolveCrossSideVisibility` per-side scanner set (query, not a registry) | equal by query; **fixed** for docked/boarded ships (fix 3) | `P851SpottingRulesTests.Docked_and_boarded_ships_are_neither_scanners_nor_targets`; `P835VisibilityCostTests` |
| SIM-0831 | `ClusterSiteImpl::DeleteScanner` :11196-11203 | same (set derived fresh every sweep) | equal by query; **fixed** for docking/boarding | `CrossSideVisibilitySweepTests.Moving_the_enemy_out_of_scope_flips_seen_to_unseen`; P851 docked test |
| SIM-0832 | `ClusterSiteImpl::GetScanners` :11204-11210 | `Sector.ResolveCrossSideVisibility` scanner loop | equal by query (no ordering-sensitive consumer) | `CrossSideVisibilitySweepTests`; `P835VisibilityCostTests` |
| SIM-0834 | `ThingSiteImpl::ThingSiteImpl` :10695-10702 | `SideVisibilityState.Predictable` per kind; sweep registration for `m_bSideVisibility` | equal (`m_bPredictable`); attribute gate design | `P832cFlagVisibilityTests`; P851 docked test; `CrossSideVisibilitySweepTests` |

## Tests added (new file `tests/Allegiance.Core.Tests/P851SpottingRulesTests.cs`)

| Test | Pins |
|---|---|
| `High_value_first_sight_credits_the_player_once_per_50s_window` | fix 1 + the native 50 s latch (a second spotter inside the window earns nothing) — also documents the native quirk that `m_timePreviouslySpotted = 0` (shipIGC.cpp:200) suppresses credits in a mission's first 50 s, which the port reproduces |
| `Ordinary_fighter_first_sight_books_no_target_credit` | the `hvMask` gate (`GetAssetMask` gives `c_amEnemyFighter`, FedSrv.CPP:10957-10959) and that a non-high-value hull is never stamped |
| `Aleph_and_special_asteroid_first_sights_credit_the_spotting_ship` | fix 1 (`SpotWarp`, `SpotSpecialAsteroid`; a plain buildable rock books nothing) |
| `Spotting_a_station_makes_the_seeing_sides_miners_abandon_that_cluster` | fix 2 (queued + plan cleared, an unrelated cluster's plan untouched) |
| `Docked_and_boarded_ships_are_neither_scanners_nor_targets` | fix 3 (docked ship unseen by the enemy, not a scanner, hidden again on re-docking, own-side flag left as native leaves it) |
| `Flag_carrying_builder_is_seen_by_a_distant_scanner_in_get_seen_sides` | fix 4 |
| `Teleport_probe_dropped_inside_an_asteroid_is_rejected_by_the_object_factory` | fix 5 (teleport probe rejected inside a rock, plain probe and an outside teleport probe created normally) |

Mutation check (each fix reverted in turn, `Sector.cs`/`Ship.cs` restored from a scratch copy afterwards): 5 of the 7 new
tests fail without their fix (the other two are negative/contract tests that must pass both ways). One existing test was
corrected because it encoded the *pre-fix* behaviour: `StationsUpdateLiveTests.P745_Newly_spotted_enemy_station_exports_identity_once_to_the_seeing_side`
moved a **docked** ship (it stayed in `Sector.Models`, so it scanned) — it now flies the seat first and places the new base
in two steps, keeping the test's intent (discovery → one identity export per peer) while matching native's cluster membership.
`P692EquipmentLiveTests.Teleport_activation_and_loss_reach_owner_and_enemy_with_native_criticality` had the same premise
(a docked viewer ship as the spotting scanner) and now clears the viewer's dock latch first; its two cases pass 3/3 alone.

## Verification (light gate)

- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**. `tools/build-tools.sh`: all 90 tool projects, 0/0.
- Rig AOT publish (`Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`): **clean**, no
  IL2026/IL3050 (`Release` sources only; scratch output deleted).
- New tests 3× alone: `P851SpottingRulesTests` **3/3 pass**; `StationsUpdateLiveTests` (affected) **3/3 pass**.
  570 visibility/visibility-adjacent tests (`*Visibility*`, `*CrossSide*`, `*Cloak*`, `*Probe*`, `*Mine*`,
  `*Detection*`, `*ClusterWarning*`, P832c/P856/P835, `*Dock*`): **570/570 pass**.
- Whole `Allegiance.Core.Tests` run (one, 21 min): **5615/5622**. The 7 failures are the three ledgered host-load
  flakes — `BroadcastSnapshotTests` (4 cases), `ConsoleVerbosityLiveTests.Stalled_stdout_…verbose_on`,
  `P699bPodLoadTests.Hundred_pods…` — and I verified the first two **also fail with `src/Allegiance.Core/Model/Sector.cs`
  restored to HEAD** on this host (BroadcastSnapshot 4/5 and the stdout test 1/1 both ways), so none is mine;
  `P699bPodLoadTests` passed 2/3 alone (ledgered). `ConsoleVerbosityLiveTests.Stalled_stdout_…verbose_on` is not yet in
  `docs/TEST_FLAKES.md` — noted here rather than editing a doc other lanes are appending to.
- Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`):
  **5/5 pass** (bot/sim behaviour changed via fix 2/3).
- ONE fast tier (`tools/run-tests.sh --tier fast`), run twice because the first caught a real regression: **run 2 is clean,
  exit 0** — Core 5491, ClientCore 1875, MonoGame 3610 (+3 not executed), Lobby 82, Update 28, **0 failures**. Run 1 had
  three: the two `P692EquipmentLiveTests.Teleport_activation_and_loss…` cases (mine — that theory moved a **docked** viewer
  ship in as the spotting scanner, exactly the deviation fix 3 removes; the test now undocks the viewer and passes its two
  cases 3/3 alone) and `AsteroidsUpdateLiveTests.Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full`
  (ore-tick timing under parallel load; 3/3 alone, a load flake not in the ledger yet). Full per-suite numbers:
  `docs/reports/P851_assets/fast-tier.txt`.
- `python3 scripts/parity-scorecard.py`: validates (846 rows). `python3 tools/check-glossary-links.py`: clean
  (GLOSSARY gained a "Spotting side effects (P851)" entry pointing at this report).

## Native-intent classification

- **Design (kept as designed, port-idiomatic):** the every-10-tick sweep is the port's `UpdateSeenBySide` (so
  `SetCluster`'s inline `UpdateSideVisibility` becomes "next sweep tick"); `ShowObject`/`HideObject`'s wire effects live
  in the host sweeps while the flags live in Core; `GetScanners`/`AddScanner`/`DeleteScanner` are a per-sweep **query**
  over the sector's live models instead of a maintained registry; territory (`UpdateTerritory`) is computed on the fly
  (`Sector.IsTerritory`) so it needs no poke; `ResetMission` + `GenerateMission` collapse into one fresh assembly;
  `m_bSideVisibility` is structural (only `c_mtSeenBySide` kinds are registered) rather than an attribute test; WARP_BOMB
  is broadcast whole-mission at bomb time (a superset of native's per-side re-send — small intel leak, accepted).
- **Era / not-modelled (n/a, stated per row):** `HitTest::Load` preloads (managed hull cache), `m_sideTeamLobby`
  pseudo-side, the C++ refcount/`AddConsumer` bookkeeping, `bExperimental`'s aleph-tip probe ban, the Steam
  `c_achmGarrSpot` achievement, the AGC admin tech-override masks, and the mission's list-empty asserts.
- **Native bugs (deliberately not reproduced):** `CFSShip::GetPlayerScoreObject()` is dereferenced unconditionally for a
  ship spotter (an NPC spotter would fault — the port credits only a peer-owned ship); `CreateObject`'s
  `assert(p)` on a NULL probe/mine from an S_FALSE `Initialize` in `LayExpendable` (only a debug-build trap; the port
  simply skips the export).

## Modern/better-approach note

The two blocking branches left in SIM-0748 are data-shape gaps, not logic gaps: `DataStationTypeIGC` decodes 540 bytes
of station-type payload but no tech masks, so native's "replace a station with one the side can actually field" rule
(the custom-map tech downgrade) cannot be evaluated. The clean follow-up is one decode extension (`requiredTechs`,
`localTechs`, `effectTechs` — the fields exist in the record and the maps carry them) plus a `Mission.Assemble` pass over
`map.Stations`, and the same pass could reuse `MissionMapGenerator`'s population helpers to top up empty clusters of a
file map (branch b) instead of growing a second population path. Both are mechanical once the masks are decoded, and
neither needs the swept visibility machinery to change — the P851 seams (`SpotterCreditEvent`, `FirstSightEffects`) are
already in place for whatever a future row wants to book on a first sighting.

## Evidence / hygiene

- Raw logs and this run's TRX files are outside the repo (`~/allegiance-evidence/P851/`); nothing large was added to
  `docs/reports/`.
- No home paths, emails or secrets in `src/ tests/ scripts/ tools/ docker/ .github/`; the scratch copies used for the
  mutation checks lived in the worktree's `.scratch/` and were deleted.
- Not committed, not pushed: the changes are left in the working tree for the dispatcher.

## Appendix — fast tier

`tools/run-tests.sh --tier fast` (both runs, per-suite counts, and the ledger status of every failure):
`docs/reports/P851_assets/fast-tier.txt`. Run 2 (final tree) exits 0 with zero failures.
