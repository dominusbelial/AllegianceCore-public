# P1037 — Jet HUD skin, story 3/9: target designator, lock, closure, lead and order steering

Implemented in the `hermes/p1037` worktree of `AllegianceCore`, base commit `f763fed4` (the P1036 merge). No commit,
no push, no stash, no remote host, no Docker (not installed here). Raw captures live outside the repo in
`~/allegiance-evidence/P1037/`; the report's assets are the 55 files under
`docs/reports/JET_TARGET_LOCK_P1037_assets/` (54 PNGs + `jet-capture.json`, 1.93 MB total, PNG 1.84 MB — inside
the 2 MB task cap).

This story is P972 §4.2 (lead / aim predictor / order steering) and §4.3 (target, lock, closure) — phase 3B in
P972 §9. Jet stays presentation-only: every value is shared `HudModel`/`HudTarget`/`HudTargetPane`/`HudLock`/
`HudLead`/`HudOrderArrows` data, nothing is invented (no altitude, G, Mach, "who locked you", no zero for an
unknown field), and no server, Core, wire, simulation, targeting, lock-acquisition or audio path is touched.

## 1. Zero-delta baseline first, then the comparison

The **unmodified** tree was built (`tools/hud2-proof`, Release) and captured **before any edit**:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1037/baseline
HUD2_CAPTURE_PASS 1848 combinations in 6.0 min -> ~/allegiance-evidence/P1037/baseline
HUD2_MANIFEST 1848 combinations, commit f763fed4bf2a+dirty, driver llvmpipe (LLVM 19.1.7, 256 bits)
```

After the change the same tree was re-rendered against that baseline, and re-run once more on the **final** tree
(after the last Jet-only colour tweak):

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)   (final tree)
```

The P980/P988 matrix is unchanged: **22 scenarios × 7 resolutions (1920×1080, 2560×1440, 1280×720, 1600×900,
1366×768, 800×480, 3840×2160) × 3 sizes (0.75/1/1.5) × 2 backdrops (dark/bright) × 2 skins = 1,848
combinations** (924 Classic + 924 Modern), decoded-RGBA goldens plus a semantic element/visibility snapshot per
combination. The only differences are the pixels inside the fixture's recorded animation mask (the wall-clock
low-resource `!` badge at `ModernHudRenderer.cs:166`, documented in P980 §3 — 83 and 84 of the 1,848 entries on the two
runs), and every entry reports `outside: 0`. **Classic and Modern stay pixel-identical (decoded RGBA) and semantically identical:
1,848/1,848 combinations, 0 pixel diffs, 0 semantic diffs.** Everything P1037 adds is Jet-only code plus one
optional record field (`HudTargetPane.ClosingSpeed`) that neither shared renderer reads.

## 2. P1036 review follow-ups (layout only, Jet-only code)

Four defects from the P1036 review, all fixed in `JetHudRenderer` alone. Before/after crops at
1920×1080 and 800×480 on dark and bright backdrops are in the asset folder (`layout-before-*` / `layout-after-*`);
each was opened and read, and the pixel measurements below are the objective corroboration.

| # | Finding | Fix (file:line) | Before → after (measured) |
|---|---|---|---|
| 1 | Heading-tape numerals clipped by the top screen edge at both resolutions; the caret `^` sat inside the `HDG nnn` box. | `JetHudRenderer.cs:482-557`: the whole block is laid out top-down from the measured label height — label box top `max(6, 5·scale)+keyline`, rail below it, caret in its own gap (`y+7…y+13`), plate top-anchored at `y+17·scale` (new `topAnchored` mode of `DrawReadout`, `:1288`). | Topmost non-background row in the 40 px top band: **1080p row 1 → row 10**, **800×480 row 0 → row 9** (nothing touches the edge any more). The caret now ends 4·scale px above the plate top instead of crossing its border (unit-pinned, see §4). |
| 2 | At 800×480 the top speed-tape label (`440`/`360`) overlapped the `SPD / MPS` caption; at 1080p `THR` touched the top throttle label and the boxed throttle readout covered the `REV` word. | `:557-614` and `:615-711`: both tapes reserve a **header band** — ticks are skipped while their label box would rise into the caption row (`tapeTop = cy − halfSpan + labelH + 3·scale`), and the throttle's `REV`/`IDLE` words are right-aligned clear of the readout plate width (`wordRight = tapeX − 10·scale − readoutWidth − 6·scale`). | 1080p THR ink box in the 180…260 row band: **rows 198…259 (caption + top label sharing rows) → 198…211 (caption only)**. 1080p reverse throttle: before the `20%` plate sits **on** `REV` ("partially obscuring it" in the read-back), after there is a clear gap; 800×480 keeps the words dropped by design (P972 §4.5). The proof's layout audit now measures **0 caption overlaps / 0 plate overlaps in all 47 Jet scenes**. |
| 3 | At 800×480 the throttle rail continued below the `000` mark. | `:615-711`: the rail is drawn from the topmost to the **bottommost drawn tick** (`firstY…lastY`), so it is clipped to its scale at both ends (the speed tape gets the same rule, `:557`). | 800×480 reverse-throttle tape column: rail ink last row **383 (span bottom, past the 000 row at 288) → 287 (the 000 tick)**. |
| 4 | In level flight the pitch ladder skipped the rung nearest the current pitch, hiding the 0° horizon exactly when level. | `:391-424`: the **0° rung is never skipped**; it is drawn as a **split horizon line** — two solid segments with a centre gap of `HorizonGapHalfWidth = 27·scale` (wider than the 23·scale gun-cross arms), gap centred on the ladder axis (the boresight column) at every pitch; other rungs keep the boresight skip. | At level flight the horizon now exists: pixel scan of the boresight row shows ladder ink at `|x−cx| = 27…51 px` on both sides in the after frame and **no ladder ink at all** in the before frame (only the gun-cross arms at 6…23). |

No label overlaps a caption or a box: the `--jet` proof renders every scene a second time through a recording
surface (`tools/hud2-proof/Program.cs:1609`) and audits the exact submitted boxes — text-vs-caption and
text-vs-plate intersections are **0 in all 47 scenes** (text-vs-text, e.g. an order word crossing a ladder
numeral, is reported as information only and occurs in 7 fixture scenes, listed in the capture JSON).

## 3. Changes

| File:line | Change |
|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/TargetClosingSpeed.cs:1` | **New** — the ONE closure derivation (`Compute(ownPos, ownVel, targetPos, targetVel)` → `(ownVel−targetVel)·(targetPos−ownPos)/distance`), **positive = separation decreasing**, `null` at a degenerate line of sight. Documented as the exact expression `Game1.ComputeTargetSelectionBracket` always used. |
| `src/Allegiance.ClientCore.Game/Game1.Camera.cs:186` | The bracket's closing speed now comes from `TargetClosingSpeed.Compute` (same value, one function; `distance` is still computed there for the bracket's range). |
| `src/Allegiance.ClientCore.Game/Rendering/HudModel.cs:56-61` | `HudTargetPane.ClosingSpeed` (optional, default null) — the pane keeps the reading for an off-screen target. |
| `src/Allegiance.ClientCore.Game/Rendering/TargetPaneModel.cs:56,64-77,243` | The pane resolves its closure from the same `TargetClosingSpeed.Compute` (static model → zero target velocity), inside the existing same-cluster/pose gate. |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetTargetGeometry.cs:1` | **New** (219 lines) — pure, allocation-free geometry: designator half-span (radius × 1.05, clamped 12…150 physical px, then the native edge gutter), relationship classification + word + dashed rule, the 16-segment lock diamond (`DiamondPoint`/`LockSegment`), the shared-clock dash phase (`LockPhase`, 0.5 s), the lead/aim radii and the dim→hot ramp, the order-arrow anchor (108·direction), and the off-screen edge-chevron anchor from the shared bearing. |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.cs:44-58` | `Coverage` grows from 5 to **22** ids (target family, lock, lead/aim/chase, order arrows), named in §6. |
| `.../JetHudRenderer.cs:258-268` | `Draw`: order arrows, lead/aim, chase aim, target designator, off-screen chevron, lock and target data block, each behind the shared model's own gate. |
| `.../JetHudRenderer.cs:391-424` | Pitch ladder: the 0° split horizon line (follow-up 4). |
| `.../JetHudRenderer.cs:482-557, 557-614, 615-711` | Heading tape and both tapes re-laid out (follow-ups 1–3); `DrawReadout` gains `topAnchored` (`:1288`). |
| `.../JetHudRenderer.cs:711-831` | `DrawTargetDesignator` + `DrawDesignatorFrame` + `DrawTargetBars` — four corner strokes sized from `ScreenRadius`, relationship by shape/word, name/range/closure/hull/shield readout beside it (flips to the left when the right side would reach the throttle tape), two short bars. |
| `.../JetHudRenderer.cs:832-858` | `DrawOffScreenTarget` — edge chevron at the shared `TargetBearing` anchor (only when nothing projects). |
| `.../JetHudRenderer.cs:859-907` | `DrawLock` — 16-segment diamond, partial = every other segment (marching on `HudLock.NowSeconds`) + `LOCK nn%`, full = all segments in the hot colour + `LOCK`, none = nothing. Radius from `LockBracketStyle.HalfSizeForFraction`. |
| `.../JetHudRenderer.cs:908-972` | `DrawLeadAndAim` — closed ring + `IN RANGE` when in range, broken/dim ring when out, top tick so it can never be read as the FPM; aim predictor corner brackets at the cockpit boresight (chase: projected aim point), colour = dim→hot ramp. `DrawChaseAim` (`:955`) — the open grey no-target cross. |
| `.../JetHudRenderer.cs:973-1044` | `DrawOrderArrows` — open chevron + `ACC`, closed triangle chevron + `CUR`, double chevron + `ACC / CUR` when same-target; anchors/directions exactly `HudOrderArrows`. |
| `.../JetHudRenderer.cs:1045-1143` | `DrawTargetBlock` — the native pane as a Jet data block, laid out top-down from a measured height above the tag: name, type/side, range or `LOC <sector>`, closure, hull/shield numbers + the two bars, speed, ore, kill bonus, TTI and `IN RANGE`; compact at 800×480 (name + range/closure + bars). |
| `.../JetHudRenderer.cs:1144-1276` | Allocation-free text helpers: `MeasureRuns`/`DrawRuns` (unrolled — a capturing local function would allocate a display class per frame), cached `RangeText`/`ClosureText`/`TargetSpeedText`/`KillBonusText`/`LockText`/`PercentText`/`SectorText`. |
| `tools/hud2-proof/Program.cs:354-...` | `--jet` scenes extended to the eight P1037 states at 1920×1080 and 800×480, dark and bright (47 scenes total), the scene summary now records the shared gates/target/pane/lock values, and every scene is re-rendered through the `RecordingSurface` layout audit (`:1609`) written into `jet-capture.json` (schema 3). |
| `tools/hud2-proof/Program.cs:1438-1512` | `Scenario` gains the Jet target/lock/lead/order/offscreen/friendly fixtures (distinct order directions for `jet-orders`, `ClosingSpeed` on the pane). |
| `tests/.../Skins/JetTargetLockTests.cs:1` | **New** 71-test suite (§5). |
| `tests/.../Skins/JetSkinScaffoldTests.cs:365-398`, `tests/.../Skins/JetFlightSymbologyTests.cs:249-261` | Coverage assertions updated to the 22 ids (P1035's five stay in the set). No behaviour assertion relaxed. |
| `GLOSSARY.md:1023-1044` | New entry: `TargetClosingSpeed`, `JetTargetGeometry`, target designator / lock diamond / edge chevron / order chevrons / target data block. |

### Design decisions

* **One closure derivation, one sign.** `TargetClosingSpeed` is the only place the range rate is computed; the
  bracket and the pane call it with the same inputs, so projection cannot change the reading. The sign is the
  existing one — `d(distance)/dt = −closing`, so positive is decreasing separation — and Modern already prints it
  that way (`ModernHudRenderer.cs:430`, `{closing:+0;-0;0}`), so Jet prints the same signed value with no flip.
* **Relationship without a second hue.** Enemy/friend/neutral are carried by (a) the word `HOSTILE`/`FRIEND`/
  `NEUTRAL`, (b) the corner pattern (enemy: solid corner brackets + inward diagonal ticks; friend: dashed/open
  arms; neutral: plain brackets) and (c) the designator's brightness (engaged = primary, selected = dim). All ink
  stays in the one Jet hue (P972 §4.1).
* **Lock is shape + clock, never audio.** The lock diamond's state is segment pattern (open/dashed vs closed)
  and ink (dim vs hot); the partial lock's dash phase is `floor(NowSeconds / 0.5) & 1` from the shared feedback
  clock, so a skin cannot own a timer. Nothing in the renderer touches sound: the tone gate stays
  `MissileLockState.IsFullLock` in the world and the tip stays `MissileLockFeedback.TipFor` (pure transition
  function), which is why a skin switch mid-lock cannot retrigger either (tested, §5).
* **Lead/aim/chase keep the shared gates.** The renderer reads `LeadMarkerVisible`, `AimPredictorVisible` and
  `ChaseAimVisible` verbatim and never widens them; in cockpit the predictor is drawn at the boresight (native
  centre crosshair, indicator.cpp:334-362), in chase at the projected aim point — the same split the Classic
  renderer uses. The lead ring (circle + top tick), the FPM (circle + wings + tail), the broken gun cross and the
  aim brackets are four distinct shapes.
* **Designator sizing.** Half-span = projected radius × 1.05 (native `radiusBracket`) clamped to 12…150 physical
  px — so the strokes stay legible at the 800×480 floor and a near hull cannot fill a 4K screen — then clipped to
  the nearest edge with native's `c_flBorderSide` gutter. An unknown radius falls back to the established fixed
  34 px × the HUD density scale instead of inventing a size.
* **Off-screen chevron reuses the shared projection.** `HudContext.TargetBearing` (from
  `Game1.ComputeTargetBearing`, the vector the Classic bearing arrow already draws) is intersected with the
  viewport rectangle inset by 26·scale px; no second projection, no invented bearing. The chevron only appears
  when nothing projects into view and a target is selected (the pane is present).
* **The data block is the native pane, not a new source.** It reads `HudTargetPane` only, and prints a row only
  when the model supplies the field: native's sentinels (hull/shield 0 = not visible/live,
  `consoledata.cpp:202-212`; speed −1 = unseen) render as **blank**, the range falls back to
  `LOC <sector>` exactly when `Distance` is null (`dialog.mdl:4053-4060`), TTI only when the model supplies it,
  and `IN RANGE` is the shared flag. No zero is ever fabricated for an unknown value.
* **Compact at 800×480 (P972 §4.5).** The block collapses to name + range/closure + the two bars; the tapes drop
  `REV`/`IDLE` words (P1036 rule kept); the designator/lock keep full stroke contrast; the ladder keeps its
  ±20° span (P1036).
* **Zero allocations.** Every readout string is cached by its integer value, the run layout is unrolled, no LINQ
  or collection expression runs inside `Draw`; proved at 0 B/frame with a locked, leading, ordered target
  (§5).

## 4. Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetTargetLockTests.cs` — **71 tests**:

* **closure**: positive when the separation decreases, negative when the target recedes, a static target's
  approach component, `null` at zero distance, and the pane/bracket parity (the pane built from real wire
  messages for a target 1500 m away that cannot project still carries the same `+140` m/s the bracket's shared
  derivation gives);
* **designator**: relationship word + shape for enemy/friend/neutral (dashed corner arms for a friend, inward
  diagonal ticks for a hostile), the 12/150 px clamps at the 800×480 floor and the 4K reference, the fixed
  fallback, and the native edge-gutter clamp;
* **lock**: none draws nothing (identical submissions to a lockless frame), partial = 16 open segments + `LOCK
  62%` in dim ink, full = 32 closed segments + `LOCK` with no percentage and hot ink, the 0.5 s dash phase and
  the marching phase flip, and **skin switch during a lock**: two frames around a Classic→Jet→Modern switch keep
  exactly one `LOCK` cue per frame (9 keyline+ink submissions), one shared-pass set per draw, no second batch
  session, and the renderer type has no sound member — the tone/tip gates are the world's lock state and
  `MissileLockFeedback`'s pure transition;
* **lead/aim gates**: a 6-case theory (camera × Granted × Visible × InRange) asserts the lead ring exists exactly
  when `LeadMarkerVisible` and `IN RANGE` exactly when visible+in-range, and the aim brackets exactly when
  `AimPredictorVisible`;
* **order chevrons**: every combination (accepted, current, both, same-target, none) asserted on both directions,
  on `OrderArrowsVisible`, and on `ACC`/`CUR`/`ACC / CUR`;
* **target block**: every supplied field renders; sentinel-unknown fields render nothing (`LOC Alystra` instead,
  no `HULL`/`SHLD`/`SPD`, no `000`, no `-1`, no `IN RANGE`);
* **off-screen target**: no bracket, edge anchor on the right edge inside the inset, chevron ink there, and the
  pane keeps `9000 m` / `-25`;
* **P1036 follow-ups**: heading label runs start below 2·scale px at both resolutions; no segment crosses the HDG
  plate border; no label overlaps either caption and no label intersects a readout plate; the 800×480 throttle
  rail ends at the 000 row (not the span bottom); level flight keeps ≥ 2 horizon halves outside the 27·scale gap;
* **0 B/frame**: `P1037 JetHudRenderer.Draw (target+lock+lead+orders+block): 0 B / 100000 frames = 0 B/frame`;
* **draw smoke** for 21 `HudModel` states (cockpit, chase, command, docked, lifepod, dead, ghost, turret,
  training, hidden, no-telemetry, no-reference, target, locked, lock-full, lead, lead-out, orders, offscreen,
  friendly, asteroid-ore): no throw, one shared pass each in the Classic composition, designator only with the
  projected target + option, lock cue only under the lock gate, no sentinel text.

Existing suites stay green: `JetSkinScaffoldTests` 45, `JetFlightSymbologyTests` 62 (coverage literals updated),
all 244 tests in the `Skins` namespace pass (45 scaffold + 62 symbology + 71 new + the switching/catalog
suites).

```
run 1: Passed 71, Failed 0, Skipped 0 (443 ms)
run 2: Passed 71, Failed 0, Skipped 0 (444 ms)
run 3: Passed 71, Failed 0, Skipped 0 (456 ms)
```

Nothing here exceeds 10 s, so no `[Trait("Tier","Live")]` is needed and all 71 run in the fast tier.

## 5. Screenshots

`tools/hud2-proof --jet <dir>` at the two required resolutions on dark and bright backdrops
(`docs/reports/JET_TARGET_LOCK_P1037_assets/`: 32 scene PNGs + 22 layout before/after crops + `jet-capture.json`,
1.93 MB total). The PNGs were losslessly re-encoded (same decoded RGBA, verified pixel-by-pixel by
`~/allegiance-evidence/P1037/reencode.py`) from the proof output to stay inside the 2 MB cap; the raw originals are in
`~/allegiance-evidence/P1037/jet-final/`. Every image below was opened and read; the reads are summarised, and
the pixel/layout-audit measurements corroborate them rather than replace them.

### 5.1 P1037 scenes (each at 1920×1080 and 800×480, dark and bright)

| Image | What is actually visible |
|---|---|
| `jet-target-1920x1080-dark.png` / `-bright.png` | Level flight: heading tape with `HDG 045`, `SPD / MPS` 260, `THR` 80 %, `IDLE`/`REV` on the tape, the ladder rungs around the gun cross, and the **target designator to the upper right**: four corner strokes with inward ticks, `HOSTILE / Enemy fighter with a long pilot name`, `1250 m  CLO +43`, `HULL 060  SHLD 030` and the two short bars under them. Bottom-centre the **data block**: name, `Fighter / Rixian`, `1250 m  CLO +43`, `HULL 060  SHLD 030`, `SPD 43 mps`, `KB 15`, the TTI string and `IN RANGE`. No lock (this scene has none), the ACC/CUR order text at the centre, `JET / GREEN - WIP` at the bottom. On bright the same strokes and text read against the light backdrop (dark keyline visible). |
| `jet-target-800x480-dark.png` / `-bright.png` | Compact frame: `HDG 045` with clearance under the top edge, 260 / 80 %, designator readout beside the bracket, and the **compact data block** (name + `1250 m CLO +43` + `HULL 060 SHLD 030` + bars) above the tag; nothing clipped at the edges, nothing overlapping. |
| `jet-locked-1920x1080-dark.png` / `-bright.png` | The same target with the **partial lock**: an open 16-segment diamond around the designator (visible gaps between segments), `LOCK 62%` above it in dim ink; the designator text is unchanged; on bright the gaps and the `LOCK 62%` text are still readable. |
| `jet-locked-800x480-dark.png` / `-bright.png` | The compact partial lock: gapped diamond around the target and the `LOCK 62%` text; read-back confirms the gaps. |
| `jet-lockfull-1920x1080-dark.png` / `-bright.png` | The **full lock**: the diamond is closed (all segments joined) in the hot colour, the word `LOCK` above it with **no percentage**; bright read-back confirms the closed diamond and `LOCK`. |
| `jet-lockfull-800x480-dark.png` / `-bright.png` | Compact closed hot diamond + `LOCK`, designator and data block unchanged. |
| `jet-offscreen-1920x1080-dark.png` / `-bright.png` | No designator near the boresight (the target does not project), a **small chevron on the right screen edge pointing right/up** at the shared bearing (ink measured at x 1890…1905, y 303…308), and the data block still reads `Enemy fighter…`, `1250 m`, `+43`, `HULL 060 SHLD 030`. |
| `jet-offscreen-800x480-dark.png` / `-bright.png` | The same off-screen state at 800×480: the edge chevron is read back on the far right edge next to the throttle tape, and the compact block shows name, `1250 m  CLO +43`, hull/shield + bars. |
| `jet-lead-1920x1080-dark.png` / `-bright.png`, `jet-lead-800x480-dark.png` / `-bright.png` | Lead **in range**: a small open circle with a short top tick at the lead point, the words `IN RANGE` under it, and the aim-predictor corner brackets around the centre crosshair. |
| `jet-leadout-1920x1080-dark.png` / `-bright.png`, `jet-leadout-800x480-dark.png` / `-bright.png` | Lead **out of range**: the ring is broken/dashed and dim, `IN RANGE` is absent, the aim brackets are still around the crosshair. |
| `jet-orders-1920x1080-dark.png` / `-bright.png`, `jet-orders-800x480-dark.png` / `-bright.png` | Order steering: an **open V chevron** with `ACC` pointing one way and a **closed triangle chevron** with `CUR` pointing another (the scene drives `.6,−.8` and `−.7,.7`), both readable on both backdrops. |
| `jet-friendly-1920x1080-dark.png` / `-bright.png`, `jet-friendly-800x480-dark.png` / `-bright.png` | A **friendly** target: the designator word reads `FRIEND` (not `HOSTILE`), the name is `Friendly pilot`, the corner arms are the dashed/open pattern instead of solid brackets + ticks, and the range/closure/hull rows are unchanged. |

The capture JSON records the shared gates each scene drove (`target`, `targetPane`, `leadGate`, `aimGate`,
`orderGate`, `lockState`, `offScreenBearing`) so a reader can check every Jet draw against the model's own gate.

### 5.2 P1036 follow-up before/after crops

| Image | What is actually visible |
|---|---|
| `layout-before-top-1920x1080-dark|bright.png` vs `layout-after-top-…` | Before: the heading numerals sit on the very first rows (ink at row 1) and the caret touches the `HDG 045` plate. After: numerals at row 10 with clear space above, and the caret in its own gap above the plate. |
| `layout-before-top-800x480-dark|bright.png` vs `layout-after-top-…` | Before: ink at row **0** (the numerals touch the top edge). After: ink starts at row 9; the caret is above the box with a gap. |
| `layout-before-throttle-1920x1080-dark|bright.png` vs `layout-after-throttle-…` | Before: the `THR` caption and the top `100` label share rows (ink 198…259). After: the caption occupies 198…211 and the labels start below it. |
| `layout-before-reverse-throttle-1920x1080-dark.png` vs `layout-after-…` | Before: the black `20%` plate sits over the `REV` word (read back as "partially obscuring it"). After: `REV` and `IDLE` clear the plate, with a visible gap. |
| `layout-before-throttle-800x480-dark|bright.png` vs `layout-after-…`, `layout-before/after-reverse-throttle-800x480-dark|bright.png` | Before: the green rail runs to the bottom of its span (column ink last row 383) past the `000` row; after it stops on the `000` tick (row 287). |

## 6. Parity and glossary

No `docs/parity/*.csv` row changes status: Jet is presentation-only (P972 §5 keeps every existing status; a new
look cannot promote a `done-visual` row). The 22 ids Jet now reports in `Coverage` are existing `HudElement` ids
from the shared inventory:

* **Selected target bracket / Target name / Target type description / Target side identity / Target hull /
  Target shield / Target range or sector / Target speed / Target ore / Target kill bonus / Target in weapon
  range / Target time to intercept** — Jet's designator and data block read `HudTarget`/`HudTargetPane` only
  (native `consoledata.cpp:40-199`, `dialog.mdl:4040-4242,1280-1355`); no new data, no invented zero.
* **Missile lock flower** — the segmented diamond reads `HudLock` (server lock fraction, native
  radarimage.cpp:325-332 sizing) and nothing else; the tone stays native.
* **Ballistic lead marker / Aim hit-probability predictor / Chase weapon aim without target** — the same
  `HudLead`/`ChaseAim` values and the same gates Classic uses (indicator.cpp:283-405), ramp mapped to one hue.
* **Accepted and current order turn arrows** — the same directions/gates as `HudOrderArrows`
  (indicator.cpp:411-457), anchors at 108·direction.

`scripts/parity-scorecard.py docs/parity/parity-matrix.csv`: CSV validates; `hud` **111/111 rows, 330/330
weighted** (unchanged), TOTAL 99.61 %. `tools/check-glossary-links.py`: **`GLOSSARY.md link check OK (907
distinct paths checked)`**.

## 7. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (21.1 s) |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built** (394 s first pass; 1.8 s incremental on the final tree), each 0 warnings / 0 errors |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetTargetLockTests` (3 separate processes) | **71/71 pass each run** (443 / 444 / 456 ms) |
| Existing skin tests | `dotnet test --filter FullyQualifiedName~Skins` | **244/244 pass** (29 s) |
| Zero-delta (baseline first, then compare) | `hud2-proof --capture baseline` (6.0 min) → `--compare baseline` | **`HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)`** |
| Jet evidence capture | `hud2-proof --jet <dir>` | `HUD2_JET_CAPTURE_PASS 47 scenes`; layout audit 0 caption/0 plate overlaps in all 47 |
| Glossary links | `python3 tools/check-glossary-links.py` | `GLOSSARY.md link check OK (907 distinct paths checked)` |
| Parity CSV | `python3 scripts/parity-scorecard.py docs/parity/parity-matrix.csv` | `hud` 111/111 rows, 330/330 weighted (unchanged), TOTAL 99.61 % |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **14,925 tests, 547.2 s, FAIL**: MonoGame 5,650 passed / 4 not executed, Lobby 193 + Update 55 passed, ClientCore.Tests 2,450 passed / 1 failed (+2 quarantine passed), Core.Tests 6,569 passed / 3 not executed (+17 quarantine passed) — the single failure is the ledgered `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` ("B never saw A's death… after 2500 ticks"), rerun **3× alone: 3 pass**, recorded in `docs/TEST_FLAKES.md` (P1037 section); the two calibrated-limit offenders (`SC2NaniteSupportTests…civ: 25` 15.9 s, `P814GunnerTests.P814b…` 13.9 s) both passed |
| AOT publish | not run — **no `src/Allegiance.Server` or `src/Allegiance.Core` change** | n/a |
| `docker compose config` | not run — no service, port, store or env var changed | n/a |
| Training gate | not run — no training-affecting code (`Game1.TrainingIntegration`, lesson flow untouched) | n/a |
| `publish-guard` | cannot run without a commit (Claude's pre-push hook) | n/a |

Raw evidence (outside the repo): baseline capture `~/allegiance-evidence/P1037/baseline` (461 MB), the compare log
`~/allegiance-evidence/P1037/compare.log`, the pre-fix Jet capture `~/allegiance-evidence/P1037/jet-before`, the
final 47-scene capture `~/allegiance-evidence/P1037/jet-final` (raw PNGs), and the pixel/re-encode scripts `~/allegiance-evidence/P1037/{png,reencode}.py`.

## 8. Anything unfinished / caveats

* **Fixture-only overlaps.** Text-vs-text overlaps remain in 7 of the 47 fixture scenes (e.g. an order word
  crossing a ladder numeral at 800×480, the designator readout near a ladder label). They are not caption or
  plate violations; the markers are independently positioned and can share sky in a real frame too. They are
  reported (not hidden) in `jet-capture.json` under `layout.textOverlaps`.
* **The name is shown in full.** Jet prints `HudTargetPane.Name`/`HudTarget.Name` as supplied (the fixture's
  deliberately long pilot name included); native truncates by pane width (measured-fit). Adding a measured
  truncation is a presentation decision for a later story; no value is invented either way.
* **Target model preview** stays with the target pane row (`Target model preview`) and is not drawn by Jet — the
  live 3-D preview needs the shared render target, which is P1038/P1042 territory.
* **The fixture's radar contacts** lie on one horizontal line (P1035/P1036 caveat), so a contact can overlap a
  designator in the captures; the real radar places contacts anywhere.
* **Lock audio** was not exercised as a live loop (no audio device here); the rule is proved structurally: the
  renderer has no sound dependency, the tone gate is the world's `IsFullLock`, and the tip is a pure transition
  function — a skin switch cannot be an input to either.
* **No new rig/Live/200v200 capture and no training gate**: story 3 adds no simulation, camera, server, wire or
  training behaviour, so `tools/run-training-tests.sh` was not needed; the fast tier exercises the loopback and
  HUD suites (§7).
* **New media** (55 files, 1.93 MB) is under the 2 MB task cap and, per the lane rules, needs Domingo's review
  before any push.
* The brief file `P1037_BRIEF.md` is **removed** from the worktree root so it cannot enter the change set.

## Native intent

Native already owns every value Jet shows. The closure is the relative velocity along the line of sight the port
has always computed for the selection bracket (`Game1.Camera.cs` `delta`/`distance`), now shared with the pane so
native's `GetRange`-style reading (consoledata.cpp:182-199) does not depend on the projection. The target pane's
fields are native `ModelData` outputs — name/type (`consoledata.cpp:40-53`), side name + `SetTargetGeo` colour
(`WinTrek.cpp:8784-8818`), hull/shield (`consoledata.cpp:202-276`), range or the `LOCATION:` sector fallback
(`dialog.mdl:4053-4060`), speed (`:136-150`), asteroid ore (`consoledata.cpp:449-460`), kill bonus
(`consoledata.cpp:831-843`), TTI (`dialog.mdl:1280-1355`) and in-range (`indicator.cpp:405`). The lock is the
server-computed `MISSILE_LOCK_UPDATE(204)` fraction and native's `radiusBracket + 40·(1−lock)` sizing
(radarimage.cpp:325-332); the tone remains `missileToneSound` gated on `HasMissileLock()` (trekigc.cpp:876-883,
1288-1290, SoundManager.cs:1222-1233). The lead/predictor/chase markers are the same native surfaces and gates
(`indicator.cpp:283-405`), the order arrows the same accepted/current directions (`indicator.cpp:411-457`), and
the off-screen direction the same projection the port uses for the bearing arrow. Jet invents no aircraft
physics, no radar lock identity, no sensor data and no zero reading: it is a new 2-D presentation of the shared
model in the one green hue (P972 §4.1).

## Modern approach

The closure lives in one tiny pure function used by both descriptors, so a new presentation cannot silently fork
the value. Geometry is a separate, allocation-free, headlessly testable file (`JetTargetGeometry`) with explicit
clamps and a documented flash phase rather than a renderer-owned timer; the renderer only composes those values
through the P1035 `JetVectorPainter` and grows its explicit `Coverage` instead of claiming native rows. Every
readout string is cached by its integer value and the run layout is unrolled, proved at 0 B/frame with a locked,
leading, ordered target. The proof tool renders every scene twice (once for the PNG, once through a recording
surface) so "no label overlaps a caption or a box" is a measured fact per scene, and Classic/Modern remain byte-
and pixel-identical, proven by the 1,848-combination decoded-RGBA comparison.
