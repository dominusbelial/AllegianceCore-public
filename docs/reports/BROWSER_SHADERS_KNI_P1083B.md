# Browser shaders under KNI — P1083B

All nine effects compile with the pinned KNI 4.3.9001 BlazorGL/HiDef pipeline
under Wine. Same-path desktop builds are deterministic and byte-identical
before and after the GLES-only edits. The nine XNBs and hash receipts are in
`src/Allegiance.ClientCore.Game.Browser/Content/Shaders/`; the existing
opt-in volumetric pack now contains the KNI-compiled effect. The other eight
are not wired into the browser. Compilation does not establish rendering
correctness or GPU cost; hardware Chromium qualification remains outside scope.

## Brief items

| Item | Status | Evidence |
|---|---|---|
| 1. Wine recipe | Done | Runner README, `docs/SHADERS.md`, P1083A report; Linux build and Windows runtime execution. |
| 2. Compiler investigation | Done | Profile mappings and isolated experiments below. |
| 3. Nine effects and desktop identity | Done | 9/9 matrix, fixed-path byte comparisons, pack and capability checks below. |
| 4. Desktop-equivalence test | Done | `tools/p1083b-desktop-preprocess-tests.py`, three isolated runs; fixed-path binary proof below. |
| 5. Report | Done | Findings and gate results below. |
| Full suite gate | Not done | Fast failed on non-task tests and timing; Live was interrupted at the four-hour brief limit and Perf did not run. |

## Matrix and isolated failures

| Shader | Before | After, XNB bytes |
|---|---|---:|
| Bloom | Pass | 5,384 |
| DisplacedSphere | X5615, 469 vertex instruction slots | 18,373 |
| FsrEasu | Pass | 14,015 |
| FsrRcas | Pass | 10,874 |
| LinearDepth | Pass | 1,942 |
| NativePbr | Pass | 52,767 |
| SoftParticle | X4574, POSITION0/VPOS0 collision | 2,611 |
| Tonemap | Pass | 4,469 |
| Volumetric | X3511, loop unrolling | 109,805 |

KNI maps BlazorGL to WebGL and then GLES
([`MojoEffectProcessor.cs:72-83,197-215`](https://github.com/kniEngine/kni/blob/v4.3.9001/src/Xna.Framework.Content.Pipeline.Graphics.MojoProcessor/Processors/MojoEffectProcessor.cs#L72-L83)),
which supplies `__GLES__` ([`ShaderProfileGL.cs:31-45`](https://github.com/kniEngine/kni/blob/v4.3.9001/src/Xna.Framework.Content.Pipeline.Graphics.MojoProcessor/EffectCompiler/ShaderProfileGL.cs#L31-L45)).
The runner supplies `OPENGL=1`, selecting SM3 in the original sources. KNI
first compiles for DX11 reflection, mapping SM3 to level 9.3, then compiles
SM3 DX9 for MojoShader ([`ShaderProfileGL.cs:84-108`](https://github.com/kniEngine/kni/blob/v4.3.9001/src/Xna.Framework.Content.Pipeline.Graphics.MojoProcessor/EffectCompiler/ShaderProfileGL.cs#L84-L108));
its compiler enables backward compatibility and optimization level 3
([`ShaderProfile.cs:53-86`](https://github.com/kniEngine/kni/blob/v4.3.9001/src/Xna.Framework.Content.Pipeline.Graphics.MojoProcessor/EffectCompiler/ShaderProfile.cs#L53-L86)).
KNI accepts SM4 and maps it back to SM3 for DX9; this raises the reflection
profile without changing the translated target.

- **DisplacedSphere:** original VS3 fails in reflection with the compiler's
  vs_2_x 256-slot limit; a two-octave scratch variant still reports 269 slots.
  Selecting VS4 for GLES alone passes both stages with all four octaves. This
  isolates the level-9 reflection restriction; the compiler supplies the vs_2_x
  diagnostic, not a literal VS2 profile chosen by KNI.
- **SoftParticle:** removing POSITION from the pixel input alone changes X4574
  to X4576 (the reflected system position requires four components). Changing
  VPOS to float4 then exposes X4502 (level-9 position read restriction).
  Selecting PS4 as well compiles. The GLES pixel input has only the interpolants
  it reads, with VPOS.xy for depth sampling; the vertex output remains intact.
- **Volumetric:** its nested early-exit march fails X3511 under level-9
  reflection. Merely adding `[loop]` to the original profile still fails;
  PS4 plus explicit loop attributes advances to the same POSITION/VPOS collision.
  A separate position-free pixel input and float4 VPOS then compile all five
  techniques. KNI compiles reflection first, so these diagnostics precede
  DX9 translation. D3DCompiler owns the internal unrolling decision; KNI source
  does not explain its internal heuristic. Warnings X3557 note that some
  specialized outer loops execute once; compilation succeeds.

## Desktop equivalence

`scripts/build-shaders.sh` now accepts `SHADER_OUT_DIR` for scratch builds.
With the same unchanged source paths and copied `MGFXC_WINE_PATH`, two builds
were byte-identical for all nine files. After the GLES-only source edits, a
third build at the same source paths was byte-identical to the first for all
nine `.ogl.mgfxo` files. The tracked `.ogl.mgfxo` files were not changed.
The `.fx.sha256` receipts were updated for the edited source bytes. This
supersedes the first-pass comparison against path-sensitive historical binaries.
The independent preprocessing test hashes the original desktop-preprocessed
text for each of the three changed shaders and passed three isolated runs.
The compiler receipt's misleading `sourceChanged=false` field was removed;
existing scratch-run receipts were normalized accordingly.

## Verification

- Solution build: 0 warnings, 0 errors. KNI matrix: 9/9.
- Desktop comparison: 9/9 deterministic baseline, 9/9 before/after identical.
- Desktop preprocessing: 1 test passed in each of three isolated runs.
- P1083 browser shader tests: 5 passed in each of three isolated runs; the
  legacy converter still checks every shader byte, while the shipped-XNB check
  now expects the actual KNI container. Shader pack: 3 passed, capability:
  2 cases passed; pack builder succeeded against a fresh generation.
- Glossary link check and `git diff --check`: passed.
- `tools/build-tools.sh`: all 155 projects passed.
- One full suite is running with artwork: Fast failed (16,117 results) on five
  non-task failures and two slow cases. The profile editor failed three times
  alone due to the read-only configuration and passed three times with scratch
  `XDG_CONFIG_HOME`. The allocation, construction, missile and wingman failures
  passed three times alone. The slow strategy theory passed 10/10 three times;
  the wingman test passed three times alone. All are classified in
  `docs/TEST_FLAKES.md`. Live had completed 3 lobby cases (3 skipped)
  and 53 MonoGame cases, all passing, when the four-hour brief limit was
  reached. The single full-suite run was stopped at that limit; remaining
  Live cases and the Perf tier did not run. This is not a full-suite pass.
- The suite started before the final artifacts and test assertion were installed;
  the changed P1083 tests were separately rerun three times as above.

## Native intent / Modern approach

### Native intent

These shaders are port-only visual work behind `visuals.*` and quality tiers;
there is no native game rule to port.

### Modern approach

Use KNI's WebGL content pipeline with GLES-only source branches while keeping
desktop preprocessing and output intact. Further browser renderer integration
and hardware-GPU validation remain outside this brief.
