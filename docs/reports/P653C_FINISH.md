# P653c — finish economy/loadout validation

2026-10-14. Working-tree changes on `26ee06b2` (`wip/p653b`); historical control `32ceeccd`. No commits or pushes.

## Outcome

**All 54 baseline failures pass on the final tree.** The final unfiltered runs leave one Core failure, `ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count`, which passes **3/3 individually after all full runs finish**. ClientCore and MonoGame have zero failures. This meets the requested remaining-load-flake target; the Core full run itself is not claimed green. Build: **0 warnings, 0 errors**. All 26 audit cases pass here and fail on master’s behavior.

Durable per-case results, skipped-test names, historical control, and individual isolation outcomes: [P653C_RESULTS.json](P653C_RESULTS.json).

## Build and reproduction

Load the gitignored workstation `local.env` (artwork and native source are present), then:

```sh
set -a
source ./local.env
set +a
dotnet build --artifacts-path bin/p653c-final -m:8 -v:m
dotnet test tests/Allegiance.Core.Tests --no-build --artifacts-path bin/p653c-final
dotnet test tests/Allegiance.ClientCore.Tests --no-build --artifacts-path bin/p653c-final
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --artifacts-path bin/p653c-final
```

Final normal build: **0 warnings, 0 errors** (`/tmp/p653c-build-final.log`). Full suites include the LiveTiming lane and live sockets; no filters, skips, or quarantines were added. Existing MonoGame skips are reported separately.

## Production corrections

- H-18 now distinguishes a registered building effect from P652’s idle `IsBuilding` reservation. Repeated orders for the committed rock are idempotent, a different rock cannot consume the same constructor again, and NPC constructors travel before drilling. Sector-level constructor/rock exclusivity and abort-on-gone-builder remain intact. Player builders retain their immediate effect path.
- H-06 now docks built-in real-map and generated debug spawns, including the MCP initial purchase flow. Delegate identity distinguishes those production policies from an explicitly replaced test placement hook. Requested-side reconciliation happens before docking. No in-flight purchase exemption was added.
- H-09 retains one complete final publication through launch, including refilled pack quantities. At the LOADOUT_CHANGE wire boundary, fully armed runtime 255 becomes native BytePercentage(1) **240** (`Utility.h:1311-1327`, `shipIGC.h:819`). The original session/shop wire assertions were legitimate and are retained. The audit now checks one publication, native fraction bytes with fLaunch, and quantities equal to the ship’s actual final inventory.
- Other audit gates stay in force: no free spawn refunds, bounded ammo/launcher/pack quantities, native per-slot mounts, one final loadout publication, bounded decoder tail, flying inventory operations, privileged/child suicide, and consumed treasure offers.

## Native justification for category (b)

These references are to the local native checkout and the corresponding finding in `docs/reviews/HOSTILE_INPUT_AUDIT.md`:

| Finding | Native behavior retained |
| --- | --- |
| H-03 | `FedSrv.CPP:2426-2443, 6182-6207, 7079-7120`: hardware refund/value differential belongs to the player; free spawn hardware cannot mint hull/treasury value. Launcher price is per remaining round (`magazineigc.h:85-88`, `dispenserigc.h:86-89`); packs have zero resale (`packigc.h:80-83`). |
| H-06 | `FedSrv.CPP:6153-6172`, `shipIGC.h:992-1000`: buy alive/unboarded at a real dock with station tech checks. Initial station entry: `fsmission.cpp:791-796`. |
| H-07 | `hullTypeIGC.h:199-213`, `shipIGC.h:1029-1031`: weapons require the requested compatible hardpoint; non-weapons use mount zero; cargo is -1 through -5; no duplicate occupied slots. |
| H-08 | `shipIGC.h:597-601`, `packigc.h:102-123`, `launchertypeigc.h:130-145`, `magazineigc.h:107-113`: ammo and part amounts obey the hull’s actual capacities. |
| H-09 | `FedSrv/fsship.cpp:694-735`: publish the final loadout once. `FedSrv.CPP:13183-13191` → `stationIGC.cpp:449-459` arms equipment at undock, but native `BytePercentage(1)` is **240**, not the port’s runtime 255. Keep the final batch through launch/refill and translate fully mounted runtime 255 to native wire 240 during LOADOUT_CHANGE serialization. |
| H-18 | `FedSrv.CPP:7281-7322`: native drone commands separate launch/return/stay-docked; no unlimited direct build path. Constructor arrival: `shipIGC.cpp:2810-2827`; P652’s accepted travel behavior remains. |
| H-22 | `FedSrv.CPP:6485-6498` and the audit’s wire inventory section: legal flying source/destination slots, including native negative cargo mounts. |
| H-26 | `FedSrv.CPP:6338`: child or CanCheat suicide; a normal pod/pilot cannot use suicide as a free respawn. |

## Baseline — every observed failure

The initial checkout had no build output. An initial `--no-build` command therefore ran no tests; it is not counted. After the clean baseline build, all three actual suites ran sequentially with `--no-build`, artwork/native paths loaded, and TRX logging.

| Assembly | Failed | Passed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Core | 35 | 4144 | 0 | 4179 |
| ClientCore | 13 | 855 | 0 | 868 |
| ClientCore.MonoGame | 6 | 1963 | 3 | 1972 |

This is **54 baseline failures**: the prior report’s 49 MAIN failures, its additional slow-hull quarantine failure, and four observed Core load/fixture failures (two station alerts, pod-order role race, quantized ore receipt). The four additional cases are accounted for below. Full names and original errors remain in `/tmp/p653c-results/{assembly}-baseline.trx` and `/tmp/p653c-{assembly}-baseline.log`.

### Core

| Failure (namespace `Allegiance.Core.Tests`) | a/b → fix | Evidence |
| --- | --- | --- |
| `AsteroidsUpdateLiveTests.Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full` | **a; fixture timing** — Wait for a lower encoded ore value, not merely two packets that can carry identical quantized ore. | Passed in final full suite |
| `BuyLoadoutPricingLiveTests.Downgrade_Refunds_Buyer` | **b; H-03/H-06/H-08** — Buy docked with explicitly sellable shop records; launcher cost is unit price times bounded rounds, and only paid hull credit trades in. Strengthen the unfunded leg to reach the funds gate at a valid dock. | Passed in final full suite |
| `BuyLoadoutPricingLiveTests.Funded_Paid_Purchase_Charges_Exact_Delta_And_Broadcasts_SideWide` | **b; H-03/H-06/H-08** — Buy docked with explicitly sellable shop records; launcher cost is unit price times bounded rounds, and only paid hull credit trades in. Strengthen the unfunded leg to reach the funds gate at a valid dock. | Passed in final full suite |
| `ClusterWarningLiveTests.Enemy_in_friendly_station_sector_warns_friendly_side_only` | **b; H-06** — Launch both initial docked seats before moving the enemy into the defended sector. | Passed in final full suite |
| `CombatLogVolumeTests.Combat_log_tags_are_silent_gate_off_and_present_gate_on` | **b; H-08** — Use an ammo/energy-capable combat hull for the sustained-fire logging workload. | Passed in final full suite |
| `DockAutoRestockLiveTests.Docking_rebuys_paid_desired_launcher_and_charges_the_native_amount` | **b; H-07/H-08** — Use a compatible hull; fund and assert launcher price per clamped round. Use P550’s legal CapLand home for a real collision-driven capital dock, retaining real restock and money-change delivery. | Passed in final full suite |
| `DockAutoRestockLiveTests.Docking_upgrades_mounted_part_to_successor_and_charges_after_research` | **b; H-07/H-08** — Use a compatible hull; fund and assert launcher price per clamped round. Use P550’s legal CapLand home for a real collision-driven capital dock, retaining real restock and money-change delivery. | Passed in final full suite |
| `DropPartTests.DropPart_drops_weapon_sectorwide_with_treasure_then_empty_pack_skips` | **b; H-06/H-07/H-22** — Prepare legal flying inventory directly; the empty pack uses equipment mount zero. Keep all wire/drop/treasure assertions. | Passed in final full suite |
| `HullSwapLiveTests.BuyLoadout_with_a_different_hull_swaps_the_server_simulated_physics_live` | **b; H-06/H-07** — Use an explicit docked shop fixture with sellable player hulls/parts and check the actual requested hardpoint. | Passed in final full suite |
| `HullSwapLiveTests.Hull_change_into_an_incompatible_hull_drops_nonmountable_parts_live` | **b; H-06/H-07** — Use an explicit docked shop fixture with sellable player hulls/parts and check the actual requested hardpoint. | Passed in final full suite |
| `LiveBoardShipTests.Board_with_equipped_turret_and_leave` | **b; H-03** — Explicit paid-hull fixture; boarding refunds the personal wallet, never the side treasury. | Passed in final full suite |
| `LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side` | **b; H-03** — Explicitly model an already-paid boarder hull; assert the side-wide message credits its personal wallet and leaves the treasury unchanged. | Passed in final full suite |
| `LiveP393DebugRespawnTests.Normal_game_death_respawn_still_hits_the_pre_p393_real_station` | **b; H-06/H-26** — Launch initial docked seats and authorize the named death-fixture connection through the existing CanCheat predicate. | Passed in final full suite |
| `LiveP393DebugRespawnTests.Side0_death_respawns_at_omega_debug_garrison_not_ganymede` | **b; H-06/H-26** — Launch initial docked seats and authorize the named death-fixture connection through the existing CanCheat predicate. | Passed in final full suite |
| `LiveP393DebugRespawnTests.Side1_death_respawns_at_plato_debug_garrison_not_organia` | **b; H-06/H-26** — Launch initial docked seats and authorize the named death-fixture connection through the existing CanCheat predicate. | Passed in final full suite |
| `LiveTeleportDockTests.Teleport_with_non_buyable_hull_refunds_strips_and_swaps_to_lifepod` | **b; H-03** — Buy a controlled paid gun through the real docked wire path; refund that gun once to the personal wallet, with no free-hull or treasury credit. | Passed in final full suite |
| `LiveTeleportProbeRipcordTests.Most_recently_deployed_teleport_probe_wins_without_goal` | **b; H-07/H-08** — Replace the depleted launcher before remounting; do not depend on duplicate dispensers and excess old rounds. | Passed in final full suite |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | **b; H-07** — Replace mount 200 (signed -56, outside native cargo) with real weapon mount zero. | Passed in final full suite |
| `LoadoutRoundTripTests.BuyLoadout_WithRealHullAndWeapon_RoundTrips` | **b; H-06/H-07** — Verify the chosen weapon fits hardpoint zero and explicitly sell it at the real initial dock; retain byte-exact ACK checks. | Passed in final full suite |
| `MissileCombatTests.Missile_and_weapon_projectile_coexist_in_one_collision_pass` | **b; H-08** — The weapon shooter used a zero-ammo target/lifepod hull. Give only the shooter a one-round capacity; preserve both exact damage assertions. | Passed in final full suite |
| `P402DebugMapGenArenaLiveTests.GeneratedBrawlDebugMode_DeathRespawnsAtOwnGeneratedStation` | **b; H-26** — Give the named death-fixture connection CanCheat and put the pod in flying state before suicide. | Passed in final full suite |
| `P467E2eGunDamageDevLiveTests.Completing_dev_143_raises_gun_damage_in_combat` | **b; H-08** — Give the weapon shooter one round of hull ammo capacity; keep the decoded weapon/damage calculation. | Passed in final full suite |
| `P470RemovePlayerFlyableBuilderTests.A_normal_player_hull_is_still_buyable` | **b; H-06** — Purchase at a real dock; explicitly make the chosen normal hull sellable. The constructor refusal remains covered. | Passed in final full suite |
| `P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` | **a; fixture race** — Seat the pod before the rescuer: asynchronous logon order previously swapped the two peer roles. Dispose the scoped server/bots. | Passed in final full suite |
| `P616TrackerNativeBugTests.AlephTransit_ChangesNeitherFuelNorCargo` | **b; H-07/H-22** — Place spare fuel packs directly in native negative cargo mounts instead of positive equipment slots 1/2. | Passed in final full suite |
| `P616TrackerNativeBugTests.FuelAutoReload_EachRefillCostsExactlyOnePack_NeverFreeFuel` | **b; H-07/H-22** — Place spare fuel packs directly in native negative cargo mounts instead of positive equipment slots 1/2. | Passed in final full suite |
| `P626InFlightUtilityLiveTests.DropCargo_wire_drops_cargo_part_as_treasure_and_broadcasts` | **b; H-06/H-22** — Launch the initially docked subject before wire cargo swap/drop; use weapon mount zero. | Passed in final full suite |
| `P626InFlightUtilityLiveTests.ExportInventoryForAck_includes_cargo_with_negative_mounts` | **b; H-06/H-22** — Launch the initially docked subject before wire cargo swap/drop; use weapon mount zero. | Passed in final full suite |
| `P652ConstructorBuildTests.Accepted_build_reaches_rock_even_with_reserved_building_flag(reserved: False)` | **a; H-18** — Distinguish an idle reservation from a registered construction, keep repeated accepted orders idempotent, and restore NPC travel before drilling. The bucket test also launches its waiting constructor before BUILD (b; H-18). | Passed in final full suite |
| `P652ConstructorBuildTests.Accepted_build_reaches_rock_even_with_reserved_building_flag(reserved: True)` | **a; H-18** — Distinguish an idle reservation from a registered construction, keep repeated accepted orders idempotent, and restore NPC travel before drilling. The bucket test also launches its waiting constructor before BUILD (b; H-18). | Passed in final full suite |
| `P652ConstructorBuildTests.Bucket_spawned_constructor_builds_after_waiting_at_home` | **a; H-18** — Distinguish an idle reservation from a registered construction, keep repeated accepted orders idempotent, and restore NPC travel before drilling. The bucket test also launches its waiting constructor before BUILD (b; H-18). | Passed in final full suite |
| `StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window_produces_no_second_alert` | **a; fixture race** — Bind both ship and peer to the actual assigned side, rather than assuming connection order. Preserve all station-alert assertions; all three cases pass repeated isolation runs. | Passed in final full suite |
| `StationUnderAttackAlertLiveTests.Sub_30_percent_fraction_produces_critical_alert_once_throttle_reopens` | **a; fixture race** — Bind both ship and peer to the actual assigned side, rather than assuming connection order. Preserve all station-alert assertions; all three cases pass repeated isolation runs. | Passed in final full suite |
| `SwapPartTests.SwapPart_swaps_two_occupied_slots_sectorwide_moves_and_silent_noop` | **b; H-06/H-07/H-22** — Prepare flying inventory on a real hull accepting both guns at all three tested hardpoints; exercise the real SWAP_PART wire. | Passed in final full suite |
| `TeammateLoadoutBroadcastTests.Teammates_real_loadout_is_broadcast_to_observers_but_enemy_loadout_is_not` | **b; H-06/H-07** — Buy the explicitly sellable real weapon from the initial hangar with fLaunch; wait for an export containing the part. | Passed in final full suite |

### ClientCore

| Failure (namespace `Allegiance.ClientCore.Tests`) | a/b → fix | Evidence |
| --- | --- | --- |
| `ChaffDropLiveTests.SendFireExpendable_DeploysChaffAndDecrementsAmmo_OverTheWire` | **b; H-08** — Use real hull capacity for three chaff units; preserve 3-to-2 depletion. | Passed in final full suite |
| `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | **a; H-06** — Fix production initial docking for built-in debug spawners, allowing the existing real docked BUY_LOADOUT(fLaunch) to succeed. Both warp laws remain unchanged. The legacy proof now checks every sample against decoded warp-plus-hull transit radius and requires remaining in the starting sector; its old arbitrary 150-unit margin falsely rejected a valid 128-unit stop outside the 115-unit volume. | Passed in final full suite |
| `ClientWarpApproachSlowHullLiveTests.Slow_board_hull_26_transits_when_the_warp_approach_flies_through_the_aleph` | **a; H-06** — Fix production initial docking for built-in debug spawners, allowing the existing real docked BUY_LOADOUT(fLaunch) to succeed. Both warp laws remain unchanged. The legacy proof now checks every sample against decoded warp-plus-hull transit radius and requires remaining in the starting sector; its old arbitrary 150-unit margin falsely rejected a valid 128-unit stop outside the 115-unit volume. | Passed in final full suite |
| `CrossSideCombatVisibilityLiveTests.CrossSide_WatcherSeesOpponent_Projectile_And_Missile_InRealTime` | **b; H-08** — Use an ammo-capable hull with real magazine capacity; preserve both sides’ projectile/missile visibility assertions. | Passed in final full suite |
| `CrossSideCombatVisibilityLiveTests.SameSide_pairStillSeesProjectileAndMissile` | **b; H-08** — Use an ammo-capable hull with real magazine capacity; preserve both sides’ projectile/missile visibility assertions. | Passed in final full suite |
| `DockLoadoutLiveTests.BuyLoadout_WithRealParts_PopulatesShipLoadout_Live` | **b; H-06/H-07** — Use an explicit initial hangar, real compatible gun hull, and sellable selected weapon. | Passed in final full suite |
| `ExplosionLiveTests.RealKill_BroadcastsExplosionEvent_ReceivedByPeerWorld` | **b; H-26** — Authorize the named death-fixture connection through CanCheat; preserve real kill/explosion delivery. | Passed in final full suite |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | **a; H-09; b; H-07** — Use compatible docked inventory. Serialize the final post-launch inventory with native full-mount 240 in both the ACK and the client world. | Passed in final full suite |
| `FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill` | **a; H-09; b; H-06/H-07/H-08** — Buy docked and launch; use an ammo-capable gun hull, retain native mount byte 240 in both ACK and completed-purchase broadcast, and stop both ships in a clear firing lane for the real cross-side kill. | Passed in final full suite |
| `MissileFireLiveTests.SendFireMissile_LaunchesHomingMissileAndDecrementsAmmo_OverTheWire` | **b; H-08** — Choose a hull with actual capacity for three decoded missiles, retaining the 3-to-2 ammo assertion. | Passed in final full suite |
| `P554AdminCheatLiveTests.Mcp_arena_against_a_refusing_server_returns_an_explicit_error` | **b; H-06** — Enable the explicit MCP test-harness money/tech grant so the arena-refusal test reaches the intended disabled-admin relocation check after buying legally. | Passed in final full suite |
| `SuccessorUpgradeLiveTests.Buy_loadout_mounts_the_successor_part_when_the_side_has_outteched_it_Live` | **b; H-07** — Use a real weapon-compatible hull in the docked purchase fixture; keep successor selection and wire assertions. | Passed in final full suite |
| `SuicideLiveTests.SendSuicide_KillsOwnShip_OverTheWire` | **b; H-26** — Authorize the named death-fixture connection through the existing CanCheat predicate. | Passed in final full suite |

### ClientCore.MonoGame

| Failure (namespace `Allegiance.ClientCore.MonoGame.Tests`) | a/b → fix | Evidence |
| --- | --- | --- |
| `CommandedDroneBuildKeyLiveTests.Live_client_commands_builder_drone_to_build_creating_real_station` | **a; H-18** — Restore NPC accepted-order travel and allow idle reservations; keep actual-construction, occupied-rock, docked-target and commander guards. | Passed in final full suite |
| `CommandedDroneBuildKeyLiveTests.Live_client_illegal_drone_build_refusal_reaches_client_chat` | **a; H-18** — Restore NPC accepted-order travel and allow idle reservations; keep actual-construction, occupied-rock, docked-target and commander guards. | Passed in final full suite |
| `P394ShopPreSeedLiveTests.EquippedShield_UntouchedPreseeded_SurvivesRelaunch_Live` | **a; H-09; b; H-07** — Use a hull accepting the selected shield; fix publication timing and preserve the original native full-mount 240 assertion and preseed/toggle behavior. | Passed in final full suite |
| `P394ShopPreSeedLiveTests.PreSeededPart_ToggledOff_DropsOnRelaunch_Live` | **a; H-09; b; H-07** — Use a hull accepting the selected shield; fix publication timing and preserve the original native full-mount 240 assertion and preseed/toggle behavior. | Passed in final full suite |
| `ShopLauncherBuyLiveTests.ShopLauncherPurchase_GrantsTheRealLauncher_Live` | **a; H-09** — Fix native fraction serialization at the final batch boundary; preserve the original BytePercentage full-mount assertion (240). No expectation was relaxed to accept runtime 255. | Passed in final full suite |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | **b; H-06/H-07/H-08** — Purchase the compatible kit docked and launch; assert bounded ammo and capture the real missile-created event even when collision removes a one-round missile during the same tick. | Passed in final full suite |

## Audit effectiveness: positive and historical negative controls

`P653EconomyLoadoutIntegrityTests` retains **26 cases**, covering H-01, H-03, H-06, H-07, H-08, H-09, H-20, H-22, H-18, H-26 and H-30. The fixed tree passes all 26; the in-checkout verification also passes both repository guards (the 26 audit cases plus both guards are included in `/tmp/p653c-Core-wire-final.log`). No audit test is skipped.

Historical negative control: archived `32ceeccd` source into `/tmp/p653c-master`, copied the final audit test file, and supplied only source-compatibility declarations absent on master (`RefundableHullValue`, a default-false `FreeSpawn`, a no-op `MarkSpawnLoadoutFree`, and identity `ClampPartAmount`). These members are not used by master’s handlers and add no validation or economy behavior. **All 26 cases fail against master’s behavior**, 0 pass, 0 skip (`/tmp/p653c-results/master-audit.trx`). The H-30 unplaced-ack case exposes master’s unsafe null dereference; the other negative controls fail their security assertions.

Two originally weak tests passed against master and were strengthened: free spawn boarding now checks that the side treasury also stays unchanged; docked-pod recovery first attempts the same purchase while flying and proves it is refused. H-18 tests explicitly reach the asteroid before checking committed construction and also assert that docked/nonleader/occupied-rock orders never set an accepted target. Thus travel-before-drill cannot make the audit’s rejection checks vacuous.

## Live MCP proof

Added `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights`. It boots the real built-in debug arena and real `BotManager`, buys with fLaunch, checks the granted hull/parts, observes actual projectile damage, drives production death and pod docking, waits for the MCP lifecycle’s own BUY_LOADOUT/fLaunch, then observes damage again. The test does not inject a flying spawner or exempt purchases from H-06. It passed three final isolated runs (alongside the full-session real-kill test; `/tmp/p653c-mcp-final-{1,2,3}.log`), and is included in the clean final ClientCore suite. Existing P657/P658 and constructor suites remain part of the full runs.

## Additional diagnostic failures and stability checks

An intermediate test adjustment accepted 255 in the client world after launch. Direct native inspection showed that was incorrect: `ExpandedPartData.fractionMounted` is a `BytePercentage` with divisor 240. Those expectation changes were reverted. Final LOADOUT_CHANGE serialization was fixed instead, preserving the original legitimate wire assertions and the actual post-launch quantities. The batch intentionally spans launch because launch can refill packs. The final audit now covers native full-mount bytes with fLaunch and still verifies only one publication. Final broad focused verification is recorded in `/tmp/p653c-*-wire-final.log`; the historical control still fails 26/26.

The previously quarantined legacy slow-hull case also reached its real geometry assertion once the initial docked purchase worked. A measured 128-unit stop is outside the decoded 115-unit transit volume, but the old hard-coded assertion demanded 150 units. The test now records the minimum clearance against each actual transit sphere and asserts positive clearance plus no first-hop transit; the production approach laws are unchanged. Both slow-hull tests pass three isolated class runs.

| Additional failure | Classification and final correction | Evidence |
| --- | --- | --- |
| `PrivatePathGuardTests.NoTrackedFileUnderSourceDirsContainsAnAbsoluteHomePath` | Harness path: the temporary artifact directory has no checkout ancestor. No test/product change. | Both repository guards pass from normal output in the 28-test audit/guard run. |
| `MeshyGuardTests.NoTrackedMeshyOutputRemainsAnywhere` | Same artifact-path lookup issue. No filtering, quarantine, or guard weakening. | Same normal-output verification. |
| `AddPartTests.Second_part_touch_while_offer_pending_destroys_but_does_not_reoffer` | a; existing arrival-order fixture race. Seat peers sequentially so `bots[0]` is always the actual picker. | Entire six-test AddPart class passes 3/3 after the fix. |
| `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` | a; existing arrival-order fixture race. Seat the watcher before connecting the warper. | The full three-test ship-delete class passes 3/3 after the fixture fix. |

The station-alert / pod-order / ore / missile-combat / audit group passed **35/35 three consecutive times** after synchronization fixes. A later contended run exposed the station fixture’s remaining peer-reference role race; after binding peers by actual side, its three alert cases pass 3/3 class runs. The final broad affected sets pass **Core 118/118, ClientCore 30/30, MonoGame 6/6**. No audit or damage assertion was relaxed. The original deterministic `MissileCombat` failure was its gun-shooter’s zero-ammo hull, not a defect requiring changes to missile collision/damage math.

The three unchanged MonoGame skips are optional private asset-pack checks (`MeshyPackTests.Pack_catalogue_has_the_full_meshy_set`, `.Pack_catalogue_merge_yields_identical_resolution_to_before`, `.Pack_built_content_directory_exists`), because `ALLEGIANCE_MESHY_PACK_PATH` is not configured. Artwork/native tests and live tests run normally.

## Validation details

The first final MonoGame invocation ended without a summary or TRX file. It is **incomplete, not counted as passing** (`/tmp/p653c-MonoGame-incomplete.log`); no cause is claimed from that missing output. The assembly was rerun alone with its child-process exit code explicitly captured (`/tmp/p653c-mono-exit.txt`).

Logs and per-case TRX files are under `/tmp/p653c-results` and `/tmp/p653c-*.log`. Core and ClientCore final measured suites use `--artifacts-path bin/p653c-final`, a fixed ignored build snapshot **inside** the checkout, so source/asset guards resolve the real repository. The final measured build uses the same argument. Normal build output was also refreshed on the final tree (0 warnings/errors, `/tmp/p653c-build-normal-final.log`). MonoGame’s final completed rerun uses normal output, the same final source tree, and `--no-build`.

- Focused reconciliation reduced Core’s affected set to 0 failures; the full audit assertions and original missile/projectile damage assertions remain.
- Each complete run executes assemblies sequentially. An earlier snapshot run overlapped the beginning of the final run, and other machine load may also contend; the final residual failure was rechecked 3× individually after those runs finished. No suite is called clean by ignoring an observed failure.
- An intermediate diagnostic build used `--artifacts-path /tmp/p653c-build` to preserve the baseline binaries while the original suites were running. Its two repository guards correctly rejected an assembly path outside any checkout (`could not locate the repo root`); both pass from normal build output. Those harness failures were not quarantined or hidden. This intermediate run also predates the final session/fixture synchronization corrections and is not the final-tree pass claim.
- A first concurrent isolation group caught the existing pod-role and encoded-ore fixture races. Those two tests now seat peers deterministically / wait for the asserted ore change; no deadline inflation or quarantine was needed. Station alert assertions remain unchanged; their bot references now follow the actually assigned sides.

## Final suite numbers

| Assembly | Failed | Passed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Core | 1 | 4178 | 0 | 4179 |
| ClientCore | 0 | 869 | 0 | 869 |
| ClientCore.MonoGame | 0 | 1969 | 3 | 1972 |

### Individual timing checks after all full runs

| Case (`Allegiance.Core.Tests`) | Passed individually | Final full-suite result | Classification |
| --- | ---: | --- | --- |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | 3/3 | Passed | load/receipt-window flake; no product or test change |
| `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another` | 2/3 | Passed | pre-existing Lane=LiveTiming quarantine, already documented in TEST_FLAKES; not introduced or reclassified here |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | 3/3 | Passed | load/receipt-window flake; no product or test change |
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | 3/3 | Passed | load/receipt-window flake; no product or test change |
| `LiveWinConditionTests.Board_refused_once_mission_over` | 2/3 | Passed | pre-existing Lane=LiveTiming quarantine, already documented in TEST_FLAKES; not introduced or reclassified here |
| `ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count` | 3/3 | Failed | load/receipt-window flake; no product or test change |

The two existing quarantine cases above remained isolation-flaky (2/3 each) but passed the final full suite. No claim is made that those pre-existing quarantines became deterministic. The **only final full-suite failure** passed 3/3 individually. No new quarantine traits were added, and no isolation result was discarded.

### Complete intermediate full-run inventory

The temporary-artifact diagnostic run was not a final-tree green run. Every failure it reported is listed here; names also appearing above refer to that earlier snapshot of the test fixture. Source/asset lookup failures are caused by the same missing checkout ancestor as the two Core guards.


Snapshot final1:

- Core: `AddPartTests.Second_part_touch_while_offer_pending_destroys_but_does_not_reoffer`; `MeshyGuardTests.NoTrackedMeshyOutputRemainsAnywhere`; `PrivatePathGuardTests.NoTrackedFileUnderSourceDirsContainsAnAbsoluteHomePath`.
- ClientCore: `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`; `FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill`; `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`.
- ClientCore.MonoGame: `EnhancedVfxTests.Cc0_atlases_ship_in_game_content_with_expected_grids`; `HullModelResolverTests.Embedded_ship_assets_match_P287_manifest`; `P547ShaderTests.Every_effect_parameter_is_set_from_code`; `P547ShaderTests.Mgfxo_staleness_each_committed_shader_matches_its_source_hash`; `P625bActionMapGuardTests.NoRawKeyLiteralsOutsideAllowList_InGameInputFiles`; `SkyHdManifestValidationTests.Every_environment_has_source_subject_url_licence_and_credit`; `SkyHdManifestValidationTests.Globe1_background_is_the_Veil_Nebula_not_the_Whirlpool_Galaxy`; `SkyHdManifestValidationTests.Globe3_background_is_the_Soul_Nebula_not_Supernova_1987A`; `SkyHdManifestValidationTests.Manifest_exists_and_has_exactly_10_environments`; `SkyHdManifestValidationTests.Planets_entry_is_native_art_not_a_NASA_body_list`; `StationAttackVoiceTableTests.Every_table_stem_is_a_bundled_asset`; `WeaponSoundMapTests.Every_singleShot_table_stem_is_a_bundled_asset`.

Snapshot final2:

- Core: `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`; `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`; `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`.
- ClientCore: `ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector_WatcherLatchesGone_OwnPeerNotHidden`.
- ClientCore.MonoGame: no failures.

Snapshot final3:

- Core: `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`; `LiveWinConditionTests.Board_refused_once_mission_over`; `StationUnderAttackAlertLiveTests.Defending_side_gets_under_attack_alert_attacker_side_hears_nothing`.
- ClientCore: no failures.
- ClientCore.MonoGame: no failures.

No commits or pushes were made. Changes remain in the working tree.
