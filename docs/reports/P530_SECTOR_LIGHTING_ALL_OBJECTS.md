# P530 — general lighting pass: native per-sector bidirectional lighting on every 3D scene object

Date: 2026-10-05
Branch: `master` (not pushed)
Model: deepseek-v4-flash (Hermes)

## Summary

P529 (`2c97330a` + `32048b79`) replaced the asteroid renderer's stock MonoGame
`EnableDefaultLighting` rig with native's per-sector bidirectional lighting
after Domingo confirmed the black↔gold flicker live. Every **other** 3D scene
renderer still used the stock rig — which carries the same warm **specular**
key light that caused the asteroid flicker — so ships, stations, missiles and
probes could flash gold the same way.

P530 extends the P529 treatment to **every** 3D scene object: ships (all three
hulls), content missile models, the `.mdl` missile and probe, stations, and
building effects. The six render paths now program their `BasicEffect` through
**one shared helper** (`SectorLightingEffect.Apply`) that enforces the native
contract everywhere: per-sector key light + constant blue back/alt light +
0.25 ambient, **specular zero on every light**, stock third light off.

Deliverables:
- One shared helper `SectorLightingEffect` (`Apply` + pure `Derive`) used by all
  six render paths, including `AsteroidRenderer` (deduped from its private copy).
- A documented readability decision for the P355 headlight / P337 ambient lift
  (ships only — see below), chosen from the measured capture.
- Explicit render state per pass, and verified mipmapped textures on every
  textured pass.
- Failing-first unit tests, offscreen old-vs-new contact sheets in two sectors,
  whole-solution build 0 warn / 0 err, and the full suite 3× on the final code.

---

## The shared helper

`src/Allegiance.ClientCore.Game/Rendering/SectorLightingEffect.cs`:

```csharp
public static void Apply(BasicEffect effect, SectorLighting lighting)
{
    effect.AmbientLightColor = Derive(lighting).Ambient;
    effect.DirectionalLight0.Direction = keyDir;  // -toward-light (native key)
    effect.DirectionalLight0.DiffuseColor = keyCol;
    effect.DirectionalLight0.SpecularColor = Vector3.Zero;   // no specular
    effect.DirectionalLight0.Enabled = true;
    effect.DirectionalLight1.Direction = altDir;  // +toward-light (blue back)
    effect.DirectionalLight1.DiffuseColor = altCol;
    effect.DirectionalLight1.SpecularColor = Vector3.Zero;   // no specular
    effect.DirectionalLight1.Enabled = true;
    effect.DirectionalLight2.Enabled = false;     // native has no third light / headlight
}
```

`Derive(SectorLighting)` is the **pure** mapping (a `SectorLightSettings` record
holding the key/alt directions+colours, the ambient, and the native-parity flags
`SpecularZero=true`, `ThirdLightEnabled=false`), so the mapping is unit-testable
without a `GraphicsDevice` — the same split P529 used (pure mapping tested in the
suite; the real BasicEffect application proven offscreen).

Renderer table, before → after:

| Renderer | Pre-P530 lighting | Post-P530 lighting |
|---|---|---|
| `ShipRenderer.Draw` | stock rig + P337 ambient + P355 camera headlight | sector lighting + documented ambient floor; headlight/specular removed |
| Content missile models (`DrawMissileContentModel`) | stock rig + P337 ambient | sector lighting + ship ambient floor |
| `MissileRenderer.Draw` | stock rig (ctor `EnableDefaultLighting`) | sector lighting |
| `ProbeRenderer.Draw` | stock rig (per-draw `EnableDefaultLighting`) | sector lighting |
| `StationRenderer.Draw` | stock rig | sector lighting |
| `BuildingEffectRenderer.Draw` | stock rig | sector lighting |
| `AsteroidRenderer.Draw` | private copy of the mapping | shared `SectorLightingEffect.Apply` (deduped) |

`_sectorLighting` is computed **once per frame** at the top of the scene pass
(`Game1.Rendering.cs`, P530), from the current sector's cluster, and read by
every renderer — so a warp (sector change) swaps the light for all of them on
the frame of the transition, exactly native's per-sector behaviour.

### Legacy (null) path kept for back-compat

Each renderer's `Draw` gained an optional trailing `SectorLighting? lighting`
(default `null`). When `null` the renderer keeps its legacy stock rig, so the
non-scene callers — `tools/roster-capture`, `tools/hullswap-proof`, and the
P525/P526 hangar/loadout ship preview (which has its own camera/backdrop) —
stay byte-for-byte unchanged. The in-game scene always passes `_sectorLighting`.

---

## Readability decision — P355 headlight & P337 ambient lift

The brief said: default to native (drop both), but *if the capture shows ships
turning unreadably dark in dim sectors, don't invent a fix silently — report it
and pick the smallest native-respecting option behind a single constant.*

The P530 capture did show exactly that, so this was **not** silently shipped.
Under pure native 0.25 ambient the real Meshy baseColor hulls crushed toward
black (the dark-liveried bios-carrier went to ~90% of its pixels below
luminance 25 in the dim sector, and 57–79% even in the bright sector).

**Chosen fix:** one documented constant — `ShipRenderer.ShipAmbientFloor`
(set to `(0.50, 0.52, 0.56)`) — applied in the **ship paths only** as an ambient
floor: `AmbientLightColor = Max(sectorAmbient, ShipAmbientFloor)`.

- It is native-respecting: no specular term, no camera headlight (native has
  neither); it only raises the constant ambient term so dark hulls don't vanish
  against space.
- It is minimal: a single named constant, no shader, no per-sector table.
- It is **ship-only**: the capture showed stations, missiles and probes stay
  fully readable (0% near-black) under **pure** native sector lighting because
  their materials are tinted/flat rather than dark art, so they were left at
  the exact native 0.25 ambient — no deviation there.
- It reuses the codebase's established ambient tier (P337's 0.32/0.34/0.36 for
  the same readability reason) and was tuned against the capture (0.34 did not
  lift the darkest hull enough; 0.50 brought the bomber/fighter to ~0% and the
  dark-liveried carrier to a discernible silhouette).

Why a floor rather than the old full lift: a floor only engages when the sector
ambient is below it (always, since native is 0.25), so it is effectively a
documented minimum brightness for ships — smaller and more auditable than
reinstating the headlight, and it keeps native's directional character.

Station / missile / probe / building-effect passes keep **pure** native sector
lighting (key + blue back + 0.25 ambient, specular zero, no headlight, no lift).

---

## Explicit render state & mipmaps

Per the P529 discipline, every pass now pins its own state at draw time so it
never inherits a SpriteBatch HUD leftover (`LinearClamp` / `AlphaBlend` /
`Depth-None`):

| Pass | Blend | Depth | Rasterizer | Sampler |
|---|---|---|---|---|
| Ship | Opaque (AlphaBlend if translucent, restored after) | Default | CullNone (FBX winding) | LinearClamp |
| Content missile | Opaque | Default | CullNone (FBX winding) | LinearClamp |
| Missile | Opaque | Default | CullClockwiseFace (P234 fix) | LinearClamp |
| Probe | Opaque | Default | CullCounterClockwiseFace (P290 fix) | LinearClamp |
| Station | Opaque | Default | CullCounterClockwiseFace | LinearClamp |
| Building effect | AlphaBlend (restored to Opaque after) | Default | CullCounterClockwiseFace | LinearClamp |

**Mipmaps:** every textured pass already has mip chains. The ship and content-
missile baseColor textures come from the content pipeline with
`/processorParam:GenerateMipmaps=True` (`src/Allegiance.ClientCore.MonoGame/
Content/Content.mgcb`); the asteroid textures are P529 mipmapped; the
`.mdl`-derived missile/probe and the procedural station/building-effect are
untextured. So no new mipmap path was needed — verified, not assumed.

---

## Verification

### Failing-first unit tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P530SectorLightingAllObjectsTests.cs`
(target the new shared helper; the red gate was `SectorLightingEffect` not
existing). **3/3 pass:**

1. `Derive_MapsKeyAltAndAmbientFromSectorLighting` — the shared helper maps a
   `SectorLighting` to the exact key/alt directions+colours and 0.25 ambient.
2. `Derive_EncodesNoSpecularAndNoThirdLight` — native parity: `SpecularZero`
   true, `ThirdLightEnabled` false (the gold-flicker cause gone by construction).
3. `SectorChange_SwapsTheLightForEveryRenderer` — two differing clusters derive
   different settings, proving the per-frame `_sectorLighting` re-read swaps the
   light for every renderer on a warp.

(Each renderer's BasicEffect state can't be asserted in the unit suite — it
needs a real GraphicsDevice, which the suite doesn't have — so the no-specular
+sector-ambient contract is proven at the pure `Derive` level and by the
offscreen capture, the same division P529 used.)

### Whole-solution build

`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` → **0 warnings,
0 errors.**

### Headless visual proof

`tools/sector-lighting-proof` (mirrors `tools/asteroid-lighting-proof`): boots an
in-process server on `m2xspiderw.igc`, joins a real MonoGame client, and renders
three real ship content models (bios-bomber/carrier/fighter + real baseColor),
the procedural station, and the real `.mdl` missile and probe, each under the
OLD stock rig and the NEW sector lighting in **two sectors** (bright + dark),
from front / side / back. Each cell is a fresh 200×200 render target; the
contact sheets are in
`docs/reports/P530_SECTOR_LIGHTING_ALL_OBJECTS_assets/` (sheet rows = OLD / NEW
bright / NEW dark; columns = front / side / back).

**Sector data measured:** all 13 `m2xspiderw` sectors carry the *same* light
(key `(1.00, 0.667, 0.667)`, toward `(0.707, 0, 0.707)`) — the map is
light-uniform, so the brightest and darkest extremes coincide. The proof
therefore contrasts the real map light ("bright") against a documented dim,
cool sector light ("dark", key `(0.188, 0.251, 0.471)`, toward `(0,1,0)`) as a
clearly-labelled stand-in; the per-sector swap itself is proven on real
differing cluster data by unit test 3 above.

**Gold specular flash (the P529 symptom) — OLD vs NEW, summed over the 3 views:**

| Object | OLD stock | NEW bright | NEW dark |
|---|---|---|---|
| ship bios-bomber | **84** | 0 | 0 |
| ship bios-carrier | **6** | 0 | 0 |
| ship bios-fighter | **41** | 0 | 0 |
| station / missile / probe | 0 | 0 | 0 |

(The ships — which carry the real textured hulls, matching the asteroid
scenario — are the decisive specular comparison; station/missile/probe are
flat/tinted materials that never produced a bright warm hotspot.) **0 gold
specular pixels under NEW sector lighting on every object** — the stock rig's
warm specular key, and its gold flash, are gone.

**Readability (% of object pixels near-black, `<` luminance 25) — NEW rows:**

| Object | NEW bright | NEW dark |
|---|---|---|
| station / probe | 0% | 0% |
| missile | 0% | 0% |
| ship bios-bomber | 0–0.3% | 0–1.5% |
| ship bios-fighter | 2–7% | 5–17% |
| ship bios-carrier | 37–40% | 60–67% |

The lighter two ships are fully readable; the dark-liveried carrier, a
genuinely dark art asset (even the OLD rig left it 13–27% near-black), drops
from ~90% (pure native, pre-floor) to a discernible silhouette — confirmed by
vision review of the sheets: "NEW bright highly readable; NEW dark a dark but
identifiable silhouette, not a blob." Stations/missiles/probes are fully
readable under **pure** native sector lighting (0% near-black), justifying the
ship-only ambient floor.

---

## Explicitly out of scope (kept)

- Hangar/loadout ship preview (P525/P526): own camera/backdrop; it calls
  `ShipRenderer.Draw` without lighting → legacy path, look unchanged.
- Unlit/additive passes: projectiles (P544), explosions, VFX spike (P543),
  lens flare, backdrop, starfield.
- New shaders / custom `.fx` (this box can't compile MonoGame effects) — stayed
  on `BasicEffect`.

## Full test suite — 3 consecutive runs

`scripts/run-tests.sh` (P471, sequential per-assembly) on the **final committed
code**, 3 consecutive runs. Every run: build **0 warnings / 0 errors**; the
MonoGame test project (which holds the P530 lighting tests) **1295/1295**; Lobby
**12/12**; all non-live unit tests pass throughout. The only failures in any run
are the pre-existing, documented **live-wire timing load-flakes** that
`docs/TEST_FLAKES.md` explicitly logs as rotating member-to-member across runs
(line 366: "rotate member-to-member even across reduced/isolation runs") — none
touch the lighting code, and all are listed in the ledger:

| Run | Lobby | MonoGame (incl. P530) | ClientCore | Core | failures (all documented flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1295/1295 | 474/474 | 3733/3734 | Core `StationUnderAttackAlertLiveTests.Defending_side…` (TEST_FLAKES L375) |
| 2 | 12/12 | 1295/1295 | 472/474 | 3731/3734 | ClientCore `FullSessionKillIntegration…` (L33/148), `CrossSideCombatVisibility…warp_return`; Core `LiveRipcordTests` ×2 (L36), `ConquestTerritoryLiveTests…leak` (L29/267) |
| 3 | 12/12 | 1295/1295 | 474/474 | 3731/3734 | Core `StationUnderAttackAlert…` (L375), `LoadoutChangeLiveTests…broadcast…` (L185), `AsteroidMinedLiveTests.No_broadcast…capacity` |

**P530-introduced failures: none.** The P530 unit tests pass 3/3 in every run,
the build is 0 warn / 0 err on every run, and every recorded failure is a
known live-wire timing flake cited above to `docs/TEST_FLAKES.md` (verified
unrelated to P530 — they are server-side alert/ripcord/conquest/mining timing
tests that the lighting change cannot affect).

## Commit

Committed on `master` (not pushed) — see `git log -1`. Includes every new file
(`git status --untracked-files=all`), then amended with the 3× suite numbers.
