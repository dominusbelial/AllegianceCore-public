# P676 — parity wave 4b: simulation rules

Entry revision: `94adf66c` on `codex/wave4`. Changes remain in the working tree. No commits or pushes. Native paths below are relative to `~/Development/Allegiance/src/`, inspected locally. All network verification uses locally booted servers on ephemeral loopback ports; no rig/221/200v200 runs.

## Scorecard before → after

The baseline is the actual entry `HEAD`, rather than the historical report's main-matrix snapshot. Full credit goes to `implemented`/`done`; `partial`/`missing` receive zero; `na`/`n/a` are excluded. No weights, populations, or native rule definitions changed.

| Simulation area | Weighted before → after | Unweighted before → after | After credit/weight; complete/eligible |
| --- | ---: | ---: | --- |
| callbacks | 33.33% → 33.33% | 33.33% → 33.33% | 66/198; 22/66 |
| constants | 88.15% → 95.56% | 89.74% → 94.87% | 129/135; 37/39 |
| periodic | 6.38% → 17.02% | 6.67% → 13.33% | 8/47; 2/15 |
| sounds | 15.66% → 15.99% | 11.84% → 12.28% | 95/594; 28/228 |
| updates | 52.15% → 57.88% | 48.28% → 52.87% | 202/349; 46/87 |
| warnings | 100.00% → 100.00% | 100.00% → 100.00% | 10/10; 2/2 |
| TOTAL | 35.48% → 38.26% | 29.52% → 31.35% | 510/1333; 137/437 |

Seven requested rows close, **35 weight**: SIM-0001, SIM-0002, SIM-0111, SIM-0116, SIM-0131, SIM-0134, SIM-0155. The corresponding weapon-ready sound producer SIM-0271 also closes, **2 weight**. Total: **8 rows / 37 weight**, weighted **35.48% → 38.26%**, unweighted **29.52% → 31.35%**.

The main `parity-matrix.csv` is unchanged: **1287/1482 weight (86.84%)**, **344/410 rows (83.90%)**. SIM-0195 moves missing → partial for weapon ready text, with no score credit. Generic callbacks SIM-0178/0212/0213 and other equipment sound producers remain conservative as explained below.

## Implemented rules and evidence

### SIM-0001 / SIM-0002 — loaded map geometry

Native `Igc/mapmakerigc.cpp:364,404` reads the loaded universe radius for procedural warp placement. `Igc/common.cpp:2814-2818,2882-2884` draws asteroid positions uniformly in a sphere of radius `0.75 * loaded radius`, then scales Z by the loaded lens multiplier. The port now reads slots 1 and 0 for those consumers, replacing the fixed 5000 radius and fixed asteroid shell/lens.

Port: `src/Allegiance.Core/MapGen/MissionMapGenerator.cs:53,1035,1340`. `P676SimRulesTests.Generated_map_reads_loaded_universe_radius_and_asteroid_lens` exercises two different radii and lenses, checking every generated warp radius and every asteroid's effective ellipsoid radius. Existing boundary-damage tests continue to exercise the other loaded consumers.

The existing deterministic warp ring and topology policy remains an adaptation: this task does not implement native random warp permutation/displacement, model-clearance retries, or relayout authored file maps. Those are separate map-generation behaviors; these two scored rows concern consumption of the loaded constants.

### SIM-0111 — wingman combat lead, turn, and firing gate

Native `Igc/shipIGC.cpp:2353-2368,2463-2477,2600` uses shoot/turn skill 0.75, turns toward the solved lead, sets the base throttle from the angle, and fires only at angle ≤0.10 rad and intercept time ≤0.95 effective projectile lifespan. `Igc/common.cpp:121-254` supplies the lead and turn laws, including angular stopping drift and coupled yaw/pitch normalization; torque scaling is `Igc/shipIGC.h:542-549`.

Port: `Ship.cs:6795,6842`. The fire check uses intercept time, including target radius, relative velocity, projectile absolute/relative behavior, and the existing ammo-speed and effective-lifespan attributes. Both steering and the trigger now refer to the lead. The close band continues to use native relative-speed matching (`Igc/shipIGC.cpp:2493-2512`), activated by intercept time rather than the old geometric standoff.

Tests: `Combat_uses_native_point_one_radian_lead_gate` distinguishes 0.09/0.11/0.40 rad; `Combat_range_gate_uses_intercept_time_and_relative_speed` holds distance fixed while changing target velocity; `Native_turn_law_applies_skill_to_stopping_drift` distinguishes 0.75 and 1.0 skill. Existing `NpcWingmanLeadLiveTests`, attack, defend, and range-maintenance tests verify the actual ticking server.

SIM-0114 remains partial: this task does not claim the other range bands, boost, dodge cooldown, retarget timer, or critical-movement behavior.

### SIM-0116 — repair orders and mine-use decision

Native `Igc/shipIGC.cpp:2608-2678` requires a negative-power mount-0 gun, solves and turns at skill 1, uses the 0.10-rad / 0.95-lifespan fire gate, and releases fire on a full-health ship. Stations intentionally keep firing: native's full-health exclusion tests only OT_ship. Its close repair movement/strafe threshold is 0.25 lifespan. Mine usage requires the UseMines behavior bit, a mine dispenser, and a different-side wingman/player within strict distance <2500 (`Igc/igc.h:785-787`; `shipIGC.cpp:2651-2665`). Native deliberately does not exclude allies from that mine-nearness predicate. Deployment additionally obeys amount, mount readiness, strict load deadline, and ripcord gates (`Igc/dispenserigc.cpp:103-119`).

Port: `Ship.cs:6871,6885,6948`, plus `Server.ShipCommands.cs:1926`. A real c_cidRepair order can now arm ship or station repair through the existing command validation and acknowledgement path. Repair suppresses autonomous attack acquisition, uses the shared lead/turn law, follows cross-sector navigation, and releases its objective when the target becomes invalid. New attack/defend/goto/hold orders supersede repair.

Tests cover positive versus negative power, full versus damaged ships, firing angles, station geometry and station removal, native mine pilot/distance/load/amount behavior, and actual nanite projectiles restoring friendly hull. `LiveQuickChatToDroneCommandTests.P676_Individual_repair_orders_ship_or_station_and_affirms_over_tcp` sends both target kinds over real TCP and waits for the drone's actual “Affirmative.” receipt.

Default and defend enemy searches exclude live allies (`Igc/common.cpp:269-273`), preventing an allied neighbor from repeatedly winning acquisition and being rejected by the combat gate. `Allied_neighbor_does_not_starve_default_or_defend_enemy_acquisition` covers that case. Friendship uses the native live side alliance groups (`Igc/igc.h:4297-4305`), shared with the existing drone-retreat helper; it does not rely on the separate synthetic damage-immunity pair collection.

### SIM-0131 / SIM-0271 — weapon mounted-ready feedback

Native `Igc/weaponIGC.h:33-48` emits exactly one mounted sound and `<part name> ready.` notification when the float mounting accumulator reaches 1. The ordinal is mountedSound=1023 (`Igc/igc.h:456-464`; `Igc/sounds.h:34`), and `sounddef.mdl:287` maps it to `unmount`.

Port: `Ship.cs:3034`, `IIgcSite.WeaponReady`, `ServerHost.cs:429`, `Server.Combat.cs:514`, `Game1.Session.cs:1060`, and `WeaponReadyCue.cs`. The server bridges native local IGC feedback through the existing owner-only chat message, carrying both text and sound ID. The renderer recognizes the cue and plays the original wave. No new FMID is introduced. The original `~/Development/Artwork/unmount.ogg` is copied byte-for-byte to bundled Content/Sounds, listed in MGCB, and compiles to `Content/Sounds/unmount.xnb` in the desktop output.

Tests prove no early notification at 0.999 fraction, one at completion, no duplicate on subsequent ticks, another after remount, owner-only text/audio delivery over TCP, native enum identity, original asset byte identity, and its content pipeline entry. Existing P651 tests continue to prove readiness blocks actual firing.

Other equipment ready producers remain missing/partial: bundling the shared asset does not implement their conditions. Generic `PlayNotificationSound` and `PostNotificationText` rows retain partial status; SIM-0195's missing → partial change records only the new weapon text path.

### SIM-0134 — payday policy

Native `FedSrv/fsmission.cpp:2330,2514-2552` runs DoTick payday policy only in Started, advances the armed phase, and gates both income accrual and payout on bAllowDevelopments. Income remains loaded baseline ×(1+civilization income) + current station income.

Port: `Mission.cs:90,654`, `GameConfig.AllowDevelopments`, and mission assembly/rotation in `Server.Lifecycle.cs`. Starting continues to simulate, but cannot arm or fire periodic payday. Disabled-development payday boundaries still advance phase; enabling developments does not grant skipped income. The separate initial starting-money payout is preserved.

Tests cover Starting/Started/Over, developments on/off, no early arming in Starting, disabled cadence without backpay, loaded cadence/formula, real starting money, captured station income, and real per-side wire payouts (`PaydayTests`, `StartingMoneyStationIncomeLiveTests`).

### SIM-0155 — countdown revalidation and alternate site behavior

Native `Igc/shipIGC.cpp:270-292` calls ContinueRipcord each tick; a rejected continuation clears the anchor. At zero, true UseRipcord consumes the attempt; false retains an active zero countdown and retries. Native server policy checks carried flag, current anchor side/cluster, bAllowAlliedRip and live alliance, and the current anchor hull's normal/light capabilities (`FedSrv/FedSrv.CPP:13379-13437`; `Igc/shipIGC.cpp:3900-3906`).

Port: `Sector.cs:863,1410,1432`. The existing native site callbacks now receive resolved actual models. Direct sites obey true/false results, including zero-time retry. `QueueRipcordUse` explicitly selects the existing server/bare-domain completion drain, retaining server teleport, energy, and wire handling. Countdown enumeration uses a snapshot so a direct callback can relocate/remove the ripper safely. A boarded ship remains frozen.

Anchors are resolved by identity every tick; a carrier changing sector is followed. A lost alliance, switched allow-allied policy, acquired flag, absent/docked/dead anchor, or reduced hull capability aborts. A light-only anchor is valid only for a light-capable ripper. Probe activation/expiry horizon remains a selection-time check; native ContinueRipcord checks a surviving non-ship anchor's side and cluster, rather than incorrectly requiring a fresh full countdown horizon every tick (`FedSrv.CPP:13400-13421`). Allied selection uses the same live alliance groups as validation (`Igc/igc.h:4297-4305`).

Tests cover the alternative native callbacks, deferred retry/consume/abort, callback relocation, alliance loss and bAllowAlliedRip, acquired flag, reduced hull/light anchors, and moving carrier identity. Existing real ripcord/probe/boarded-freeze/countdown-HUD suites remain in the affected set.

`Sector.Update` is **byte-for-byte unchanged** against entry HEAD. No swept collision ordering or SIM-0063 work was attempted. All sector edits are confined to the ripcord helpers. Generic callback rows SIM-0212/0213 retain partial status for the separate callback architecture audit; their evidence now describes the executable direct-site path and explicit queued server adapter.

## Fixture corrections

The existing P171 live range fixture used SetForward(+X) without rebuilding right/up, leaving a degenerate frame; native turn-to-lead needs a complete orientation. It now uses the complete Set(forward,up) and resets turn rates before each prescribed-velocity measurement. Its closing/backoff and fleeing/forward assertions are unchanged.

The P162 attack fixture placed a large target only 420 units away and assumed “outside standoff” required pursuit. Native intercept time includes target radius; that target was already in the native close band, where a stationary target is fired on at rest. The fixture now starts beyond projectile range plus target radius. It retains actual enemy acquisition, real damage, trigger, and distance-reduction assertions.

## Verification

`NUGET_HTTP_CACHE_PATH=/tmp/p676-nuget-http dotnet restore AllegianceCore.slnx` succeeded with package auditing enabled. The final normal `dotnet build AllegianceCore.slnx --no-restore -m:1` completed with **0 warnings / 0 errors**, including the desktop content pipeline. All source/test changes preceded this final build and the final suites; only documentation/evidence was edited afterward.

**147 affected methods / 227 distinct data cases**, including **40 new cases**, were exercised by three sequential exact-method isolated invocations. Theory methods execute all data rows on each invocation. The final build additionally reran the changed AI and all new rules: Core 52 cases, ClientCore 3 cases, MonoGame 4 cases, followed by their exact-method triplets. Failed timing-sensitive cases received clean final triplets after all three final assemblies finished.

The final suites run sequentially, unfiltered, using `dotnet test tests/<assembly> --no-build --no-restore --logger trx --results-directory /tmp/p676-results`. ClientCore sets `ALLEGIANCE_LIVE_P660_SHOWCASE=1`.

| Assembly | Passed | Failed in full pass | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Core | 4352 | 2 | 0 | 4354 |
| ClientCore | 945 | 1 | 0 | 946 |
| ClientCore.MonoGame | 2151 | 0 | 3 | 2154 |

The three skips are the existing `MeshyPackTests` optional private-pack cases. Native source, artwork, required TCP/loopback tests, and the opt-in MCP showcase executed.

The final 15v15, 90-second local MCP battle uses an ephemeral loopback server (`ALLEGIANCE_LIVE_P632_ASSERT=1`, `ALLEGIANCE_P632_SIZE=15`, `ALLEGIANCE_P632_SECONDS=90`, `ALLEGIANCE_P632_DEBUG=1`). **30/30 bots reached; 5 kills (4 / 1); 12,823 shots; 4,551 hits; 0 friendly hits.** `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights` passed **3/3 alone** on the final build: real purchase, damage, production death/pod docking, own MCP repurchase/relaunch, and damage again. The full ClientCore showcase also passed: all 30 bots migrate, both humans stay connected, and the finale rearms.

**Result: verified under the brief's 3/3-alone flake rule.** Every observed live receipt failure has a final clean three-run triplet. No deadlines, assertions, or quarantine traits were relaxed for these failures:

| Test | Observed run categories | Final isolated retry |
| --- | --- | --- |
| `Allegiance.ClientCore.Tests.ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | final | 3/3 passed |
| `Allegiance.Core.Tests.LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | full | 3/3 passed |
| `Allegiance.Core.Tests.LiveRipcordTests.Re_target_activates_to_requester_only` | exploratory | 3/3 passed |
| `Allegiance.Core.Tests.LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | alone | 3/3 passed |
| `Allegiance.Core.Tests.LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` | final | 3/3 passed |
| `Allegiance.Core.Tests.SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` | final | 3/3 passed |

The corresponding ledger entry in `docs/TEST_FLAKES.md` records their symptoms and receipts. The schema/scope checks also pass: P642 validates 231 audited / 438 main rows; the simulation CSV has 473 unique IDs and 16 fields per row; populations, weights, and native rule definitions remain unchanged; `Sector.Update` matches the entry tree byte-for-byte.

Durable per-invocation counts, exact filters, phases, failures/retries, battle receipt, build and scope evidence: [P676_RESULTS.json](P676_RESULTS.json). Every invocation ran real tests (no empty filter matches). TRX and console artifacts are under `/tmp/p676-results`; the candidate and final phases use distinct artifact names.

## Matrix changes

The seven requested rows and SIM-0271 move to implemented. SIM-0195 moves missing → partial. Evidence-only refreshes: SIM-0114, SIM-0178, SIM-0212, SIM-0213, SIM-0234, SIM-0237, SIM-0238, SIM-0239, SIM-0240. Those five non-weapon sound rows now correctly record the bundled shared asset while retaining their missing producer status. In total, 18 simulation rows change; the main matrix is untouched.
