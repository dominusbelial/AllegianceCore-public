# P1039c — Jet HUD skin: radar labels clear of glyphs/rings, compact-scope declutter, real 800×480 radar zone

Follow-up to P1039b (`docs/reports/JET_RADAR_FIXES_P1039B.md`, merged at `744c49bf`), built in the
`hermes/p1039c` worktree of `AllegianceCore` at Claude's master. No commit, no push, no stash, no
remote host, no Docker (not installed here). Scratch lived in `.scratch/p1039c/` (removed); the raw
captures, the zero-delta baseline, the compare log and the gate logs live outside the repo in
`~/allegiance-evidence/P1039C/`. Task evidence is
`docs/reports/JET_RADAR_FIXES_P1039C_assets/` — 19 PNGs + `jet-audit-summary.json`,
**20 files, 796,286 bytes = 0.76 MiB** (under the 2 MB cap).

Jet stays presentation-only: every value is the shared `HudModel`/`HudFrame`/`HudContext` data plus
the presentation-only `HudContact.RelativeBearingRadians` P1039 added. No server, Core, wire, audio,
input, contact-eligibility, targeting, command or Tab-starmap change; the native LOD tier stays the
authority for which labels exist, and nothing here adds, removes or widens a contact or a label.
Jet remains behind `hud.jetSkin` (default off), so Classic and Modern are untouched (proven by the
1,848-combination zero-delta in §9).

**Status**

| Item | Status |
|---|---|
| 1 — labels over glyphs and ring strokes (glyph boxes as obstacles; band rule; leaders) | **done** — slot test **and** a plate where a band crosses; every glyph box is an obstacle; leaders tested |
| 2 — compact label budget as a rule of scope pixels-per-label | **done** — `LabelBudget` 9 cells at 800×480 / 24 at 1080p, full form needs two full blocks |
| 3 — 800×480 scope zone measured against every competing element | **done** — no free corner exists (the mini-map owns it); stated 8·scale gap from the plate, position described as it appears |
| 4 — duplicate target data at 800×480 (`TGT` block vs bottom pane) | **done** — the fallback prints only what the pane lacks |
| 5 — stores annunciator `AUTOVLOCK FLAG` word gap | **done** — `WordGap` 6 units, reduced only to stay before the ladder column |
| Audit — `TextOverGlyph` + radar `StrokeThroughText` added; every class 0 | **0 / 0 / 0** |
| Gate — baseline-first zero-delta, 0 B/frame, tests 3×, build 0/0, tools build, one fast tier, evidence | see §9 |

## 1. Defect 1 — labels over glyphs and ring strokes

### 1.1 What was wrong (measured on the P1039b placement)

P1039b's pass only tested a candidate against **other text boxes**. A label could therefore land on
another contact's glyph or across a range band, and no audit class counted either. Measured by
replaying the P1039b placement in the instrumented tree (the P1039c rules disabled, §7):

| Scene (dark) | readings over another contact's glyph | band/leader strokes through a reading |
|---|---|---|
| `jet-radar` 1920×1080 | 5 | 18 |
| `jet-radar` 800×480 | 7 | 21 |
| `jet-radar-round` 800×480 | 7 | 24 |
| `jet-radar-dense` 1920×1080 | **78** | **37** |
| `jet-radar-dense` 800×480 | **69** | **34** |
| `jet-radar-dense` 3840×2160 | **77** | **38** |
| `jet-radar-kinds` 800×480 | 11 | 15 |
| all 96 Jet scenes | **1,182** | **898** |

The review's reading of the two crops matches these boxes: in `radar-dense-1920-after.png` the labels
`293`, `346`, `Contact 0`, `QUE`, `SUB`, `T` and `!` sat on glyphs/brackets and the bands ran through
them; in the sparse crop `2220 m`, `1440 m`, `2480 m` and `Contact 9` were crossed by ring strokes.

### 1.2 The rule (Jet-only, stated)

* **Every contact's glyph/overlay box is an obstacle.** `JetRadarLabels.GlyphBox`
  (`JetRadarLabels.cs:169`) is the union of the glyph and every overlay the contact carries: the
  4.5-unit mark plus the 8.5-unit bracket (4.5 glyph + 4 arm), the nearest-enemy outer bracket
  (+2 units), the carried flag's pennant (+7 units right), the carried artifact's mark (+8), and the
  ripcord stroke (+5 above). The planner reserves all of them before any caption is placed
  (`PrepareRadar`, `JetHudRenderer.Radar.cs:976`), so a label **and** a marker word may not touch
  another contact's box. A contact's own box is skipped **by index** (`ownGlyph` in
  `Place`/`Scan`/`Blocked`), because its label already keeps `GlyphClear` + `SlotGap` and its own
  marker letters sit beside it by design.
* **Ring/band strokes: the slot test AND a plate — why both.** The stated choice is
  *slot test first, plate only where the scope has no ring-free cell.* The four bands are geometry
  (`JetRadarLabels.Rings`, `:194`): a square outline tested segment-by-segment, a circle tested
  exactly (nearest point inside the radius, farthest corner outside it). A candidate the band crosses
  is never taken while a ring-free one exists; when none does (the compact scope's annuli are 15.8 px
  at 800×480, less than one 22 px text line) the block is taken **with an opaque backing plate** in
  the readout-plate opacity 0.82 (`RadarPlateOpacity`, `JetHudRenderer.Radar.cs:674`, the same value
  every Jet readout fills with, `JetHudRenderer.cs:1723`). The plate is submitted **after** the bands
  and **before** every glyph and text, so it interrupts the band under the ink: no stroke can read as
  a strikethrough. This is why both mechanisms exist rather than one: a pure slot test would make
  every reading impossible at 144 px, and a pure plate rule would put a plate under every label at
  1080p even where the scope has clean sky. The audit counts the plates
  (`jet-audit-summary.json` → `plates`), and `StrokeThroughText` excludes a band that a plate drawn
  between the band and the run interrupts (§7).
* **Leaders must not cross another label or glyph.** A diagonal candidate is only accepted when its
  leader is clear (`LeaderCrosses`, `JetRadarLabels.cs:497`); the draw pass re-tests every leader
  against the **final** plan (blocks placed later, ring captions, words) and drops the stroke rather
  than draw a line through someone else's ink (`RadarLeaderBlocked`,
  `JetHudRenderer.Radar.cs:1013`). A scan-placed priority label whose leader cannot be drawn keeps its
  block and loses only the stroke.
* The mode caption (`RDR <mode>`) is fixed in the scope's bottom-right corner and the four band
  captions sit on the vertical spoke; both are *text* inside the scope, so each takes a plate where a
  band crosses it, and the mode caption steps left/up over up to four fixed tries if a contact glyph
  owns the corner. The mode caption is reserved before any label, so no label may sit on it.

### 1.3 Before → after (measured, per scene)

| Scene (dark) | over-glyph / stroke before | placed/shortened/hidden after | plates | budget | full form |
|---|---|---|---|---|---|
| `jet-radar` 1920×1080 | 5 / 18 | 9/1/0 | 14 | 24 | allowed |
| `jet-radar` 800×480 | 7 / 21 | 7/4/2 | 14 | 9 | no |
| `jet-radar-round` 1920×1080 | 5 / 21 | 9/1/0 | 10 | 24 | allowed |
| `jet-radar-round` 800×480 | 7 / 24 | 7/4/2 | 14 | 9 | no |
| `jet-radar-dense` 1920×1080 | 78 / 37 | 24/16/39 | 23 | 24 | allowed |
| `jet-radar-dense` 800×480 | 69 / 34 | **10/10/53** (1 priority hidden) | 12 | 9 | no |
| `jet-radar-dense` 3840×2160 | 77 / 38 | 26/17/37 | 21 | 26 | allowed |
| `jet-radar-dense-round` 1920×1080 | 78 / 43 | 24/19/39 | 20 | 24 | allowed |
| `jet-radar-kinds` 1920 / 800 | 6 / 18 · 11 / 15 | 11/1/0 · 9/7/2 | 12 · 14 | 24 · 9 | allowed · no |
| `jet-radar-nobearing` 1920 / 800 | 5 / 20 · 8 / 15 | 9/1/0 · 7/4/2 | 12 · 13 | 24 · 9 | allowed · no |
| LOD All / Default / Target / Minimal 1920 | 6/22 · 5/18 · 0/8 · 0/4 | 9/1/0 · 9/1/0 · 4/0/0 · 1/0/0 | 14 · 14 · 6 · 4 | 24 | allowed |

The dense 800×480 frame hides 53 of its 63 native-eligible labels: 10 blocks remain (the fixture's
eleven never-hidden contacts, one of which has no clear cell — §2.3), every glyph, overlay and
`ACC`/`QUE` word still drawn, and no reading over a glyph or across a stroke.

## 2. Defect 2 — the compact label budget

### 2.1 The rule (a function of the scope's own area)

The budget is stated in **pixels per label**, never in the window's name
(`JetRadarLabels.cs:299-340`):

* **One label cell** = `LabelCellPixels` **4,800 px² at scale 1** — a shortened block's ink (4–6 digits
  of one row, ≈30×22 px) plus one row of sky on every side. The scope's budget is
  `LabelBudget = innerArea / (LabelCellPixels × scale²)`, at least 1. At 800×480 the inner scope is
  141.3² px → **9 blocks**; the 341.6 px 1080p inner scope → **24 blocks**; the 4K scope → 26.
* **A full block costs 12 cells** (`FullBlockCells`: four rows × ~three cells of width and sky). A
  scope may carry a full multi-row block for a **non-priority** label only when it can hold **two**
  full blocks (`FullFormAllowed`: budget ≥ 24 cells). At 800×480 (9 cells) the full form is refused
  for every non-priority label; at 1080p/4K (24/26 cells) it is allowed.
* **Who gets a block**: the never-hidden classes (selected target → order subject → threat/missile →
  own command verb) always take a block; every other label is reduced to its shortest form (native's
  own bare `_itoa` digits, `radarimage.cpp:1209-1218`; the name alone when the contact has no range)
  and only while the budget has room, otherwise it is **hidden with its glyph, overlays and
  `ACC`/`QUE` word kept**.
* **When the scope is genuinely full** (no cell clear of other contacts' glyphs and readings) a
  never-hidden label is **hidden** and counted (`RadarPriorityHidden`) rather than drawn over a glyph:
  this is the one deliberate departure from P1039b's "never hidden" heuristic, and it is measured
  (1 frame in 96: the artificial 63-contact 800×480 dense fixture, where 63 glyph boxes occupy every
  cell). `TextOverGlyph` is absolute; a reading on another contact's glyph can mis-attribute the
  contact.

Native intent is unchanged: the budget only *hides or shortens* labels the native per-kind LOD tier
already allowed. No contact and no eligible label is added, and no label native would not show is
shown.

### 2.2 What it looks like at 800×480

`radar-dense-800-x2.png` (2× nearest-neighbour crop of the 144 px scope): 10 separated single-row
blocks (bare ranges), each on its own cell, with a dark backing plate where a band crosses it; the
glyph field in the middle is untouched. P1039b placed 15 blocks here, including four-row blocks.
`radar-sparse-800-dark-x2.png`: 7 blocks (4 shortened) instead of 7 full blocks.

## 3. Defect 3 — the 800×480 scope zone (measured, as it appears)

**There is no free corner zone at 800×480.** The bottom-right corner is the shared sector mini-map,
the instrument band native also draws there. The measured rectangles of every competing element at
800×480 (dark, scale 0.667; from `jet-audit-summary.json` and `MinimapGeometry.CornerPanel`):

| Element | Rectangle (px) | Relationship to the scope |
|---|---|---|
| Radar scope (new) | **533.3, 174, 144 × 144** (right edge 677.3, bottom 318) | — |
| Sector mini-map (`CornerPanel(800,480)` = 578, 322, 208 × 144) | 578–786 × 322–466 | owns the bottom-right corner; the scope sits **directly above it** (4 px gap) |
| Throttle readout plate (measured `_throttleBandLeft`) | left edge **682.7**, 682.7–742.7 × 221.5–258.4 | 5.33 px right of the scope's right edge (8·scale) |
| Throttle tape column | 752–778.7 × 112.7–384 | 74.7 px right of the scope |
| Master-caution panel | 638.5–784 × 39.3–80 | 94 px above the scope |
| Stores block | 64–221.3 × 247.3–314 | 312 px left of the scope |
| Bottom target pane | 446.7–710.7 × 404–457 | 86 px below the scope (top edge below the mini-map) |
| Pitch ladder numeral columns | 348.7–363.3 and 421.3–443.3 (per rung) | left of the scope |
| Speed tape column | 24–50.7 × 112.7–384 | far left |

So the scope occupies the **right-hand column immediately above the mini-map** —
x 533.3–677.3 (right of 0.66·w = 528), y 174–318 — which reads as mid-right in the frame because the
bottom 33 % of that column is the mini-map (322–466) and the caution panel ends at 80, leaving 94 px
of sky above it. The report states the position exactly that way.

The gap to the plate is now **stated and enforced**: `JetRadar.CompactPlateGap = 8` standard-stroke
units (`JetRadar.cs:47`), i.e. **5.33 px** at 800×480 (`right = rightLimit − 8·scale`,
`JetRadar.cs:109`), up from P1039b's 4 px (`BottomGap` = 6 units). `scope-gap-800-x2.png` /
`scope-gap-800-before-x2.png` are the 2× crops of that boundary before/after.

## 4. Defect 4 — duplicate target data at 800×480

**Which element is which.**

* The `TGT` block is the **designator readout** (P1037), drawn in `DrawTargetDesignator`
  (`src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.cs:888-1050`, the tagged fallback at
  `:965-1005`) — Jet's own text beside the selected target's bracket. Native has **no text beside the
  designator**: `indicator.cpp:411-450` draws only the bracket, and native prints the target's data
  once, in the pane.
* The bottom-centre block is the **native target pane** as a Jet data block (`DrawTargetBlock`,
  `JetHudRenderer.cs:1399-1500`), whose rows are the shared `HudTargetPane` fields (name, type/side,
  range or `LOC <sector>`, closure, hull/shield, speed, ore, kill bonus, TTI, `IN RANGE`) — the
  native pane's own content (`dialog.mdl:4049-4060` target name/type and range/`RANGE`, `:4250-4260`
  the `nnnn m` line, `:4334-4366` hull/shield gauges, `:4435-4480` the small-target name and bars;
  sentinels at `consoledata.cpp:202-212`). **Native shows the target data once** — in that pane.

**The rule (stated).** When no slot beside the target is clear, the designator readout takes the fixed
`TGT` slot and prints **only what the pane lacks**: always the relationship word (`HOSTILE` /
`FRIEND` / `NEUTRAL`, which the pane never carries), plus any row whose value the pane itself has no
data for (name when `pane.Name` is null, the range row when the pane has neither a distance nor a
`LOC` sector, the closure when `pane.ClosingSpeed` is null, hull/shield when the pane reports no
fraction). A **clear** slot beside the target keeps P1037's readout unchanged. Classic and Modern are
untouched (they never draw Jet's readout at all).

**Evidence** (`jet-audit-summary.json` → `targetReadoutTexts`, and `tgt-800-x2.png`):

| Scene | before (P1039b) | after |
|---|---|---|
| `jet-locked` 800×480 dark/bright | `TGT` + `HOSTILE` + `Enemy fighter with a long pilot name` + `1250 m CLO +43` + `HULL 060` + `SHLD 030` (117.3 × 85.3 at 225.3, 158) | box **51.3 × 32** at (225.3, 211.3), rows **`TGT`, `HOSTILE`** only |
| `jet-target` 800×480 dark/bright | same five rows | **`TGT`, `HOSTILE`** only |
| `jet-locked` / `jet-lockfull` / `jet-locklarge` 1920 | 506 × 104 with all rows | box **77 × 48**, rows **`TGT`, `HOSTILE`** only |
| `jet-target` 1920 (a clear slot exists) | full readout beside the target | unchanged (P1037 readout, not a fallback) |

The pane still prints the target's name, `1250 m CLO +43`, `HULL 060`, `SHLD 030` once (bottom
centre), so the pilot sees every reading exactly once.

## 5. Defect 5 — the compact annunciator row

`DrawCompactAnnunciators` (`JetHudRenderer.StoresCaution.cs:343`) now draws the lit words with
**`JetStoresPanel.WordGap` = 6 standard-stroke units** (`JetStoresPanel.cs:203`) — at 800×480 that is
4 px, i.e. ≈4 px of sky after the two 1 px keylines — and only reduces it as far as the ladder's
numeral column requires: the row is measured and the gap is the largest that still ends before
`LadderColumnLeft` (the ladder's left numeral column, 65 units from the boresight → **356.7 px** at
800×480, `JetStoresPanel.cs:208-211`).

Measured (`jet-audit-summary.json` → `annunciatorRow`, `annunciatorWordGap`):

| Scene | row ink box | gap | ladder column |
|---|---|---|---|
| `jet-flags` 800 (six words: `EYE CLK AUTO VLOCK FLAG ART`) | 176–354.0, width **178.0** | **3.07 px** (4.6 units) | 356.7 px → **2.7 px clear** |
| every other 800 scene (≤5 words) | 176–280, width 104 | **4 px** (6 units) | 356.7 px |

P1039 used a 2-unit gap (1.33 px at this scale) which left the words' keylines touching
(`AUTOVLOCK FLAG`); the six-word row cannot take the full 6 units before the ladder column, so it
takes the largest that fits and still ends clear. No annunciator row touches a ladder numeral
(`text-overlap-cross` 0 in every scene).

## 6. What changed

| File:line | Change |
|---|---|
| `src/.../Rendering/Jet/JetRadarLabels.cs:79` | `RingPad` (1 px) shared by the placement pass and the audit. |
| `:169` | `GlyphBox`: glyph + every overlay (bracket, nearest-enemy outer bracket, flag pennant, artifact mark, ripcord) as the obstacle box. |
| `:194-257` | `Rings`: the four band strokes as geometry (square outline segments, exact circle-vs-box). |
| `:267` | `SegmentHitsBox` (Liang–Barsky slab clip, pure). |
| `:299-340` | `LabelCellPixels` (4,800), `FullBlockCells` (12), `MinFullBlocks` (2), `LabelBudget`, `FullFormAllowed`. |
| `:405-475` | `Place` with glyph boxes (own skipped by index) and bands: a ring-crossed cell is taken only as a plated fallback; diagonal candidates need a clear leader. |
| `:497-520` | `LeaderCrosses` (another label, another glyph). |
| `:526-586` | `Scan` searches outward from the glyph's own cell (a priority block lands beside its contact), prefers a ring-free cell with a clear leader (tested for the first `LeaderSearchCells` = 20 free cells only, so a dense frame stays cheap), then a plated cell. |
| `src/.../Jet/JetRadar.cs:47,106-109` | `CompactPlateGap` 8 units; the compact right edge keeps it from the throttle plate. |
| `src/.../Jet/JetHudRenderer.Radar.cs:258-305` | The plate pass: mode caption, band captions, label blocks and marker words that a band crosses get a 0.82 plate drawn after the bands. |
| `:674` | `RadarPlateOpacity` 0.82 (the readout-plate rule). |
| `:833-975` | `PlanRadarLabel`: budget gate, full-form gate, glyph/band placement, priority hide instead of a covered slot. |
| `:976-1025` | `PrepareRadar` (points + glyph boxes before the captions), `RadarRings`, `RadarGlyphHits`, `RadarLeaderBlocked`. |
| `:100-140` | Per-frame counts: `RadarLabelBudget`, `RadarFullFormAllowed`, `RadarPlates`, `RadarPriorityHidden`. |
| `src/.../Jet/JetHudRenderer.cs:965-1035` | The `TGT` fallback prints only what the target pane lacks (relationship word + any row the pane has no value for). |
| `:227,330` | `AnnunciatorRow` (the measured compact row) for the audit/evidence. |
| `src/.../Jet/JetHudRenderer.StoresCaution.cs:329-390` | The compact annunciator row: `WordGap`, measured fit before the ladder column, `AnnunciatorGap`. |
| `src/.../Jet/JetStoresPanel.cs:203-211` | `WordGap` 6 units, `LadderColumn` 65 units, `LadderColumnLeft`. |
| `src/.../Jet/JetVectorPainter.cs:78-101` | `IJetRadarAudit` (contact index, glyph box, band stroke, leader, plate) — one type test per call on production/test surfaces. |
| `tools/hud2-proof/Program.cs` | `TextOverGlyph` + radar `StrokeThroughText` (+ plate exemption and count), per-scene `budget`/`fullFormAllowed`/`plates`/`priorityHidden`, `annunciatorRow`/`annunciatorWordGap`, `targetReadoutTexts`, schema 9. |
| `tests/.../Skins/JetRadarTests.cs` | +7 tests (glyph box extents, band detection, another glyph blocks a slot / own glyph does not, leader crossing, budget as a function of the scope, annunciator gap + ladder column, and the renderer's compact-scope audit with a radar-aware recorder). |
| `tests/.../Skins/JetStoresCautionTests.cs` | +1 test (compact annunciator word gap and the measured row before the ladder column). |
| `GLOSSARY.md` | New P1039c entry. |

No `docs/parity/*.csv` row changes status (presentation only).

## 7. Audit (96 Jet scenes)

```
HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0,
               text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0,
               text-overlap-same 0 over 96 scenes
HUD2_JET_RADAR_STROKES text-over-glyph 0, stroke-through-text 0, backing plates 812 over 96 scenes
HUD2_JET_RADAR_LABELS placed 426, shortened 179, hidden 219, priority hidden 1,
               never-hidden overflows 0, priority misses 1, words dropped 392 over 96 scenes
```

* **`TextOverGlyph`** — every radar text run against every OTHER contact's glyph/overlay box (the
  contact's own box excluded by index, leaders are separate strokes). 0 in every scene; 1,182 in the
  P1039b replay.
* **`StrokeThroughText` (radar)** — every band or leader stroke against every radar run's ink band,
  exempted only when a backing plate drawn after the stroke and before the run interrupts it (then the
  plate is counted). 0 in every scene; 898 in the replay.
* `plates` 812 over the run (per scene in `jet-audit-summary.json`): the mode caption, band captions,
  label blocks and marker words that sit across a band. Priority labels hidden by a genuinely full
  scope: 1 (the 63-contact 800 dense fixture); `never-hidden overflows` stays structurally 0 because
  no reading is ever committed to a covered cell.

## 8. Draw cost (dense battle, same scene)

`jet-radar-dense` — 63 contacts, 1920×1080, 120 frames after a warm-up, whole frames (`--jet`):

```
before: HUD2_JET_RADAR_COST scene=jet-radar-dense 1920x1080 contacts=63 jet=0.788 ms/frame modern=1.121 ms/frame ratio=0.70
after:  HUD2_JET_RADAR_COST scene=jet-radar-dense 1920x1080 contacts=63 jet=1.250 ms/frame modern=1.120 ms/frame ratio=1.12
```

The pass costs **+0.46 ms/frame** on this frame: every candidate is now tested against up to 63 glyph
boxes and four bands (the dense scope's cells are mostly occupied, so more priority labels take the
scan), and 23 plates are additional fill submissions. The Modern radar on the identical contact set
measured 1.120 ms in the same run (unchanged from the P1039b run's 1.121 ms), so the frame's HUD cost
rose from 0.70× to 1.12× Modern on the densest proof scene. Steady-state allocation is still
0 B/frame (the P1039b allocation test passes unchanged: 200 frames < 32 KB, every candidate span is
preallocated, the scan is index loops over preallocated arrays, and the plate/fill path allocates
nothing).

## 9. Gate log

| Gate | Command | Result |
|---|---|---|
| Zero-delta | `--capture` before the first edit (`~/allegiance-evidence/P1039C/baseline`) → `--compare` on the final tree (`.../compare-final3.log`) | **1,848 combinations, 0 pixel diff(s), 0 semantic diff(s)**, 84 animation-only (masked wall-clock badge), 0 corrupt |
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (`~/allegiance-evidence/P1039C/build-release.log`) |
| Tools build | `tools/build-tools.sh` | **all tool projects built, 0 errors** (`~/allegiance-evidence/P1039C/build-tools.log`) |
| New/affected tests 3× alone | `--filter "FullyQualifiedName~JetRadarTests\|JetTargetLockTests\|JetStoresCautionTests"` ×3 | **182/182 each run** (`Passed 182, Failed 0, Skipped 0`; `~/allegiance-evidence/P1039C/tests3x.log`) |
| Skins namespace | `--filter FullyQualifiedName~Skins` | **355/355** (was 347; +8) |
| Jet capture + audit | `hud2-proof --jet` (`~/allegiance-evidence/P1039C/jet-final4.log`, schema 9) | `HUD2_JET_CAPTURE_PASS 96 scenes`; every class 0 (§7) |
| Fast tier | `tools/run-tests.sh --tier fast` | **15,093 tests, 549.3 s, 4 failed / 7 not executed**, 9/9 infrastructure checks, 6 executions over the calibrated 11.539 s limit (all passed). The four failures are already-ledgered load families, each **pass/pass/pass alone** except the P660 clock-observation case (pass/pass/fail — the same 14.85–14.90 boundary the ledger records; no clock code is touched): `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump`, `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`, `P467E2eGunDamageDevLiveTests.Completing_dev_143_raises_gun_damage_in_combat`, `P813cReconnectGraceTests.Seated_connection_cannot_take_or_retire_a_parked_seat`. Ledgered in `docs/TEST_FLAKES.md` (§ P1039c); no HUD file is on any of those paths. |
| AOT publish | not run — no `src/Allegiance.Server` or `src/Allegiance.Core` change | n/a |
| Training gate | not run — no training-affecting file changed (HUD renderer + proof tool + tests only) | n/a |


## 10. Screenshots (each opened; where vision is unreliable, the measured boxes decide)

All crops are **2× nearest-neighbour** so the pixel font is inspectable; the assets dir holds them and
`jet-audit-summary.json` holds every measured box behind the statements.

| Image | What is visible |
|---|---|
| `radar-sparse-1920-dark-x2.png` | The square scope with the four bands; `RDR Default` in the corner; contact blocks in the cells around their glyphs, each numeric block on its own plate where a band runs beneath it; the mode caption on its own plate at the corner. |
| `radar-sparse-1920-bright-x2.png` | The same layout on the bright backdrop; the dark plates are the only dark rectangles in the scope. |
| `radar-sparse-800-dark-x2.png` | The 144 px scope above the mini-map: 7 blocks (4 shortened to bare metres), 2 hidden; the glyph field in the centre is untouched. |
| `radar-round-800-dark-x2.png` | The inscribed circular RWR at 144 px: band captions on the vertical spoke (plated where a neighbouring circle crosses them), labels in the free space, no text on the circle strokes. |
| `radar-dense-1920-x2.png` | The dense scope: 24 separate blocks (16 shortened) in the scope's cells, glyphs complete in the middle; no block overlaps another block, a glyph, or a band (a band under a block is interrupted by its plate). |
| `radar-dense-1920-before-x2.png` | P1039b for comparison: the same scope with the text pile — several numbers on glyphs and bands (78 / 37 measured). |
| `radar-dense-800-x2.png` | 10 single-row blocks in two edge columns with the glyph cluster in the middle; each number's plate is visible as a dark box; 53 labels hidden, each glyph kept. |
| `radar-dense-800-before-x2.png` | P1039b for comparison: 15 blocks including multi-row ones, the scope a dense mass. |
| `radar-dense-4k-x2.png` | The 4K scope: 26 blocks (16 shortened), vectors crisp, the plates visible under the numbers that cross a band. |
| `radar-lod-All-1920-x2.png`, `radar-lod-Target-1920-x2.png`, `radar-lod-Minimal-1920-x2.png` | The same placement rules under the four LOD modes; the band captions follow the contact set (All 800…3200, Target 400…1600, Minimal 250…1000). |
| `locked-800-after.png` / `locked-800-before.png` | After: the `TGT` block above the stores with only `TGT` and `HOSTILE`, the target's data once in the bottom-centre pane, the scope in the right-hand column with the stated gap to the `80 %` plate. Before: the five-row `TGT` block duplicating the pane. |
| `tgt-800-x2.png` | 2× crop of the fallback block: `TGT` over `HOSTILE`, nothing else. |
| `scope-gap-800-x2.png` / `scope-gap-800-before-x2.png` | 2× crop of the scope's right edge and the throttle plate: 5.33 px after, 4 px before. |
| `target-800-after.png` | The `jet-target` 800 frame: `TGT`/`HOSTILE` block plus the pane's rows, the scope, and the stores annunciator row ending before the ladder column. |

**Honesty note on reading the images.** At these pixel-font densities the vision model's prose is
unreliable: it called the square scope a circle twice, invented edge coordinates, and reported
"labels on the ring lines" for frames where the audit measures 0 strokes through any text and 810
plates interrupting the bands. Every placement statement above is tied to the measured boxes in
`jet-audit-summary.json` (label boxes, glyph boxes, plate boxes, annunciator row) and to the audit
counts; the images were opened and their coarse content — the block count and separation, the plates,
the scope position, the `TGT`/`HOSTILE` block, the annunciator row — matches those numbers. Where the
vision's reading of a *placement* contradicted a measured rectangle, the rectangle decides.

## 11. Anything unfinished / caveats

* **The dense 800×480 fixture hides 53 labels and one priority block.** The fixture's 63 contacts put
  a glyph box on every cell of the 144 px scope; a never-hidden label with no clear cell is hidden
  rather than drawn over a glyph (§2.1). `RadarPriorityLabelMisses` is therefore 1 in the run (it
  counts exactly that case); `never-hidden overflows` is 0.
* **Plates are common at 1080p too** (812 over 96 scenes): the slot offsets and the four bands meet
  often, so a label that crosses a band gets its plate. The slot test is preferred and used wherever
  the scope has ring-free sky; the plate only makes the reading legible where it does not.
* **Marker words can be dropped** on a crowded scope (392 across the run) and a leader stroke can be
  dropped when it would cross another label/glyph; the block stays.
* **Draw cost rose** by 0.48 ms/frame on the densest proof frame (§8); it is the cost of testing
  glyphs and bands for every candidate plus the plates, and it is reported rather than hidden.
* The `!` badge's animation-only diffs in the Classic/Modern matrix are fixture-owned (83, inside the
  recorded mask, every entry `outside: 0`).
* `docs/TEST_FLAKES.md` gained a P1039c section: the four fast-tier failures and their 3×-alone results (the P813c seat case is a new row in that family).
* New media (19 PNGs + 1 JSON, 0.76 MiB) needs Domingo's review before any push.
* The brief file `P1039C_BRIEF.md` stays untracked (excluded through the shared git dir's
  `.git/info/exclude`); `.scratch/p1039c/` was deleted.

## Native intent

Native's radar is an allowed-set projector with no collision logic: `RenderBlips`/`DrawBlip` place
every contact the per-kind LOD admits at its projected position or pin it to the real direction at the
edge (`WinTrek/radarimage.cpp:286-427`, the eye-space clamp `WinTrek.cpp:7882-7960`), the per-kind LOD
decides which icons exist (`radarimage.cpp:703-980`), and the text loop draws **every** eligible label
unconditionally at its own offset (`:1189-1461`; name `:1396`, range `:1402`, the bare `_itoa` digits
`:1209-1218`), each block placed outside its icon along the radial direction from the scope centre
(`:360-379,1284-1311`); its only priority is list order — target/accepted/threat labels are pushed to
the end and drawn on top, everything else to the front (`:419-425`). Native has no range scale at all
(Classic and Modern draw no rings; the Jet bands are the presentation's own numbers over the frame's
own contacts) and no declutter. The target data is printed **once**, in the pane the Jet bottom block
ports (`dialog.mdl:4049-4060,4250-4260,4334-4480`; sentinels `consoledata.cpp:202-212`); the
designator is a bracket only (`indicator.cpp:411-450`). P1039c therefore keeps every native-eligible
reading and its native form, and adds exactly the placement/visibility decision a corner-thumbnail
radar never needed — glyph boxes and bands as obstacles, the scope-sized label budget, the plate that
interrupts a band under a reading, and the fallback block that prints only what the pane lacks — all
behind `hud.jetSkin`, over the same contact snapshot and the same LOD tier.

## Modern approach

The whole pass is plain geometry over preallocated arrays: `GlyphBox`, `Rings`, `SegmentHitsBox`,
`LabelBudget`/`FullFormAllowed`, `Place`, `Scan` and `LeaderCrosses` live in the pure
`JetRadarLabels` (headlessly testable, no allocation), the scope rule in `JetRadar.Scope`
(`CompactPlateGap`), the pane-complement rule in `DrawTargetDesignator`, and the plate/slot
decisions in one partial renderer file that plans the frame before submitting a run. The proof stamps
every radar run with its contact (`IJetRadarAudit`) so `TextOverGlyph` and the radar
`StrokeThroughText` are facts in `jet-capture.json`/`jet-audit-summary.json`, records every plate so
the "interrupted band" rule is measurable rather than asserted, and records the per-scene
placed/shortened/hidden/priority-hidden counts, the budget and the drawn rows. `TextOverlapSame`,
`TextOverlapCross`, `stroke-vs-text`, `text-outside-plate`, `text-off-screen`, icon size, cue-vs-readout
and legible-behind-pane all stay 0 across the 96 scenes, and Classic/Modern stay pixel- and
semantically identical (1,848/1,848 against the baseline captured before the first edit).
