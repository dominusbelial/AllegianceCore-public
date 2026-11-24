# P1083 — pinned KNI shader spike tool

Only KNI content-pipeline NuGet packages at **4.3.9001** are referenced here.
`Builder` supplies the MojoProcessor, CppNet, SharpDX, SharpFont and Windows
MojoShader DLLs; the standalone MojoProcessor NuGet package is not published.
No global tools or system installs are needed for this project.

From the repository root:

```sh
NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1083/nuget-http" \
  "$HOME/.dotnet/dotnet" restore tools/p1083-kni-shaders \
  --packages "$PWD/.scratch/p1083/packages" --disable-build-servers
NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1083/nuget-http" \
  "$HOME/.dotnet/dotnet" build tools/p1083-kni-shaders --no-restore --disable-build-servers
"$HOME/.dotnet/dotnet" tools/p1083-kni-shaders/bin/Debug/net10.0/p1083-kni-shaders.dll \
  src/Allegiance.ClientCore.Game/Shaders .scratch/p1083/compile
```

The matrix command attempts **all nine unchanged inputs**, with `OPENGL=1`
passed through `EffectProcessor.Defines`, `TargetPlatform.BlazorGL`, HiDef,
optimized effects and uncompressed XNB. It writes `matrix.json`, returns 1 if
any compile fails, and removes stale per-shader outputs before each attempt.
Each successful XNB also gets a source/output hash receipt. To qualify a true
KNI compile, copy both `Volumetric.xnb` and `Volumetric.xnb.json` from the
Windows output into `src/Allegiance.ClientCore.Game.Browser/Content/Shaders/`,
then run the normal publisher into a **new** generation directory and rerun
the browser harness. The pack builder intentionally rejects conflicting
metadata in an existing generation.
Linux has no native path in this release: SharpDX directly loads
`d3dcompiler_47.dll`, then MojoShader loads `libmojoshader_64.dll` (Windows PE).
KNI does **not** implement MonoGame's `MGFXC_WINE_PATH` subprocess bridge.
Running native `dotnet` with that variable would not help.

On the workstation, run the **whole tool under Windows .NET in the existing
Wine prefix** from `docs/SHADERS.md`. Its existing SDK is .NET 8, so compile
this small runner for net8.0 (the pinned KNI libraries support it). No SDK,
Wine, DLL, package or global-tool install is requested:

```sh
# Prefix and Wine executable must already exist; see docs/SHADERS.md.
# Run from this repository. Use Wine's Z: mapping for the same unchanged files.
export WINEPREFIX="$HOME/.winemonogame"
shader_repo_win="$(winepath -w "$PWD")"
shader_cache_win="$(winepath -w "$PWD/.scratch/p1083/nuget-http-windows")"
WINEDEBUG=-all NUGET_HTTP_CACHE_PATH="$shader_cache_win" \
  wine64 dotnet build "$shader_repo_win\\tools\\p1083-kni-shaders\\p1083-kni-shaders.csproj" \
  -p:TargetFramework=net8.0 --disable-build-servers
WINEDEBUG=-all NUGET_HTTP_CACHE_PATH="$shader_cache_win" \
  wine64 dotnet "$shader_repo_win\\tools\\p1083-kni-shaders\\bin\\Debug\\net8.0\\p1083-kni-shaders.dll" \
  "$shader_repo_win\\src\\Allegiance.ClientCore.Game\\Shaders" \
  "$shader_repo_win\\.scratch\\p1083\\compile-windows"
```

This Windows/Wine command is prepared, **not tested in the Linux lane**. Keep
its output separate from the measured Linux matrix. A true KNI compile result
can replace the spike bridge after matching its render proof.

## Legacy runtime bridge

KNI 4.3.9001 has an explicit MGFX10 reader and WebGL GLSL ES300 translator.
MonoGame 3.8.5.1's MGFX11 adds source/entrypoint debug strings to each shader.
The narrow `LegacyVolumetric` adapter removes those strings, recomputes the
container cache key and uses KNI's ContentCompiler/CompiledEffectWriter to wrap
it in an XNB. **No GLSL, HLSL, uniforms or desktop file is edited.** The source
must match the desktop source-hash receipt. Tests compare every shader-code
hash before/after and verify that the shipped XNB embeds the exact result.

```sh
"$HOME/.dotnet/dotnet" tools/p1083-kni-shaders/bin/Debug/net10.0/p1083-kni-shaders.dll \
  --legacy-volumetric src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx \
  src/Allegiance.ClientCore.MonoGame/Content/Shaders/Volumetric.ogl.mgfxo \
  src/Allegiance.ClientCore.Game.Browser/Content/Shaders/Volumetric.xnb
```

This is container adaptation, **not successful compilation of the nine `.fx`
files**, and it is intentionally only used for Volumetric in this spike.
The normal publisher adds this exact filename to a separate verified,
on-demand `shader-volumetric` pack. A local generation can be updated with:

```sh
python3 tools/p1083-shader-pack.py --root .scratch/p1083/generation
```

The browser requests it only with `?dev=1&volumetric=1`. Missing or unusable
content falls back to P1079 native explosions. Default play never requests it.
