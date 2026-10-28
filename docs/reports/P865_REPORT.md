# P865 — parity: stations, docking, rearm, tech and drones

Lane: pi (worktree `allegiance-pi-p865`, branch `pi/p865`). Native reference
`~/Development/Allegiance/src/` (read-only; every rule below re-grepped at
implementation time). **No commits/pushes** — all changes left in the working
tree.

## What changed

**SIM-0636/0638/0643 — eager `UpgradeDrones` (new `ServerHost.UpgradeDrones`).**
The port of native `FedSrvSiteBase::UpgradeDrones` (`FedSrv.CPP:11234-11257`):
per-side station walk, non-player gate (`!pfsship->IsPlayer()`), then the
existing `UpgradeNpcShipAtStation` (the ported `UpgradeShip`). Wired at both
native call sites: the `SideDevelopmentTechChange` tail
(`FedSrv.CPP:11312`) from `Server.Economy.CompleteDevelopment`, and the
`BuildStation` tail (`FedSrv.CPP:11507`) from `ServerHost.BuildStation`. The
dock-time call (`FedSrv.CPP:13240`) is unchanged.

**SIM-0662/0666/0667 — the allied-base dock entry.**
- `Sector.ResolveDockCollisions` now admits own-or-ALLIED docks
  (shipIGC.cpp:729-731, the `pside1->AlliedSides(pside1, pside2)` leg), so the
  allied-dock half of the handlers is reachable at all.
- Capture gate gets its ALLIED exemption (FedSrv.CPP:13100-13105): an allied
  `c_habmBoard` hull captures only when `bAllowFriendlyFire` is on — and then
  bypasses the enemy-branch shield gate exactly as native (the shield-down gate
  is shipIGC.cpp:849-851, the *enemy* collision branch only).
- `DockWithStationEvent` gives the station to boarded children
  (FedSrv.CPP:13172-13178).
- `ChangeStation` grows the whole #ALLY allied-base branch
  (FedSrv.CPP:13242-13310): a player docking at an allied base is rearmed
  (the restock body is the port's `RestockOnDock`) and relaunched —
  `BUY_LOADOUT_ACK` with `fLaunch = true` (FedSrv.CPP:12776-12782) plus
  ship+children `SetStation(NULL)` via the real undock path
  (FedSrv.CPP:12800-12810). A lifepod at an allied base is re-homed: own-side
  restart station (`GetBase` fallback, fsmission.cpp:6398-6412 — the port keeps
  no per-pilot old-ship-status store, so native's own fallbacks are used),
  hull from native's reverse free-buyable walk (FedSrv.CPP:13273-13283),
  `SHIP_DELETE(SDR_DOCKED)` to the whole mission (FedSrv.CPP:13287-13291),
  re-dock + re-hull. One documented deviation: the pod latches the allied dock
  for the instant it takes the redirect to re-home it inside the same event
  (native skips `Dock` there); the net observable state is native's.

**SIM-0780 — `RefillPart` unconditional.** The restock core is extracted as
`Server.Docking.RefillAndRestock` (shared by the station dock and the carrier
relaunch). The mounted-part successor upgrade is now unconditional — native
`RefillPart` never charges and never keeps the base part
(FedSrv.CPP:12592-12620). The wallet gate (P441 soft-fail) now applies only to
the desired-loadout *re-buys*.

**SIM-0663 — carrier landing restock.** `PerformRelaunch` runs the shared
restock core: the `RefillPart` walk (FedSrv.CPP:12818-12848), the player-only
desired-loadout re-buy + depleted-launcher remount (:12836-12900) and the full
scalar reset including `SetFuel`/`SetFraction(1.0)`/shield fraction
(:12855-12861), on top of the already-ported launch-slot placement and
`RELAUNCH_SHIP(192)` broadcast.

**SIM-0650 — non-victory destruction announcement.**
`Mission.NotifyStationVictory` raises the host seam for every station event
(not only when the recompute changed/won), and
`Server.OnVictoryByStationsComputed` sends the native
`"%s has destroyed %s's %s."` line `CHAT_EVERYONE` when a side's ship killed a
station without producing a victory (FedSrv.CPP:12376-12379).

**SIM-0637 — prosperity GameOver.** The economic-victory GameOver now carries
native's reason text `"%s has proven the economic value of their outpost."`
(FedSrv.CPP:11276-11277). The `ProsperityEnabled` gate is **kept** because it
is native reachability, not scoping: native `CreateBuckets` excludes the
`c_didTeamMoney` development (sideigc.cpp:186) and `EnterGame` creates its
bucket only for prosperity games (missionigc.h:769-794); this port seeds the
bucket in every mission, so the gate reproduces native behaviour exactly
(pinned by a test).

**SIM-0770 — drone naming ceremony.** `BuildPlayerInfo` names NPC ships exactly
like native `CreateStockDrone`: `<droneTypeName><shipId>`, or the building
station type's `BuilderName` for a builder drone (FedSrv.CPP:2246-2261).
(`SetDroneTypeID`/DtidDrone, real hull + builder base-data, whole-mission
`SendPlayerInfo` and the standing ship-status broadcast already covered the
rest of the body.)

## Verification

- `dotnet build AllegianceCore.slnx` — 0 warnings, 0 errors.
- `tools/build-tools.sh` — all 91 tool projects built.
- AOT publish (`-p:PublishAot=true`, linux-x64) — clean, no IL2026/IL3050.
- New tests 3× alone: `P865StationDockTechTests` 15/15 ×3;
  `P832bProsperityLiveTests` (updated reason assertion) ×3.
- ONE fast-tier run: `tools/run-tests.sh --tier fast` — **PASS**, 11,323 tests
  (`TestResults/tiers-20261028T185651.843486Z/summary.json`).
- Showcase never-parks ×5:
  `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
  5/5 passed (3m20s–4m40s).
- `python3 scripts/parity-scorecard.py` — validates; no CRLF in the CSV.
- Targeted regressions green before the fast tier: Dock*/Launch*/Relaunch/P754/
  P760/P845/P847/P650/ConquestTerritory/StationCombat/Prosperity/P703/P832
  (322 + 94 tests).

## Scorecard (before → after)

| scope | before | after |
|---|---|---|
| sim-rules TOTAL weighted | 94.80% (1804/1903) | **96.11% (1829/1903)** |
| sim-rules TOTAL unweighted | 93.16% (640/687) | **94.76% (651/687)** |
| FedSrv mission rules weighted | 85.57% (516/603) | **89.72% (541/603)** |

## Per-row table

| Row (weight) | Native cite | Before → after | New tests |
|---|---|---|---|
| SIM-0636 (2) | FedSrv.CPP:11234-11257 (walk+gate); 11312; 11507 | partial → **implemented** | `UpgradeDrones_upgrades_docked_npcs_and_skips_player_ships`; `UpgradeDrones_only_walks_the_owning_sides_stations`; `Completing_a_development_upgrades_docked_drones_eagerly`; `Station_build_upgrades_docked_drones` |
| SIM-0637 (3) | FedSrv.CPP:11259-11280; sideigc.cpp:186; missionigc.h:769-794 | partial → **implemented** (gate = native reachability, documented + pinned) | `Non_prosperity_teamMoney_completion_does_not_end_the_game`; `P832bProsperityLiveTests` (native reason text) |
| SIM-0638 (2) | FedSrv.CPP:11282-11314 (tail :11312) | partial → **implemented** | `Completing_a_development_upgrades_docked_drones_eagerly` (+ existing P845 suite) |
| SIM-0643 (3) | FedSrv.CPP:11374-11516 (trailing :11507) | partial → **implemented** (2 artefacts classified, below) | `Station_build_upgrades_docked_drones` (+ existing P652/P747/P769 suites) |
| SIM-0650 (2) | FedSrv.CPP:12237-12409 (chat :12376-12379) | partial → **implemented** (AGC/Steam n/a) | `Non_victory_station_kill_announces_the_destruction_to_everyone` |
| SIM-0662 (2) | FedSrv.CPP:12655-12812; shipIGC.cpp:729-731; FedSrv.CPP:13295-13310 | partial → **implemented** | `Player_docking_at_an_allied_base_is_rearmed_and_relaunched`; `Allied_ship_docks_at_an_allied_station_without_capture` |
| SIM-0663 (2) | FedSrv.CPP:12814-13031 | partial → **implemented** (autopilot re-task n/a, below) | `Dock_restock_upgrades_the_successor_even_on_an_empty_wallet` (shared restock core) + existing LiveCarrierLanding/P754 |
| SIM-0666 (3) | FedSrv.CPP:13087-13177 (:13100-13105, :13172-13178) | partial → **implemented** | `Docking_gives_the_station_to_boarded_children`; `Allied_board_hull_captures_only_with_friendly_fire` (theory FF off/on); `Non_allied_non_board_ship_still_cannot_dock_at_a_foreign_station` |
| SIM-0667 (2) | FedSrv.CPP:13179-13311 (:13242-13310) | partial → **implemented** | `Lifepod_at_an_allied_base_is_rehomed_at_the_own_station`; `Player_docking_at_an_allied_base_is_rearmed_and_relaunched` |
| SIM-0770 (2) | FedSrv.CPP:2220-2275 (naming :2246-2261) | partial → **implemented** | `Drone_playerinfo_carries_the_native_name_id` |
| SIM-0780 (2) | FedSrv.CPP:12589-12621 | partial → **implemented** | `Dock_restock_upgrades_the_successor_even_on_an_empty_wallet` |

## Native-intent classification (branches not ported verbatim)

- **n/a — AGC/Windows telemetry** (SIM-0643 `EventID_StationCreated`,
  SIM-0650 AGC events + Steam achievements): no AGC module exists in the port.
- **n/a — autopilot command model** (SIM-0663 Goto→Defend re-task,
  FedSrv.CPP:13026-13030): the port has no `c_cid*` autopilot command state
  (established P113 precedent); a relaunched NPC re-decides its order next tick
  via the P386 plan-clear, which is the same observable outcome.
- **design — roid-sig/pos/radius/caps disguise** (SIM-0643,
  FedSrv.CPP:11463-11476): native copies rock data onto a station a side cannot
  see so its client draws a rock. The port never exports a not-yet-visible
  station at all (SideVisibility-gated export); a side receives nothing until
  first sight, then a full station export. No hidden-station wire record exists
  to disguise.
- **design/equivalence — the prosperity gate** (SIM-0637): documented above;
  the port's gate equals native's reachability because the port seeds the
  teamMoney bucket where native does not.
- **design — lifepod re-home lookups** (SIM-0667): the port keeps no per-pilot
  old-ship-status store, so "last station" uses the side's restart station
  (native's `GetBase` fallback) and "last hull" uses native's own no-saved-hull
  fallback walk.
- **deviation-in-event — allied pod latch**: the pod latches the allied dock
  for the instant before the redirect re-homes it (native never calls `Dock`
  there); the wire carries only the home station.

## Modern/better-approach note

The allied-dock entry was implemented as a single predicate
(`own-or-AlliedSides`) at the sector's one dock funnel, with the capture gate
kept as a *separate* predicate beside it — native's split between the IGC
collision branch (shipIGC.cpp:729/845) and the FedSrv capture gate
(FedSrv.CPP:13100) is preserved conceptually but collapses two code paths into
one readable gate with the FF/shield terms named explicitly. The restock core
is now shared by station docks, allied rearm and carrier landings, so
`RefillPart`'s rule exists once. If a future task adds full per-pilot ship
status (native `GetOldShipStatus`), the two fallbacks in the lifepod redirect
are the only places to upgrade.

## Unfinished

Nothing known. The fast tier, AOT publish, 3× new-test runs and the 5×
showcase run are all green; the scorecard validates. `.scratch/` was removed;
evidence lives in `TestResults/tiers-20261028T185651.843486Z/`.
