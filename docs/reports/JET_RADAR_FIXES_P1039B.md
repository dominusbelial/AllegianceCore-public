# P1039b — Jet HUD skin: radar label legibility, compact radar placement, 800×480 readout slot

Follow-up to P1039 (`docs/reports/JET_RWR_P1039.md`, merged at `b33ba5cc`), built in the
`hermes/p1039b` worktree of `AllegianceCore` at Claude's master. No commit, no push, no stash, no
remote host, no Docker (not installed here). Scratch lived in `.scratch/p1039b/`; the raw captures,
the zero-delta baseline, the compare log and the gate logs live outside the repo in
`~/allegiance-evidence/P1039B/`. Task evidence is `docs/reports/JET_RADAR_FIXES_P1039B_assets/` —
17 PNGs + `jet-audit-summary.json`, **18 files, 893,749 bytes = 0.85 MiB** (under the 2 MB cap).

Jet stays presentation-only: every value is the shared `HudModel`/`HudFrame`/`HudContext` data plus the
presentation-only `HudContact.RelativeBearingRadians` P1039 added. No server, Core, wire, audio, input,
contact-eligibility, targeting, command or Tab-starmap change; no change to which contacts or labels are
eligible (the native LOD tier stays the authority). Jet remains behind `hud.jetSkin` (default off).

**Status**

| Item | Status |
|---|---|
| 1 — radar label placement pass (slot order, leader stroke, short form, hide-but-keep-glyph, priority order, ring-caption reserve, native-intent departure stated) | **done** |
| 2 — scope range mapping checked against native/Modern for the same LOD; crowding was a Jet mapping defect, fixed | **done** |
| 3 — 800×480 compact scope moved to a reserved corner zone (flight-path area, throttle plate, caution panel clear) | **done** |
| 4 — 800×480 designator readout: fixed labelled `TGT` slot instead of the clamp corner | **done** |
| Audit — `TextOverlapSame` added; every class 0 in 96 scenes; per-scene placed/shortened/hidden counts | **0 / 0 / 0** |
| Gate — baseline-first zero-delta, 0 B/frame, tests 3×, build 0/0, tools build, one fast tier, evidence | see §9 |

## 1. Zero-delta baseline first

The **unmodified** tree was built (`+dirty` is the worktree's untracked brief markdown only — no source file
differed from `b33ba5cc4111`, which the proof confirms by the 0 pixel / 0 semantic compare below) (`tools/hud2-proof`, Release) and captured before the first edit:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1039B/baseline
HUD2_CAPTURE_PASS 1848 combinations in 7.8 min -> .../baseline      (manifest: commit b33ba5cc4111+dirty)
HUD2_MANIFEST 1848 combinations, commit b33ba5cc4111+dirty, driver llvmpipe (LLVM 19.1.7, 256 bits)
```

After all changes the same tree was compared against it:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)
```

1848 = 22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins (924 Classic + 924 Modern).
**Classic and Modern stay pixel- and semantically identical: 1,848/1,848, 0 pixel diffs, 0 semantic
diffs.** The 84 animation-only entries are inside the fixture's recorded animation mask (the wall-clock
`!` badge; P1039 recorded 83 — the badge's pixels depend on when the frame is rendered, and they are
excluded by the mask, every entry `outside: 0`). The shared model is untouched (no `HudModel`,
`HudFrame`, `HudContext` or wire field changed in this story), so neither renderer can move.

## 2. Defect 1 — the radar contact labels

### 2.1 What was wrong

`jet-radar-1920x1080-dark.png` (10 contacts) stacked the name / range / verb lines into one blob and the
ring captions crossed them; `jet-radar-dense-*` was a solid mass. Measured on the unmodified tree, the
number of intersecting text pairs inside the 96 Jet scenes was **41** (sparse 1920), **60** (sparse 800),
**924** (dense 1920), **877** (dense 4K) and **1,427** (dense 800).

Native's own radar does exactly this: its text loop draws **every eligible label unconditionally**, with
no collision test at all — `for (…) { … pcontext->DrawString(pfont, data.m_color, offset, name) … }`
(`WinTrek/radarimage.cpp:1189-1461`; the per-contact offset is `:1285`, the name `:1396`, the range
`:1402`), each block placed outside its icon along the **radial direction from the scope centre**
(`:360-379`, direction `(0,−1)` at the centre, applied `:1284-1311`). The pile is native behaviour, not a
port bug — tolerable only because native's radar is a corner thumbnail with a fixed bitmap font.

### 2.2 The placement pass (Jet-only, stated departure)

`src/.../Rendering/Jet/JetRadarLabels.cs` (new, pure) + `JetHudRenderer.Radar.cs`:

* **Slot order**, tried for every label: **right → left → above → below → up-right → up-left →
  down-right → down-left**. Inside a slot the block is first tried at the slot's own position, then slid
  *away* from the glyph along the slot's axis (one block, two blocks) and finally sideways (half a
  block either way). A diagonal placement draws a **short leader stroke** from the glyph's bracket edge
  to the block (stopped 2.5·scale short of its ink). The order keeps native's "away from the icon"
  intent (`radarimage.cpp:360-379`) while making the choice deterministic and unit-pinned.
* **Priority** (placed first, never hidden): **(1) selected target, (2) order subject, (3) threat /
  missile, (4) the pilot's own command verb (`ACC`/`QUE`)**; then everything else **by nearest range**,
  ties in the model's own order. Native's priority is weaker but points the same way: target / accepted /
  threat labels are pushed to the END of its text list (drawn on top), everything else to the front
  (`radarimage.cpp:419-425`).
* **Fallback ladder**: the full native block → **the range line alone** → **native's own bare `_itoa`
  digits** (`radarimage.cpp:1209-1218`; the `nnnn m` suffix is the Jet full form) → **hidden, glyph
  kept** (the glyph, every overlay bracket and the `ACC`/`QUE` word stay). A never-hidden contact is never
  dropped: when its eight slots are taken it takes the **first free position of a scope-wide scan**
  (cells of the block's own size, half-width steps, row-major) with a leader back to its glyph; only if
  the scope is genuinely full does it keep the shortest form over another block, which the audit counts
  as an *overflow* (0 in every scene here).
* **Ring captions** live on **one fixed spoke** (the vertical axis, right of it, above the ring they
  name) and their rects are reserved before any contact label is placed; a caption that would touch
  another caption on the spoke is skipped rather than stacked (at 800×480 the rings are closer than one
  text line). The `RDR <mode>` corner caption is reserved the same way.
* **Plan before draw**: the frame is drawn in the order frame → bands → glyphs → leader strokes → band
  captions → marker words → label blocks, so **all strokes precede all text** and no stroke can read as
  a strikethrough. The marker words (`M`/`P`/`T`, `SUB`, `!`, `RIP n`, `ACC`/`QUE`) are planned in the
  same pass; the verb and `SUB` try four positions around their glyph before being dropped, a class
  letter is droppable.
* **0 B/frame**: preallocated arrays (`MaxRadarPlan = 512` contacts, 512×(1 label + 5 words) obstacle
  rects), index loops, cached strings (`RadarLabelModel.RangeText`/`RangeLineText`, `RIP n`). The
  placement test uses an early-exit `Overlaps`; the summed `Covered` area is only needed for the
  least-covered fallback.
* **Native-intent departure, stated**: this is a Jet-only presentation rule behind `hud.jetSkin`
  (default off). It never shows a label native would not (it only *hides* or *shortens* labels the native
  per-kind LOD tier already allowed, `RadarLabelModel.Resolve`) and never drops or adds a contact.

### 2.3 Before → after (measured, per scene)

| Scene (dark) | intersecting text pairs before → after | labels placed / shortened / hidden | band values before → after |
|---|---|---|---|
| `jet-radar` 1920×1080 | 41 → **0** | 9 / 0 / 0 | 1250·2500·3750·5000 → **800·1600·2400·3200** |
| `jet-radar` 800×480 | 60 → **0** | 8 / 2 / 1 | same → **800·1600·2400·3200** |
| `jet-radar-round` 1920×1080 | 41 → **0** | 9 / 0 / 0 | same |
| `jet-radar-round` 800×480 | 60 → **0** | 8 / 2 / 1 | same |
| `jet-radar-dense` 1920×1080 | 924 → **0** | 36 / 16 / 27 | 1250·2500·3750·5000 → **1000·2000·3000·4000** |
| `jet-radar-dense` 800×480 | 1,427 → **0** | 15 / 9 / 48 | same → **1000·2000·3000·4000** |
| `jet-radar-dense` 3840×2160 | 877 → **0** | 42 / 25 / 21 | same → **1000·2000·3000·4000** |
| `jet-radar-kinds` 1920 / 800 | 47 / 86 → **0** | 11/0/0 · 9/5/2 | same → **1000·2000·3000·4000** |
| `jet-radar-nobearing` 1920 / 800 | 19 / 31 → **0** | 9/0/0 · 9/2/0 | same → **800·1600·2400·3200** |
| LOD All / Default / Target / Minimal 1920 | 45 / 41 / 10 / 2 → **0** | 9 / 9 / 4 / 1 placed, 0 shortened, 0 hidden | Default 800…3200, Target 400…1600, Minimal 250…1000 |

Dense scenes hide labels — expected; the assert is that the **priority** classes never hide, and
`priority misses = 0` in all 96 scenes (§7).

## 3. Defect 2 — the scope range mapping

**Verdict: the crowding was a Jet mapping defect, not a native range.** Native has **no range scale at
all** — a blip is placed by the native projection (`RenderBlips` → `DrawBlip`, `radarimage.cpp:286-427`,
the eye-space clamp `WinTrek.cpp:7882-7960`) and the only metre numbers on it are the per-contact range
labels; Classic and Modern draw brackets at projected points and no rings. So there is no native/Modern
scope range for the same LOD to match.

What P1039 did: `ScopeRange` rounded the frame's **maximum** contact range up to the next 1/2/5×10ⁿ
value. A sparse frame whose farthest contact was at 2,740 m got a **5,000 m** outer ring — every contact
sat inside the inner 55 % of the instrument and 45 % of the radius was empty sky (the 3,750 m ring the
review saw was the third band of that 5,000 m scale).

Fix (`JetRadar.ScaleFor`): the **band step** is rounded instead — `step = RoundUp(farthest / 4)` on the
1/1.5/2/2.5/3/4/5/6/8×10ⁿ series, outer ring = `4·step`, so the outer ring is **at most 1.6× the farthest
contact** and the captions stay short round numbers. The contacts now spread across all four bands:
2,740 m → 800/1600/2400/3200 (outer 1.17×); 3,639 m → 1000/2000/3000/4000 (outer 1.10×). No sensor range
is invented: the numbers are the frame's own contacts, and anything at or beyond the rim is still pinned
to it.

## 4. Defect 3 — the 800×480 compact scope zone

**The zone (stated)**: at a viewport height below 700 px the scope is the **bottom-right corner band**:

* **side** = `min(0.32·h, 0.30·h)` → **144 px** at 800×480 (`CompactSideFraction`, P972 §4.5 shrink);
* **right edge** at the throttle tape's own band (the measured left edge of the readout plate / IDLE-REV
  words) minus the gap → **678.7 px**: the scope never touches the `80 %` plate;
* **bottom edge** at the sector mini-map's top minus the gap → **318 px**: the shared corner mini-map
  stays visible (it is the same instrument band native uses);
* **left edge** never left of **0.66·w = 528 px** (`CompactLeftFraction`): the flight-path column — the
  pitch ladder's right numeral column ends at ≈55 % of the width and the `ACC`/`CUR` order word at ≈64 %
  — so the scope cannot reach the boresight area; if the corner band were ever narrower than the floor
  (`CompactMinSide = 64 px·scale`) the left margin gives way, never the four-band structure;
* **top edge** stays below the measured bottom of the master-caution panel + gap: the caution panel is
  the higher-priority element and is never moved.

At 800×480 the scope moved from **(525.1, 164.4, 153.6)** — the right-middle of the view, its right edge
4 px from the throttle plate's left edge (678.7 vs 682.7) and the lock cue's bottom arcs running through
it — to **(534.7, 174, 144, 144)**, the corner band. Every contact class is still drawn (glyphs, overlays, `M`/`P`/`T`/`SUB`/`RIP`/`!`), only
the label blocks are decluttered, exactly as P972 §4.5 requires.

**The lock cue over the scope**: the cue is a world-anchored mark and the scope is fixed furniture, so a
cue arc that would enter the scope's rectangle is now **clipped** (`JetTargetGeometry.ArcEntersRect`,
mirroring native's own `radiusBracket` edge clip `radarimage.cpp:308-319`); the cue's centre, radius,
state and dash timing are untouched. The **target designator's corner strokes** remain a world-anchored
mark that may share sky with the fixed instrument (P1039's stated exception; the audit's stroke-vs-text
keeps that exemption and reports 0 hits).

## 5. Defect 4 — the 800×480 designator readout slot

**The rule (stated)**: the designator readout keeps the priority order right → left → below → above, but
**a candidate the viewport clamp has to move is no longer "clear"** — it is a block that fell into a
screen corner, not a slot beside the target (`JetReadoutPlacement.Place`, `P1039b`). When no slot beside
the target is clear, the readout takes the **fixed, labelled target-data slot**: a `TGT`-tagged block in
the reserved bottom-left band, whose **bottom edge sits on the measured top edge of the stores block**
(`JetReadoutPlacement.TargetZone`/`TargetSlot`, anchored on `_storesTop`), between the stores column's
right edge (`96 + 236 + 6` standard units) and the boresight column's left margin
(`w/2 − 80·scale`). That zone is clear of the heading tape (top), the master-caution panel (top right),
the stores block (below it), the pitch ladder / `PITCH` column (right of it), the radar scope and the
mini-map (bottom-right corner). At the compact window the band is narrower than one three-row block, so
the **hull and shield readings take one line each** — no field is dropped, the block just gets narrower.

Evidence (audit JSON, `targetReadout` before → after):

| Scene | before | after | slot |
|---|---|---|---|
| `jet-locked` 800×480 dark/bright | (2.7, 2.7) — the top-left corner | **(225.3, 158) 117.3×85.3** `TGT` block | Left (TGT) |
| `jet-target` 800×480 dark/bright | (2.7, 2.7) | **(225.3, 158) 117.3×85.3** | Left (TGT) |
| `jet-locklarge` 800×480 | (2.7, 2.7) | **(550.8, 349.4)** — a clear slot beside the cue | Below |
| `jet-locked` 1920×1080 | (446.0, 283.3) — the least-covered clamp (P1039's listed exception) | **(338, 579) 506×104** `TGT` block | Left (TGT) |
| `jet-lockfull` 1920×1080 | (1168.9, 89.6) | **(338, 579) 506×104** `TGT` block | Left (TGT) |
| `jet-locklarge` 1920×1080 | (427.3, 259.3) | **(338, 579) 506×104** `TGT` block | Left (TGT) |
| `jet-locksmall` 800 / 1920 | (193.2, 119.5) / (1224.3, 386.2) | (599.1, 119.5) / (1224.3, 386.2) — a clear slot exists | Right |

The P1039 intentional exception (the 1920 `jet-locked` readout sharing 22 px with the `+30` ladder
numeral) is **gone**: that frame now uses the TGT block, so `text-overlap-cross` is 0 in every scene.

Two supporting fixes came out of the same defects, both measured rather than asserted:
`DrawTargetBlock` now registers its **measured** width as furniture (the old fixed `140·scale`
under-reported the 264 px pilot name at 800×480, so a readout could be placed over ink the furniture list
did not cover), and `JetStoresPanel.BlockWidth` is the single source of the stores block's authored width.

## 6. What changed

| File:line | Change |
|---|---|
| `src/.../Rendering/Jet/JetRadarLabels.cs` (new) | Pure placement pass: 8 slots + bounded slides, priority (`Priority`/`Before`/`NeverHidden`), `Place`, `Scan`, `Overlaps`/`Covered`/`Fits`, `Leader`, `Inner`. |
| `src/.../Rendering/Jet/JetRadar.cs:45-66,91-120` | `CompactHeight`/`CompactSideFraction` (0.30)/`CompactLeftFraction` (0.66)/`CompactMinSide`; `Scope(...)` gains the compact corner zone and a caution `topLimit`. |
| `src/.../Rendering/Jet/JetRadar.cs:135-185` | `ScopeRange` rounds the BAND (`ScaleFor`), outer ring ≤ 1.6× the farthest contact; `RoundRange` kept for the 1/2/5 series. |
| `src/.../Rendering/Jet/JetReadoutPlacement.cs:94-165` | `Place`: a clamped candidate is never `Clear` (it is only the least-covered fallback). |
| `src/.../Rendering/Jet/JetReadoutPlacement.cs:186-226` | `TargetZone`/`TargetSlot`: the reserved TGT band, bottom-anchored on the measured stores top. |
| `src/.../Rendering/Jet/JetStoresPanel.cs:26-30` | `BlockWidth = 236` (the stores block's authored width, one source for the zone and the furniture). |
| `src/.../Rendering/Jet/JetHudRenderer.Radar.cs:52-95` | Preallocated plan state (512 contacts × label + 5 words), per-frame counts (`RadarLabelsPlaced/Shortened/Hidden/Overflows/PriorityMisses/WordsDropped`), planned box/rows/word arrays. |
| `src/.../Rendering/Jet/JetHudRenderer.Radar.cs:97-232` | `DrawRadar`: compact corner zone, reserved ring captions (one spoke) + `RDR` corner, `PlanRadar`, then glyphs → leaders → captions → words → labels. |
| `src/.../Rendering/Jet/JetHudRenderer.Radar.cs:360-485` | Word text/positions (4 variants for the verb and `SUB`), planned word anchors, label draw from the planned rows/box. |
| `src/.../Rendering/Jet/JetHudRenderer.Radar.cs:566-815` | `PlanRadar` (stable priority sort), `PlanRadarWords` (priority verbs first, labels second, other words last), `PlanRadarLabel` (full → bare range → name → scan → overflow), `CommitRadarLabel`. |
| `src/.../Rendering/Jet/JetHudRenderer.cs:92,954-1005` | `TGT` word; the clamped-slot fallback to `TargetSlot`, compact split hull/shield rows, `_targetReadoutBox` includes the TGT row. |
| `src/.../Rendering/Jet/JetHudRenderer.cs:220-230,248,1437-1449` | `_storesTop` (measured stores top, reset per frame); target-pane furniture width = the measured widest row. |
| `src/.../Rendering/Jet/JetHudRenderer.cs:1160-1192` | `DrawLock`: a cue arc that would enter the fixed scope is clipped (`ArcEntersRect`). |
| `src/.../Rendering/Jet/JetHudRenderer.StoresCaution.cs:92-96` | Records `_storesTop`; furniture width from `JetStoresPanel.BlockWidth`. |
| `src/.../Rendering/Jet/JetTargetGeometry.cs:164-188` | `ArcEntersRect` (pure arc-vs-rect test for the cue clip). |
| `tools/hud2-proof/Program.cs` | `TextOverlapSame` (same-element pairs) + samples, per-scene `radar` counts/labels/bands, `HUD2_JET_RADAR_LABELS` line, schema 8. |
| `tests/.../Skins/JetRadarTests.cs` | +6 tests: slot order, priority queue, first-free-slot/covered fallback, clamp-is-not-clear + TGT slot, compact corner zone, crowded-scope (no priority miss, no overflow, no two radar texts intersect); the band test now pins the band rounding. |
| `GLOSSARY.md` | New P1039b entry; the P1039 entry's band/schema lines corrected. |

No `docs/parity/*.csv` row changes status (presentation only).

## 7. Audit (96 Jet scenes)

```
HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0,
               text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0, text-overlap-cross 0,
               text-overlap-same 0 over 96 scenes
HUD2_JET_RADAR_LABELS placed 489, shortened 130, hidden 156, never-hidden overflows 0,
               priority misses 0, words dropped 376 over 96 scenes
```

`TextOverlapSame` is the new class: any two text boxes drawn by the **same element** that intersect
(radar labels, ring captions, marker words, plate rows). It is 0 in every scene — and `text-overlap-cross`
(the P1039 cross-element class) is now 0 too, where P1039 had its one listed exception. Per-scene
placed/shortened/hidden counts are in §2.3 and in `jet-audit-summary.json` (`radar.placed`,
`radar.shortened`, `radar.hidden`, `radar.overflows`, `radar.priorityMisses`, `radar.wordsDropped`).

The never-hidden assertion is measured, not asserted: `RadarPriorityLabelMisses` counts, for every
contact whose rank is target/subject/threat/verb **and** whose native tier returns a label, a plan with no
block — 0 in every scene. `RadarLabelOverflows` (a priority label that had to share a cell) is 0 too.

## 8. Draw cost (dense battle, same scene)

`jet-radar-dense` — 63 contacts with labels at LOD All, 1920×1080, 120 frames after a warm-up, whole
frames:

```
before: HUD2_JET_RADAR_COST scene=jet-radar-dense 1920x1080 contacts=63 jet=1.353 ms/frame modern=1.519 ms/frame ratio=0.89
after:  HUD2_JET_RADAR_COST scene=jet-radar-dense 1920x1080 contacts=63 jet=1.095 ms/frame modern=1.505 ms/frame ratio=0.73
```

The pass costs less than the text it removes: on this dense frame 27 labels and 22 marker words are
hidden, so the frame submits fewer runs than P1039's, and the placement search is an early-exit overlap
test per candidate (the summed-area test is only used for a least-covered fallback). Steady-state
allocation is 0 B/frame (the P1039 allocation test still passes, 200 frames < 32 KB).

## 9. Gate log

| Gate | Command | Result |
|---|---|---|
| Zero-delta | `--capture` before the first edit (`~/allegiance-evidence/P1039B/baseline`) → `--compare` on the final tree (`.../compare-final.log`) | **1,848 combinations, 0 pixel diff(s), 0 semantic diff(s)**, 84 animation-only (masked badge), 0 corrupt |
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** (`~/allegiance-evidence/P1039B/build-release.log`) |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built**, 0 errors (`~/allegiance-evidence/P1039B/build-tools-final.log`) |
| New/affected tests 3× alone | `--filter "FullyQualifiedName~JetRadarTests|JetTargetLockTests|JetStoresCautionTests"` ×3 | **174/174 each run** (`Passed 174, Failed 0, Skipped 0`; `~/allegiance-evidence/P1039B/tests3x.log`) |
| Skins namespace | `--filter FullyQualifiedName~Skins` | **347/347** (was 341; +6 Jet radar tests) |
| Jet capture + audit | `hud2-proof --jet` (`~/allegiance-evidence/P1039B/jet-final2.log`) | `HUD2_JET_CAPTURE_PASS 96 scenes`; every class 0 (§7) |
| Fast tier | `tools/run-tests.sh --tier fast` | **15,056 tests, 721.7 s, 15,046 passed / 3 failed / 7 not executed**, 9/9 infrastructure checks, 8 executions over the calibrated 13.259 s limit (all passed). The three failures are existing ledgered families and each passed **3/3 alone** (`~/allegiance-evidence/P1039B/flakes3x.log`): `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` (chat-receipt ordering `ChatTarget 0` vs `-2`, `TEST_FLAKES.md` many entries), `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (remote death latch, `TEST_FLAKES.md:50`), `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` (warp receipt, `TEST_FLAKES.md:76`). No HUD file is on those paths. |
| AOT publish | not run — no `src/Allegiance.Server` or `src/Allegiance.Core` change | n/a |
| Training gate | not run — no training-affecting file changed (HUD renderer + proof tool + tests only) | n/a |

## 10. Screenshots (each opened and read)

| Image (assets dir) | What is visible |
|---|---|
| `radar-sparse-1920-before.png` / `-after.png` | Before: the scope's text is a tight pile of numeric/name lines with the ring captions inside it. After: separated blocks — the ring captions on the vertical spoke, contact blocks to the right/left of their glyphs, the `RDR All` word in the corner; the scope keeps its bottom-right position. |
| `radar-round-800-before.png` / `-after.png` | Before: the compact circle sits mid-right of the view with its labels around the centre and the throttle `80 %` plate at its right edge. After: the circle is in the bottom-right corner band, above the mini-map area, with a smaller label set and the band captions stacked on the spoke. |
| `radar-dense-1920-before.png` / `-after.png` | Before: a solid mass of text over the whole scope. After: a readable set of blocks (36 placed, 27 hidden) with every glyph still drawn; no two text boxes touch. |
| `radar-dense-800-before.png` / `-after.png` | Before: text filling the middle of the frame. After: the compact corner scope with 15 placed label blocks (9 shortened, 48 hidden), glyphs complete. |
| `radar-dense-4k-after.png` | The 4K scope: 42 blocks placed (25 shortened, 21 hidden), vectors crisp, no sub-pixel hairline loss. |
| `radar-sparse-bright-1920-after.png` | The same separated layout over bright space. |
| `radar-lod-All/Target/Minimal-1920-after.png` | The same scope and placement under the four LOD modes (All/Default/Target/Minimal), the band captions following the contact set (Default 800…3200, Target 400…1600, Minimal 250…1000). |
| `locked-800-before.png` / `-after.png` | Before: `HOSTILE En …` parked in the top-left screen corner beside the heading tape; the scope in the right-middle with the lock cue across it. After: a `TGT` block with `HOSTILE`, the shortened name, `1250 m CLO +43`, `HULL` and `SHLD` on their own lines, sitting above the stores block; the scope in the corner, the cue clipped where it would enter it. |
| `tgt-800-after.png` | 1:1 crop of that block: `TGT` / `HOSTILE` / `En ...` / `1250 m CLO +43` / `HULL 060` / `SHLD 030`, every line separate. |
| `f4-800-after.png` | The F4 pane scene: the scope is suppressed (it is a modal overlay band, as native draws its inventory over the radar) and the pane's rows stay clear. |

**Honesty note on reading the images**: at these pixel-font densities the vision model's prose is
unreliable (it reported "concentric circles" for the square scope in P1039, and this time it described
overlapping text in frames the audit measures as 0 overlaps — e.g. it claimed stacked text in
`radar-dense-800-after.png` where no two text boxes intersect by ≥ 1 px). Every claim above about
*placement* is tied to the measured boxes in `jet-audit-summary.json` and to the audit counts; the images
were opened and their coarse content (scope position in the corner, the `TGT` block above the stores,
the bundle of label blocks) matches those numbers. **A pilot can read every visible label in the sparse
scene**: 9 blocks with 0 intersecting pairs, and each block's own rows are separated by the measured row
pitch.

## 11. Anything unfinished / caveats

* **Dense scenes hide labels by design** (27/27 at 1920 dense, 48 at 800 dense, 21 at 4K): the brief
  expects this. The priority classes never hide and every glyph, overlay and `ACC`/`QUE` word stays.
* **Marker words can be dropped** on a crowded scope (376 across the whole run): a class letter (`M`,
  `P`, `T`), the `!` or `RIP n` gives way to a label; the glyph and its overlay brackets stay. The verb
  and `SUB` get four placements before giving way.
* **The target designator may still share sky with the fixed scope** where the target projects near the
  corner: it is a world-anchored bracket and the P1039 stroke-vs-text exception covers it (audit: 0).
* **The lock cue is clipped at the scope** (presentation choice, stated in §4); no cue timing, radius or
  audio state changed.
* The `!` badge's animation-only diffs in the classic/modern matrix are fixture-owned (84 vs P1039's 83);
  every one is inside the recorded mask, `outside: 0`.
* `docs/TEST_FLAKES.md` was not touched: the affected tests are headless (174 in ~5 s per run), and the
  three fast-tier failures are already-ledgered load families (`FullSessionIntegrationTests` chat receipt,
  `DeadLatchRespawnLiveTests`, `WarpTransitServerTests`), each green 3/3 alone.
* New media (17 PNGs + 1 JSON, 0.85 MiB) needs Domingo's review before any push.
* The brief file `P1039B_BRIEF.md` stays untracked (excluded through `.git/info/exclude` in the shared
  git dir).

## Native intent

Native's radar is an allowed-set projector with no collision logic: `RenderBlips`/`DrawBlip` place every
contact the per-kind LOD admits at its projected position or pin it to the real direction at the edge
(`WinTrek/radarimage.cpp:286-427`, eye-space clamp `WinTrek.cpp:7882-7960`), the per-kind LOD decides
which icons exist (`radarimage.cpp:703-980`), and the text loop draws **every** eligible label block at
its own offset without testing overlaps (`:1189-1461`, name `:1396`, range `:1402`, the bare `_itoa`
digits `:1209-1218`), each block placed outside its icon along the radial direction from the scope centre
(`:360-379`). Its only priority is list order: target/accepted/threat labels are pushed to the end (drawn
on top), everything else to the front (`:419-425`). Native therefore has no range scale (there is nothing
to match; Classic and Modern draw no rings either) and no declutter — the Jet pass adds exactly the
placement/visibility decision a corner-thumbnail radar never needed, as a presentation-only rule behind
`hud.jetSkin`, over the same contact snapshot and the same LOD tier.

## Modern approach

The pass is a pure function of the shared model: slot/rank/overlap arithmetic in `JetRadarLabels`
(headlessly testable), the scope zone in `JetRadar.Scope`, the fallback slot in
`JetReadoutPlacement.TargetZone`/`TargetSlot`, and the drawing in one partial file that plans the whole
frame before submitting a single run (so strokes always precede text). The proof stamps every run with
its element, so `TextOverlapSame` (same element) and `TextOverlapCross` (different elements) are facts in
`jet-capture.json`/`jet-audit-summary.json`, the per-scene placed/shortened/hidden/overflow counts and
the planned label boxes are recorded, and the dense-frame cost is measured against the Modern radar on the
identical contact set. Classic and Modern remain pixel- and semantically identical, proven by the
1,848-combination comparison against a baseline captured before the first edit.
