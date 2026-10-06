#!/usr/bin/env bash
#
# P547 — build the custom shaders (.fx) into MonoGame OpenGL-profile bytecode
# (.ogl.mgfxo) for DesktopGL AND Android (both run the OpenGL profile through
# MojoShader).
#
#   Shader sources : src/Allegiance.ClientCore.Game/Shaders/*.fx
#   Outputs        : src/Allegiance.ClientCore.MonoGame/Content/Shaders/*.ogl.mgfxo
#                    (shipped with the game — see that project's csproj; the
#                     runtime `new Effect(gd, bytes)` loader reads them via
#                     TitleContainer, so building AND running the game never
#                     needs Wine.)
#
# REQUIREMENTS: mgfxc (the dotnet tool) + a Wine prefix with the Windows .NET
# SDK and d3dcompiler_47. On this workstation:
#   - Wine prefix ........ ~/.winemonogame   (MonoGame's official
#                           net8_mgfxc_wine_setup.sh)
#   - env var ............. MGFXC_WINE_PATH=<prefix>  (also in ~/.profile)
#   - mgfxc ............... ~/.dotnet/tools/mgfxc
# See docs/SHADERS.md for the full toolchain (Wine 10, the setup script, the
# env var, the command, and the GL/MojoShader limits).
#
# Usage:  scripts/build-shaders.sh
# Exit:   0 on success (a line per compiled shader), non-zero on any failure.
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SRC_DIR="$REPO/src/Allegiance.ClientCore.Game/Shaders"
OUT_DIR="$REPO/src/Allegiance.ClientCore.MonoGame/Content/Shaders"
MGFXC="${MGFXC:-$HOME/.dotnet/tools/mgfxc}"

mkdir -p "$OUT_DIR"

# --- Toolchain presence / clear failure ------------------------------------
if [ ! -x "$MGFXC" ] && [ ! -f "$MGFXC" ]; then
  echo "ERROR: mgfxc not found at '$MGFXC'." >&2
  echo "Install it with:  dotnet tool install -g dotnet-mgfxc" >&2
  exit 1
fi

if [ -z "${MGFXC_WINE_PATH:-}" ]; then
  echo "ERROR: MGFXC_WINE_PATH is not set." >&2
  echo "Point it at the Wine prefix that has the Windows .NET SDK + d3dcompiler_47," >&2
  echo "e.g.   export MGFXC_WINE_PATH=\$HOME/.winemonogame" >&2
  echo "Set it up with MonoGame's official net8_mgfxc_wine_setup.sh (see docs/SHADERS.md)." >&2
  exit 1
fi

if [ ! -d "$MGFXC_WINE_PATH" ]; then
  echo "ERROR: MGFXC_WINE_PATH '$MGFXC_WINE_PATH' is not a directory." >&2
  echo "Did the Wine setup run? See docs/SHADERS.md." >&2
  exit 1
fi

# --- Compile ----------------------------------------------------------------
fails=0
for fx in "$SRC_DIR"/*.fx; do
  [ -f "$fx" ] || continue
  stem="$(basename "$fx" .fx)"
  out="$OUT_DIR/${stem}.ogl.mgfxo"
  echo "==> ${stem}.fx -> ${stem}.ogl.mgfxo"
  WINEDEBUG=-all "$MGFXC" "$fx" "$out" /Profile:OpenGL >/dev/null 2>&1
  rc=$?
  if [ "$rc" -ne 0 ] || [ ! -s "$out" ]; then
    echo "ERROR: failed to compile '$fx' (mgfxc exit $rc)." >&2
    # Retry once verbosely so the real error is visible.
    WINEDEBUG=-all "$MGFXC" "$fx" "$out" /Profile:OpenGL
    fails=$((fails + 1))
  else
    printf '   ok  (%s bytes)\n' "$(stat -c%s "$out")"
    # Record the source hash so the P547 staleness test can detect an edited
    # .fx that wasn't rebuilt (survives fresh checkouts, where git mtimes all
    # collapse to checkout time). The <stem>.fx.sha256 rides alongside the
    # .mgfxo in Content/Shaders and ships with the game.
    (
      command -v sha256sum >/dev/null 2>&1 && sha256sum "$fx" | awk '{print $1}' > "$OUT_DIR/${stem}.fx.sha256" \
        || sha256sum_command_missing=true
    )
    [ "${sha256sum_command_missing:-}" = "true" ] && { echo "WARN: sha256sum unavailable; no staleness record for ${stem}.fx" >&2; }
  fi
done

if [ "$fails" -gt 0 ]; then
  echo "ERROR: $fails shader(s) failed to compile." >&2
  exit 1
fi

echo "Done. ${OUT_DIR}"
exit 0
