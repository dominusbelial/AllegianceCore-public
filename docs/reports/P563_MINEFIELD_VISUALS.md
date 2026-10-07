# P563 — Minefield visual parity: native decal scale (1 u, not 10 u) + field thinning

Status: implemented, failing-first tested, 3× suite run (documented pre-existing
live-wire flakes only), committed on `master` (not pushed).

## Goal (Domingo, live on 221, 2026-10-06)

> "i had the chance to see the minefields too, it does the job but each disk
> looks a bit big and too close to each other? they need a pass based on the
> original client."

## Root cause: the port drew each mine decal ~10× native size

The port's `MineRenderer.DecalHalfExtent = 10f` was display tuning, not a native
value. Native derives the decal's world half-extent from its decal math — which
yields **1.0 world unit**, exactly 10× smaller. With the decal count fixed by the
field radius (`GetCount`), a 10× linear size means a ~100× area per sprite, so a
radius-200 field (~400 sprites) became a wall of overlapping discs — Domingo's
"too big and too close together."

## Native derivation (authoritative, /home/domingo/Development/Allegiance)

1. **Draw call** — `MineFieldGeoImpl::Render` draws each decal with scale 1,
   angle 0, and zero forward/right vectors (`src/effect/efgeo.cpp:74-84`):
   ```cpp
   pcontext->DrawDecal(m_psurface, color, m_vposition[index],
                       Vector::GetZero(), Vector::GetZero(), 1, 0, BlendModeSourceAlpha);
   ```
2. **`m_scale`** — `Context::DrawDecal` stores `decal.m_scale = mat.GetScale() * scale`
   (`src/engine/context.cpp:2292-2296`), i.e. the local-to-eye matrix scale × 1.
   Because `forwardLocal.IsZero()`, the returned size is `sizeScale·m_scale²`.
3. **Quad span** — `Decal::GetVertices` (`src/engine/context.cpp:218-240`), with
   `m_forward` zero and `angle = 0`:
   ```cpp
   float c = cos(0)*m_scale = m_scale;
   float s = sin(0)*m_scale = 0;
   float dx = c - s = m_scale;   float dy = c + s = m_scale;
   v0=(x-dy,y+dx) v1=(x-dx,y-dy) v2=(x+dy,y-dx) v3=(x+dx,y+dy)
   ```
   The quad spans ±`m_scale` in both eye-space axes → **world half-extent =
   `m_scale`**.
4. **`GetScale()`** — `Matrix::GetScale()` is the length of the first column
   (`src/zlib/matrix.cpp:80-84`). The mine field `ThingGeo` sits at the mine's
   world position under a rigid (rotation + translation) local-to-eye transform,
   so its column lengths are 1 → `GetScale() = 1.0`.
5. ⇒ **native decal half-extent = 1.0 world unit.** The port's `10f` was a
   10× oversize.

The brightness `bound(100 / eyeDist, 0, 1)` (efgeo.cpp:66-70), the grayscale
colour (efgeo.cpp:70), the uniform-in-ball scatter
(`Vector::RandomPosition(radius)`, efgeo.cpp:33), and the count
`1 + int(strength·radius²/100)` (efgeo.cpp:21-24) were all already ported
faithfully (P291, P574). Only the per-sprite size was wrong.

## The fix

`src/Allegiance.ClientCore.Game/Rendering/MineRenderer.cs`:
`DecalHalfExtent = 10f → 1f`, with the class doc rewritten from "display tuning"
to the native derivation above (removed the 10 u display tune). A stale
comparison in `ChaffRenderer.cs`'s doc comment was corrected (comment only).

Distribution (uniform-in-ball, `MineFieldGeometry.SampleUniformInSphere`) and
depletion thinning (decal count follows the mine's live `Strength` via
`OffsetCount(strength, radius)` — native `SetStrength`, efgeo.cpp:43-51,
already ported in P574, wired from the server's `MINE_STRENGTH(853)` to
`MineEntity.Strength`) were verified already native-faithful; no change needed.

## Failing-first test

`tests/Allegiance.ClientCore.MonoGame.Tests/P563MineVisualParityTests.cs` —
`Decal_half_extent_equals_the_native_derived_world_value` asserts
`MineRenderer.DecalHalfExtent == 1.0f` (with the derivation documented in the
class comment).

- Ran on today's (pre-fix) code: **FAIL** (was 10).
- Ran after the fix: **PASS**.

Existing coverage (unchanged, still green): count-vs-strength depletion
(`P574ExplosionSoundAndMineThinningTests`, `MineFieldGeometryTests`) and the
uniform-in-ball distribution / determinism (`MineFieldGeometryTests`).

## Verification

- Whole-solution build: **0 warnings, 0 errors**.
- Full suite `scripts/run-tests.sh` (P471), 3 consecutive runs on the final code:

| Run | Lobby | ClientCore.MonoGame | ClientCore | Core |
|-----|-------|---------------------|------------|------|
| 1 | 12/12 | 1407/1407 | 545/545 | 3771/3773 |
| 2 | 12/12 | 1407/1407 | 545/545 | 3771/3773 |
| 3 | 12/12 | 1407/1407 | 544/545 | 3769/3773 |

Every failure is a documented pre-existing live-wire load-flake in
`docs/TEST_FLAKES.md`, none in mine code, rotating member-to-member across runs
(all pass in isolation):
- `Core…LiveRipcordTests.First_activation_broadcasts_…flying_in_sector` (row 36, "suspect (P472)")
- `Core…ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` (row 29; recurring load-flake)
- `Core…LiveRipcordTests.Re_target_activates_to_requester_only` (row 182, "load-flake")
- `Core…LiveChatSectorWireTests.Individual_AllSector_…relay_to_real_peers_over_the_wire` (row 222, "load-flake")
- `Core…SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` (row 361 family)
- `Core…LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` (LiveRipcordTests family, rows 36/182)
- `Core…CargoBayReloadTests.Reload_NA_terminates_the_cargo_pack` (row 332 family)
- `ClientCore…FullSessionKillIntegrationTests.…RealCrossSideKill` (row 345 family)

The P563 failing-first test passed in all three runs (inside the 1407/1407
MonoGame totals).

## Visual proof

### Geometric contact sheet (offline projection — clearly NOT a live window)
`P563_MINE_VISUALS_assets/contact_sheet_geometric.{png,svg}` — the exact native
geometry (`MineFieldGeometry` uniform-in-ball sampling, native `GetCount`
counts, native `100/dist` brightness) projected through the client's real 50°
vertical FOV (`WinTrek.h:26` default, `Game1.Lifecycle.cs`) at 2560×1440, for a
radius-75 and a radius-200 field at 100/300/800 u, **old 10 u vs new 1 u**, with
an Adv Fighter (17.14 u) scale bar. It reproduces the complaint and the fix:

- **Old 10 u**: large overlapping discs — at 100 u a solid blob; still
  overlapping at 300/800 u. (Each sprite 20 u wide.)
- **New 1 u**: small well-separated motes — clearly visible as a dotted cloud at
  100–300 u, thinning to faint specks at 800 u (brightness also falls to 0.12).

Measured pixel widths (1440p, 50° FOV; px = worldWidth/depth · 1544):

| distance | native 2 u sprite | old 20 u sprite | brightness `100/d` |
|----------|-------------------|-----------------|--------------------|
| 100 u | ~31 px | ~309 px | 1.00 |
| 300 u | ~10 px | ~103 px | 0.33 |
| 800 u | ~4 px | ~39 px | 0.12 |

### Real-window frames (P572 `live-proof.sh`, 2560×1440 backbuffer)
`P563_MINE_VISUALS_assets/realwindow_t0{52,76}_1u_Alderan.png` — genuine
presented-backbuffer frames of a live 15v15 showcase dogfight on the 221 map
(`m2xspiderw.igc`) with the **new 1 u** build: the own Adv Fighter in chase view
surrounded by asteroids and the Alderan scrum (server log confirms
`[mine] … deployed -> EXPORT(26)/OT_mine` 30×, mine type 12 = native Mine Pack,
radius 75). These give the scale context (Adv Fighter ~17 u) for judging the
corrected sprite size.

**Capture caveat, reported honestly:** I could not land a clean close-up real-
window frame of a 1 u mine cloud this session. Reasons: (a) the static layer-
drone field path did not lay — after fixing the `.TowerAC` bucket-name ambiguity
(it returned a name shared by 10 buckets instead of a unique id; P560's
prescribed fix), the invest still did **not complete in the window** (server log:
`donated 1000 … invested 1000/8000, timeMs=0` — a P560 invest/TimeToBuild bug,
**out of P563 scope**, so I reverted the bucket change to keep P563's commit
clean); (b) the dogfight mines I had as live evidence live only ~5 s
(`life 5`) and the autonomous chase camera kept leaving the drop zone between
autoshots. The sprite-size relationship is instead proven geometrically (above)
plus the definitive native math; no screenshot was fabricated.

## Visibility assessment (scope item 1) + proposed option

The native 1 u size is **not** "barely visible at combat range": at 100–300 u a
mine decal reads ~31→10 px — a small, distinguishable glowing mote in a loose
cloud, which is exactly how native presents minefields (and the intended
"subtle field" look, versus the old 10× clutter). At long range (>~800 u) it
falls to ~4 px and ~12 % brightness — intentionally faint, as native's own
`100/dist` falloff dictates.

Because the change is a 10× visual cut, I'm not silently baking in any
compensation. **Recommendation: keep native 1 u.** If, on a real live watch,
Domingo finds it too subtle, the documented follow-up option is a client-only
size/density knob or a small HUD/radar contact marker for fields beyond combat
range (native shows no such marker today — adding one would be a deliberate
divergence to approve explicitly). I've left the code at exact native parity so
that choice stays clean.

## Files changed

- `src/Allegiance.ClientCore.Game/Rendering/MineRenderer.cs` — `DecalHalfExtent`
  `10f → 1f` (+ native derivation in the class/constant doc).
- `src/Allegiance.ClientCore.Game/Rendering/ChaffRenderer.cs` — stale "10 u mote"
  doc-comment corrected (no behaviour change).
- `tests/Allegiance.ClientCore.MonoGame.Tests/P563MineVisualParityTests.cs` (new)
  — failing-first half-extent test.
- `docs/reports/P563_MINEFIELD_VISUALS.md` + `P563_MINE_VISUALS_assets/` (this
  report, geometric contact sheet, real-window frames).

Committed on `master` (`git branch --show-current` = master), **not pushed**.
`git log -1` = `6253b667` "P563: minefield visual parity — native decal scale
(half-extent 1 u), not 10 u display tune"; working tree clean of P563 files.

## Out of scope (confirmed, not touched)

Mine damage/attrition (P452), visibility (P453), minefield laying (P560) — the
latter is why no static layer-drone field could be captured this session.
