#!/usr/bin/env bash
# CI-1: run the release workflow's "Existing CI test gate" locally.
#
#   tools/run-ci-gate.sh [--clean-clone] [--no-build] [extra dotnet test args...]
#
# Runs EXACTLY the "Existing CI test gate" command of .github/workflows/release.yml
# (the --filter is read from that file), e.g.
#   dotnet test AllegianceCore.slnx --no-build \
#     --filter "Requires!=Artwork&Requires!=NativeSource&Lane!=LiveTiming"
#
# --clean-clone reproduces a clean GitHub ubuntu runner on this workstation:
#   * no artwork corpus / native source / sky / Meshy / HD pack: every
#     ALLEGIANCE_*_PATH is pointed at a nonexistent directory (the env var wins
#     over local.env, see src/Allegiance.Core/ExternalPaths.cs), so a local.env
#     in the checkout does not leak in;
#   * no display: DISPLAY / WAYLAND_DISPLAY unset;
#   * no GPU: __EGL_VENDOR_LIBRARY_FILENAMES points at a nonexistent vendor file
#     so libglvnd finds no EGL driver and MonoGame's offscreen SDL context throws
#     NoSuitableGraphicsDeviceException, exactly as on the GPU-less runner.
# Tests that need any of these must SKIP (Require.Artwork() / Require.GraphicsDevice()),
# never fail. Expect "Failed: 0" in every project. See docs/CI_TEST_GATES.md.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
clean=0; build=1; args=()
for a in "$@"; do
  case "$a" in
    --clean-clone) clean=1 ;;
    --no-build) build=0 ;;
    *) args+=("$a") ;;
  esac
done
if [[ $clean == 1 ]]; then
  missing=/nonexistent/allegiance-ci-clean-clone
  export ALLEGIANCE_ARTWORK_PATH=$missing ALLEGIANCE_NATIVE_SRC_PATH=$missing \
         ALLEGIANCE_SKY_PACK_PATH=$missing ALLEGIANCE_MESHY_PACK_PATH=$missing \
         ALLEGIANCE_HD_PACK_PATH=$missing
  export __EGL_VENDOR_LIBRARY_FILENAMES=$missing/egl_vendor.json
  unset DISPLAY WAYLAND_DISPLAY ALLEGIANCE_REQUIRE_GPU
elif [[ -f local.env ]]; then
  set -a
  # shellcheck disable=SC1091
  source ./local.env
  set +a
fi
if [[ $build == 1 ]]; then
  "$DOTNET" build AllegianceCore.slnx -m:1 -p:AutoRestoreMGCBTool=false
fi
# Take the filter from the workflow itself so this script cannot drift from it.
filter=$(sed -n 's/.*dotnet test AllegianceCore.slnx --no-build --filter "\([^"]*\)".*/\1/p' \
  .github/workflows/release.yml | head -n1)
[[ -n $filter ]] || { echo "run-ci-gate: test-gate filter not found in release.yml" >&2; exit 2; }
echo "run-ci-gate: clean-clone=$clean filter=$filter" >&2
exec "$DOTNET" test AllegianceCore.slnx --no-build --filter "$filter" ${args[@]+"${args[@]}"}
