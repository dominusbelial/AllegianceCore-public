# P395 — Cockpit HUD gauges render top/bottom-inverted from native ("horns" instead of a clustered ring)

## Root cause — a coordinate-convention mistake, confirmed

The six `DrawGauges` gauges (Thrust/Fuel/Energy/Ammo/Hull/Shield) were placed
using native's training-tutorial overlay offsets (`WinTrek/indicator.cpp:514-566`)
— the only readable screen-coordinate reference for these six gauges relative
to the viewport centre in the native source. That much was right. The mistake
was the **Y-axis sign handling** in `HudRenderer.cs`.

The P241 port assumed native's 2D `Point` was **math/Y-up** (positive = up) and
therefore negated every Y offset to convert into MonoGame's Y-down screen
space. That assumption is **wrong**.

Native's `Point` type (`src/zlib/point.h:152-185`) is built directly on the
Windows `POINT` struct:

- `class WinPointCore : public POINT` (point.h:119) — derives from the Win32
  `POINT`, whose convention is **top-left origin, +Y DOWNWARD** (screen space).
- `PointCore::operator POINT()` (point.h:174-182) — casts straight to `POINT`.
- It is used interchangeably with `GetMousePosition()` / `ScreenToClient()`
  (`WinTrek.cpp:8481-8489`, `load.cpp:1748`, etc.), both of which return
  Windows **Y-down** screen coordinates.

So native's training-overlay offsets are **already in Y-down screen space**,
identical to MonoGame's own convention. **No inversion should have been
applied.** The port's negation flipped the whole gauge layout vertically:
native's "+93.5" (meaning *below* centre, Y-down) became "-93.5" and rendered
*above* the crosshair — putting Throttle/Fuel up top as the observed "horns".

(The earlier candidate explanation in the task brief — that `WinTrek.cpp:8482`'s
`YMax()-1-Y` shows native is Y-up — is actually the opposite: that line converts
a Y-down cursor position *into* a special Y-up command-camera navigation
subsystem, i.e. it is the exception that proves the general 2D screen space is
Y-down.)

### Scope item 1 — is the training overlay even the right source?

Investigated. The real always-on cockpit gauge layout in native is **not**
recoverable from the C++ source as a coordinate table: classic Allegiance
positions its in-flight gauges inside the compiled `HUDImage`/artwork overlay
(the `HUDImage` interface in `WinTrek/hudimage.h` has **no implementation in
this source tree**, and the gauge bitmaps/positions live in the shipped artwork
MDL binaries, not in readable code). `indicator.cpp` is the *only* native file
carrying explicit centre-relative screen offsets for exactly these six gauges,
and the training arrows are intentionally pointed **at** the real gauges'
rest positions, so they are a legitimate proxy. Conclusion: **path 3** — the
training-overlay numbers are the best available source; the fix is the sign
handling, not a new source.

## The fix

`HudRenderer.cs` `DrawGauges` — use the native offsets **verbatim** (no Y
negation), and correct the two now-misleading comments (the method doc and the
placement-table comment) to state the verified Y-down convention.

New placement table (offsets px from centre, Y-down):

| Gauge   | Native offset  | Screen position (fixed) |
|---------|----------------|-------------------------|
| Thrust  | (−126.5, +93.5)  | lower-left  |
| Fuel    | (+126.5, +93.5)  | lower-right |
| Energy  | (+126.5, −112.5) | upper-right |
| Ammo    | (−126.5, −112.5) | upper-left  |
| Hull    | (−65.5,  +35.5)  | lower-centre-left  |
| Shield  | (+65.5,  +35.5)  | lower-centre-right |

Net effect: Throttle/Fuel move from the top (horns) to the **bottom row**;
Ammo/Energy move up to the top row. The six form a compact, symmetric 3×2
cluster around the crosshair instead of a lopsided 2-up/4-down split with two
gauges sticking past the top.

## Verification

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors.**

### Test suites (each run ≥2×)
- `Allegiance.ClientCore.MonoGame.Tests` — **770/770 passing**, twice, 0 failures
  (this is the project that hosts `HudRenderer`).
- `Allegiance.ClientCore.Tests` — only pre-existing `*LiveTests` network-timing
  flakes (ShipDeleteGoneLiveTests, JoinFlowLiveTests; 4 then 2 — variance is the
  flakiness), no HUD/rendering failures.
- `Allegiance.Core.Tests` — only pre-existing `*LiveTests` timing flakes
  (LoadoutChangeLiveTests, LiveWinConditionTests, LiveTurretGunnerBindingTests,
  LiveTeamLobbyTests, LiveRipcordTests; count varies run-to-run 3↔5). These live
  wire-timing tests are untouched by this pure layout change (git diff touches
  only `HudRenderer.cs`).

### Live before/after (real HudRenderer, offscreen RenderTarget2D)
Rendered with a purpose-built harness (`tools/hud-gauge-proof/`, untracked) that
constructs the **real** `HudRenderer` + **real** `GaugeTextures` (all 6 real
`*Gaugebmp.mdl` textures decoded) + the **real** production spritefonts, and
drives the real `Draw` path. Evidence in `.verify/p395/`:

- **`02_before_horns.png`** — the old negated layout: **THR upper-left, FUEL
  upper-right** — two gauges sticking *above* the crosshair (the "horns").
- **`after_fixed.png`** — the fixed layout: **THR lower-left, FUEL lower-right**
  — both *below* the crosshair, forming a tight 3×2 cluster (AMMO/ENGY on top,
  HULL/SHLD middle, THR/FUEL bottom) around the reticle. No gauge rises above
  the AMMO/ENGY top row.
- `01_p241_before_reference.png` — the P241 era full-HUD capture (same horns).

### Comparison against the native reference
Against `docs/reference-images/native-hud-reference.jpg`, where the throttle /
fuel / armour cluster sits **below** the crosshair: the fixed layout matches far
better. Throttle and Fuel now sit below the crosshair exactly where native puts
them, in a compact central group, instead of hanging off the top. The overall
span (top AMMO/ENGY row to bottom THR/FUEL row ≈ ±130px about centre on a 720px
viewport) is a clustered diamond around the targeting reticle, not spread toward
the corners.

## Out of scope — noted follow-up (not done here)
This task is about *position*, not texture. To be fully native-faithful the
gauge *shape* also differs: native's real HUD uses large sweeping **arc gauges**
framing the top of the reticle (visible in the reference), whereas this port
still draws every gauge as a **small circular bubble** (`*Gaugebmp.mdl` decoded
128×128 segmented circles). Making the cluster truly look like the reference
(e.g. the big top arcs) is a separate texture/geometry follow-up; this stage
only corrects the placement/convention.

## Files changed
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` — removed the
  erroneous Y negation in `DrawGauges`, corrected both comments.
- `P395_HUD_GAUGE_LAYOUT.md` (this report).

Evidence images + the untracked harness live in `.verify/p395/` and
`tools/hud-gauge-proof/` for inspection; neither is committed (matching the P241
"disposable scaffolding, evidence left in .verify" convention). Committed on
`master` (not pushed — Domingo pushes).
