# P1074 — Browser keyboard parity

Status: browser input fixes and focused checks complete; final Fast gate remains red for existing non-task receipt failures and timing. Chromium flight/capture proof is deferred to the workstation as permitted by the brief. This report was created before implementation and updated throughout the work. No commits, pushes, installs, deployments, VPS changes or lane-bot runs.

## 1. Complete physical key mapping

`src/Allegiance.ClientCore.Game.Browser/BrowserKeyboard.cs:9-45` replaces `ParseKey` with an explicit, ordinal `KeyboardEvent.code` table. Letters, digits and F1–F24 use bounded contiguous ranges; named keys have explicit entries. Unknown codes map to `Keys.None`, rather than becoming arbitrary enum values.

Coverage includes all catalog defaults, native suggestions, layout defaults, alternates and modifier sides: OEM punctuation, numpad digits/operators/decimal and `NumpadEnter`, both backslashes, and left/right Shift, Control and Alt. The actual `Game1.FlightKeys` entry point uses this table (`Game1.Flight.cs:234`).

`P1074KeyboardTests` walks the complete catalog and both layouts, including alternates and required modifiers. It also drives the actual Game1 entry point for the previously missing punctuation/numpad keys. Both `Backslash` and `IntlBackslash` still reach ThrottleFull.

DOM physical codes are a browser platform adapter. Native command identity remains the shared catalog: `WinTrek/trekinput.cpp:176-186,252-264,1531-1600`; the native authored keyboard table is `Artwork/defaultinputmap.mdl:3-108` (read from the installed original artwork). Native's stock table has no arrow-steering assignments; the port's fresh-profile arrow/WASD scheme is the existing keyboard-friendly layout.

## 2. Keyboard flight, capture and focus

The starting revision had three separate gaps:

1. In `src/Allegiance.ClientCore.Game.Browser/Game1.Flight.cs:315-321` **before this change**, pointer lock replaced keyboard yaw/pitch with the mouse's axes every frame. A stationary mouse supplied zero and discarded held arrows. Native reads analog axes first (`WinTrek/WinTrek.cpp:10822-10840`), then overrides individual axes with held keyboard commands (`:10887-10915`). Right/down take precedence over their opposite keys; RollModifier routes yaw keys to roll after ordinary roll keys. `BrowserFlightSteering.Combine` implements that sequence, including nonzero/opposing mouse deflection, with unrelated axes, throttle and state bits preserved. The pure regression exercises 768 combinations. The existing gamepad post-processing position is preserved.
2. In `flight-input.js:48` **before this change**, a focused button/link/select made `owns()` false. Clicking Dock/Watch/Starmap could therefore stop the entire key stream, and the focused control could consume Space/Enter. The new ownership check excludes text editors, rather than all controls. Mouse-down on toolbar buttons preserves canvas focus; completion of a button/select action returns focus to the canvas. A stray focused non-text control yields ordinary flight keys back to the game. `menu.js` also leaves session keys to the flight adapter.
3. Before this change, every trusted non-repeat flight key called `lock()` (`flight-input.js:73`). **Nuance:** `lock()` already required `wantsPointer`; an untouched fresh keyboard-only session did not request lock until a canvas click had set it. After a refused/pending capture, subsequent arrows could request capture again. Keys now neither capture nor retry, except the deliberately pressed, live-bound ToggleMouse command. Canvas click remains the other capture gesture. Real capture grant preserves held keys; loss/blur/hidden/context loss clears them.

The final input path is at `Game1.Flight.cs:234-235,270-315`; focus/gesture ownership is in `flight-input.js:49-100,138-165`. Text inputs, textareas and contenteditable editors retain text, arrows, Space, Enter and shortcuts. Entering a text editor clears already-held game keys. **Shift+Tab leaves the canvas for the toolbar; Tab navigates it; Space/Enter activate a keyboard-focused button; Escape returns to flight.** A keyboard-focused select retains its normal selection keys until its action or Escape returns focus. These instructions appear in the flight hint in both HUD styles.

Desktop comparison: `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:2005-2038` reads mouse input only during deliberate Flight capture (or the proof seam), but then also overwrites keyboard yaw/pitch. That differs from native's internal-camera keyboard override. **Desktop input and desktop defaults remain unchanged**, following the brief's final “Desktop … input code, except to read it” scope rule. The browser uses native per-axis precedence in the port's pilot camera views, including Chase; native's original override is gated by `bInternalCamera`.

### Browser action wiring

The original browser adapter also lacked the target-key dispatch required by the requested keyboard-only sequence. `BrowserTargeting.cs` and `Game1.KeyboardActions.cs` now connect the existing visible world, target selection, current-command wire slot, shared native default-order resolver, MatchSpeed, G autopilot and existing station docking law. The selected name/autopilot status is visible in either style. TargetNothing clears the server's current target and autopilot. The port's existing P256 reticle tiering remains a port choice; this work claims no new native FindTarget parity.

`Game1.KeyboardUtility.cs` connects the existing shared cargo selector, inventory/part planner, weapon selection, expendable wire verbs, zoom model, mouse settings, vote and turret-seat verbs. These use their desktop/shared guards and server validation. Native sources remain those cited on the shared helpers, including `WinTrek/WinTrek.cpp:9640-9730` (cargo), `:10452-10464` (next turret), `:10966-10974` (match speed), `:8393-8409,8618-8645` (zoom), and `Igc/shipIGC.h:2071-2161` (default orders). The browser project source-compiles the existing `CommandOrderResolver.cs`; it sends a resolved concrete command, rather than inventing a server default-order path.

The hangar-to-flight boundary primes held-key edges so the L used to launch cannot also toggle flight vector lock. Help owns navigation keys while open and is available before a docked ship has its first flight pose.

## 3. Browser-reserved keys

This is a conservative source audit, **not a measured Chromium delivery matrix**. Chromium's [reserved command table](https://github.com/chromium/chromium/blob/main/chrome/browser/ui/accelerator_table.cc#L348-L361) reserves tab/window creation, closing, restoration and cycling. Its [command controller](https://github.com/chromium/chromium/blob/main/chrome/browser/ui/browser_command_controller.cc#L373-L432) applies platform/fullscreen exceptions. The [Keyboard Lock documentation](https://developer.chrome.com/docs/capabilities/web-apis/keyboard-lock) describes the separate fullscreen/permission-dependent capture API. Flight works without requiring that API.

During flight/hangar, `preventDefault()` cancels deliverable game keys and browser shortcuts. Text entry, normal toolbar Tab navigation and non-game page states retain browser behavior. F5/F11/F12/PrintScreen and OS Alt+function-key defaults use conservative alternatives even where a particular browser might allow cancellation. The continuous missile key moves off Control: Ctrl+W while holding forward thrust could otherwise close the tab.

`BrowserKeyboardBindings.ReservedDefaults` records every replacement and reason. It is applied only under `BROWSER` when building the layout defaults; saved user overrides remain user overrides. Replacement chords are checked against both layouts' original defaults. The view-number row, function row and original N/T mnemonics are retained where possible.

| Action/default family | Browser default |
|---|---|
| FireMissile (LeftControl, Port layout) | CapsLock |
| VoteNo (Ctrl+N) | Alt+N |
| TeamPane / native TargetFriendlyPrev (Ctrl+T) | Alt+T, in their respective layouts |
| ViewFlyby (F5 or Shift+F5) | Shift+5 |
| Native ToggleInvestmentPane (F5) | Shift+8 |
| ViewExternalStation (F11) | Shift+6 |
| Credits (F12) | Shift+F9 |
| CycleHudSkin (Shift+F12) | Shift+0 |
| IncreaseTMClockSpeed (Ctrl+F11) | Shift+Num Add |
| ResetTMClockSpeed (Ctrl+F12) | Shift+Num Decimal |
| ScreenShot (PrintScreen) | Shift+F2 |
| ConModeCombat (Alt+F2) | Shift+F1 |
| ConModeGameState (Alt+F7) | Shift+9 |
| ConModeMiniTeam (Alt+F6) | Shift+7 |
| Quit (Alt+F4) | Shift+F8 |

Only an actually reserved original chord is replaced; for example the Port investment I and native combat F2 stay as they were. OS custom shortcuts and browser extensions can still claim keys. Existing user-bound reserved chords are not silently rewritten.

## 4. Live key reference, Classic and Modern

F1, the live OpenKeyBindings chord (F6 in the Port layout), the flight Key reference button and Options → Map Keys and Controls open the shared in-game reference instead of “Controls unavailable.” Both Controls pages derive labels from the same `KeyBindingStore.Instance.Map` used by flight. `ActionMap.LabelOf` adds **(browser)** to adapted defaults; a user override immediately supplies its own label. The toolbar hint is also generated from live labels. Menu reference/screenshot keys cancel browser defaults through that same map; screenshot handling no longer has the old hard-coded F12 path.

`Game1.KeyboardHelp.cs` owns the browser lifecycle of the existing `HelpManualModel`/`HelpOverlayRenderer`. Classic and Modern share that model/renderer. Left/Right or PageUp/PageDown switch topics; arrows scroll text; Escape closes the reference. The browser header describes this navigation rather than advertising a browser rebind editor.

This is complete physical-key coverage for the shared desktop catalog plus the browser flight/hangar action wiring above. The catalog also contains desktop-only features (local training, tactical command screens, career, background music and other screens) that the browser has not ported. Their catalog mappings remain available in the full reference; this input task does not implement those systems or claim that every desktop feature is available in the browser. In particular, a remapped catalog action needs its owning browser feature before that feature can run. This is an outstanding limitation of a literal “every desktop binding works” claim.

## Validation

Machine-readable counts and isolated results: [validation.json](BROWSER_KEYBOARD_PARITY_P1074_assets/validation.json).

| Check | Result |
|---|---|
| Solution build | Passed, 0 warnings / 0 errors |
| `tools/build-tools.sh` | Passed, all 152 tool projects; affected browser host/scene proof also rebuilt after later input edits |
| New C# tests alone | 8 methods, 21 cases, each repeated 3 times in fresh filtered processes: 63/63 case executions passed |
| New DOM/menu tests alone | 12 tests × 3 isolated processes: 36/36 passed |
| DOM regressions together | 27/27 passed, including the 15 existing P1051 tests |
| Original DOM script against new regression cases | 7 failures / 2 passes in the original 9-case flight regression set; demonstrates the prior capture/focus/shortcut failures |
| `Allegiance.ClientCore.Browser.Tests` | 52/52 passed |
| `Allegiance.ClientCore.Game.Browser.Tests` | 239/239 passed |
| Full Fast sweep, artwork present | Red: 15,964 passed / 2 failed / 7 not executed, 595.9 s; 4 passing timing offenders; all 9 infrastructure checks passed |
| Glossary links / parity CSV / whitespace | Passed; 1,102 glossary paths, 5 parity rows; `git diff --check` clean |
| Browser harness syntax | `node --check tools/p1074-browser-keyboard.cjs` passed |
| Real Chromium, authoritative flight and captures | Pending workstation run: Chromium/Playwright are unavailable in this lane; no browser flight success or captures claimed |

All dotnet build/test commands used `$HOME/.dotnet/dotnet`, a task-specific `NUGET_HTTP_CACHE_PATH` and `--disable-build-servers`. Artwork was available. Gate logs and isolated-run receipts are in the task's temporary evidence directory; the final report records the results rather than checking in machine paths or raw credential-bearing fixture state.

### Broad sweep observations

The earlier sweep had 15,972 results: **15,963 passed / 2 failed / 7 not executed**, 582.2 s, all 9 infrastructure checks passed. Its calibrated per-case limit was 11.638 s. The browser projects and task tests were green. The failed launcher client receipt and remote-death latch are existing ledger families; the two passing timing offenders are also already ledgered. Afterward, in three fresh isolated processes with artwork:

| Method | Sweep observation | Isolated outcome / seconds |
|---|---|---|
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | Client granted launcher was default ExpandedPartData | 3/3 passed: 1.396 / 1.476 / 1.407 |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | B never latched A's death | 3/3 passed: 1.387 / 1.354 / 1.334 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | civ 25 passed in 16.104 s | Both cases passed ×3; slowest 4.182 / 4.207 / 4.174 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | Passed in 15.573 s | 3/3 passed: 3.234 / 3.286 / 3.160 |

The final camera-projection regression and its three isolated repeats were added after that sweep; the final-source sweep follows them. `docs/TEST_FLAKES.md` records both sweep outcomes. No assertion, timeout, quarantine, tier or timing threshold was changed.

### Final-source Fast sweep

**15,973 results: 15,964 passed / 2 failed / 7 not executed, 595.9 s.** All 9 infrastructure checks passed; the calibrated limit was **11.546 s**. Both browser projects (52 and 239), all new C# tests, the desktop client project and the session project passed. The earlier launcher/death-latch failures did not recur. The unchanged core/server test paths failed in two existing receipt families:

- `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`: expected one queued receipt, received three.
- `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`: the peer's receipt collection still contained only the old sector.

Four tests passed above the timing limit: `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` **14.769 s**; `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` **13.491 s**; `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` **13.075 s**; `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates(civ: 5)` **17.108 s**. All four families were already ledgered. All final receipt/timing methods passed in three fresh isolated processes with artwork:

| Method | Isolated qualification after the final sweep |
|---|---|
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | 3/3 passed: 1.786 / 1.808 / 1.769 s |
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | 3/3 passed: 0.312 / 0.315 / 0.306 s |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | All 3 cases passed ×3; slowest 5.399 / 5.469 / 5.392 s |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | All 10 cases passed ×3; slowest 4.475 / 4.406 / 5.697 s |

The P814/SC2 three-run isolated qualifications above also passed on the final production code.

**The broad gate is not green or waived.** The lane leaves these unrelated failures visible rather than modifying their production code or test policy.

### Workstation handoff

Rebuild a **current-tree** browser generation using the local publication recipe in [P1069's workstation section](BROWSER_SCENE_ENGINES_P1069.md#workstation-browser-run--for-claude), with the workstation's existing modules and artwork. No install or public deployment is needed. The keyboard harness uses the ordinary P1051 host with **zero bots**.

```bash
# Set these to the existing local modules/artwork and a freshly built generation.
NPM_DIR=.scratch/p1051/npm
GENERATION_DIR=.scratch/p1074/generation
STATE_DIR=.scratch/p1074/host
OUTPUT_DIR=.scratch/p1074/browser
mkdir -p "$STATE_DIR" "$OUTPUT_DIR"
"$HOME/.dotnet/dotnet" build tools/p1051-browser-host --disable-build-servers
"$HOME/.dotnet/dotnet" run --project tools/p1051-browser-host --no-build -- \
  "$ALLEGIANCE_ARTWORK_PATH" "$GENERATION_DIR" "$STATE_DIR" --bots 0 \
  > "$STATE_DIR/host.log" 2>&1 &
proof_pid=$!
trap 'touch "$STATE_DIR/stop"; wait "$proof_pid" || true' EXIT
node tools/p1074-browser-keyboard.cjs \
  "$NPM_DIR" "$STATE_DIR" "$OUTPUT_DIR" "$GENERATION_DIR"
```

The harness runs Classic and Modern with separate accounts, launches with L, and asserts server yaw/pitch/roll plus changed authoritative orientation, throttle steps/zero/full and speed, afterburner state/speed, weapon state and ammo/projectiles, reticle/next/clear target commands, match speed, G autopilot and authoritative docking. The launch-to-dock phase counts **zero DOM mouse events**; setup also uses focus and keyboard activation (HTML Enter naturally emits a browser click before that measured phase). The host now exports orientation, speed, roll and current target alongside its existing authoritative control receipts.

After relaunch it clicks Dock/Starmap/Watch, verifies restored focus and real steering, tests Shift+Tab/Escape, requests **real** pointer lock with a canvas click, waits through the delayed-capture interval, and checks keyboard steering with both stationary and moved mouse input. It captures the in-game reference in both styles and writes `browser-keyboard.json`; failure receipts record the last authoritative/UI samples. It never substitutes a synthetic PointerCapture callback for actual browser lock.

## Unfinished work and limits

- Run the Chromium harness on the workstation in Classic and Modern and review its two captures. The explicit no-Chromium exception in the brief applies here.
- The final Fast gate remains red: the two receipt assertions and four timing offenders are recorded below and in `docs/TEST_FLAKES.md`. No thresholds or assertions were weakened.
- Complete desktop feature parity remains broader than key transport: the desktop-only systems described above still need their own browser work. Desktop mouse/keyboard precedence also retains its documented discrepancy because desktop input edits are outside this lane's final scope rule.

## Workstation browser run (Claude, review)

Chromium 153, combined P1073+P1074 generation, P1051 host with zero bots: `BROWSER_KEYBOARD_PASS 2` (Classic and
Modern), both captures opened.

- Keyboard-only from launch to dock, zero DOM mouse events: arrows and A/D move the authoritative yaw/pitch/roll and
  orientation; Backspace, `=`, `-` and `\` set the server throttle; Space fires; `'`, T and `;` select, cycle and clear
  a real target; Shift+Z picks the home station, `/` matches speed, G autopilots and docks.
- Toolbar Dock/Starmap/Watch return focus to the canvas and arrows still steer; Shift+Tab leaves and Escape returns.
- Real pointer lock: held arrows and D still steer with the mouse still and after a nonzero mouse move.
- The key reference opens in both styles and labels the browser defaults "(browser)".

Review fixes to the harness:
- The hangar screen has no launch key (desktop `Game1.Hangar.cs` neither; L is the Loadout screen's `ShopLaunch`).
  The harness launches through the page hook and resets the mouse counter after it, so the keyboard-only phase is
  still measured from flight to dock.
- The default guest loadout (hull 13, one gun) has no afterburner, so the server never sets the booster bit; the
  boost assertion now applies only when the bit appears.
- `@sparticuz/chromium` passes `--single-process`, so closing the Classic context closed the browser; the harness
  drops that flag.

Candidates: a keyboard launch from the hangar screen (check native hangar hotkeys first); the key reference footer
still says "UP/DOWN topics" while its header says LEFT/RIGHT pages.

## Native intent

Native `TK_*` identity and authored defaults remain the shared catalog. Reviewed source: [FreeAllegiance commit c1065318](https://github.com/FreeAllegiance/Allegiance/blob/c1065318bfd6459173fbd2434661c2231d67106e/src/WinTrek/WinTrek.cpp#L10822-L10915). `SenseJoystick` reads analog first, then applies per-axis held-key precedence with right/down and RollModifier ordering (`WinTrek/WinTrek.cpp:10822-10915`). Target/default-order, cargo, weapon, zoom and docking rules remain shared native-derived helpers. `docs/parity/p1074-browser-keyboard.csv` records the distinction between the one native precedence rule and platform reachability/adaptations.

## Modern approach

A complete physical-code table, an explicit reserved-chord policy, the live action map for both handling and labels, deliberate pointer capture, and text-aware DOM focus ownership make keyboard flight independent of incidental toolbar focus or mouse motion. Pure C#/DOM tests pin those boundaries; the loopback Chromium harness checks actual server/HUD effects and leaves the unexecuted browser proof clearly identified.
