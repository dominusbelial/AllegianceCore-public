# P757 — interface parity wave 8: remaining screens

**Base:** the actual merge base `fac0da07` (master at dispatch). The wave ran on `pi/p757`
(`e4a07a1e`); the P757b review-fix branch `pi/p757b` merged master `beda2413` first (merge commit
`5f137a05`, allowed by the brief). No commits or pushes from this round beyond that merge; all
P757b changes are in the working tree.

## Scope decision

The brief's area is the **36 `screens` rows still open after P752**. Read P743 (correctness
pass), P748 (UI6) and P752 (`pi/p752b`, UI7) first, then checked every unmerged `pi/*` branch
(`git branch --list "pi/*" --no-merged master`): **pi/p752b already owns** the game-over tables
(3 rows), the team-pane controls (3 rows), the sector-info helium/hover rows (2) and the starmap
threat row — all **skipped** here, and no function those rows live in was edited. The HUD
branches (hud3–hud5) own `hud` rows (out of scope anyway); p751/p751b/p753/p754/p755 own
simulation rows; pi/ux1 is already merged into master (master is ahead of it).

Of the remaining rows, this wave implemented a **12-row package** (weight 12.918) around three
code-sharing clusters: the chat pane (chatpane.cpp), the intro/splash/help screens
(introscreen/splashscreen/help.cpp) and the small screen-control fixes (teleportpane, trekinput,
shipIGC.h GetDefaultOrder). The two heaviest remaining clusters are deliberately left open and
listed under "Unfinished".

## Rows addressed (12; 10 `done`, 2 `partial` after review)

| Row (screens) | Weight | Native rule | Port evidence |
| --- | ---: | --- | --- |
| Chat pane / History scrolling and channels (**partial** after review) | 2.500 | chatpane.cpp:226-233, 255-283, 299-358 | `ChatPaneModel` + `Game1.FlightInput`/`BuildChatLines`/`FilteredChatCount`; `P757ChatPaneTests` |
| Chat pane / Compose and send focus flow | 2.500 | WinTrek.cpp:9441-9446; console.cpp:1282-1338 | `Game1.HandleChatCompose` (extracted production seam) + `SendChatLine`/`OnTextInput`; `P757ChatPaneTests` loopback compose/send |
| F-key help overlay(s) / Manual pages and navigation | 2.000 | effect/help.cpp:96-296; WinTrek.cpp:9243-9268 | `HelpManualModel` + `HelpOverlayRenderer` topic bar/page + H-overlay navigation; `P757IntroScreensTests` |
| Commander / sector map / Generic target-to-order interaction | 1.334 | Igc/shipIGC.h:2071-2161 (GetDefaultOrder) | `CommandOrderResolver.ResolveDefaultOrder` + `Game1.Targeting` target profile; `P757DefaultOrderTests` |
| in-game key rebinding popup / OK versus Cancel persistence | 1.334 | trekinput.cpp:2436-2458, 3005-3030 | `KeyBindingController.Commit/Close` + `KeyBindingStore.Reload` + Space/Esc in `Game1.KeyBind`; `P757KeyBindOkCancelTests` |
| Teleport pane / Back and failure recovery | 1.000 | appmsg.cpp:1899-1910; teleportpane.cpp:276-294, 499-531 | `ClientWorld.Apply(BoardNack)` branch + `BoardRefusedTurretsFull` cue + `Game1.ReleaseTeleportOnBoardRefusal`; `P638FeedbackWireTests` (incl. P757b turret-state test) |
| Splash / loading screen / Timeout to intro and connection progress | 1.000 | splashscreen.cpp:34-46, 63-80 | `ClientState.Splash` + `Game1.UpdateSplash`/`DrawSplash`/`SplashTimeoutSeconds` (0.01 s quickstart); `P757IntroScreensTests` |
| Intro screen / Replay-intro button | 0.250 | introscreen.cpp:1224, 1865-1876 | `IntroCommand.ReplayIntro` → `BeginSplash` → splash → intro; `P757IntroScreensTests` + proof shots |
| Intro screen / Help button | 0.250 | introscreen.cpp:1226, 1806-1810 | `IntroCommand.Help` → `IntroView.Help` (the manual at hlpStart); `P757IntroScreensTests` |
| Intro screen / Credits popup | 0.250 | introscreen.cpp:643-653, 1853-1863 | `DrawIntroCredits` scrolls over time (creditsTime) and resets on open; shot |
| Intro screen / Options menu (**partial** after review) | 0.250 | introscreen.cpp:1847-1851; WinTrek.cpp:4429-4442, 4568-4586 | `UpdateOptionsMenu`/`DrawPrivacySettings`: Graphics/Sound/Game/Controls on real controls; shot |
| Intro screen / LAN popup and polling | 0.250 | introscreen.cpp:824-995 | `IntroFindServerList` (dedup, 1 < maxPlayers < 1000, name sort) on the 1 s-polled Find Server view; `P757IntroScreensTests` |

Every row cites the native file:line it matches and a test that pins the native rule (see the
matrix evidence column for the full citations). The row evidence in `docs/parity/parity-matrix.csv`
was rewritten per row; no other rows were touched.

**P757b status correction:** two rows are `partial`, not `done`. The in-flight **Alt+C pane-filter
cycle is a port adaptation** (native filters belong to the lobby chat pane, teamscreen.cpp:3085-3101),
and the **Sound/Game options groups are one-press toggles**, not native submenus. The remaining ten
rows stay `done`; the fixes in this round strengthen three of them (station order, turret counting,
help layout) rather than changing their status.

## What changed (production)

- **`ChatPaneModel` (new)** — native `ChatListPaneImpl`'s view semantics: `ChatPassesFilter`
  (non-critical = not-from-player/not-object-model/not-IndividualNoFilter while
  player-chats-only) + `AddChannel`/`RemoveChannel`/`ResetChannels` → rebuild-and-snap-to-bottom,
  and the autoscroll machine (PageUp/PageDown/scroll re-enable at the bottom, the 10-second
  resume timer, appended lines keep a paused pane's content). Driven from `UpdateFlying`:
  `ChatPageUp/Down`, `Prev/NextCommMsg` (scroll events), and a new rebindable
  `ChatPaneFilter` action (**Alt+C**) cycling All → Players (native `m_bPlayerChatsOnly`
  default) → Channel (the native lobby pane's `{channel, Individual, IndividualNoFilter,
  Admin}` set, teamscreen.cpp:3085-3101) — every state is a native configuration of the same
  machinery. `BuildChatLines` filters the real world log through it. *Adaptation note (row
  `partial`):* the Alt+C in-flight cycle itself has **no native equivalent** — native applies
  those filters in the lobby chat pane — and the port pane defaults to **All** (not Players)
  because its chat log is also its console surface, whereas native routes filtered chatter to
  `PostPlainText`. The Players mode is one keypress away and its default is persisted through the
  Game Options entry.
- **Compose flow** — the enter/backspace/tab block extracted from `UpdateFlying` into
  `HandleChatCompose` (the P748 `HandleHangarQuitInput` seam discipline) so the production
  compose/send path is drivable headlessly; the compose line now shows native's always-shown
  `"|"` cursor (console.cpp:1290-1292).
- **Chat scroll window (P757b)** — both in-flight chat renderers were re-applying the pane's
  scroll offset to the already-windowed `BuildChatLines` output, which collapsed every paused
  view back to the newest page (and hid the "(OLDER)" marker). The renderers now draw the window
  the `ChatPaneModel` produced and use `Offset` only for the OLDER marker; a paused pane really
  shows older lines. The chat pane/filter label is right-aligned inside the left column and the
  history sits above the RADAR/cargo/WEAPONS status band, clear of the THR gauge.
- **`HelpManualModel` (new)** — native `HelpPaneImpl` navigation (topic history + Back +
  scroll-reset per topic) with `hlpStart`, the generated P257 key reference as the Controls
  topic, and Flight/Combat/Command pages describing the port's real controls. The **H overlay**
  opens it and navigates with action-mapped keys (`MenuUp/MenuDown`, `ChatDelete` = Back,
  `ChatPageUp/Down` = scroll — no raw literals, P625b guard passes); while open the overlay owns
  the keyboard like the native pane. The **intro Help button** opens the same manual
  (`IntroView.Help`).
- **`CommandOrderResolver.ResolveDefaultOrder`** — the full native `GetDefaultOrder` table
  (miner/builder Mine/Build, layer Build at warp, friendly Defend with the parentless lifepod↔rescue
  **Pickup**, carrier **Goto**, station ladder, rescue-probe **Goto**, enemy **Attack**, treasure
  **Pickup**, enemy probe Attack with the RescueAny exception). The issue-order key builds a
  `CommandTargetProfile` from the live world (side, target hull ABM, station SABM, probe EABM from
  the shop catalogues) and the commandee's layer role from DroneTypes. Replaces the old
  `ResolveChat` approximation. **P757b correction:** native's station branch is exclusive —
  non-fighter checks **only CapLand**, a fighter lifepod checks **Rescue|Land**, and a
  non-lifepod fighter checks **Land** (Igc/shipIGC.h:2124-2135). The port's earlier if/else-if
  fall-through let a non-fighter with Land-only fall into the Land test and return Goto instead of
  native **Defend**; the branches are now exclusive and pinned by
  `P757DefaultOrderTests.Friendly_station_stays_defend_unless_the_capability_ladder_makes_it_goto`.
  `P728CommanderTests` passes no station capabilities, so its friendly-station case expects the
  native default **Defend** for that reason (the test comment now says so).
- **Key rebind OK/Cancel** — native persistence: mutations are pending in the live map; **Space =
  OK** (`SaveMap` + close), **Esc = Cancel** (reload the persisted map, falling back to defaults
  without saving). `KeyBindingStore.Reload(From)` added; the popup footer names both keys.
- **Teleport recovery** — `ClientWorld.Apply(BoardNack)` now gates on `InGame`, picks the native
  branch (turret race → "Teleportation canceled - no turret positions were open." + the
  `noTurretsSound` cue `vo_sal_noturrets`, counting the parent's children against
  `MaxWeapons - MaxFixedWeapons` from the mission core) and the docked handler releases the
  pending teleport latch (`ReleaseTeleportOnBoardRefusal`, the `EndLockDown` equivalent) so the
  pane/shop stay usable for reselection. `MannedTurrets` counts only children in the **turret
  state** (teleportpane.cpp:499-518), not every `ParentId` child — fixed in P757b.
- **Splash + Replay Intro** — `ClientState.Splash`: the native 0.01 s quickstart timeout (the
  `g_bQuickstart || true` REVIEW branch) advances to the intro, any key/click skips; the client
  boots splash → intro (native WinTrek.cpp:2690) and the intro menu gains the native
  `introButtonPane` (**Replay Intro**) and `helpButtonPane` (**Help**) slots.
- **Options menu** — the intro Settings view is now the native four-group options menu on real
  port controls: Graphics (the existing overlay), Sound (music toggle/volume), Game (the
  chat-filter default, persisted in `IntroPreferences.ChatFilterPlayersOnly` + "Map Keys and
  Controls"), Controls (the rebind popup). **P757b:** the inherited hotkeys are restored
  (**G** = Graphics, **S** = benchmark sharing, **K** = Map Keys and Controls) and the new
  Sound/Game toggles moved to the previously unused **A**/**F** keys; `IntroPreferences` is loaded
  once when the screen opens (`EnsureOptionsPrefs`) and cached, never read from the Draw path.
  Because Sound/Game remain one-press toggles rather than native submenus, the row is `partial`.
- **Credits** — the intro credits scroll over time like the native `creditsTime` animation and
  reset on open.
- **Find Server list** — `IntroFindServerList`: DPlay8 duplicate stripping, the native
  single-player/Zone cap filter (`nMaxPlayers > 1 && < 1000`; the port directory's 0 =
  unknown/unbounded passes) and the `GameNameCompare` sort, applied to the 1 s-polled view.

No `Allegiance.Core` or `Allegiance.Server` production file changed, so the conditional AOT
publish gate does not apply. The flight-input changes are client-side input gating (chat compose
+ the help overlay owning the keyboard); no bot, autopilot, physics or simulation behavior
touched, so the load-sensitive never-parks 5× gate does not apply. No Live-tier test was added
or modified (the new loopback compose test is fast-tier, 81 ms).

## Scorecard (before → after)

`python3 scripts/parity-scorecard.py` — before from the `fac0da07` CSVs, after from the working
tree (raw outputs kept in the assets folder):

| area | before (`fac0da07`) | after (working tree) |
| --- | --- | --- |
| screens | 64.99% (74.093/114); 55/91 rows | **73.91% (84.261/114); 65/91 rows** |
| commander | 84.15% (68.164/81); 39/46 | 84.15% (68.164/81); 39/46 (unchanged) |
| lobby | 69.96% (32.880/47); 22/33 | 69.96% (32.880/47); 22/33 (unchanged) |
| TOTAL | 76.23% (1385.137/1817); 453/631 | **77.12% (1401.305/1817); 465/631** |
| sim-rules TOTAL | 81.03% (1042/1286); 330/420 | 81.62% (1048/1284); 332/419 |

The screens delta is the wave: **+10 rows, +10.168 weighted credit** = the 12 addressed rows'
12.918 minus the two rows P757b marks `partial` (2.500 + 0.250). No `n/a`, no exclusions, no weight
edits. The TOTAL/sim-rules movement beyond that comes from the `beda2413` master merge (HUD-3/HUD-4,
P751b and TT rows), not from this task; the screens denominator is unchanged.

## Verification (light gate)

- **Whole-solution build** `dotnet build AllegianceCore.slnx -c Release`: **0 warnings /
  0 errors** (after the final change).
- **New/affected tests 3× alone**: three isolated groups, all green on all three invocations —
  P757b-affected suites (92 MonoGame tests: `CommandOrderResolverTests`, `P757DefaultOrderTests`,
  `P728CommanderTests`, `HelpOverlayTests`, `P757IntroScreensTests`, `P757ChatPaneTests`,
  `IntroScreenModelTests`), the wire suite (12 ClientCore tests: `P638FeedbackWireTests`), and the
  regression-adjacent group (33 MonoGame tests: `CommandedDroneBuildKeyLiveTests`,
  `P748InterfaceTests`, `P625bActionMapGuardTests`, `TeleportLayoutTests`,
  `P740FlightInputTests`, `UX1EscapeClosesPanesTests`). That is **3 × 137 = 411 test
  invocations**, all green, plus final 3×52 and 3×37 re-runs of the help/chat/order/options
  suites after the last renderer changes (267 more, green). (The old report named a
  `P757TeleportRecoveryTests` class that does not exist in the tree; the recovery rules are pinned by `P638FeedbackWireTests`, including the
  new turret-state test.) Raw ledger: [isolated-results.txt](P757_assets/isolated-results.txt);
  scorecard outputs: [scorecard-before.txt](P757_assets/scorecard-before.txt),
  [scorecard-after.txt](P757_assets/scorecard-after.txt).
- **One fast-tier run** `tools/run-tests.sh --tier fast`: **PASS — 8,997 tests, 270.4 s**
  (summary: [fast-summary.json](P757_assets/fast-summary.json),
  `TestResults/tiers-20261020T135136.148505Z/summary.json`). The first sweep aborted in the
  **untouched** `Allegiance.Core.Tests` host with `InvalidOperationException: The stream does not
  support reading` in `TcpTransport.ReadSome` (the concurrency/transport flake family listed in
  `docs/TEST_FLAKES.md`; this task changed no Core/Server file). The immediate full rerun was
  clean at the full 8,997-test discovery count.
- **AOT publish**: not applicable (no `Allegiance.Core`/`Allegiance.Server` changes — the turret
  count fix is in `Allegiance.ClientCore`).
- `git diff --check` clean; `parity-scorecard.py` validates both matrices (665 + 473 rows).

## Real-client evidence (1280×720 backbuffer captures, 980 KiB total ≤ 2 MB)

Reproduced with [tools/p757-ui-proof](../../tools/p757-ui-proof/Program.cs) — a real ephemeral
loopback server + the real `Game1` DesktopGL client (flight lane), and the real intro/splash
boot (intro lane); no seeded rows or composed images. All shots driven through production
handlers/seams (`HandleChatCompose`, `ApplyChatPaneFilterMode`, `ActivateIntroMain`,
`HelpManualModel` navigation).

| Visible behaviour | Capture |
| --- | --- |
| Chat compose line: recipient label + typed text + native "\|" cursor | [chat-compose-720p.png](P757_assets/chat-compose-720p.png) |
| Chat pane channel filter ([CHANNEL TEAM], system lines filtered out) | [chat-filter-channel-720p.png](P757_assets/chat-filter-channel-720p.png) |
| Chat pane paused (autoscroll off, "(OLDER)" window + filter label) | [chat-filter-paused-720p.png](P757_assets/chat-filter-paused-720p.png) |
| Help manual, start topic (hlpStart) with the topic bar | [help-manual-start-720p.png](P757_assets/help-manual-start-720p.png) |
| Help manual, Controls topic (the generated key reference) | [help-manual-controls-720p.png](P757_assets/help-manual-controls-720p.png) |
| Help manual after Back (history pop) | [help-manual-back-720p.png](P757_assets/help-manual-back-720p.png) |
| Splash screen (title + loading; times out to the intro) | [splash-720p.png](P757_assets/splash-720p.png) |
| Intro menu with Replay Intro highlighted (slot 6) | [intro-menu-720p.png](P757_assets/intro-menu-720p.png) |
| Intro Options menu (Graphics/Sound/Game/Controls) | [intro-options-720p.png](P757_assets/intro-options-720p.png) |
| Intro credits, scrolling | [intro-credits-720p.png](P757_assets/intro-credits-720p.png) |
| Intro Help (the manual view) | [intro-help-720p.png](P757_assets/intro-help-720p.png) |
| Replay Intro → splash (driven through ActivateIntroMain) | [intro-replay-splash-720p.png](P757_assets/intro-replay-splash-720p.png) |

The captures were checked by looking at the drawn text and its bounding boxes (OCR over each
1280×720 PNG), not by pixel density: `chat-filter-channel-720p` contains `[CHANNEL TEAM]` at
x≈281 y≈496 and the real `[TEAM] P757 UI pilot: wingman cover me` line at y≈520, both left of the
THR label at x≈478 y≈522; `chat-filter-paused-720p` contains `(OLDER - PgDn to newest)` at the top
of the history window; `help-manual-controls-720p` has the topic bar at y≈46, the CAMERA/VIEW
column headers at y≈90, the last binding row ending around y≈593 and the footer at y≈663 (no
overlap). The proof logs confirm each state transition, including splash→intro and
intro→splash→intro.

## Native citations (spot-checked this pass)

- Chat pane: `WinTrek/chatpane.cpp:128-198` (player-chats-only, autoscroll, 10 s timer),
  `:226-233` (ChatIsNonCriticalMessage), `:255-283` (PageUp/PageDown/OnScroll),
  `:286-297` (UpdateContents), `:299-358` (ChatPassesFilter, Add/Remove/ResetChannels);
  `teamscreen.cpp:3085-3101` (the lobby channel set).
- Compose: `WinTrek/WinTrek.cpp:9441-9446` (TK_StartChat + SetFocus(false));
  `WinTrek/console.cpp:1282-1338` (UpdateComposedChat: typed + "|", recipient labels),
  `:2747-2755` (OnTab cycle).
- Help: `effect/help.cpp:96-296` (nav pane, PagePanes, history, Back, DoSetTopic scroll reset);
  `WinTrek/WinTrek.cpp:9243-9268` (InitializeHelp/OnHelp).
- GetDefaultOrder: `Igc/shipIGC.h:2071-2161`; `WinTrek/WinTrek.cpp:8830` (SetTarget →
  c_cidDefault); `WinTrek/trekigc.cpp:4716-4725` (chat path).
- Keymap popup: `WinTrek/trekinput.cpp:2436-2458` (Close reloads, OK saves),
  `:3005-3030` (Esc=Cancel, Space=OK, Enter=edit).
- Teleport: `clintlib/appmsg.cpp:1899-1910` (in-game gate, EndLockDown, OnBoardFailed);
  `WinTrek/teleportpane.cpp:276-294` (branch messages + noTurretsSound), `:499-531`
  (MannedTurrets/NumTurrets).
- Splash: `WinTrek/splashscreen.cpp:34-46` (0.01 s quickstart timeout), `:63-80` (key/click skip).
- Intro: `introscreen.cpp:824-995` (FindServerPopup FindGames), `:643-653` (creditsTime),
  `:1224-1226` (introButtonPane/helpButtonPane), `:1806-1876` (OnButtonHelp/Options/Credits/Intro);
  `WinTrek.cpp:4429-4442, 4568-4586` (ShowOptionsMenu groups).

## Review fixes (P757b)

Claude's review of `e4a07a1e` produced eight items; all are fixed in this working tree.

1. **Station default order.** `CommandOrderResolver.ResolveDefaultOrder` now mirrors native's
   exclusive branches (Igc/shipIGC.h:2124-2135): non-fighter checks **only CapLand**, fighter
   lifepod checks **Rescue|Land**, fighter checks **Land**; a non-fighter with Land-only stays
   **Defend**. The test covers fighter+Land, non-fighter+CapLand and non-fighter+Land-only (plus
   the native miner/builder pilot-type default of Goto at a station). See the correction block in
   "What changed".
2. **Defend/Goto wording.** The P728 test comment, this report's resolver bullet and the matrix
   row now say native gives Goto for a station the ship can land at; the P728 case only gets
   Defend because it passes **no station capabilities**.
3. **Manned turret count.** `ClientWorld.Apply(BoardNack)` counts only children whose
   `State == StateTurret` (native `MannedTurrets` counts `c_ssTurret` only,
   teleportpane.cpp:499-518). New test
   `P638FeedbackWireTests.P757b_BoardNack_MannedTurretsCountsOnlyTurretStateChildren` fills every
   slot with docked children (still a free turret → race message) and then with turret children
   (full → generic failure).
4. **Chat screenshots/layout.** The filter label is right-aligned inside the chat column instead of
   at `leftWidth-140` (which sat on the THR gauge); the history sits above the RADAR/cargo/WEAPONS
   status band, and the classic renderer's panel no longer runs under the gauges. Both chat
   renderers stopped re-applying the scroll offset to the already-windowed lines, which is what
   hid the "(OLDER)" marker. The proof now sends a real Team line and captures the shots listed
   above; they were verified by OCR of the drawn strings.
5. **Help manual topic bar.** `HelpOverlayLayout.ContentBand` reserves the topic bar and footer
   rows; the Controls columns start below the bar, the tall targeting group splits into sections,
   and two tests pin the bands. `help-manual-controls-720p` was re-shot: CAMERA/VIEW headers no
   longer collide with the topic bar and no row crosses the footer.
6. **Options menu.** G/S/K keep their old meanings (Graphics / benchmark sharing / Map Keys and
   Controls); the new Sound/Game toggles moved to A/F. `IntroPreferences` is loaded once when the
   screen opens and cached (`EnsureOptionsPrefs`), never in Draw. Sound/Game are still one-press
   toggles, so the row is `partial` as the brief allows.
7. **Alt+C chat filter.** Kept as the documented adaptation but the chat-history row is now
   `partial` in `docs/parity/parity-matrix.csv` and above (native filters live in the lobby chat
   pane, teamscreen.cpp:3085-3101).
8. **Report base line.** Corrected to `fac0da07` (actual merge base) and the scorecard regenerated
   from those CSVs.

Verification for the round is in the light-gate section: final whole-solution build 0/0, 411
isolated test invocations green, one fast-tier run PASS (8,997 tests). The two affected rows
(`Chat pane / History scrolling and channels`, `Intro screen / Options menu`) are `partial`, which
is reflected in the scorecard.

## Unfinished / known limits

- **Not picked this wave** (still open in the matrix, untouched): the career cluster
  (Scoreboard/leaderboard query-search-find-me-sort ×3, Character-info career stats/profile
  editing/leaderboard navigation ×3 — needs a persistent career store the port does not have
  yet), the two server-list rows (sort/filter stack + find-player — needs Lobby directory
  player data), the two loadout-panel rows (saved/custom loadout slots + the buy/REQUEST_MONEY
  path), Mission briefing civ-generated prose (1.5) and the training rows (out of scope).
- The chat pane's **Players filter mode** approximates native `IsFromPlayer` with "sender
  resolves to a character name" (the port's EXPORT_SHIP only names player ships); documented
  in `ChatLinePassesPaneFilter`. The **Alt+C cycle is a documented port adaptation** with no
  native equivalent, so the history row is `partial`.
- The **Options menu's Sound/Game groups are one-press toggles**, not native submenus, so that
  row is `partial` (the inherited G/S/K hotkeys and the A/F toggles are fixed in P757b).
- The **manual's page content** is the port's own text (native pages live in the MDL helpdata
  namespace); the navigation machinery, the hlpStart entry point and the generated key reference
  are the faithful parts.
- The **Options menu's Graphics Device** group is not offered (the port has no device picker —
  platform constraint); Graphics/Sound/Game/Controls are the four offered groups.
- The splash **movie** (intro.avi) is not replayed — no movie asset in the port; the splash
  draws title + loading and follows the native timeout/skip rules.
- The intro credits content remains the port's short credits; the scrolling/reset mechanics are
  the native-faithful part.

Scratch: everything lived in `.scratch/` inside the worktree (added to
`.git/info/exclude`, never committed) and was deleted at the end; the small ledgers this report
cites were copied into [P757_assets](P757_assets/) (980 KiB total, under the 2 MB evidence cap).
