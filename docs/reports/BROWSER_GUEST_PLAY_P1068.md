# P1068 — Browser guest play

Started **2026-11-21 09:00**, America/Bogota (UTC−5); five-hour cap **14:00**.
Finished **2026-11-21 09:50 local**; all owned build/test/browser/host processes exited.
Written before implementation and updated throughout. Working-tree changes only;
no commits, pushes, package installations, public-host access or deployment edits.

Items 1–6 are implemented. Chromium proof passes. The closing Fast sweep has
**zero failed assertions**; its timing guard is red for five existing methods
(each method passed three subsequent isolated repeats; details below).
Desktop client sources, server identity/admission defaults, reserved-name checks,
`docker/public/*` and the browser `/play/index.html` are unchanged.

## Design and implementation evidence

| Requirement | Implementation |
|---|---|
| Discovery capability | `src/Allegiance.Server/Server.Lifecycle.cs:331,368` sets `AcceptsGuests = Identity?.AllowAnonymous ?? true` for injected and production heartbeats, exactly matching `BindIdentity`. `LobbyHeartbeat.cs:89,254,690` publishes only the boolean fact. `src/Allegiance.Lobby/ServerListing.cs:76,143,308` stores/accepts/projects it; `ServerRegistry.cs:150,203,519` handles registration, heartbeat refresh and public projection. `src/Allegiance.ClientCore.Browser/Pages/BrowserServer.cs:9` defaults missing `acceptsGuests` to false. False is omitted from public/server JSON, preserving old meaning. Ownership tokens and enrollment secrets have no field in the public DTO. |
| Play now entries | Lobby `wwwroot/index.html:22` + `app.js:24` offer a primary Play now beside Sign in only after `/api/browser` enables it. `wwwroot/identity.html:7` + `identity.js:37,52` do the same while signed out. The links use the served prefix plus `/menu.html?online=1`; the identity page reuses the server-validated return target. Browser `Pages/Menu.razor:39` offers Play now beside the existing same-tab sign-in action. `Menu.Online.cs:102,114,146` reads the offer and identity; `?online=1` opens the disclosed guest list directly when signed out, honoring the lobby's one-click Play now action; a failed offer never enables guests or blocks the existing signed-in ticket flow. `BrowserEntry` and lobby return-target validation are unchanged. |
| Browser-local identity | `wwwroot/guest-identity.js:4,9,11,16,23,30` owns `allegiance.guest.callsign`, validation, crypto generation, storage, reuse and forgetting. `crypto.getRandomValues` supplies ten independent low-five-bit characters: `Pilot-` plus ten Crockford-base32 symbols, 50 random bits, 16 printable ASCII bytes. No screen, fonts, user agent or other device-derived input is read. Every storage operation, including the storage getter, is caught. Storage failure gives a page-session name and a visible explanation. Forget resets the page identity; if storage cannot actually be cleared, the UI says so. |
| Edit, roll and retry | `Menu.Online.cs:32,45,61,70` loads the identity only after choosing Play now, saves an edited valid name, rolls a new name, forgets it, or rerolls and retries the pending server/mission in one click. Names are validated with the existing `JoinTokens.ValidCallsign` before saving. No guest name is copied to the separate desktop-style intro preferences. |
| Disclosure | `Menu.razor:47` renders the short disclosure before any Join button. The exact claims and their code basis are below. It remains available whenever the guest returns to the server list. |
| Persistent identity | `Menu.Online.cs:29` and `Menu.razor:34` show **Guest · callsign** with a **Sign in / create account** link in the online panel, hangar and flight. `wwwroot/menu.css:51` explicitly keeps it visible through the compact-flight/hangar CSS rules. Both styles consume the same label. `wwwroot/flight-input.js:54` requests fullscreen on the document root so the identity label and sign-in link remain available. `src/Allegiance.ClientCore.Game.Browser/BrowserIdentityModel.cs:6` and `Game1.Browser.cs:21,137` replace the stale canvas `Pilot: pilot (unverified)` identity with the actual guest/account label and prevent a second canvas-edited identity. `Game1.Flight.cs:398` labels the canvas hangar from the same browser model. Signed-in players see their actual account callsign without the desktop-only F2 sign-out instruction. |
| Closed join policy | `Pages/BrowserJoinPolicy.cs:16` retains route validation, HTTPS/loopback, same-origin host/port, WSS, starter core and reported-version gates. Only its identity prerequisite changes: a guest needs explicit `AcceptsGuests`; a signed-in player still needs `Verified` plus an identity issuer. An absent/false guest flag produces **Sign in to join**, and `Menu.razor` renders a disabled join while keeping the server listed. The lobby still publishes WebSocket routes only for verified registrations (`ServerRegistry.cs:440`); this task does not widen route publication. |
| Guest versus signed-in logon | `Menu.Online.cs:268,274` uses the saved guest callsign with a null `identityTicket` callback. `LogonRequest.ZoneTicket` stays unset. An account still uses its canonical callsign and the existing `/api/identity/join-token` callback, requested only after verified asset staging. Password, client version, mission cookie and authoritative server checks are unchanged. |
| Refusal mapping | `Pages/BrowserJoinFailure.cs:8` handles duplicate/reserved names, native `BadPassword`, sign-in-required and both actual server version-refusal wordings. Name conflicts expose **Try another name**; password failures ask for a corrected password. Version failures preserve the required/reported version detail and tell browser players to reload an updated browser build, without claiming a desktop updater installs it. Other refusal text is preserved. `Menu.Online.cs:292` returns the failed attempt to the server list and disposes the owned session before retry. |
| Loopback fixture | `tools/p1051-browser-host/BattleOptions.cs` adds proof-only `--guests 0|1`, default **0**. `Program.cs` applies it to the existing identity configuration and records the advertised fact. Normal P1051/P1065 host admission stays sign-in-only. No production server configuration option is added. |

### Name rules and native sources

Native `_Utility/Utility.h:60,64` defines a 24-byte usable name and a 25-byte
NUL-terminated buffer; `FedSrv/FedSrv.CPP:5817-5818` rejects a missing terminator.
The port's existing `Server.Dispatch.cs:144,2572` rejects overlong/control/format
names. The existing safe account/wire subset is
`src/Allegiance.Core/Identity/JoinTokens.cs:58,77`: 1–24 printable Latin-1
characters, no outer spaces or invisible characters. Browser generation and edits
use this safe subset, without changing server name rules.

Native standalone duplicate checks compare names case-insensitively in
`FedSrv/FedSrv.CPP:600-624`; USEAUTH validates the issuer and binds name/account
at `:5834-5878`. Native password refusal is `:648-661`, with an in-place retry
in `WinTrek/gamescreen.cpp:1498-1505`. The parity CSV
`docs/parity/p1068-browser-guest.csv` records existing-rule reachability and modern
browser additions without claiming new simulation parity credit.

## Disclosure: exact claims and verification

The panel says:

> Your guest identity starts with a random callsign, saved only in this browser. Servers see your callsign while you play. Clearing site data or using another browser or device gives you a different pilot.
>
> Your name is not reserved for you. Registered callsigns cannot be used as a guest on identity-enabled servers. Guest play does not add career or ladder stats to an account and gives you no account squad membership or reserved name.
>
> You can sign in or create an account at any time. Signing in is optional.

| Claim | Verified basis and limits |
|---|---|
| Random and local to this browser | `guest-identity.js:11` uses only Web Crypto bytes. `:16,23` saves/loads only localStorage; there is no account creation or identity registration request. Servers receive the ordinary callsign during logon, so the text explicitly says servers see it. The harness proves no ticket/passkey requests. |
| Clear site data / different browser or device | The name has no synchronization/account path. Removing the key forces generation. Node tests exercise independent storage and forgetting; the actual browser proof gets distinct callsigns in independent contexts and reuses the first name after a page reload. Blocked storage is truthfully labeled page-session-only. |
| Name not reserved | Only `IdentityStore.CreateAccount` (`src/Allegiance.Lobby/Identity/IdentityStore.cs:152`) creates an account/reservation. Guests never call that API. Native/port duplicate checks still refuse an occupied name; a browser-local name grants no ownership. |
| Registered callsigns unavailable to guests | `src/Allegiance.Server/Server.Dispatch.cs:285-291` rejects anonymous reserved names on identity-enabled servers. Standalone servers do not have this rule; the disclosure states that qualification. The existing cache is fail-open if the lobby is unreachable/empty; this task does not strengthen or change it. The populated-cache unit and browser paths are proved. |
| No account career / ladder credit | `Server.Identity.cs:40,91` gives a guest no verified account ID. `Server.Lobby.cs:1478-1479` skips account result entries when it is null. The lobby ladder reads those account-backed career rows (`src/Allegiance.Lobby/Career/CareerStore.cs:256`). Match scoring and in-memory results still exist. |
| No account squad membership | `Server.Identity.cs:45` reads memberships only from verified token claims; guests have no token/claims. The UI does **not** claim all squad match results disappear: team-level squad outcomes can still be reported at `Server.Lobby.cs:1568`. |
| Optional sign-in at any time | `Server.Identity.cs:96-100` already admits no-ticket guests when configured. Every guest panel/hangar/flight retains the same-origin sign-in/create-account link through the unchanged `BrowserEntry.SignInHref` return flow. No account is created or required by this path. |

Keeping or transferring a guest callsign when registering is a follow-up. Sign-in
does not claim that name, transfer guest history or promise account stats retroactively.

## Tests and gates

All commands set `NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1068/nuget-http` before
build/test, use installed packages only, and use the SDK at `$HOME/.dotnet/dotnet`.
Build invocations disable build servers, node reuse and shared compilation.

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors**, final source. |
| `tools/build-tools.sh --drop-bins` | **151 projects passed**, including the final repeat. Wrapper adds `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false`. |
| New `P1068GuestIdentityTests`, alone ×3 | **1/0/0 ×3**. Invokes the actual dependency-free JS tests under Node. Tests crypto provenance/no Math.random, 1,001 generated names, format/length, exhaustive Latin-1 rules, present/invalid/absent/blocked/silent/quota-failed storage, storage getter failures, reuse/edit/roll/forget. |
| New `P1068GuestJoinTests`, alone ×3 | **21/0/0 ×3**. Guest versus account policy, old listing default and JSON round trip, origin/scheme/port/route/core/version constraints, password admission, actual refusal texts, shared identity labels in Classic/Modern, proof-host opt-in. |
| New `P1068GuestListingTests`, alone ×3 | **1/0/0 ×3**. HTTP registration/default-off, both heartbeat transitions, list/single projections, no ownership-token/secret leakage. |
| New `P1068GuestDiscoveryTests`, alone ×3 | **3/0/0 ×3**, with artwork. Real server boot + heartbeat + lobby projection for standalone, identity/anonymous on and identity/anonymous off. Real no-ticket logon agrees with the advertisement; duplicate names are refused case-insensitively, including pending-side names. |
| Existing reserved-name network test, alone ×3 | **1/0/0 ×3**: `P808cReservedCallsignTests.Anonymous_logon_refused_for_a_registered_callsign_but_free_names_join`. |
| Browser test projects, full | **50 passed** (`Allegiance.ClientCore.Browser.Tests`), **188 passed** (`Allegiance.ClientCore.Game.Browser.Tests`), zero failures/skips. |
| Lobby suite, full | **330 passed / 0 failed / 3 optional PostgreSQL skips**. |
| Server identity/logon/password/reserved selection | **127 passed / 0 failed / 0 skipped**. Filter: `FullyQualifiedName~Identity\|FullyQualifiedName~Logon\|FullyQualifiedName~P808cReserved\|FullyQualifiedName~P970JoinPassword\|FullyQualifiedName~P1068`. |
| Changed fullscreen input tests, alone ×3 | **15/0/0 ×3** via `node --test tools/p1051-input-tests.cjs`; keyboard and toolbar fullscreen target the document root, preserve chrome, exit correctly and retain existing gesture/input/audio tests. |
| Guest Chromium harness | **PASS**, no virtual authenticator. Final run details below. |
| JS syntax / glossary / parity / whitespace | All pass. Glossary: 1,075 distinct paths; parity: four valid rows. |
| Closing Fast sweep, run last | **15,898 results, 572.7 s: 15,891 passed / 0 failed / 7 not executed**, 9/9 infrastructure checks pass, calibrated limit **11.608 s**. Exit **1 for timing only**: five passing rows exceed the limit. Existing-method isolated repeats are recorded in `docs/TEST_FLAKES.md`; no threshold, quarantine or test behavior was changed. |

The final sweep ran **09:35–09:45 local** with all browser and build processes
stopped. Its five timing rows were:

| Existing method | Passing sweep row (seconds) | Subsequent isolated results ×3 | Slowest isolated seconds |
|---|---|---|---|
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 16.397 | 2 passed ×3 | 4.096 / 4.053 / 4.301 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 12.612 | 3 passed ×3 | 5.293 / 5.383 / 5.368 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 16.529 | 1 passed ×3 | 3.121 / 3.109 / 3.075 |
| `P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable` | 12.495 | 10 passed ×3 | 0.950 / 1.034 / 0.957 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | 16.127 | 10 passed ×3 | 4.423 / 4.468 / 4.870 |

The preceding broad sweep completed 15,898 results in 606.7 s with one existing
wingman throttle assertion (`expected 16, got 528`) and four existing timing rows.
All five methods passed three isolated runs; the wingman case was green in the
closing sweep. The ledger retains this result rather than silently treating the
first sweep as green. All five final timing methods passed three fresh isolated runs after the closing
sweep as required by the brief; these are the only subsequent test executions.

No training, service, port, environment configuration or server store was added.
Browser-local callsign storage needs no compose setting; Docker is not installed
in this lane. `docker/public/*` stays untouched as requested.

## Browser proof and captures

The archived P1053 Playwright/Sparticuz modules, Chromium **153.0.8010.0**, runtime
libraries and fonts were available locally. No packages were installed. Three
early runs stopped in Chromium/Skia before disclosure because fontconfig was not
configured. `FONTCONFIG_PATH` resolves that container setup issue. The archived
`--single-process` flag also shared guest storage across contexts; the P1068
harness removes that flag so the independent-context identity proof is meaningful.
One attempt started after `build-tools --drop-bins` had removed the proof host;
the host was rebuilt. These setup attempts are not counted as passing evidence.

The passing harness opens the real lobby home, chooses Play now once, reaches the
disclosure, types the actual game password, joins, chooses a side, launches and
flies with W/ArrowRight. It asserts the server's authoritative position changes,
the persistent Guest label and sign-in link are visible in hangar and both flight HUD styles (including full-page fullscreen), and
a second fresh context starting from `identity.html` gets a different name. It
then edits that name to the occupied first callsign and a reserved fixture name,
asserts the real server refusals, and proves **Try another name** rerolls/retries
in one click. It also checks same-browser reuse, forgetting, disabled browser
offers on all three entry points, clean logoff, and zero ticket/passkey requests.
No listing is fabricated for the guest admission/refusal proof.

Final structured evidence: `BROWSER_GUEST_PLAY_P1068_assets/browser-guest.json`.
Timing is an automated fresh-context loopback measurement including menu/pack
loading and the disclosure capture, not measured human reading time or a public
VPS latency claim. The final one-click/fullscreen run reached flight in
**10,647 ms** from lobby page open. Independent contexts used
`Pilot-ARNQV07J4M` and `Pilot-Z3ZX12E47R`; conflict retry joined as
`Pilot-PF5HVVQJZ8`. Duplicate and reserved refusals matched the
expected messages. The ordinary-menu guest/sign-in choice and all three disabled
offers passed. The final capture set is **181,083 bytes**
(45781 / 63494 / 71808), with zero page errors.

| Capture (JPEG quality 80, set ≤2 MiB) | Purpose / review status |
|---|---|
| `guest-disclosure.jpg` | Disclosure panel before joining; actual text asserted through the DOM. **Visual description for Claude.** |
| `guest-flight-classic.jpg` | Classic fullscreen flight with persistent Guest/sign-in label; state and visibility asserted. **Visual description for Claude.** |
| `guest-flight-modern.jpg` | Modern fullscreen flight with the same identity label; state and visibility asserted. **Visual description for Claude.** |

**No images were viewed in this lane.** Media review and visual descriptions remain
for Claude before any future push. The passing set is about 181 KB, below 2 MiB.

### Exact workstation reproduction (loopback only)

Use an existing P1051/P1053 npm directory; do not install packages. From repo root:

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1068/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR="<existing P1051 or P1053 npm directory>"
test -d "$NPM_DIR/node_modules/playwright"
test -d "$NPM_DIR/node_modules/@sparticuz/chromium"
mkdir -p .scratch/p1068/traces
python3 - <<'PY'
import gzip, json
from pathlib import Path
traces = json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name, trace in traces.items():
    target = Path('.scratch/p1068/traces') / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(trace))
PY
# Run after build-tools --drop-bins: that gate removes standalone tool binaries.
for project in tools/p1051-browser-host src/Allegiance.ClientCore.MonoGame src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done
TRACE="$PWD/.scratch/p1068/traces"
GENERATION="$PWD/.scratch/p1068/reproduced-generation"  # choose a fresh directory
scripts/publish-browser-flight.sh "$GENERATION" "$TRACE/opens/Classic.json" "$TRACE/opens/Modern.json" \
  "$TRACE/flight-opens4/Classic.json" "$TRACE/flight-opens4/Modern.json" \
  "$TRACE/flight-miners0/Classic.json" "$TRACE/flight-miners1/Modern.json"
STATE_DIR=$(mktemp -d "$PWD/.scratch/p1068/guest-host.XXXXXX")
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" "$GENERATION" "$STATE_DIR" --guests 1 --seconds 600 \
  > "$STATE_DIR/host.log" 2>&1 &
guest_host_pid=$!
cleanup() { touch "$STATE_DIR/stop"; wait "$guest_host_pid" || true; }
trap cleanup EXIT INT TERM
for attempt in $(seq 1 300); do
  [[ ! -f "$STATE_DIR/state.json" ]] || break
  kill -0 "$guest_host_pid"
  sleep .1
done
# This lane needs the already-extracted local Chromium libraries and fonts:
FONTCONFIG_PATH=/tmp/fonts LD_LIBRARY_PATH=/tmp/al2023/lib CHROMIUM_PATH=/tmp/chromium \
  node tools/p1068-browser-guest.cjs "$NPM_DIR" "$STATE_DIR" \
    docs/reports/BROWSER_GUEST_PLAY_P1068_assets "$GENERATION"
# On a workstation with its standard runtime/fonts, omit those three overrides.
# The harness picks the installed Sparticuz executable when CHROMIUM_PATH is absent.
```

All owned proof hosts use their stop files and are awaited by exact PID. The
harness closes all contexts and Chromium in `finally`. Nothing is deployed.

## Remaining work / follow-ups

The final Fast timing guard remains red for the five existing methods listed below;
there are no failed assertions or unfinished guest features. Visual inspection/descriptions and
future public deployment remain for Claude. The existing public stack requires
the operator to enable anonymous admission separately if it currently disables guests;
this lane changes no public configuration. Guest-name retention on registration
is explicitly outside scope. No other feature item is intentionally deferred.

## Native intent / Modern approach

Native standalone accepted player-supplied names subject to the native size and
case-insensitive duplicate checks; USEAUTH bound the issuer-verified name/account.
This adds an explicit browser choice, a random private-by-default local identity,
a discovery admission fact, honest account-limit disclosure and clear refusal
recovery. Existing server authority, account sign-in and all gameplay/wire rules
stay intact.
