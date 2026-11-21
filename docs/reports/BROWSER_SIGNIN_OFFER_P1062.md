# Signed-in players are offered the browser client — P1062

Status: **complete** (items 1–4, every gate attempted). Started 2026-11-20 21:05, finished
2026-11-20 23:54 (lane local time, America/Bogota). Base: master after P1061 (`29272f7d`).
No commits, pushes, deployment or non-loopback traffic: every page and harness request stayed on
`localhost`/`127.0.0.1` (the harness asserts it), and no other host was contacted. Changes are left
in the working tree.

| Item | Status |
|---|---|
| 1 — Offer after sign-in on the lobby identity page | **done** |
| 2 — Sign-in round trip with a validated `return` target | **done** (rules + open-redirect tests; providers keep it safely) |
| 3 — "Play in browser" link per listed game on the lobby home page | **done** |
| 4 — Chromium proof, both directions + screenshots | **done** (green, 3 flows) |

## What changed (file:line)

| Source | Change and reason |
|---|---|
| `src/Allegiance.Lobby/BrowserPreview/BrowserOffer.cs` (new) | `BrowserOffer` (enabled, prefix, validated return target) and `BrowserReturnTarget` — `Normalize` (the one rule that may send the identity page back to the browser client) and `IdentityPage` (the external-provider callback URL). A refused value is `null`/the plain page, never repaired. |
| `src/Allegiance.Lobby/LobbyEndpoints.cs:141-148` | `GET /api/browser[?return=…]`: a read-only config answer like `/api/update/info` — `enabled`, the served `prefix` (empty when disabled) and `returnTarget` after the closed rule. No origin, token or secret. |
| `src/Allegiance.Lobby/Identity/ExternalProviders.cs:39-41,56-61` | The provider challenge's `RedirectUri` becomes `/identity.html?return=<validated target>` when the browser build is enabled; it rides in the data-protected authentication state, so the client cannot substitute a destination. |
| `src/Allegiance.Lobby/wwwroot/identity.html:8`, `identity.css:1` | Signed-in section gains `<a id="play-browser" class="primary" hidden>Play in browser</a>` in the Classic lobby button palette. No other page change. |
| `src/Allegiance.Lobby/wwwroot/identity.js:25-52,56-70,79-95` | Reads the offer once (`/api/browser` with the page's `return`), unhides the action only when signed in **and** enabled, points it at the validated target (else `prefix + "/menu.html?online=1"`), returns to the client in the same tab by `location.replace` after passkey register/sign-in, after provider completion and on page load when already signed in; provider links carry the target. Hidden, no redirect, when disabled or refused. |
| `src/Allegiance.Lobby/wwwroot/app.js:16-41,45-96` | Reads `/api/browser` once; adds a "Play in browser" cell per listed game **only** when enabled and the listing advertises a non-empty `webSocketUrl`; the link names the same `s.id` as the desktop `allegiance://join` link and opens `prefix + "/menu.html?online=1&server=<id>"`. Empty cell otherwise. DOM construction only (`createElement` + `textContent`), empty rows span 10 columns. |
| `src/Allegiance.Lobby/wwwroot/index.html:36` | The new `Play in browser` table column. |
| `src/Allegiance.ClientCore.Browser/Pages/BrowserEntry.cs` (new) | Pure URL contract: `Parse` (`?online=1`, `?server=` with a short id only) and `ReturnTarget`/`SignInHref`. The client never forwards caller text — its own path plus the entry flags. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor:5,37-40,55-57` | Same-tab sign-in link `#online-signin`; the listing entry named by the lobby is marked (`data-preselected`, "Opened from the server list"). |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:30-58,72-80` | Entry state, `SignInHref()`, `OpenEntryAsync()` (auto-pickup) and preselection ordering. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs:77-80,181` | The auto-pickup runs once after the menu loads; `Metrics()` reports `entry.autoOnline`/`entry.preselected` for the harness. |
| `src/Allegiance.ClientCore.Browser/wwwroot/menu.css:11-13` | Preselected-entry marker (Classic/Modern share the markup). |
| `tests/Allegiance.Lobby.Tests/P1062BrowserOfferTests.cs` (new) | 41 cases: the return-target rule, provider callback page, enabled/disabled answers, identity-page/server-list content, DOM safety. |
| `tests/Allegiance.ClientCore.Browser.Tests/P1062BrowserEntryTests.cs` (new), `…csproj:10-11` | 27 cases: auto-pickup parse, unusable ids ignored, return-target/sign-in-href construction, odd paths falling back to the root. |
| `tools/p1062-browser-signin.cjs` (new) | Chromium proof of the three directions (below), with the visible text of every captured frame. |
| `GLOSSARY.md`, `docs/parity/p1062-browser-signin-offer.csv` | P1062 entry and two `n/a` parity rows (modern presentation / existing identity path reused, no new native credit). |
| `docs/TEST_FLAKES.md` | P1062 Fast observations (one load-flake failure, three pass-only timing offenders). |

## Item 2 — the return target (rules and tests)

`BrowserReturnTarget.Normalize(raw, prefix)` accepts a target unchanged only when all hold:

* non-empty, ≤ 2,048 characters, starts with `prefix + "/"` (ordinal: `/play/…`; `/player/`,
  `/PLAY/`, `/play?…` and a bare `play/` are refused);
* visible ASCII only — no whitespace, control characters, backslash, `#` fragment, and **no `%`**
  (an encoded variant is refused rather than decoded a second time: `%2F%2Fhost`, `%252F…`,
  `%2e%2e`, `?x=%2F%2F`);
* no `//` in the path (protocol-relative/doubled separators), no `.`/`..` segment;
* a query is allowed (`?online=1&server=…`), a bare `?` is not.

Refused rows (all in the test theory): `https://…`, `http://…`, `//host`, `///host`, `/\host`,
`javascript:`/`data:`, backslashes, `/play/../api/servers`, `/play/./x`, `/play//host/`, fragments
with and without a query, leading/inner/trailing whitespace, tab/newline, empty, over-2,048.

The client only ever builds `/play/menu.html?online=1[&server=<id>]` from its own path; the identity
page sends the raw value to `/api/browser`, which answers `returnTarget: null` for anything refused,
and the page then shows the plain `/play/menu.html?online=1` link instead of redirecting. External
provider sign-in keeps the target: the challenge's `RedirectUri` is `/identity.html?return=…` built
by `BrowserReturnTarget.IdentityPage` (validated, inside the data-protected auth state), and the page's
`complete` handler then returns to the client. `Provider_callback_page_carries_only_a_validated_target`
covers that decision.

## Item 1 — identity page

`identity.js` fetches `/api/browser` first, then `me`. The action is unhidden only for a signed-in
account when `enabled` is true, so the disabled build shows nothing, and a page load that finds a
signed-in account shows it at once (and, when a validated `return` target is present, returns to the
client immediately). Classic styling only; no other page redesign.

## Item 3 — server list

One extra column and one extra cell per row. The cell holds a link only when the build is enabled and
`webSocketUrl` is a non-empty string (the brief's criterion: no WebSocket route → no link). The
`server=` value is the same `s.id` as the desktop Join link; the client shows that entry first, marks
it, and the ordinary sign-in + ticket path is unchanged.

## Item 4 — Chromium proof (both directions)

`tools/p1062-browser-signin.cjs` — real Chromium **153.0.8010.0** (headless shell, SwiftShader) against
the production-shaped loopback origin used by P1061: `tools/p1051-browser-host` serving the lobby at
`/play` with the real passkey identity, ticket signer, trusted proxy and the game server's WSS route at
`/game` through `tools/p1051-loopback-proxy.cjs`; the published generation reports `2.0.0`, equal to the
server's enforced minimum.

| Flow | What actually ran | Result |
|---|---|---|
| A — lobby home → sign-in → "Play in browser" → join → flight | `/` table row: desktop `allegiance://join` and the browser link name the **same** listing id; identity page signed-out screenshotted, then real passkey registration; `#play-browser` visible with `href=/play/menu.html?online=1`; clicking it landed on `/play/menu.html?online=1` where `#online-panel[data-state="Servers"]` appeared **without** clicking `#online-open`, showing "Signed in as Browser1062…"; password typed, join → sides → hangar → launch → flight | **PASS** (ship 8114, station null, sector 1053; clean logoff) |
| B — reverse from the client | Signed out at `/identity.html`; `/play/menu.html` → `#online-open` → Login; `#online-signin` href decoded to `/identity.html?return=/play/menu.html?online=1`; same-tab navigation; passkey login; page returned by itself to `/play/menu.html?online=1`; server list visible with no refresh or extra click; join → flight | **PASS** (ship 8117, station null, sector 1053; clean logoff) |
| C — lobby server list link | Clicked the row's "Play in browser" → `/play/menu.html?online=1&server=<id>`; `article[data-preselected="true"]` showed "Opened from the server list"; `metrics().entry.preselected` equal to the listing id; join → flight | **PASS** (ship 8120, station null, sector 1053; clean logoff) |

Receipt: `signin-proof.json` — `{"passed":true,"directions":["identityOffer","roundTrip","serverList"]}`,
`autoOnline: true` in both auto flows, three server-confirmed ships, 7 PNGs, **685,475 bytes**. Every
request was asserted to be `localhost`/`127.0.0.1`; no page errors; the only console errors are the
three expected `401` answers of the signed-out identity probe.

Regression: the existing **P1051 flight harness** was re-run on the new client against the same shape —
`{"passed":true,"scenarios":1,"pngBytes":241132}` (Modern 1920 cold: join, mount cycling, cross
visibility/shots, pointer refusal, blur neutral, same-ship WS resume, dock, logoff).

### Screenshots (Classic; all opened and described)

| Image | What is shown |
|---|---|
| [server-list.png](BROWSER_SIGNIN_OFFER_P1062_assets/server-list.png) (47,625 B) | The lobby home page: "Allegiance Lobby — Live Servers", link row, and the 10-column table with **Name, Address, Port, Players, Mission, Core, Version, Last seen, Join, Play in browser**; one row "P1061 loopback [verified]", 127.0.0.1, port 45631, 0 / 200, "P1061 public mission 🔒", `PCore001.igc`, version 990051329, a `Join` link and a `Play in browser` link. |
| [identity-signed-out.png](BROWSER_SIGNIN_OFFER_P1062_assets/identity-signed-out.png) (67,383 B) | The identity page signed out: "Your callsign. Your identity.", the callsign input with its rule line, "Create account with a passkey" / "Sign in with a passkey", the "Approve a device" section and the footer. **No "Play in browser" button.** |
| [identity-signed-in.png](BROWSER_SIGNIN_OFFER_P1062_assets/identity-signed-in.png) (64,529 B) | The same page signed in: "SIGNED IN", callsign **Browser1062796823**, "Account ID: 28d412a216eb4392ac547284335f6f03", the solid teal **Play in browser** button, "Sign out of this browser" below it, then "Approve a device". |
| [client-preselected.png](BROWSER_SIGNIN_OFFER_P1062_assets/client-preselected.png) (59,368 B) | The browser client panel in Classic: "Server list", "Signed in as Browser1062796823", the game-password field, one entry "P1061 loopback" marked by a cyan left bar with the line "Opened from the server list", its "Join P1061 public mission" button, and "Disconnect and return to menu". Behind it the menu toolbar (Classic/Modern/Play online/sound/screenshot) and the intro. |
| [flight-from-identity.png](BROWSER_SIGNIN_OFFER_P1062_assets/flight-from-identity.png) (139,683 B) | Classic flight after flow A: chase view of the scout against asteroids, the circular THRUST/FUEL/SHIELD/ENERGY/AMMO/ARMOR gauges, `20 mps`, `CHASE`, AFTERBURNER PWR 0 %, WEAPONS: ALL, and the bottom toolbar (Mission briefing, View nearby ships, Dock at home station, Starmap, Aleph "Select destination", Fullscreen, Disconnect). |
| [flight-round-trip.png](BROWSER_SIGNIN_OFFER_P1062_assets/flight-round-trip.png) (161,852 B) | The same Classic scene after flow B: ship over the asteroid field, the six gauges, `25 mps`, CHASE, the same toolbar and the top bar "Browser game · Classic · Preferences saved". |
| [flight-from-server-list.png](BROWSER_SIGNIN_OFFER_P1062_assets/flight-from-server-list.png) (145,035 B) | Flow C's Classic frame: ship in the asteroid field with a planet below, `25 mps`, the gauges, CHASE, toolbar and footer. |

## Gate results

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (final source, hang workaround) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 148 tool projects built**, 0 warnings / 0 errors |
| New lobby tests alone, 3× fresh processes | **41/41 ×3** |
| New browser-entry tests alone, 3× fresh processes | **27/27 ×3** |
| Whole lobby suite | **328 passed / 0 failed / 3 skipped** |
| Browser head tests / browser game seam | **39/39** / **91/91** |
| `tools/p1062-browser-signin.cjs` | **green**, 3 flows, 7 PNGs, 685,475 B |
| `tools/p1051-browser-flight.cjs --modern-cold` (regression) | **green**, 1 scenario |
| `node --check` on identity.js, app.js, the harness | OK |
| `scripts/parity-scorecard.py docs/parity/p1062-browser-signin-offer.csv` | validated **2 rows** |
| `tools/check-glossary-links.py` | **1047 paths**, OK |
| `tools/run-tests.sh --tier fast` (last, final source) | **Red, exit 1**: 15,761 passed / **1 failed** / 7 not executed of 15,769, 564.9 s, 9/9 infrastructure checks, calibrated limit 11.540 s. The failure `P467E2eGunDamageDevLiveTests.Completing_dev_143_raises_gun_damage_in_combat` passed **1/1 ×3 alone** (existing gun-damage live family, ledgered); three pass-only cases ran 13.064–13.813 s past the timing limit (all passed). Details in `docs/TEST_FLAKES.md`. An earlier sweep of the same source shape had 0 failures / 15,768 cases (560.9 s, 3 timing offenders). |

## Reproduction

```sh
export DOTNET="$HOME/.dotnet/dotnet" NUGET_HTTP_CACHE_PATH="$PWD/.scratch/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork BROWSER_CLIENT_VERSION=2.0.0
# Publish a generation (needs recorded desktop flight opens; the trace set used here is
# docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz, extracted to a scratch dir).
scripts/publish-browser-flight.sh <gen> <opens/Classic.json> <opens/Modern.json> <flight-opens4/…> …
# Proof host (production shape on loopback: lobby 8049, TLS proxy 8443, game WSS /game).
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" <gen> <host>
# Chromium harness (npm dir with playwright 1.65.1 + @sparticuz/chromium 153.0.0).
node tools/p1062-browser-signin.cjs <npm> <host> <evidence> <gen> <listing-id>
touch <host>/stop   # stops the host and its exact proxy child
```

The proof container needed `libnspr4`/`libnss3` (`apt-get download` + `dpkg-deb -x` into scratch) and a
fontconfig file pointing at the repo's DejaVu fonts; those machine-specific scratch paths are not in
the product sources.

## Evidence

* Bounded assets (685,475 B, under the 2 MB rule): `docs/reports/BROWSER_SIGNIN_OFFER_P1062_assets/`
  — the seven PNGs above plus `signin-proof.json` (per-direction server-side ships, `autoOnline`,
  preselected id, and the exact visible text captured with each frame).
* Raw lane evidence in `~/allegiance-evidence/P1062/`: solution and tools build logs, both Fast logs
  and summaries, the proof-host logs, the harness logs, and a copy of the brief.

## Unfinished / limitations

* **P1058 launch and P1051 security harnesses were not re-run**; the new flow harness and the P1051
  flight harness are the proof for this item, and P1058/P1051-security are untouched by the change
  (no shared control, transport or identity code changed). The P1051 flight helpers the new harness
  reuses were exercised in all three flows.
* Proof tooling lives in ignored scratch (not committed): Playwright 1.65.1 +
  `@sparticuz/chromium` 153.0.0, `libnspr4`/`libnss3` from `apt-get download`+`dpkg-deb -x`, and a
  fontconfig file pointing at the repo's DejaVu fonts. No product change depends on them.
* The seven screenshots are new media and need Domingo's review before a push.
* Docker is not installed here; nothing in this task touches compose/hosting files.
* `P1062_BRIEF.md` is removed from the worktree at the end (a copy is in the lane evidence directory),
  so it is not part of the change set.
