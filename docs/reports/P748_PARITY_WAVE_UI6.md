# P748 — interface parity wave 6

Dispatch base: `a00e842fe424787e4bd0a2b7319fc386b41324b9` (`codex/p748`). Changes remain in the working tree; no commits or pushes.

## Result and scope

Closed **12 existing matrix rows**, adding **10.448 completed weight**: eight screen rows and four commander rows. These cover five hangar behaviors and the shared investment/queue implementation; the screen and commander investment entries intentionally share production code and tests. No weights, exclusions, HUD rows, sounds, simulation rows, or training rows changed. Lobby remains unchanged.

The dispatch inventory contained **70 open rows** across these areas (screens 46, commander 11, lobby 13), including 13 out-of-scope training entries. After this wave, 58 remain open. The complete inventory is sorted by weight below and retained verbatim in [open-before.json](P748_assets/open-before.json). The selected package shares `Game1`'s docked dispatch, purchase model and pane renderer. Career leaderboard and game-over table work remain open.

Some investment gates already existed from P703/P728 while the P743 row explanations still described the older approximation. This wave fixes the completion/default filtering differences, adds the missing tab/detail flow, and checks the actual production consumers before granting those rows credit.

## Before → after scorecard

Partial/missing rows earn zero credit. Denominators are unchanged. Exact output: [before](P748_assets/scorecard-before.txt), [after](P748_assets/scorecard-after.txt).

| Matrix / area | Weighted before → after | Completed rows before → after |
| --- | --- | --- |
| Main / screens | 59.44% (67.761/114) → 64.51% (73.541/114) | 45/91 → 53/91 |
| Main / commander | 78.39% (63.496/81) → 84.15% (68.164/81) | 35/46 → 39/46 |
| Main / lobby | 68.40% (32.147/47) → 68.40% (32.147/47) | 20/33 → 20/33 |
| Main / total, including unchanged HUD track | 75.59% (1373.404/1817) → 76.16% (1383.852/1817) | 437/631 → 449/631 |
| Simulation / total, unchanged | 78.74% (1015/1289) → 78.74% (1015/1289) | 322/421 → 322/421 |
| Combined | 76.90% (2388.404/3106) → 77.23% (2398.852/3106) | 759/1052 → 771/1052 |

## Production changes and native rules

All native citations refer to `~/Development/Allegiance/src/`, read directly from this checkout.

- **Hangar controls:** all eight native verbs have shared drawing/hit geometry and distinct hover/leave feedback ([WinTrek/hangarscreen.cpp:37-48](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:37), [243-300](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:243)). The investment button is hidden when developments are disabled ([119-120](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:119)). Loadout, investment and teleport reuse the existing production panes; team/help draw above the docked shop.
- **Boarded launch:** send `BOARD_SHIP(NA)`, wait for authoritative parent clearance, then send one `BUY_LOADOUT(fLaunch=true)` preserving the pending hull/parts. Duplicate clicks remain suppressed while waiting. `BOARD_NACK` cancels the pending sequence and allows retry; undocking cancels it. Native: [hangarscreen.cpp:145-160](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:145), [clintlib/clintlib.h:1389-1393](/home/domingo/Development/Allegiance/src/clintlib/clintlib.h:1389), [clintlib/appmsg.cpp:1899-1938](/home/domingo/Development/Allegiance/src/clintlib/appmsg.cpp:1899). `ClientWorld.BoardFailureRevision` observes the existing refusal message without changing its wire format.
- **Command and quit:** Command requests the docked station's sector before entering command view ([hangarscreen.cpp:182-194](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:182)). Quit opens a modal confirmation, owns Enter/Escape, and sends `QUIT_MISSION` for the local ship with `QSR_Quit`; the existing authoritative departure path returns to the browser. Native: [hangarscreen.cpp:233-240](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:233), [WinTrek.cpp:3580-3605](/home/domingo/Development/Allegiance/src/WinTrek/WinTrek.cpp:3580), [clintlib.cpp:2727-2734](/home/domingo/Development/Allegiance/src/clintlib/clintlib.cpp:2727).
- **Purchase filtering:** `CanBuy` is independent of completion; Hide Completed remains a separate display filter. Show Complete starts checked in native, so Hide Completed now starts off. Show All bypasses buyability/predecessor display filtering while keeping investment gates. Native: [loadout.cpp:763](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:763), [1004-1011](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1004), [Igc/sideigc.h:224-281](/home/domingo/Development/Allegiance/src/Igc/sideigc.h:224), [Igc/bucketigc.cpp:36-65](/home/domingo/Development/Allegiance/src/Igc/bucketigc.cpp:36). Filter changes clamp the actual cursor and selected detail together.
- **Builder gate:** the production model uses the existing P703 exact station-type identity for living own-side constructors, including constructors still travelling. It combines that identity with compatible known rocks; unknown station types fail closed. Native: [loadout.cpp:213-240](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:213), [1124-1131](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1124). Funding dictionaries are copied under the session lock before building a frame model.
- **Tabs and detail:** preserve each bucket's native group ID, start on Starbase, cycle permitted/enabled groups, and reset selection on a tab change. Empty/unbuyable groups are disabled; enabling ignores Hide Completed but retains predecessor/mission-path gates. Selected details show name, cost, build time, description, funding and availability. Native: [loadout.cpp:777-785](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:777), [1042-1083](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1042), [1221-1310](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1221), [1376-1409](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1376), [1453-1499](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1453). The managed input adaptation uses rebindable `[`/`]` and controller shoulders, with held/composed/closed-pane suppression. Pane layout reserves the new hangar footer and keeps investment detail beside the list.

## Row evidence

Every row below changed from partial/missing to done. [row-evidence.json](P748_assets/row-evidence.json) preserves exact before/after records, weights and source/test references. Production code is in `src/Allegiance.ClientCore.Game/Game1.Hangar.cs`, `Game1.Panes.cs`, `Rendering/HangarControls.cs`, `Rendering/InvestmentPaneModel.cs`, `Rendering/InvestmentLayout.cs` and `Flight/InvestmentTabsInput.cs`. The new production-transition checks are in [P748InterfaceTests.cs](../../tests/Allegiance.ClientCore.MonoGame.Tests/P748InterfaceTests.cs).

| Area | Row | Weight | Native citation |
| --- | --- | ---: | --- |
| screens | Hangar / loadout screen / Disembark then launch boarded ship | 0.555 | [WinTrek/hangarscreen.cpp:149-152](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:149) |
| screens | Hangar / loadout screen / Command view button | 0.555 | [WinTrek/hangarscreen.cpp:182-194](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:182) |
| screens | Hangar / loadout screen / Team and help buttons | 0.555 | [WinTrek/hangarscreen.cpp:210-215](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:210); [WinTrek/hangarscreen.cpp:225-231](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:225) |
| screens | Hangar / loadout screen / Quit-mission button | 0.555 | [WinTrek/hangarscreen.cpp:233-240](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:233) |
| screens | Hangar / loadout screen / Button hover feedback | 0.560 | [WinTrek/hangarscreen.cpp:243-300](/home/domingo/Development/Allegiance/src/WinTrek/hangarscreen.cpp:243) |
| screens | Investment screen / Predecessor and continued-buyability filtering | 1.000 | [WinTrek/loadout.cpp:1004-1011](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1004) |
| screens | Investment screen / Station buildspot and builder gate | 1.000 | [WinTrek/loadout.cpp:213-240](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:213); [WinTrek/loadout.cpp:1124-1131](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1124) |
| screens | Investment screen / Tabs and status detail selection | 1.000 | [WinTrek/loadout.cpp:777-785](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:777); [WinTrek/loadout.cpp:1360-1499](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1360) |
| commander | Investments / buy-in / Predecessor and continued-buyability filtering | 1.000 | [WinTrek/loadout.cpp:1004-1011](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1004) |
| commander | Investments / buy-in / Station buildspot and builder gate | 1.000 | [WinTrek/loadout.cpp:213-240](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:213); [WinTrek/loadout.cpp:1124-1131](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1124) |
| commander | Investments / buy-in / Tabs and status detail selection | 1.000 | [WinTrek/loadout.cpp:777-785](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:777); [WinTrek/loadout.cpp:1360-1499](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1360) |
| commander | Build / research queue / Client queue predecessor and grouping UI | 1.668 | [WinTrek/loadout.cpp:777-785](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:777); [WinTrek/loadout.cpp:1004-1011](/home/domingo/Development/Allegiance/src/WinTrek/loadout.cpp:1004) |

The new tests exercise the real hangar dispatcher through loopback TCP, including exact command-view/quit/board/buy payloads, acknowledgement ordering, duplicate suppression, refusal cancellation and modal key handling. Production investment tests invoke `BuildInvestDefs`/`BuildInvestmentModel` with wire-fed side/funding/production state and the real PCore001 catalogue. PCore001 contains no matching attribute-level predecessor pair, so that consumer test adds two controlled native-format developments to its private in-memory catalogue to prove completion unlocks the next row. A separate production snapshot covers own/enemy/dead/different-type constructors, en-route identity and compatible/incompatible rocks. Existing P728 tech-gate coverage checks all civilizations against authoritative side buyability.

## Real-client evidence

The retained images are **1280×720 real Game1 DesktopGL backbuffer captures** from a real ephemeral loopback server and the configured Artwork corpus. The harness drives production handlers; it does not inject screen rows or compose images. Show All is intentionally enabled in the investment captures. Reproduction: [tools/p748-ui-proof/README.md](../../tools/p748-ui-proof/README.md). [client-proof.json](P748_assets/client-proof.json) records live ship/station IDs, hover and the actual group/bucket selections.

| Visible behavior | Capture |
| --- | --- |
| Hangar buttons and pointer hover | [hangar-hover-720p.png](P748_assets/hangar-hover-720p.png) |
| Starbase investment tab and detail | [investment-starbase-720p.png](P748_assets/investment-starbase-720p.png) |
| Garrison tab, different rows and selected detail | [investment-garrison-720p.png](P748_assets/investment-garrison-720p.png) |
| Station-sector command view | [hangar-command-720p.png](P748_assets/hangar-command-720p.png) |
| Reachable help overlay | [hangar-help-720p.png](P748_assets/hangar-help-720p.png) |
| Reachable team roster | [hangar-team-720p.png](P748_assets/hangar-team-720p.png) |
| Quit confirmation | [hangar-quit-720p.png](P748_assets/hangar-quit-720p.png) |

The SDL offscreen backend clipped the first captures to an 800×480 default framebuffer despite reporting 1280×720. Those images were replaced with X11/software-GL captures and visually inspected. The UI retains the existing mission-objective overlays; their text can overlap lower purchase rows. HUD presentation remains on its separate track.

## Verification

- **Whole-solution build:** PASS, **0 warnings / 0 errors**, including the new UI proof tool. Raw: `.p748-evidence/build-final.log`.
- **Affected tests:** **64 methods × 3 isolated invocations = 192 processes / 348 passing cases**, zero failures or skips. Longest case: 0.129 s. This includes every new test and the affected investment, build-gate, P728, launch and rebinding checks. [Exact ledger](P748_assets/isolated-tests.json); [reproduction script](P748_assets/run-isolated.py).
- **One fast-tier sweep:** **8,806 passed / 5 failed / 3 optional private-pack skips**, **8,814 cases in 275.4 s**. All nine runner infrastructure checks passed; there were no timing offenders or quarantine flakes. [Original summary](P748_assets/fast-tier-summary.json) retains `passed: false`; the sweep was not repeated.
- **Five environment-context failures:** the unchanged `PlayerBundlePathsTests` fixtures use temporary install directories that must be outside a checkout's logical ancestor chain. Workspace-local scratch exposes the real `local.env`, matching the previously documented SC2 observation in [TEST_FLAKES.md](../TEST_FLAKES.md). Four methods cover the five failed cases (`UsesExecutableDirectoryOutsideRepository`, `MissingDirectoryHasNoDefault`, `LocalConfigWinsOverBundleWithBlankEnvironment`, `NativeSourceNeverGuessesAnInstallSubdirectory`). All four passed **3/3 individual method invocations**, including all theory rows: **21 passing case executions**. Rechecks use `/proc/<parent>/fd/<fd>` as a logical alias to the same workspace NVMe scratch directory. No test assertion, deadline, path resolver, or quarantine was edited. These are **environment-context failures, not load flakes**. [Recheck evidence](P748_assets/environment-rechecks.json).
- **Real-client proof:** all seven X11 captures completed; images were opened and visually inspected. The proof's game-library bytes match the built production library. Its JSON metadata uses a source-generated serialization context. PNG integrity, CSV validation and `git diff --check` passed. [Verification manifest](P748_assets/verification.json).

No `Allegiance.Core` or `Allegiance.Server` files changed, so the conditional server AOT publish gate does not apply. No bot, physics, simulation, or in-flight control algorithm changed; the never-parks showcase gate is outside this docked-interface change. No Live-tier test was added or modified. The new loopback tests run in the fast tier, and each selected case remained below 10 seconds.

The sandbox permits writes only in this workspace and `/tmp`; the brief forbids `/tmp` scratch. Consequently scratch used `.p748-scratch/` and raw logs/TRX use [raw evidence directory](/home/domingo/allegiance-codex-p748/.p748-evidence/) (`/home/domingo/allegiance-codex-p748/.p748-evidence/`), both ignored. The preferred `~/allegiance-scratch/P748/` and `~/allegiance-evidence/P748/` were unavailable to this lane. Scratch and 80 generated project/content build directories were removed after validation. Small linked assets remain under `docs/reports/P748_assets/`, **1318202 bytes**, below the 2 MB limit. The tier runner's lock and temporary/cache paths were also redirected into workspace scratch.

## All open rows at dispatch, descending weight

“Selected” means closed by this wave. Training and native simulation entries remain listed for inventory completeness and were excluded from implementation. Other entries are deferred. Full original explanations and native citations are in [open-before.json](P748_assets/open-before.json).

| Weight | Area | Status before | Native behavior | Disposition |
| ---: | --- | --- | --- | --- |
| 3 | screens | partial | Training screen | out of scope: training |
| 2.500 | screens | partial | Chat pane / History scrolling and channels | deferred |
| 2.500 | screens | partial | Chat pane / Compose and send focus flow | deferred |
| 2.500 | commander | partial | Team chat / Compose recipient and team rendering | deferred |
| 2.500 | lobby | partial | Connect to server (host/IP + character) / Connect progress failure retry and abort | deferred |
| 2.000 | screens | partial | F-key help overlay(s) / Manual pages and navigation | deferred |
| 2.000 | commander | partial | Drones: mine layers / Default least-targeted asteroid plan | out of scope: simulation |
| 2.000 | commander | partial | Team leadership / owner (promote, owner-only cmds) / Donation-preferred departure succession | deferred |
| 2.000 | commander | partial | Sector threat / alerts / Map threat marker and alert presentation | deferred |
| 2.000 | lobby | partial | Server list / browse lobby / Directory filters details and player search | deferred |
| 2.000 | lobby | partial | Mission briefing screen / Civilization-generated briefing and tips | deferred |
| 2.000 | lobby | partial | Lobby chat / Lobby-directory relay feedback | deferred |
| 1.668 | screens | partial | Loadout panel (shop) / Native saved and default loadout controls | deferred |
| 1.668 | commander | partial | Wing / command orders to drones / General default-order target resolution | deferred |
| 1.668 | commander | partial | Build / research queue / Client queue predecessor and grouping UI | selected |
| 1.668 | commander | partial | Drones: miners / Native optimal travel controls | out of scope: simulation |
| 1.666 | screens | partial | Loadout panel (shop) / Hull and part purchase gates | deferred |
| 1.500 | screens | partial | Mission briefing screen / Civilization-generated briefing and tips | deferred |
| 1.500 | lobby | partial | AFK / auto-seat handling / Timed automatic side assignment | deferred |
| 1.334 | screens | missing | Scoreboard / leaderboard screen / Column sort and profile navigation | deferred |
| 1.334 | screens | partial | Character-info screen / Leaderboard and games navigation | deferred |
| 1.334 | screens | partial | Commander / sector map / Generic target-to-order interaction | deferred |
| 1.334 | screens | partial | Server list / lobby browser / Find-player and details popup | deferred |
| 1.334 | lobby | missing | Kick / ban / mute (moderation) / Native moderator ban and defection boot | deferred |
| 1.334 | lobby | partial | Team lobby / team pane / Native team configuration and roster controls | deferred |
| 1.334 | screens | partial | in-game key rebinding popup / OK versus Cancel persistence | deferred |
| 1.333 | screens | missing | Scoreboard / leaderboard screen / Career leaderboard query and search | deferred |
| 1.333 | screens | missing | Scoreboard / leaderboard screen / Find-me and page navigation | deferred |
| 1.333 | screens | partial | Character-info screen / Career stats and civilization selection | deferred |
| 1.333 | screens | missing | Character-info screen / Profile editing and apply | deferred |
| 1.333 | screens | partial | Server list / lobby browser / Sort and game filters | deferred |
| 1.000 | screens | partial | Game-over screen / Player results table | deferred |
| 1.000 | screens | missing | Game-over screen / Team totals table | deferred |
| 1.000 | screens | missing | Game-over screen / Column sorting and reversal | deferred |
| 1.000 | screens | partial | Splash / loading screen / Timeout to intro and connection progress | deferred |
| 1.000 | screens | partial | Teleport pane / Back and failure recovery | deferred |
| 1.000 | screens | partial | Investment screen / Predecessor and continued-buyability filtering | selected |
| 1.000 | screens | partial | Investment screen / Station buildspot and builder gate | selected |
| 1.000 | screens | partial | Investment screen / Tabs and status detail selection | selected |
| 1.000 | screens | partial | Sector info pane / Known helium sum | deferred |
| 1.000 | screens | missing | Sector info pane / Hover-selected sector and expand button | deferred |
| 1.000 | commander | partial | Investments / buy-in / Predecessor and continued-buyability filtering | selected |
| 1.000 | commander | partial | Investments / buy-in / Station buildspot and builder gate | selected |
| 1.000 | commander | partial | Investments / buy-in / Tabs and status detail selection | selected |
| 1 | commander | partial | Drones: constructors / Drilling and building controls | out of scope: simulation |
| 0.800 | screens | partial | Team pane / screen / Selected-player donate and stop buttons | deferred |
| 0.800 | screens | partial | Team pane / screen / Take-me-to and accept controls | deferred |
| 0.800 | screens | missing | Team pane / screen / Sort headers and player selection | deferred |
| 0.560 | screens | missing | Hangar / loadout screen / Button hover feedback | selected |
| 0.555 | screens | missing | Hangar / loadout screen / Disembark then launch boarded ship | selected |
| 0.555 | screens | partial | Hangar / loadout screen / Command view button | selected |
| 0.555 | screens | partial | Hangar / loadout screen / Team and help buttons | selected |
| 0.555 | screens | partial | Hangar / loadout screen / Quit-mission button | selected |
| 0.370 | lobby | missing | Training missions (tutorial) / Free Flight launch | out of scope: training |
| 0.363 | lobby | partial | Training missions (tutorial) / Back to intro | out of scope: training |
| 0.363 | lobby | partial | Training missions (tutorial) / Basic Flight launch/runtime | out of scope: training |
| 0.363 | lobby | missing | Training missions (tutorial) / Pre/post-training slideshow flow | out of scope: training |
| 0.363 | lobby | missing | Training missions (tutorial) / Introduction mission | out of scope: training |
| 0.363 | lobby | missing | Training missions (tutorial) / Nanite mission | out of scope: training |
| 0.280 | screens | missing | Training screen / Free Flight launch | out of scope: training |
| 0.272 | screens | partial | Training screen / Back to intro | out of scope: training |
| 0.272 | screens | partial | Training screen / Basic Flight launch/runtime | out of scope: training |
| 0.272 | screens | missing | Training screen / Pre/post-training slideshow flow | out of scope: training |
| 0.272 | screens | missing | Training screen / Introduction mission | out of scope: training |
| 0.272 | screens | missing | Training screen / Nanite mission | out of scope: training |
| 0.250 | screens | partial | Intro screen / LAN popup and polling | deferred |
| 0.250 | screens | partial | Intro screen / Options menu | deferred |
| 0.250 | screens | partial | Intro screen / Credits popup | deferred |
| 0.250 | screens | missing | Intro screen / Help button | deferred |
| 0.250 | screens | missing | Intro screen / Replay-intro button | deferred |
