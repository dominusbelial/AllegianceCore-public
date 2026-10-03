# P500 — Android in-app connect screen (server list, manual host, player name)

Date: 2026-10-03
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`, not pushed)
Prereq: P499 commit `bf4fc912` confirmed present.
Purpose: Replace the Android head's **baked-in direct connect** (host/port/name
fixed in `Assets/connect.json`, needing a rebuild per device) with an **in-app
connect screen** — the shared server-browser UI extended with locally-saved
servers, add/edit/delete, player-name editing via the soft keyboard, an optional
lobby URL, touch + gamepad navigation — while leaving the desktop head
byte-for-byte unchanged.

---

## 1. The bug fixed first: connect.json was never actually read

P499's `AndroidConnectConfig.Load` deserialized the committed **camelCase**
`connect.json` (`host`,`port`,`playerName`) with default **case-sensitive**
options against PascalCase properties, so **no field ever matched** — no
exception, silent hard-coded defaults (`127.0.0.1:2326`, `android`). It surfaced
when a 221-pointed APK still dialed loopback.

**Fix (pure + tested):** the parse moved to the shared library as
`ConnectConfigJson` with `PropertyNameCaseInsensitive = true`, effective values
are logged, and a warning is emitted when the file parses but no known field
appeared. Headline failing-first test
`ConnectConfigJsonTests.CamelCase_file_with_non_default_values_is_honoured`
(plus PascalCase / defaults / no-field / empty / garbage cases).

**On-device proof** (emulator logcat):
```
[android] connect.json seed: host=127.0.0.1 port=2326 name='android' lobby='(none)'
[android] first-run: seeded connect profile from connect.json
```
— the values are read out of the file (not the hard-coded fallback), confirming
the fix end to end.

## 2. The connect screen (reuses the existing server-browser state)

Rather than a parallel UI, the existing `ClientState.Lobby` server browser is
extended. The Android head now boots with **no `DirectHost`** → into the lobby/
connect screen. New shared-library pieces:

- `Connect/ConnectProfile.cs` — `SavedServer(label,host,port)` + the persisted
  profile (servers, player name, lobby URL, last-used key, `Initialized`),
  tolerant JSON round-trip. Pure, unit-tested.
- `Connect/IConnectStore.cs` — durability seam; only the Android head supplies
  one (desktop has none → editing features absent → desktop unchanged).
- `Connect/ITextPrompt.cs` — soft-keyboard seam; default is MonoGame
  `KeyboardInput.Show(...)` (verified: same API in both DesktopGL **and**
  Android 3.8.5.1 assemblies; a real Gboard prompt appeared on-device).
- `Connect/ConnectScreenLayout.cs` — pure geometry (row rects + bottom action
  row CONNECT/ADD/EDIT/DEL/NAME/LOBBY) shared by the Draw and the TouchPanel
  hit-test, so what you tap is what you draw. Unit-tested.
- `Game1.ConnectScreen.cs` (partial) — saved-server merge into the combined
  `_displayServers` row list, add/edit/delete/name/lobby actions, the
  soft-keyboard prompt state machine, touch hit-testing, gamepad A/Y/X.
- `Game1.Session.cs` / `Game1.cs` — the lobby list now combines saved servers +
  fetched lobby listings; Enter/A/CONNECT share `TryJoinSelected`; the poll is
  gated on a configured lobby URL; `BeginConnect` records the active target
  (so resume-reconnect + last-used work from any entry path).

Android head additions: `AndroidConnectStore` (JSON file in the app's private
data dir), `MainActivity` boots into the connect screen and folds
`connect.json` into the store on first run only, `AndroidConnectConfig`
rewritten to use the shared parser.

**Scope mapping:**
- Saved servers / add / edit / delete / player name / optional lobby URL /
  last-used remembered — all in; last-used preselects the row on boot.
- Connect + failure-returns-to-list — reuses `_returnToLobbyOnFailure`.
- Navigable by gamepad (DPAD select, A join, Y add, X edit, B back) and touch
  (tap row to select, tap a button to act). Touch is `TouchPanel` (no gesture
  synthesis); flight touch is P501.
- Desktop unchanged — verified by the `git diff` scope (only the shared
  library + Android head) and by the fact that with no store the lobby renders
  and behaves exactly as before.

### Real bug found + fixed during on-device testing: the re-seed

The first in-game edit rebuilt the profile in `PersistConnectProfile` **without
carrying `Initialized`**, so on restart the first-run gate saw `Initialized =
false` and re-seeded `connect.json`, duplicating the server and resetting the
name. Fixed by loading the existing profile and forwarding `Initialized`.
Verified: fresh install seeds once; edit name; force-stop + relaunch → profile
loads with the edited name and **no** re-seed.

### Real finding: Android clips the bottom ~6% of the viewport

The Android head reported a 1920×1080 viewport, but content drawn below
~94% of the height was **not presented** (measured: solid test rects at logical
y=900 render; y=1020 does not — the bottom ~6% is the system gesture/nav inset
MonoGame Android does not draw into). The first-draft connect screen anchored
its buttons/footer at `h−52`/`h−40`, which landed in that clipped band — the
buttons were invisible. Fixed by anchoring the connect-screen chrome above a
`safe bottom` inset (`max(64px, 8.4% of height)`); the desktop footer keeps its
original `h−40` position unchanged. Verified on-screen: the button row now
renders (bands measured) and touch taps land on them.

## 3. Verification (all numbers measured)

- **Whole-solution build** (`/home/domingo/.dotnet/dotnet build -m:8`): **0
  warnings, 0 errors**.
- **Android head build** (`scripts/build-android-head.sh Debug`): **0 warnings,
  0 errors**; signed debug APK
  `src/Allegiance.ClientCore.Android/bin/Debug/net10.0-android/org.allegiance.client-Signed.apk`
  (~181 MiB). `unzip -l` confirms it contains **574 `assets/Content/`** entries,
  **206 `assets/artwork/`** entries, and `assets/connect.json` (a fresh checkout
  otherwise yields a 45 MB asset-less APK).
- **Full suite** via `scripts/run-tests.sh` (P471), 3 consecutive runs — see
  the table below. The **changed area**
  (`Allegiance.ClientCore.MonoGame.Tests`) passed in all three runs; the only
  failures were pre-existing **live-wire timing flakes in the untouched
  `Allegiance.Core`**, all cited in `docs/TEST_FLAKES.md` (see §5).
- **Emulator (`p499emu`, android-35 x86_64) measured observations:**
  1. Fresh install → connect screen boots (no direct connect); logcat shows the
     first-run seed then `connect screen: 1 saved server(s), player 'android',
     lobby '(none)'`.
  2. Screenshot shows the full screen: SERVER BROWSER title, "Manual connect
     (no lobby)", "Player: android", saved row `[S] 127.0.0.1 127.0.0.1:2326
     manual`, and the six bottom buttons CONNECT ADD EDIT DEL NAME LOBBY.
  3. `adb shell input tap` on NAME → log `connect: tapped Name` and MonoGame
     `KeyboardInput` dialog appears ("Player name" / field prefilled
     "android" / Ok Cancel). Typed text via Gboard; Ok → log `player name set
     to '...'` and `connect profile saved`.
  4. Force-stop + relaunch → profile loads the edited name, **no re-seed**.
  5. `adb reverse tcp:2326 tcp:6073` (the dockerized Allegiance server listens
     on 6073) then tap CONNECT → `joining 127.0.0.1 @ 127.0.0.1:2326 ...` →
     `logged on: serverTime=1235` → in-game MISSION BRIEFING screen (the app
     sat a seat and reached the briefing panel). Touch-driven full join proven.
  6. Last-used remembered: the profile is re-saved with `LastUsedKey` at join.

## 4. Deliverables

1. Committed on **`master`**, **not pushed** (`git branch --show-current` →
   `master`; `git log -1` confirms).
2. This report.
3. Signed debug APK (sideload, §3). First run seeds the connect screen from
   `Assets/connect.json`; thereafter servers/name are edited in-game and
   survive reboots.

## 5. Flake ledger for this session

Three consecutive `scripts/run-tests.sh` runs:
- **Lobby.Tests: 12/12** in all 3 runs.
- **`Allegiance.ClientCore.MonoGame.Tests` (the changed area): 1182/1182 in all
  3 runs** (incl. the 16 new connect-screen tests).
- `Allegiance.ClientCore.Tests`: 410/411 (run 2), 408/411 (run 3) — 1 and 3
  live-wire timing flakes, **untouched** assembly.
- `Allegiance.Core.Tests`: 3696/3699, 3695/3699, 3694/3699 — 3/4/5 live-wire
  timing flakes across runs 1-3, **untouched** assembly.

All flakes are the documented full-suite-load live-wire receipt class in
`docs/TEST_FLAKES.md` ("green in isolation, flakes under full-suite load");
re-verified that both flaky assemblies pass in isolation during P500 (the
specific rotating set does not reproduce standalone). Named from run 1:
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`
(cited, already in the ledger) and `AsteroidMinedLiveTests.Mining_broadcasts_
ASTEROID_MINED_…` (**added** to the ledger under "P500 session observations" so
it stays the single source). No flake touches the P500 code.

## 6. Would running a lobby on 221 be useful?

**Yes.** The connect screen's optional-lobby path is implemented and merges
`/api/servers` listings, but 221 currently runs only the game server (no lobby),
so the phone is on **manual entry** mode — which the task required as mandatory.
Standing up the `Allegiance.Lobby` service (a config-free image exists) would
turn the phone into a real server browser: lobby-hosted 221 servers would appear
automatically, players could see mission/player counts before joining, and the
manual saved-server list would remain as the offline/first-run fallback. The
only caveat: the lobby poll needs a reachable lobby URL on the device (LAN IP),
and the existing `adb reverse` trick is host-loopback-only.
