# P530 — general lighting pass: light every 3D scene object (ships, stations, missiles, probes, building effects) with the native per-sector bidirectional light, as P529 did for asteroids

## Why this task exists

**Domingo (2026-10-04):** "we will have to do a general lighting pass on all
the other features". P529 (`2c97330a` + fixup `32048b79`) fixed the asteroid
black↔gold flicker by replacing MonoGame's stock rig with native's per-sector
lighting, and **Domingo confirmed live on 221 that the asteroid issue is
gone.** Every other 3D renderer still uses the stock rig.

**Native:** one scene light for everything in the 3D scene.
`TrekWindowImpl::SetCluster` (`src/WinTrek/WinTrek.cpp:3614-3640`) reads the
current cluster's `GetLightColor()`, `GetLightColorAlt()`,
`GetBidirectionalAmbientLevel()` and `GetLightDirection()`, and
`UpdateBidirectionalLighting` (`:4928-4937`, on by default `:2696`,
`:3296-3297`) calls `m_pimageScene->SetLight(m_color, m_colorAlt)` +
`SetAmbientLevel(0.25)`. `m_pimageScene` renders **all** scene geometry:
ships, stations, missiles, probes, rocks. There is no per-type light, no
camera headlight and no specular. The P529 report
(`docs/reports/P529_ASTEROID_LIGHTING.md`) has the full trace, including
the colour decode (`clusterIGC.h:307-318`) and the alt colour.

**The port's shared model already exists:**
- `src/Allegiance.Core/Rendering/SectorLighting.cs` (`FromCluster`, key
  colour/direction, constant blue alt/back-light, 0.25 ambient, no
  specular).
- `Game1.Rendering.cs:~549` builds `_sectorLighting` per current sector.
- `AsteroidRenderer.ApplySectorLighting` (`AsteroidRenderer.cs:163-170`)
  maps it onto a `BasicEffect`.

**Renderers still on the stock rig (`EnableDefaultLighting`):**

| Renderer | Location | Notes |
|---|---|---|
| `ShipRenderer` | `:130` | Ambient `ShipAmbient` (P337); P355's camera-relative headlight replaces default light slot 2 (`:111-135`). Native has no headlight. |
| Content missile models | `Game1.Rendering.cs:~2325` (`DrawMissileContentModel`) | `ShipAmbient` |
| `MissileRenderer` | `:91` | |
| `ProbeRenderer` | `:123` | Ambient 0.32/0.34/0.36 |
| `StationRenderer` | `:107` | Ambient 0.32/0.34/0.36 |
| `BuildingEffectRenderer` | `:104` | |

The stock rig carries the same warm specular key light that caused the
asteroid flicker, so ships and stations can flash gold in the same way.

## Scope

1. **Share the mapping.** Lift `ApplySectorLighting` into one reusable
   helper (e.g. `Rendering/SectorLightingEffect.Apply(BasicEffect,
   SectorLighting)`) and use it from `AsteroidRenderer` too. Don't copy it
   six times.
2. **Apply the current sector's lighting** to every renderer in the table:
   key + alt back-light + 0.25 ambient, **specular zero**, light 2 off unless
   native uses it. Every renderer reads the same per-frame `_sectorLighting`.
3. **P355 headlight and P337 ambient lifts:**
   - Native has neither. Default to native (remove them).
   - **But check readability:** the port's ships use Meshy baseColor
     textures, not native art. If capture shows ships turning unreadably
     dark in dim sectors, don't invent a fix silently. Report it with
     captures and propose the smallest native-respecting option (e.g. a
     documented ambient floor, behind a single constant), then pick it and
     say why.
4. **Explicit render state per pass** (depth, blend, rasterizer, sampler) as
   P529 did, and restore it after. Mipmaps for any texture that lacks them
   (reuse `TextureMipmapBuilder`).
5. **Sector changes:** the lighting updates when you warp (it's
   per-sector), for every renderer, on the frame of the transition.

## Explicitly out of scope

- The hangar/loadout ship preview (P525/P526): it has its own camera and
  backdrop. Leave it alone unless it shares `ShipRenderer`'s effect state,
  in which case keep its current look and say how.
- Unlit/additive passes: projectiles (P544), explosions, the VFX spike
  (P543), lens flare, backdrop, starfield, HUD.
- Asteroids (done in P529), except for switching them to the shared helper.
- New shaders/custom `.fx`. This box can't compile MonoGame effects without
  Wine. Stay on `BasicEffect`.

## Verification

- **Failing-first unit tests:**
  - The shared helper sets key/alt/ambient from a `SectorLighting` and
    zeroes specular on all lights.
  - Each listed renderer's effect, after setup, has no specular and the
    sector's ambient (testable through an internal effect-config accessor
    or the helper call).
  - A sector change swaps the light for every renderer.
- **Headless visual proof** (the `tools/roster-capture` offscreen path from
  P525/P529): an old-vs-new contact sheet of a ship (2–3 hulls), a station,
  a missile and a probe, in **two sectors with different light
  colour/direction** (the brightest and darkest in PCore001's clusters),
  from front, side and back. Point out any gold specular flashes on the
  "old" side, and say whether the "new" side stays readable.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and proof are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file**
  (`git status --untracked-files=all`). Confirm with `git log -1` and a
  clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P530_SECTOR_LIGHTING_ALL_OBJECTS.md`: native
   citations, the renderer table before/after, the readability decision
   on P355/P337, contact sheets and tests.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
