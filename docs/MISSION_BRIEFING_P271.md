# Mission Briefing Popup + Countdown — P271

## Summary
The port already implemented the server side of the mission-briefing wire
message `SET_BRIEFING_TEXT` (9), but the MonoGame client never decoded or
rendered it. This stage wires that message through the client (decode ->
world state -> renderer) so a player joining a mission actually sees a
dismissible mission-briefing popup with the real server-sent text and a live
"Mission starts in Ns" countdown — fixing the confusing "black screen" moment
flagged in live playtesting.

## Scope delivered
1. **Decode `SET_BRIEFING_TEXT` (9) client-side.** Two layers, matching this
   project's client message-flow (the P264 trap — a new client wire message
   needs BOTH layers):
   - `ConnectionSession` receive switch: `case SetBriefingText.Fmid:
     _world.Apply(SetBriefingText.Decode(m));` (the decode-from-bytes layer —
     without it the packet is silently dropped; proven live, see below).
   - `ClientWorld.Apply(IFedMessage)` dispatch hub: added `case SetBriefingText`
     + a private `Apply(SetBriefingText)` storing `Text` and
     `FGenerateCivBriefing`, exposed as `World.BriefingText` /
     `World.BriefingFGenerateCivBriefing`.
   Field names confirmed against `Allegiance.Core.Wire.SetBriefingText`:
   `Text` (string) and `FGenerateCivBriefing` (bool) — matching the port's
   pinned wire layout (header 4 + one ib/cb var pair + the bool + CB_ZTS blob).
   A decode round-trip test guards against field-name drift.

2. **Render a dismissible mission-briefing popup.**
   - `Rendering/MissionBriefingLayout.cs` — pure, MonoGame-free greedy word-wrap
     (wrap the body into lines fitting the panel; a single over-wide word keeps
     its own line rather than clipping), headless-unit-testable.
   - `Rendering/MissionBriefingRenderer.cs` — draws the centred translucent
     panel following this client's overlay conventions (own `SpriteBatch`
     session, teal border, pure-ASCII strings via `ScoreboardLayout.AsciiSafe`,
     exactly like `HelpOverlayRenderer` / `ScoreboardOverlayRenderer`).
   - Wired into `Game1` (`_briefingRenderer`, constructed in `LoadContent`,
     drawn in `Draw` above HUD/starmap/chat/scoreboard, below help/game-over).
   **Title and body are both the real `Text` the server sends** (the starting
   cluster name): the port's server always sends the generate-civ-briefing
   branch (`FGenerateCivBriefing=true`, `Server.cs:2019`) with that single
   location string, and there is no separate mission-title field on the wire,
   so the cluster name doubles as the synthesized title (the brief's suggested
   "reasonable synthesis").

3. **Surface the 15s briefing countdown.** The popup shows
   `MISSION STARTS IN {N}s...` (amber) using the same client-side timing as the
   P216 countdown announcer: `World.CountdownStartLocalMs` (set by the real
   `SET_START_TIME(14)` receipt) minus the local
   `Audio.CountdownSound.BriefingCountdownSeconds = 15` constant, computed
   exactly as `Game1`'s existing per-second tick logic does. Before the
   countdown begins it shows a neutral `MISSION PREPARING...` line.

4. **Dismissal.** The popup is dismissed by **Enter or Esc** (this client's
   popup-dismiss conventions; while it shows, Enter does not open chat and Esc
   does not quit) **or automatically when the countdown expires** (remaining
   seconds <= 0 — the mission goes live).

## Native citations
- `WinTrek/teamscreen.cpp:606` — `class MissionBriefPopup : public IPopup`,
  the original popup (title + body + tip + countdown, `m_numCountdown`).
- `WinTrek/teamscreen.cpp:795` `GetMissionBriefingTitle` /
  `:671` `GetMissionBriefingBodyText` / `:807` `GetMissionTip` — title/body/tip
  source functions.
- `WinTrek/teamscreen.cpp:1668-1704` — `OnFrame`: the popup is shown **only
  while `GetStage() == STAGE_STARTING` (with a live countdown)** and is
  auto-closed in the `else` branch when the stage advances (mission goes live).
  **Dismissal decision:** native auto-closes the popup when the countdown
  expires / the mission leaves `STAGE_STARTING`; there is no explicit dismiss
  key in that path. This port's client pins `Mission.Stage` to `StageStarted`
  by the join burst (see the P216 notes, `Server.cs:2138`), so it cannot read
  the brief `STAGE_STARTING` window from `Stage` — the countdown-expiry
  auto-dismiss is the faithful equivalent, and Enter/Esc is added as a courtesy
  matching this client's established popup-dismiss conventions (informational
  popups there are dismissed with Enter/Esc).
- `FedSrv/fsmission.cpp:722-732` — native's join-sync emission of the briefing
  to the joining player's own connection; the port replicates the
  generate-civ-briefing branch (`Server.cs:2011-2051`).

## Out of scope (unchanged)
- Civ-specific text variants (`GetCivString`/`GetCivListString`,
  `teamscreen.cpp:622-634`) — the port's server only ever sends the single
  civ-briefing branch; no UI is built for a response that never happens.
- `Server.cs`'s existing `SET_BRIEFING_TEXT` send logic — untouched; the wire
  shape fully supports the rendering (steps 1-2).
- Training-mission slideshow / tutorial — unrelated, separate gap.
- Native reference source untouched.

## Live proof (offscreen screenshots; `/tmp/p271verify`)
Harness (throwaway, `/tmp/p271verify/`, P262-pattern MiniGame) boots a REAL
in-process `Allegiance.Server` (teams:2, PCore001) + a REAL `ConnectionSession`
"PILOT"; a real `SET_BRIEFING_TEXT` arrives in the join burst, is decoded by
the real client, and the real `MissionBriefingRenderer` is rendered to
offscreen RTs. The server's real `StartCountdown` is invoked so a real
`SET_START_TIME(14)` lands on the peer and the seconds-remaining ticks in real
time.

Evidence:
- Server log: `[briefing] peer 1 'PILOT' -> SET_BRIEFING_TEXT(9)
  generateCivBriefing=true text='Ceto' (friendly station 1051 side 0)`.
- Client decode: `Text = "Ceto"`, `FGenerateCivBriefing = True`, `MySideId = 0`.
- `briefing_countdown_{13,10,6,3}.png` — the real popup with the real text +
  `MISSION STARTS IN {N}s...`; the four frames have **distinct MD5s**, proving
  the countdown digit genuinely changed across the real ticking (measure, not
  eyeball). Vision-check of `briefing_countdown_10.png` reads: "MISSION
  BRIEFING" header, title "Ceto", body "Ceto", "MISSION STARTS IN 10s...",
  "(PRESS ENTER OR ESC TO DISMISS)".
- `briefing_preparing.png` — the popup before the countdown begins
  (`MISSION PREPARING...`).
- `briefing_expired_no_popup.png` — after the countdown expires
  (`remaining=0`), the popup is absent: **auto-dismiss confirmed**
  (vision: no panel, just the space backdrop). Manual Enter/Esc dismissal drives
  the same render gate (`IsBriefingVisible` returns false once `_briefingDismissed`).

The live run also proved the **decode-layer trap**: the first run shipped
without the `ConnectionSession` case and the client's `World.BriefingText`
stayed empty while the server logged sending it ("no own ship or no briefing
text — abort") — adding the case fixed it, a genuine must-fail observation.

## Fields rendered (all real wire data)
- `World.BriefingText` — the `SET_BRIEFING_TEXT(9)` `Text` body verbatim (the
  starting cluster name).
- `World.CountdownStartLocalMs` + `BriefingCountdownSeconds=15` — the
  "Mission starts in Ns" line (same source the P216 announcer uses).
- `World.BriefingFGenerateCivBriefing` — retained verbatim (always true here).

## Build / test status
- Whole solution (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **380 passed** (incl. 9 new:
  5 `MissionBriefingLayoutTests` + 4 `ClientWorldBriefingTests`).
- `Allegiance.ClientCore.Tests`: **123 passed**.
- `Allegiance.Core.Tests`: **run 1 — 3125 passed / 1 failed** and **run 2 —
  3125 passed / 1 failed**. The sole failure both runs is the pre-existing,
  documented `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  machine-state flake (persists in isolation and on the pristine tree per the
  project's flake log; zero references to this stage's symbols — client-side
  only, no Core change). Expected per the task's verification criteria.

## Files
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — store briefing text/flag +
  `Apply(SetBriefingText)` + dispatch case.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — decode fmid 9.
- `src/Allegiance.ClientCore.MonoGame/Rendering/MissionBriefingLayout.cs` — pure
  word-wrap (new).
- `src/Allegiance.ClientCore.MonoGame/Rendering/MissionBriefingRenderer.cs` —
  popup renderer (new).
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — renderer + visibility/dismiss
  logic (Enter/Esc + auto-dismiss at countdown expiry).
- `tests/Allegiance.ClientCore.MonoGame.Tests/MissionBriefingLayoutTests.cs`,
  `ClientWorldBriefingTests.cs` (new).
