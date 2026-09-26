# P348 — Add a per-hull turnaround-image mode to roster-capture (Meshy generation hints)

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Domingo's own words, live 2026-09-22, after a full-roster flight-test
pass across all 72 real P287 ship models: "some of the models are so
terrible i think we should build our own ship renderer tool to produce
a better hint for meshy next month... we can build the image catalogue
while we hit next month, i like the roster capture tool idea." The
goal is **not** urgent gameplay work — it's building the reference
material now (a clean per-hull image catalogue) so a future Meshy
regeneration pass next month has better guidance than a text prompt
alone: a real multi-angle "turnaround" reference image set per hull,
the standard input shape for image-conditioned 3D generation (Meshy's
own multi-image-to-3D flow takes exactly this: a small set of clean,
isolated, consistently-lit views of one subject from canonical angles —
front / right / back / left).

**The existing `tools/roster-capture/` tool (P331) is the right
foundation, not a rewrite** — its own header comment
(`Program.cs:1-27`) already documents reusing "the exact rendering path
the in-game client uses: `HullModelResolver` + `ShipRenderer.Draw` +
compiled P287 content models," and it already has a proven, working
**orthographic** camera convention (`Camera`/`SheetCamera`,
`Program.cs:517-561`, `Matrix.CreateOrthographic` — no perspective
distortion, ideal for reference art) that frames a bounding region and
picks a resolution/pixels-per-unit automatically. Today it only ever
uses ONE fixed elevated 3/4 angle (`eleY: 0.9f, backZ: 1.4f`, hardcoded
at every call site) for grid/roster overviews. This task adds a new
mode that reuses the same rendering pipeline and the same orthographic-
camera math, but orbits a camera around ONE ship at a time at several
fixed canonical angles, instead of framing a whole grid from one angle.

## Scope

1. **New mode**: `roster-capture turnaround [--dump PATH] [--art PATH]
   [--content PATH] [--out DIR]` (mirroring the existing `dump`/`wire`
   mode-selection convention, `Program.cs:52-89`). For every entry in
   `roster_dump.json` (all 72 real distinct hull assets — the same
   source `dump` mode already reads, `Options.DumpPath`), render a
   **4-angle turnaround**: front, right-side (90°), back (180°), left-
   side (270°) — the camera orbiting the ship's own vertical axis at a
   fixed elevation (reuse `Camera`'s own `eleY` convention rather than
   inventing a new elevation scheme), each written as its own separate
   clean image (`<asset>_front.png`, `<asset>_right.png`,
   `<asset>_back.png`, `<asset>_left.png`) — separate files, not one
   composite sheet, matching Meshy's real multi-image input shape (one
   clean image per canonical angle, not a contact sheet the generation
   tool would need to crop itself).
2. **Isolated, consistent framing per ship** — tight crop on the single
   hull (no other ships, no grid), enough margin that nothing clips,
   identical apparent scale/margin across all 4 angles of the SAME
   ship (so the 4 images genuinely read as one consistent turnaround,
   not 4 independently-framed renders) — reuse `Camera`'s own bounding-
   box-driven pixels-per-unit sizing logic, adapted to a single ship's
   real render-radius (see `ShipRenderScale.RenderRadius`,
   `src/Allegiance.ClientCore.MonoGame/Rendering/ShipRenderScale.cs`,
   already used elsewhere in this codebase for exactly this
   "how big does this model really render" question) instead of a
   multi-ship grid's bounds.
3. **Clean/neutral background** — no starfield, no HUD, no grid lines
   (this tool's existing debug grid overlay, `Program.cs:~615-621`, is
   for the cataloguing sheets; skip it here) — a plain, solid neutral
   background is the right choice for an image-generation reference
   input (avoids the generation model reading star clutter as part of
   the subject). Pick a real solid color (not pure black/white — something
   that won't be confused with a real hull surface color, e.g. a
   mid-grey or a saturated key color) and state your choice plainly in
   the final report.
4. **Consistent lighting across all 4 angles AND across all 72 hulls**
   — reuse the exact `ShipRenderer.Draw`/`ShipAmbient` lighting this
   codebase already settled on (`ShipRenderer.cs:48-52`,
   `ShipAmbient = (0.32, 0.34, 0.36)`) rather than inventing new
   lighting for this mode; the point is a fair, comparable hint image
   per hull, not a dramatic showcase render.
5. **A manifest**: write `turnaround_manifest.json` (or `.md`, your
   call) listing every hull processed with its asset name, civ, class,
   and the 4 output file paths — so a future task/human can iterate the
   catalogue programmatically without re-deriving the roster.
6. **Output location**: a new directory, not mixed into the existing
   `docs/P305_roster/` cataloguing output — propose and use something
   like `docs/meshy_hints/<asset>/front.png` etc. (nested per-asset) or
   a flat `docs/meshy_hints/<asset>_front.png` naming — either is fine,
   just be consistent and state which you picked.

## Explicitly out of scope this task

- **Actually calling Meshy or regenerating any model** — this task
  produces the reference IMAGES only; using them to regenerate anything
  is real future work ("next month"), not this task.
- **A 5th/back-3-quarter/top angle** or any richer turnaround than the
  4 canonical front/right/back/left views — the standard 4-view set is
  the explicit ask; don't over-build.
- **Touching the existing `dump`/`wire` modes** — this is a pure
  addition; the existing cataloguing output (`docs/P305_roster/*.jpg`)
  must be byte-identical before/after (don't even reformat their code
  if you're in the neighborhood — diff-minimal).
- **Fixing the actual "terrible" model quality itself** — that's
  precisely what this catalogue is future input for; this task doesn't
  touch `Rendering/`'s model-loading, `ShipRenderer`, or any of the
  P333/P336/P337 scale/texture work — reuse it as-is.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Real output, required**: run `roster-capture turnaround` against a
  real `roster_dump.json` (regenerate one via `vizfight roster <port>`
  first if the existing one is stale) and confirm all 72×4 = 288 images
  exist, are non-trivial file sizes (not blank), and — pick at least 6
  hulls spanning small/large/different civs and actually LOOK at their
  4-image sets yourself (read them as images, the same way this
  session verified `roster_all.jpg`/`ic-scout.png` directly) to confirm
  they're legible, consistently framed, and genuinely show 4 distinct
  angles of the same ship, not 4 near-identical renders (a real
  orbit-angle bug would be easy to miss without actually looking).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`) — this is a
  tool-only change with no test project of its own currently
  (`tools/roster-capture/` has no accompanying test project); running
  the suite is just to confirm no accidental regression elsewhere. Run
  once; the pre-existing live-wire timing flakes are expected and
  unrelated.

## Deliverables

1. The new mode, committed on `master` (not pushed — Domingo pushes).
   Do not create a side branch.
2. A short final summary: the background color chosen, the output path
   convention chosen, confirmation of the real 72×4 output + the 6-hull
   visual spot-check performed (name which hulls, what you saw), and
   build status.
