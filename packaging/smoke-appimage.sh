#!/bin/sh
# Run inside a --network none container (loopback stays available to skirmish).
set -eu
image=$(realpath "$1")
work=$(mktemp -d)
trap 'chmod -R u+w "$work"; rm -rf "$work"' EXIT HUP INT TERM
export HOME="$work/home" XDG_CONFIG_HOME="$work/home/.config"
mkdir -p "$XDG_CONFIG_HOME"
unset ALLEGIANCE_ARTWORK_PATH ALLEGIANCE_SKY_PACK_PATH
cd "$work"
# Exercise the runtime directly without requiring host FUSE.
APPIMAGE_EXTRACT_AND_RUN=1 "$image" --check-assets
# Exercise the explicitly extracted layout too, from outside the AppDir.
"$image" --appimage-extract > extract.log
payload="$work/squashfs-root/usr/lib/AllegianceCore"
(cd "$payload" && sha256sum --check bundle-files.sha256 > "$work/inventory.log")
# AppRun must work from a read-only installation, with all writes in user dirs.
chmod -R a-w "$work/squashfs-root"
"$work/squashfs-root/AppRun" --check-assets
APPIMAGE_EXTRACT_AND_RUN=1 "$image" --skirmish-smoke 60
if [ "${2:-}" = '--intro' ]; then
    # A software-rendered intro frame must complete and exit without internet.
    mkdir -p "$XDG_CONFIG_HOME/AllegianceCore"
    printf '%s\n' '{"Lobby":"http://192.0.2.1:5000","ShowStartupTrainingSuggestion":false}' \
        > "$XDG_CONFIG_HOME/AllegianceCore/player.json"
    ALLEGIANCE_AUTOSHOT=1500 LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
        timeout 60 xvfb-run -a "$work/squashfs-root/AppRun"
    test -s "$XDG_CONFIG_HOME/AllegianceCore/screenshots/intro_proof.png"
fi
chmod -R u+w "$work/squashfs-root"
echo 'AppImage runtime, extracted payload, checksums and offline skirmish passed.'
