# P576 — (A) native per-sector sky (study + port the native globe/nebplnt library): each cluster's own seeded star field, environment (nebula) geo and planet poster, instead of one generic starfield everywhere; then (B) an OPTIONAL, non-native "glimpse" of the destination sector's sky inside each aleph's funnel centre; with a `Sky: Native | HD` switch seam for P577

## Why this task exists

**Domingo** loved the native aleph after P562/P568 ("feels like you are
getting a peek to the other side") and asked to scope Claude's idea: fade
the destination sector's sky faintly into the funnel centre, so you really
glimpse where it leads.

**Prerequisite gap found while scoping: the port has no per-sector sky.**
Every sector draws the same three generic point-star shells
(`src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs:269-285`,
`Rendering/Starfield.cs`). There is no destination sky to glimpse, and
sectors don't look like native.

**Native per-sector sky** (applied on cluster change, `WinTrek/WinTrek.cpp:3622-3640`):
- `m_pimageEnvironment->SetGeo(CreateCopyModeGeo(clusterSite->GetEnvironmentGeo()))`,
  where the environment geo is `SetEnvironmentGeo(m_data.posterName)`
  (`Igc/clusterIGC.cpp:41-42`): the cluster's **posters/environment model**
  (e.g. 221 map Alderan: `posters=globe3`).
- **Planet poster** (`clusterIGC.cpp:44-57`): `planetName` (e.g.
  `nebplnt06bmp`) at the direction from `planetSinLatitude`/`planetLongitude`
  (`sinLat = planetSinLatitude − 0.5`, `lon = planetLongitude·2π`, `x = cos lon ·
  cosLat`, `y = sin lon · cosLat`, `z = sinLat`) with `planetRadius`, through
  `AddPoster` → `m_pposterImage->AddPoster(image, vec, radius)`
  (`WinTrek/trekigc.cpp:30, 243-248`; PosterImage in `effect/`).
- **Stars:** `m_pimageStars->SetCount(pcluster->GetStarSeed(), pcluster->GetNstars())`
  (`StarImage`, created with 500 at `WinTrek.cpp:~3150`; check `effect/` for
  its generation and brightness law).
- Lighting is already per-cluster in the port (P530 `SectorLighting`, the same
  block of native code).
- The map fields are already decoded: `missiondump` prints `seed=`,
  `posters=`, `planet=` per cluster (`tools/missiondump/Program.cs:~96`).

## Scope

**A. Native per-sector sky (default ON: it's native).**
1. Decode/port `StarImage` generation (seeded by `StarSeed`, `nStars`), the
   environment geo from `posterName` (native `.mdl`/texture, via the P566 MDL
   decoder and texture paths), and the planet poster (texture + direction +
   radius) from the cluster data the client already has (check which wire or
   map path carries `ClusterInfo` fields; add only what's missing).
2. Draw them as native does: at infinity (camera-centred, no parallax for
   posters/environment/stars, depth-write off, behind everything), switching on
   sector change (warp transit). Keep the existing parallax starfields only if
   native has an equivalent; otherwise replace them (motion cues come from
   native's own star image; check).
3. Pin all render states (P568 lesson).

**B. Aleph destination glimpse (non-native, OPTIONAL, default OFF, setting +
CLI flag `--aleph-glimpse on`).**
1. For each aleph in view, render the **destination cluster's** sky (A's
   stars, environment and planet, from that cluster's data, oriented as if
   looking through the aleph toward its destination warp) into a small,
   cached render target (low-res, updated rarely or once per destination; the
   sky is static).
2. Composite it into the funnel centre: a soft radial mask inside the inner
   funnel, faint (e.g. 15–35% at the centre, fading to 0 before the funnel
   wall), under the counter-rotating f02 star core, with the P562 flow intact.
   Slight swirl distortion along the funnel flow is optional.
3. Bound the cost: max N alephs glimpsed (nearest first), with skip and LOD by
   on-screen size.


**A0. Study native first (required).** Native's sky is a
   **library, not per-location art**: 10 environments `globe1…10.mdl` (a ~3 KB
   `MeshGeo` sky dome, `model environmentNbmp`, textured with
   `environmentNbmp.mdl`, ~8 MB ≈ 2048² at 16 bpp) and 43 planets
   `nebplntNNbmp.mdl` (same size/format); maps pick entries per cluster (221:
   6 environments, 9 planets across 13 sectors). Document the dome geometry
   (decode it with the P566 MDL decoder), UV/projection, draw order, blending,
   and how planets are sized/placed (`PosterImage`, `effect/`). Produce
   **contact sheets of all 10 environments and all 43 planets** (they are
   Domingo's reference and the basis for C's mapping).

The HD sky tier is **P577** (next task): build the `Sky: Native | HD` setting
and `--sky native|hd` flag here with only Native implemented, as the seam P577
plugs into.

## Proof (real window: P572 tooling)

- A: real-window screenshots of 4 different sectors on 221 (e.g. Ganymede,
  Organia, Alderan, Rigel) showing different skies and the planet, matching the
  map data; the same views from native, if Domingo can supply them, are optional.
- B: the Rigel aleph in Alderan at 3 distances with glimpse off/on, plus a frame
  strip; cost in ms/frame with 1/3/6 alephs on screen.
- Unit tests: the planet direction maths vs the native formula, star seeding
  determinism, the glimpse setting defaulting to OFF.
- Report `docs/reports/P576_SECTOR_SKY_AND_ALEPH_GLIMPSE.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
