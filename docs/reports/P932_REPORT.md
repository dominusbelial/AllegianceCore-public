# P932 — training loadout transactions

TRN-0167 and TRN-0208 are implemented. TRN-0106 remains partial, classified as a native authored-loadout bug: the default M3 path supplies no spare missile magazine. No equipment was added to make the narration true. Changes remain in the working tree; no commits, pushes or remote-host operations were performed.

Qualification: solution and all 116 tool projects built with **0 warnings / 0 errors**; final isolated regressions passed **48/48**. The single Fast sweep failed (10 failures, 3 optional skips); the training gate failed (1 failure, 4 optional skips). All failed families received three isolated reruns and are ledgered. The exact training failure passed 3/3 in isolation. Both HUD styles passed the purchase window proof three times each.

## Row verdicts and evidence

| Row | Verdict | Rechecked native source | Production port | Regression evidence |
|---|---|---|---|---|
| TRN-0167 (5) | Implemented | `training/trainingmission.cpp:810-854`; `Igc/shipIGC.h:396-406`; `clintlib/clintlib.h:2127-2141,1305-1338`; `Igc/packigc.h:80-83`, `Igc/magazineigc.h:85-88`, `Igc/dispenserigc.h:86-89` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Lifecycle.cs:124` | `P932TrainingLoadoutTests.Station_reapplication_uses_hull_equipment_and_cargo_value_before_clearing_cargo` (three independently priced variants); existing `P882aTrainingIntegrationTests.No_landing_station_loadout_mounts_preferred_equipment_and_native_five_cargo_slots` retains the no-station branch. |
| TRN-0208 (4) | Implemented | `clintlib/clintlib.cpp:1731-1744`; `Igc/shipIGC.h:989-1111`; `WinTrek/trekigc.cpp:2870-2883`; `Igc/stationIGC.cpp:299-382`; multiplayer event: `clintlib/appmsg.cpp:3183-3187` | `src/Allegiance.SinglePlayer/Training/TrainingHost.Purchase.cs:13`; `src/Allegiance.ClientCore.Game/Game1.Hangar.cs:89`; `src/Allegiance.ClientCore/Session/ConnectionSession.cs:277`; `src/Allegiance.ClientCore/State/ClientWorld.cs` (`ApplyPurchasedLoadout`, `ApplyLocalTrainingLaunch`) | `P932TrainingLoadoutTests.Hangar_training_purchase_installs_notifies_then_launches_before_return` (Classic/Modern × buy/launch), `Local_purchase_reports_true_even_when_requested_hull_is_unavailable`, `Connected_training_launch_remains_consistent_after_loopback_ticks_resume` (both styles), and `Multiplayer_keeps_exact_buy_loadout_packet_and_waits_for_ack` (both styles × buy/launch). |
| TRN-0106 (4) | Partial; native bug | `training/mission3.cpp:161-186,905-991`; `training/trainingmission.cpp:784-788,810-854`; `clintlib/clintlib.h:2222-2279`; `training/SwapWeaponAction.cpp:33-35` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:389`; `src/Allegiance.ClientCore/Training/Mission3.cs:629` | `P932TrainingLoadoutTests.Mission3_station_default_then_script_removal_leaves_no_spare_for_goal09` independently reapplies the shipping hull-414 default, checks cargo before and after the scripted removal, then executes the actual swap action. Existing P912 window tests retain the native-legal gunfire ending in both styles. |

All new automated P932 regressions live in `tests/Allegiance.ClientCore.MonoGame.Tests/P932TrainingLoadoutTests.cs`. The six methods expand to 15 cases. Development run: **15 passed, 0 failed, 0 skipped**, including real TCP and connected training. Artwork was available through the worktree's existing `local.env`.

### Station reapplication oracle

Native samples `GetValue()` before `BuyDefaultLoadout(..., NULL, &refund)` clears cargo. It retains mounted parts, does not spend the hull price buying the hull again, and ignores the port's multiplayer free-spawn provenance for this native value calculation. Ordinary equipment contributes its price, launcher stacks contribute quantity × unit price, and packs contribute zero resale value.

The oracle uses a private copy of the mission's type dictionaries. A 100-credit hull receives exactly 100 additional credits from one of three independent sources: an equipped item, five launcher rounds priced at 20 each, or a cargo item. A 150-credit shield therefore fits; the subsequent 100-credit booster does not. A pack deliberately priced at 1000 must contribute zero refund. The failed paid ammo attempt still advances the cargo cursor, as native does, leaving -5 empty and filling -4..-1 through the unbudgeted filler. This checks the budget, clearing, retained equipment identity, launcher pricing, pack exclusion and final resource charging with explicit expected outcomes.

### Immediate purchase order

Both hangar Buy and Launch call the shared dispatcher. Training exports the chosen inventory before mutation, applies station availability/successors to the authoritative trainee, projects the actual inventory into the client, and raises `PurchaseCompleted(true)` synchronously while still docked. Native always reports true here, even when `PurchaseShipLoadout` cannot supply a requested hull. A requested launch then clears the station, applies native departure repair/refuel, uses the existing station launch geometry and updates local flight prediction before returning.

The transaction holds the client session gate followed by the host simulation gate. A local launch retains the loopback input cookie, remote model poses and sector generation. It does not run network SET_CLUSTER's remote-object retirement. The resumed-loopback test verifies that authoritative updates retain the purchased inventory and flying state. The multiplayer control captures the exact `BUY_LOADOUT` packet over real TCP, verifies that no synchronous completion occurs, then supplies an ACK and checks that its actual success flag is delivered after the inventory update. A launch ACK still waits for the ordinary server launch message.

### M3 cargo derivation and exact remaining gap

1. `trainingmission.cpp:655-656` loads `IGC_TRAINING_CORE_FILENAME`, defined as `PCore014` at `Igc/igc.h:1002`. `mission3.cpp:163` loads `training_1`, hull 414, station 1010. `trainingmission.cpp:779-788` assigns the player to side 0 before loading the map, then applies and saves the default loadout before M3 customizes it.
2. The friendly landing station selects `trainingmission.cpp:818-821`, which calls `BuyDefaultLoadout`. The two spare magazines at `:841-842` belong exclusively to the **no-station** branch and do not describe M3.
3. The shipping training core's hull 414 carries ammo weapons, an afterburner and a dispenser and has more than two weapon slots. `clintlib.h:2232-2244` fills all five cargo mounts before the missile filler at `:2274-2277` can execute:

   | Cargo mount | Part | Equipment |
   |---|---|---|
   | -5 | 24 | Ammo pack |
   | -4 | 24 | Ammo pack |
   | -3 | 25 | Fuel pack |
   | -2 | 25 | Fuel pack |
   | -1 | 14 | Dispenser |

4. `mission3.cpp:180-182` terminates the mounted magazine. It does not transfer it to cargo. The only explicit `AddPartToShip` in that file is the anti-shield weapon at `:177`; the first RepairRearm action is later, at `:1081` (lesson 11).
5. Lesson 9's `SwapWeaponAction(ET_Magazine, 0)` at `:919` calls `GetWindow()->SwapPart` (`SwapWeaponAction.cpp:33-35`). There is no cargo magazine to find. The missile-fire gate at `mission3.cpp:928` tests an input state bit; the destruction gate at `:966` accepts destruction of the current target without requiring missile damage. Gunfire remains a native-legal ending.

The uncredited gap is specifically a **default-path magazine swap and missile kill**: native supplies no magazine for it. Completing that outcome would require changing the authored equipment or the native rule. TRN-0106 stays partial with weight 4 and production reachability; only its evidence and native bug classification were updated. There is no claimed missile-kill screenshot.

## Scorecard

`python3 scripts/parity-scorecard.py` validated all matrices before and after. Training changes from **797/901 (88.46%), 204/235 eligible rows (86.81%)** to **806/901 (89.46%), 206/235 (87.66%)**: **+9 points, +2 completed production rows**. Re-measured by Claude after rebasing onto master 0f6c9892 (which adds P931): **807/901 (89.57%), 206/235 (87.66%)** to **816/901 (90.57%), 208/235 (88.51%)** — the same +9 points and +2 rows. Integration moves from 301/326 to 310/326; framework, missions and tools retain their credits. Only TRN-0167, TRN-0208 and TRN-0106 were edited; all other parity rows remain unchanged.

Raw receipts: `.scratch/p932/scorecard-before.txt`, `.scratch/p932/scorecard-after.txt`, `.scratch/p932/development-8/p932.trx`. Scratch uses the task directory, including a writable NuGet HTTP cache. The initial default-cache restore failed with NU1900 because the sandbox could not write the shared cache; redirecting that cache resolved it without changing package policy or repository settings.

## Negative controls

Each control was compiled and executed against the new assertions, then the production source was restored in a `finally` block. All failures are assertion failures with zero skipped cases; the controls were not merely source comparisons:

| Removed behavior | Failing cases / executed | Result |
|---|---|---|
| Existing-part refund (hull-only budget) | 3/3 priced variants | Missing 150-credit shield |
| Cargo clearing | 3/3 priced variants | Old cargo survives instead of the expected rebuilt slots |
| Synchronous local route (send BUY_LOADOUT instead) | 4/4 style × launch cases | Completion count remains zero |
| Completion before station launch | 2/4 (both launch cases) | Callback observes an undocked ship |
| Local launch projection (use network SET_CLUSTER instead) | 2/4 (both launch cases) | Cookie / remote-pose retention assertion fails |

Receipts: `.scratch/p932/mutations/summary.json` and the five sibling TRX directories. After the priced-fixture audit, both price-related controls were repeated: **3/3 assertion failures each**, zero skips, under `.scratch/p932/oracle-final-negative/`. The M3 cargo test is a source-derived characterization of an existing native bug; it is not claimed to fail against the dispatch base and earns no new completion credit.

## Qualification record

The report and matrix decisions were written before the full qualification scripts. The following records completed runs; isolated reruns do not replace a failed full gate.


- Debug solution build: **PASS, 0 warnings, 0 errors** (`.scratch/p932/solution-build.log`).
- `tools/build-tools.sh`: **PASS, all 116 projects built, zero warnings/errors** (`.scratch/p932/tools-build.log`).
- Glossary link check: **PASS, 559 distinct paths**. Matrix row-scope check: exactly the three assigned rows changed; the CSV has LF line endings. `git diff --check`: **PASS**.

- Isolated regression gate: **PASS, 48/48 executions, 0 failed, 0 skipped**. Each of the six new methods ran alone three times (45 executions), as did the existing no-station loadout regression (3 executions). This includes both connected-training styles. Receipts: `.scratch/p932/isolation/summary.json` and 21 per-method/run TRX directories.
- Exactly one Fast sweep: **FAIL, 13,129 passed / 10 failed / 3 skipped (optional Meshy packs), 13,142 executions in 538.8 s**. All nine infrastructure checks passed. All P932 Fast cases passed. The passing `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` took **10.519 s**, exceeding Fast's 10 s timing guard. No full-tier retry or unrelated implementation change was made. Receipts: `.scratch/p932/fast/summary.json`, `.scratch/p932/fast/fast/summary.json` and per-project TRX files. The failed families are PlayerBundlePaths (five cases), queued-station command relay, own join status, NPC defend, missile terminal position and side-selection timing; the ledger records their three-run isolation outcomes.

- Fast failure isolation: all seven families (including the timing offender) completed **three serial runs each**. Paths: five failures and four passes per run; NPC defend: fail/fail/fail; relay, join status and missile position: pass/pass/pass; side selection: fail/pass/pass (32.25 simulated seconds in the failed isolated sample, sweep 31.95); warp exit: pass/pass/pass at 9.212/9.345/9.302 s. These outcomes are ledgered in `docs/TEST_FLAKES.md`; the failed Fast sweep remains failed. Receipts: `.scratch/p932/flake-isolation/`.

### Actual purchase windows — both HUD styles

The existing `tools/p910-training-proof/Program.cs` now accepts an optional `purchase` mode. It opens the shipping Game1, joins native Free Flight, arranges a dock through the real station hook, then invokes the same hangar button dispatcher as the pointer controls. It checks Buy completion before return while still docked, then checks a second completion while docked followed by launch before return. It also checks that the actual client leaves the hangar on its next frame. This fixture arranges the dock; it is not claimed as a manual docking/input proof or as M3 missile equipment.

**Classic 3/3 and Modern 3/3 real-window runs pass.** The four retained PNGs were opened and inspected. Both hangar frames show the shared Adv Fighter loadout/stats panel, $0 cost and the Launch/Buy/Command/Loadout controls. The Classic flight frame shows the cockpit's ring gauges, Neptune location, systems readout and starmap. Modern shows circular percentage gauges, speed, Neptune location and starmap. These captures establish the rendered view transition; the synchronous ordering is established by assertions and the unit/loopback regressions.

| Style | After Buy, still docked | After same-call Launch |
|---|---|---|
| Classic | [Hangar](P932_assets/p932-classic-hangar.png) | [Flight](P932_assets/p932-classic-launch.png) |
| Modern | [Hangar](P932_assets/p932-modern-hangar.png) | [Flight](P932_assets/p932-modern-launch.png) |

Reproduce after a Debug/tool build with artwork configured:

```sh
SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
  DOTNET_gcServer=1 ALLEGIANCE_NOVSYNC=1 "$DOTNET" \
  tools/p910-training-proof/bin/Debug/net10.0/p910-training-proof.dll \
  src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content \
  .scratch/p932/purchase-window Classic purchase
```

Repeat with Modern. Raw six-run receipts: `.scratch/p932/purchase-windows/`. Full training window traces remain under `.scratch/p932/p917/windows/` and `.scratch/p932/p918/windows/`. The small persisted gate summary is [verification.json](P932_assets/verification.json).

- Final post-proof Debug solution build and targeted proof build: **PASS, 0 warnings, 0 errors** (`.scratch/p932/solution-build-final.log`, `.scratch/p932/purchase-proof-build-final.log`). The complete 116-project tool build was repeated after adding purchase mode and also passed with zero warnings/errors (`.scratch/p932/tools-build-final.log`).
- Training qualification command: `tools/run-training-tests.sh --no-build --repeat 1 --results-dir .scratch/p932/training`, after the final Debug build with artwork configured. **FAIL: ClientCore 100 passed / 0 failed / 0 skipped; MonoGame 241 passed / 1 failed / 4 skipped; aggregate 341 passed / 1 failed / 4 skipped (346 cases).** The MonoGame phase took about 2 h 25 m. All P932 cases, both P918 windows, both early-Nanite windows, all 20 lesson-prefix windows and nine of ten whole-course windows passed. The four skips are the explicitly opt-in miner-loss and combined-loss branches in both HUD styles (`ALLEGIANCE_TRAINING_BRANCH_PROOFS` unset), not missing-artwork skips. No training case was excluded from the requested gate. Receipts: `.scratch/p932/training/1/`.

The failed case is `P912TrainingWindowTests.Every_lesson_completes_in_one_real_window_session(mission: 4, style: "Modern", policy: null)`. It timed out at goal **9.3**, the `GetCommandCondition(CmdGoto)` acceptance wait after construction, while its trace still held the destroyed scout's old target. No loadout purchase was reached in that course. M4 Classic, Modern departure, and the Modern lesson-9/10 prefix proofs passed in the same gate. The failure remains recorded. The exact case subsequently passed **3/3 isolated whole-course runs**, with case durations **00:11:12/00:11:06/00:09:46**. This is a full-gate-only window failure in these samples; the underlying command-delivery cause was not isolated. No M4 implementation, proof driver, timeout or assertion was changed. Receipts: `.scratch/p932/training-isolation/summary.json` and its three TRX files. Raw receipt: `.scratch/p932/p917/windows/m4-Modern-default-a85b4e53c7a74056978ec88be6a1d3a8/run.log` and the MonoGame training TRX.

The priced-oracle fixture was further audited during qualification: ammo depletion now happens before cargo installation, with an explicit assertion that the 1000-credit pack remains present when the budget is sampled. Nanite 91 remains in the private core with an incompatible socket mask, preserving native's non-null precondition; the synthetic launcher's expendable definition and canonical part price agree. Production code was unchanged throughout the full Fast/training gates. After the training process released its loaded test assembly, both priced negative controls were repeated, the correct production source was restored, and the final Debug solution rebuilt with **0 warnings / 0 errors** (`.scratch/p932/solution-build-oracle-final.log`). The final fixture and all six new methods plus the no-station regression then passed **48/48, zero skips**, three isolated runs per method (`.scratch/p932/isolation-final/summary.json`).

## Native-intent footer

- **TRN-0167 — design.** Training reuses a station's normal default-loadout builder and the ship's current value. Matching the ordering preserves native budget decisions. A modern implementation benefits from an explicit local transaction and priced regression oracle; it should preserve those decisions.
- **TRN-0208 — design, with an era-specific client event interface.** Offline training acts on local IGC state immediately; multiplayer waits for a server. An explicit managed transaction with callbacks and locks is a better fit for the port than the old client interface, while preserving its order. An asynchronous local purchase would change what same-frame lesson conditions observe.
- **TRN-0106 — bug.** The narration/goal assumes cargo that the station's five-slot default recipe never supplies. A modern authored lesson would validate its inventory preconditions and deliberately supply a spare or change the teaching goal. That would be a separate behavior change, not native parity; this task preserves and documents the native outcome.
