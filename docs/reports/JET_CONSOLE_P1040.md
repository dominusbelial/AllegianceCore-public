# P1040 — Jet HUD skin story 6/9: console, chat/command, lifecycle and training callouts (P972 slice 3D)

Worktree `hermes/p1040` of `AllegianceCore`, base commit `c89ab678` (the P1039c merge). No commit, no push, no
stash, no remote host, no Docker (not installed here). Raw captures/sweep logs live outside the repo in
`~/allegiance-evidence/P1040/`; the report's assets are the PNGs + `jet-capture.json` under
`docs/reports/JET_CONSOLE_P1040_assets/` (≤ 2 MB).

**Status: DONE** — every item below has its verdict, evidence and gate result. Written before the
implementation and updated after every item (the brief's order).

## Item status

| # | Item | Status |
|---|---|---|
| 1 | Walk all 26 §5.5 rows + §5.6 turret-gunner/command-view + §5.3 training callout + §5.1 roll row | **DONE** (§2: every row has a verdict + file:line / citation / reason; nothing dropped) |
| 2 | Jet console/chat: history, leader bold, scroll/channels/height, five compose states, recipient/caret, 0.5 s slide | **DONE** (`DrawChatPane`, `DrawComposePane`; §2 rows 6-7, §5) |
| 3 | `CMD` console: recipient/verb/wing controls, same hit rectangles/fan-out, one geometry source + click test | **DONE** (`JetCommandConsole` + `DrawCommandBar`; 14-cell click test green) |
| 4 | Tips, `ACC`/`QUE`, `VOTE`, `SECTOR`, `TURRET / carrier`, `MISSION T−/T+`, no flight money | **DONE** (`DrawConsoleColumn`, `DrawBottomLines`, `DrawVote`) |
| 5 | Lifecycle: `LIFEPOD` + return binding, `OXY` segmented + numeric, `PILOT DOWN / GHOST`, command summary, gunner rows | **DONE** (`DrawConsoleColumn` lifepod, `DrawCommandSummary`; ghost/gunner rows from P1038) |
| 6 | Training: thin-vector callouts + hint, roll cue pointer, training-preference restore test | **DONE** (`DrawTrainingCallout`, `DrawControlHint`, `DrawRollPointer`, two restore tests) |
| 7 | 800×480 fit of every new element + stated truncation rules | **DONE** (§4; audits 0 at 800×480 and 4K) |
| A | Zero-delta baseline captured before editing; compare after | **DONE** (1,848 / 0 pixel / 0 semantic) |
| B | Audits 0 in every Jet scene; scenes for every new state; PNGs + 2× crops opened and read | **DONE** (139 scenes, every class 0 including `text-overlap-cross`; 25 asset files = 24 PNGs + the JSON, 1,911,211 B = 1.82 MB) |
| C | Gate: build 0/0, tools build, new tests 3× alone, one fast tier, training-gate rule | **DONE** — build 0/0; tools build 141/141; new tests 32/32 ×3 and skins 387/387 ×3; fast tier run (see §8: every failure 3/3 alone, ledgered; the final run aborted on the ledgered transport crash, no assertion failure). The training-gate script was not run: P1040 touches no lesson logic (only the Jet callout drawing); `JetConsoleTests` pins the training-preference restore ×2. |

## 1. Zero-delta baseline (Classic and Modern unchanged)

Baseline: `hud2-proof --capture ~/allegiance-evidence/P1040/baseline` on the **unmodified** tree
(commit `c89ab678`, 1,848 golden PNGs + 1,848 semantic snapshots). After the change the same tree was
compared against it:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)
```

22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins = **1,848** (924 Classic + 924
Modern). The only differences are the fixture's wall-clock low-resource `!` badge inside its recorded
animation mask (the P980/P1038-documented family, 84 of 1,848 entries), and every entry reports
`outside: 0`. Classic and Modern are pixel- (decoded RGBA) and semantically identical: everything
P1040 adds is Jet-only code plus presentation-only context fields neither shared renderer reads.

## 2. Row walk (P972 §5.5 26 rows, §5.6 last rows, §5.3 training, §5.1 roll)

Verdict key: **Jet-drawn** (new Jet code, file:line), **earlier story** (cited), **shared**
(existing model/data, why), **deferred** (reason). Nothing is silently dropped.

### 2.1 §5.5 Console, alerts and lifecycle (26 rows)

| # | HUD parity row | Verdict | Where / why |
|---|---|---|---|
| 1 | Sector location | **Jet-drawn** | `JetHudRenderer.Console.cs:321` `DrawConsoleColumn` writes `SECTOR <cluster>` in flight (native <c>console.cpp:870</c>, <c>dialog.mdl:5419</c>) and `VIEWING <name>` for the docked command view (the native "Currently viewing:" block, `dialog.mdl:1429-1471,5421-5422`), both from `HudConsoleText.SectorName` / `ViewedSectorName`. |
| 2 | Turret carrier identity | **Jet-drawn** | `:321` row 2 prints `TURRET / <carrier>` only under the native gate `FlightVisible && BoardState == 2 && CarrierName != null` (`console.cpp:797`, `dialog.mdl:1508-1543`); nothing when the player is not a gunner. |
| 3 | Observer and turret board state | **Shared, unchanged** | The loadout OBSERVER/TURRET badge is the docked-screen board text (`DockedScreenRenderer` DrawBoardText, native `dialog.mdl:2086-2112`, ofInLoadout only, `console.cpp:797-804`). Native's flight HUD draws nothing for a board state, and Jet draws nothing either — one skin-neutral screen, no new widget. |
| 4 | Accepted order text | **Jet-drawn** | `:321` row 4 `ACC <order>` from `HudConsoleText.AcceptedOrder` (native verb + target, `console.cpp:875-890`, `dialog.mdl:1623-1640`). |
| 5 | Queued order text | **Jet-drawn** | `:321` row 3 `QUE <order>` from `HudConsoleText.QueuedOrder` (native `console.cpp:893-908`, `dialog.mdl:1605-1620`), drawn hot. |
| 6 | Chat history | **Jet-drawn** | `JetHudRenderer.Console.cs:144` `DrawChatPane`: the shared formatted window (`ChatLogFormat` already carries the channel tag and sender), the configured `HudChat.PaneLines` height through `ChatPaneLayout.ConfiguredLines`, the pane's `Offset` with the OLDER marker, the filter label, and leader lines in the **large (bolder) face** scaled to the row height (native `largeBoldFont`, `chatpane.cpp:34,93`). |
| 7 | Chat composition and recipient | **Jet-drawn** | `:210` `DrawComposePane`: all five native states through `ComposePaneLayout.Variant` (panel box, recipient box, verb icon, text box), the resolved `HudChat.Recipient`, the native caret (`console.cpp:1290-1292`), the shared 0.5 s `ComposeProgress`. Typing/focus unchanged (only read). |
| 8 | Command recipient verb and wing controls | **Jet-drawn** | `JetCommandConsole.cs` (cells derived from `CommandBarLayout`) drawn by `JetHudRenderer.Console.cs:503` `DrawCommandBar`; the hit test is the existing `Game1.Commanding.TryCommandBarPick` against the **same** `CommandBarLayout` rectangles, in the same precedence (wing → verb → recipient). P1040 test clicks every drawn cell and gets the same control/action as Classic/Modern. |
| 9 | Tip text lines | **Jet-drawn** | `:277` `DrawBottomLines`: the three retained ordinary slots (`dialog.mdl:5347-5349`, `OrdinaryTips`) at the bottom-left, newest brightest; the side colour is represented by ink brightness, never a hue. |
| 10 | Critical tip text lines | **Earlier story (P1038)** | The master-caution panel already draws the up-to-four critical tips with the shared 30 s / 7.5 s lifetimes (`HudConsoleFormat.CriticalTipLifetime`, `console.cpp:913-931`); P1040 leaves that as the single owner (the console column draws ordinary tips only, so no tip appears twice). |
| 11 | Vote banner | **Jet-drawn** (moved from the caution stack) | `:462` `DrawVote` centres the shared ballot text at the native offset `85 + 12·NumChatLines` (`dialog.mdl:5174`), fitted to the free centre span; the caution panel no longer repeats it (`JetHudRenderer.StoresCaution.cs:466-478` compacts the list before measuring). |
| 12 | Money credits | **Shared, unchanged** | Native has no flight credit counter: the credit box lives only inside the investment pane (`MoneyCreditsLayout`, `dialog.mdl:1885-1908,5199-5203,5368`). Jet draws no flight money readout; the investment pane is shared and untouched. |
| 13 | Lives remaining | **Earlier story (P1038)** | Caution badge `LIVES n` under the native <10 rule (`HudConsoleFormat.LivesVisible`, `console.cpp:812-818`). |
| 14 | Mission kills | **Earlier story (P1038)** | Caution badge `KILLS n` under the native deathmatch gate (`HudConsoleFormat.KillsVisible`, `console.cpp:818-821`). |
| 15 | Mission countdown | **Jet-drawn** | `:321` row 5 prints `MISSION T- mm:ss` (countdown game) or `MISSION T+ mm:ss` (elapsed) from the new `HudContext.MissionClockSeconds`/`MissionCountdown`, which `Game1.Rendering.cs` fills from the shared `MissionClock.DisplaySeconds` (`WinTrek.cpp:8128-8143`, `igc.h:1526-1529`). Null when the world has no mission/elapsed reading — no invented clock. |
| 16 | Ripcord countdown | **Earlier story (P1038)** | The caution line `RIP n` keeps the shared label and its null/rounding gate (`HudConsoleFormat.RipcordLabel`, `consoleedata.cpp:521-532`); the native centred 150-wide position is not reproduced (the Jet centre is the ladder/boresight), stated as the presentation difference. |
| 17 | Eject pod identity panel | **Jet-drawn** | `:321` row 6 `DrawConsoleColumn` draws the `LIFEPOD` panel with the `RETURN` binding from `HudContext.PodReturnInstruction`, only while `HudModel.PodVisible` (native of own-lifepod gate, `consoledata.cpp:578-590`). |
| 18 | Lifepod oxygen endurance | **Jet-drawn** | Same row: `OXY` with the **segmented endurance bar** (the shared fuel gauge fraction that `HudModel` relabels to `OXY`) plus the numeric endurance `nn s` from `HudFrame.OxygenSecondsRemaining`. The caution panel's `POD OXY` line leaves the stack while the panel is up, so the value is displayed once. |
| 19 | Death banner | **Earlier story (P1038)** | `PILOT DOWN / GHOST` under the same `HudModel.DeathVisible` (= `Context.Ghost`, native `IsGhost`, `WinTrek.cpp:3653`, `dialog.mdl:5438-5439`). |
| 20 | Boundary and crowd warnings | **Earlier story (P1038)** | `BOUNDARY` / `CROWD` caution lines from the shared warning mask; the native audio rising edges stay in `SoundManager.UpdateShipWarnings` (`WinTrek.cpp:8650-8666`), untouched. |
| 21 | Incoming missile warning | **Earlier story (P1038)** | `MSL TRACK nn%` from `HudContext.MissileTrack`; the native owner/source-ship loop (`MissileWarningController`, `trekigc.cpp:5047-5065`) is untouched — no duplicate controller. |
| 22 | Damage flash timer | **Earlier story (P1038)** | `DAMAGE` from the shared `DamageFlashTracker` 0.25/0.25/2.0 s clock (`WinTrek.cpp:35-37,8921-8958`); the tracker is not duplicated. |
| 23 | HUD style and software layouts | **Shared, unchanged** | The skin selector/cycle (`Shift+F12`, options row) and the centre/target/chat visibility controls are the P988 switcher, untouched; P1040 adds no variant (amber is P1041). |
| 24 | Console pane visibility and slide animation | **Jet-drawn** | The compose panel rides `HudChat.ComposeProgress` (the shared `PaneSlideTracker` value, `.5 s`, `dialog.mdl:5355`) and the CMD panel rides the new `HudContext.CommandPaneSlide` (the same tracker's `SlidingPane.CommandPane`, `dialog.mdl:5416`); the slide sound stays the host's mask-edge sound, untouched. |
| 25 | HUD base class contract | **Shared, unchanged** | `IHudSkinRenderer`/`HudSkinResources` still own model/camera/rect responsibilities; Jet remains one concrete presentation. P1040 adds only an optional `CommandChrome` callback (native NORTH marker + drag rectangle) so a skin can own the console without losing the camera affordance. |
| 26 | Game list screen boundary | **Deferred** | Screens (server browser chrome) are P1042 and explicitly out of this story's scope; the shared host screen is unchanged. |

### 2.2 §5.6 last rows and §5.3/§5.1

| HUD parity row | Verdict | Where / why |
|---|---|---|
| Inventory turret gunner identity | **Earlier story (P1038)** | The Jet F4 pane already draws the `TURRET n / occupant` and observer rows from `HudInventoryPane` (native `inventoryimage.cpp:263-289`, `invturret.mdl:3`); unchanged. |
| Command view ship summary | **Jet-drawn** | `JetHudRenderer.Console.cs:601` `DrawCommandSummary` prints the model's own `CommandLines` verbatim, centred in the free span — the same fields the host builds (`Game1.Commanding.cs:320-340`), no invented row (no money/AP echo, no "Target:" name). Drawn only in the command view. |
| Training HUD callouts (§5.3) | **Jet-drawn** | `:626` `DrawTrainingCallout` uses the shared `HudTrainingCallout` table for the anchors/second point/text (identical to `HudRenderer.DrawTrainingCallout`, native `indicator.cpp:461-701`) and draws thin vector strokes + the anchor dot in Jet ink; the training control flow and `TrainingOverlay.Current` are only read. The P949 controls hint is drawn at the shared anchor (`:445`). |
| Roll tutorial cue (§5.1) | **Jet-drawn** | `:658` `DrawRollPointer` is the ladder's bank index (the shared `HudFlightReference` roll, valid-only), and the roll training callout itself is the unchanged shared table row 17 (`PUSH Q AND E TO ROLL LEFT AND RIGHT`), so Jet adds a pointer rather than a new instrument. |

## 3. Changes

All work is presentation only; no server, Core, wire, simulation, audio, input-binding,
chat-routing, fan-out or training-lesson logic changed.

**New files (Jet-only, `src/Allegiance.ClientCore.Game/Rendering/Jet/`)**

| File | Contents |
|---|---|
| `JetConsoleLayout.cs` | The pure console placement rules: the console edge (84 standard units, pushed right of the measured speed plate), the native chat-pane width/height (`355 px`, `8 + 14·lines`), the column's right bound (`min(ladder−6, w/2−190)`), the centred banner span, the tip stack (`h−108`, 19 px pitch), the native cargo/flight-parts offsets (`h−132`/`h−158`), the compose travel (18 px), row pitch. No MonoGame types. |
| `JetCommandConsole.cs` | The `CMD` panel geometry: recipient/verb/wing cells derived from `CommandBarLayout` (the same rectangles `Game1.Commanding.TryCommandBarPick` tests), `HitTest` in the production precedence (wing → verb → recipient), the cell words, and the action words the test compares. |
| `JetHudRenderer.Console.cs` | `DrawConsoleJet` (chat history + compose + the two shared bottom lines), `DrawConsoleColumn` (sector/viewing, turret carrier, `ACC`/`QUE`, mission clock, `LIFEPOD`/`OXY`/`RETURN`), `DrawVote`, `DrawControlHint`, `DrawCommandConsole`/`DrawCommandBar`/`DrawCommandSummary`, `DrawTrainingCallout`, `DrawRollPointer`, and the `JetConsoleFitCache` (source+width+scale → fitted string). |

**Edited files**

| File | Change |
|---|---|
| `Rendering/Jet/JetHudRenderer.cs:46-90` | `CoverageIds` +14 console/chat/command/lifecycle/training ids (the P1036-P1039 ids are unchanged). |
| `Rendering/Jet/JetHudRenderer.cs:339-460` | `Draw` now draws the training callout, the roll pointer (yielding to the F4 pane), the console column, the vote banner and the controls hint, and resolves `_chatPane` once; `DrawCommand` calls the host chrome + the Jet CMD console; `DrawConsole` calls `DrawConsoleJet`. |
| `Rendering/Jet/JetHudRenderer.cs:205,809` | The speed readout plate's measured box is kept (`_speedPlate`, also furniture) so the console column can start right of it. |
| `Rendering/Jet/JetHudRenderer.StoresCaution.cs:453-478` | The caution list is compacted before measuring: `Vote` (the banner owns it) and `PodOxygen` while the pod panel is up leave the stack — one display per reading, no empty row band. |
| `Rendering/HudModel.cs:243-270` | `HudContext` gains `CommandPaneSlide`, `CommandWing`, `CommandRecipientColumn`, `CommandVerb`, `MissionClockSeconds`, `MissionCountdown` — presentation only (Classic/Modern never read them). |
| `Rendering/HudModelAdapter.cs:34-57,76-84` | Optional parameters for the same six values. |
| `Game1.Rendering.cs:3589-3600,3670-3680` | Fills them: the shared `PaneSlideTracker` progress for `CommandPane`, the handler's own wing/recipient/verb, and `MissionClockSeconds()` (the `MissionClock.DisplaySeconds` rule, null when unknown). |
| `Rendering/Skins/HudSkinResources.cs` | Optional `CommandChrome` callback (native NORTH marker + drag rectangle). |
| `Game1.Commanding.cs:441-500` | `DrawCommandHud` split into a chrome body (with a `commandBar` flag) and the summary loop; `DrawCommandChrome` is the host callback Jet uses so it keeps the camera affordances but not the native bar art. Classic/Modern behaviour is unchanged (same drawing, same order). |
| `Game1.GpuBudget.cs:40-48` | Binds `CommandChrome = DrawCommandChrome`. |
| `tools/hud2-proof/Program.cs` | New `--jet` scenes (chat/compose/CMD/tips/vote/orders/pod/ghost/turret/command/training/roll), the model values behind them, `DrawCommand`+`DrawConsole` in the capture and audit passes (production order), and the console boxes in `jet-capture.json`. |
| `tests/.../Skins/JetConsoleTests.cs` | New: 32 tests (chat, compose, CMD clicks, console rows, tips, vote, training, roll, allocation, training restore). |
| `tests/.../Skins/JetSkinScaffoldTests.cs` | The composition guard now expects the host to supply only tags/pod-death/chrome (Jet owns chat/cargo/parts/command). |

## 4. 800×480, 4K and the truncation rules

**Where the console lives.** Jet's own furniture already owns the top-centre (heading tape and
pitch ladder), the top-right (caution panel), the left rail (speed tape and its numeric plate), the
lower-left (stores block) and the bottom-right (radar/minimap). The console therefore occupies the
strip between them:

* **Chat pane** — left edge = `ConsoleLeft` (84 units), pushed right of the *measured* speed plate;
  top = below the heading plate **and** below the speed tape's caption (at 800×480 the tape reaches
  y 96, so the caption is the binding edge); one row is reserved above the pane for the OLDER/filter
  markers; width = native 355 px capped at the ladder's numeral column; height =
  `8 + rowPitch·ConfiguredLines`.
* **Compose panel** — directly under the log at the same left edge, riding the shared `.5 s`
  progress (18 px travel).
* **Console column** — under the chat/compose block, ending above the measured stores-block top
  (`_storesTop`), right bound `min(ladder−6, w/2−190)`.
* **Bottom lines** — the native cargo/flight-parts offsets, at the console edge, fitted to the
  column's right bound (never reaching the ladder numerals) and, in the command view, to the space
  left of the CMD panel.
* **Tips** — `h−108`, 19 px pitch, same left edge and width bound.
* **Vote** — centred at the native `85 + 12·NumChatLines` px offset, clamped above the ladder's top
  rung when the unscaled offset would reach it (800×480), width fitted to the free centre span.
* **CMD panel** — the native `CommandPane` rectangle **unscaled** (`274×92` at `SideBottom(-135,-190)
  →(-135,40)`), so its cells are exactly the hit rectangles; each cell word is drawn at a measured
  fit scale when the substitute font is wider than the native smallFont.

**Measured at 1920×1080 and 800×480** (from `jet-capture.json`, all values px):

| Element | 1920×1080 (`jet-chat-long`) | 800×480 (`jet-chat-long` / `jet-tips`) |
|---|---|---|
| Chat pane box | 182…537 × 249…482 (9 configured lines, 355×233, pushed right of the speed plate and below the tape caption) | 121…352 × 118…273 (9 lines, `jet-chat-long`) / 121…352 × 101…223 (5 lines, `jet-tips`) |
| Console column | 182…770 × 490…540 (SECTOR + MISSION rows) | 121…273 × 228.7…245.3 (`jet-tips`; one row) — none in `jet-chat-long`, whose 9-line log owns the band |
| Vote banner | native offset 169, centred (no collision at 1080) | 420…721 × 108.7…123.3 (clamped above the ladder's top numeral, centred in the free span) |
| CMD panel | 825…1099 × 948…1040 (native 274×92) | 265…539 × 348…440 (the same native rect) |
| CMD view chat + column | 84…439 × 35…218 / 84…770 × 226…276 | 56…293 × 23…145 / 56…273 × 151…184 |

(The exact per-scene boxes are in `jet-capture.json` → `console`; every §6 image was read back
against them.)

**Truncation rule (stated once, applied everywhere).** Text that cannot fit its slot is cut to its
longest prefix that fits with a trailing `...` — the ASCII ellipsis, because the HUD fonts bake only
32..126 and the brief's `…` would be sanitised to `?` by `HudFormat.Printable`. The slots are: a
chat line (`pane width − 6 px`), a console value (the column's right bound), the cargo/flight-parts
lines (the column's right bound), the vote (the centred span), the CMD wing name (cell width). A CMD
cell word is instead drawn at a fit *scale* (≥ 0.5) so short verbs stay whole and readable. Fitted
strings are cached per (source, width, scale), so a steady frame allocates nothing (test-pinned).

**Compact forms (800×480).** The console keeps every element, with four stated contractions:
(1) the chat pane keeps the configured line count (native `ChatPaneLayout` height, never shortened),
so with a 9-line preference the log fills the whole left column and the sector/orders rows below it
are **not drawn** at that size (`jet-chat-long` at 800×480: no column row; `jet-tips` with the
default 5 lines keeps one row at 228.7–245.3); (2) the CMD panel stays at its native 274×92 (no
shrink — the hit rectangles are the drawn rectangles); (3) a console row that does not fit above the
stores block is not drawn at all rather than clipped, and its value is never cut to fit vertically;
(4) the vote banner rides above the ladder's top numeral instead of the native unscaled offset.

## 5. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetConsoleTests.cs` (new, 32 tests, all
headless through a recording `IJetVectorSurface`; `Jet` = the production renderer):

| Test | What it pins |
|---|---|
| `The_chat_history_draws_the_shared_window_with_channel_sender_and_leader_face` | the shared formatted lines (channel + sender) are drawn verbatim, the leader line is in the large (bolder) face, the OLDER marker and filter label are drawn, the pane height is `ConfiguredLines` |
| `The_chat_pane_height_follows_the_configured_ChatLines_preference` | 2 vs 9 configured lines produce the native `8 + pitch·lines` heights |
| `A_long_chat_line_truncates_with_the_ascii_ellipsis_inside_the_pane` | the ellipsis is `...` (no `?`), the run stays inside the pane |
| `A_hidden_chat_history_still_draws_the_active_compose_line` | `ChatHistoryVisible=false` hides the log but keeps the composer (native gate) |
| `Every_compose_state_draws_the_shared_variant_with_recipient_and_caret` (5 cases) | all five `ComposePaneLayout` variants draw at their own panel size, the recipient is drawn, the caret `|` appears in every state but the command line |
| `The_compose_panel_rides_the_shared_slide_progress` | progress 0 → progress 1 moves the panel exactly `ComposeTravel` px |
| `A_not_composing_chat_draws_no_compose_panel` | no panel without a compose state |
| `Every_drawn_command_control_resolves_to_itself_through_the_jet_geometry` | every drawn recipient/verb/wing cell, clicked at its centre, resolves to itself through `JetCommandConsole.HitTest`, and Classic/Modern's production order (`CommandBarLayout.InWingCombo → VerbColumn → RecipientColumn`) picks the same control; verb cells map to the same `VerbCommandId`/verb text, recipient cells to the same `ChatTarget`; the drawn recipient band is above the verb band |
| `The_cmd_console_draws_the_native_panel_at_the_shared_slide_geometry` | the panel is the native 274×92 at `CommandBarLayout.Left/Top(w, 1)`, all cell words + the current wing are drawn |
| `The_cmd_console_is_absent_outside_the_command_view` | no panel/caption without the command view |
| `The_command_summary_prints_the_models_own_lines_and_nothing_more` | the summary is the model's `CommandLines` verbatim |
| `Sector_turret_and_orders_read_the_shared_console_text_under_their_gates` | sector, turret (BoardState 2 only) and ACC/QUE rows appear with the model's text; no turret row for BoardState 1 |
| `A_long_order_line_truncates_inside_the_console_column` | the value ends inside `ColumnRight` with `...` |
| `The_mission_clock_uses_the_shared_countdown_or_elapsed_form` | `MISSION T- 02:23` / `MISSION T+ 01:05` from the shared `MissionClock` rule |
| `The_lifepod_panel_prints_the_shared_oxygen_fraction_and_endurance_once` | `LIFEPOD`, `OXY`, `95 s`, `RETURN`; the caution stack no longer repeats `POD OXY` |
| `Three_ordinary_tip_slots_are_drawn_from_the_shared_slots` | all three retained tips drawn |
| `The_vote_banner_is_centred_at_the_native_offset_and_fitted_to_the_free_span` | native offset when the row is clear, clamped above the ladder's top numeral at 800×480, inside the free span |
| `Training_callouts_use_the_shared_table_geometry_and_text` (5 overlays) | the shared `HudTrainingCallout` text/description2 are drawn |
| `No_training_callout_is_drawn_without_an_overlay` | no callout with id 0 |
| `The_controls_hint_is_drawn_at_the_shared_anchor` | the P949 hint at `ChatPaneLayout.BottomFromTop + 6` |
| `The_roll_pointer_follows_the_shared_attitude_and_its_validity` | the pointer is drawn from the valid roll and absent without an attitude |
| `The_console_composition_allocates_zero_bytes_per_frame` | **0 B/frame** over 20,000 `Draw`+`DrawConsole` frames with a long chat line, a compose state, every console row, tips, the vote and a training callout |
| `A_lesson_started_under_jet_restores_jet_even_after_a_cycle_inside_it` | the pre-lesson snapshot restores Jet (§6 rule) |
| `A_lesson_started_under_jet_restores_modern_when_the_flag_went_off_meanwhile` | with `hud.jetSkin` off, `Request("jet")` is refused and the restore settles on Modern |

`JetSkinScaffoldTests` was updated for the new composition (the host now supplies only the ship
tags, pod/death and the command-camera chrome; chat/cargo/parts/command are the skin's own).

Results: see the gate log (§8).

## 6. Screenshots (every cited image opened and read)

`tools/hud2-proof --jet` renders **139 scenes**: the 96 P1035–P1039c scenes (unchanged scene
list) plus the 43 P1040 console/chat/lifecycle/training scenes at 1920×1080 and 800×480 on dark
and bright backdrops, with one 3840×2160 CMD frame. Both the PNG pass and the layout-audit pass now
call `Draw` → `DrawCommand` → `DrawConsole` in the production order, so the audit measures the
whole frame including the chat pane, the compose panel, the CMD console and the console column.

The kept assets (`docs/reports/JET_CONSOLE_P1040_assets/`, **1,911,211 bytes = 1.82 MB**, inside the
2 MB cap) are lossless RGB re-encodes (`ffmpeg -pix_fmt rgb24 -pred mixed`) of the rendered frames,
plus the 2× nearest-neighbour console crops and a per-scene audit JSON; the full capture with the
coverage/furniture lists is in `~/allegiance-evidence/P1040/jet-final/jet-capture.json`.

| Image | What is actually visible (read back) |
|---|---|
| `jet-chat-long-1920x1080-dark.png` | `HDG 045` tape, the chat pane on the left with `(OLDER - PgDn to newest)` and the `[TEAM]` filter label above its border, seven bracketed channel/sender lines (`[TEAM] Pilot Alpha: defend t...`, `[ALL] Pilot Bravo: incoming ...`, `[SECTOR] Pilot Charlie: WWW...`, `[TEAM] Pilot Delta: acknowle...`, `[ADMIN] Pilot Echo: quiet se...`, `[ALLIES] Pilot Foxtrot: mini...`, `[TEAM] Pilot Golf: on my way...`), then `SECTOR Alystra` and `MISSION T+ 02:05` underneath, the stores block and the ladder. |
| `jet-chat-long-800x480-dark.png` | The same window at the small frame: the 9-line pane sits between the speed caption and the stores block (121…352 × 118…273) and fills the whole left column, so no sector/orders row is drawn under it — the stated compact form (the row is omitted, never squeezed or clipped). |
| `jet-compose-chat-1920x1080-dark.png` | The `chattextbmp` variant under the log: the `[TEAM]` recipient box, the typed line with the `|` caret, the panel's left caret tick. |
| `jet-compose-command-1920x1080-dark.png` | The `chatcommandbmp` variant: the recipient box, the corner verb mark at the native icon slot, the typed command line without a caret. |
| `jet-compose-mouse/…-shell/…-quick-1920x1080-dark.png` | The narrow `chatmousebmp` panels (recipient + input, 73 px) and the wide `chatshellbmp` panel (229 px text box). |
| `jet-compose-chat-800x480-dark.png` | The same chat compose panel at 800×480, inside its compact band. |
| `jet-cmd-1920x1080-dark.png` | Command view: the `CMD` caption, the 274×92 panel with `ME SECTOR TEAM ALL` (ALL marked as the current recipient), the `WING / DELTA` combo, the nine verb cells `ATK CAP DEF PIK GO REP JOIN MINE BLD` (DEF marked), the centred summary `COMMAND SHIP: Fig Adv`, `Hull 82% Shield 70% Speed 260 mps`, `RECIPIENTS: 9 wings + 3 ships …` at the top. |
| `jet-cmd-800x480-dark.png` | The same 274×92 panel at native size in the small frame (265…539 × 348…440), every cell legible, with the chat pane and the SECTOR/QUE rows above it. |
| `jet-commandview-1920x1080-dark.png` | The command view without the CMD panel's alternate state: summary lines at the top centre, the console column under the chat pane, the radar bottom-right. |
| `jet-tips-1920x1080-dark.png` | `MASTER CAUTION` at the top right with two critical tips (`Reloading ammo...`, `Enemy carrier spotted in sector seven`), the `Vote: Mutiny against the commander? (Y/N)` banner, and the three ordinary tips at the bottom left (`Welcome to the sector`, `Alystra: 3 miners on duty`, `Payday: $500 deposited into your account balance`). |
| `jet-tips-800x480-dark.png` | The same scene at the small frame: the vote banner rides above the ladder's top rung, two caution lines, three tips at the bottom left. |
| `jet-orders-text-1920x1080-dark.png` | `SECTOR Alystra`, `QUE attack Enemy fighter` (hot) and `ACC defend Garrison` in the console column. |
| `jet-podoxy-1920x1080-dark.png` | `SECTOR Alystra`, `MISSION T+ 02:05`, then `LIFEPOD`, `OXY 95 s` with the segmented endurance bar, `RETURN Shift+Z / G AUTOPILOT TO BASE`; the stores block reads `OXY 63%` from the same gauge. |
| `jet-podoxy-800x480-dark.png` | The same lifepod panel at 800×480 inside the compact column. |
| `jet-turret-1920x1080-dark.png` | `TURRET / Pilot Alpha` under the sector row (BoardState 2), with the turret throttle state. |
| `jet-ghost-1920x1080-dark.png` | The ghost lifecycle: `PILOT DOWN / GHOST` in the caution panel with `LIVES 3` / `KILLS 7`. |
| `jet-training-1920x1080-dark.png` | The thin-vector roll callout `PUSH Q AND E TO ROLL LEFT AND RIGHT` + `PUSH V TO TOGGLE MOUSE CONTROL` with its two legs and anchor dot, and the `Shift+F1 opens the controls` hint at the shared anchor. |
| `jet-training-800x480-dark.png` | The same callout/hint at the small frame (the hint sits above the ladder's numerals). |
| `jet-rollcue-1920x1080-dark.png` | A banked attitude with the ladder roll pointer off the centre line above the top rung. |
| `console-chat-2x.png` | 2× nearest-neighbour crop: the OLDER marker, `[TEAM]` filter, every chat row with its ellipsis, and the `SECTOR`/`MISSION` rows — no row touches another. |
| `console-cmd-2x.png` | 2× crop of the CMD panel: `ME SECTOR TEAM ALL`, `WING`, and `ATK CAP DEF PIK GO REP JOIN MINE BLD`, each inside its own cell. |

**Vision caveat (stated as the brief requires).** The 800×480 frames are small; at that size the
vision read-back can misread individual glyphs (e.g. it read the `MISSION T-` hyphen as a colon and
`HULL` as `WULL`). Every position claim above was therefore checked against the measured boxes in
`jet-capture.json` (`console.chatPane`, `console.consoleColumn`, `console.vote`,
`console.commandPanel`, `console.compose`, `console.lifepod`) and the 2× crops, which are
unambiguous; no position is described that the measured rectangles contradict. The layout audit is
0 for every class in all 139 scenes (see §8).

## 7. Coverage

`JetHudRenderer.Coverage` grows from 64 to **76** `HudElement` ids — the P1036–P1039c ids unchanged
plus the twelve Jet now draws itself:

* `Chat history`, `Chat composition and recipient`, `Command recipient verb and wing controls`,
  `Sector location`, `Turret carrier identity`, `Accepted order text`, `Queued order text`,
  `Tip text lines`, `Mission countdown`, `Eject pod identity panel`, `Command view ship summary`,
  `Training HUD callouts`.

Rows Jet still reaches through an earlier story (and says so, rather than claiming them again):
`Critical tip text lines`, `Vote banner` (now drawn by the banner *and* removed from the caution
stack), `Ripcord countdown`, `Lives remaining`, `Mission kills`, `Death banner`, `Boundary and crowd
warnings`, `Incoming missile warning`, `Damage flash timer`, `Lifepod oxygen endurance` (the panel
now prints the endurance, the caution line yields) — all P1038, all still drawn.

## 8. Gate log

| Gate | Command | Result |
|---|---|---|
| Baseline (before any edit) | `hud2-proof --capture ~/allegiance-evidence/P1040/baseline` | **1,848 combinations** captured (924 Classic + 924 Modern, decoded-RGBA goldens + semantic snapshots) from the unmodified tree |
| Zero-delta | `hud2-proof --compare ~/allegiance-evidence/P1040/baseline` | **`1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)`** — Classic and Modern unchanged |
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Tools build | `tools/build-tools.sh` | **build-tools: all 141 tool projects built**, each 0 warnings / 0 errors |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetConsoleTests` ×3 | **32/32 on each of the final three runs** (431 / 433 / 431 ms) |
| Skin suite 3× alone | `dotnet test --filter FullyQualifiedName~Skins` ×3 | **387/387** on each of three final runs (27 s each) |
| Jet evidence capture | `hud2-proof --jet ~/allegiance-evidence/P1040/jet-final` | **`HUD2_JET_CAPTURE_PASS 139 scenes`**; audit **stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0, text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0, text-overlap-same 0**; radar **text-over-glyph 0, stroke-through-text 0** |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **Final run `tiers-20261117T105007.709479Z`: 13,657 tests, 267.6 s — FAIL, infrastructure only.** MonoGame 5,803: 5,798 passed / 4 skipped / **1 failed** (`TurretGunnerFireLiveTests.Gunner_send_loop_makes_the_server_create_projectiles_along_its_orientation`, live server; **3/3 alone**); Core.Tests aborted after **5,084 passed / 3 skipped** because the test host crashed on the ledgered background transport exception (`InvalidOperationException: not connected`, `TcpTransport.Send:600` ← `Bot.Send:1895` ← `Bot.OnReceive:2340` — the P1039 entry in `docs/TEST_FLAKES.md`: no owning test attributed, does not recur), so 1,468 expected cases are reported missing; 3 calibrated-limit offenders, timing only. No assertion failure in any P1040 file. **Earlier complete run (`tiers-20261117T100635.548399Z`): 15,240 tests, 669 s**, every failure 3/3-alone in its own family and ledgered (P1040 section of `docs/TEST_FLAKES.md`) — one of them was the P1040 harness leaking the process-wide `KeyBindingStore`, fixed in `Dispose`. Skin suite after the fix: **387/387 ×3**; `JetConsoleTests` **32/32 ×3**. |
| Training gate | see below | see below |
| Glossary links | `python3 tools/check-glossary-links.py` | **GLOSSARY.md link check OK (941 distinct paths)** |
| Parity scorecard | `python3 scripts/parity-scorecard.py` | clean (no new matrix rows: P1040 ports no rule, only Jet drawing — every native citation is in §2 of this report) |
| AOT publish | not run — no `src/Allegiance.Server` / `src/Allegiance.Core` change | n/a |
| `docker compose config` | not run — no service/port/store/env change | n/a |

**Training gate.** No training-affecting file outside `Rendering/Jet/` changed: the lesson logic
(`TrainingController`, `TrainingIntegration`, the overlay ids) is untouched, and the only non-Jet
edits are the HUD model/adapter fields, the skin-resource record, the command-chrome split and the
proof/tests. The training *restore* path is exercised end to end by the two new
`JetConsoleTests` training tests (3×, green). So the `tools/run-training-tests.sh` gate is not
required; the files changed outside `Rendering/Jet/` are listed in §3.

## 9. Anything unfinished / caveats

* **Ripcord position** — the caution line `RIP n` (P1038) keeps the shared label/gate, but the native
  centred 150-wide position is not reproduced: Jet's centre is the ladder/boresight. Stated in §2.
* **Board state and money rows** — deliberately shared/unchanged (loadout-only badge; investment-pane
  credits), not re-drawn in Jet.
* **Deferred** — §5.5 row 26 (game-list screen chrome) is P1042 scope, per the brief.
* **The warning/sound edges** are the host's: no audio, no timer, no acknowledgement is duplicated;
  the caution panel still aggregates the same shared warnings, minus the two readings that now have
  their own display (vote banner, lifepod endurance).
* **Vision at small fonts** — the 800×480 read-back can misread individual glyphs; every position
  claim in §6 was checked against the measured boxes and the 2× crops (stated there).
* **New media** (25 files = 24 PNGs + `jet-capture.json`, 1,911,211 bytes = 1.82 MB) needs Domingo's review before any push, per the
  lane rules; the full capture and the raw scene set are in `~/allegiance-evidence/P1040/`.
* The brief file `P1040_BRIEF.md` is removed from the worktree root so it cannot enter the change set.

## Native intent

Native owns every reading the Jet console shows. The chat list is `ChatListPane` — 355 px wide,
`8 + itemHeight·NumChatLines` tall, hidden scrollbar, `CommPane SideTop(-160,-126)`
(`dialog.mdl:180`, `chatpane.cpp:374-380`, `WinTrek.cpp:3317`); each line is stamped with the
sender's side colour and `IsFromLeader` (bold face, `chatpane.cpp:34,93`, `trekigc.cpp:4498`) and the
five compose states are the `ComposingPane` variants with their inner boxes and the `m_strTypedText +
"|"` caret (`dialog.mdl:89-170`, `console.cpp:524-529,1290-1292`), sliding over the native 0.5 s
(`dialog.mdl:5355`). The command bar is the native `CommandPane` (`dialog.mdl:73-87`, `.5 s` at
`dialog.mdl:5416`) whose recipient/verb/wing rectangles the native picker tests in the order wing →
verb → recipient (`console.cpp:1134-1192,1264-1278`); the summary rows are the port's own
`BuildCommandHudLines`. Sector/turret/orders are `SectorName`/`GetName(Me)`/verb+model name
(`console.cpp:797,870,875-908`), the mission clock is native's
`GetCountDownTime() − dt` vs `dt` (`WinTrek.cpp:8128-8143`, `igc.h:1526-1529`), the pod panel is the
`EjectPodImage` identity (`dialog.mdl:1922-1996,5436`) and its `GetEndurance` gauge
(`consoledata.cpp:534-545`), the vote banner is `VoteTextImage` at `85 + 12·NumChatLines`
(`dialog.mdl:2114-2120,5174`), the tips are the three retained slots and the four critical slots with
`fTipTextDuration = Training ? 30 : 7.5` (`dialog.mdl:2006-2033,5347-5349`, `console.cpp:913-931`),
and the training callouts/roll cue are `RenderTrainingOverlay`'s table (`indicator.cpp:461-741`).
Native 2-D conventions still apply unchanged: y-up anchors (every `dialog.mdl` point negated into the
port's y-down space), `StringImage` hanging below its point, and group paint order — the Jet console
placement is a Jet composition over the same readings, not a new model.

## Modern approach

The console family is one pure layout module (`JetConsoleLayout`) plus one pure command-geometry
module (`JetCommandConsole`) and a partial renderer that only reads the shared model: chat/cargo/
flight-parts and the command composition are the skin's own now (the host keeps the ship tags, the
pod/death overlay and the command-camera chrome), and every drawn CMD cell is the rectangle the
production hit test uses, so draw and hit can never disagree. Placement is rule-based and measured
(`ConsoleLeft` pushed right of the measured speed plate, the pane below the heading/caption furniture,
the column ending above the measured stores top, the vote clamped above the ladder at 800×480), and
every fitted string is cached per (source, width, scale) so a steady frame is 0 B/frame. The layout
audit — stroke-vs-text, text-outside-plate, text-off-screen, icon size, text-in-plate,
cue-vs-readout, legible-behind-pane, TextOverlapCross, TextOverlapSame, TextOverGlyph and the radar
StrokeThroughText — is 0 in all 139 Jet scenes, and Classic/Modern stay pixel- and
semantically identical over the full 1,848-combination matrix. The one display per reading rule
is applied explicitly: the vote moved out of the caution stack to its banner, and the pod's OXY
endurance moved to the LIFEPOD panel.
