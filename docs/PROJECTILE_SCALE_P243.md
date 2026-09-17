# P243 — Shrink oversized projectile bolts to real ship-relative scale

## Relationship
Pure **MonoGame-client constant fix** (Allegiance.ClientCore.MonoGame). Zero change
to `Allegiance.Core`/`Allegiance.Server` sim logic, zero wire change, zero change to
`Weapon.cs` spawn positions (no-muzzle-offset scope cut honoured). Landed directly
on `master` (Domingo pushes). Part of the "gameplay sense" review pass: the live
played client showed projectiles "looking like flying pieces of paper (too large),
specially in cockpit view".

## Root cause — confirmed with real numbers, not a guess
The projectile bolt renders as a solid box scaled `ProjectileWidth × ProjectileWidth ×
bLen` (`Game1.cs:1171`). `ProjectileWidth` was **8f** (`Game1.cs:62`). A real,
corpus-verified fighter hull (`fig01`) has a half-extent of **~1.94 units**
(`docs/COLLISION.md:174`, "fig01's leading vertex is |x| = 1.936683"). So the old bolt
was **~4.1× a fighter's half-extent and ~2.1× its full width** — wider than the ship
firing it. Length (`BoltLengthPerUnitSeconds 0.12`, `MinBoltLength 32`, `MaxBoltLength
480`, `Game1.cs:63-65`) is 16–240× the fighter's radius — the longitudinal stretch is
legitimate (a fast-moving tracer streak reads longer than the ship), so length was left
alone (see below).

P239's `ProjectileNearCamClearance = 60` (`Game1.cs:78`) was itself a workaround for
this oversized geometry, not a fix: an 8-wide bolt face 3 units ahead of the eye
subtends ~100° of the 75° FOV (`docs/PROJECTILE_NEAR_CAMERA_CULL_P239.md`). Shrinking
the geometry attacks the same flood at its source.

## Fix
- `ProjectileWidth`: **8f → 0.35f**. Ship-relative reasoning: the fighter's full width
  is ~3.88 units (2 × half-extent 1.94); 0.35 is ~0.09× that full width — a thin
  bolt/tracer, well under "an order of magnitude smaller" than the old 8, and squarely
  in the task's 0.2–0.5 target band. It reads as a thin bolt in the cockpit (the
  complaint view) without vanishing entirely at close range (see live proof). The old
  bolt, by contrast, rendered as a sheet wider than the firing ship — the "paper".
- `ProjectileNearCamClearance`: **60f → 15f**. With the cross-section shrunk 8 → 0.35,
  the first-visible bolt face subtends only ~1.3° at the 15-unit gap (vs ~7.6° for the
  old 8-wide at 60), so the much tighter gap still prevents any screen flood while
  letting own-forward shots appear sooner (254+15 ≈ 269 units of flight ≈ ~0.45s at
  600 u/s). It is deliberately **not** removed: a bolt spawned at ship centre
  (`Weapon.cs:230`, no muzzle offset) can still momentarily coincide with the 14-unit-
  forward cockpit eye (`CockpitCamera._forwardOffset = 14f`), so the cull stays.
- Lengths (`BoltLengthPerUnitSeconds` / `MinBoltLength` / `MaxBoltLength`):
  **unchanged** — re-examined with the same lens and judged not broken. Length encodes
  projectile speed (faster shots read as longer streaks); a 32–480-unit stretch is the
  intended tracer streak, and thinning the cross-section does not make the length
  disproportionate (only the width was the complaint). Not fixing what isn't broken.

Out of scope (honoured): no sim/Weapon muzzle-offset change; no missile rendering
change (P233-verified); native reference untouched.

## Live screenshot verification (offscreen RT, cockpit view — required)
Built a disposable harness `/tmp/p243verify` (NOT in the repo): a **real in-process
`Allegiance.Server`** with a fight-capable hull override (reflective `ShipSpawner` —
repo kept clean), a **real `ConnectionSession`**, driven to `Started`, then held fire
(`stateM = AllWeapons 0x8000`) so **real projectiles** (PE Gat Gun 3, projType 4,
vel (0,0,-600), life 1.1 s) were spawned and broadcast (`PROJ_INST(12)`; 79 in the
world at snapshot). The snapshot was then rendered with the **real `ProjectileRenderer`**
through the **real `CockpitCamera` / `ChaseCamera`** to offscreen `RenderTarget2D`s
(the P207/P218 capture technique), at the new width (0.35) and the old width (8) for a
same-camera before/after. Evidence in `.verify_p243/`.

Objective pixel analysis (800×480 frames, amber-masked; "amber px" = the bolt's tint):

| frame | width | amber px | % of frame | what it shows |
|---|---|---|---|---|
| cockpit, one bolt 40 u ahead | 8  | 384,000 | **100%** | the 8-wide face floods the whole 75° FOV — the "paper" |
| cockpit, one bolt 40 u ahead | 0.35 | 12,100 | 3.15% | small centred ~109×109 bolt dot — no flood |
| cockpit, live held-fire (all bolts) | 8  | 11,664 | 3.04% | big centre slab |
| cockpit, live held-fire (all bolts) | 0.35 | 16 | **0.00%** | clean view, no flood |
| close chase, hull + bolts | 8  | 1,042 | 0.27% | thick ship-scale yellow slabs/columns |
| close chase, hull + bolts | 0.35 | 30 | 0.01% | bolts sub-pixel — far smaller than the hull |

Reading: at the new width a near cockpit bolt is a small dot/square instead of a
full-screen sheet, and sustained held-fire in the cockpit (the P239 flood repro) stays
clean (**0.00%** amber). In the chase frame the bolts are so thin relative to the hull
they are sub-pixel — the same bullets the old width rendered as thick slabs ~the hull's
own size. The before/after pairs are at identical camera distance, so the only variable
is the width constant.

## Build / test
- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`): **0 warnings,
  0 errors**.
- `ProjectileCameraCullTests` updated to the new production constants (width 0.35,
  clearance 15) — the two cross-section-boundary tests whose numbers depended on the
  old half-lateral 64 were re-derived (lateral reach is now 15.175); all 11 pass in
  isolation.
- Full suites (baseline matches the pre-task counts):
  - MonoGame.Tests: **242** passed (count unchanged; the cull tests were updated in
    place, not added).
  - Core.Tests: **3064** passed ×2, 1 fail each = the documented pre-existing
    `LiveTurretGunnerBindingTests` machine-state flake (total 3065, baseline match).
  - ClientCore.Tests: **83** passed.

## Follow-up candidate (named, NOT built this task)
The solid box is a documented scope cut standing in for native's real technique —
`CprojectileIGC::Initialize` (`Igc/projectileIGC.cpp:34-70`) renders projectiles as a
flat textured **decal billboard** (`LoadDecal`, lines 59-66) with a `width` decal field
decoupled from the separate hit-test `radius` field. A future fidelity task should
switch `ProjectileRenderer` to that billboard/decal technique (camera-facing textured
quads), which gives naturally visible bright streaks at range without the near-camera
flood a solid box imposes — the width constant tuned here would inform the decal's
`width`. Explicitly out of scope for P243, flagged as the follow-up.

## Citations
- `Game1.cs:62` — `ProjectileWidth = 8f` (was); `:1171` — `CreateScale(width,width,len)`.
- `Game1.cs:63-65` — length constants (re-examined, kept).
- `Game1.cs:78` — `ProjectileNearCamClearance = 60f` (was, P239).
- `docs/COLLISION.md:174` — `fig01` half-extent ~1.94 (leading vertex |x| = 1.936683).
- `Rendering/ProjectileRenderer.cs` — generated box, `CullMode.None`; its doc-comment
  (lines 16-22) already flags the box as a scope cut vs native decals.
- `Rendering/ProjectileCameraCull.cs` — the near-camera cull (P239), unchanged logic.
- `docs/PROJECTILE_NEAR_CAMERA_CULL_P239.md` — the 8-wide face ~100° flood finding.
- `Sim/Weapon.cs:230` — projectile spawn at ship centre, no muzzle offset (scope cut).
- `Rendering/CockpitCamera.cs` — `_forwardOffset = 14f`.
- Native: `Igc/projectileIGC.cpp:34-70` (decal billboard, `LoadDecal`) — the named
  follow-up (read-only reference).
