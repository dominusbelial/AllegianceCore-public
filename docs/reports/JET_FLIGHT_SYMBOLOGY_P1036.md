# P1036 — Jet HUD skin, story 2/9: flight symbology (FPM, pitch ladder, heading tape, speed + throttle tapes)

Implemented in the `hermes/p1036` worktree of `AllegianceCore`, base commit `0783d747`. No commit, no push, no
stash, no remote host, no Docker (not installed here). Raw captures (461 MB each) and the fast-tier logs live in
`~/allegiance-evidence/P1036/` (outside the repo); the report's assets are the 18 files under
`docs/reports/JET_FLIGHT_SYMBOLOGY_P1036_assets/` (1.7 MB, inside the 2 MB task cap).

This story is P972 §4.2 / phase 3A: the Jet skin's core flight instruments — the round flight-path marker
(FPM), the sector-plane pitch ladder, the heading tape and the moving speed/throttle tapes — on top of P1035's
foundation (N-skin registry, `hud.jetSkin` flag default OFF, `jet.skin.json` palette, `JetVectorPainter`,
gun cross + boxed readouts + WIP tag). Jet remains presentation-only: every number comes from the shared
`HudModel`/`HudFrame` or from a snapshot the model adapter builds out of the pose the client already tracks.

## 1. Zero-delta baseline first, then the comparison

The **unmodified** tree was built (`tools/hud2-proof`, Release) and captured **before any edit**:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1036/baseline
HUD2_CAPTURE_PASS 1848 combinations in 6.0 min -> ~/allegiance-evidence/P1036/baseline
HUD2_MANIFEST 1848 combinations, commit 0783d74753c4+dirty, driver llvmpipe (LLVM 19.1.7, 256 bits)
```

The P980/P988 matrix is unchanged: **22 scenarios × 7 resolutions (1920×1080, 2560×1440, 1280×720, 1600×900,
1366×768, 800×480, 3840×2160) × 3 sizes (0.75/1/1.5) × 2 backdrops (dark/bright) × 2 skins = 1,848
combinations** (924 Classic + 924 Modern), decoded-RGBA goldens plus a semantic element/visibility snapshot per
combination. After the change the same tree was captured again and compared:

```
HUD2_CAPTURE_PASS 1848 combinations in 5.8 min -> ~/allegiance-evidence/P1036/after
HUD2_MANIFEST 1848 combinations, commit 0783d74753c4+dirty, driver llvmpipe (LLVM 19.1.7, 256 bits)
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)
```

The only differences are the pixels inside the fixture's recorded animation mask (the wall-clock low-resource
`!` badge at `ModernHudRenderer.cs:166`, documented in P980 §3 — 83 of the 1,848 entries on this run, 84 on the
next one: the mask is taken from the badge's measured box and the badge pixel phase is wall-clock timing), and
every entry reports `outside: 0`. **Classic and Modern are pixel-identical (decoded RGBA) and
semantically identical: 1,848/1,848 combinations, 0 pixel diffs, 0 semantic diffs.** The new
`HudContext.FlightReference` field is therefore proven invisible to both existing skins.

## 2. Changes

| File:line | Change |
|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/HudFlightReference.cs:1` | **New** presentation-only snapshot: own (source) ship attitude `Quaternion`, `Velocity`, `SectorPlaneNormal`; `Forward`/`Up`/`Right` derived from the quaternion, `HasAttitude` rejects a stale/zero quaternion, `FromSim(Orientation, Vector3, planeNormal)` converts the sim basis once through `PoseConverter.ToXnaRotation`. `SectorUp` is world +Z. |
| `src/Allegiance.ClientCore.Game/Rendering/HudModel.cs:203` | `HudContext.FlightReference` (`HudFlightReference?`, null when unavailable). Nothing in Classic/Modern reads it. |
| `src/Allegiance.ClientCore.Game/Rendering/HudModelAdapter.cs:26,40` | Optional `flightReference` parameter, assigned into the context — the adapter is where the snapshot enters the model. |
| `src/Allegiance.ClientCore.Game/Game1.cs:1291` | `_flightReference` field staged per frame beside `_velocityMarker`. |
| `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:372,552,3554` | Reset `_flightReference = null` with the other per-frame descriptors, stage `_flightReference = ComputeFlightReference()` at the same frame boundary as the velocity marker, and pass it to `HudModelAdapter.Create`. |
| `src/Allegiance.ClientCore.Game/Game1.Camera.cs:841` | `ComputeFlightReference()` — builds the snapshot from the same `TrySourcePose` (own source ship; the parent pose when boarded, native `GetSourceShip()`), sector normal `HudFlightReference.SectorUp`. |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetFlightGeometry.cs:88` | **New** pure, allocation-free math: `JetAttitude` (pitch/roll/heading + validity), `JetAttitudeState` (holds the last valid heading/roll through the vertical singularity), quaternion `Attitude`, `RoundedSpeed`, `FlightPathMarkerVisible` (native gate), `ThrottlePercent`, `WrapHeading`, `LadderOffset`, off-scale helpers, tape scales/tick counts. |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.cs:41` | `Coverage` grows to the five ids Jet draws: `Velocity direction marker`, `Roll tutorial cue`, `Speed`, `Thrust setting`, `Cockpit centre reticle`. |
| `.../JetHudRenderer.cs:160-206` | `Draw`: attitude resolved once per instrument frame, heading tape + ladder, gun cross, FPM (native gate), speed + throttle tapes, tag. |
| `.../JetHudRenderer.cs:270` | `DrawFlightPathMarker` — circle + two wings + tail at `HudModel.Context.VelocityMarker`. |
| `.../JetHudRenderer.cs:293` | `DrawPitchLadder` — 10° labelled rungs, 5° unlabelled ticks at ≥1080p, dashed below the plane, horizontal labels, off-scale chevrons, rolled by bank, boresight rung skipped. |
| `.../JetHudRenderer.cs:387` | `DrawHeadingTape` — 10° ticks, 20° marks, caret, boxed `HDG nnn` / `HDG --`; no tape inside the vertical cone. |
| `.../JetHudRenderer.cs:453` | `DrawSpeedTape` — moving 20 mps ticks / 40 mps labels + boxed numeric, left edge. |
| `.../JetHudRenderer.cs:500` | `DrawThrottleTape` — moving 10 % ticks / 20 % labels, boxed percent, doubled 50 % idle detent, dashed reverse rail + `REV`/`IDLE` (1080p+ only). |
| `.../JetHudRenderer.cs:573` | `DrawReadout` — one shared boxed-numeric plate (centre/left/right anchored) with a keyline tick facing its tape. |
| `.../JetHudRenderer.cs:154,158` | `Attitude` (proof/test readout) and `ResetFlightState()` (proof fixtures). |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetVectorPainter.cs:100,140,155` | Review follow-up: `MinimumLineStroke = 1.5` physical px for ink lines and `LineKeylineAllowance(width) = min(1 px, width/2)` used by `Line`/`Polyline`/`CornerBrackets`, so the ink is always the dominant stroke. |
| `tools/hud2-proof/Program.cs:344-440` | `--jet` scenes extended to the four flight cases (level, pitched+rolled, near-vertical, reverse throttle) at 1920×1080 and 800×480, dark and bright, plus the P1035 radar/lifepod/docked/command gating scenes; the JSON now records pitch/roll/heading validity, throttle percent, FPM gate and stroke px. |
| `tools/hud2-proof/Program.cs:1314-1327,1483` | `Scenario` gains optional `flightReference`/`speed`/`throttle`/`afterburner` (the matrix calls keep every default) and the deterministic `FlightReference(pitch, roll, heading)` fixture. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetFlightSymbologyTests.cs:1` | **New** 62-test suite (§4). |
| `tests/.../Skins/JetSkinScaffoldTests.cs:366-389,404-412` | Coverage assertions updated to the five ids; the stroke-floor assertion now pins `JetVectorPainter.MinimumLineStroke` (the intentional P1036 ink floor). No behaviour assertion was relaxed. |
| `GLOSSARY.md:999` | `HudFlightReference` / `JetFlightGeometry` / FPM / tapes entry. |

Jet-only code changed after this point (REV label position) does not touch Classic/Modern; the zero-delta
comparison below was rendered from the same shared code.

### Design decisions

* **Sector reference plane = native command-grid plane.** Native builds the command view's grid in the world XY
  plane at `zGrid = 0` (`WinTrek/WinTrek.cpp:3099` — `new CommandGeo(c_fCommandGridRadius, 0.0f, 40)`;
  `WinTrek/cmdview.cpp:17-115` places every vertex on `zGrid`) and draws each contact's drop line down to that
  plane, colouring it by `vecPosition.Z() > m_zGrid` (`WinTrek/cmdview.cpp:164`). The port keeps that geometry
  (`CommandGridRenderer.cs:35-49`), and native's own `Roll` helper confirms the same up axis with
  `o.GetUp().z; //Dot product of the up vector with (0,0,1)` (`Igc/common.cpp:2073`). That plane — normal +Z —
  is the only attitude reference Jet uses; there is no planetary horizon, altitude, G or Mach anywhere in the
  feature.
* **Quaternion snapshot, built once.** `HudFlightReference.FromSim` converts the sim basis (rows = right, up,
  backward) through the existing `PoseConverter.ToXnaRotation` and `Quaternion.CreateFromRotationMatrix`. The
  geometry then derives forward/up/right by transforming the unit axes, so the renderer never reads the world,
  never touches the sim, and costs one 3×3→quaternion conversion per frame outside the draw.
* **Pitch/heading definition.** Pitch = `asin(forward · planeNormal)`, positive when the nose is above the
  plane (±90 = straight up/down). Heading = the planar component's angle, 0° = world +X and increasing toward
  +Y, wrapped into 0…<360 — presentation navigation only.
* **Roll definition and ladder rotation.** Roll is the signed angle from the level-up axis (plane normal made
  perpendicular to forward) to the ship's up about forward, positive = right wing down. The ladder is rotated
  by −roll on screen, i.e. the world reference rotates counter-clockwise in the pilot's view when the ship banks
  right. `Orientation.PostRotate(forward, +θ)` in the fixtures recovers `roll = +θ`, which the tests pin.
* **Vertical singularity.** `|forward · normal| ≥ 0.99985` (≈1° from ±90°) marks heading and roll invalid.
  `JetAttitudeState` then returns the last valid values (never a random jump) and the heading readout becomes
  `HDG --` with no tape; the state resumes real values as soon as the ship leaves the cone. Pitch itself stays
  defined (±90). Tested at ±89.9°, ±90°, hold-and-recover.
* **FPM gate is native and shared.** Native `RenderDirectionIndicator` (`WinTrek/indicator.cpp:128-150`):
  `myVelocity` at `:130`, `int speed = (int)(myVelocity.Length() + 0.5f)` at `:131`, cockpit camera at `:132`,
  `speed != 0` at `:138`, `TransformDirectionToImage` at `:140`, `rect.TransformNDCToImage` at `:142`,
  `rect.Inside` at `:144`, `DrawImage` at `:145`. Jet does not re-project: it draws at the port's existing
  `HudModel.Context.VelocityMarker`, which `Game1.Camera.ComputeVelocityMarker` fills with that exact gate
  (`Game1.Camera.cs:823-830`) and which Classic draws. `JetFlightGeometry.FlightPathMarkerVisible` restates the
  same rule, and a test sweeps the speed axis asserting the model gate and the geometry gate agree on every
  sample — so Jet and Classic cannot disagree about where/whether the marker exists.
* **Never a stale snapshot.** `_flightReference` is cleared at the top of `Draw` with `_velocityMarker` and the
  other per-frame descriptors (`Game1.Rendering.cs:372`), so a frame that skips the world branch (hangar/command
  without a ship) cannot hand the HUD the previous frame's attitude; `ComputeFlightReference()` returns null when
  `TrySourcePose` has no own-ship pose (before the first update, no local entity). On top of that the renderer's
  instrument gate keeps ladder/heading/tapes off in docked/dead/command frames.
* **Instrument gates.** Ladder, heading and tapes follow `HudModel.InstrumentsVisible` (flight, not
  docked/dead/command) — the same group Classic hides there — while the gun cross and FPM keep
  `ReticleVisible`/`VelocityMarkerVisible` (cockpit + native indicator cameras). A lifepod keeps the tapes (pod
  speed is the same `HudFrame.Speed` field) and shows `HDG --` because the fixture supplies no attitude.
* **Tapes replace the P1035 boxes.** Left speed tape: 20 mps ticks, 40 mps labels, boxed numeric on the
  boresight row. Right throttle tape: the shared model's `(throttle+1)/2` fraction (`HudModel.cs:421`, native
  `jsValues[c_axisThrottle]*0.5+0.5` — `WinTrek/WinTrek.cpp:8458-8466`) as 0…100 %, 10 % ticks, 20 % labels, a
  doubled detent at the 50 % idle mark and a dashed rail through the 0…50 % reverse band with `IDLE`/`REV`
  words at 1080p+ (the 800×480 floor keeps only the strokes, P972 §4.5). Afterburner/movement-button and turret
  overrides are untouched: they happen in `HudModel` (`HudModel.cs:385-388`), so a latched afterburner reads
  100 % and a turret's forced −1 reads 0 %.
* **Ladder density.** Labels stay on the 10° rungs; the optional 5° refinement (P972 §4.2) is drawn as short
  unlabelled dim ticks at ≥1080p. At 800×480 the span shrinks to ±20° and the 5° ticks are dropped, so the
  ladder stays uncluttered on the small window. The rung nearest the current pitch is skipped so it cannot
  cover the gun cross or the flight-path marker; the pitch numeral below the ladder carries that value.
* **Ink-dominant stroke (P1035 review follow-up).** Two changes: the physical line floor rises to 1.5 px
  (`MinimumLineStroke`; a 1 px quad centred on a fractional row splits ~50/50 across two rows and reads dark
  green inside the keyline), and the keyline allowance per side is capped at min(1 px, ink/2), so at 800×480 the
  cross section is 1 dark px + 2 ink px. Measured on the bright 800×480 capture: **67 % ink / 33 % dark on every
  gun-cross arm** (§5), and the vision read-back now reports bright green strokes with a thin dark edge. Text
  keeps the full 1 px keyline (glyph strokes are denser than a hairline).
* **Zero allocations.** The tick labels are pre-formatted tables (heading/throttle) or a 16-slot ring keyed by
  the visible label ordinal (speed); the heading/pitch/speed/throttle numerals are cached by integer value. The
  new 0 B/frame test covers the full symbology (ladder + heading tape + both tapes + FPM) for 100,000 frames.

## 3. Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetFlightSymbologyTests.cs` (62 tests):

* geometry: level (0/0/45), pitched ±30, rolled ±30/±90/±180 (roll round-trip), inverted flight, heading wrap
  359→0 / −1→359 / 360→0 / 720.25→0.25, sector-plane bearing (0°=+X, 90°=+Y), near-vertical ±89.9 and exact
  ±90 (pitch valid, heading/roll invalid), the state holder's hold-and-recover, null/degenerate reference;
* FPM gate: native truncated rounding (0.2/0.49 → 0, 0.5 → 1), camera/zero-speed/behind-camera cases, and a
  speed sweep asserting `JetFlightGeometry.FlightPathMarkerVisible` equals `HudModel.VelocityMarkerVisible`;
* throttle: axis → percent for −1/0/+1/±0.6/0.5, plus the shared model's afterburner latch (100 %) and turret
  forced −1 (0 %) and idle = 50 %;
* ladder math: rung above/below the boresight, rotation under a 90° bank, off-scale chevrons only past ±80°,
  the compact span;
* renderer: the five coverage ids; `HDG nnn` boxed text and `HDG --` for both no-reference and vertical;
  instrument gate (command/docked/dead draw no tapes/ladder/heading, lifepod keeps them with pod speed);
  FPM appears only with the native gate (0 speed / chase don't draw it); reverse throttle draws 20 % plus
  `REV`/`IDLE` at 1080p and neither word at 800×480;
* ink dominance at scale 0.25/1/4 (`LineKeylineAllowance ≤ ink/2`, ink ≥ 1 px, dark submitted first) and the
  800×480 numbers;
* **0 B/frame** `JetHudRenderer.Draw` with the full symbology (prints
  `P1036 JetHudRenderer.Draw (ladder+tapes+FPM): 0 B / 100000 frames = 0 B/frame`);
* draw smoke for 12 states: cockpit, chase, command, docked, lifepod, dead, ghost, turret, training, hidden,
  no-telemetry, no-reference — no throw, shared passes exactly once each in the Classic composition, and no
  fabricated ladder numeral without a reference.

All existing skin/HUD tests stay green; the only edits to existing tests are the two coverage assertions in
`JetSkinScaffoldTests` (the coverage set intentionally grew) and the stroke-floor literal (`Stroke` is now
floored at 1.5 px on purpose).

```
run 1: Passed 62, Failed 0, Skipped 0 (504 ms)
run 2: Passed 62, Failed 0, Skipped 0 (495 ms)
run 3: Passed 62, Failed 0, Skipped 0 (507 ms)
```

The 0 B/frame test prints
`P1036 JetHudRenderer.Draw (ladder+tapes+FPM): 0 B / 100000 frames = 0 B/frame`, and the ink test prints the
measured strokes at scale 0.25/1/4 (`dark 3.00 / 3.00 / 12.00 px` around `ink 1.50 / 1.50 / 6.00 px`). Nothing
here exceeds 10 s, so no `[Trait("Tier","Live")]` is needed and all 62 run in the fast tier. `JetSkinScaffoldTests`
(45, updated coverage/stroke literals) also ran green here.

## 4. Screenshots

`tools/hud2-proof --jet <dir>` at the two required resolutions, dark and bright
(`docs/reports/JET_FLIGHT_SYMBOLOGY_P1036_assets/`, 15 Jet PNGs + `jet-capture.json` + the Classic/Modern pair =
18 files, 1.7 MB total). The fixture's flight scene drives `Reference(pitch, roll, heading)` through the sim's
own `Orientation.PostRotate`, so each scene's attitude is the exact quaternion the renderer reports back (the
JSON records it: pitch/roll/heading, validity, throttle percent, FPM gate). Every cited image was opened and
read; the pixel measurements in §5 corroborate, they do not replace the read.

| Image (in `JET_FLIGHT_SYMBOLOGY_P1036_assets/`) | What is actually visible |
|---|---|
| `jet-level-1920x1080-dark.png` | Heading tape `020 040 060 080` with the caret at **HDG 045** (heading 045°, pitch 0°, roll 0°); ladder rungs `+40 +30 +20 +10` solid, `-10 -20 -30` dashed below the plane, with the shorter 5° ticks between them and horizontal numerals on both ends; the broken gun cross at the centre; the round flight-path marker (circle, two side wings, top tail) 60 px left/20 px below the cross; left tape `SPD / MPS` with 120…360 labels and a boxed **260**; right tape `THR` with `020 040 060 080 100`, a boxed **80%**, the doubled **IDLE** detent at 50 % and the dashed reverse rail with **REV**; bottom `JET / GREEN - WIP`. No clipping. |
| `jet-level-1920x1080-bright.png` | Same frame on the light backdrop: every stroke and label is legible; the gun cross reads **green** with a thin dark edge (measured 67 % ink in §5); HDG 045, 260, 80 %, IDLE/REV all readable. |
| `jet-level-800x480-dark.png` | The 800×480 composition: heading tape `340 000 020 040 060 080 100` with HDG 045; compact ladder (no 5° ticks) labelled `+20 +10` solid and `-10 -20` dashed, with the `00` pitch numeral below it; shorter cross and FPM; tapes at the edges with 260 and 80 %; nothing clipped at the borders. |
| `jet-level-800x480-bright.png` | Same at the small window on bright: green ink stays dominant over the dark keyline, all numbers readable, no overlap with the edge tapes. |
| `jet-pitched-1920x1080-dark.png` | Pitched **+18°**, banked **−25°**, heading **HDG 210**. The rungs `+50 +40 +30 +10 -10 -20` (and the rest of the ±40° span) are rotated by the bank: the fitted slope of the +10 rung is **26.1°** against the expected 25°, i.e. the ladder really rolls with the aircraft while its numerals stay horizontal. Speed 320, throttle 100 %, IDLE/REV on the tape. |
| `jet-pitched-800x480-dark.png` | Compact version of the same attitude: fewer rungs (±20° span), tilted ladder, HDG 210, 320, 100 %. |
| `jet-vertical-1920x1080-dark.png` | Near-vertical: **89.9° pitch**, heading/roll invalid on this fresh scene → the box reads **HDG --** and no heading tape is drawn; two rungs sit below the boresight (the 70°/80° marks) and one off-scale **chevron** sits above the ladder; tapes 180 and 65 %. |
| `jet-vertical-800x480-dark.png` | The compact near-vertical frame: HDG --, two rungs and the chevron, tapes 180 / 65 %. |
| `jet-reverse-1920x1080-dark.png` | Pitch −10°, roll +12°, heading **HDG 090**. Throttle **20 %** (axis −0.6): the boxed 20 % sits below the doubled **IDLE** detent at 50 %, the dashed reverse rail runs through the 0…50 % band and **REV** marks the 25 % row; speed 40 on the left tape. The rungs below the plane (−20 and lower) are dashed; the rung at the current pitch is skipped by design. |
| `jet-reverse-800x480-dark.png` / `jet-reverse-800x480-bright.png` | Compact reverse frame at both backdrops: 20 %, 40, HDG 090, dashed lower rung; the IDLE/REV words are dropped at this size by design (the detent strokes remain). |
| `jet-radar-overlays-1920x1080-dark.png` | The shared Classic radar pass with every overlay/label flag: faint round border, the native contact icon row and labels across the middle, while Jet's tapes, `HDG --` (the scene carries no flight reference) and tag coexist; no ladder is invented. |
| `jet-lifepod-800x480-dark.png` | Lifepod: tapes and readouts stay (pod speed 260, 100 %), `HDG --` because the fixture supplies no attitude, no ladder, tag present — exactly the "pod keeps the tapes" gate. |
| `jet-docked-800x480-dark.png` | Docked: **only** the bottom-centre tag — no cross, no ladder, no tapes (native hides the centre instrument group). |
| `jet-command-1920x1080-dark.png` | Command camera: only the tag plus the shared radar contact row; no reticle, ladder, tapes or heading (native hides them in the command view). |
| `classic-flight-1920x1080.png` | The untouched Classic frame from the matrix capture (byte-identical to the pre-change baseline, SHA-256 `abef2436…0d742`): native grey/teal segmented gauges, `260 mps`, the systems/sector text and radar labels. |
| `modern-flight-1920x1080.png` | The untouched Modern frame (SHA-256 `acddf482…76613`, identical to the baseline): cyan gauge rings, percentages and Modern typography. Both hashes match the P1035 report's pair, i.e. nothing moved. |

## 5. Ink-dominance proof (P1035 review follow-up)

The review finding was that at 800×480 the 1 px green ink sat inside a wider near-black under-stroke. Measured
on the **P1035 asset** (`JET_SKIN_SCAFFOLD_P1035_assets/jet-flight-800x480-bright.png`) with exact-colour
counting over each gun-cross arm: **11 ink px vs 22 near-black px = 33 % ink / 67 % dark** — the ink was a third
of the stroke, exactly the reported look.

Two changes fix it: the line-ink floor is now 1.5 physical px (`JetVectorPainter.MinimumLineStroke`; a 1 px quad
centred on a fractional row splits ~50/50 over two rows and reads dark green), and the keyline allowance per
side is `min(1 px, ink/2)` (`LineKeylineAllowance`), so the cross-section is one dark px on each side of a 2 px
ink core. Measured on the **P1036 assets** (same boxes, same metric):

| Image | Arm | Before (P1035) | After (P1036) |
|---|---|---|---|
| `jet-reverse-800x480-bright.png` | vertical arm | 33 % ink | **67 % ink** (22 ink / 11 dark) |
| | left arm | 33 % | **67 %** (16 / 8) |
| | right arm | 33 % | **67 %** (14 / 7) |
| `jet-level-1920x1080-dark.png` | vertical arm | — | **67 %** (34 / 17) |
| | left / right arms | — | **67 %** (30 / 15, 28 / 14) |

The raw pixel cross-section of the 800×480 bright vertical arm is now `dark · ink · ink` per row (a one-pixel
dark edge and a two-pixel green core); the vision read-back of the 4× zoom reports "primarily green with a thin
dark outline", and the whole-frame bright capture keeps every label legible. Text keeps the full 1 px keyline —
glyph strokes are far denser than a hairline and never had the problem. The rule is unit-tested at scale
0.25/1/4 and at the 800×480 density (`The_under_stroke_never_dominates_the_ink`,
`The_800x480_keyline_is_no_wider_than_the_ink`).

## 6. Parity

No `docs/parity/*.csv` row changes status: Jet is presentation-only (P972 §5 keeps every existing status; a new
look cannot promote a `done-visual` row). The five ids Jet now reports in `Coverage` are existing `HudElement`
ids from the shared inventory:

* **`Velocity direction marker`** — the FPM. Jet draws at the port's `HudModel.Context.VelocityMarker`, i.e.
  the same native projection/gate the Classic row already asserts (`WinTrek/indicator.cpp:130-148`); no new data.
* **`Roll tutorial cue`** — the only roll id in the inventory. Jet's ladder reports the ship's bank relative to
  the sector plane; it is not the native `RollTrainingOverlay` text and does not replace it, so the row stays
  `done` for the callout and is neither upgraded nor demoted.
* **`Speed`** — the same `HudFrame.Speed`/`mps` value the Classic row reads.
* **`Thrust setting`** — the same shared throttle fraction (native axis mapping) the Classic gauge reads.
* **`Cockpit centre reticle`** — the same broken-cross presentation under the same cockpit gate story 1 used.

`scripts/parity-scorecard.py` re-validated the CSV; `tools/check-glossary-links.py` passes with the new entry.
Nothing is invented: no altitude, G, Mach, horizon, lock identity or sensor data appears anywhere in the feature.

## 7. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (21.2 s; the post-edit incremental re-build 10.9 s) |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built** (399 s), each 0 warnings / 0 errors |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetFlightSymbologyTests` (3 separate processes) | **62/62 pass each run** (504 / 495 / 507 ms) |
| Existing skin tests | `dotnet test --filter FullyQualifiedName~JetSkinScaffoldTests` | **45/45 pass** |
| Zero-delta (baseline first, then compare) | `hud2-proof --capture baseline` (6.0 min) → `--capture after` (5.8 min) → `--compare baseline`, then **re-run on the final tree** (`--compare baseline`, 6.3 min) | **`HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 (84 on the earlier run) animation-only diff(s), 0 corrupt golden(s)`** |
| Jet evidence capture | `hud2-proof --jet <dir>` | `HUD2_JET_CAPTURE_PASS 15 scenes` |
| Glossary links | `python3 tools/check-glossary-links.py` | `GLOSSARY.md link check OK (903 distinct paths checked)` |
| Parity CSV | `python3 scripts/parity-scorecard.py docs/parity/parity-matrix.csv` | CSV validates; `hud` **111/111 rows, 330/330 weighted** (unchanged), TOTAL 99.61 % |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **14,854 tests, 556.5 s, FAIL**: MonoGame 5,579 passed / 4 not executed, Lobby 193 + Update 55 passed, ClientCore.Tests 2,450 passed / 1 failed, Core.Tests 6,569 passed / 3 not executed; 5 calibrated-limit offenders. The single failure is the ledgered `P660NextMatchLiveTests…` countdown window (1 of 4 rows, 14.90 vs [14.94, 15.06]) — 3× alone reproduced the same pre-existing flake (3/1, 4/0, 3/1) and it is recorded in `docs/TEST_FLAKES.md` (P1036 section); every offender passed. No test was deleted, quarantined or relaxed. |
| AOT publish | not run — **no `src/Allegiance.Server` or `src/Allegiance.Core` change** | n/a |
| `docker compose config` | not run — no service, port, store or env var changed | n/a |
| Training gate | not run — no training-affecting code (`Game1.TrainingIntegration` untouched) | n/a |
| `publish-guard` | cannot run without a commit (Claude's pre-push hook) | n/a |

Raw evidence (outside the repo): captures `~/allegiance-evidence/P1036/{baseline,after}`, the final Jet scene
set `~/allegiance-evidence/P1036/jetfinal`, the fast-tier logs `~/allegiance-evidence/P1036/tiers-20261116T164325.094551Z/`,
and the stdlib pixel counter used for §5 `~/allegiance-evidence/P1036/png.py`.

## 8. Anything unfinished / caveats

* Jet is still a **work in progress**: stories 3–9 (target designator/lock/closure, stores/caution/F4, RWR,
  console/training, amber, screens, ship gate) are later tasks (P1037–P1043). The bottom tag still says
  `JET / GREEN - WIP`; the ladder/RWR/target surfaces of the P972 mockups are not in this story.
* The ladder, heading tape and FPM are new *presentations* of data the model already carries. Native has no
  pitch ladder or heading tape, so those rows keep their existing status; the FPM reuses the native velocity
  indicator unchanged.
* At 800×480 the throttle tape keeps the doubled idle detent and the dashed reverse rail but drops the `IDLE`
  and `REV` words (P972 §4.5 compact layout) — the numeric percent plus the detent still show the reverse/idle
  state.
* The fixture's radar contacts lie on one horizontal line (fixture composition), so a contact can overlap the
  FPM or the gun cross in the captures; the real radar places contacts anywhere on screen. This is the same
  fixture caveat P1035 recorded.
* The `--jet` proof resets the held attitude before each scene so every PNG is self-contained; the live client
  holds the last valid heading/roll across frames (covered by
  `The_state_holder_keeps_the_last_valid_heading_across_the_singularity`).
* No new rig/Live/200v200 capture and no training gate: story 2 adds no simulation, camera, server, wire or
  training behaviour, so `tools/run-training-tests.sh` was not needed. The fast tier exercises the existing
  loopback and HUD suites (§7).
* The shared-pass composition is still Classic for every pass (P1035 decision); a later story may give Jet its
  own radar/tag treatment.
* **Roll reference note (pre-existing port divergence, not changed here).** The Classic roll *readout* is the
  port extension `HudFrame.RollDegrees` ← `Orientation.GetRollDegrees()`, which measures against a **+Y** up
  axis (`Orientation.cs:83-100`). Native's sector plane — and therefore Jet's ladder/tapes — is **+Z** up
  (`Igc/common.cpp:2073`, `WinTrek/WinTrek.cpp:3099`). Jet follows the native reference as the brief requires;
  the Classic readout is untouched and may therefore disagree with Jet's bank value. Reconciling the two is a
  parity decision for the Classic roll extension, outside this story's scope.
* The 18 files under `docs/reports/JET_FLIGHT_SYMBOLOGY_P1036_assets/` are new media (PNG); per the lane rules
  they need Domingo's review before any push. Total 1.7 MB, inside the 2 MB task cap.

## Native intent

Native already owns every value Jet shows. The flight-path marker is the existing velocity-direction surface
`directionbmp`: the source ship's velocity (`WinTrek/indicator.cpp:130`), the truncated rounded speed and the
`speed != 0` gate (`:131,138`), the cockpit camera (`:132`), `TransformDirectionToImage` (`:140`),
`rect.TransformNDCToImage` (`:142`) and `rect.Inside` (`:144`); the port projects exactly that in
`Game1.Camera.ComputeVelocityMarker`, and Jet draws at that point. The attitude reference is the artificial
sector plane the command view already uses — the XY grid at `z = 0` (`WinTrek/WinTrek.cpp:3099`,
`WinTrek/cmdview.cpp:17-115,164`) — not a planetary horizon. Speed is the existing `HudFrame.Speed` (native
`dialog.mdl` speed text), and throttle is native's own axis mapping
`jsValues[c_axisThrottle]*0.5+0.5` with the movement/afterburner override (`WinTrek/WinTrek.cpp:8458-8466`), the
same fraction the shared model hands every skin (`HudModel.cs:421`). Native hides the centre instrument group in
the command view, docked and dead — the gates Jet reuses. Jet invents no altitude, G, Mach, horizon, lock
identity or sensor data; the ladder and heading are a new 2-D presentation of the pose the client already
tracks, and the vertical singularity is handled by holding the last valid value instead of fabricating one.

## Modern approach

The snapshot stays a validated, immutable value crossing one boundary: `Game1.Camera` builds it from the pose
it already resolves for the cameras, `HudModelAdapter` carries it, and the Jet renderer consumes it — no world
reads, no per-frame IO, no lookups. The math is a pure, allocation-free, headless-testable file
(`JetFlightGeometry`) with explicit validity flags rather than silent zeroes, so the vertical singularity can
never produce a random heading. The renderer keeps the P1035 composition contract (Classic shared passes), grows
its explicit `Coverage` instead of claiming native rows, and its new primitives are all the existing
`JetVectorPainter` with one rule change that makes the ink the dominant stroke at every density. Classic and
Modern remain pixel- and semantically identical, proven by the 1,848-combination decoded-RGBA comparison.
