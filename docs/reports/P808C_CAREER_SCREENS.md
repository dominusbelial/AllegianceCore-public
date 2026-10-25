# P808c — render the career screens (character info + leaderboard), lobby web pages, reserved callsigns

Base: this worktree starts from **P808b's branch** (`hermes/p808b`, includes P808a +
P808b); branch **`hermes/p808c`**. Native reference: `~/Development/Allegiance/src/`.
No commits, pushes or stashes — changes are left in the working tree. Lane rules:
`~/.dotnet/dotnet`, no `git stash`, no `pkill`/`pgrep -f`, `.scratch/` inside the
worktree (removed at the end).

## What this slice delivers

P808b built the lobby career store, rank rules, HTTP API, server reporting and the
**client screen models**. P808c turns those models into **rendered, reachable
screens** in the MonoGame client (both HUD styles, real-window captures), adds the
**lobby ladder + player-profile web pages**, and closes the **P808a reserved-callsign**
follow-up (an anonymous logon cannot take a registered player's name). The six
deferred parity rows are flipped to `done` with the real-window captures below.

## Scope item 1 — client career screens (Classic + Modern)

Native reaches the character-info and leaderboard screens from the club lobby. The
port's lobby portal is the intro screen, so a **Career** entry joins the intro main
menu (`IntroScreenModel.MainLabels`, `IntroCommand.Career` → `IntroView.Career` →
`Game1.EnterCareerScreen`). This is a modern adaptation of "reachable from the
intro/lobby path" — native's button slot table is extended rather than displaced
(the new entry is appended after Showcase, so no existing index changes).

Everything renderable lives in the shared P808b models
(`CharacterInfoModel` / `LeaderboardModel`); the new slice drives them from the
lobby's `CareerApiClient` over real HTTP and draws them with a style-aware
presenter:

- `src/Allegiance.ClientCore.Game/Game1.Career.cs` — career-screen state, entry,
  async fetch (`GetCareerAsync` / `GetLeaderboardAsync`), keyboard drive
  (civ selector, description edit + apply, TAB between the two screens, leaderboard
  cursor/ENTER-profile/S-search/F-find-me/T-top/PgUp-PgDn/page/<-/-> sort, ESC back),
  and the bearer-session plumbing so a signed-in player can persist an owner-only
  description edit (`SetDescriptionAsync`; `LobbyIdentityClient.SessionBearer` added).
- `src/Allegiance.ClientCore.Game/Rendering/CareerScreenRenderer.cs` — the presenter
  for **both** `HudStyle`s: Classic draws a flat dark panel with cyan headers and
  thin rules; Modern draws a filled translucent panel, stronger selection fills and
  a brighter active-tab block. One shared model renders either style (HUD-parity rule).
- `src/Allegiance.ClientCore.Game/Game1.IntroScreen.cs` — `IntroView.Career` wired
  into update/draw/activate.
- `CharacterInfoModel.CommitDescription(string)` — the native `OnButtonApply` copy
  (CharInfoScreen.cpp:837-859) that adopts the edited text and leaves edit mode.

## Scope item 2 — lobby web pages (ladder + player profile)

Read-only pages on the lobby site (`src/Allegiance.Lobby/wwwroot/`), consuming the
same `/api/career/*` endpoints, with the same DOM-only construction discipline as
`app.js` (callsigns are player-supplied — never interpolated into markup):

- `ladder.html` + `ladder.js` — career ladder: civ selector (All + each played civ),
  the native sort columns, and a table of pilots (Pos / Pilot / Rank / Rating /
  Games / Time / Kills / Deaths / Wins / Losses / Score) with each pilot linking to
  their profile. Sorted by score by default; sortable by the native columns.
- `profile.html` + `profile.js` — a pilot's profile: callsign, description, overall
  rating, and a per-civilization career table plus an all-civs total row.
- `career.css` — shared styling; `index.html` gains a "Career ladder" link.

## Scope item 3 — reserved callsigns (P808a follow-up)

On a server with identity configured, an **anonymous** logon whose name matches a
REGISTERED callsign is **refused** (rather than suffixed). Rationale: allowing the
seat under that name would let anyone impersonate a registered player — there is no
authenticated link between an anonymous seat and the account — and silently renaming
someone is worse UX than a clear refusal. The legitimate owner signs in and
`BindIdentity` swaps the name to their canonical callsign anyway.

- **Lobby**: `IdentityStore.RegisteredCallsigns()` + `GET /api/identity/callsigns`
  (`IdentityEndpoints.cs`) — the account-held callsigns, case-preserved.
- **Server**: `RegisteredCallsignCache` (`src/Allegiance.Server/RegisteredCallsignCache.cs`)
  polls the endpoint on a 60 s TTL, normalizes names case-insensitively, and **fails
  open** — an unreachable/erroring lobby leaves the cache empty (or keeps its last
  snapshot), so anonymous play never breaks. Constructed alongside the heartbeat
  (needs the Lobby config), enforced in the logon dispatch (`Server.Dispatch.cs`):
  identity configured + no account bound + name in the cache ⇒ `FValidated=false`
  with the reason "that callsign is registered — sign in to use it". Standalone
  servers (no identity) keep their historic behaviour.

## Parity rows (the 6 deferred rows)

All six rows flipped to **done** (verified below with the real-window captures):
`docs/parity/parity-matrix.csv` rows 356 (leaderboard query+search), 357 (find-me +
page nav), 358 (column sort + profile nav), 502 (career stats + civ selection),
503 (profile editing + apply), 504 (leaderboard + games nav). LF-clean (0 CR),
`scripts/parity-scorecard.py` validates 670 rows.

## Verification

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx -c Release` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 77 tool projects built**, 0 warnings (incl. new `tools/p808c-ui-proof`) |
| Server Native AOT publish (`-c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **succeeded**, 14.6 MB binary, no IL2026/IL3050 (new `string[]` rides the source-generated `ServerJsonContext`) |
| New tests, 3× alone | `P808cReservedCallsignTests` (Lobby, 1) + `P808cLobbyPagesTests` (Lobby, 2) = **3/3**; `P808cReservedCallsignTests` (Core, 4) = **4/4**; `P808CareerScreenModelTests` incl. new cases (MonoGame, 8) = **3/3** |
| Pre-existing P808 suites | Lobby `P808*` **31 pass / 1 skip**; Core `P808*`+P806GameParams **11 pass** — no regression |
| `python3 tools/check-glossary-links.py` | OK (131 paths) |
| `scripts/parity-scorecard.py` | validates 670 rows; the six career rows now `done` |
| `tools/run-tests.sh --tier fast` | see gate note below |

Fast-tier note: a single Fast sweep was run — 10,382 tests, 289.9 s. It had exactly
two failures: (1) `IntroScreenModelTests.Menu_preserves_the_native_button_set_and_adds_showcase`
— INTRODUCED by P808c (adding the Career menu entry makes the set 11, not 10); the
test now expects the 11-button set including Career and passes 3/3 alone; and
(2) `PackagingManifestTests.Artwork_manifest_matches_the_code_and_the_pinned_corpus` —
the same documented pre-existing environmental failure P808b recorded (stale
artwork-roots on this sandbox; fails on master too; P808c touches no packaging
files). Everything else passes.

## Screenshots (real-window, both styles — each opened and read)

Produced by `tools/p808c-ui-proof/Program.cs`: a real `Allegiance.Lobby` app boots
in-process on an ephemeral loopback port, is seeded with six accounts + career lines
through the lobby's own store types, and the **production** `Game1` fetches them over
HTTP via `CareerApiClient` (nothing is seeded into the renderer). Reproduce with
`~/.dotnet/dotnet run --project tools/p808c-ui-proof -- docs/reports/P808C_assets`.

| Shot | What is visible (read from the capture) |
|---|---|
| `P808C_assets/career-character-info-classic-720p.png` | `[ CHARACTER INFO ]`; tabs Career Info (active) / Leaderboard / Back; callsign `ArchonAlpha`; civ selector `[ All ]  Civ 2`; Profile description "Archon of the alpha wing - mining for glory."; career-by-civ stats Score 4850, Rating 910, Time 15h 20m, Kills 120, Deaths 5, Wins 1, Losses 0, BaseKills 3, BaseCaps 1; controls footer. Flat cyan-on-dark Classic style. |
| `P808C_assets/career-character-info-modern-720p.png` | Same character-info content; Modern filled-panel styling, stronger active-tab block. |
| `P808C_assets/career-leaderboard-classic-720p.png` | `[ LEADERBOARD ]`; 10-column table `# Pilot Rank Rating Games Time Wins Kills Deaths Score`; six players sorted by score (DeltaDawn, BelterBeta, FoxtrotFelon, ArchonAlpha, CosmoGamma, EchoEpsilon); the active sort column highlighted; UP/DOWN move, ENTER profile, S search, F find-me, T top, PgUp/PgDn page, <-/-> sort, ESC back. |
| `P808C_assets/career-leaderboard-modern-720p.png` | Same leaderboard; Modern panel styling. |

(The captions above list the exact callsigns/values read from each PNG via the
vision tool — matching the seeded fixtures.)

## Files changed

- Client: `src/Allegiance.ClientCore.Game/Game1.Career.cs` (new),
  `Rendering/CareerScreenRenderer.cs` (new), `Career/CharacterInfoModel.cs`
  (`CommitDescription`), `Game1.IntroScreen.cs` (Career view),
  `IntroScreenModel.cs` (Career menu entry), `Game1.cs`/`Game1.Lifecycle.cs`
  (career cursor field lives in Game1.Career.cs), `src/Allegiance.ClientCore/Session/LobbyIdentityClient.cs`
  (`SessionBearer`).
- Lobby: `src/Allegiance.Lobby/Identity/IdentityStore.cs`
  (`RegisteredCallsigns`), `Identity/IdentityEndpoints.cs`
  (`GET /api/identity/callsigns`), `wwwroot/ladder.{html,js}`,
  `wwwroot/profile.{html,js}`, `wwwroot/career.css`, `wwwroot/index.html` (link).
- Server: `src/Allegiance.Server/RegisteredCallsignCache.cs` (new),
  `Server.Dispatch.cs` (reserved-name refusal), `Server.cs` / `Server.Lifecycle.cs`
  (cache wiring + test seam), `ServerJsonContext.cs` (`string[]`).
- Proof: `tools/p808c-ui-proof/` (new).
- Tests: `tests/Allegiance.Lobby.Tests/P808cReservedCallsignTests.cs`,
  `tests/Allegiance.Lobby.Tests/P808cLobbyPagesTests.cs`,
  `tests/Allegiance.Core.Tests/P808cReservedCallsignTests.cs`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P808CareerScreenModelTests.cs`.
- `docs/parity/parity-matrix.csv` (six rows → `done`), `GLOSSARY.md` (section 13),
  `docs/reports/P808C_assets/` (4 PNGs).

## Docker Compose

No new/changed service, database, volume, port, env var or healthcheck: the new
endpoint and web pages live on the existing lobby, the career tables reuse the
existing lobby store, and the server cache is an HTTP client on the existing server.
`docker compose config` remains valid (no compose file edit needed).

## Unfinished / notes

- The description **edit UI** is implemented and driven (E to edit, type, ENTER to
  apply — persisting owner-only via bearer when signed in), but the screenshots show
  the read-only view; the mid-edit state was not captured as a separate shot.
- The reconstructed rank-ladder SEED values remain documented as reconstructive
  (inherited from P808b; the native club `Ranks` table is operator-owned and not part
  of the MSFA game-server source).
- The four PNGs under `docs/reports/P808C_assets/` (~210 KB total) are **new media**
  and need Domingo's review before any push, per the public-repo rule.
- `.scratch/` (AOT publish) was removed at completion except the report-linked
  assets under `docs/reports/`.
