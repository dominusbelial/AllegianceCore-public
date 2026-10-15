# P499 — Android prep, step 2: Android head (APK) + gamepad play, cross-play against the existing server

## Why this task exists

P498 split the client into a shared game library and a DesktopGL head, and
added the asset-source abstraction and an asset manifest. Check that P498's
commit exists (`git log --oneline | grep P498`). If it doesn't, **stop and
report**.

Now add an **Android head**, so the same game runs on a phone and joins the
same server (cross-play). The first playable target is **Bluetooth gamepad**
input; touch controls are a later task.

**This machine has no Android toolchain.** There's no `android` dotnet
workload, no JDK, no Android SDK and no `adb`. Install everything **in
user space** (no `sudo`; there's no passwordless sudo on this box):
- `/home/domingo/.dotnet/dotnet workload install android`;
- a JDK (Microsoft OpenJDK 17 or 21 tarball under `~/`);
- the Android SDK command-line tools under `~/Android/Sdk`, with the
  platform, build-tools and platform-tools (`adb`) the workload needs.

Record exact versions and install commands in the report. If a step needs
root, **stop and report**; don't work around it.

## Scope

1. **The Android head project** (`net10.0-android`, `MonoGame.Framework.Android`
   3.8.x), referencing the P498 shared library.
   - Check that MonoGame 3.8.5 supports `net10.0-android`. If it doesn't,
     use the nearest supported target **for the Android head only**, and
     document it.
   - Entry Activity, landscape, fullscreen and keep-screen-on.
   - Server host and port come from a simple in-app connect screen (or a
     config file for now); nothing is hard-coded to LAN addresses.
2. **Assets.** Package the P498 manifest's files as Android assets, through
   an Android `IAssetSource` that reads them. Report the APK or AAB size. If
   it exceeds Play's base-module limit, note that Play Asset Delivery is the
   step-4 follow-up; for now a sideloaded APK is fine.
3. **Gamepad input.** Map MonoGame `GamePad` state onto the existing flight
   controls: sticks for yaw, pitch and roll, triggers or buttons for fire,
   afterburner and throttle steps. Reuse P380's joystick mapping patterns and
   P445's throttle model. Make dock and shop navigable with the pad, at least
   minimally.
4. **Lifecycle.** On pause or background, close the connection cleanly. On
   resume, offer reconnect. P480 keeps players seated across rotation.
5. **Build proof.** Produce a signed debug APK. With no emulator or device
   on this box, a successful build plus a launch on an emulator **if one can
   be set up in user space** is enough. If neither is possible, deliver the
   APK path and the exact `adb install` command for Domingo, and say so
   plainly.

## Explicitly out of scope

- Touch controls, UI scaling, Play Store submission, ads or IAP, accounts,
  TLS. Those are later steps (wiki log 2026-10-02).
- Server or protocol changes, other than a protocol/version check if one is
  needed to keep desktop and Android in lockstep. If so, report it as a
  follow-up rather than adding it here.

## Verification

- Every fix has a test that fails on unfixed code first, where applicable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P499_ANDROID_HEAD_GAMEPAD.md`.
