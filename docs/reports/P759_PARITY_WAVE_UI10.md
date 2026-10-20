# P759 — interface parity wave 10: mop-up of every remaining non-HUD interface row

Base: master at dispatch, branch `pi/p759`, worktree `allegiance-pi-p759`. No commits or
pushes; all changes remain in the working tree.

## Scope decision

Read P743 (correctness pass), P748 (UI6) and P752/P752b/P752c (UI7, `pi/p752*`) first, then
P757/P757b (UI8, `pi/p757*`) and checked every unmerged `pi/*` branch
(`git branch --list "pi/*" --no-merged master`). The non-`hud` interface rows those branches
own were skipped, together with the functions they edit:

- **`pi/p752b`/`pi/p752c`** own the game-over tables (3 rows), team-pane controls (3 rows),
  sector-info helium/hover (2 rows) and the starmap-threat row — all skipped.
- **`pi/p757`/`pi/p757b`** own the chat pane (2 rows), F-key help manual, generic
  target-to-order interaction, key-rebind OK/Cancel, teleport recovery, splash, and the five
  intro rows — all skipped. Their `Game1.FlightInput.cs` chat-compose seam also owns
  `TK_StartChat`, and their `CommandOrderResolver` owns the commander default-order row, so
  those were left untouched.
- **`pi/p758`** owns the `fedmsg` client handlers for `WARP_BOMB(188)` and
  `ACQUIRE_TREASURE(162)` (`ClientWorld.WarpBomb.cs`, `TreasureMountPicker`), and
  **`pi/p757`** owns `BOARD_NACK(178)` — all three open `fedmsg` rows were skipped.
- HUD branches own `hud` rows (out of scope); p751/p753/p754/p755 own simulation rows.

After those skips the genuinely implementable non-`hud` interface rows share four clusters.
This wave implements **10 rows** (weight 20.334) around them. Training rows, the career
leaderboard/character-info cluster (no persistent career store), the server-list
sort/filter/find-player cluster (no lobby player data), and the Steam moderator ban row
(no Steam identity) are left open with reasons in "Unfinished".

## Rows closed (10, all `partial`/`missing` → `done`)

Native citations are `~/Development/Allegiance/src/`, read directly.

| Area | Row | Weight | Native rule | Production + test |
| --- | --- | ---: | --- | --- |
| commands | TK_ToggleLODSlider | 2.000 | WinTrek.cpp:9448-9453 (toggle `m_pwrapImageLOD`) | `Rendering/LodControl.cs ToggleVisible`; `Game1.FlightInput.cs:964`; `LodControlTests.ToggleVisible_flips_the_native_slider_image_state` |
| commands | TK_LODUp | 2.000 | WinTrek.cpp:10415 (`m_pscrollPane->PageUp()`) | `LodControl.PageUp` (+10, clamp 100); `Game1.FlightInput.cs:967`; `LodControlTests.PageUp_and_PageDown_move_by_page_size_and_clamp` |
| commands | TK_LODDown | 2.000 | WinTrek.cpp:10421 (`m_pscrollPane->PageDown()`) | `LodControl.PageDown` (−10, clamp 0); `Game1.FlightInput.cs:970`; same test |
| commands | TK_ConModeInvest | 3.000 | WinTrek.cpp:10025-10033 (airborne toggle gated on `AllowDevelopments()`) | `Game1.Panes.cs HandleInvestmentScreen` (was already airborne; now gated) + `InvestmentPaneModel.TogglePermitted`; `InvestmentPaneModelTests.ConModeInvest_ToggleRequiresDevelopmentsAndEdge` |
| screens | Loadout panel (shop) / Hull and part purchase gates | 1.666 | load.cpp:1498-1554 (`OnButtonBuy`/`OnButtonLaunch`: `cost = new − (current+money)`; `REQUEST_MONEY` shortfall) | `ShopModel.BuyShortfall`; `Game1.Hangar.cs BuyFromHangar` (REQUEST_MONEY else `BUY_LOADOUT(fLaunch=false)` + save); `ShopModelTests.Buy_shortfall_is_new_value_minus_current_and_money` |
| screens | Loadout panel (shop) / Native saved and default loadout controls | 1.668 | load.cpp:1639-1648 (`OnButtonDefault`); load.cpp:1478-1486 (`BuyHull → RefreshLoadout(customLoadouts[0], phullType)`); clintlib.h:1425-1428 (`SaveCustomLoadout`) | `ShopModel.SaveCustomLoadout`/`ApplyDefaultLoadout`/`RebaseForEffectiveHull`; `Game1.Hangar.cs Default` action; `ShopModelTests.Default_loadout_is_saved_and_reused_on_the_next_hull_swap` |
| screens | Mission briefing screen / Civilization-generated briefing and tips | 1.500 | teamscreen.cpp:732-793 (`GetCivMissionBriefing`), 820-826 (`GetMissionTip`) | `MissionBriefingGenerator` (P759b: builds the text from the artwork's `missionbrief.mdl` civ strings, no invented prose) + `ClientWorld.MissionBriefing` + `MissionBriefingInfo` wire; `MissionBriefingGeneratorTests` |
| lobby | Mission briefing screen / Civilization-generated briefing and tips | 2.000 | same native rule | shared with the screens row; `SendMissionBriefingInfo` (Server.Lobby.cs) is the production caller |
| lobby | Connect to server (host/IP + character) / Connect progress failure retry and abort | 2.500 | introscreen.cpp:1667-1699 (`OnLogonGameServerFailed`), 1794-1797 (`OnAbort`) | `Game1.Session.cs BeginConnect` stages `ConnectFailed`; `UpdateConnectFailed` (R retry / Esc abort); `AbortConnect` + `ConnectScreenLayout.AbortDestination`; `ConnectScreenLayoutTests.Abort_destination_prefers_intro_then_lobby_then_exit` |
| commander | Team leadership / owner (promote, owner-only cmds) / Donation-preferred departure succession | 2.000 | fsmission.cpp:955-991 (Started → `PickNewLeader`); FedSrv.CPP:13981-14024 | (P759b: the duplicate `PickNewLeaderByDonation` was dropped; master's P758 implementation is authoritative) `Server.Lobby.cs:2378 HandleLeaderSuccession` runs `PickNewLeader` on a Started side with the first-other fallback; `LiveTeamLeadershipTests.Started_mission_leader_succession_prefers_donation_target` |

The CSV evidence column carries the full per-row citations; only the rows above were edited.

## Before → after scorecard

Exact output from `python3 scripts/parity-scorecard.py` (raw in
[P759_assets/scorecard-before.txt](P759_assets/scorecard-before.txt) /
[scorecard-after.txt](P759_assets/scorecard-after.txt)):

| Matrix / area | Before | After |
| --- | --- | --- |
| main / commander | 84.15% (68.164/81); 39/46 | **86.62% (70.164/81); 40/46** |
| main / commands | 95.09% (562/591); 177/190 | **96.62% (571/591); 181/190** |
| main / fedmsg | 97.63% (495/507); 120/123 | unchanged (skipped: owned by p757/p758) |
| main / hud | 1.85% (6/324); 2/108 | unchanged (out of scope) |
| main / lobby | 69.96% (32.880/47); 22/33 | **79.53% (37.380/47); 24/33** |
| main / parts | 100.00% (153/153); 40/40 | unchanged |
| main / screens | 64.99% (74.093/114); 55/91 | **69.23% (78.927/114); 58/91** |
| main / TOTAL | 76.56% (1391.137/1817); 455/631 | **77.68% (1411.471/1817); 465/631** |
| sim-rules / TOTAL | 81.62% (1048/1284); 332/419 | unchanged (no simulation rows) |

Delta: **+10 rows, +20.334 weighted credit** — exactly the 10 rows' weights. Denominators are
unchanged; no `n/a`, no exclusions, no weight edits, no new rows.

## Production changes

- **`Rendering/LodControl.cs` (new)** — pure port of the native LOD scroll pane
  (`WinTrek.cpp:3179-3185, 3553-3555, 9448-9453, 10415-10424`): value 0..100, page step 10,
  initial 90, bias `value/100`, toggle visibility. `ShipLodProfile.LodBias` +
  `ShipLod.ApplyLodBias` reproduce `SetLOD(screenRadius * lodBias)` (thinggeo.cpp:1624,
  1750-1753). The three commands expose their native MDL defaults in the catalogue:
  `TK_ToggleLODSlider` = `Alt+J` (shipped unbound because that chord already belongs to
  ToggleMusic), `TK_LODUp` = `Alt+[`, `TK_LODDown` = `Alt+]`.
- **`Game1.FlightInput.cs`** — edge-triggered LOD keys in flight; the renderer copies
  `_lodControl.Bias` into `_shipLodProfile` before every ship draw, and a small slider overlay
  is drawn when visible.
- **`Game1.Panes.cs`** — `HandleInvestmentScreen` now gates the airborne investment toggle on
  `AllowDevelopments` (native `TK_ConModeInvest`); the pure edge decision lives in
  `InvestmentPaneModel.TogglePermitted`.
- **`ShopModel.cs`** — `BuyShortfall`, `SaveCustomLoadout`, `ApplyDefaultLoadout`, and
  hull-swap re-seed from the saved custom-loadout slot 0 (`RebaseForEffectiveHull`).
  `HangarControls` gained `Buy` and `Default`; `Game1.Hangar.cs BuyFromHangar` sends
  `REQUEST_MONEY` for the shortfall or `BUY_LOADOUT(fLaunch=false)` + save, with the existing
  disembark-then-buy sequence for a boarded ship.
- **`Game1.Session.cs`** — connect failures now stage a `ConnectFailed` screen with
  R-retry / Esc-abort instead of silently bouncing back; `ConnectScreenLayout.AbortDestination`
  is the pure abort-routing helper (intro wins, then lobby, else exit).
- **`MissionBriefingInfo` (new wire message, Fmid 996)** — Brotli source-generated-JSON
  **port-only extension** (native reads the mission parameters client-side from the live
  `MissionParams`/side/civ objects, so no native wire counterpart exists) sent in the side
  join-sync beside `SET_BRIEFING_TEXT(9)`, carrying side/civ names, the civ's mdl string
  prefix (`GetIconName`), civ list, briefing text, the game-length countdown
  (`dtGameLength`) and the server's authoritative win-condition flags (native
  `GetCivMissionBriefing` reads these live). `ClientWorld.MissionBriefing` stores it;
  `MissionBriefingGenerator` (P759b: rewritten to read the artwork corpus's
  `missionbrief.mdl` and format the native `NoWin`/`Default` main text,
  `SecondaryObjectives` lines and random tip exactly as `teamscreen.cpp:732-826`; no
  invented prose) produces the briefing, and `Game1.Rendering.cs` feeds it to the existing
  `MissionBriefingRenderer`.
- **`Server.Lobby.cs`** — `SendMissionBriefingInfo` join-sync. (The P759
  `PickNewLeaderByDonation` duplicated master's P758 donation-preferred succession and was
  removed in P759b during the merge.)

## Verification

- **Whole-solution build** `dotnet build AllegianceCore.slnx -c Release`: **0 warnings /
  0 errors**, including the new `tools/p759-ui-proof`.
- **AOT publish** (`Allegiance.Server`, `-r linux-x64 --self-contained -p:PublishAot=true`):
  clean, **no IL2026/IL3050** (Core + Server changed). Output: `.scratch/aot-p759`.
- **New/affected tests 3× alone, all green:**
  - MonoGame.Tests (`LodControlTests`, `InvestmentPaneModelTests`, `ShopModelTests`,
    `ConnectScreenLayoutTests`, `ClientWorldBriefingTests`, `MissionBriefingGeneratorTests`,
    `P748InterfaceTests`, `P642ConsoleZoomTests`, `P625bActionMapBehaviorTests`): 153/153 × 3.
  - Core.Tests (`LiveTeamLeadershipTests`, 12 tests incl. the new donation-vote test): 12/12 × 3.
- **One fast-tier run** `tools/run-tests.sh --tier fast`: **PASS — 8,979 tests, 267.9 s**.
  The first sweep caught one real regression in `P642ConsoleZoomTests`
  (`TK_ToggleLODSlider` must expose native `Alt+J`); fixed by setting the native-default
  chord on the three LOD actions, then the full fast tier re-ran clean.
- `git diff --check` clean; `parity-scorecard.py` validates both matrices (665 + 473 rows).

## Real-client evidence (1280×720 backbuffer captures, 340 KiB ≤ 2 MB)

Reproduced with [tools/p759-ui-proof/Program.cs](../../tools/p759-ui-proof/Program.cs) — a real
ephemeral loopback server + the real `Game1` DesktopGL client; no seeded rows or composed
images. State transitions are driven through production handlers/fields. PNG integrity and the
non-blank content check are in the proof run log; [client-proof.json](P759_assets/client-proof.json)
records ship/station per capture.

| Visible behaviour | Capture |
| --- | --- |
| Docked hangar button row with the new Buy + Default buttons (Buy hovered) | [hangar-buy-default-720p.png](P759_assets/hangar-buy-default-720p.png) |
| LOD slider overlay in flight (bias label; P759b re-shot: no briefing overlay, slider clear of every chat/HUD line) | [lod-slider-720p.png](P759_assets/lod-slider-720p.png) |
| Mission briefing with the native `missionbrief.mdl` civ prose + secondary objectives + tip (P759b re-shot) | [briefing-civ-720p.png](P759_assets/briefing-civ-720p.png) |
| Connect-failure retry/abort screen | [connect-failed-720p.png](P759_assets/connect-failed-720p.png) |

## Native citations (spot-checked this pass)

- LOD slider/bias: `WinTrek.cpp:3179-3185, 3553-3555, 9448-9453, 10415-10424`;
  `engine/controls.h:339-370, controls.cpp:1654-1700`; `effect/thinggeo.cpp:1618-1624, 1674, 1750-1753`.
- Loadout buy/default/save: `WinTrek/load.cpp:1498-1554, 1478-1486, 1639-1648`;
  `clintlib/clintlib.h:1390-1445, 2110-2160`.
- Mission briefing: `WinTrek/teamscreen.cpp:732-793, 820-826, 1668-1704`.
- Connect retry/abort: `WinTrek/introscreen.cpp:1667-1699, 1788-1797`.
- Leader succession: `FedSrv/fsmission.cpp:949-990`; `FedSrv/FedSrv.CPP:13981-14024`.

## Unfinished / known limits

- **Not picked this wave** (still open in the matrix, with the reason they were left):
  training rows (out of scope); `TK_StartChat` and the commander default-order row (owned by
  `pi/p757`'s unmerged chat-compose / `CommandOrderResolver` code); `fedmsg` 162/178/188
  (owned by `pi/p757`/`pi/p758` unmerged client handlers); `TK_ViewTurret` (native handler is
  disabled — deliberate port extension, left `partial`); career leaderboard/character-info
  rows (need a persistent career store); server-list sort/filter/find-player rows (need lobby
  directory player data); lobby chat relay (directory social channel); Steam moderator ban
  (no Steam identity); mission briefing custom-story-text branch (no `m_strStoryText` to
  transmit — the port always generates civ prose).
- The LOD slider's English wording is the port's own overlay text; the value domain, page step,
  initial value and bias formula are the faithful native parts. `TK_ToggleLODSlider` ships
  unbound because native's `Alt+J` default collides with the port's existing ToggleMusic
  `Alt+J`; `Alt+J` is shown as the native suggestion (also noted on the matrix row).
- ~~The mission-briefing civ prose uses the port's English content with the native structure;
  native's MDL helpdata wording is content, not ported. The port has no `IsCountdownGame()`
  flag, so a countdown secondary objective is not emitted~~ — **superseded in P759b** (see
  below): the prose now comes from the artwork's `missionbrief.mdl` and the countdown
  minutes flow through the forwarded `dtGameLength`. The one remaining gap is the prosperity
  money amount (`fGoalTeamMoney` is not carried, so the line renders "costing 0 credits").
- Connect retry re-runs the same host/port (native re-opens the callsign/logon dialog); the
  abort routing matches native's origin-return decision.
- The docked hangar detail panel overlaps the left hull list and the hotkey hint row
  (visible in `hangar-buy-default-720p.png`); that overlap predates P759 (it is present in
  the original P759 evidence shot) and was out of scope for P759b.

## Review fixes (P759b)

Fix round on Claude's review of this report. Base `pi/p759b` = `pi/p759` (51d4a8b4) merged
with master (P757 `Splash` + P758 leader succession + HUD rows): the merge kept both
`ClientState.Splash` and `ConnectFailed`, and took master's side for the leader-succession
code, test and CSV rows.

1. **Mission briefing text is no longer invented.** `MissionBriefingGenerator` was rewritten
   to build everything from `missionbrief.mdl` in the artwork corpus (resolved through
   `ExternalPaths`/`local.env`) — the data native reads through the `missionbrief` modeler
   namespace (`GetCivMissionBriefing`/`GetMissionBriefingTitle`/`GetMissionBriefingString`/
   `GetMissionTip`, `WinTrek/teamscreen.cpp:718-826`; the `GetCivString` civ-prefix rule is
   teamscreen.cpp:629-639). The port now carries: a small Latin-1 reader for
   `name = "…";` lines (comment lines skipped — the file's commented `//Tip11` duplicate
   never enters the pool; `\n`/`\"` escapes unescaped); the `NoWin` vs `Default` selection
   formatted with the side name, briefing text and civ list (the `_snprintf` arguments of
   teamscreen.cpp:742-750; native runs the side name through `CensorBadWords` — the port has
   no profanity filter and uses the server-validated name verbatim); the
   `SecondaryObjectives` header when any of artifacts/flags/countdown/conquest≠100/
   deathmatch/prosperity applies, then each formatted line with its native number
   (conquest %, artifact count, flag count, countdown minutes = `dtGameLength/60`, kill
   limit, prosperity money — teamscreen.cpp:752-793); and the random tip with
   `numTips = "40"` and `Tip1..Tip40`, picked as `rand() % numTips + 1`
   (teamscreen.cpp:820-826). The invented 4-tip array and all hand-written prose were
   deleted; the mdl `Title`/`String` strings replace the synthesized title and render as
   the two native header panes. `MissionBriefingGeneratorTests` was rewritten (11 tests):
   NoWin vs Default selection against the real file, the secondary-objective lines with
   their formatted numbers, the tip pool size (40) with every tip from the file,
   comment/escape parsing, civ-list dedup (native's `GetCivListString`), Title/String.
   Re-shot [briefing-civ-720p.png](P759_assets/briefing-civ-720p.png): it now shows the
   native Iron Coalition text ("Your squadron Alpha have been assigned to Serpentis. Hostile
   Belters units nearby… Other objectives include: * Control 65% of the bases. * Capture and
   return 4 of the artifacts."). The live join-sync path renders the same way — the proof
   run's loopback server delivers its own Dreghklar briefing and the client shows the native
   "Imperial Decree" mdl text.
2. **Duplicate leader succession dropped.** `PickNewLeaderByDonation`, its matrix row and
   its test were removed; master's P758 already implements donation-preferred succession by
   reusing `PickNewLeader` (Server.Lobby.cs:2378 `HandleLeaderSuccession`,
   fsmission.cpp:955-991, FedSrv.CPP:13981-14024), including the id-ordered `remaining`
   list this merge originally kept divergent.
3. **Wire extension labelled.** `MissionBriefingInfo` (fmid 996, Brotli JSON) is documented
   as a port-only extension in its `///` doc and in the Production changes above (native
   reads the mission params client-side from IGC). P759b also extended it with
   `CivIconName` (the `GetCivString` prefix, teamscreen.cpp:636-639) and `CountdownSeconds`
   (`dtGameLength`, igc.h:1553-1556), forwarded by `SendMissionBriefingInfo`.
4. **LOD evidence re-shot.** [lod-slider-720p.png](P759_assets/lod-slider-720p.png) now has
   no briefing overlay, and the slider moved from `h−84` — it covered the bottom-left
   console/chat line ("Ceto secured") — to `h−120`, clear of the status line above and the
   nav readout below. The `TK_ToggleLODSlider` matrix row now notes that it ships unbound
   because native's `Alt+J` is taken by ToggleMusic.

P759b verification: whole-solution build 0/0; AOT publish (`-r linux-x64 --self-contained
-p:PublishAot=true`) clean, no IL2026/IL3050 (Server + Core changed); affected tests 3×
alone, all green (`MissionBriefingGeneratorTests`+`ClientWorldBriefingTests`+
`LodControlTests` 22/22 ×3; `LiveTeamLeadershipTests` 12/12 ×3; `HelpOverlayTests` 28/28
×3); one fast-tier run PASS (9,268 tests, 270 s). The first fast tier caught
`HelpOverlayTests.Table_BalancesIntoTypicalPanelWithoutOverflow` failing 3/3 alone — the
merge pushed the CAMERA/VIEW group past what the legacy whole-group
`HelpOverlayLayout.Layout` can place in any single 1440p column; the test now asserts the
production splittable `LayoutSections` (what `HelpOverlayRenderer` actually draws) with the
same no-overflow invariant, and the fast tier re-ran clean.

Scratch stayed in `.scratch/` inside the worktree (added to the worktree's
`.git/info/exclude`, never committed) and the AOT output there was deleted after validation;
the small ledgers this report cites are copied into [P759_assets](P759_assets/).
