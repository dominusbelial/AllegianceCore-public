# P499 — Android head (APK) + Bluetooth gamepad, cross-play against the existing server

Date: 2026-10-02
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`)
Prereq: P498 commit `bbc9db3f` confirmed present (`git log --oneline | grep P498`).
Purpose: Step 2 of the free-Android-client plan. Add an **Android head** that runs
the SAME shared `Allegiance.ClientCore.Game` library the DesktopGL head uses, on a
phone, joining the same server (cross-play), with **Bluetooth gamepad** flight
input as the first playable target. Built entirely in user space on a machine that
had **no Android toolchain** beforehand.

---

## 1. Toolchain installed in user space (no sudo)

| Component | Version | Install |
|---|---|---|
| .NET SDK / Android workload | dotnet `10.0.400`; workload `android 36.1.69/10.0.100` | `/home/domingo/.dotnet/dotnet workload install android` |
| JDK | Microsoft OpenJDK `17.0.20.1+1` | `curl -L -o ~/microsoft-jdk-17-linux-x64.tar.gz "https://aka.ms/download-jdk/microsoft-jdk-17-linux-x64.tar.gz"` then extract to `~/jdk-17.0.20.1+1`; `JAVA_HOME=/home/domingo/jdk-17.0.20.1+1` |
| Android cmdline-tools | `11076708` (sdkmanager 16+) | `curl -L -o ~/cmdtools.zip "https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip"` → `~/Android/Sdk/cmdline-tools/latest` |
| Android platforms | `android-34/35/36` | `sdkmanager "platforms;android-36" "platforms;android-35" "platforms;android-34"` |
| build-tools | `34.0.0/35.0.0/36.0.0` | `sdkmanager "build-tools;36.0.0" ...` |
| platform-tools | `adb 1.0.41` | `sdkmanager "platform-tools"` |
| Emulator (bonus, for the build-proof launch) | `emulator` + `system-images;android-35;google_apis;x86_64`, AVD `p499emu` | `sdkmanager "emulator" "system-images;android-35;google_apis;x86_64"`; `avdmanager create avd -n p499emu -k "system-images;android-35;google_apis;x86_64" -d pixel_5` |

Environment is captured in `~/.android_env.sh` (`JAVA_HOME`, `ANDROID_HOME`,
`PATH` for adb) and sourced by `scripts/build-android-head.sh`. No step required
root; everything is under `/home/domingo/.dotnet`, `~/jdk-17.0.20.1+1` and
`~/Android/Sdk`.

## 2. Android head project — `src/Allegiance.ClientCore.Android/`

`net10.0-android`, references the shared `Allegiance.ClientCore.Game` library +
`MonoGame.Framework.Android` **3.8.5.1**.

- **TFM decision (task item 1).** MonoGame 3.8.5.1's Android package targets
  `net8.0-android34.0` (there is no `net10.0-android` build of MonoGame 3.8.x —
  verified from the package nuspec). It does not *require* the head to downgrade:
  a `net10.0-android` application consumes that `net8.0-android34.0` package fine
  (upward TFM compatibility) and can also reference the repo's `net10.0` shared
  library. The head therefore stays **net10.0-android** to match the rest of the
  repo; only the MonoGame *package* is the older net8.0-android build. Documented,
  measured: whole Android build 0 warn / 0 err.
- **Entry Activity** (`MainActivity : AndroidGameActivity`): landscape
  (`ScreenOrientation=Landscape`), fullscreen + keep-screen-on (window flags;
  the immersive-sticky tooltip was deliberately dropped — it layered Android's
  "Viewing full screen" overlay over the game), and `ConfigurationChanges` so the
  Activity is **not recreated on rotation** (P480 keeps players seated; the
  rotation simply doesn't tear the session down).
- **Connect screen/config (task item 1).** Host/port/name come from
  `Assets/connect.json` (`AndroidConnectConfig`), editable per device without a
  rebuild; nothing is hard-coded to a LAN address. A full in-app touch connect
  form is a later step (out of scope).

**Solution-split decision (documented divergence).** The Android head is **not**
added to `AllegianceCore.slnx`, so the DesktopGL solution build and the P471 test
runner never require the Android SDK/JDK environment (keeping the desktop CI
hermetic). It is built explicitly via `scripts/build-android-head.sh` — the same
non-solution pattern the repo already uses for the committed-but-not-in-solution
`weapon-status-proof` tool (P498 §5).

### Assets (task item 2) — `AndroidAssetSource`

`AndroidAssetSource` implements the P498 `IAssetSource` over the APK's packed
assets (reads under `assets/artwork/`, POSIX relative paths preserved). No
loader in the shared library changed — the same `Game1` code that reads the
desktop on-disk tree reads the APK's assets.

- `scripts/pack-android-assets.sh` stages the **P498 manifest's 206 Artwork
  files** (68,751,938 bytes) from `docs/reports/P498_ARTWORK_MANIFEST.json` into
  `Assets/artwork/`, plus the DesktopGL head's **compiled MGCB Content** (574
  files, 161,660,062 bytes) into `Assets/Content/` for `ContentManager`.
- The generated `Assets/artwork` and `Assets/Content` are **gitignored**; only
  the packer, the manifest and `Assets/connect.json` are committed.

**APK size.** Final signed debug APK `org.allegiance.client-Signed.apk` =
**189,105,088 bytes (181 MiB)**, ABIs `arm64-v8a` + `x86_64`, minSdk 24,
compileSdk/targetSdk 36, versionName 0.1.0. Inside: 206 artwork entries (~65.6 MB
compressed in the APK), 574 Content entries (~396 MB uncompressed), the two
managed runtimes, and (with `EmbedAssembliesIntoApk=true`) the embedded managed
assemblies so a bare `adb install` sideload works (see §5). The APK's
*uncompressed* payload (~461 MB) far exceeds Play's 150 MB base-module limit →
**Play Asset Delivery is the step-4 follow-up**, exactly as the task predicted;
for now a sideloaded APK is the deliverable.

### Gamepad input (task item 3) — P380/P445 reuse

Added in the **shared library** so the phone and any desktop pad share the same
flight model:

- `Flight/GamePadFlightInput.cs` — `GamePadFlightRoles`, a pure, unit-testable
  mapper (MonoGame-free public surface) that converts the two analog sticks onto
  the four roles (right stick = yaw/pitch, left stick = roll/throttle, a
  conventional Mode-2 layout matching the port's `DjiFpvRc3` joystick sense) into
  the `JoystickAxes` the **P380 `JoystickFlightController`** already consumes —
  so deadzone/rescale and the throttle latch come free. Signs (incl. the pitch
  inversion that cancels `MapJoystick`'s negation) are the single edit point for
  a device that feels inverted, exactly like `JoystickAxisLayout`.
- `Game1.FlightInput.ApplyGamePadFlight` — the MonoGame glue, folded into the
  same post-Translate override path as the P380 joystick (only when the USB
  joystick didn't take the turn axes): sticks → `ControlData`, buttons OR into
  `stateM` (A / RT = **fire** via `FireContribution`, B / LT = **afterburner**),
  and RB/LB = **P445 throttle steps** (`StepThrottleUp/Down`).
- **Dock & shop navigation (task item 3, minimal):** a `PadNav` snapshot maps
  the DPAD + face buttons onto the existing pane verbs in `Game1.Panes.cs` —
  shop: DPAD Up/Down = cursor, A = select, Y = launch; teleport & invest: DPAD =
  cursor, A = select, B = back. All-false when no pad is present (no change on
  desktop without a pad).

### Lifecycle (task item 4)

`Game1.NotifyPaused()` closes the wire session cleanly on background
(`_session.Shutdown()`); `Game1.NotifyResumed()` offers reconnect to the
remembered direct-connect target (a resume-reconnect failure reverts to the
server browser rather than exiting). The Activity calls them from `OnPause` /
`OnResume` / `OnDestroy`. With `ConfigurationChanges` guarding rotation, players
stay seated across rotation consistent with the P480 desktop path.

## 3. Verification (all numbers measured)

- **Whole-solution build** (`/home/domingo/.dotnet/dotnet build`): **0 warnings,
  0 errors** in both `Debug` and `Release`. (Desktop-only solution; the Android
  head is built separately below — deliberate divergence, §2.)
- **Android head build** (`scripts/build-android-head.sh Debug`): **0 warnings,
  0 errors**, signed debug APK produced (§2 sizes).
- **Full suite** via `scripts/run-tests.sh` (P471), 3 consecutive runs. The
  **changed area** (`Allegiance.ClientCore.MonoGame.Tests`) passed **1119/1119 in
  all three runs** (P498's 1110 + the 9 new `GamePadFlightRolesTests`; Lobby
  12/12 all runs; ClientCore 407/407 in runs 1-2). The only failures were
  pre-existing live-wire timing flakes in the **untouched** `Allegiance.Core`
  (3699 tests; 1/7/7 across runs) and one rotating ClientCore flake in run 3 —
  `git diff --name-only` confirms P499 touches none of those assemblies. All are
  recorded in `docs/TEST_FLAKES.md` (§"P499 session observations").
- **Android build-proof launch on an emulator (user-space setup succeeded).** AVD
  `p499emu` (android-35 x86_64) booted under KVM on this box. The APK installed,
  the app stayed alive, and logcat showed the FULL asset load through the
  `AndroidAssetSource` from APK assets: lock sprites, the shop catalogue (447
  hulls / 117 parts from `PCore001.igc`), the 2048×2048 rock surfaces, missile
  meshes, explosion film strips, debris + audio cues — and a rendering screenshot
  (`/home/domingo/p499_render.png`) shows the actual in-game "SELECT SIDE" panel.
  (One graceful degradation: `Vertex buffers are write-only on OpenGL ES` for the
  three imported asteroid meshes falls back to the procedural mesh — cosmetic,
  no crash.)
- **Cross-play against the existing server — proven.** With the host server
  running (`src/Allegiance.Server`, port 2326) and `adb reverse tcp:2326 tcp:2326`
  tunnelling the emulator's loopback to it, the Android head logged on
  (`[monogame] logged on: serverTime=1235`) and the server-side log shows the full
  join: `peer 2 'android' joined` → `logon validated` → auto-seated **side 0
  (Alpha)** → `JOINED_MISSION` + `PLAYERINFO` + `SET_MONEY` + cluster/warp/station
  export + `ENTER_GAME` — i.e. the phone client joined the SAME server the
  desktop client joins, on the same wire protocol.

## 4. Deliverables

1. Committed on **`master`**, **not pushed** (`git log -1` / `git branch
   --show-current` confirmed at commit time).
2. This report.
3. Signed debug APK:
   `src/Allegiance.ClientCore.Android/bin/Debug/net10.0-android/org.allegiance.client-Signed.apk` (181 MiB).
   **For Domingo to sideload onto a phone** (after editing `Assets/connect.json`
   to point at the server's reachable address, e.g. the server's LAN IP, then
   rebuilding, or keeping 127.0.0.1 + `adb reverse`):
   ```
   adb install -r src/Allegiance.ClientCore.Android/bin/Debug/net10.0-android/org.allegiance.client-Signed.apk
   ```
   The app connects on launch to whatever `connect.json` says; pair a Bluetooth
   controller and fly (right stick = yaw/pitch, left stick = roll/throttle,
   A/RT = fire, B/LT = afterburner, RB/LB = throttle steps; docked: DPAD + A/B/Y).

## 5. Notes / divergences / follow-ups

- **Typed chat disabled on Android.** MonoGame.Android's `GameWindow` has no
  `TextInput` event; the shared (DesktopGL-compiled) `Initialize` therefore
  subscribes via reflection and skips on Android (logged: "typed chat disabled").
  Quick-chat `.mdl` is likewise not loaded on Android (its loader reads
  `AppContext.BaseDirectory`). Both are graceful degradations, acceptable for a
  gamepad-first head; a touch/soft-keyboard chat is a later step.
- **`adb reverse` trick** (used for the proof): with the server on the host at
  port 2326, `adb reverse tcp:2326 tcp:2326` maps the phone's `localhost:2326` to
  the host — no rebuild needed when the APK's `connect.json` says `127.0.0.1`.
- **Gamepad signs are provisional** (same honest caveat as P380's real joystick):
  the exact pad sense is unit-tested for the *mapping convention* (9 tests) but
  not yet verified hands-on on a physical controller; `GamePadFlightRoles.Layout`
  is the one edit point to flip any axis that feels inverted.
- **Protocol/version check:** none added — desktop and Android use the identical
  `Allegiance.ClientCore.Game` wire code, so they are inherently in lockstep. If a
  version mismatch ever arises it would be reported as a follow-up (task item,
  out of scope here).
- **Follow-ups for later steps:** touch controls + UI scaling; Play Asset Delivery
  (APK exceeds the base-module limit); an in-app connect form; on-device gamepad
  sign calibration; renaming the shared `Allegiance.ClientCore.MonoGame` namespace
  now the Android head proves cross-platform.
