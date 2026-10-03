# P503 — Ambient space dust renders as "bubbles": match native DebrisGeo's decal look

Stage: P503. Landed on `master` (not pushed). Top commit: `git log -1` after landing.
A gameplay-parity fix for the P449a ambient-dust field: Domingo playtested live
2026-10-02 and the space dust "looks like bubbles". Root cause traced, measured,
and fixed to native `DebrisGeo`/`DrawDecal` semantics.

## 1. Symptom and hypothesis triage

The P449a field (landed `1c50f116`) draws each dust grain as a camera-facing soft
quad, sized in `Game1.Rendering.cs` by an invented

    half = (0.1/focus) · distance · Calibration      (Calibration = 0.13)

Because the world half-extent grows linearly with the grain's distance, the
perspective divide cancels and **every grain subtends a constant ~1.3% of the
screen height regardless of how far it is** — an ~8 px soft glowing disc on a
640 px frame. Thirty-plus such discs drifting past read as floating "bubbles",
compounded by additive brightening where they overlap.

The brief suggested three candidate culprits: the decal texture, the `Calibration`
size, and the blend mode. Evidence below eliminates the texture and blend, and
confirms the size.

## 2. What native actually does (all file:line cited)

### 2.1 The decal texture is a soft disc, not a ring
`debris1bmp.mdl` (`/home/domingo/Development/Artwork/debris1bmp.mdl`) decoded via
the port's `MdlTextureDecoder` recipe (`docs/reports/P503_debris1bmp_raw.png`) is a
**32×32 RGB565 soft radial disc**: radial luminance 244 at centre → ~175 at
r=0.4 → ~106 at r=0.7 → ~15 at r=0.9 → ~0 at the rim. After the port's
near-black key (`ToPremultipliedRgba`, lum≤40 → alpha 0) only the outer ~10% rim
becomes transparent; the remainder is a soft disc. It is NOT a hollow ring, so
the authored texture is not the "bubble" source. (Native `LoadSurface("debris1bmp",
true)` at debrisgeo.cpp:51.)

### 2.2 The decal SIZE is a constant world half-extent, not distance-scaled
- `DebrisGeo::Render` calls `DrawDecal(…, scale = 0.1f/focus, …)` for every grain
  (debrisgeo.cpp:166) — the same `scale` value for a grain at 20 m and one at 100 m.
- `ContextImpl::DrawDecal` stores it as the quad's eye-space half-extent
  `decal.m_scale = scaleMat · scale` with `scaleMat = GetLocalToEyeMatrix().GetScale()
  = 1` (context.cpp:2317).
- `Decal::GetVertices` (context.cpp:211-224) builds an eye-space, camera-facing,
  axis-aligned quad spanning ±`m_scale` in screen-right / screen-up at the grain's
  eye-space depth. So the world half-extent is **constant 0.1/focus** for all grains.

Projection (camera.cpp:45-101, `TransformEyeToImage` :174): a world half-extent `h`
at depth `d` maps to `h·focus/d` image units across a view normalised to ±1 tall
(`focus = cot(FOV/2)`, so `focus·tan(FOV/2) = 1`). Hence a grain subtends

    screen-height fraction = h·focus/d = (0.1/focus)·focus/d = 0.1/d

— **independent of camera FOV** (the point of baking focus in) and **DECREASING with
distance**: 0.5% of the screen height at 20 m down to 0.1% at 100 m. Native's field
is a spread of tiny specks that shrink with depth, exactly the subtle "sense of
speed" look.

### 2.3 The blend mode was already faithful
`DrawDecal`'s default `BlendModeAdd` (context.h:929) maps to
`SRCBLEND=D3DBLEND_SRCALPHA, DESTBLEND=D3DBLEND_ONE` (RasterD3D9.cpp:295-302) —
plain additive, src-alpha modulated. The port's `AmbientDustRenderer._additiveSoft`
(`SourceAlpha` src / `One` dest, Add) matches exactly. Native's RGB565 texture has no
alpha (alpha=1), so the visual is pure `src + dest` of the tinted disc — the same as
the port. **Blend is not the bug.**

## 3. The fix

- **`AmbientDustRenderer.cs`**: removed the invented `Calibration = 0.13f`;
  added native-derived pure helpers `DecalHalfExtent(focus) => 0.1/focus` and
  `ScreenHeightFraction(focus, depth) => DecalHalfExtent(focus)·focus/depth
  (= 0.1/depth)`, both documented with file:line provenance.
- **`Game1.Rendering.cs`**: the per-grain half-extent is now the constant
  `DecalHalfExtent(debrisFocus)` — no `·distance`, no `Calibration`. The `dist`
  variable became unused and was removed (the 20 m near-skip still uses `dsq`).
- **Harness** `.verify/p449a/harness/Program.cs` updated to the same formula so
  the before/after proof reflects the fix. `.verify/` is gitignored (never committed).

The new API is structurally distance-free at the call site — the old
distance-scaled bug is no longer expressible in `Game1.Rendering.cs`.

## 4. Before / after captures (real server → real wire → real DebrisGeo step →
real AmbientDustRenderer offscreen)

P449a harness (real in-process server, real `ConnectionSession`, real field + real
renderer), 960×640, three phases. Frames and logs under `.verify/p503/{before,after}/`.

| Metric (slow / fast moving frames) | BEFORE (P449a) | AFTER (P503) |
|---|---|---|
| dust lit-pixels (sum over frame) | 2302 / 2603 | 67 / 51 |
| grains drawn | 40 / 46 | 31 / 36 |
| median blob area (px) | 56 / 52 | 1 / 1 |
| grains >16 px (disc-like) | 39 / 46 | **0 / 0** |
| grains ≤4 px (specks) | 0 / 0 | 28 / 35 |
| avg hue (R,G,B) | (78,110,107) / (82,119,116) | (72,104,102) / (85,116,116) |

The before frames are ~40-50 discs of median ~52-56 px (≈8 px diameter ≈ 1.3% of
the screen height) — the "bubbles". The after frames are the same bluish-white
grains as 1-2 px specks (0.1-0.5% of the screen height), matching native's
`0.1/depth` curve. Visuals:
`docs/reports/P503_montage_slow_zoomed.png` (left = before discs, right = after
specks) — inspected: left shows soft glowing bokeh discs, right shows tiny point
specks. Both hues remain bluish-white (red is the low channel, native
r∈[0.5,0.75] vs g,b∈[0.75,1.0]).

## 5. Tests (failing-first)

`tests/Allegiance.ClientCore.MonoGame.Tests/AmbientDustSizeTests.cs` — 6 pure,
graphics-free tests on the new helpers:
- `DecalHalfExtent_is_natives_0_1_over_focus` — 0.1/focus (debrisgeo.cpp:166 /
  context.cpp:2317).
- `DecalHalfExtent_is_a_pure_function_of_focus` — the same half-extent regardless
  of a grain's distance (the core bug: P449a made it distance-dependent).
- `ScreenHeightFraction_equals_native_0_1_over_depth` — 0.1/depth for several depths.
- `ScreenHeightFraction_is_FOV_independent` — 75° and 50° cameras agree.
- `ScreenHeightFraction_decreases_with_distance` — a 4× depth → ¼ fraction.
- `Former_P449a_formula_gave_a_bubble_const_at_all_distances` — reconstructs the old
  `(0.1/focus)·dist·0.13` formula and proves it is distance-invariant (the bug) and
  disagrees with native at every depth (the regression guard).

Isolation: `dotnet test --filter "AmbientDustSizeTests|DebrisGeoTests"` → **17/17 pass**
(6 new + 11 existing DebrisGeo).

MUST-FAIL proof (change removed ⇒ red): temporarily making
`ScreenHeightFraction` distance-invariant (`0.1·0.13` constant) fails
`ScreenHeightFraction_decreases_with_distance` and
`ScreenHeightFraction_equals_native_0_1_over_depth` (observed 2 FAIL / 4 PASS);
reverted green immediately after the red observation.

## 6. Whole-solution build

`/home/domingo/.dotnet/dotnet build` → **Build succeeded, 0 Warning(s), 0 Error(s)**.

## 7. Full test suite — 4 consecutive runs

`scripts/run-tests.sh` (P471 sequential runner, 4 assemblies), 4 consecutive runs.
(The 6 new + 11 existing mono tests above are in `Allegiance.ClientCore.MonoGame.Tests`,
which this stage modifies; the stage touches no server/wire/ClientCore/Core logic.)

| Run | Lobby | ClientCore.MonoGame | ClientCore | Core | Result |
|---|---|---|---|---|---|
| 1 | 12/12 | 1125/1125 | 409/409 | 3699/3699 | **ALL PASSED** (exit 0) |
| 2 | 12/12 | 1125/1125 | 409/409 | 3697/3699 | 2 live flakes |
| 3 | 12/12 | 1125/1125 | 406/409 | 3698/3699 | 4 live flakes |
| 4 | 12/12 | 1125/1125 | 408/409 | 3696/3699 | 4 live flakes |

Run 1 is fully clean. Runs 2-4 were each interrupted by 2-4 **pre-existing live-wire
timing flakes** whose membership rotates per run (CrossSideCombatVisibilityLiveTests,
FullSessionKillIntegrationTests, ShipDeleteGoneLiveTests, LiveRipcordTests,
GameOverPlayersWireTests, ConquestTerritoryLiveTests, AutoReloadOnEmptyTests,
WarpBombTests — a different subset each run, the documented "rotating live flake"
pattern, cf. TEST_FLAKES.md and the P322/P361 suite notes). Every one passes green in
isolation (verified: the run-4 Core set 5/5, the run-3 ClientCore CrossSide/ShipDeleteGone
pair, and LiveRipcord/GameOverPlayers; the latter two are intermittent even in isolation,
1 FAIL / 2 PASS, i.e. environmental). **None can be a P503 regression**: P503 changes
only GPU rendering in `Allegiance.ClientCore.Game` (MonoGame), and neither
`Allegiance.ClientCore.Tests` nor `Allegiance.Core.Tests` references that project —
`ClientCore.Tests` → ClientCore/Core/Server/MCP, `Core.Tests` → Core/Server/Bot/Lobby —
so their binaries are bit-identical with or without this change (verified by
project-reference inspection), and a pristine A/B of the worst offender
(`GameOverPlayersWireTests.Conquest_win_…`) passes in isolation on the pristine tree.

## 8. Scope / exclusions (unchanged)

- Other particle effects (damage debris, engine glows, explosions) and the starfield
  are untouched.
- Camera FOV (50°, P449b) is out of scope; the fix is FOV-independent by construction.
- The 20 m near-camera skip, the `1−0.25t²` fade, the 500 m cull, the forward-cone
  placement and the additive blend are all unchanged and already native-faithful.

## 9. Notes on the brief's premises (verified, not assumed)

- "the on-screen size comes from an invented `Calibration = 0.13`" — TRUE, and it is
  the root cause; removed.
- "additive soft-alpha blend" is a P449a guess — **verified FAITHFUL**: native
  `BlendModeAdd` == `SRCALPHA`/`ONE` (RasterD3D9.cpp:295-302); left unchanged.
- "generated 32px fallback sprite" — the real `debris1bmp.mdl` IS loaded (Domingo's
  log: `32x32 'debris1bmp.mdl'`); the decoded asset is a soft disc, not a ring, and
  is not the cause. The fallback only triggers if the asset is missing.
- "native does NOT stretch particles with speed" — TRUE; the only density variation
  is native's `distancePerParticle − (0.3−0.004·m_speed)` speed adjustment, already
  ported faithfully in `DebrisGeo.Step`.
- Native `DrawDecal` scale semantics WERE established from source (section 2.2), so
  no size assumption was needed — the size is `0.1/focus` world half-extent exactly.
