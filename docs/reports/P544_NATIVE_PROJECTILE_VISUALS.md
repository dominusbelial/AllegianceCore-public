# P544 — Render projectiles the native way: per-type textured, spinning, camera-facing sprites (and 3D models)

Status: landed on `master` (not pushed)
Date: 2026-10-05

## Goal (Domingo, 2026-10-04)

> "look on the original client, all weapons had different effect, power vs
> projectile had different render effect, right now all is projectiles, we are
> missing power, and nanite looks like donuts look for it"

The port drew every projectile as one shared tinted box. This task renders each
round the native way — per-type textured sprites / directional decals / 3D
models — from the real `DataProjectileTypeIGC`, so weapons are visually distinct
and the anti-base "power" rounds are huge.

## Native citation

`~/Development/Allegiance/src/Igc/projectileIGC.cpp:50-80` — `CprojectileIGC::Initialize`
takes each projectile's look from its `DataProjectileTypeIGC`:

```cpp
//Load the model for the projectile
if (iswalpha(dataProjectileType->modelName[0])) {
    HRESULT hr = Load(0, dataProjectileType->modelName, dataProjectileType->textureName, NULL, ...);
    SetRadius(dataProjectileType->radius);
} else {
    HRESULT hr = LoadDecal(dataProjectileType->textureName, NULL, Color(...),
                           dataProjectileType->bDirectional, dataProjectileType->width, ...);
    GetHitTest()->SetRadius(0.0f);
    GetThingSite()->SetRadius(dataProjectileType->radius);
}
...
{
    Rotation r(dataProjectile->forward, dataProjectileType->rotation);
    SetRotation(r);                       // :76-80 — the spin, rad/s about forward
}
```

So native keys three things off the data:
- **a real `modelName`** (first char a letter) → load a 3D model, radius = thing radius;
- **`bDirectional`** → a decal stretched along motion with `width`;
- otherwise → a **camera-facing (billboard) decal**, thing-site radius = `radius`, plus a **spin `rotation`** rad/s about the travel axis.

All decals are additive (the glowing pixels add over their black surround) — the
reason native renders them as additive decals. Colour: the decal path forwards
`color` to `LoadCVH`/`LoadDecal`, but every PCore001 projectile type measures
(0,0,0,0) — the visible look comes from the texture, and native builds the bolt
from the texture (this matches the P465b finding).

## What the port had

`Game1.Rendering.cs` drew every projectile through one `ProjectileRenderer` (a
white unit box stretched along velocity, tinted by `ProjectileStyle.For`, width
from the core, amber fallback). The fields `ModelName`, `TextureName`,
`Directional`, `Width`, `Radius`, `Rotation` were all parsed in
`DataProjectileTypeIGC.cs:15-29` but unused for visuals.

## Design

### 1. A pure per-type visual resolver — `src/Allegiance.Core/Visual/ProjectileVisual.cs`

`ProjectileVisual.Resolve(DataProjectileTypeIGC)` reproduces native's branch and
returns one of four kinds:

| Kind | Meaning | Drives |
|---|---|---|
| `Model` | real `modelName` (letter-leading) | 3D mesh at `Radius`, oriented along travel, spun by `Rotation` |
| `Directional` | `bDirectional` decal | texture stretched along travel with `Width` |
| `Billboard` | plain decal | camera-facing texture, size `Radius`, spun by `Rotation` |
| `Box` | no model + no texture, or a missing asset (via `RequireAssets`) | the legacy amber tracer (never invisible) |

Tint: textured kinds resolve to white (the texture carries the colour — the
P465b "PCore001 carries no colour" finding); a genuinely non-black `color` is
honoured as an override; the box keeps the amber P465b tracer so a missing asset
reads distinctly.

### 2. `ProjectileSpriteRenderer` — billboards + directional decals
`src/Allegiance.ClientCore.Game/Rendering/ProjectileSpriteRenderer.cs`

Decodes real RGB565 `.mdl` sprites (`<textureName>bmp.mdl`) at runtime from
`ALLEGIANCE_ARTWORK_PATH`, cached per stem (the `ExplosionAssets` /
`GaugeTextureSource` runtime-asset convention). Draws camera-facing billboards
(half-width = `radius × HalfExtentScale(2)`, the chaff/mine decal convention)
spun in the screen plane by `rotation × elapsed`; and directional decals
stretched along travel (length = `2×radius`, cross = `width`). Additive blend
(SrcAlpha, One) with restore-to-opaque afterwards (the P339/P525/P529 render-state
lesson). Every projectile texture measured is a 128×128 square (fx92 is a
128×16 strip), so full-texture UV mapping is correct.

### 3. `ProjectileModelRenderer` — the 3D model rounds
`src/Allegiance.ClientCore.Game/Rendering/ProjectileModelRenderer.cs`

Parses the real `.mdl` mesh (`<modelName>.mdl`) at runtime (the
`ProbeRenderer`/`MissileRenderer` convention) and draws it oriented along
travel, rolled by `rotation`, at its **authored native scale** — native's
`SetRadius` describes the collision/thing radius, it does not rescale the mesh.
Measured: the `dn_ptlascan1/2` beam meshes span ~250 units, matching their
radius-125 half-length, so no uniform rescale is applied. Textured with the
mesh's own `<modelName>bmp.mdl` when present, else lit-solid. Cull-mode none
(the small beams render both faces — no interior holes from DirectX winding).

### 4. Wiring — `Game1.Rendering.cs`

Each round now resolves a cached `ProjectileVisual`, accumulates its spin
(`Rotation × elapsed`), and is dispatched to the billboard / directional / model
/ box path. P462's own-shot local tracer and P239's camera-near cull ride the
same new path (the cull feeds the round's largest lateral extent so a huge
A-B Omega round near the eye is culled conservatively). A missing texture or
mesh logs once and degrades to the box — nothing renders invisible.

## Real PCore001 data (measured this session)

| Weapon | pid | Kind | Tex / Model | Radius | Rotation | Width |
|---|---|---|---|---|---|---|
| PE Gat Gun 1–3 | 1/3/4 | Billboard | `f99` | 1/1.5/2 | 3.84 | 0 |
| PE Mini-Gun 1 | 2 | Billboard | `tf5` | 1 | 3.84 | 0.1 |
| PE Vul Can 1/2 | 73/74 | Billboard | `dn_ptvulcan1/2` | 1.5/2 | 3.84 | 0 |
| PE SkyCap 1 | 7 | Billboard | `tf8` | 4 | 3.84 | 0 |
| PE AutoCan 1 | 10 | Billboard | `tf3` | 1.75 | 3.84 | 0 |
| PE EMP Can 1 | 13 | Billboard | `tf7` | 2 | 4.71 | 0 |
| EW Disruptor 1 | 42 | Billboard | `f28` | 2 | 5.24 | 0 |
| EW Gauss Gun | 65 | Billboard | `f06` | 2 | 3.84 | 0 |
| EW Utl Can 1 | 34 | Billboard | `f26` | 2 | 3.84 | 0 |
| EW A-B Stinger 1 | 200 | Billboard | `f17` | 2 | 3.84 | 0 |
| ER Nanite 1/2/Hvy | 58/59/29 | Billboard | `f44` | 1 | 3.84 | 0 |
| ER Nanite 3 | 88 | Billboard | `f49` | 1 | 3.84 | 0 |
| PW A-B Omega 1 | 84 | Billboard | `faohomega1` | **50** | 3.84 | 0 |
| PW A-B Hammer 1 | 81 | Billboard | `faohhammer1` | 4 | **8** | 0 |
| PW A-B Ion Bls 1 | 25 | Billboard | `dn_ptionbls` | 4 | 3.84 | 0 |
| EW A-B Can 1 | 21 | Billboard | `fx92` | 7 | 0 | 0.1 |
| EW Hvy A-B Can 1 | 27 | Billboard | `tf4` | 30 | 3.84 | 0 |
| EW A-BS Can | 24 | Billboard | `tf2` | 15 | 3.84 | 0 |
| EW Pulse Laser | 66 | **Directional** | `fx95` | 15 | 0 | 0.1 |
| EW Lsr Can 1/2 | 75/76 | **Model** | `dn_ptlascan1/2` | 125 | 0 | 0 |
| EW Mini-LC 1/2 | 79/80 | **Model** | `dn_ptlascan1/2` | 75 | 0 | 0 |
| PE MassDriver | 71 | **Model** | `bgrnd55` | 20 | 3.84 | 0 |

(The "donut" Domingo refers to is the Nanite's `f44` — a ring/torus sprite.)

## Tests — `tests/Allegiance.Core.Tests/ProjectileVisualTests.cs`

Failing-first: written before the resolver existed (the test project failed to
compile), then implemented to green. 8 tests, all against the **real PCore001
corpus** where possible:

- Gat Gun (pid 1) → billboard `f99`, radius 1, spin 3.84, white tint
- Pulse Laser (pid 66) → directional `fx95`, width 0.1, radius 15
- Lsr Can 1 (pid 75) → model `dn_ptlascan1`, radius 125
- Nanite (pid 58) → billboard `f44`, radius 1, spin 3.84
- synthetic non-black colour → tints the sprite
- missing-asset fallback → box (amber tracer)
- missing-model fallback → box
- no model + no texture → resolves straight to box

`ProjectileVisualTests` 8, plus the retained `ProjectileStyleTests` (still green).

## Headless visual proof — `roster-capture projectiles`

Offscreen-render path (P525/P529 pattern): the MonoGame client's compiled
Content provides the HUD fonts; every round is rendered to a `RenderTarget2D`
and saved as JPEG.

- `p544_projectiles_contact_sheet.jpg` — **62 distinct projectile visuals** in
  PCore001, each labelled with its weapon, kind, texture, radius and spin. Shows
  the real textured variety: glowing orbs (Gat `f99`), rings/tori (Nanite `f44`),
  starbursts, beams, the huge A-B Omega `faohomega1` orb.
- `p544_old_vs_new.jpg` — old tinted box (left) vs new native visual (right) for
  the six weapons Domingo named: **PE Gat Gun 1, PE Vul Can 1, ER Nanite 1 (the
  DONUT), EW Pulse Laser, PW A-B Omega (POWER), EW Lsr Can 1 (MODEL)**. Left =
  uniform amber boxes; right = the real textured sprites / directional beam /
  3D model.

The nanite `f44` is the **donut**; the A-B Omega `faohomega1` (radius 50 → a
200-unit sprite) is the missing "power" round — both now render their real look.

Images: `docs/reports/P544_NATIVE_PROJECTILE_VISUALS_assets/`
(also in `.verify_p544/`).

## Build & suite

Whole-solution build: **0 warnings, 0 errors**.

Full suite (4 test assemblies) via the P471 sequential-assembly approach
(`scripts/run-tests.sh`). Because the whole suite exceeds a single foreground
window (MonoGame.Tests alone is ~4m20s), each assembly was executed in its own
process, sequentially — the same per-assembly isolation P471's runner provides.
Numbers measured, per assembly, across the final code:

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | 1270/1270 | 1270/1270 | 1270/1270 |
| Allegiance.ClientCore.Tests | 474/474 | 474/474 | 474/474* |
| Allegiance.Core.Tests | 3734/3734 | 3734/3734 | 3731/3734 (documented flakes)† |

\* Run 3 ClientCore hit documented live-wire flakes on intermediate attempts
(CrossSide_WarperLeavesSector, ThreePeerSeededSession, FullSessionKill,
SessionLoss — TEST_FLAKES.md lines 33/38/51) and then passed 474/474 clean.

† Core.Tests passed 3734/3734 clean on runs 1 and 2. Every run-3 attempt (7×)
hit 3–5 of the ledger's live-socket flakes, which the ledger (line 427) records
as tests that "rotate run-to-run and remain": Conquest_win_in_one_mission_
does_not_leak, Board_refused_once_mission_over, GameOverPlayersWire Conquest
variants, TcpLoopbackProof, AutoReloadOnEmpty, ShipsUpdateLive, AddPartTests,
SideElimination, CargoBayReload, LiveStationRescue, LiveRelaunch, LiveGainFlag,
LiveCountdownStartPing, InboundFloodSafety, MineCrossSideVisibility. All are
pre-existing live-wire timing tests unrelated to P544 (this task changes only
client rendering + a pure resolver); none is ProjectileVisualTests, which passed
8/8 in every run. Citations: `docs/TEST_FLAKES.md` lines 33, 38, 39, 51, 121,
204, 301, 427.

So the final code produced two fully-clean consecutive full-suite passes
(run 1: 5490/5490; run 2: 5490/5490) and a third pass whose three deterministic
assemblies are clean and whose Core assembly was blocked only by the documented
live-wire flake population — no P544 regression in any of the 3 runs.
