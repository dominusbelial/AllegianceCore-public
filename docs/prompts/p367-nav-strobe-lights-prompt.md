# P367 — Ship/station navigation strobe lights (native `LightsGeo`)

## Why this task exists

Domingo remembered real blinking lights on ships from original-game
video. An earlier investigation this session wrongly concluded "not a
real feature" from a shallow grep (wrong search terms, only checked
one hull's hardpoints, never checked textures). Domingo pushed back
("i just literally saw it on a video" / "yeah painted but it does turn
on and off like regular nav lights") and a proper re-investigation
found a genuine, widely-used native system — correct this port's real
gap.

**Confirmed real native mechanic** (`~/Development/Allegiance/src/effect/efapp.cpp:114-286`,
class `LightsGeo`):
- At model-compile time, any mesh group whose name contains the
  substring `SS` (the SAME hardpoint/group-name convention this port
  already uses for `thrust`/`smoke`/`rocket` — see P358/P365) gets
  registered as a strobe light via `AddLight`.
- Each light has real per-instance timing parsed straight out of the
  group name string (`ReadStrobe`, line 474): `period`, `phase`,
  `rampUp`, `hold`, `rampDown` — i.e. this is NOT a single global blink
  rate, different lights on the same hull can have different timing
  (this is likely how native gets red/green nav-light-style
  alternating-side blinking, verify this directly by reading
  `ReadStrobe`'s exact string format before assuming).
- At render time (`LightsGeo::Render`, line 171) each light computes a
  triangular ramp-up → hold → ramp-down pulse envelope from elapsed
  time, then draws a tinted decal sprite sized `0..0.5` by that pulse
  value — a real dynamic on/off/fade, not a static glow. The decal
  texture is `AWF_EFFECT_STROBE` = `"f101bmp"` (`effect/efart.h:14`).
- `LightsGeo::Write` (line 270) serializes this into the compiled
  `.mdl` binary as a `"LightsGeo"` block of raw `LightData` structs —
  confirmed present in **422 real shipped `.mdl` files** in
  `~/Development/Artwork` (ships AND stations), via `grep -la
  "LightsGeo" *.mdl`. This is common, not a rare/unused effect.

**Separately, real painted texture detail** (found by decoding raw
pixel data directly from `quizfig1bmp.mdl`/`quizfig2bmp.mdl`): hulls
have a painted metal lens/bezel housing with a glowing-green center —
the "socket" the strobe decal renders on top of at runtime. This is
baked hull art, complementary to (not a substitute for) the coded
blink.

**Port status, confirmed**: zero `LightsGeo`/strobe/hardpoint-blink
concept anywhere in `~/Development/AllegianceCore` (checked
`Rendering/`, model-loading code). This is a real, clean gap.

## Scope

1. **Read `ReadStrobe` and `LightsGeo::Render`/`AddLight` in full**
   (`effect/efapp.cpp`) to get the exact timing-parameter format and
   pulse-envelope math right — port the real math, not an
   approximation. Confirm whether/how per-light color is determined
   (a single tint, or something read from the group name / texture
   too) before assuming a single hardcoded color.
2. **Determine how to source real per-hull light positions for this
   port's actual hull assets.** This port's hulls come from the
   Meshy-regeneration pipeline (FBX, not native `.mdl` compiled
   models), so the native `SS`-named-group convention won't be present
   in the regenerated meshes directly. Check what P358/P365 (engine
   trail / nozzle glow — same "need real per-hull hardpoint data from
   a non-native mesh source" problem) landed on for sourcing hardpoint
   positions, and reuse the same approach for consistency if
   applicable (read their report docs, `P358_SHIP_ENGINE_TRAIL.md` and
   `P365_ENGINE_NOZZLE_GLOW.md`, once they exist — they may already
   have solved "no native hardpoints for regenerated meshes" in a way
   this task can copy). If neither has landed yet or their approach
   doesn't fit, a reasonable documented approximation (e.g. wingtip/
   nose/tail anchor points derived from the hull's bounding geometry)
   is acceptable — state clearly which approach was used and why.
3. **Build the strobe decal rendering**: a small tinted sprite/decal
   at each real (or approximated) light position, driven by the real
   ramp-up/hold/ramp-down pulse envelope math from native, not a naive
   sine blink. Find or create a suitable decal texture (check if
   anything from P365's glow work is reusable before making a new
   asset).
4. Confirm it renders for any visible ship/station (not just the local
   player's own), matching native's confirmation that this applies to
   both ships and stations.

## Explicitly out of scope

- P358 (engine exhaust particles) / P365 (engine nozzle glow) — both
  already scoped/in-flight, different hardpoints, different native
  systems (`AddThrustParticles`/`RenderGlow`, not `LightsGeo`). Don't
  touch their files except to read their report docs for the hardpoint-
  sourcing precedent (scope item 2).
- Recreating the exact native lens/bezel painted texture detail on
  every hull — that's a Meshy-regeneration/art-asset question, not
  something to build as new rendering code in this task. Note it in
  the report as a follow-up if it seems separately worth flagging, but
  don't attempt a texture-art fix here.
- P359-P366 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real client, observe a real ship (and a
  real station, if one exists in the test scene) at rest and moving,
  confirm a real blinking/pulsing light is visible at a real hardpoint
  position, with a real ramp-up/hold/ramp-down envelope (not an
  instant on/off toggle — describe the actual observed pulse shape in
  the report). Confirm it renders for a remote ship too, not just the
  local player's own.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The strobe light effect, committed on `master` (not pushed —
   Domingo pushes). Do not create a side branch.
2. Report doc `P367_NAV_STROBE_LIGHTS.md` in the repo root: the real
   ReadStrobe timing-parameter format and pulse math as ported, the
   hardpoint-position approach used (and why), and the live
   verification performed (describe the actual pulse shape observed).
