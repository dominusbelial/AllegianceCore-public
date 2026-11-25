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

The matrix command attempts **all nine inputs**, with `OPENGL=1`
passed through `EffectProcessor.Defines`, `TargetPlatform.BlazorGL`, HiDef,
optimized effects and uncompressed XNB. It writes `matrix.json`, returns 1 if
any compile fails, and removes stale per-shader outputs before each attempt.
Each successful XNB also gets a source/output hash receipt. P1083B's nine
compiled effects and receipts are in the browser Content/Shaders directory.
Only Volumetric is currently packed and requested by the existing opt-in spike;
the other effects are not wired into the browser renderer. Publish into a new
generation directory: the pack builder rejects conflicting existing metadata.
Linux has no native path in this release: SharpDX directly loads
`d3dcompiler_47.dll`, then MojoShader loads `libmojoshader_64.dll` (Windows PE).
KNI does **not** implement MonoGame's `MGFXC_WINE_PATH` subprocess bridge.
Running native `dotnet` with that variable would not help.

Build the runner with Linux .NET, then execute the built assembly with Windows
.NET under an existing Wine prefix. Set `WINEPREFIX` to a disposable prefix
that already contains the required runtime and DLLs. No installs are needed:

```sh
rm -rf tools/p1083-kni-shaders/obj tools/p1083-kni-shaders/bin
NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1083/nuget-http" dotnet restore tools/p1083-kni-shaders \
  -p:TargetFramework=net8.0 --packages .scratch/p1083/packages
dotnet build tools/p1083-kni-shaders -p:TargetFramework=net8.0 --no-restore --disable-build-servers \
  -o .scratch/p1083/runner-net8
export WINEPREFIX="$PWD/.scratch/wineprefix"
r="$(winepath -w "$PWD")"
WINEDEBUG=-all wine64 dotnet "$r\\.scratch\\p1083\\runner-net8\\p1083-kni-shaders.dll" \
  "$r\\src\\Allegiance.ClientCore.Game\\Shaders" "$r\\.scratch\\p1083\\compile-windows"
```

Running `wine64 dotnet build` itself crashes in the .NET CLI on the measured
workstation. See `docs/reports/BROWSER_SHADERS_KNI_P1083B.md` for the matrix
and current qualification status.

## Legacy runtime bridge

KNI 4.3.9001 has an explicit MGFX10 reader and WebGL GLSL ES300 translator.
MonoGame 3.8.5.1's MGFX11 adds source/entrypoint debug strings to each shader.
The optional `LegacyVolumetric` adapter removes those strings, recomputes the
container cache key and uses KNI's ContentCompiler/CompiledEffectWriter to wrap
it in an XNB. **No GLSL, HLSL, uniforms or desktop file is edited.** The source
must match the desktop source-hash receipt. Tests compare every shader-code
hash before/after and verify that the shipped XNB embeds the exact result.

```sh
"$HOME/.dotnet/dotnet" tools/p1083-kni-shaders/bin/Debug/net10.0/p1083-kni-shaders.dll \
  --legacy-volumetric src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx \
  src/Allegiance.ClientCore.MonoGame/Content/Shaders/Volumetric.ogl.mgfxo \
  .scratch/p1083/legacy/Volumetric.xnb
```

This is container adaptation, **not successful compilation of the nine `.fx`
files**, and it is intentionally only used for Volumetric in this spike.
The normal publisher adds the compiled Volumetric filename to a separate verified,
on-demand `shader-volumetric` pack. A local generation can be updated with:

```sh
python3 tools/p1083-shader-pack.py --root .scratch/p1083/generation
```

The browser requests it only with `?dev=1&volumetric=1`. Missing or unusable
content falls back to P1079 native explosions. Default play never requests it.
