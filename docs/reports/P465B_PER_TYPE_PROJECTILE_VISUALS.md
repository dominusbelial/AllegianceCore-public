# P465b — per-type projectile visuals (colour, width, texture/model)

Date: 2026-10-01 · Branch: `master` (commit not pushed) · Part 2 of the P465 split (independent of P465 / P465c)

## What changed

The port previously drew every projectile as one hard-coded amber tracer
(`ProjectileTint (1.0, 0.84, 0.32)`, `ProjectileWidth 0.35`) because the client
had no static core (the P231-era comment). The client now loads PCore001
(`Game1.Lifecycle.cs:208 → Rendering/ShopCatalog.cs`, `StaticCore.ProjectileTypes`),
so per-type data is reachable. This task wires it in through a pure, tested
helper:

- **New `Allegiance.Core/Visual/ProjectileStyle.cs`** — a pure
  `ProjectileStyle.For(typeId, core.ProjectileTypes)` resolver that returns a
  bolt style `{ R, G, B, Width, Radius }`. A real non-black colour is honoured,
  a positive real `Width` is used, and the amber tracer (`AmberFallback`,
  width `DefaultWidth` 0.35) is the fallback for an unknown type or a missing
  core.
- **New `tests/Allegiance.Core.Tests/ProjectileStyleTests.cs`** — 5 tests over
  the real PCore001 corpus + a synthetic coloured type + the unknown/null
  fallbacks.
- **Renderer wiring** (`Game1.cs`, `Game1.Lifecycle.cs`, `Game1.Rendering.cs`):
  the projectile-type dictionary is captured once into `_projectileTypes`;
  every gather (server `ProjectileEntity` bolts AND the P462 local own-shot
  echo, both of which carry `ProjectileTypeId`) resolves the bolt's width +
  tint via the helper; the cull and the box scale use the per-bolt width, the
  draw uses the per-bolt tint. The stale "no static core / colour unreachable"
  comments were updated (incl. `ProjectileRenderer.cs` remarks).

## ⚠ The headline finding: PCore001 carries NO per-type colour — and native ignores it anyway

Measured across the whole real corpus (the same PCore001 the server boots):

- **99 projectile types; ZERO carry a non-black colour.** Every
  `DataProjectileTypeIGC.Color` is `(0,0,0,0)`.
- **Only 10 types carry a real width**, and among player-mounted weapons the
  real widths are just `0.1` (the Mini-Gun / A-B Can / Pulse-Laser series);
  every other player weapon has width `0` → the `0.35` default.
- **Only 3 of the 39 referenced bolt textures exist in the Artwork pack**
  (`dn_ptlasbls1/2`, `dn_ptlngtom`), and **none of those is fired by a
  player-mountable weapon** (they belong to NPC/station/expendable ordnance).

Furthermore, native does **not** drive the visible bolt from `color` at all.
The projectile decal path forwards the colour only to `LoadCVH` (hit-test/icon
setup) and builds the visible bolt from the `<textureName>bmp` **animated
image** tinted `Color::White()`:

- `src/Igc/projectileIGC.cpp:51-70` — decal path calls
  `LoadDecal(textureName, …, Color(color.r,g,b,a), bDirectional, width, …)`.
- `src/Igc/modelIGC.cpp:276-289` — `CmodelIGC::LoadDecal` forwards the colour
  **only to `LoadCVH`** and calls `m_pThingSite->LoadDecal(textureName,
  bDirectional, width)` — the colour argument is never applied to the visual.
- `WinTrek/trekigc.cpp:1772-1814` — the actual decal is a `DecalGeo` built
  from `LoadAnimatedImage(textureName + "bmp")` with **`Color::White()`** and
  the `width`.

So the real per-type bolt identity in native is the **texture**, not the
colour. Because (a) PCore001's colours are all black, (b) native ignores
`color` for the bolt anyway (it shows the texture un-tinted), and (c) almost
none of the bolt textures exist in the Artwork pack (and native animates them
over ~2 s), the **flat tinted bolt stays** — that is the scope-3 "otherwise
document why". Treating a genuinely non-black colour (which a *different* core
could carry) as an override is a pragmatic superset that costs nothing on
PCore001 (it stays amber, native-equivalent) and makes the mechanism useful
beyond this one core.

## Scope items, disposition

1. Look up `ProjectileTypeId` in the loaded core, cached once — **done**
   (`_projectileTypes` cached at load; O(1) lookup per bolt).
2. Draw with the type's real Colour / Width / Radius — **done** as the data
   permits: real positive width (e.g. Mini-Gun 0.1), amber colour (PCore001
   black → amber fallback), radius captured in the style (native uses radius
   for the `ThingSite`/3D-model path, not the flat decal cross-section, which
   `width` drives — documented on the helper). Length by speed unchanged.
3. Texture the bolt if a real `TextureName` exists and native uses it —
   **documented why not** (above: only 3 textures exist, none player-fired;
   native animates them; the flat tinted bolt stays).
4. Amber tracer fallback for unknown / missing core — **done**
   (`ProjectileStyle.AmberFallback`).
5. Update the stale comment — **done** in `Game1.cs`, `Game1.Rendering.cs`,
   `ProjectileRenderer.cs`.

Out of scope respected: no sounds / muzzle offsets / missile & expendable
models; **no server or wire change**.

## Verification

### Build
`/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors.**

### Tests (fail-first)
`ProjectileStyleTests` were authored against `ProjectileStyle.For`, which did
not exist on the pre-task tree (RED — no such type; the whole helper is new),
then the helper was added (GREEN). 5/5 pass against the real PCore001:

- pid 2 (PE Mini-Gun 1): real `Width 0.1`, `Radius 1`, amber tint (black
  colour → amber).
- pid 1 (PE Gat Gun 1): zero width → `DefaultWidth 0.35`, `Radius 1`, amber.
- synthetic id 777 (green colour + width 1.5 + radius 4): maps exactly.
- unknown id 9999 → `AmberFallback`; `null` core → `AmberFallback`.

### Full suite — 3 consecutive runs
Three full-solution `/home/domingo/.dotnet/dotnet test` runs (observed per-project
failures; all remaining tests passed):

| Project | Run A | Run 1 | Run 2 |
|---|---|---|---|
| Lobby | 12/12 ✅ | 12/12 ✅ | 12/12 ✅ |
| **ClientCore.MonoGame** (this task's renderer) | **1011/1011 ✅** | **1011/1011 ✅** | **1011/1011 ✅** |
| ClientCore | 4 fail | 4 fail | 2 fail |
| Core | 8 fail | 15 fail | 8 fail |

The failures are **only** the pre-existing live-wire / timing flake families
documented by the P465 report and the P471 flake ledger — conquest capture
(`ConquestTerritoryLiveTests`), `GameOverPlayersWireTests`, `SideEliminationLiveTests`,
`LiveTurretGunnerBindingTests`, `LiveMoneyTests.Board_refund`, `LiveRipcordTests`,
`JoinFlowLiveTests`, `ShipDeleteGoneLiveTests`, `FullSessionKillIntegrationTests`,
plus assorted broadcast/update/reload/transport timing tests — whose membership
and count shift run-to-run (Core 8 → 15 → 8; ClientCore 4 → 4 → 2), the classic
signature of load/timing flakes. **None is in a file this task touched, and none
relates to projectiles or rendering.** Two are explicitly called out in P471 as
failing on the pristine tree (`LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
here in run 2). **Proven pre-existing, not caused by P465b**: stashing this
task's four MonoGame file edits and re-running the failing classes against the
pristine `master` tree reproduces the same 7–8 failures. The new
`ProjectileStyleTests` (5) pass every run, and the changed MonoGame project is
1011/1011 in all three runs.

### Live screenshots (real renderer, real per-type styles from PCore001)
`tools/projectile-visuals-proof` (a `*-proof` harness in the repo's established
pattern) loads the real core, resolves real styles via `ProjectileStyle.For`,
and renders the **real `ProjectileRenderer`** offscreen:

- `.verify_p465b/p465b_two_real_bolts.png` — EW Gauss Gun (width 0.35 default,
  speed 2900 → long) vs PE Mini-Gun 1 (width 0.10 real, speed 500 → short), plus
  a clearly-labelled synthetic green bolt proving the (core-conditional) colour
  path is wired even though PCore001 never carries colour.
- `.verify_p465b/p465b_cross_sections.png` — end-on cross-sections magnified:
  a ~0.10-square vs a ~0.35-square (the real 3.5× width ratio), both amber
  (PCore001 has no colour).

Console confirms the resolution: `EW Gauss Gun (pid 65) → width 0.35 radius 2`,
`PE Mini-Gun 1 (pid 2) → width 0.10 radius 1`, both amber.
(NB the proof PNGs carry no text — fonts don't load in the offscreen proof
harness; the existing shieldflare/hullhit proof PNGs are likewise text-less.
Mapping is in this report and the tool's stdout.)

## Files

- `src/Allegiance.Core/Visual/ProjectileStyle.cs` (new)
- `tests/Allegiance.Core.Tests/ProjectileStyleTests.cs` (new)
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` (cached dict, comments)
- `src/Allegiance.ClientCore.MonoGame/Game1.Lifecycle.cs` (cache `_projectileTypes`)
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` (per-bolt width/tint)
- `src/Allegiance.ClientCore.MonoGame/Rendering/ProjectileRenderer.cs` (comments)
- `tools/projectile-visuals-proof/` (new proof harness)
- `docs/reports/P465B_PER_TYPE_PROJECTILE_VISUALS.md` (this report)
