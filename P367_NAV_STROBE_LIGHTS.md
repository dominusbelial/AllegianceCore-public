# P367 — Ship/station navigation strobe lights (native `LightsGeo`)

Native system ported: `LightsGeo` (`effect/efapp.cpp:99-286`) — the real blinking
nav-light effect native compiles into any ship/station mesh group whose name
contains `SS` (the same hardpoint/group-name convention this port already uses
for `thrust`/`smoke`/`rocket`, cf. P358/P365). At render time each light computes
a ramp-up → hold → ramp-down pulse envelope from elapsed time and draws a tinted
decal sprite sized by that pulse — a genuine dynamic on/off/fade blink, not a
static glow. Confirmed present (via `grep -la "LightsGeo"`) in 422 real shipped
`.mdl` files covering ships **and** stations — a common, real effect this port
was entirely missing. This task closes that gap.

## The real native mechanic, as ported (exact math)

**Timing parameters** — native `LightData` (efapp.cpp:99-112) carries per-light
`period`, `phase`, `rampUp`, `hold`, `rampDown` plus a colour and a position. The
values are parsed out of the mesh-group name at model-compile time by
`GroupGeoCallbackImpl::Execute` (efapp.cpp:531-561): any group whose name
(after the `frm-` prefix is stripped) contains `SS` is registered as a strobe via
`AddLight` (efapp.cpp:524-528), with:

- `Token::ReadStrobe` (efapp.cpp:474-482) reading the first one-or-two numeric
  tokens of the group name as `(phase, period)`, **negating** them (the compiled
  names encode the values as negatives). `rampUp`/`hold`/`rampDown` are never in
  the name; they stay at the defaults.
- Defaults (efapp.cpp:507-511): `period 1.25 s, phase 0, rampUp 0.1 s, hold 0 s,
  rampDown 0.05 s`.
- Colour (efapp.cpp:552-558): the `SS` group's single child material's diffuse
  colour, else white — so lights on one hull can genuinely be different colours,
  and their per-light timing means they can blink out of phase (the real source
  of native's red/green alternating-side nav-light look).

**Pulse envelope** — `LightsGeo::Render` (efapp.cpp:171-222):

```
while (true) { value = time - phase; if (value < period) break; phase += period; }
if (value < rampUp)      value /= rampUp;                      // ramp 0→1
else { value -= rampUp;
       if (value < hold) value = 1;                            // hold at 1
       else { value -= hold;
              if (value < rampDown) value = 1 - value/rampDown; // ramp 1→0
              else value = 0; }}                               // off
if (value > 0) DrawDecal(image, colour, position, ..., 0.5*value, 0);  // scale = 0.5·value
```

The `while` normalizes `time-phase` into `[0, period)` by advancing `phase` whole
periods; the port folds that into one modulo (`NavStrobeMath.Pulse`), which is
numerically identical for all positive times and makes the function stateless and
pure. Crucially native scales the decal's **size** by the pulse (`0.5·value`,
efapp.cpp:215) and never multiplies the colour by it — so a flash GROWS to full,
then SHRINKS, then is not drawn at all while off. The port replicates that exactly
(the pulse gates and sizes the quad; the colour stays full).

## Hardpoint-position approach: (b) geometric approximation — consistent with P358/P365

Like P358 (`ShipEngineTrailParticles`) and P365 (`EngineNozzleGlow`) before it,
option (a) — extracting real per-hardpoint offsets from the native `.mdl` — was
judged not cleanly tractable here: this port renders Meshy-regenerated content
hulls (P287/P351-356), not the native `.mdl` geometry, so the native `SS`-named
groups and their local offsets do not survive into the regenerated meshes (and
the coordinate frames/scale differ per asset, exactly the P358/P365 finding).

Chosen **(b) anchors derived from the real rendered hull geometry** — the same
"faithful approximation when precise native data isn't cleanly portable" precedent
those two stages (and `DamageDebrisParticles` P297/P338) established:

- **Port** (left) wingtip — **red**, at `position + (−right)·(radius·0.95)`.
- **Starboard** (right) wingtip — **green**, at `position + right·(radius·0.95)`.
- **Stern** — **white**, at `position + backward·(radius·0.92)`.

These land on the actually-drawn hull on every hull class, and the
red-left/green-right/white-tail scheme is the universal aviation nav-light
convention, standing in for native's per-group material-derived colour (which
isn't recoverable from regenerated meshes). All lights share native's default
`period`/`rampUp`/`hold`/`rampDown` but carry **distinct phases** (port 0,
starboard half a period, stern a quarter) — reproducing the native
per-light-independent-timing mechanic that produces the alternating red/green
blink, rather than a single in-unison blink.

**Stations** get the same treatment: `StationAnchors` places red/green beacons at
the structure's opposite ends and a white top beacon, all at the real
`DataStationTypeIGC.Radius` (`StationRenderer.DefaultFallbackRadius = 302.5` only
if the type is unresolvable), blinked by the same envelope. This honours native's
confirmed ship+station scope.

**Decal size normalization**: native draws at `0.5·value` in native mesh units;
the port normalizes to real-hull world units (`half = value·radius·
NavStrobeLayout.SizeFactor`, `SizeFactor = 0.095`) — the same documented
normalization P358/P365 apply to their particle/glow sizes. The strobe texture is
the port's standard procedural soft radial sprite (there is no dedicated
`f101bmp`/`AWF_EFFECT_STROBE` asset in this port's content), tinted per-vertex —
the same convention the P365 glow and the particle renderers use.

## Implementation

- `Rendering/NavStrobe.cs` — `NavStrobeTiming` (period/phase/rampUp/hold/rampDown,
  native defaults) + `NavStrobeMath.Pulse` — pure, MonoGame-free, faithful port of
  the `LightsGeo::Render` envelope.
- `Rendering/NavStrobeLayout.cs` — `ShipAnchors`/`StationAnchors` (positions,
  colours, phases) + `SizeFactor`.
- `Rendering/NavStrobeRenderer.cs` — camera-facing additive soft quad per light
  (P339-B2 three-state restore), size = `pulse·radius·SizeFactor`, full colour.
- `Game1.cs` — per-ship `_navStrobeEmitters` gathered under the Gate in the
  pose-gather pass (local hull gated to non-cockpit like P358/P365; every visible
  remote ship); station beacons added in the station draw pass; all drawn once in
  the ship-effects 3D pass right after the P365 nozzle glow, driven by the running
  `_nowSeconds` clock.
- `tests/.../NavStrobeTests.cs` — 10 pure tests: exact envelope values at native
  default timing, the long off-interval of a blink, period wrap, the phase-delay
  mechanic, non-positive-period defence, port/starboard alternation, and the ship/
  station anchor layout. Must-fail proven: temporarily forcing `Pulse` to a
  constant 1 makes the envelope/alternation tests red (they expect 0 mid-period).
- `tools/strobe-proof/` — the reusable live-proof harness (see below).

## Verification — real proof

Evidence: `.verify_p367/frames/` (PNGs + `p367_run.log`). Harness
(`tools/strobe-proof/`, the P358/P365 live-proof pattern): real in-process
`Allegiance.Server` (XXV_simple) + two real `ConnectionSession` peers (ships
8096/8097, same side, real fighter hull Length 42.5 → radius 21.2, at X=±45), the
same `NavStrobeMath.Pulse` envelope Game1 applies, the real anchors, and the real
`NavStrobeRenderer` drawn offscreen (960×640). Results:

- **Real pulse shape (observed, not an instant toggle, not a static glow):** the
  numeric trace shows the port light ramp through intermediate values 0.50 → 1.00
  → 0, the starboard through 0.25 → 0.75 → 0.50 → 0, and the white tail through
  0.38 → 0.88 → 0.25 → 0 — a genuine ramp-up → (zero-length hold) → ramp-down
  envelope, and the light is **fully off in 36/40 (90%) of samples** between
  flashes. The rendered frames confirm a dot that grows to full then vanishes and
  is entirely absent (all-off frame is byte-identical pure black).
- **Alternating red/green sides:** port (red) peak lands at frame #1, starboard
  (green) at frame #38 — DIFFERENT moments, so the two wingtips blink out of
  phase (native's per-light-phase mechanic), not in unison.
- **Remote ship too:** each ship frame shows a dot in BOTH halves — red on the
  local left and remote right at port-peak, green on both at starboard-peak — so
  the strobes render for a remote ship as well as the local hull.
- **At rest AND moving:** Phase A captures the full blink at rest; Phase B drives
  both ships moving (+X) and strobes still render (frames differ from the all-off
  black baseline while flashing, and go back to the identical black baseline when
  all lights are between flashes).
- **Real station:** Phase C pulls the real `Garrison` (station type 15, real
  radius 423.5) from the test scene's wire, places beacons at its real radius, and
  renders a lit beacon as a centred red dot (`station_beacon.png`) — native's
  ship+station scope honoured.
- Vision on the saved frames: `port_peak.png` = two small red dots (left local,
  right remote); `star_peak.png` = two small green dots (same two positions,
  proving the same sockets flash red then green); `all_off.png` = pure black;
  `station_beacon.png` = one centred red dot.

## Build / tests

- Whole-solution build: 0 warnings, 0 errors.
- `Allegiance.ClientCore.MonoGame.Tests`: 708 passed (10 new), twice (708/708).
- `Allegiance.ClientCore.Tests`: 187 passed.
- `Allegiance.Core.Tests`: 3 full runs — only pre-existing documented live-wire
  timing env-flakes, each run a DIFFERENT rotating subset (7, then 5, then 4
  failures: ProjectileBroadcast, LiveTurretGunnerBinding, LiveRipcord,
  LiveMoney, LiveWinCondition, LiveGainFlag, AsteroidMinedLive,
  GameOverPlayersWire, LiveRelaunch) — the documented env-flake signature. This
  change adds zero code to `Allegiance.Core` (it is MonoGame-client-only), and
  `grep` confirms zero of the new `NavStrobe*` symbols exist in
  `Allegiance.Core.Tests`, so none of those failures can be a regression. Each
  flake passes green in isolation (verified, e.g. the Conquest
  GameOverPlayersWire test).

## Scope boundaries (per brief — untouched)

- P358 (engine exhaust particles) / P365 (engine nozzle glow) — separate native
  systems (`AddThrustParticles`/`RenderGlow`, not `LightsGeo`); their files were
  only read for the hardpoint-sourcing precedent.
- The native lens/bezel painted-texture detail on hulls — a Meshy-regeneration /
  art-asset question, not rendering code; noted as a separate follow-up (the
  strobe decal renders on top of whatever the hull shows today).
- P359-P366 — unrelated; untouched.
