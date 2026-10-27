# P845 — Certify the event-driven mission rules: site events (connections, research/tech/hull/part/station completion)

**Worktree:** `/home/domingo/allegiance-hermes-p845` (public AllegianceCore worktree at master, branch `hermes/p845`)
**Native reference (read-only):** `~/Development/Allegiance/src/`
**Rows:** SIM-0579, SIM-0580, SIM-0582, SIM-0583, SIM-0584, SIM-0636, SIM-0637, SIM-0638, SIM-0639, SIM-0640, SIM-0641, SIM-0642, SIM-0643, SIM-0644, SIM-0645, SIM-0770 (16 rows, weight 34)
**Date:** 2026-10-27 · **Not committed / not pushed** (left in the working tree, per pi-lane rules)

---

## Summary

Certification of the "FedSrv mission rules (event-driven)" batch: the FedSrv-site site-event handlers
(`FedSrvSiteBase`) that fire on connections, message transport, and research / tech / hull / part /
station / drone completion. The batch splits naturally into four groups:

1. **Connection / transport callbacks** (0579, 0580, 0582, 0583, 0584) — DirectPlay/FedMessaging-era
   callbacks whose semantics are realized in the port's TCP transport + dispatch. Mostly structural
   mapping; the giant `OnAppMessage` dispatch stays `partial` pending a per-case audit.
2. **Research / tech-change** (0636, 0637, 0638, 0639) — `DevelopmentCompleted`,
   `SideDevelopmentTechChange`, `SideGlobalAttributeChange`, `UpgradeDrones`.
3. **Completion events** (0640, 0641, 0642, 0644, 0645) — `HullTypeCompleted`, `PartTypeCompleted`,
   `StationTypeCompleted`, `DroneTypeCompleted`, `UpgradeShip`.
4. **Construction / spawn** (0643 `BuildStation`, 0770 `CreateStockDrone`).

**Result:** 6 of 16 rows certified **implemented** (0639, 0640, 0641, 0642, 0644, 0645 — weight 12);
10 rows remain **partial** (weight 22) with precise residual statements. One genuine behaviour gap was
found and fixed (**SIM-0638** station-successor upgrade on tech change) with 3 new tests (pass 3/3, ×3).

**Family weighted % (FedSrv mission rules (event-driven)):** **1.52 % → 3.55 %** (9/592 → 21/592).
`python3 scripts/parity-scorecard.py` validates clean.

---

## Method

For every row the native body at `native_cite` was re-grepped in `~/Development/Allegiance/src/FedSrv/FedSrv.CPP`
(line numbers re-confirmed) and each branch listed, then mapped to the port (`src/Allegiance.Server`,
`src/Allegiance.Core`). Verdict per branch: **equal / different / missing / n/a** (era constraint). Every
behaviour-relevant different/missing branch was fixed (or documented with an exact residual); a test was
added for each fix (fails before the fix — for the new method, compilation fails) and each row's `evidence`
column lists the pinning test(s).

---

## Row-by-row

### SIM-0579 `FedSrvSiteBase::OnNewConnection` — `FedSrv/FedSrv.CPP:4536-4546` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| `GetIPAddress` + debugf log of new connection id/IP | `TcpTransport` accept path logs `RemoteEndPoint` (`TcpTransport.cs:881,1471`) | different |
| returns S_OK | n/a (managed return) | n/a |

Native is a pure debug-log callback with no behavioural effect. The port's TCP accept logs the remote
endpoint. Kept `partial` only because the DirectPlay `CFMConnection` connect ceremony has no 1:1 port
(era constraint); no behaviour is lost.

### SIM-0580 `FedSrvSiteBase::OnDestroyConnection` — `FedSrv/FedSrv.CPP:4549-4566` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| `GetPlayerFromConnection` → if player: `PlaySound(AppGPFault)`, `TriggerEvent(PlayerDropped)`, `cTimeouts++/cTimeoutsPerSecond++`, `delete pfsPlayer` | `Server.Lifecycle.cs ReleasePeerWithReason`/`RetireDepartedPeer` (P813c): retire ship + announce departure; leader succession; QUIT_MISSION; win-by-inactive check | different (superset) |
| if no player: no-op | idempotent early-return path | equal |

The departure/cleanup behaviour is fully realized (and elaborated) in the port. Residual: native's
`AllsrvEventID_PlayerDropped` telemetry event and the `cTimeouts` server counter have no named 1:1
counter/telemetry row (the port tracks disconnects via `TcpTransport.DisconnectReason` counts instead).
Behaviour-relevant player-drop logic is equal; the telemetry counter is a non-behaviour residual.

### SIM-0582 `FedSrvSiteBase::OnBadCRC` — `FedSrv/FedSrv.CPP:4591-4603` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| format corrupt-message log, `OnMessageBox`, `DeleteConnection(cnxn)` (drop offender) | `TcpTransport` frames each packet with a trailing CRC and `DropPeer`/`ClosePeer` on validation failure (`TcpTransport.cs:35,789,813`) | equal (intent) / different (plumbing) |

Native drops a peer on a bad CRC; the port validates the per-frame CRC and drops the peer. Intent equal,
mechanism rewritten for TCP (era constraint). Residual: native writes the offender line to a
crashplayers file + message box — developer-only diagnostics, not behaviour.

### SIM-0583 `FedSrvSiteBase::OnMessageSent` — `FedSrv/FedSrv.CPP:4613-4651` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| guaranteed: bump `cPacketsOut/PerSecond`, `cBytesOut/PerSecond` (× connection count); ORDER_CHANGE debug | `EgressAccounting.cs` per-fmid packet/byte counters; `SendPeer`/egress paths | different |
| not-guaranteed: (commented out) | n/a | n/a |

Performance-counter accounting only (no gameplay). The port has egress accounting but not the exact
named `g.pServerCounters` rows; kept `partial` as a metrics-mapping residual, no behaviour loss.

### SIM-0584 `FedSrvSiteBase::OnAppMessage` — `FedSrv/FedSrv.CPP:4798-7827` · **partial**
The ~3000-line client-message switch (packet-in counters + per-`fmid` handler). Port: `Server.Dispatch.cs
Dispatch` plus per-message handlers across `Server.*.cs`. Client consumption is machine-checked by
`FedMsgDispatchCoverageTests` (all 113 server-sent consumed). **Kept `partial`**: certifying
branch-for-branch equality of the entire native switch per case is a large audit beyond this batch's
scope; this row documents the structural mapping (dispatch ↔ switch, packet-in counters ↔
`EgressAccounting`/`PacketsIn`) and names the residual as a per-case audit.

### SIM-0636 `UpgradeDrones` — `FedSrv/FedSrv.CPP:11234-11257` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| for each side station → each docked ship → if !IsPlayer → `UpgradeShip(pship,pstation)` | `ServerHost.ChangeStation` → `UpgradeNpcShipAtStation` on NPC dock (`ServerHost.cs:1743-1744`) | different |
| eager on tech change / station build | realised at dock-time, not eagerly | missing (approximated) |

Native eagerly upgrades *currently-docked* NPCs when techs change/build completes; the port upgrades an
NPC only when it *next docks*. Residual documented; the underlying `UpgradeShip` algorithm is fully
ported (see 0645). `partial`.

### SIM-0637 `DevelopmentCompleted` — `FedSrv/FedSrv.CPP:11259-11280` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| `ApplyDevelopmentTechs(pdev->GetEffectTechs())` (OR new bits) → stage-gated SIDE_TECH_CHANGE(75) | `Server.Economy.cs CompleteDevelopment` (P189) + `BroadcastSideTechChange` | equal |
| `DEV_COMPLETED(8)` to side's own group, unconditional | `BroadcastDevCompleted` | equal |
| unless tech-only: `ApplyGlobalAttributeSet` → whole-mission SIDE_ATTRIBUTE_CHANGE(76) | `GlobalAttributeSet.Apply` (multiply) + `BroadcastSideAttributeChange` | equal |
| `if (pdev->GetObjectID() == c_didTeamMoney) GameOver(...)` | `CompleteDevelopment` step 4 gated on `mission.ProsperityEnabled` (P302) | **different** |

All but one branch equal; the `c_didTeamMoney` economic-victory GameOver is unconditional in native
(`:11274-11279`) but the port gates it on `ProsperityEnabled` (deliberate scoping so a non-prosperity
mission completing that dev does not end). Documented residual — kept `partial` (the divergence is
intentional, protective, and prosperity-driven; flipping to unconditional risks ending non-prosperity
games the port intends to protect). Strong test coverage: `TechTreeDevelopmentLiveTests`
(4 live tests) + `P832bProsperityLiveTests` (economic-victory GameOver).

### SIM-0638 `SideDevelopmentTechChange` — `FedSrv/FedSrv.CPP:11282-11314` · **partial** ⭐ FIXED BRANCH
| Native branch | Port site | Verdict |
|---|---|---|
| stage==STARTED: SIDE_TECH_CHANGE(75) to side group | `BroadcastSideTechChange` (stage gate) | equal |
| for each station: successor≠old → `SetName(succ->GetName())` + `SetBaseStationType(succ)` | **NEW** `Side.UpgradeStationsToSuccessors()` (P845) wired into `CompleteDevelopment` | **fixed** (was missing) |
| `UpgradeDrones(pside)` | realised at dock-time (`ServerHost`); see SIM-0636 | different (approx.) |

**Fix:** native upgrades every owned station to its buyable successor station type (name + base type)
when development techs change — previously missing entirely (the port only broadcast the tech change).
Added `Side.UpgradeStationsToSuccessors()` and `Station.SetName`; the upgrade renames then retypes
(native `SetName(succ->GetName())` + `SetBaseStationType`). (Claude review: hermes first folded the
rename into `SetBaseStationType`, which would also rename stations on a civ change; native's civ change
retypes without renaming, `sideigc.h:92-100`.) Called from `CompleteDevelopment` after the stage-gated broadcast,
matching native order. **Tests (fail before fix — the method did not exist / was not wired):**
`P845SideDevelopmentTechChangeTests` — (1) `Side_development_tech_change_upgrades_owned_stations_to_successor_type`
(upgrades type+name, fires `StationTypeChange`, preserves identity), (2) `No_dev_bits_unlocked_leaves_stations_unchanged`,
(3) `Only_the_tech_gaining_sides_stations_are_upgraded`. Residual: the trailing `UpgradeDrones` is realised
at dock-time (SIM-0636 scope), not eagerly on tech change → `partial`.

### SIM-0639 `SideGlobalAttributeChange` — `FedSrv/FedSrv.CPP:11316-11325` · **implemented**
| Native branch | Port site | Verdict |
|---|---|---|
| build SIDE_ATTRIBUTE_CHANGE(76) {sideID, gasAttributes}, send to `GetGroupMission()` (whole mission), unconditional | `Server.Economy.cs BroadcastSideAttributeChange` → all mission peers | equal |

Exact whole-mission broadcast, tested for both same-side/opposite-side scoping and cross-mission
isolation by `TechTreeDevelopmentLiveTests` (fmid 76).

### SIM-0640 `HullTypeCompleted` — `FedSrv/FedSrv.CPP:11327-11332` · **implemented**
| Native branch | Port site | Verdict |
|---|---|---|
| `pside->AddToStockpile(pht)`; `//NYI send messages` | `ServerHost.HullTypeCompleted` → `Side.AddToStockpile(HullType, hullId)`; no wire message (matches NYI) | equal |

Pinned by `P691EconomyCallbackTests.Typed_completion_stocks_hulls_and_parts_separately_per_side_and_type`
(+ `Explicit_stock_bucket...`).

### SIM-0641 `PartTypeCompleted` — `FedSrv/FedSrv.CPP:11334-11341` · **implemented**
| Native branch | Port site | Verdict |
|---|---|---|
| `AddToStockpile(ppt, IsLauncher?GetAmount(NULL):1)`; `//NYI send messages` | `ServerHost.PartTypeCompleted` — launcher uses authored `Amount`, else 1; no wire message | equal |

Pinned by `P691EconomyCallbackTests.Launcher_completion_uses_loaded_amount_for_raw_and_mountable_forms`
+ `Typed_completion...`.

### SIM-0642 `StationTypeCompleted` — `FedSrv/FedSrv.CPP:11343-11372` · **implemented**
| Native branch | Port site | Verdict |
|---|---|---|
| active gate `GetActiveF()` | `CompleteStationType` (`!side.Active` return) | equal |
| `CreateStockDrone(constructionDroneType, side, pstationtype)` | `SpawnNpcShip` construction drone + `BuilderStationType` base-data | equal |
| dock→pick-default→undock | spawned already-undocked at home (native post-undock state) | equal (approx.) |
| don't auto-build (rewrite cidBuild) | `WaitsForBuildOrder` gate | equal (approx.) |
| team chat "New constructor requesting asteroid." + ConstructorNeedRockSound | `BroadcastConstructorNeedRock` (P458) | equal |

Pinned by `StationBucketSpawnsConstructorLiveTests` (2 tests).

### SIM-0643 `BuildStation` — `FedSrv/FedSrv.CPP:11374-11516` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| successor station type | `Sector.BuildStation` `StationSuccessor` | equal |
| probe-expiry near rock (Xynth #10) | `Sector.BuildStation` probe `SetExpiration(now+1)` | equal |
| kill-asteroid receipts / seen sides | side visibility + `StaleRockSides` + `AsteroidDestroyed` export (`Server.Research.OnStationConstructed`) | different (approx.) |
| `EventID_StationCreated` telemetry | `StationConstructed` hook | different |
| unseen-side roid sig/pos/radius/caps copy | not mirrored | missing |
| `UpgradeDrones(pside)` | dock-time only (SIM-0636) | different |
| `CheckForVictoryByStationBuild` → GameOver | station-capture/kill victory paths exist; **build-triggered** victory not confirmed | missing (residual) |

Construction core is ported and tested (`P652ConstructorBuildTests`, `P747ConstructionTests`,
`P769BuilderCollisionBuildTests`); residuals (telemetry, roid-receipt fidelity, build-triggered victory)
kept `partial`.

### SIM-0644 `DroneTypeCompleted` — `FedSrv/FedSrv.CPP:11518-11552` · **implemented**
| Native branch | Port site | Verdict |
|---|---|---|
| active gate; assert pilot!=builder (log) | `CompleteDroneType` active gate + builder-ignore | equal |
| `CreateStockDrone(dronetype, side, layer?GetLaidExpendable():NULL)` | `SpawnNpcShip` + `LayerLaidExpendableTypeId` | equal |
| dock→pick-default→undock | spawned already-undocked; lazy per-pilot order | equal (approx.) |
| team chats: mine-layer / tower-layer / miner-where-to | `BroadcastTeamChat` (minefield/tower/miner) | equal |
| carrier: no chat | no chat | equal |

Pinned by `DroneBucketSpawnsDroneLiveTests` (4 tests: wingman, miner, layer+chat, carrier).

### SIM-0645 `UpgradeShip` — `FedSrv/FedSrv.CPP:11554-11628` · **implemented**
| Native branch | Port site | Verdict |
|---|---|---|
| hull successor ≠ current → nuke parts, `SetBaseHullType(succ)`, buy preferred loadout | `ServerHost.UpgradeNpcShipAtStation` hull branch + `BuyPreferredLoadout` (P760) | equal |
| same hull → backward part walk, trash cargo (`GetMountID()<0`), per-part successor remount | same-hull part successor walk + cargo purge | equal |
| mount-max weapons vs 1; ignore price | `MountMaxWeapons` vs 1; price ignored | equal |
| trigger on NPC dock | `ChangeStation` NPC path (`ServerHost.cs:1743`) | equal |

Algorithm ported branch-for-branch and helper-pinned by `SuccessorUpgradeTests` (HullSuccessor /
PartSuccessor / StationSuccessor) + `P760ServerCallbacksTests.ChangeStation_launch_repair_and_first_dock_tech_recovery`;
trigger on NPC dock verified. The eager-on-tech-change trigger is captured by SIM-0636 (partial).

### SIM-0770 `CreateStockDrone` — `FedSrv/FedSrv.CPP:2220-2275` · **partial**
| Native branch | Port site | Verdict |
|---|---|---|
| `CreateDrone(...)` with hull/pilot/skills; `SetBaseData` if base | `Server.cs SpawnNpcShip` (real hull + base-data via drone type) | equal |
| `CreateDamageTrack` | spawn path | equal |
| name = `<base-or-type-name>+<objectid>` | not mirrored (P703 identity only) | missing (visual) |
| `SendPlayerInfo` + `ShipStatusStart` observer broadcasts | `BroadcastResearchProduction` (P703) subset | different |

Core spawn realized and tested (`StationBucketSpawnsConstructorLiveTests.SpawnNpcShip_uses_the_drone_types_real_hull`,
`DroneBucketSpawnsDroneLiveTests`); residual: exact CFSDrone observer/drone-name ceremonies not mirrored → `partial`.

---

## Light gate
- Whole-solution build: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 90 tool projects built**.
- AOT publish (`src/Allegiance.Server`, `-r linux-x64 --self-contained -p:PublishAot=true`): **clean**, no IL2026/IL3050.
- New tests 3× alone: `P845SideDevelopmentTechChangeTests` — 3/3 passed, ×3.
- Affected existing tests (TechTreeDevelopmentLive, P691EconomyCallback, P760ServerCallbacks + P845): 32/32 passed.
- ONE fast-tier run: `tools/run-tests.sh --tier fast` — 11011 tests, 306 s. Only 2 failures are the
  pre-existing MonoGame HUD allocation load-flakes (`P836HudLevelTests.The_level_selection_paths_allocate_nothing_per_frame`,
  `P828RadarLabelDeclutterTests.The_selection_path_allocates_nothing_per_frame`) — confirmed passing 3/3
  alone, unrelated to this change (my project `Allegiance.Core.Tests` passed 5445/5445; see
  `~/allegiance-evidence/P845/fast-tier.log`).
- `python3 scripts/parity-scorecard.py`: validates clean.

## Native-intent classification
Most rows are **design** (the native FedSrv-site event handlers define real gameplay consequences:
broadcasts, spawns, stock deposits, upgrades, victory checks). The connection/transport rows (0579, 0582,
0583) are largely **era plumbing** (DirectPlay `CFMConnection`/`FedMessaging` callbacks → TCP transport)
whose gameplay-irrelevant diagnostics/metrics have no exact port. No native bugs were encountered that
would break the port.

## Modern / better-approach note
The port centralizes the formerly scattered FedSrv-site side effects in typed server components
(`Server.Economy`/`ServerHost`/`Server.Research`) with explicit hooks and model methods
(`Side.UpgradeStationsToSuccessors`, `CompleteDevelopment`, `CompleteStationType`, `CompleteDroneType`)
— an improvement over native's monolithic `FedSrv.CPP` switch. A future modernization would drive
station-successor upgrades and drone upgrades off a single "tech-changed" event (fully closing the
SIM-0636 eager-upgrade gap) and route the native `OnAppMessage` per-case audit through the existing
`FedMsgDispatchCoverageTests` registry.

## Unfinished / follow-ups
- SIM-0584 `OnAppMessage`: full per-case branch audit (large).
- SIM-0636 eager drone upgrade on tech change (currently dock-time).
- SIM-0643 build-triggered `CheckForVictoryByStationBuild` + roid-receipt fidelity.
- SIM-0770 drone-name/observer ceremony parity.
- SIM-0580/0582/0583 named-server-counter parity (metrics only).
