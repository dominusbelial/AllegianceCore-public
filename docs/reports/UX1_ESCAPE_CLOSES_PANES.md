# UX-1 — Escape closes panes (investment, teleport, loadout, …), consistently

Base `dca9431f` (master at dispatch), branch `pi/ux1`. Changes are left in the working
tree; no commits or pushes were made.

## Why

Domingo (macOS, 2026-10-19) could not close the investment screen with Escape; only
**B** (back) or **I** (toggle) worked. Players try Escape first, so Escape now closes
the topmost open modal pane/overlay and keeps its old main-menu meaning only when none
is open.

## Native behaviour used as the model

Native citations resolve against `/home/domingo/Development/Allegiance/src/`. Native
panes are mouse-driven `ButtonPane`s; the close action on each pane's **Back** button is
the behaviour Escape now reuses. Native `TK_MainMenu` itself only opens the menu and does
not close panes (`WinTrek/WinTrek.cpp:9304-9320`).

| Pane / overlay | Native close source | Port close action |
|---|---|---|
| Investment | `PurchasesPaneImpl::OnButtonBack` → `TurnOffOverlayFlags(ofInvestment)`, `WinTrek/loadout.cpp:916-920`; toggle `TK_ConModeInvest` at `WinTrek/WinTrek.cpp:10029-10033` | `_investActive = false` |
| Teleport | `TeleportPane::OnButtonBack` → `TurnOffOverlayFlags(ofTeleportPane)`, `WinTrek/teleportpane.cpp:363-366`; toggle at `WinTrek/WinTrek.cpp:10088-10095` | `_teleportActive = false` |
| Loadout / shop | `LoadoutImpl::OnButtonBack` → `SetViewMode(vmHangar)`, `WinTrek/load.cpp:1533-1536`; hangar buttons at `WinTrek/hangarscreen.cpp:195-218` | `_hangarOnly = true` (port's hangar base) |
| Help | `TK_Help` → `OnHelp(false)`, `WinTrek/WinTrek.cpp:9267-9270`, `:9300-9301` | `_helpVisible = false` (existing) |
| Key bindings | `VK_ESCAPE` cancels editing / `OnButtonCancel`, `WinTrek/trekinput.cpp:2959-3014` | `_keyBindVisible = false` (existing) |
| Quick-chat menu | Native popup has no Escape binding (`WinTrek/WinTrek.cpp:820-1010`; the `VK_ESC` compose note is marked NYI at `WinTrek/console.cpp:2374`, `WinTrek/WinTrek.cpp:828`) | `BackQuickChat()` (existing port extension) |
| Credits | No native screen (port extension P577) | `_creditsVisible = false` (existing, `Game1.FlightInput.cs:455-465`) |
| Graphics settings | No native screen (port extension P643) | handled before the main Escape chain by `UpdateGraphicsSettings` (`Game1.GpuBudget.cs:103-108`) |
| Briefing | Native `MissionBriefPopup` stage-advances-and-closes, `WinTrek/teamscreen.cpp:1668-1704` | `DismissBriefing()` (existing) |
| Main menu | `TK_MainMenu` → `ShowMainMenu`, `WinTrek/WinTrek.cpp:9304-9320` | toggled only when nothing above is open (existing) |

## Changes

1. **Escape precedence chain** — `Rendering/MainMenuRules.cs` now resolves one action in
   this order (first true wins, so exactly one pane closes per press):

   `briefing → chat → quick-chat → controls → help → investment → teleport → loadout → credits → main-menu`

   The three new docked panes sit between help and credits because the docked screen is
   drawn above credits/key-bindings but below the help overlay (draw order in
   `Game1.Rendering.cs`). Existing precedence is unchanged.

2. **Wiring** — `Game1.FlightInput.cs:396-437` passes
   `investment: _investActive`, `teleport: _teleportActive`, and
   `loadout: _dockedActive && _cameraMode != Command && !_hangarOnly && _shopModel is not null`
   into the rule and handles the three new actions:
   - `Investment` → `_investActive = false`
   - `Teleport` → `_teleportActive = false`
   - `Loadout` → `_hangarOnly = true` (back to the hangar base, native `vmHangar`)

3. **Key hints** — panes that already show a hint line now advertise the Escape close key:
   - Investment: `... [/] TABS  ESC/B CLOSE` (`InvestmentLayout.cs:26`)
   - Teleport: `UP/DOWN NAV   SPACE GO   ESC/B CLOSE` (`TeleportLayout.cs:29`)
   - Loadout shop: `... U UNDOCK   ESC HANGAR` (`ShopLayout.cs:48`)

   Help, key bindings, credits, quick-chat and graphics settings already showed their
   Escape/close hint (`HelpOverlayRenderer.cs:166`, `KeyBindingRenderer.cs:121,138`,
   `CreditsOverlayRenderer.cs:171-172`, `QuickChatMenuRenderer.cs:93`,
   `Game1.GpuBudget.cs:161`).

## Tests

New pure tests in `tests/Allegiance.ClientCore.MonoGame.Tests/UX1EscapeClosesPanesTests.cs`
cover the brief's three requirements headlessly:

- Escape closes the investment pane (`EscapeUiAction.Investment`).
- With two panes open, only the topmost closes (help+investment → Help;
  investment+credits → Investment; teleport+credits → Teleport).
- With none open, Escape keeps the existing main-menu meaning, including the
  override/lockdown suppression, and existing composition precedence is unchanged.

`TeleportLayoutTests.Empty_list_shows_no_boardable_and_has_close_hint` was updated to the
new `ESC/B CLOSE` hint.

## Verification

- Whole-solution Debug build: **0 warnings / 0 errors** (`dotnet build AllegianceCore.slnx`).
- New/affected tests run **3× alone**: 52/52 passed each run
  (`UX1EscapeClosesPanesTests` + `P642ConsoleZoomTests`). After the teleport hint
  assertion was updated, `UX1EscapeClosesPanesTests` (8 tests) and the renamed
  `TeleportLayoutTests.Empty_list_shows_no_boardable_and_has_close_hint` each passed 3/3
  alone.
- Fast tier (`tools/run-tests.sh --tier fast --no-build`): **8884 tests PASS**
  (first run failed only on the stale teleport `BACK` assertion, fixed above; the rerun
  passed). Summary: `TestResults/tiers-20261020T025605.213216Z/summary.json`.

No `src/Allegiance.Server` or `src/Allegiance.Core` changes, so the AOT publish gate was
not required. No Live-tier test was added or touched.

## Unfinished / notes

- The skirmish-pause Escape branch (`Game1.FlightInput.cs:318-326`) keeps its existing
  toggle-main-menu behaviour; investment/teleport are not reachable while the skirmish
  pause branch returns early, so this is unchanged.
- Scoreboard / pilot-info / minimap / team-pane / game-state / player-pings are treated as
  HUD or non-modal console panes and were left out of scope per the brief.
- No scratch data was produced; `.scratch/` is excluded in the shared git exclude file.
