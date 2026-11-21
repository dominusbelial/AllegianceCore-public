# P1070 — Browser hangar and full ship loadout in the game window (P1063 gap F2); HTML launch controls behind `?dev=1`

Report name: `docs/reports/BROWSER_HANGAR_LOADOUT_P1070.md`.

Read `AGENTS.md`, `GLOSSARY.md`, `docs/BROWSER_HOSTING.md`, then `docs/reports/BROWSER_DESKTOP_GAP_AUDIT_P1063.md`
(gap row **F2** "Choose a ship or buy anything", task D) and `docs/reports/BROWSER_DIRECTOR_WATCH_P1067.md` (the
pattern: shared renderer/model already compiled into the browser, a browser copy of the desktop `Game1*` glue that cites
the lines it mirrors, desktop untouched).

**Write the report FIRST** and keep it current. **Do not end your turn until items 1–5 are done or the 5-hour cap is
reached.** A status report is not a stopping point.

## Why
Domingo wants the browser to stop feeling like a web page with buttons: the regular in-game menus, starting with the
**hangar and the full ship loadout**. Today the browser shows the docked screen and one HTML **Launch** button that
launches the current hull with its current parts (`src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:252-345`,
`HangarLaunch`; `Pages/Menu.razor` `#online-launch`, `#launch-status`, `#launch-reason`). The player cannot choose a
hull or buy anything.

Everything visual already exists and is already compiled into the browser assembly (the browser csproj compiles
`Allegiance.ClientCore.Game/**` except `Game1*.cs`): `Rendering/HangarScreen.cs`, `HangarScreenRenderer.cs`,
`HangarControls.cs`, `HangarArt.cs`, `HangarLaunchAnimation.cs`, `LoadoutV2Model.cs`, `LoadoutV2Layout.cs`,
`LoadoutV2Input.cs`, `LoadoutV2Renderer.cs`, `LoadoutV2Presentation.cs`, `LoadoutV2Hardpoints.cs`, `LoadoutBuyFlow.cs`,
`ShopModel.cs`, `ShopCatalog.cs`, `ShopLayout.cs`, `ShopHover.cs`. Every wire call exists too: the browser compiles the
shared `src/Allegiance.ClientCore/Session/ConnectionSession*.cs`, which has `SendBuyLoadout`, `SendBoardShip`,
`SendMoneyRequest`, `SendDocked`, `SendQuitMission`, `SendViewClusterRequest`. What the browser lacks is the desktop glue:
`src/Allegiance.ClientCore.Game/Game1.Hangar.cs` (331 lines; `SendBoardShip` :59, `SendMoneyRequest` :113,
`SendBuyLoadout` :155, `SendViewClusterRequest` :184, `SendQuitMission` :215), `Game1.LoadoutV2.cs` (143) and
`Game1.LoadoutV2Input.cs` (146; `SendDocked` :129).

Native: `WinTrek/hangarscreen.cpp`, `WinTrek/loadout.cpp`; messages `C_BUY_LOADOUT` / `S_BUY_LOADOUT_ACK`
(`Inc/Messages.h:65,70`), `C_BOARD_SHIP` (`:686`), `CS_REQUEST_MONEY` (`:940`), `CS_QUIT_MISSION` (`:812`).

## Items
1. **Browser hangar.** When docked, draw the desktop hangar screen in the game window instead of the plain docked
   screen, through a new browser partial (e.g. `src/Allegiance.ClientCore.Game.Browser/Game1.Hangar.cs`) that mirrors
   the desktop glue and cites it. Mouse and keyboard as on the desktop (`HangarControls`), both HUD styles' fonts/
   scaling at 1920×1080 and 800×480. Launch from the hangar uses the same `HangarLaunch` readiness rules the HTML button
   uses today (keep its blocker texts, show them in the game window). Skip local-training-only paths
   (`CompleteLocalTrainingPurchase`, `ApplyLocalTrainingLaunch`) and say so.
2. **Full Loadout V2.** Open the loadout from the hangar exactly as the desktop does: hull choice, every hardpoint,
   cargo, buy flow (`LoadoutBuyFlow`), prices/money from the world, `SendBuyLoadout` and the ACK handling, board-ship and
   money request where the desktop offers them. The hulls/parts offered are the server's (shop catalog + tech), never a
   browser list. Hull art previews: any mesh/texture not in the join pack streams through the existing verified
   `BattlePackStreamer` (P1065) with a stand-in until it arrives; the join budget does not grow.
3. **HTML launch controls behind `?dev=1` (Domingo, explicit).** Without `?dev=1`, the Hangar state shows no HTML
   Launch/status/reason controls: the game window is the interface. With `?dev=1` they stay, so existing harnesses
   (P1051/P1058/P1065/P1066/P1067, all use `/play/menu.html?dev=1`) keep working; check every harness under `tools/` that
   clicks `#online-launch` and confirm it passes `dev=1`. Add a `gameMenu` JS test hook for "hangar visible / loadout
   open / launch" so future harnesses do not need HTML buttons. Do not hide anything else in this task (online panel,
   server list, password, flight toolbar are later tasks).
4. **Docked/undocked transitions.** Docking returns to the hangar; launching leaves it; ejection/death/reconnect follow
   the desktop's rules; a refused buy (money, tech, server ACK failure) shows the desktop's message. Session reset clears
   hangar state.
5. **Proof.** Unit tests: hangar/loadout glue state machine (open, choose hull, buy, ACK success/failure, launch blockers,
   reset), the buy request bytes equal the desktop's for the same choice, dev-flag visibility of the HTML controls.
   Harness `tools/p1070-browser-hangar.cjs` (from the P1065/P1067 harnesses, `p1051-browser-host --bots 1`): join, see
   the hangar in the canvas, open loadout, pick a different hull and a part, buy, launch through the game window, fly,
   dock, see the hangar again; also one run **without** `?dev=1` proving the HTML launch controls are absent. Captures
   (hangar, loadout) Classic and Modern at 1920×1080 and 800×480, JPEG q80, set ≤ 2 MiB in
   `docs/reports/BROWSER_HANGAR_LOADOUT_P1070_assets/browser/`. This lane has **no Chromium** (P1066/P1067): syntax-check
   the harness, give exact workstation commands, leave run and visual descriptions "for Claude". You cannot view images.

## Gate
Build 0/0 + `tools/build-tools.sh`. New/changed tests 3× alone. `tests/Allegiance.ClientCore.Browser.Tests`,
`tests/Allegiance.ClientCore.Game.Browser.Tests`, desktop MonoGame tests, server buy/loadout tests and the lobby suite
green. One `tools/run-tests.sh --tier fast` last (non-task failures 3× alone + `docs/TEST_FLAKES.md`). **The desktop must
not change** (no edit under `src/Allegiance.ClientCore.Game/` unless a pure move proven with the P1066 desktop
zero-delta method). Glossary + parity CSV in `docs/parity/`. Footer: Native intent / Modern approach (hangarscreen.cpp,
loadout.cpp, the buy/board messages).

## Not in scope
Team/side picker, mission briefing, server list or password in the canvas (next task); removing the flight toolbar
(task C); investment/commander screens; HD textures; sound; any change on the public host, `docker/public/*` or the VPS.
**Parallel tasks:** P1068 (guest play) edits `Pages/Menu.Online.cs` (guest label), `BrowserJoinPolicy.cs`,
`BrowserServer.cs`, `src/Allegiance.Lobby/`; P1069 (engine effects) edits the ship pass in
`src/Allegiance.ClientCore.Game.Browser/Game1.Flight.cs` and `Game1.Scene.cs`. Keep your code in **new** files; in
`Game1.Flight.cs` change only the docked-screen branch (one call into your partial); in `Menu.Online.cs`/`Menu.razor`
touch only the launch controls and their dev gate.

---
## Common rules
- Behaviour parity with native; visuals may differ. HUD/screen changes cover Classic AND Modern.
- Lane environment: MS-A2 Debian 13 container, own clone; your worktree is the current directory (own branch). SDK
  `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only).
  **Do not leave the brief file in the change set** (check `git status`).
- **NuGet:** `~/.nuget/packages` is read-only; before any build/test run
  `export NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1070/nuget-http`. No new packages (NuGet or npm).
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1070/`, do not
  commit or push, network only on loopback, do not contact the rig, mini, Mac, VPS or any public host. Public-repo
  hygiene: no secrets, no personal paths/IPs/hostnames in committed files (no absolute paths in JSON receipts). Write
  times in the report in the lane's local time. Every process you start must end before you finish — stop by exact PID.
  Build-hang workaround: `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`.
- **Time:** 5 hours max; report first and kept current. Priority: 1, 2, 4, 3, 5 (tests), 5 (harness).
- **Report:** design with file:line (hangar glue, loadout glue, buy/ACK path, streamed previews, dev gate), desktop
  zero-delta evidence, test list, harness commands, capture list, anything unfinished.
