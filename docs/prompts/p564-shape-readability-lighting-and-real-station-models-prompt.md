# P564 — shape readability: everything (ships, stations) reads as a flat silhouette under the P530 lighting; measure it, restore shape cues the native-respecting way, and replace the placeholder station mesh with the real native station models

## Why this task exists

**Domingo (2026-10-06, live on 221, client `cd90c86a`, soft particles on):**
"the homebase is visible but the details that revealed the shapes are hidden,
so now you dont know it's a donut and a cone … but that is happening with
ships too … everything".

**Findings (Claude):**
- **Lighting since P530** (`c2… 585e8402`, `docs/reports/P530_SECTOR_LIGHTING_ALL_OBJECTS.md`):
  native per-sector **bidirectional** light (`WinTrek.cpp:3614-3640, 4928-4937`):
  key colour on one side, constant blue alt light `(0,0.5,1)` on the opposite
  side, ambient 0.25, no specular (`Allegiance.Core/Rendering/SectorLighting.cs`,
  `Rendering/SectorLightingEffect.cs`). The sign convention is correct
  (`engine/device3d.cpp:~1980` `dot(normal, m_vecLight)`, P529/P530). Native
  relies on **texture art** (painted panel detail, baked shading) for shape;
  the bidirectional rig lights both sides.
- P530 **removed P355's camera-relative headlight** (the light that gave the
  visible faces contrast) and set **`ShipRenderer.ShipAmbientFloor = (0.50,
  0.52, 0.56)`** for ships, 2× native ambient, so dark Meshy hulls wouldn't go
  black. **A high ambient compresses shading contrast**; together with the
  low-detail Meshy textures, ships read as flat.
- **Stations are a placeholder:** `Rendering/StationRenderer.cs` +
  `StationMeshFactory` draw a procedural hub+ring with a single flat side tint
  and **no texture** ("deliberately enough for this temporary placeholder").
  Under one key light, a single-colour object loses all shape cues. Native
  station types carry real models: PCore001 Garrison `ss24a` (types 10/15),
  `ss27` (110/115), `ss300` (310/315); Outposts `ss92`, `ss90`, `ss101`,
  `ss301`, `ss98`, … (`DataStationTypeIGC.ModelName`), all in
  `~/Development/Artwork` (`.mdl` + `bmp.mdl` textures).

> **Ordering note (2026-10-06):** P566 runs BEFORE this task and switches ships to the
> **native `.mdl` meshes + native textures** by default (`--ships native`). Measure and
> tune readability for the **native** ships first (the Meshy path is secondary), and reuse
> P566's native mesh pipeline for the station models.

## Scope

1. **Measure first (a readability metric):** for a ship (2–3 hulls, incl. the
   Adv Fighter and a dark-liveried one), a station and an asteroid, rendered in
   the real client's lighting from front/side/back and in the
   brightest/darkest sectors, compute **per-object shading contrast** (e.g.
   the std-dev and the 10th–90th percentile spread of luminance over the
   object's pixels, plus a normal-vs-brightness correlation). Compare:
   (a) pre-P530 stock rig, (b) P530 today, (c) candidate fixes below.
   Report the numbers and contact sheets.
2. **Restore shape cues, native-respecting:** evaluate and pick (with the
   numbers), documenting why:
   - lowering `ShipAmbientFloor` toward native 0.25 while keeping hulls out of
     black (e.g. ambient floor by luminance, or a hemispheric ambient
     (sky/ground) instead of a flat one);
   - the alt/back light's intensity vs native (verify native's actual alt
     colour/intensity and whether the alt light is per-vertex
     `max(−dot,0)`);
   - a **subtle camera-relative fill** (the P355 idea, documented as a port
     deviation for non-native art) only if the metric shows it's needed;
   - per-pixel lighting instead of per-vertex where the mesh is coarse.
   The native path (`--fx native` / the lighting rig) stays exactly native when
   using native art.
3. **Real native station models:** replace the placeholder hub+ring with
   each station type's native `ModelName` model, decoded from the `.mdl`
   (+ texture) through the existing `.mdl` mesh/texture pipeline the port uses
   for rocks/ships/missiles, at native scale/orientation, lit by the sector
   light. Side tint only if native tints stations (check), otherwise none.
   Keep the placeholder as a logged fallback for missing art.
4. Keep P559 soft particles, P546/P547 VFX and the `--fx/--soft/--bloom`
   switches working.

5. **Per-ship transparency / missing parts** (Domingo: "some parts of the mesh
   are kind of transparent or invisible … depends on the ship"). Not a global
   state leak (`ShipRenderer.Draw` pins depth Default + CullNone,
   `ShipRenderer.cs:136-138`), so investigate per hull:
   - **Cloak/translucency:** the ship draw uses `BlendState.AlphaBlend` when
     `translucent` (cloak). The showcase fleets carry `Sig Cloak 1` on Stealth
     Bombers. A translucent draw with depth-write ON hides the hull's own far
     parts (missing pieces instead of a uniform fade). Check native's cloaked
     rendering (fade/alpha, sorting, depth) and match it; draw translucent ships
     after opaque ones, with depth-write off and back-to-front if needed.
   - **Mesh defects:** for **every player-flyable hull of both 221 civs** (not
     only the showcase ones: Adv Fighter, Hvy Interceptor, Adv Scout, Stealth
     Bomber, …; Domingo: "we will have to go hull by hull"), render a contact sheet (front/side/back/top, opaque and cloaked)
     in the real lighting; flag flipped/missing faces or holes; compare with the
     P525/P526 hangar-preview fixes (which fixed "gaps" in the preview path)
     and apply the same fix to in-flight rendering if it was preview-only.
   - Report which hulls were affected and why.

## Explicitly out of scope

- New art (re-texturing Meshy ships), the VFX tuning (P561), alephs (P562).

## Proof

- Failing-first tests: the station renderer resolves the native model for a
  Garrison/Outpost; the chosen lighting parameters (unit-level); the contrast
  metric function.
- The **readability table** (before/after per object/view/sector) and contact
  sheets.
- **Real client screenshots** (window, via the P559 `ALLEGIANCE_AUTOSHOT`
  hook): the home Garrison up close and at 1 km, a fighter dogfight, old vs
  new.
- Per-hull contact sheet (opaque + cloaked) before/after for item 5, **one image per
  hull plus an index page** (`docs/reports/P564_assets/hulls/index.html` or .md)
  so Domingo can skim every hull and point at problems.
- Report `docs/reports/P564_SHAPE_READABILITY.md`.

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
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

- **Visual proof rule:** screenshots must come from the **real client window** (backbuffer,
  e.g. `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture`/offscreen renders alone don't
  count; that gap hid the P547 (invisible geometry) and P562 (near-invisible aleph) regressions.

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
