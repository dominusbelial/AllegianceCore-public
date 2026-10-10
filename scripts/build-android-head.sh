#!/usr/bin/env bash
#
# P499 — build the Android head to a signed debug APK.
#
# Requires the user-space Android toolchain (see docs/reports/
# P499_ANDROID_HEAD_GAMEPAD.md §toolchain): the Android dotnet workload, a JDK
# and the Android SDK, plus the Java/SDK env in ~/.android_env.sh. The head is
# deliberately NOT in AllegianceCore.slnx (so the DesktopGL solution build and
# the P471 test runner never need the Android toolchain); build it here.
#
# Steps: stage the assets (scripts/pack-android-assets.sh) then build the
# Android head project. The APK lands in
#   src/Allegiance.ClientCore.Android/bin/Debug/net10.0-android/
#   org.allegiance.client-Signed.apk
#
# Usage:  scripts/build-android-head.sh [Debug|Release]
# Exit:   0 on success (APK path printed).

set -euo pipefail

CFG="${1:-Debug}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"

# User-space Android env (JAVA_HOME, ANDROID_HOME, PATH for adb). Skip silently
# if it's not present so the script fails fast with a clear dotnet message.
if [ -f "$HOME/.android_env.sh" ]; then
  # shellcheck disable=SC1091
  source "$HOME/.android_env.sh"
fi

cd "$REPO"

echo "==> staging Android assets (artwork from P498 manifest + compiled Content)"
bash scripts/pack-android-assets.sh

echo "==> building Android head ($CFG)"
"${DOTNET:-dotnet}" build src/Allegiance.ClientCore.Android/Allegiance.ClientCore.Android.csproj -c "$CFG"

APK="$REPO/src/Allegiance.ClientCore.Android/bin/$CFG/net10.0-android/org.allegiance.client-Signed.apk"
if [ -f "$APK" ]; then
  SIZE=$(du -h "$APK" | cut -f1)
  echo
  echo "Signed debug APK: $APK ($SIZE)"
else
  echo "ERROR: no signed APK produced (look for the build error above)." >&2
  exit 1
fi
