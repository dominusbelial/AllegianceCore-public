# P214 — Real lobby (server browser) + real chat in the MonoGame POC

## Summary

P214 wires two things the MonoGame POC (`src/Allegiance.ClientCore.MonoGame/`,
P207–P213) previously lacked, both as **pure client work over real, already-working
infrastructure** — no new protocol, no server/lobby changes:

1. **A real lobby / server-browser screen** (new default entry point). The client
   now starts in a `Lobby` state that polls the configured lobby directory's
   `GET /api/servers` over plain `System.Net.Http.HttpClient`, renders the real
   `ServerListingDto` rows as a selectable list, and joins the highlighted server
   with Enter — reusing the exact connect/logon logic that used to run
   unconditionally at startup (now refactored into `BeginConnect`, callable once a
   server is chosen). The existing `[host] [port] [name]` direct-connect shortcut is
   preserved unchanged (skips the lobby).
2. **Real chat** (in-flight). Enter opens a text-entry line fed by MonoGame's real
   `Window.TextInput` event (not a keyboard scan); Enter sends via the real
   `ConnectionSession.SendChat` (an `FM_CS_CHATMESSAGE(23)` wire send) to
   `ChatTarget.Everyone`; Escape-while-composing cancels without sending *and without
   quitting* (normal flight's Escape-to-quit still works when not composing). Incoming
   chat is drawn from the already-populated `ClientWorld.Events.Chats` (arrival-ordered,
   including the server-relayed self-echo) as a small scrolling overlay.

The server directory service (`Allegiance.Lobby`) and the server's auto-registration
heartbeat (`Allegiance.Server/LobbyHeartbeat.cs`) already existed and were left
completely untouched — this task is a *consumer* of that HTTP API.

## Real lobby URL (not a guessed port)

`dotnet run --project src/Allegiance.Lobby` was launched during development; with no
`appsettings`/`launchSettings` it binds Kestrel's default, confirmed live as
**`http://127.0.0.1:5000`** (`GET /api/servers` → `[]` initially). That real address is
the documented default (`ServerListing.LobbyUrlDefault`), overridable via
`--lobby <url>` / `--lobby=<url>`.

## Key bindings — checked against the LATEST `Game1.cs`/`FlightInput.cs` for collisions

Existing bindings (P209–P213): `W/S` throttle, `Q/E` strafe, `Up/Down` pitch,
`Left/Right` yaw, `A/D` roll, `M` mouse-flight toggle, `V` camera toggle,
`T` autopilot-target select, `G` autopilot engage. `Enter`, `Backspace`, `R`, and
`Escape` were all free, so:

- **Enter** (in-flight) opens chat composition / sends it; (in lobby) joins the
  highlighted server.
- **Backspace** deletes a char while composing.
- **Escape** cancels an in-progress chat line; otherwise quits (unchanged).
- **Up/Down** (lobby) move the selection; **R** forces an immediate lobby refetch.

While chat is being composed, ALL keyboard flight input is gated off (input is fed
`FlightInputState.None`, and the M/V/T/G toggles + mouse-flight application + the
autopilot's manual-input-disengage check are skipped), so typing a word can never also
thrust/turn the ship. The autopilot, once engaged, may keep steering during
composition (it is not a keystroke-derived input).

## Implementation

- `Program.cs` — parses `--lobby`, decides mode: `>=2` positional args → direct connect
  (unchanged), else lobby. Builds a `Game1.LaunchOptions`.
- `Game1.cs` — new `LaunchOptions` + a 3-state machine `ClientState { Lobby, Connecting,
  Flying }`; `Update`/`Draw` dispatch on it. The old startup connect is refactored into
  `BeginConnect(host, port)`; a lobby-initiated join reverts to the lobby with an
  on-screen message on failure (instead of exiting), while the direct path still exits
  on failure (unchanged). Lobby: periodic (3 s) async `HttpClient` GET of `/api/servers`
  with a worker→main-thread staged result (volatile handoff; no cross-thread list
  mutation), navigation/join/retry keys, and a self-contained `DrawLobby`/`DrawConnecting`
  (ASCII-safe text, same HUD fonts/colour conventions as P211). Chat: `Window.TextInput`
  handler appends printable chars; Enter/Backspace/Escape handled by key-edges in
  `UpdateFlying`; `SendChatLine` trims + sends via `_session.SendChat`; `DrawChat` +
  `BuildChatLines` render the log overlay from `ClientWorld.Events.Chats`.
- `ServerListing.cs` (new) — client-side mirror of `Allegiance.Lobby`'s public
  `ServerListingDto` (spoken as camelCase JSON; deliberately NOT a reference to the
  Web-SDK Lobby assembly, the same local-mirror pattern `Allegiance.Server` uses for the
  register response — pulling the Web-SDK project in would drag the AspNetCore framework
  into the client). Carries the documented default lobby URL + the pure `FormatRow()`
  server-row string logic.
- `Rendering/ChatLogFormat.cs` (new, pure) — chat-line composition (`tag + sender: msg`),
  the channel-tag map (Team/Allies/Leaders/Group/Sector/FriendlySector/Admin), and
  **ASCII sanitisation** — the HUD spritefonts bake ASCII 32..126 only, so any char
  outside that must not reach `DrawString` (it throws); high/control chars map to `?`.
- `Rendering/ChatRenderer.cs` (new) — presentation-only chat overlay (rolling last-8 log
  panel + composing line with a blink cursor), ASCII-only cursor/ellipsis.
- `tests/.../LobbyChatLogicTests.cs` (new) — `ServerListing.FormatRow` and
  `ChatLogFormat.Format`/`AsciiSafe` unit coverage (no graphics device), matching the
  project's pure-logic test discipline.

## The two "obvious" pitfalls the brief flagged — both handled

- **Escape-while-typing must not quit the game.** Escape is edge-detected (`_escWasDown`);
  while `_chatActive` it cancels the line instead of exiting, and a single held Escape can
  never both cancel chat and then quit across consecutive frames (the quit only fires on a
  fresh press in a non-composing state).
- **`Window.TextInput`, not a keyboard scan**, captures the typed text (shift/caps/
  punctuation correct), confirmed against `MonoGame.Framework.dll` by reflection:
  `GameWindow.TextInput : EventHandler<TextInputEventArgs>`, `TextInputEventArgs.Character`.

## Out-of-scope adherence

Did NOT touch: `Flight/MouseFlightController.cs`, `Flight/AutopilotSteering.cs`,
`Rendering/ChaseCamera.cs`, `Rendering/CockpitCamera.cs`, `Rendering/ShipRenderer.cs`,
`Rendering/Starfield.cs`, `Rendering/PoseConverter.cs`, any `Audio/` code — read their
key bindings/state, didn't modify their logic. `Allegiance.Lobby/` and
`Allegiance.Server/LobbyHeartbeat.cs` untouched. No hangar/loadout/ready-up menu UIs
(the existing one-shot auto-ready/auto-undock in `Game1.cs` stays). No `SendChatBuoy`,
no private/individual chat, no slash-command parsing — plain `Everyone` broadcast chat
only. Zero `Allegiance.Core` / `Allegiance.ClientCore` changes (client + client tests
only).

## Verification

- `dotnet build AllegianceCore.slnx` → **0 warnings / 0 errors**, whole solution.
- `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` → **95 pass, 0 fail, clean
  across 3 consecutive runs** (existing 84 + 11 new). No server/Core code changed, so
  `Allegiance.Core.Tests` is unaffected by this stage; a whole-solution test run was
  also green (see commit note).
- **Live end-to-end** (real display `:0`): started `Allegiance.Lobby` (bound
  `127.0.0.1:5000`) and a real `Allegiance.Server` on a spare port (temp config,
  `teams:1`, **with a `lobby.url` heartbeat block**, so it genuinely auto-registered).
  Confirmed via `curl <lobby>/api/servers` that the real server appeared (name, address,
  port, `missionName`, `coreFile`, `version`). Then launched the MonoGame client with
  **no host/port args**; via a temporary diagnostic (removed before commit) + an
  offscreen `RenderTarget2D` capture (the P211 Wayland-backbuffer-is-black caveat):
  - the **lobby screen rendered the real server row**, highlighted: `P214 Lobby Test
    Server  127.0.0.1:6780  0 players  P214 Lobby Test`;
  - **join → logon → auto-ready → auto-undock → flight** all proceeded (same
    `[ctrl]`/`STAGE_STARTED` progression the session relies on);
  - a test **chat round-tripped into `ClientWorld.Events.Chats`**: sent
    `P214 diagnostic hello`, and the server's relay of it (with the sender's real ship
    id) appeared in the inbound log — proving `SendChat` → server relay → `Events.Chats`;
  - the **unreachable-lobby failure case** was exercised with `--lobby
    http://127.0.0.1:59999`: the client showed a clear red on-screen error
    (`Lobby unreachable (...): Connection refused ... - retrying...` + `Press R to retry
    now.`) and kept running/retrying — no silent hang, no crash.
  - **Honest limitation:** the two new visual surfaces (browser + chat overlay) were
    verified via offscreen PNG captures + console logs, not by driving real Enter/keyboard
    input through the OS window (no `xdotool` on this host) — the join path itself was
    exercised through the same `BeginConnect` the Enter key calls. Domingo should give the
    screens a real eyes-on playtest.
