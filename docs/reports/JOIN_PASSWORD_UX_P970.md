# P970 — Join screen: game passwords that are easy to enter, checked early, logged, and optionally remembered

Task: `P970_BRIEF.md`. Lane: pi lane (`/home/domingo/.claude/jobs/82c1509b/tmp/pi-lane-rules.md`).
Worktree: `allegiance-hermes-p970` (branch `hermes/p970`), nothing committed.

## What was wrong (Domingo's first real join-link test, v0.1.5)

The join-link screen said only "TAB password (if required)". Nothing told the player the game
needed a password, so an empty one was sent; the logon was **accepted**
(`[logon] ... validated=True`), and the refusal surfaced on the **side-select** screen as
"The game password is incorrect." The server logged nothing, the listing said nothing, and the
password had to be retyped on every join.

## Native findings (re-grepped before citing)

| Native fact | Citation |
| --- | --- |
| The mission password lives on the server in `MissionParams.strGamePassword` (`char[17]`, `c_cbGamePassword = 17`) | `Igc/igc.h:995`, `Igc/igc.h:1120` |
| The password is sent by the CLIENT inside `LOGONREQ` and the server copies it for the logon | `FedSrv/FedSrv.CPP:5805-5820` (`FM_VAR_REF(pfmLogon, MissionPassword)` → `pqd->szPassword`) |
| The password is checked **at the mission join**, in the player-creation path: mission cookie lookup → STAGE_OVER → **password** → rank/lives/bans | `FedSrv/FedSrv.CPP:489-515` (cookie/`DPR_NoMission`/`DPR_ServerPaused`), `FedSrv/FedSrv.CPP:648-661` (password → `DPR_BadPassword`, `iSide = NA`) |
| The compare is case-sensitive (`Strcmp` → `lstrcmpA`) | `FedSrv/FedSrv.CPP:650`, `_Utility/Utility.h:1389-1392` |
| The lobby's mission broadcast `LOBBYMISSIONINFO` has **no password field and no "password required" flag** | `lobby/messagesall.h:25-63` |
| The native client's game-list join sends the password and remembers the last attempt (`m_strLastPassword`), defaulting to an empty one | `WinTrek/gamescreen.cpp:1318-1322`, `clintlib/clintlib.cpp:1999-2010` |
| On `DPR_BadPassword` the native client pops the password dialog **pre-filled with the last attempt** and does **not** disconnect (`bDisconnect = false`) — it retries in place from the game list | `WinTrek/gamescreen.cpp:1496-1504` |
| The password dialog: `EditPane::Password`, `SetMaxLength(c_cbGamePassword - 1)`, field focused on open, **Enter = OK** | `WinTrek/passworddialog.cpp:41`, `:57-68`, `:88-95` |
| Native DID have a "save password" checkbox — for the **account** password (`m_pbuttonSavePassword`, `fRememberPW`) | `WinTrek/logon.cpp:112-137` |
| The intro (direct logon) path re-prompts and **reconnects** on `DPR_BadPassword` | `WinTrek/introscreen.cpp:1702-1716`, `:1779-1787` |

### Classification of the port's late check

**Bug in the port, not native design.** Native refused the bad password at the join
(`CreatePlayerInfo`, before any side choice) and the client stayed on the game list to retype.
The port already evaluated the password at the logon (`Server.Dispatch.cs`,
`JoinMission(...)` right after `LOGONACK`), but the client ignored the ordered
`DELPOSITIONREQ(DPR_BadPassword)` that followed the ack and walked into the side-select screen,
which is where the message appeared. Nothing about the native-era design required that; it was a
missing client-side join-answer step plus a silent server.

## What changed

### 1. Advertise "password required" (fact only, never the value)

* `src/Allegiance.Server/Server.Broadcast.cs:37-41` — the live mission snapshot advertises
  `PasswordRequired: ParamsOf(m).StrGamePassword.Length > 0`.
* `src/Allegiance.Server/LobbyHeartbeat.cs:649-651` (per mission) and `:226-244` (payload
  `PasswordRequired: missions.Any(...)`, omitted when false so a pre-P970 host's bytes are
  unchanged).
* `src/Allegiance.Lobby/ServerListing.cs` — `ServerListing.PasswordRequired`,
  `ServerListingDto.PasswordRequired`, `MissionListingDto.PasswordRequired`,
  `RegisterRequest.PasswordRequired`, `MissionListingRequest.PasswordRequired`.
* `src/Allegiance.Lobby/ServerRegistry.cs:123-127` (create), `:177-181` (heartbeat refresh),
  `:470-475` (`ToMissions`), `:431-436` (`ToDto`).
* `src/Allegiance.Lobby/wwwroot/app.js:58-61` — the mission cell gets a lock
  (`\uD83D\uDD12`) via `textContent` only (the page's DOM-only rule is intact).
* Client mirror: `src/Allegiance.ClientCore.Game/ServerListing.cs` (`PasswordRequired`,
  `MissionListing.PasswordRequired`, `ExpandMissions` keeps the PER-GAME fact,
  `FormatRow` shows `[password required]`, `DetailsLines` shows `PASSWORD: required`),
  `src/Allegiance.ClientCore.Game/Discovery/DiscoveredServer.cs` (`[password]` in the row).

### 2. Join screen: focused field, plain statement, character count

* `src/Allegiance.ClientCore.Game/Rendering/JoinPasswordPrompt.cs` — the shared, pure model
  every screen uses (`FocusOnOpen`, `RequirementLine`, `EditLine` with `(n/15)`,
  `RememberLine`, `RememberHint`, `EnterSubmits`, `RefusalMessage`). ASCII only: the bundled
  HUD fonts carry 32..126. Behaviour lives here, so Classic and Modern cannot diverge.
* Join link: `Game1.JoinLink.cs:64-84` (on resolve, seed + focus when `PasswordRequired`),
  `:98-104` (a REQUIRED password submits on Enter; an optional one keeps the two-step edit),
  `:143-178` (requirement line, counted edit line, remember state in the footer).
* Find Server: `Game1.IntroScreen.cs:672-694` (`SyncFindServerPasswordFocus`), `:920-960`
  (requirement/count/remember + fitted rows so the lock can never be clipped),
  `IntroScreenModel.FindServerHint(..., passwordRequired)`.
* Direct Connect: `Game1.IntroScreen.cs:986-1004` (counted line, remember state, refusal line,
  seeded remembered password on entry).
* Server browser: `Game1.HostLan.cs:136-151` (`SyncLobbyPasswordFocus` — prefills a remembered
  password for the newly selected game and clears the previous one's), `Game1.Session.cs:528-536`
  (called on every selection) and `:1053-1072` (requirement line, counted edit line, remember
  state). The browser does NOT steal focus on selection: it joins with ENTER straight off the row
  list and its password-edit mode owns Up/Down (`HandleLobbyPassword`), so auto-focusing would
  stop the cursor from moving; TAB focuses it and a refused join arrives focused.

### 3. Check early — the refusal lands on the join screen

* `src/Allegiance.ClientCore/Session/ConnectionSession.cs:529-559` — `WaitForJoinAnswer`: after
  the LOGONACK, wait for the ordered join answer — the `DELPOSITIONREQ` naming our ship id
  (`_lastPositionRefusal`) or the `SIDE_ROSTER` that means "admitted". No polling race: frames
  are ordered, and a timeout keeps the old behaviour.
* `src/Allegiance.ClientCore.Game/Game1.Session.cs:1290-1305` — `BeginConnect` waits for that
  answer; a `DPR_BadPassword` goes to `HandleJoinPasswordRefusal` instead of `SideSelect`.
* `Game1.Session.cs:1344-1368` — the refusal returns to the screen that started the join
  (intro / browser / join link) with the field focused, the typed attempt kept (native pre-typed
  the last attempt), the saved entry cleared and the address unchanged, so Enter retries without
  leaving the screen (native `bDisconnect = false`).
* `src/Allegiance.ClientCore.Game/JoinLinkConfirmation.cs:93-101` — `RejectPassword` reopens the
  SAME confirmation (no second HTTP lookup, no lost mission selection).
* The server keeps the authoritative check and its order: `Server.MissionCookies.cs:43-49`
  (mission → stage → password), also re-checked at side selection for a password changed after
  logon (`Server.Lobby.cs:4394-4399`).

### 4. Server log of refusals

* `src/Allegiance.Server/Server.MissionCookies.cs:60-100` — one line per refusal,
  `[join] peer N 'Callsign' refused: game password incorrect (DPR_BadPassword …)`, and a
  suppressed-repeats summary; **the attempted and the real password are never printed**.
* Rate-limited by `PerPeerRateLimiter` (`src/Allegiance.Server/PerPeerRateLimiter.cs`) keyed on
  the **callsign**, because a retrying player reconnects and therefore changes connection id.

### 5. Remember password (opt-in, default OFF)

* `src/Allegiance.ClientCore.Game/SavedGamePasswords.cs` — a separate per-user file
  (`game-passwords.json`) **next to `player.json`**, never inside it (`ALLEGIANCE_GAME_PASSWORDS`
  overrides for tests). Key = `lobby|<lobby URL>|<listing id>` for lobby/join-link games, else
  `host|<host>:<port>`; an empty or >16-char password is not stored; at most 64 entries; a
  corrupt file degrades to "nothing remembered".
* Protection: **Unix/macOS 0600** (`File.SetUnixFileMode`) — there is no cheap secret service on
  a plain Linux box (libsecret needs a session D-Bus + keyring daemon) and shelling out to
  `security` on macOS would put the password on a command line; 0600 in the user's own profile
  directory is the `~/.netrc` model. **Windows: DPAPI** (`CryptProtectData`, user scope) so a
  copy to another account cannot decrypt it, with a plaintext fallback if crypt32 is unavailable
  (observable via `SavedGamePasswords.DpapiFailed`; the P/Invoke is compile-checked here, not
  run on Windows).
* UI: `F8` checkbox on every join screen (`UpdateRememberToggle`, `Game1.HostLan.cs:64-110`).
  `F8` is a FUNCTION key on purpose — the password box owns typed characters. Turning it OFF
  deletes that server's saved entry (the join screen's "forget"). A refused password always
  clears the saved entry. Stored only once the join was **accepted** (`SaveRememberedPassword`).

## Screenshots (both HUD styles; inspected, not just counted)

`docs/reports/P970_assets/`:

| File | What is visible |
| --- | --- |
| `p970_joinlink_classic_800x480.png`, `p970_joinlink_modern_800x480.png` | Title, lobby URL, "Confirm to join this game.", server/mission/players, **"This game requires a password (case-sensitive, max 15 chars)."**, the highlighted **`Password: **** (4/15)_`**, footer "TYPE the password ENTER join ESC cancel F8 remember password for this server: OFF" |
| `p970_findserver_classic_800x480.png`, `p970_findserver_modern_800x480.png` | The guarded row carries **`[password]`** and is selected; the orange requirement line, the highlighted `Password: **** (4/15)_`, the `F8 remember … OFF` line, and the hint "(this game requires it)" |
| `p970_joinlink_refused_classic_800x480.png`, `_modern_…` | **"The game password is incorrect. Type it again and press ENTER."** on the join screen with the field focused |
| `p970_joinlink_{classic,modern}_960x540.png`, `p970_findserver_{classic,modern}_960x540.png` | The same two screens at a desktop window size |
| `p970_joinlink_{classic,modern}_1280x720.png` | 720p width: the password block, counters and footer (see the note below) |

The captures come from the real `Game1` client driven headlessly (`tools/p970-join-password-proof/`,
`SDL_VIDEODRIVER=offscreen`), the same recipe as `tools/p883-join-proof`. Both styles were captured
because the HUD rule requires it: these join/intro screens are drawn by `Game1` itself, not by
`ModernHudRenderer`, so all behaviour lives in the shared `JoinPasswordPrompt` model and the two
style pairs come out **byte-identical** (`cmp` on each pair) — the parity is real, and it is
asserted in `P970JoinPasswordUiTests` for both `HudStyle` values (text, focus, counts, ASCII).

**Capture note (pre-existing, not this task):** with this offscreen driver the intro's *top
chrome* (title/subtitle) stops rendering once the window height exceeds ~540 px — a fresh run of
the untouched `tools/p883-join-proof` at 1280x720 reproduces it (its committed `games.png` has a
title, a fresh capture does not). The 800x480 and 960x540 pairs show every line; the 720p pairs
show the whole password band the task changes.

## Tests (new, each 3× alone)

| Suite | Tests | Result |
| --- | --- | --- |
| `tests/Allegiance.Core.Tests/P970JoinPasswordTests.cs` | 6 | Passed 6 / Failed 0 / Skipped 0 — three consecutive runs |
| `tests/Allegiance.Lobby.Tests/P970ListingPasswordTests.cs` | 5 | Passed 5 / Failed 0 / Skipped 0 — three consecutive runs |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P970JoinPasswordUiTests.cs` | 18 | Passed 18 / Failed 0 / Skipped 0 — three consecutive runs |

What they pin:

* **Loopback, early refusal + retry** — a real `Server` with a password game: the logon is
  validated, the join answers `DPR_BadPassword` (`iSide = -1`, our ship id) with **no roster and
  no seat**, and a retry with the right password gets the roster and a seat. Case-sensitivity and
  the open-game path are separate tests. The CLIENT half drives `WaitForJoinAnswer` exactly as
  `BeginConnect` does for both outcomes.
* **The log line** — exactly one `[join] … refused: game password incorrect` line naming the
  pilot, neither the attempt nor the real password anywhere in the log, and 13 refusals produce
  far fewer than 13 lines (rate-limited).
* **Listing flag end to end** — a real server's `LiveMissions` → the real `LobbyHeartbeat` → the
  real lobby `/api/servers` JSON (`passwordRequired` server-level and per mission, no password
  value anywhere) → the client's own record and `ExpandMissions`/`FormatRow`.
* **Profile store** — round trip, key hygiene, delete, **0600 on disk**, corrupt/huge input
  refused without throwing, and the refusal path clearing the entry on disk.
* **UI, both HUD styles** — JoinLink focus + typed count + Enter rule + in-place reopen; Find
  Server focus/prefill; browser prefill without stealing Up/Down; the refusal bounce for intro
  and lobby origins; ASCII safety of every drawn string.
* **Web page** — the lock is in `app.js` and the page still never uses `innerHTML`.

## Gate

* `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
* `tools/build-tools.sh`: every tool project builds (see the run log).
* AOT publish of `src/Allegiance.Server` (server changed): no IL2026/IL3050 (see below).
* `tools/run-tests.sh --tier fast`: one run (below); new tests 3× alone above.
* Showcase never-parks: 5× (below).
* `local.env` present in the worktree; **Skipped count** in the fast run is reported below (the
  artwork corpus is present here, so artwork-gated tests run rather than skip).

* **`dotnet build AllegianceCore.slnx`** — `Build succeeded. 0 Warning(s) 0 Error(s)`.
* **`tools/build-tools.sh`** — `build-tools: all 122 tool projects built` (includes the new
  `tools/p970-join-password-proof/`).
* **AOT publish** (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`) — published, **0 IL2026 / 0 IL3050** and no warnings
  (the new `passwordRequired` fields ride the existing source-generated `ServerJsonContext`).
* **`tools/run-tests.sh --tier fast`** (one run, final tree) — **13,568 cases: 13,563 passed /
  2 failed / 3 not executed**, 9 infrastructure checks pass, 553 s. Skipped = **3** (the optional
  Meshy-pack cases; artwork-gated task coverage did *not* skip — `local.env` is present, so
  `Require.Artwork()` ran). The two failures are live-tier combat/elimination timing tests with
  no join/password/listing code in common, each **passing 3/3 alone in ~0.6 s** (ledgered in
  `docs/TEST_FLAKES.md` under "P970 sweeps"). One timing-only Fast offender
  (`SideEliminationLiveTests…`, 20.6 s, passed in its isolated repeats) is recorded there too.
* **Showcase never-parks 5×** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  serial, alone): **5/5 passed** (base rate in `docs/TEST_FLAKES.md`: 5/5), 3 m 23 s / 4 m 38 s /
  2 m 59 s / 5 m 36 s / 4 m 5 s. No parking-limit failure in any run.
* **Both-HUD evidence** — Classic and Modern captures of every changed screen at 800×480 and
  960×540 (plus 720p width), see the screenshot table.
* **Public-repo hygiene** — no personal path, email or secret in the diff (manual scan of the
  `src/ tests tools docker .github` hunks); `python3 tools/check-glossary-links.py` passes
  (660 paths). The 12 new PNGs under `docs/reports/P970_assets/` are new media and need
  Domingo's review before a push, as the lane rules require.

---

## Native intent

Native's game password is a **server-side join secret**: it rides `LOGONREQ`
(`FedSrv.CPP:5805-5820`), is compared case-sensitively at the mission join
(`FedSrv.CPP:648-661`, `_Utility/Utility.h:1389-1392`), and a mismatch is a
`DELPOSITIONREQ` with `DPR_BadPassword` and `iSide = NA`. The lobby never learned it and never
advertised it (`lobby/messagesall.h:25-63`). The native client learned the requirement only from
that refusal, then opened a focused password dialog pre-filled with the last attempt
(`WinTrek/gamescreen.cpp:1496-1504`, `WinTrek/passworddialog.cpp:57-68`, Enter = OK at `:88-95`)
and retried **without leaving the game list** (`bDisconnect = false`). Native remembered the
ACCOUNT password (`WinTrek/logon.cpp:112-137`), never a game password. The operator got the wire
refusal and nothing on the server log.

## Modern approach

Keep every native rule exactly (case-sensitive compare, same reason code, same order, retype in
place) and remove the two things native's era made impossible:

1. **Advertise the fact, not the secret.** The server heartbeat publishes `passwordRequired`
   (server level and per mission) through the HTTP directory, so the browser can draw a lock and
   the join screen can focus the password **before** the wasted round trip. The password value
   never leaves the server; both the register contract and the public listing have no field for
   it (a hostile host that posts one is ignored — asserted).
2. **Observation instead of silence.** One rate-limited operator line per refusal names the peer
   and callsign and never the password, which is what an operator needs to see. The opt-in
   per-server store ("remember for this server", default off) is the port's extension of native's
   account-password checkbox, kept in a separate user-only file so it never travels with the
   portable `player.json`, encrypted with DPAPI on Windows and 0600 elsewhere; a refused password
   clears it, and F8-off on the join screen is the explicit forget.
