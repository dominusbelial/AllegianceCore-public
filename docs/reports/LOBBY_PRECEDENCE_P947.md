# LOBBY_PRECEDENCE_P947 — an explicit `--lobby` must win over the saved connect profile

Base: `master` at dispatch; worktree branch `pi/p947` (uncommitted, per lane rules).
Scope: client lobby-URL precedence only — no lobby protocol, hosting/punch or P944 join-link behaviour changed.

## 1. The failure (P936 real-internet test, 2026-11-07)

Both clients were started with `--lobby https://<vps>` (process args confirmed), yet the Host
internet game form showed `Lobby: http://127.0.0.1:5000` and the create failed with
"Connection refused (127.0.0.1:5000)". Evidence: `docs/reports/P936_assets/host.png` and
`failure.png`. P871b had already guarded the **preferences** path
(`Game1.IntroScreen.cs:150-153`, condition `!_lobbyUrlExplicit`) but the **connect-profile**
load in the `Game1` constructor still overwrote the command line unconditionally
(`Game1.cs:1814` pre-fix: `if (!string.IsNullOrWhiteSpace(profile.LobbyUrl)) _lobbyUrl = profile.LobbyUrl;`).

## 2. Full trace — every writer of `_lobbyUrl`

`grep -n "_lobbyUrl\s*=" src/Allegiance.ClientCore.Game` (re-run at report time):

| # | Site | Before P947 | After P947 |
|---|------|-------------|------------|
| W1 | `Game1.cs:1788` — `_lobbyUrl = opts.LobbyUrl` (the `--lobby` seed from the head) | first write | unchanged |
| W2 | `Game1.cs:1814-1815` — connect-profile load (Android `IConnectStore`) | **overwrote W1 unconditionally — the P936 bug** | replaced by `LobbyUrl.Resolve` at `Game1.cs:1830-1833`; an explicit `--lobby` can no longer be overridden |
| W3 | `Game1.IntroScreen.cs:153` — `prefs.Lobby` (player.json) when `!_lobbyUrlExplicit` | P871b guard, correct | unchanged (now redundant on desktop — the constructor applies the same tier at `Game1.cs:1830`; kept as a no-op safety) |
| W4 | `Game1.IntroScreen.cs:389` — Play Online fills `ServerListing.LobbyUrlDefault` when `_lobbyUrl` empty | correct (default tier) | unchanged |
| W5 | `Game1.ConnectScreen.cs:325-330` — in-game "Lobby URL" prompt | explicit user edit | unchanged |

**Persistence finding (brief step 2):** the command-line URL is **not** persisted into the
profile at startup — `PersistConnectProfile` (`Game1.ConnectScreen.cs:145-163`) runs only on
explicit user actions (lobby-URL prompt W5 at :321-330, player-name edit :316-319,
add/edit/delete server). The profile already "saves on an explicit user change", so no
change was needed; with the W2 fix an explicit `--lobby` is still not written to the store
unless the player edits something in-game (existing behaviour, kept).

## 3. Full trace — every consumer that re-reads a saved lobby instead of `_lobbyUrl`

| Consumer | Site | Before | After |
|---|------|--------|-------|
| Directory chat relay | `Game1.DirectoryChat.cs:66-67` — `_lobbyUrl` empty → `IntroPreferences.Load().Lobby` | fallback to player.json | **unchanged** — consistent with the precedence (saved tier), and `_lobbyUrl` is now never empty on desktop because the constructor resolves the preferences tier (`Game1.cs:1830-1833`); verified by test |
| Career screen clients | `Game1.Career.cs:50` — `_lobbyUrl` empty → `ServerListing.LobbyUrlDefault` | duplicated default fallback | `LobbyUrl.EffectiveUrl(_lobbyUrl)` (`Game1.Career.cs:53`), same behaviour, one definition |
| F2 identity client | `Game1.Identity.cs:21` — same duplicated fallback | duplicated | `LobbyUrl.EffectiveUrl(_lobbyUrl)` (`Game1.Identity.cs:22`) |
| Host internet form | `Game1.IntroScreen.cs:381` → `HostLanScreenModel.UseInternetLobby`, shown at `Game1.HostLan.cs:79` | inline `IsNullOrWhiteSpace` fallback | `LobbyUrl.EffectiveUrl(_lobbyUrl)` |
| Play Online fetch | poll gates `Game1.IntroScreen.cs:239` / `Game1.Session.cs:486`, `FetchServersAsync` `Game1.Session.cs:800`, http base `Game1.cs:1842-1849` | read `_lobbyUrl` / lobbyBase | unchanged — now fed by the resolved `_lobbyUrl` |
| New-game wizard | `Game1.CreateGame.cs:44,86` — `new LobbyGameClient(_http, _lobbyUrl, …)` | read `_lobbyUrl` | unchanged — now fed by the resolved `_lobbyUrl` |
| P944 internet join target | `Game1.Session.cs:1168` — `new(_lobbyUrl, playerHost.Id)` | read `_lobbyUrl` | unchanged — now fed by the resolved `_lobbyUrl` |

## 4. The fix

* **New `src/Allegiance.ClientCore.Game/LobbyUrl.cs`** — the precedence in one testable place:
  `Resolve` (explicit `--lobby` pins the session, even blank `--lobby=` = manual-only >
  saved connect profile > saved player.json preferences > command-line/default value),
  `EffectiveUrl` (the default fallback every lobby-backed feature shares),
  `Display`, `DescribeSource`.
* **`Game1.cs:1795-1845`** — the constructor loads the connect profile for
  servers/name/last-used only, then resolves the URL once via `LobbyUrl.Resolve`. On the
  Android head (a connect store exists) the profile **is** the saved value — an empty
  `LobbyUrl` deliberately means "manual connect only" (`MainActivity.cs:141`) — so the
  player.json preferences tier is only consulted when no store exists (desktop).
  It then logs once: `[lobby] using <url> (from --lobby|profile|default)` (`Game1.cs:1845`).
* **`Program.cs:51,592`** — the desktop no-args head synthesises `--lobby player.Lobby`
  from player.json (`Program.cs:55-60`); that is a **saved** value, so it no longer claims
  `LobbyUrlExplicit` (it would have beaten a future desktop connect profile and mislabelled
  the log). A real `--lobby` on the command line is untouched.

### Precedence table (implemented + tested)

| Situation | URL used | Log label |
|---|---|---|
| `--lobby <url>` (this session, incl. over profile AND preferences) | the command-line URL | `--lobby` |
| `--lobby=` (explicit blank) | empty — manual connect only, nothing re-enables polling | `default` |
| No `--lobby`, connect profile has a URL (Android) | profile URL | `profile` |
| No `--lobby`, no profile URL (desktop), player.json has a lobby | player.json URL | `profile` |
| Nothing saved (or saved value equals the built-in default) | `ServerListing.LobbyUrlDefault` | `default` |
| Saved profile empty (Android) | empty — manual only, preferences not consulted behind it | `default` |

## 5. Tests

New: `tests/Allegiance.ClientCore.MonoGame.Tests/P947LobbyPrecedenceTests.cs` — 29 tests,
all headless (uninitialized `Game1` + reflection, loopback URLs only, no real host contacted):

* `LobbyUrl.Resolve` precedence matrix (6 facts + explicit-blank pinning), source labels, `EffectiveUrl`/`Display`.
* **One test per consumer from §3** (each a 3-row Theory: explicit / saved / nothing saved):
  host internet form (`HostLanScreenModel.UseInternetLobby`), Play Online fetch
  (`FetchServersAsync` over the `_http` base), directory chat (`UpdateDirectoryChat`),
  career screen (`EnterCareerScreen` → `_careerOrigin`), F2 identity (`UpdateIdentity` →
  `_identityOrigin`), new-game wizard (`EnterCreateGame` → `/api/games/catalog` request host),
  P944 join target (`ConfigureClientTransport` → `ConnectionSession.InternetJoinTarget`).

| Run | Result |
|---|---|
| New P947 tests, 3× alone (final tree) | **29/29 passed ×3** |
| Affected existing classes, 3× alone — `ConnectProfileTests` (5), `P871bDirectoryChatTests` (5), `P871bCreateGameClientTests` (2), `P936InternetHostModelTests` (2), `P936InternetJoinTests` (4) | **all passed ×3** |

## 6. Gate results

| Gate item | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** (re-run on final tree) |
| `tools/build-tools.sh` | **all 118 tool projects built**, 0/0 |
| One full fast suite (`tools/run-tests.sh --tier fast`) | run 1 (pre-final tree): 13,341 exec, 6 failed / 3 NotExecuted, 613.3 s; **run 2 (final tree): 13,342 exec, 4 failed / 13,335 passed / 3 NotExecuted (optional Meshy-pack skips), 629.9 s** — FAIL by non-task failures only |
| Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks`) **5×** | **5/5 passed** (3 m 48 s / 6 m 53 s / 5 m 05 s / 4 m 58 s / 3 m 42 s — duration varies with host load, never parked) |
| New/affected tests 3× alone | §5 — all green |

Non-task failures (each triaged per lane rules, ledgered in `docs/TEST_FLAKES.md` "P947
validation — two Fast sweeps, 2026-11-07 UTC"):

* **Load-only** (failed once in a sweep, passed 3× alone): `MissileTrackWarningLiveTests.EnemyMissileHoming…`,
  `P813cReconnectGraceTests.Voluntary_logoff…`, `P739TimerLiveTests.Warp_bomb_notice…` (delay 7),
  `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown…` (sweep 1); `P660NextMatchLiveTests.Late_game_over_rebuild…`,
  `NaniteThreatBonusLiveTests.Threat_target_earns_2x…` (sweep 2).
* **Persistent, already-ledgered**: `NpcWingmanDefendLiveTests.Defender_disengages…` (both sweeps, fails on unmodified base too).
* **Environment-dependent, new to the ledger**: `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent`
  — fails 3/3 alone on this workstation because a live Tailscale coordinator advertises a
  server (<tailnet address>:6073); needs a skip guard. P947 changes no discovery code.
* **New genuine intermittent, unrelated**: `MissionRotationLiveTests.Finished_mission_rotates…`
  (`Assert.Contains` empty collection, `MissionRotationLiveTests.cs:181`) — 3 failures in 26
  isolated single-test runs (~12%), two later 10-run chains fully green. Core mission
  rotation; no Core source changed in P947. Flagged for tracking.

## 7. Out of scope / not done

* No lobby UI to edit the URL, no lobby protocol or hosting/punch changes, no P944 join-link work.
* `Game1.IntroScreen.cs:153` (P871b preferences guard) intentionally kept — it is a no-op after
  the constructor resolution but is the guard that protects the intro path on the Android head
  if `InitIntro` runs there.
* `Game1.Session.cs:68` (`ConnectionSession`-adjacent `LobbyUrl` field) appears write-dead in
  the current tree (declaration + doc comment only); left untouched.

---

## Native intent (footer)

Native has no HTTP lobby directory; its analogue is the lobby *configuration* the client
downloads before login: the config location comes from the user's own registry setting
`Online.ConfigFile`, falling back to `CfgFile`, falling back to the shipped default URL —
`GetConfiguration()->GetStringValue("Online.ConfigFile",
GetConfiguration()->GetStringValue("CfgFile", "http://allegiance.zaphop.com/allegiance.txt"))`
(`WinTrek/introscreen.cpp:424-425`, re-greped 2026-11-07; the same override pair at
`WinTrek/ZoneClubScreen.cpp:414-415`), then `StartConfigRead(...)` either locally or via
`Downloader().Download(...)` (`introscreen.cpp:430-441`). So native's own precedence is
*explicit per-user configuration > shipped default*, with an explicit local path short-
circuiting the download. The port extends that with a per-session command line (`--lobby`),
which native has no equivalent for; the P936 bug let a lower tier (a saved profile) beat the
session's explicit instruction — the exact inversion the precedence now forbids.

## Modern approach

Yes, with one caveat. The bug class — four near-copies of "if empty use default / overwrite
unconditionally" scattered across the constructor, intro, chat, career and identity — is a
modern-code smell: every new consumer re-decided the precedence by hand, and one of them
(P936) got it wrong. Collapsing it into one pure `LobbyUrl.Resolve`/`EffectiveUrl` with table
tests per consumer makes the precedence executable documentation and the startup log
(`[lobby] using … (from …)`) makes the choice observable in the field instead of
reconstructed from screenshots. The caveat: the two saved stores (Android connect profile
vs desktop player.json) are not strictly ordered by native — the port keeps
"profile beats preferences when a profile store exists, preferences otherwise", which
preserves the Android "empty = manual connect only" contract rather than inventing a new
unification. A single cross-platform profile store would remove that fork, but that is a
larger change than this task warranted.
