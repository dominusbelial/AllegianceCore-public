# P752 — interface parity wave 7: screens and lobby (finished from the GLM hand-over)

**Base:** `pi/p752b` (worktree of `master` at `dca9431f`), built on `pi/p752`
commit `9a594d0c` ("WIP p752 (Pi, zai/glm-5.3) — pending Claude review").

**Hand-over:** the GLM 5.3 agent implemented the P752 batch (28 files,
+2140/−181) and then stopped on an API balance error **before verification,
report or scorecard**. This pass (DeepSeek, Pi lane) reviewed that diff against
native, fixed three wrong/half-done pieces, ran the full light gate, produced
real-client screenshots, updated the parity matrix and wrote this report.

## What changed

The GLM batch landed the following, all now verified:

- **Game-over tables** — `GameOverTablesLayout` (pure side + player table
  builders/sorters), `GameOverScreenRenderer` (paints both tables + clickable
  headers + sort arrow), `Game1.Panes`/`Game1.Rendering` (sort state, mouse
  click routing, live `GAME_OVER(62)`/`GAME_OVER_PLAYERS(63)` feed).
- **Game-over data** — per-side endgame counters
  (`Side.Ejections/BaseKills/BaseCaptures`; `GameOver.ForMission` now fills
  Kills/Deaths/Ejections/BaseKills/BaseCaptures/Flags/Artifacts instead of
  zeros), the kill-ASSIST credit (`DamageTrack.DealersAfterTop` +
  `PlayerScore.Assists` + the `FedSrv.CPP:11637-11670` walk in
  `Server.Combat`), and `GameOverPlayerEntry` extended with `Assists` +
  `TimePlayed` (60-byte stride).
- **Sector info pane** — side-known helium sum
  (`SectorInfoPaneLayout.Sample/SamplesForCluster`, live
  `ASTEROIDS_UPDATE/ASTEROID_MINED` fraction × `OreMax`), hover-selected
  sector + expand/collapse toggle, mouse-over warning text.
- **Starmap threat presentation** — `StarmapThreatPresentation` (native
  `sectormap.cpp:1066-1082` threshold cascade + `GetClusterWarningText`) used
  by `StarmapRenderer` and the info-pane warning line.
- **Team pane controls** — `TeamPaneControls` (roster selection cursor,
  donate $100/$500, leader stop-toggle, whole-wallet auto-donate, take-me-to
  sector gate, accept join request) wired through `Game1.Panes`.
- **Tests** — `GameOverTablesLayoutTests`, `SectorInfoPaneLayoutTests`,
  `StarmapHoverTests` (+ `StarmapThreatPresentationTests`),
  `TeamPaneControlsTests`, `P752GameOverAssistDeliveryTests`, and the
  `GameOverPlayersWireTests` layout update.

## Review fixes (this pass)

The following were wrong or half-done in the GLM diff and are corrected here:

1. **Game-over sort direction was inverted.** Native's list comparator is
   *"true if the first value should sort after the second"* (`zlib/tlist.h`
   `InsertSorted`), so a fresh column click is **ascending** (the startup
   comment "WLP sort with low scores on top") and the second click reverses to
   descending (`ReverseCompare`, `gameoverscreen.cpp:922-956`). The GLM code
   had fresh=descending. `GameOverTablesLayout` comparators, the renderer
   arrow, the `Game1.Panes` defaults (Score `Reverse:true`, Kills
   `Reverse:false`) and the tests now match native.
2. **Team pane next-pilot key collided with the starmap toggle.** Both were
   plain `Tab` in the `CAMERA / VIEW` group, which broke the
   `NoDuplicateKeys_WithinAGroup` help-overlay invariants. Moved
   `TeamPaneNextPilot` to `PageDown` and made the pane hint read the live
   key label instead of hardcoding `TAB`.
3. **Team pane row indent regression.** The selection cursor changed the
   leading indent from 4 to 2 spaces, which broke the existing
   `TeamPaneLayoutTests` text-column parsing. The cursor is now a fixed
   4-character field (`>   ` selected / `    ` otherwise), preserving the
   original name-column offset.
4. **Take-me-to could jump the command camera to world origin** when the
   target had no known pose (native passes `NULL` and keeps the default
   position, `WinTrek.cpp:7463-7491`). The port now leaves the focus untouched
   in that case.
5. **Stale/incorrect docs + missing wire-offset assertions** — `GameOver.cs`
   no longer claims side counters are "zeroed (P178)"; the
   `GameOverPlayersWireTests` layout test now pins assists @52, pad @54 and
   timePlayed @56.

## Review fixes (P752c)

Claude's second review of P752+P752b found the following, all corrected in this
pass (base `pi/p752c`):

1. **Game-over "#" column now toggles the current sort.** Native
   `OnButtonBarStats`/`OnButtonBarStatsSide` (gameoverscreen.cpp:840-874,
   922-956) folds a "#" click into a click on the CURRENT sort column, whose
   radio-button path toggles its direction. The port treated it as a no-op
   re-apply. `GameOverTablesLayout.ClickPlayerColumn`/`ClickSideColumn` now
   toggle on `Position`; `GameOverTablesLayoutTests.
   ClickingThePositionColumn_TogglesTheCurrentSort` pins the rule (and the
   toggle-back).
2. **Take-me-to always closes the team overlay and picks the ship.** Native
   `OnButtonTakeMeTo` (teampane.cpp:1288-1330) runs `TurnOffOverlayFlags` and
   `PickShip` even when the target is in another sector and the viewer isn't
   docked — only the camera jump is gated. The port gated the whole action.
   `TeamPaneControls.Step` now always emits the take-me-to intent and carries
   the ship-STATUS sector (`GetShipStatus().GetSectorID()`, teampane.cpp:1305),
   not the live entity's cluster; `TakeMeToSelected` closes the team pane and
   calls `CommitTargetForPlayer` unconditionally and gates only the camera
   reposition/request. `TeamPaneControlsTests.
   TakeMeTo_AlwaysFiresForAPlayerRow_AcceptOnJoinRequests_BallotWins` pins it.
3. **Auto-donate zeroes the local wallet; gift line shows money − gift.**
   Native `OnButtonAutoDonate` does `SetMoney(0)` after sending
   (teampane.cpp:1241), and the cross-side donation line prints
   money − gift (teampane.cpp:1152-1158, 1181-1188). The port showed the old
   balance and never zeroed. `ClientWorld.ZeroOwnWallet()` + the Game1 handler
   zero the wallet on the whole-wallet verb (amount ≠ −1), the relay's
   `Apply(AutoDonateUpdate)` now matches native's `pshipBy != m_ship` skip
   (appmsg.cpp:1248-1263), and the gift line subtracts the gift.
   `ClientWorldMoneyTests` pins the zero, the no-double-debit, the other-donor
   debit, the refund, and the gift line's subtraction.
4. **Threat-marker row marked `partial`.** The starmap draws the port's own
   primitive shapes/colours (`StarmapRenderer.DrawThreatIcon`), not native's
   `sectorwarning`/`sectorcapital`/`sectorbomber`/`sectorminer`/`sectorcombat`
   artwork BMPs (sectormap.cpp:1055-1082). Parity row 625 (map threat marker
   and alert presentation) is now `partial` — commander weighted returns to
   84.15% (68.164/81) and TOTAL to 76.60% (1391.737/1817).
5. **Sector-info hover reverted to the wrong sector + wrong toggle edge.**
   Native empty-map hover reverts to `GetChatCluster()` (sectormap.cpp:1446),
   not the own ship's sector; the expand button fires on `!bDown` (release,
   sectormap.cpp:205); hovering a new cluster plays `mouseoverSound`
   (sectormap.cpp:1442). `StarmapHover.Move` now takes the chat cluster,
   `HandleStarmapHoverAndExpandClick` computes `ViewedClusterId ?? own.SectorId`
   and toggles on release, and `SoundManager.PlayMouseOver` plays the
   `mouseover` wave (sounddef.mdl:322). `StarmapHoverTests` pins the chat-cluster
   revert.
6. **Base kills credit any launcher with a side.** Native reads
   `plauncher->GetSide()` after the OT_ship-only score block
   (FedSrv.CPP:12269-12273), so a missile/mine/probe launcher also increments
   the side's `BaseKills`. The port only credited `Ship` launchers. Now
   `BroadcastStationDestroyed` uses `EquipmentTarget.SideOf(launcher)`;
   `LivePlayerScoringFeedsTests.Base_kill_side_counter_credits_a_non_ship_launcher_with_a_side`
   pins a probe launcher crediting the side (and NOT the player score).
7. **Game-over panel no longer leaks in-flight panes.** The team pane, mission
   briefing, scoreboard and pilot-info overlays showed through the game-over
   panel. `Game1.Rendering` now hides all four while `GameOverResult` is up
   (same condition the game-over draw uses). `gameover-tables-720p.png` was
   re-shot with the real `tools/p752-ui-proof` client; the capture is now a
   clean dark game-over panel (dominant backdrop, no pane text at the edges).

P752c verification: whole-solution build 0 warnings/0 errors; AOT publish of
`Allegiance.Server` clean (no IL2026/IL3050; log
`~/allegiance-evidence/P752c/aot-publish.log`). New/affected tests 3× alone all
green — `GameOverTablesLayoutTests`/`TeamPaneControlsTests`/`StarmapHoverTests`
(22×3), `ClientWorldMoneyTests` (17×3), `LivePlayerScoringFeedsTests` (6×3).
Fast tier (`tools/run-tests.sh --tier fast`): **PASS, 8984 tests** (~272 s). An
earlier run had one load flake —
`MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`
failed once then passed 3/3 alone (documented in `docs/TEST_FLAKES.md`).
Summary/log: `~/allegiance-evidence/P752c/`.

## Scorecard

`python3 scripts/parity-scorecard.py` (parity-matrix.csv only):

| area | before weighted | after weighted | before unweighted | after unweighted |
| --- | --- | --- | --- | --- |
| screens | 64.99% (74.093/114) | **70.78% (80.693/114)** | 60.44% (55/91) | **68.13% (62/91)** |
| lobby | 69.96% (32.880/47) | 69.96% (32.880/47) | 66.67% (22/33) | 66.67% (22/33) |
| commander | 84.15% (68.164/81) | **86.62% (70.164/81)** | 84.78% (39/46) | **86.96% (40/46)** |
| TOTAL | 76.23% (1385.137/1817) | **76.71% (1393.737/1817)** | 71.79% (453/631) | **73.06% (461/631)** |

Rows updated to `done`: 352 (player results table), 353 (team totals table),
354 (column sorting/reversal), 511 (team pane donate/stop), 512 (take-me-to /
accept), 528 (known helium sum), 530 (hover-selected sector + expand button),
625 (map threat marker + alert presentation). Row 513 (team pane sort headers
and player selection) moved `missing → partial`: selection now exists, native
sort headers remain absent.

## Verification

- **Whole-solution build** `dotnet build AllegianceCore.slnx -c Release`:
  0 warnings, 0 errors.
- **AOT publish** (`Allegiance.Server`, `PublishAot=true`, linux-x64
  self-contained): clean, **no IL2026/IL3050**. Log:
  `~/allegiance-evidence/P752/aot-publish.log`.
- **New/affected tests 3× alone, all green:**
  - `Allegiance.ClientCore.MonoGame.Tests` (5 test classes): 57/57 × 3.
  - `Allegiance.Core.Tests` (GameOverPlayersWire layout + round-trip +
    P752GameOverAssistDelivery): 3/3 × 3.
  - `SessionLossLiveTests.EstablishedSession_ServerKilled_…` failed once in
    the first fast-tier run and then passed 3/3 alone → load flake (per
    `docs/TEST_FLAKES.md`), not a P752 regression.
- **Fast tier** `tools/run-tests.sh --tier fast`: **PASS, 8929 tests** (~270 s).
  Summary: `~/allegiance-evidence/P752/fast-tier-summary.json`.
- **Real-client screenshots** (1280×720 backbuffer, ≤ 2 MB total):
  [P752_assets/](P752_assets/). Reproduced with
  [tools/p752-ui-proof](../../tools/p752-ui-proof/Program.cs) — a real
  ephemeral loopback server + the real `Game1` DesktopGL client; no seeded
  rows or composed images. `client-proof.json` records ship/side/state/winner
  per capture.

| Visible behaviour | Capture |
| --- | --- |
| Sector info pane, expanded (helium + groups + expand `-`) | [sectorinfo-expanded-720p.png](P752_assets/sectorinfo-expanded-720p.png) |
| Sector info pane, collapsed (name + asteroid total only, expand `+`) | [sectorinfo-collapsed-720p.png](P752_assets/sectorinfo-collapsed-720p.png) |
| Team pane with the selected pilot cursor | [teampane-selection-720p.png](P752_assets/teampane-selection-720p.png) |
| Game-over screen with side + player result tables | [gameover-tables-720p.png](P752_assets/gameover-tables-720p.png) |

## Native citations (spot-checked this pass)

- Sort semantics: `zlib/tlist.h` (`InsertSorted` + "compare returns true if the
  first value should be after the second"), `gameoverscreen.cpp:733-1000`.
- Player painter columns/rank/time/assists/deaths-ejections:
  `gameoverscreen.cpp:84-190`; side painter: `gameoverscreen.cpp:231-301`.
- Assist walk: `FedSrv.CPP:11637-11670`; per-side counters:
  `sideigc.h:476-503`, `FedSrv.CPP:12269-12273`, `fsship.cpp:617`,
  `fsmission.cpp:3069-3080`.
- Sector info helium: `sectormap.cpp:350-369`, `asteroidigc.h:280-285`;
  expand button/collapse: `sectormap.cpp:186-238,495-515`; hover:
  `sectormap.cpp:1413-1461`; warning text: `sectormap.cpp:517-534`.
- Starmap threat cascade: `sectormap.cpp:1063-1082`.
- Team pane donate/stop/take-me-to/accept: `teampane.cpp:1141-1367`.

## Unfinished / known limits

- **Team pane sort headers** (row 513) are still not implemented — only the
  selection cursor landed; the row stays `partial`.
- **Lobby rows were untouched** by the GLM batch (its `Server.Lobby.cs`
  change is the game-over player-entry wire extension, which serves the
  screens rows, not a lobby UI row), so lobby stays 69.96% this wave.
- The team pane's key-driven verbs (D/E/A/X/PageDown/Y) are the keyboard
  adaptation of native's mouse button row; the underlying server rules are
  authoritative and unchanged.
- `GameOver.EjectPods` remains hardcoded `false` in the port, so the
  game-over player table always shows deaths (native switches only when
  `bEjectPods` is set in the mission).
