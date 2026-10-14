# P649 — combat blast parity

Date: 2026-10-14. Worktree: `codex/p649`, base `22d024b8`. Reviewed and completed the changes left by the interrupted run. No commits or pushes were made.

Explosions now damage all five native target types at their explosion-time positions, with native surface falloff and damage flags. Friendly-fire immunity belongs to each recipient, so friendly/self repairs reach the healing path. Warp bombs use loaded constant slot 32, explode at the destination partner, and also explode at the source for dual bombs.

## Rules and evidence

Native paths below are relative to `~/Development/Allegiance/src/`; port paths are relative to this worktree. Test references in this table are in `tests/Allegiance.Core.Tests/P649CombatBlastParityTests.cs` unless stated otherwise.

| Rule | Native file:line | Port change at file:line | Socket-free test |
| --- | --- | --- | --- |
| Ships, asteroids, stations, missiles and probes receive splash; removed/dead targets are skipped. | `Igc/clusterIGC.cpp:378-379,416,431-435` | `src/Allegiance.Core/Model/Sector.cs:3215-3280`; static stations/rocks use their separate runtime lists. `src/Allegiance.Core/Sim/Missile.cs:154-193` adds missile health and damage reception. | `Every_native_target_takes_surface_falloff_with_flagged_matrix_damage` at line 110 (five target cases); `Lethal_splash_removes_targets_and_notifies_each_death_once_even_without_a_launcher` at line 283. |
| Sample positions at the explosion timestamp; pass projected target position, blast position and explosion time to the recipient. | `Igc/clusterIGC.cpp:409,419,431-435` | `src/Allegiance.Core/Model/Sector.cs:3232-3249` projects from each model's stored-position clock; the model currently integrating uses the current update time (`Sector.cs:673-676`). Static recipients have zero velocity. | `Moving_ship_is_sampled_at_the_explosion_timestamp` at line 156 tests forward projection, rewind, timestamp and position arguments; `Moving_missile_is_sampled_at_blast_time` at line 273. |
| Use surface distance, strict `d < radius`, squared falloff outside the target surface, and full amount for overlap. | `Igc/clusterIGC.cpp:420-429` | `src/Allegiance.Core/Model/Sector.cs:3291-3302`, shared by all recipients. | Five target cases at line 110; `Surface_boundary_is_strict_and_overlap_is_full_damage` at line 264 (four boundary/overlap cases). |
| OR NoWarn/NoDebris into damage type and mask flags before indexing the core matrix. | `Igc/clusterIGC.cpp:431`; `Igc/igc.h:559-562`; `Igc/missionigc.h:148-155` | `src/Allegiance.Core/Model/Sector.cs:3217`; `src/Allegiance.Core/Sim/BlastDamage.cs:9-11`; matrix lookups at `Ship.cs:4403`, `Station.cs:596`, `Asteroid.cs:292`, `Probe.cs:633`, `Missile.cs:166`. | `Splash_passes_NoWarn_and_NoDebris_without_losing_the_damage_row` at line 171 observes `0x63` at both ship/station damage callbacks. Five target cases at line 110 distinguish the loaded row multiplier 2 from fallback 1. |
| Suppress nonnegative friendly/self/allied damage only while friendly fire is off; support non-ship launchers' sides. | `Igc/shipIGC.cpp:1222-1228`; `Igc/stationIGC.cpp:128-130`; `Igc/missileigc.h:88-94`; `Igc/igc.h:4297-4303` | `src/Allegiance.Core/Model/Mission.cs:85-93` supplies the default-off toggle and allied pairs; `src/Allegiance.Core/Sim/BlastDamage.cs:13-25` shares side lookup/immunity. Gates at `Ship.cs:4179`, `Station.cs:416`, `Missile.cs:164`. There is no unconditional launcher/same-side skip in the blast walk. | `Ship_friendly_and_self_splash_obeys_the_mission_toggle` at line 126; `Native_target_specific_friendly_fire_rules` at line 193; `Non_ship_launchers_obey_friendly_immunity` at line 207; `Allied_sides_share_friendly_immunity_and_can_repair` at line 225. |
| Armed friendly probes are protected; sensor and RescueAny probes remain vulnerable. Asteroids have no friendly-fire gate. | `Igc/probeigc.h:74-89`; `Igc/asteroidigc.h:126-171` | `src/Allegiance.Core/Sim/Probe.cs:280-283` checks the loaded projectile type and RescueAny bit; `Asteroid.cs:231-272` has no side gate. | Armed, sensor, RescueAny and asteroid cases in `Native_target_specific_friendly_fire_rules` at line 193. |
| Negative splash repairs bypass friendly immunity, use the damage matrix and ship-launcher experience, and cap health at one. Station repair heals armor. | `Igc/shipIGC.cpp:1233-1255`; `Igc/stationIGC.cpp:138-155`; `Igc/asteroidigc.h:135-145`; `Igc/missileigc.h:98-115`; `Igc/probeigc.h:95-109` | Existing ship/station repair paths at `src/Allegiance.Core/Sim/Ship.cs:4193-4211`, `Station.cs:427-446` now receive splash. Repair/experience added at `Asteroid.cs:240-246`, `Missile.cs:169-173`, `Probe.cs:285-290`. Mission ownership at `Model/Mission.cs:562-566,855` supplies the loaded matrix even without a launcher. | `Friendly_and_self_repair_splash_heals_and_caps_at_one` at line 140; allied repair cases at line 225; `Every_repairable_target_heals_caps_and_uses_launcher_experience` at line 245 covers rock, station armor, missile and armed probe. |
| A first lethal crossing removes/notifies a recipient once. Killed missiles can generate a secondary blast; the zero-position resonator sentinel suppresses it. Expiry must not damage its spent source twice. | `Igc/shipIGC.cpp:1316-1334`; `Igc/stationIGC.cpp:189-193`; `Igc/asteroidigc.h:157-163`; `Igc/probeigc.h:121-139`; `Igc/missileigc.h:119-127`; `FedSrv/FedSrv.CPP:12512-12528` | `src/Allegiance.Core/Model/Sector.cs:3200-3212` queues secondary blasts during resolution; removal/notifications at `Sector.cs:3243-3279`, `Sim/Missile.cs:177-190`, `Probe.cs:296-310`. Missile expiry removes before blasting (`Missile.cs:316`). | Death-once test at line 283; `Missile_destroyed_by_splash_creates_its_secondary_blast_once` at line 297; sentinel test at line 311; expiry test at line 326. |
| Warp bombs always explode at the destination partner's position; only WarpBombDual adds a source blast. Use missile Power, BlastRadius, DamageType and scheduled time, with NULL launcher. | `Igc/warpIGC.h:65-87` | `src/Allegiance.Core/Model/Sector.cs:3550-3570`. Missing partners supply no fabricated destination; dual bombs can still hit the source. | `Warp_bomb_uses_core_delay_destination_and_optional_dual_source` at line 418 covers single `0x04`, dual `0x02` and combined `0x06`; launcher shares the victims' side, proving the blast is unattributed. Dangling-warp cases at line 363. |
| Read delay from loaded `c_fcidWarpBombDelay` slot 32 and capture the deadline when the bomb is triggered. Pop due bombs once in deadline order. | `Igc/igc.h:98`; `FedSrv/FedSrv.CPP:12551,12561`; `Igc/warpIGC.h:58-59,89` | `src/Allegiance.Core/Model/Sector.cs:1475-1476,3522-3523,3552-3556,3573-3579`. The old 15-second constant is a standalone-sector fallback/compatibility value; mission simulation uses its loaded core. | Loaded 5/7/10-second delay cases at line 418 assert no early or repeated detonation; `Due_bombs_detonate_in_deadline_order_with_each_delay_captured_at_trigger` at line 378 changes the slot between triggers and checks both deadlines. |
| Destination routing works through a complete mission tick regardless of sector update order; trigger consumes the missile and raises warp-bomb/destruction callbacks. | `Igc/warpIGC.h:79-87`; `Igc/missileigc.cpp:459-466,512` | `src/Allegiance.Core/Model/Sector.cs:3499-3545,3550-3570`. | `Full_mission_tick_routes_a_warp_blast_regardless_of_sector_update_order` at line 342; destination/dual cases at line 418 check trigger callbacks, immediate consumption and one detonation. |

## Regression adjustments

- `tests/Allegiance.Core.Tests/ProbeCombatTests.cs:195-210,282-292`: the probe struck by an explosive projectile now receives both the direct hit and its own impact splash. The fixture uses a sensor probe to retain the native same-side vulnerability.
- `tests/Allegiance.Core.Tests/WarpBombTests.cs:80,133-152,202`: source-blast fixtures explicitly use a dual bomb and valid warp pair, and expected deadlines use the loaded delay.
- `tests/Allegiance.Core.Tests/ShipAsteroidCollisionTests.cs:260-265,355-409`: NULL-launcher asteroid damage now correctly reads the owning core. Collision expectations include the asteroid damage-matrix cell; a zero cell leaves the rock intact while ship bounce and shield absorption are still checked.

## Verification

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**. Final log: `/tmp/p649-final-build.log`.
- Focused in-process xUnit run: **53 passed, 0 failed, 0 skipped**. Log: `/tmp/p649-xunit-focused-final.log`.
- Broad socket-free Core run: **3,211 passed, 0 failed, 0 skipped** (3,167 discovered cases expand to 3,211 executions through theory data). Log: `/tmp/p649-socket-free-final.log`.
- Additional domain regressions in mixed live/domain collections: **10 passed, 0 failed, 0 skipped** (five warp-bomb cases, four explosion/mine/shield cases, and missile/projectile coexistence). Logs: `/tmp/p649-mixed-collection-regressions.log`, `/tmp/p649-coexistence-regression.log`. These are outside the broad collection-filtered selection, for **3,221 distinct passing socket-free executions** overall; the 53 focused P649 executions are already included in the broad total.
- `git diff --check`: clean.

The standard `dotnet test` entry point aborts before executing tests because VSTest attempts to bind a TCP listener and this sandbox denies sockets (`SocketException (13)`). A temporary runner in `/tmp/p649-xunit-runner` uses the installed xUnit framework's discoverer/executor directly, including theory data and normal xUnit lifecycle. Commands:

```sh
dotnet run --project /tmp/p649-xunit-runner/runner.csproj --no-restore -- P649CombatBlastParityTests
dotnet run --project /tmp/p649-xunit-runner/runner.csproj --no-build --no-restore -- --socket-free
dotnet run --project /tmp/p649-xunit-runner/runner.csproj --no-build --no-restore -- WarpBombTests:Domain_ ExplosionPropagationTests:Falloff_ ExplosionPropagationTests:Shielded_ ExplosionPropagationTests:Explosion_ ExplosionPropagationTests:Accumulated_
dotnet run --project /tmp/p649-xunit-runner/runner.csproj --no-build --no-restore -- MissileCombatTests:Missile_and_
```

The broad selection excludes the `LiveNetwork` collection and older socket cases without that marker: `ShipIdAllocationLiveTests`, `TcpLoopbackProofTests`, `P366LeaderBotCommandLiveTests`, `AsteroidExportTests`, the two `ShipAsteroidCollisionTests.Loopback_*` methods, `FuelPoolAndAfterburnerTests.Live_*`, and `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`. Repository guard tests use the repository test-output directory as `AppContext.BaseDirectory`. Available artwork/native-corpus domain tests also ran. The initial broad selection exposed those socket/harness exclusions and the two collision expectations fixed above; the final selection does not exclude simulation failures.

The interrupted run's original failing-first log remains at `/tmp/p649-before-reflection.log`: **3 passed / 16 failed**, covering missing target types, repair, projection, flags and destination/dual behavior. Its older reflection runner and logs remain temporary; final verification uses xUnit itself.

## Matrix and remaining work

Updated `docs/parity/sim-rules-matrix.csv` rows `SIM-0033`, `SIM-0066`, `SIM-0067`, `SIM-0068`, `SIM-0130` and `SIM-0207`. Explosion recipients and warp destination/dual simulation are implemented. The following limits are explicit:

1. **Networking excluded by the brief:** `src/Allegiance.Server/Server.Combat.cs:957` still stamps the WARP_BOMB broadcast using `Sector.WarpBombDelaySeconds` (15 seconds). Simulation deadlines now use slot 32, so non-15-second cores can still produce a mismatched client notice. No server/transport/networking/client-rendering files were changed. `SIM-0033` and `SIM-0207` remain partial to preserve this follow-up. Mission friendly-fire/allied-pair state is available to simulation callers; this task does not add network parameter/alliance binding.
2. **Live/full suite deferred as requested:** Claude should run the full socket-capable suite and bomber/missile battle, checking kills and station splash. No live battle was run here.
3. **Existing architecture retained:** blasts resolve synchronously, with secondary blasts queued during the walk. Native's shared cluster offset is represented by per-model stored-position clocks for extrapolation. A complete shared swept collision/update event ordering remains the separate `SIM-0063` issue. This task does not add client debris/pulse rendering or alter scoring systems.
4. **Mine convention retained:** `Sector.cs:3282-3287` preserves point-like mine falloff, strength reduction and no side gate. Native subtracts the mine radius (`Igc/clusterIGC.cpp:448`); that existing sentinel-radius discrepancy remains `SIM-0067` partial. The managed snapshot still replaces native's 500-target fixed array (`SIM-0068`, n/a).

All requested simulation rules have socket-free coverage. Changes and the report remain in the working tree.

## P649b — live-test regression fixes

Date: 2026-10-14. This section supersedes the armed-probe immunity claims in the P649 table above. All earlier P649 changes remain in the working tree; no commits or pushes were made.

### Friendly missile hitting a probe

**Root cause:** P649 added an armed-probe side gate to the shared `Probe.ReceiveDamage` entry point, so it blocked direct missile hits as well as splash. Inspection of the actual `ProbeCombatLiveTests.Nonlethal` fixture found projectile ID **63** (armed), capability mask **120**, missile `BlastPower = 0`, and direct damage fraction **0.075**. The failure was entirely the new direct-hit gate, rather than explosion falloff or position projection.

**Fix:** `src/Allegiance.Core/Sim/Probe.cs:265-284` restores the existing P100/P491 contract: every probe can take friendly damage, including armed probes, through both direct hits and splash. P649's matrix masking, experience multiplier, repair cap and death notification remain. Ships still suppress nonnegative friendly damage while friendly fire is off (`Ship.cs:4176-4179`, `BlastDamage.cs:23-25`; native `Igc/shipIGC.cpp:1222-1228`).

**Native evidence and scope correction:** In this checkout, `CprobeIGC::ReceiveDamage` is defined in **`Igc/probeigc.h:65-146`**, not `probeIGC.cpp`. Lines **74-89** allow friendly damage to sensors and RescueAny probes, but lines **87-88** explicitly protect an armed friendly probe. Thus the brief's blanket claim that native probes have no friendly-fire gate is too broad. This fix honors the requested existing port behavior and live-test contract; armed-probe immunity remains an intentional native parity exception, just as documented before P649. Production comments and matrix row `SIM-0066` now record that exception explicitly.

**Socket-free regression:** `P649CombatBlastParityTests.Same_side_missile_damages_probe_with_friendly_fire_off` (`tests/Allegiance.Core.Tests/P649CombatBlastParityTests.cs:127`) sends a real missile through the sector collision pass at a sensor, armed probe and RescueAny probe. Each takes the exact loaded-matrix damage and survives. Before the fix: **3 passed / 1 failed** across these three cases and the station regression below; the armed case failed with expected fraction **0.6**, actual **1** (`/tmp/p649b-before-tests.log`). The two existing P649 expectations for armed same-side/allied splash were updated to the restored contract; their cases remain, and all other blast/warp coverage remains intact.

### Capturing the last restart station

**Root cause:** The failing assertion is **`bas.ShieldFraction == 0` before capture**, formerly line 580 in `SideEliminationLiveTests.cs`. The setup called `ReceiveDamage(0, EffectiveMaxShield(), ..., launcher: null)`. P649 changed `Station.DamageConstant` to read the owning mission's loaded matrix even without a launcher (`src/Allegiance.Core/Sim/Station.cs:591-599`), instead of returning the old fallback multiplier 1. Inspection of the actual fixture/core confirmed **damage type 0 / shield defense 0 = 0**. The station correctly takes no shield damage and remains at fraction **1**. No explosion is created in this setup; station/asteroid splash targets, extrapolated positions and post-capture health are not the cause.

**Native evidence:** `Igc/stationIGC.cpp:159-161` reads the mission damage matrix unconditionally and skips shield damage for a zero cell; lines **171-176** compute `absorbed = amount * dtmShield / maxShield` and subtract it. `Igc/missionigc.h:148-155` supplies the masked matrix lookup. `Igc/clusterIGC.cpp:378-433` enumerates native station splash targets and forwards flagged damage through that same recipient method; it provides no launcher-less matrix bypass. Therefore native proves the old test setup wrong, and the production station/blast implementation is retained.

**Fix to the test setup only:** `tests/Allegiance.Core.Tests/SideEliminationLiveTests.cs:579-584` uses `Combo.Value`'s selected missile damage type and positive shield cell, passing **`EffectiveMaxShield() / dtmShield`**. The actual fixture selects damage type **8**, shield defense **0**, multiplier **1**. This drains the GA-scaled shield through real damage without weakening any capture/elimination assertions. Native capture remains `FedSrv/fsship.cpp:582` (halve current armor), **589-596** (regen-derived shield boost) and **631** (change side).

**Socket-free regression:** `P649CombatBlastParityTests.Launcherless_station_damage_uses_matrix_before_hostile_capture` (`tests/Allegiance.Core.Tests/P649CombatBlastParityTests.cs:151`) verifies that a NULL-launcher hit with a zero matrix cell leaves the shield full, then drains it using a positive non-unit multiplier. A real sector dock pass captures the boardable station, halves armor, keeps its zero-regen shield at zero, notifies the old side's loss of its restart station once, and retains the station without a kill event. Server-side elimination broadcasts remain covered by the existing live test.

### P649b verification

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**, log `/tmp/p649b-build.log`.
- Focused in-process xUnit: **57 passed / 0 failed / 0 skipped** (all 53 original P649 cases retained, plus four new executions), log `/tmp/p649b-focused.log`.
- Broad socket-free Core run: **3,215 passed / 0 failed / 0 skipped**, log `/tmp/p649b-socket-free.log`. The focused cases are included in this total.
- Additional socket-free cases from mixed live/domain collections: **10 passed / 0 failed / 0 skipped**, log `/tmp/p649b-mixed-regressions.log`.
- Fixture/core inspection without sockets: `/tmp/p649-xunit-runner` with `--inspect-p649b` confirmed the values cited above.
- `git diff --check`: clean. Overall: **3,225 distinct passing socket-free executions**.

The runner and socket exclusions are the same as the P649 verification section. The two live/socket tests must still be rerun in the socket-capable environment; they were not executed here. No transport, server or rendering implementation changed.
