# P500 — Android in-app connect screen (server list, manual host, player name) instead of a baked-in connect.json

## Why this task exists

The Android head (P499, `bf4fc912`) always connects **directly** to the target
in `Assets/connect.json`. `MainActivity.OnCreate` loads `AndroidConnectConfig`
and passes `DirectHost`/`DirectPort`/`PlayerName` to `Game1.LaunchOptions`
(`src/Allegiance.ClientCore.Android/MainActivity.cs:~52-62`). Changing server
or name therefore needs a rebuild. Domingo's 221 APK had to be built from a
worktree with an edited `connect.json`.

**The shared game already has a server-browser state.** When
`LaunchOptions.DirectHost` is null, `Game1` polls a lobby's `/api/servers`.
See `src/Allegiance.ClientCore.Game/Game1.Session.cs` (`LaunchOptions` ~38-60,
the state machine ~143, the draw method ~310-330, the "SERVER BROWSER" title,
and the failed-reconnect fallback to the browser at ~488). The lobby service
exposes `GET /api/servers` (`src/Allegiance.Lobby/LobbyEndpoints.cs:39`,
`ServerListing`: name, address, port, player count and max, mission,
version). Note that **221 currently runs only the game server, not a
lobby**, so manual entry is required, not optional.

**Text input on Android:** `Window.TextInput` doesn't exist on
MonoGame.Android. P499 subscribes via reflection and disables typed chat on
Android (`Game1.cs:~1014-1026`). Text entry here needs MonoGame's platform
soft keyboard, `Microsoft.Xna.Framework.Input.KeyboardInput.Show(...)`
(async, available on Android). Verify the exact API in MonoGame 3.8.5.

## Known bug to fix first (found 2026-10-02 on the emulator)

`AndroidConnectConfig.Load` (`src/Allegiance.ClientCore.Android/AndroidConnectConfig.cs:~24-40`)
calls `JsonSerializer.Deserialize<AndroidConnectConfig>(json)` with default
**case-sensitive** options. The committed `Assets/connect.json` uses
camelCase keys (`host`, `port`, `playerName`) while the properties are
PascalCase, so **nothing ever matches**. No exception is thrown either, so
the app silently uses its hard-coded defaults (`127.0.0.1:2326`, `android`).
**The config file has never actually been read.** P499 didn't notice because
its file contained exactly the default values. It was found when a 221-pointed
APK still connected to `127.0.0.1:2326`. Claude's interim workaround was to
build Domingo's APK from worktree `~/allegiance-android` with PascalCase
keys.

Fix it:
- read with `PropertyNameCaseInsensitive = true`, or `[JsonPropertyName]`
  attributes;
- log the **effective** values, and warn when the file is present but no
  field was recognised;
- add a failing-first test that a camelCase file with non-default values is
  honoured.

Do this before building the connect screen on top of the saved-server seed.

## Scope

1. **A connect screen on Android**, reusing the existing server-browser state
   and its rendering style rather than a parallel UI. It offers:
   - **Saved servers:** host, port and a label, persisted on device (app
     preferences or a file under the app's data dir). `connect.json` becomes
     only the **first-run seed**.
   - **Add / edit / delete server** and **player name**, through the soft
     keyboard.
   - **Optional lobby URL:** when set, merge `/api/servers` listings into the
     list via the existing browser poll.
   - **Connect**, plus the existing failure path (unreachable or refused shows
     an error and returns to the list).
   - The last-used server and name are remembered.
2. **Navigable by gamepad and by touch taps.** Touch here means tapping
   rows and buttons only; flight touch controls are P501. Use MonoGame
   `TouchPanel`.
3. **Desktop unchanged.** The CLI direct-connect and the existing browser
   behave exactly as before.

## Explicitly out of scope

- Flight touch controls (P501), accounts, TLS, Play Store packaging.
- Running a lobby on 221. Note in the report whether it would be useful.

## Verification

- Pure-layout and logic tests (no graphics device) that fail first, where
  applicable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
- **An Android build that includes the assets.** Run
  `source ~/.android_env.sh`, then `scripts/build-android-head.sh`, or build
  the DesktopGL head, run `scripts/pack-android-assets.sh`, then build the
  Android head. A fresh checkout otherwise yields a 45 MB APK with **no
  assets**. Confirm the APK contains `assets/Content` and `assets/artwork`.
- Run it on the `p499emu` emulator (P499 set it up) if you can, and record
  what you observed. **Report only numbers and outcomes you actually
  measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P500_ANDROID_CONNECT_SCREEN.md`.
