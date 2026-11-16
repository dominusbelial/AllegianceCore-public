# P946 — Intro menu: never trap the arrow keys in the pilot-name field

The main menu no longer steals Up/Down for the pilot-name field: the arrows always move the selection, the field shows a
visible caret + highlight while it owns typed text, and a one-line hint says what the screen accepts. The same audit
found and fixed a second silent-key trap in Find Server (typing `n` in the password opened the New Game wizard), and
gave Direct Connect the same visible focus. The screen model now owns the key→action mapping, so all of it is proven
without a window.

Changes are left in the working tree (no commit, push, `git stash` or remote-host operation). Scratch data lives in
`.scratch/p946/` (git-ignored); the three proof PNGs per style are attached under `docs/reports/P946_assets/`.

## The trap

Before P946 the main menu started with the pilot-name field focused (`_introEditingPilot = true`,
`src/Allegiance.ClientCore.Game/Game1.IntroScreen.cs:58` before this change) and the Main view consumed Up/Down only
when the field was **not** focused:

- `Game1.IntroScreen.cs:256-259` (before): `if (up && !_iUp && !_introEditingPilot)` and the matching Down branch.
- Only Tab released the field, and only while no identity account was signed in (`:255` before). Nothing on screen said
  so — the footer said "UP/DOWN select … TAB edit pilot", which named Tab but not that the arrows were dead until it.

So a fresh install's main menu answered Enter and Tab but ignored the arrow keys: "doesn't let me go up and down", the
screen "feels blocked". The name field never needed the arrows; blocking them bought nothing.

The field is a **port addition**, not native: native has no pilot-name edit box on the intro screen — the character
name comes from the logon flow and is remembered (`trekClient.SaveCharacterName`, `WinTrek/introscreen.cpp:1607`;
`OnLogon` at `:1604`). Native's intro is a pane of mouse-driven buttons: the ButtonPanes are bound at
`WinTrek/introscreen.cpp:1212` (playLan), `:1213` (playInt), `:1221` (trainBig), `:1223` (options), `:1224` (intro),
`:1225` (credits), `:1227` (exit), `:1228` (help), and the handlers are `OnButtonTraining :1558`,
`OnButtonExit :1799`, `OnButtonHelp :1806`, `OnButtonInternet :1822`, `OnButtonLAN :1832`, `OnButtonOptions :1847`,
`OnButtonCredits :1853`, `OnButtonIntro :1865`. The screen class has **no** key handler of its own; the only `OnKey`
methods in `introscreen.cpp` belong to the popups and merely force character translation for their edit panes
(CreditsPopup `:686-694`, FindServerPopup `:862-877`, whose edit-pane `OnChar` is commented out at `:869-881`, and whose
`EditPane` member is commented out at `:712`). A keyboard menu is therefore the port's own design and has to explain
itself. (Two stale pane citations in the port's comments were corrected while re-grepping: exit is `:1227`, not `:1238`;
help is `:1228`, not `:1226`.)

## The fix

1. **Arrows always move the menu.** The mapping is now pure and focus-independent:
   `IntroScreenModel.MainMenuKeys(...)` (`src/Allegiance.ClientCore.Game/IntroScreenModel.cs:89`) returns the selection
   delta, the Enter activation and the Tab focus toggle; `IntroScreenModel.MoveSelection` (`:96`) wraps the cursor. The
   Main view calls it at `Game1.IntroScreen.cs:260-275`. Typing still goes to the name only while the field has focus
   (`IntroScreenModel.TypesPilotName`, `:104`, used by `OnTextInput` at `Game1.Session.cs:1548`), and Enter keeps its
   meaning (activate the highlighted entry). Tab still toggles the field, and only when there is no identity account.
2. **Visible focus + one-line hint.** `DrawIntroMain` (`Game1.IntroScreen.cs:747`) paints the pilot field's highlight
   box and a bright underline plus an explicit vertical caret after the typed name, then draws
   `IntroScreenModel.MainHint(tabAllowed)` (`:109`) at the footer: `Type your pilot name   UP/DOWN choose   ENTER select
   [TAB switch field]   ESC quit`. The Tab segment is omitted when an account is signed in (Tab does nothing then). The
   line is measured and scaled down only if the viewport is narrower than the text, and the footer sits below the pilot
   field, so it cannot enter the menu column at 1280×720, 1920×1080, 2560×1440 or 3440×1440 (pinned by tests).
3. **Short viewports.** The shipping 13-row menu at the 34 px design pitch needs 442 px of column. On SDL's 800×480
   proof surface (and a 480p window) that used to run through the pilot field and off the bottom. `IntroLayout.Compute`
   (`src/Allegiance.ClientCore.Game/IntroLayout.cs:44`) now compresses the rows to the space available and moves the
   identity status line above the column when the design pitch does not fit; 720p and taller keep the exact old
   geometry (`RowHeight = 34`, text scale 1). `IntroLayout.PilotField` (`:83`) is the field's box geometry.
4. **Testable logic.** All of the mapping and the per-view focus rules live in `IntroScreenModel`; no test needs a
   window. The real-window proof drives the shipping handlers through a proof-only key seam
   (`Game1._introProofKeys`, `Game1.IntroScreen.cs:85`, merged in `ReadProofKeyboard`,
   `Game1.TrainingProof.cs:61`), separate from the training autoproof's array (which the training pump resets every
   frame).

## Per-view audit (step 3)

| View | Text field(s) | Was navigation swallowed? | Change |
| --- | --- | --- | --- |
| `Main` | pilot name (always-on; Tab toggles) | **Yes** — Up/Down ignored while the field had focus | Arrows always move the selection; highlight box + underline + vertical caret; hint line; Tab part hidden with an account (`Game1.IntroScreen.cs:260-275`, `:747-819`) |
| `FindServer` | password (Tab toggles) | No for Up/Down (the list always moved), but `N` (New Game) fired while the password was typing, so a password containing `n` jumped to the wizard and the character was lost | `N` gated by focus (`IntroScreenModel.FindServerNewGameKey`, `:131`; call at `Game1.IntroScreen.cs:295-297`); password line gets the focus highlight/caret; hint names the field and switches wording (`FindServerHint`, `:134`; draw `:870-886`) |
| `DirectConnect` | address (focused on entry) + password (Tab) | No list exists to navigate; the risk was an invisible owner of typing (address vs password) | Focus model (`DirectConnectFocus`, `:123`) highlights the owning field with the caret and underline; hint says `TYPE address … TAB password` or `TYPE password … TAB back to address` (`DirectConnectHint`, `:139`; draw `:887-926`). No navigation keys are used in this view. |
| `HostLan` (Host LAN game and Host internet game forms share this view) | game name / port / password rows | No — Up/Down always move the rows and the selected row is the highlighted field; typing targets only the selected text row | Nothing needed (existing row highlight + `TYPE name / port / password   UP/DOWN select …` hint, `Game1.HostLan.cs:66-87`) |
| `CreateGame` | game name | No — Up/Down/Left/Right always move; the cursor row is highlighted and the hint says `TYPE name` | Nothing needed (`Game1.CreateGame.cs:76-82,131-133`) |
| `TrainingSuggest` | none (Tab toggles the checkbox) | No navigation is used; the checkbox is the only control and the hint names Tab | Nothing needed |
| `Settings` (options tree) | none | No text field; Up/Down/Enter/ESC have their own model | Nothing needed |
| `Career` | description/search editors | The editors are modal on purpose (they consume printable keys, then ENTER commits / ESC discards, with a status line) and belong to the out-of-scope identity/career area | Nothing needed (`Game1.Career.cs:180-200,246-270`) |
| `Help` | none | — | Nothing needed |
| `SinglePlayer` / `SkirmishSetup` / `Training` / `Benchmark` / `Credits` / `TrainingSlideshow` | none | — | Nothing needed (list views keep Up/Down; slideshow is "any key: next") |

## Screenshots (real-window proof)

`tools/p946-intro-keys-proof` boots the shipping `Game1` intro with a fresh profile (`player.json` with an empty pilot
and the first-run training suggestion dismissed, via a scratch `XDG_CONFIG_HOME`), presses **real key edges** through
the game's input path (`_introProofKeys` → `ReadProofKeyboard` → `UpdateIntro`), types through the real
`OnTextInput` handler, and photographs the real backbuffer with the game's own autoshot. It asserts
selection 0 → 1 → 2 with two Down presses while the field stays focused, that typing still produces "Nova" after
navigating, that Up returns to 1, and that Tab releases then restores the field. I read every capture myself:

- `docs/reports/P946_assets/p946-00-main-focus-classic.png` — 800×480 fresh-profile menu. "ALLEGIANCE" title (large,
  cyan), subtitle "Choose how to play - Single Player or Multiplayer", then "F2 sign in with a passkey (optional)"
  above the 13-row list (Single Player … Host internet game) with **Single Player** inside the teal selection box.
  Below it the pilot field "Pilot: (unverified)" is boxed in teal with a bright underline and a vertical cyan caret
  after the colon. The footer reads `Type your pilot name   UP/DOWN choose   ENTER select   TAB switch field   ESC
  quit`. Nothing overlaps or clips; all 13 rows and both bottom lines are inside the frame.
- `docs/reports/P946_assets/p946-01-down-twice-classic.png` — after two real Down presses the teal selection box is on
  **Play LAN / Find Server** (row 3 of 13), proving the arrows work with the field focused; the field is still focused
  and now reads "Pilot: Nova (unverified)" with the caret right after "Nova" (the same frame proves typed characters
  still reach the name after navigating). The hint line is unchanged.
- `docs/reports/P946_assets/p946-02-tab-menu-focus-classic.png` — after Tab the menu selection is on **Play Online**
  (one Up from row 3), and the pilot field is plain dim text: no box, no underline, no caret. The hint still offers Tab
  because this profile has no identity account. This is the visible-focus control case.
- Modern twins: `p946-00/01/02-…-modern.png`. The intro shares one renderer across styles (P936), and the captures are
  **byte-identical** to the Classic set — `sha256` of each pair matches (e.g. main focus
  `c12a1e93888183afd3bfe15248942571cbb460d75ea175bd3f5d0760d4407107`), so both styles are covered by the same frames.

Invocation (from the repository root; the offscreen SDL surface is 480 px tall — a taller request renders only its
bottom 480 rows, the P943 finding — so the proof asks for 800×480, the surface the brief names):

```
XDG_CONFIG_HOME=$PWD/.scratch/p946/config \
ALLEGIANCE_HUD_SETTINGS=$PWD/.scratch/p946/config/AllegianceCore/hud-<style>.json \
SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null ALLEGIANCE_NOVSYNC=1 \
  dotnet tools/p946-intro-keys-proof/bin/Debug/net10.0/p946-intro-keys-proof.dll \
  .scratch/p946/shots-<style> <style>
```

Both runs printed `P946_PASS surface=800x480 shots=3`.

## Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/P946IntroKeysTests.cs` (9 tests): arrows move/wrap with the name field
focused; printables edit the name only while focused (control characters rejected); Tab toggles only without an
account and the hint drops its Tab part; Enter activates the selected entry; the main hint is one line; Find Server
password focus keeps list navigation and suppresses the `N` shortcut; Direct Connect focus is password-over-address;
every hint is a single line. `IntroLayoutTests` gained 800×480 and ultrawide coverage plus four geometry tests (identity
line clear of the column, design pitch kept at 720p+, compressed short viewport, pilot box between column and hint).

- New/affected tests, 3× alone: **Passed 30 / Failed 0 / Skipped 0** in each of the three runs, re-run after the final
  build (`.scratch/p946/tests-3x-final.log`).
- The proof was re-run against the final build; its three captures are byte-identical to the attached assets
  (same `sha256`), so the screenshots above are of the shipped code.

## Gate

- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors** (final tree).
- `tools/build-tools.sh`: **all 119 tool projects built** (includes `tools/p946-intro-keys-proof`).
- One full Fast tier (`tools/run-tests.sh --tier fast`): **13,337 executions — 13,325 passed / 9 failed / 3 optional
  Meshy-pack skips**, 664.7 s, infrastructure checks PASS, exit 1 on the failure list (`.scratch/p946/fast-tier.log`).
  Eleven executions also exceeded the 10 s Fast guard. Shared-host load average was 10–16 during the run. No failure
  touches P946's scope; each was run 3× in an isolated process afterwards:
  - **pass/pass/pass (load/scheduling flakes)**: `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`,
    `P864dTeamLobbyClientListLiveTests.A_lobby_player_requests_a_side_and_the_join_side_relay_moves_it_out_of_the_pool`,
    both `NpcPilotTypeGateLiveTests` cases, `MiningTests.Loopback_synthetic_rock_mines_and_ore_reaches_the_wire`,
    `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels`.
  - **fail/fail/fail alone**: `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
    (already-led persistent failure, unchanged code) and `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
    (existing open partial-tip receipt failure; a further isolated run passed in 1 s). The sweep's
    `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` also fails alone 3/3 because this host's Tailscale
    interface is online and the "absent" scan finds the real node — environmental, not task code.
  Everything above is ledgered in the new P946 section of `docs/TEST_FLAKES.md` (`.scratch/p946/isolate.log`).
- Showcase never-parks, 5 sequential attempts: see below.

### Showcase never-parks

Five sequential attempts of `Local_showcase_never_parks_with_target_for_five_seconds` (only that method in each test
process): **4 passed / 1 failed**. The failure was the **arrival** assertion ("Most showcase ships must
reach their objectives", 19/30 inside the 480 s window; longest idle with target 1.958 s, 3,657 shots / 1,231 hits /
10 kills, load average 12–16), **not** the never-park bound: no attempt ever reported `Parked >5s`, and the parking
metric assertions (`idleWithTarget` current/botsLastMinute = 0) passed in the failing run too. The four passes finished
in 7 m 00 s / 4 m 27 s / 6 m 48 s / 4 m 47 s. This matches the existing P700 arrival-sensitive base rate (P942's
patched cohort 4/5, dispatch 5/5 — `.scratch/p946/showcase/runs.log`). No bots/flight/sim code changed; the intro
screen is the only surface touched.

## Files changed

- `src/Allegiance.ClientCore.Game/IntroScreenModel.cs` — key/focus mapping, hint strings, stale native citations fixed.
- `src/Allegiance.ClientCore.Game/Game1.IntroScreen.cs` — Main key handling, focus drawing, hints, Find Server `N`
  gate, proof-only intro key seam, citation fixes.
- `src/Allegiance.ClientCore.Game/IntroLayout.cs` — adaptive row pitch for short viewports, pilot-field geometry.
- `src/Allegiance.ClientCore.Game/Game1.Session.cs` — pilot typing routed through `IntroScreenModel.TypesPilotName`.
- `src/Allegiance.ClientCore.Game/Game1.TrainingProof.cs` — `ReadProofKeyboard` also merges the intro proof keys.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P946IntroKeysTests.cs` (new), `IntroLayoutTests.cs` (extended).
- `tools/p946-intro-keys-proof/` (new proof tool: csproj + Program.cs).
- `GLOSSARY.md` — intro-screen bullet now names `IntroScreenModel`/`IntroLayout` and the proof tool.
- `docs/TEST_FLAKES.md` — new P946 validation section (the sweep's nine non-task failures, each with its isolated
  outcome).
- `docs/reports/P946_REPORT.md` — `docs/reports/` pointer to this report (lane rule) plus the asset/tool/test paths.
- `docs/reports/P946_assets/` — the six captures (172 KB).

## Native intent

Native's intro screen is mouse-driven: every entry is a ButtonPane wired to a handler
(`WinTrek/introscreen.cpp:1212-1228`, handlers `:1558-1876`) and the screen has no key handler at all; the only key
translation is inside popups for their edit panes (`:686-694`, `:862-877`, with FindServerPopup's edit pane disabled
at `:712`/`:869-881`). The pilot-name field and the keyboard menu are the port's additions, so their keys are a port
design decision with no native rule to port — the port must explain them itself, which is what the arrows-always rule,
the visible field focus and the hint line do. Native's remembered character name (`SaveCharacterName`,
`introscreen.cpp:1607`) is the only native behaviour behind the field, and it is unchanged.

## Modern approach

No new artwork, no native asset and no HUD-style divergence: the intro keeps one shared renderer in both `HudStyle`
values (the Classic and Modern captures are byte-identical), the behaviour lives in the shared models
(`IntroScreenModel`, `IntroLayout`), and focus is shown with the intro's existing fonts, teal palette and rectangles.
The hint is one measured, auto-scaled line above the bottom edge, and the menu compresses instead of clipping on short
viewports — the opposite of hiding a rule the player cannot discover.
