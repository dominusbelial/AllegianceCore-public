# P1107 — Compact Modern HUD (`visuals.compactHud`)

Lane: P1107 worktree, base `22a59565` (master at dispatch). Changes are left in the working tree —
no commit, no push; no rig/VPS/lane host contacted. Scratch (build logs, proof frames, base-tree
comparison build) lives in the git-excluded `.scratch/p1107/`. `local.env` is present, so the
artwork environment resolves and the artwork-dependent proofs ran.

Domingo, flying the 20v20 on the Modern HUD at 1920×1080 (2026-11-28): "the HUD looks cluttered" —
the own-ship status ring reads as six large 100 % gauges with a label under each, and the bottom
target pane draws its own ARMOR / SHIELD labels with ring-like gauges in the same font, so the two
blocks look like duplicate armour/shield gauges. Nothing is removed: the target pane's content is
native (`Artwork/dialog.mdl:4105-4142` draws ARMOR/SHIELD and the hull/shield gauges
unconditionally; `WinTrek/consoledata.cpp:202-212` returns 0 for a non-damageable or out-of-sector
target). This is a proportion/legibility pass behind one default-off flag.

## Brief items

| # | Item | State | Evidence |
|---|------|-------|----------|
| 1 | Measure first: headless screenshots at 1280×720 / 1920×1080 / 5120×2160 (HUD 100 %), ship + aleph targets; per-text pixel height and per-gauge outer diameter in a table | **done** | `tools/p1107-compact-hud-proof` (`P1107 FONT` / `P1107 MEASURE` lines, `.scratch/p1107/proof-final.log`); tables below |
| 2 | Secondary text one font step smaller, with a stated minimum rendered height | **done** | `CompactHudLayout.SecondaryStep` (11/14), `MinTextHeightPx` (11 px), `SecondaryScale`; `P1107CompactHudTests.Secondary_text_is_one_font_step_and_honours_the_minimum_height`; measurement table (22.0 → 17.3 px box, 17 → 13 px ink) |
| 3 | Neater own-status gauges: smaller/thinner rings, % centred inside, labels tight, tightened cluster, clear of the crosshair / lead indicator / speed readout / kill-bonus badge | **done** | rings 64 → 48 px diameter, stroke 2 → 1.5 px; cluster spread 1.588 → 1.350 (footprint 485×422 → 407×347 px at 1280×720); `P1107CompactHudTests.Compact_rings_and_labels_clear_the_centre_and_the_hud_furniture`; proof `P1107 AUDIT` (18/18 compact combinations with `violations=0`) |
| 4 | Distinct target pane: slim horizontal hull/shield bars, side-tinted, empty for non-damageable / out-of-sector targets | **done** | `CompactHudLayout.TargetBar` / `BarFill`, `ModernHudRenderer.DrawCompactTargetBar`; `P1107CompactHudTests.Target_bars_are_slim_horizontal_and_clear_of_the_model_preview` + `Aleph_and_out_of_sector_targets_draw_empty_bars`; `after-*-aleph.png` show empty tracks |
| 5 | One `visuals.compactHud` toggle, default off, Modern only, flag off byte-identical, composes with HUD size 50–200 % | **done** | `VisualFlags` row + `VisualOptions.CompactHud`; flag-off pixel hashes identical to a base-commit build (table below); `Flag_row_is_default_off_and_persists`, `Flag_off_returns_todays_geometry_exactly`, `Classic_hud_path_never_reads_the_compact_flag`, `Compact_geometry_scales_with_the_hud_size_slider_both_ways` |
| 6 | Fast-tier tests + deliberate breaks | **done** | `tests/Allegiance.ClientCore.MonoGame.Tests/P1107CompactHudTests.cs` (9 tests, 3× alone all green); 7 deliberate breaks, each caught (table below) |
| 7 | Before/after screenshots in `COMPACT_MODERN_HUD_P1107_assets/` (≤ 300 KB each) and a look at each | **done** | 12 PNGs, 101–167 KiB each (below); per-image checks below |
| 8 | Report | **done** | this file |

### How long each item took (wall clock, local time, from the 08:40 start to the 09:50 report)

| Item | Window | Elapsed |
|------|--------|---------|
| Report skeleton + baseline solution build | 08:40–08:45 | 5 min |
| 1 (measure) | proof harness (offscreen renderer, text/ring recording, lossless PNG writer, per-text ink measurement), before/after measurements, then the base-commit byte-identity run | 08:47–09:01 | 14 min |
| 2 + 3 (secondary text, neater gauges) | `CompactHudLayout`, Modern renderer, screenshot-driven iteration (`100%` fit, ring reservation, proportional spread) | 08:47–09:04 | 17 min (interleaved with 1) |
| 4 (distinct target pane) | native-anchor bars, in-range marker, crowded fallback, "no tucking into the cluster" rule (found by looking at `after-1280x720-aleph.png`) | 08:50–09:02 | 12 min |
| 5 (flag) | `VisualFlags` row, desktop + browser wiring, base-commit flag-off pixel comparison | 09:00–09:03 | 3 min (+ ~15 min of build inside the gate) |
| 6 (tests + breaks) | 9 Fast tests (census through the production `HudTextLayout`), 7 deliberate breaks | 08:53–09:05 | 12 min |
| 7 (screenshots) | 12 PNGs written by the proof, each inspected, one layout rule added because of what the aleph image showed | 08:47–09:02 | 15 min |
| 8 (report) | measurements, design choices, gate receipts | 09:40–09:50 | 10 min |
| Gate | Debug + Release solution builds (0/0), `tools/build-tools.sh` (159 projects), two complete Fast sweeps | 09:05–09:47 | 42 min (mostly runner queue + sweep time) |

Total ≈ 70 min wall clock, of which ~35 min is the shared test runner.

## 1. What was measured first (today's Modern HUD, HUD size 100 %)

The proof harness renders the **production** `ModernHudRenderer` (same constructor arguments the
client uses: the HUDModern outline font, the live `HudSettings`, `displayScale 1`) into offscreen
render targets, and records every text submission and every arc. Heights are reported twice: the
**layout box** (`SpriteFont.MeasureString × draw scale` — the height the HUD reserves) and the
**real ink** (that text painted alone onto an atlas and read back as pixels). Measured font facts:

```
P1107 FONT small lineSpacing=22 spacing=0 'ARMOR' box={X:56 Y:22} '0%' box={X:23 Y:22}
```

**Ordering note (honest):** the harness landed in the same pass as the first implementation cut, so
the "before" column below is produced by the *flag-off* path of that harness rather than by a
snapshot taken before touching the code. That is deliberate and stronger: the flag-off path was
then rendered from a `git archive` of the base commit (section 5) and produced **identical pixels**
in all six scenes, so the recorded before-state is provably the shipped renderer, not a remembered
one. The first screenshots also changed two design decisions (the `100%` fit and the aleph pane's
"tuck into the cluster" case), which is why those rules exist.

At HUD size 100 % the Modern scale is `ModernHudScale.Core(1, 1, h) == 1.0` for **all three**
resolutions (the P833 density rule deliberately does not grow text with the window), so text
heights are identical at 1280×720, 1920×1080 and 5120×2160; only positions move with the viewport.

### Text elements (before → after, HUD 100 %; identical at all three resolutions)

| Element (kind) | Before scale / box / ink | After scale / box / ink |
|---|---|---|
| Gauge labels `AMMO`, `ENERGY`, `ARMOR`, `SHIELD`, `THRUST`, `FUEL` (secondary) | 1.000 / 22.0 / 17 px | **0.786 / 17.3 / 13 px** |
| Gauge percentages `64% 78% 52% 80% 83%` (primary) | 1.000 / 22.0 / 16–17 px | **1.000 / 22.0 / 16–17 px** (unchanged) |
| Gauge percentage `100%` (primary, widest value) | 1.000 / 22.0 / 16 px | **0.800 / 17.6 / 13 px** (fitted to the ring's clear width) |
| Own status `SIG: 42%`, `KB 15%` (secondary) | 1.000 / 22.0 / 17 px | **0.786 / 17.3 / 13–16 px** |
| Own speed `260` + `mps` (primary) | 1.000 / 22.0 / 24 px | **1.000 / 22.0 / 24 px** |
| Target name `Peregrine` (primary) | 1.000 / 22.0 / 20 px | **1.000 / 22.0 / 20 px** |
| Target type `: Heavy Interceptor` (secondary) | (part of the name run) | **0.786 / 17.3 / 15 px** |
| Target `ARMOR`, `SHIELD` labels (secondary) | 1.000 / 22.0 / 17 px | **0.786 / 17.3 / 13 px** |
| Target `SPEED: 260`, `RANGE: 1250`, `TTI: infinite`, `LOCATION: …` (secondary) | 1.000 / 22.0 / 17 px | **0.786 / 17.3 / 13 px** |
| Target kill bonus `KB 21%` (primary) | 1.000 / 22.0 / 17 px | **1.000 / 22.0 / 17 px** |
| `DOCK PILOT  [X]` leader line, `PWR 100%`, `AFTERBURNER ACTIVE`, `WEAPONS: ALL`, systems-panel lines, `HUD Tactical`, `RADAR Default`, `COCKPIT`, radar contact labels, `+000deg`, `ROLL`, chat lines | 1.000 / 22.0 / 17–21 px | **unchanged** (not in this brief's scope) |

### Own-status ring gauges (outer diameter / stroke)

| Resolution (HUD 100 %) | Before | After |
|---|---|---|
| 1280×720 | 64.0 px / 2.0 px, label 8 px under the ring | **48.0 px / 1.5 px, label 3 px under the ring** |
| 1920×1080 | 64.0 px / 2.0 px | **48.0 px / 1.5 px** |
| 5120×2160 | 64.0 px / 2.0 px | **48.0 px / 1.5 px** |

HUD-size sweep (same numbers scaled by the resolved `ModernHudScale.Core`, which the window-height
budget caps at 1.0 for 720p at size 200 %, 1.5 for 1080p at size 200 %, 2.0 for 2160p at size 200 %):
size 50 % → 32 → **24 px** radius, size 100 % → 32 → **24**, size 200 % (2160p) → 64 → **48**.

At 5120×2160 with HUD size 200 % the **before** state is measurably broken: `ModernHudScale.Cluster`
clamps the spread at 2.1 while the ring radius keeps growing, and the proof records six
intersections per target case — the Thrust↔Hull and Fuel↔Shield ring pairs (each reported from both
sides) and the `ARMOR`/`SHIELD` labels sitting inside the neighbouring row's rings. The compact HUD is clean there because its spread stays proportional
(see design choice 3).

## 2-4. Design choices

1. **One table, `CompactHudLayout`** (`src/Allegiance.ClientCore.Game/Rendering/CompactHudLayout.cs`):
   ring fraction 0.75, cluster fraction 0.85, secondary step 11/14, minimum text height 11 px,
   label gap 3/8, stroke 1.5, glow 4, percent padding 1, bar geometry. Every number the renderer,
   the tests and the proof use lives there, and every helper returns *exactly today's value* when
   `compact` is false — that is what makes the flag-off identity a property of one file.
2. **"One font step smaller" = 11/14 = 0.786**, the native smVerdana→medVerdana ratio (`1/1.25`).
   Applied to the gauge labels, the own status readouts (SIG/kill bonus/AUTOPILOT/CLOAK/…), the
   target pane's type suffix, its ARMOR/SHIELD labels and its SPEED/TTI/RANGE-LOCATION lines.
   The target name, the gauge percentages, the own speed numeral and the target kill-bonus number
   stay at today's size. The caption used to be ONE run (`"Peregrine: Heavy Interceptor"`), so the
   compact pane splits it into name (primary) + `": type"` (secondary, vertically centred on the
   name) — same content, same side colour, native ordering.
3. **Stated minimum rendered text height: 11 px** (`MinTextHeightPx`), applied to the text's
   layout box (`LineSpacing 22 × scale`), which is the height the HUD reserves and the eye reads as
   one line. `SecondaryScale` never goes below that floor and never exceeds the primary size, so
   at HUD size 50 % (where the primary line itself is only 11 px) the compact step simply does not
   shrink further instead of pushing a line under the floor; at 100 % the secondary line is
   17.3 px (ink 13 px). A second step (0.617 → 13.6 px) is *not* taken and the floor rule is pinned
   by a test.
4. **Gauge percentage inside the ring.** The primary value keeps today's size; only a value whose
   run would cross the ring's clear width is reduced by exactly the amount needed
   (`PercentScale`): 3-glyph values (`78%`) draw at 1.0, and the single widest value `100%`
   (53.7 px against a 43 px clear width) draws at 0.80. That is the one case the 48 px ring cannot
   hold at full size — the alternative would be a ~60 px ring (≈94 % of today), which the brief
   explicitly rules out.
5. **Cluster tightening stays proportional.** The compact spread is
   `scale × ClusterAtReference × 0.85`, not `Cluster(scale) × 0.85`. `Cluster()` stops at 2.1, so
   multiplying it would let the rings (which keep growing with the HUD size) collide at 200 %; the
   proportional form keeps the ring-to-spacing ratio, and therefore every clearance, constant from
   HUD size 50 % to 200 % (and beyond, up to the 4.0 scale ceiling). At 1280×720 the cluster
   footprint falls from 485×422 px to 407×347 px (−31 % of area) and every compact ring lies inside
   the disc its old ring occupied (it never grows outward); the inward margin is measured, not
   assumed: the nearest compact ring pixel sits 73.6 px from the crosshair centre (57.6 px clear of
   the 16 px crosshair arms) versus 83.3 px before, and the audit confirms the lead indicator, the
   speed numeral and the kill-bonus badge are untouched at every HUD size.
6. **Labels hug the ring instead of floating** (gap 8 → 3 px) and keep their native words
   (`ARMOR`, `SHIELD`, `THRUST`, …) instead of short tags: at the secondary size every label fits
   inside its ring's width (`SHIELD` 44 px under a 48 px ring), so tags would have cost the native
   vocabulary for no gain. The screenshots (`after-1920x1080-ship.png`) show the tight labels
   reading as one block with the ring.
7. **Target pane: two slim horizontal bars**, 80 × 5 native px, laid on the row the ring gauges
   used (native local y 83.5) and flanking the native 3-D model preview: `ARMOR ▬▬▬` to the left of
   preview (local x 270..366), `▬▬▬ SHIELD` to its right. They keep the native pane anchors and the
   native label sides, are tinted with the pane's existing `SideColor` (native
   `WinTrek/WinTrek.cpp:8784-8818`, bright 0.85, alpha 0.5) — a track at 22 % and the fill at the
   full side colour — and a 0 %/null reading draws the empty track, exactly native's 0 %. The
   in-range diamond moved from native local (200, 90) — where it would sit on the bar's top edge —
   to the left end of the bar row. The crowded one-row fallback (the pane's existing behaviour for
   small windows) replaces its two 24 px rings with 48 × 5 px bars in the same slots, so its row
   heights and therefore its name/detail positions are unchanged.
8. **The compact pane refuses to tuck itself into the gauge cluster's footprint.** At 1280×720 the
   pane's *native* anchors put its name between the hull/shield rings and its bars between the
   thrust/fuel rings; the first screenshot of this change showed exactly that ("Aleph: aleph"
   floating between four own gauges). The compact path therefore also falls back to the crowded
   bottom-row layout when any pane part lands inside the union of the own ring/label/status boxes.
   The flag-off path keeps its shipped per-element rule.
9. **Flag plumbing** is the existing `visuals.*` machinery: one `VisualFlags` row (`compactHud`,
   label "Compact HUD", description "smaller Modern labels, neater gauges, bar-style target hull",
   toggle, **default off**, no hotkey) plus `VisualOptions.CompactHud`. Nothing else was needed:
   the profile group, `--visuals` override, Visuals page, startup log and persistence are
   table-driven. `ModernHudRenderer` takes the **live** `VisualOptions` object (desktop
   `Game1.Lifecycle.cs`, browser `Game1.Flight.cs`), so a Visuals-page toggle applies on the next
   frame. Classic never receives the object and never references the flag.
10. **Browser parity**: the browser client shares `ModernHudRenderer`; it passes its own
    `_visuals`, so with the flag off its output is unchanged (same code path, same `CompactHud ==
    false` branch).
11. **Not touched**: Classic HUD, element visibility, radar/minimap, systems panel, chat, the HUD
    size slider, server/game rules. `HudSdfFont.InkBounds` was made `public` (it was `internal`) so
    the proof can measure the ink box with the renderer's own maths; no behaviour change.

## 5. Flag off is byte-identical to today (base-commit comparison)

The proof tool was built twice: once from the working tree, and once from a `git archive` of the
base commit (`22a59565`) into `.scratch/p1107/base/` with a small base-API dump program that has no
`visuals` parameter and no `CompactHudLayout` at all (the shipped renderer). Both render the same
fixture and print the SHA-256 of the decoded RGBA pixels:

| Scene | Base commit (`git archive HEAD`) | Working tree, `compactHud` off |
|---|---|---|
| before-1280x720-ship | `01FB473CECE86EDB` | `01FB473CECE86EDB` |
| before-1280x720-aleph | `A13F4982DBDC2622` | `A13F4982DBDC2622` |
| before-1920x1080-ship | `6D0DF1067F332633` | `6D0DF1067F332633` |
| before-1920x1080-aleph | `C8A5C665F10B3AC2` | `C8A5C665F10B3AC2` |
| before-5120x2160-ship | `AB7727717A802116` | `AB7727717A802116` |
| before-5120x2160-aleph | `638BDE4D30AB190D` | `638BDE4D30AB190D` |

The existing P1091 Modern 1920×1080 golden hash test remains the checked-in regression guard for the
same path (see the gate section for its flake status on this host).

## 6. Tests and deliberate breaks

`tests/Allegiance.ClientCore.MonoGame.Tests/P1107CompactHudTests.cs` — all `Tier=Fast`, no GPU:

| Test | What it pins |
|---|---|
| `Flag_off_returns_todays_geometry_exactly` | every helper returns today's literal (32 px radius, 2 px stroke, 6 px glow, 8 px gap, unchanged spread, primary secondary-scale) and the compact values are 75 %/thinner/tighter/one-step |
| `Flag_row_is_default_off_and_persists` | table row exists, toggle, `DefaultOn == false`, label+description, profile JSON round-trip, legacy profile → off, `Describe()` line |
| `Secondary_text_is_one_font_step_and_honours_the_minimum_height` | 11/14 step, 11 px floor at every scale, secondary never above primary, 50 % boundary behaviour |
| `Gauge_percentage_fits_inside_the_compact_ring` | `100%` fits inside the ring's clear width, short values keep full size, the fit never grows |
| `Compact_rings_and_labels_clear_the_centre_and_the_hud_furniture` | 3 resolutions × HUD size 50/100/200 %: rings vs crosshair / lead indicator / speed readout / kill-bonus badge / minimap / chat pane / systems panel / centre messages / other rings; labels placed with the production `HudTextLayout` vs the same set; the target pane's content (native layout, or the crowded fallback the renderer really picks) vs rings/labels/panels |
| `Compact_geometry_scales_with_the_hud_size_slider_both_ways` | ring radius and spread track the resolved scale; the label sits exactly one gap under its ring; the spread stays proportional at 200 % and keeps the 1.046 floor |
| `Aleph_and_out_of_sector_targets_draw_empty_bars` | real `TargetPaneModel` for a warp (null/null → 0), a ship that left the sector (0/0) and an in-sector ship (1.0, not always empty) |
| `Target_bars_are_slim_horizontal_and_clear_of_the_model_preview` | bars are ≥ 8:1, ≤ 5 native px tall, flank the HUD-21 preview rect, stay inside the native pane extent, never overlap each other — at 3 resolutions × 3 HUD sizes |
| `Classic_hud_path_never_reads_the_compact_flag` | source guard: `HudRenderer.cs` (the Classic base) contains no `CompactHud`/`compactHud`/`VisualOptions`; the Modern partials do |

Proof harness (GPU, not part of the Fast tier): `tools/p1107-compact-hud-proof` renders 3
resolutions × 2 target cases × 2 flag states for the PNGs, and audits 3 resolutions × HUD size
50/100/200 % × 2 target cases × 2 flag states against the boxes the production draw path actually
submitted (`P1107 AUDIT`). Result: **18/18 compact combinations `violations=0`**; the only flagged
combinations are the pre-existing flag-off 5120×2160/size-200 % ring collisions.

### Deliberate breaks (each applied alone, run, then reverted)

| Break | Caught by |
|---|---|
| `compactHud` row `DefaultOn: true` | `Flag_row_is_default_off_and_persists` |
| `SecondaryStep = 1` (no shrink) | `Flag_off_returns_todays_geometry_exactly`, `Secondary_text_is_one_font_step_and_honours_the_minimum_height` |
| `MinTextHeightPx = 4` (floor ignored) | `Secondary_text_is_one_font_step_and_honours_the_minimum_height` |
| `ClusterFraction = 0.28` (cluster collapses onto the crosshair) | `Compact_geometry_scales_with_the_hud_size_slider_both_ways`, `Compact_rings_and_labels_clear_the_centre_and_the_hud_furniture` |
| `BarFill(null) → 1` (empty aleph bars filled) | `Aleph_and_out_of_sector_targets_draw_empty_bars` |
| `BarWidth = 260` (bars over the model preview / off the pane) | `Target_bars_are_slim_horizontal_and_clear_of_the_model_preview`, `Compact_rings_and_labels_clear_the_centre_and_the_hud_furniture` |
| Classic base renderer references `CompactHudLayout` | `Classic_hud_path_never_reads_the_compact_flag` |

## 7. Screenshots (new media — every file listed)

All under `docs/reports/COMPACT_MODERN_HUD_P1107_assets/`, lossless PNG (RGBA, per-row adaptive
filter), 101–167 KiB each (the 5120×2160 pair would have been ~313 KiB with the P980 filter-0
writer), all ≤ 300 KiB:

| File | Size |
|---|---|
| `before-1280x720-ship.png` | 111 KiB |
| `after-1280x720-ship.png` | 108 KiB |
| `before-1280x720-aleph.png` | 104 KiB |
| `after-1280x720-aleph.png` | 101 KiB |
| `before-1920x1080-ship.png` | 125 KiB |
| `after-1920x1080-ship.png` | 118 KiB |
| `before-1920x1080-aleph.png` | 118 KiB |
| `after-1920x1080-aleph.png` | 112 KiB |
| `before-5120x2160-ship.png` | 167 KiB |
| `after-5120x2160-ship.png` | 159 KiB |
| `before-5120x2160-aleph.png` | 159 KiB |
| `after-5120x2160-aleph.png` | 152 KiB |

The fixture is a cockpit flight frame (hull 78 %, shield 52 %, ammo 64 %, fuel 83 %, energy 100 %,
speed 260, SIG 42 %, kill bonus 15 %, two mounted systems, three friendly radar contacts, three chat
lines, a leader command line and a ripcord countdown) with either an in-sector enemy ship targeted
(`Peregrine: Heavy Interceptor`, hull 78 %, shield 52 %, in weapon range, KB 21 %) or an aleph
(non-damageable: no hull/shield reading). The 3-D target-model preview is a GPU render target the
proof does not create, so that native pane column is empty in these captures — its space is the gap
between the two target bars.

**What I checked in each image** (all twelve looked at, one by one):

- `before-1280x720-ship.png` — baseline for the complaint: six 64 px rings with 22 px labels, and the
  target pane at the bottom drawing two *more* rings (`ARMOR` ring, `KB 21%`, ring, `SHIELD`) in the
  same font. The own `260`/`mps` readout, `DOCK PILOT [X]`, `SIG: 42%` and `KB 15%` are clear of the
  rings; the chat is top-centre, the systems panel right, the minimap area bottom-right.
- `after-1280x720-ship.png` — compact: 48 px rings, `%` centred inside, labels tight and smaller;
  the target pane is in its crowded bottom row with `ARMOR ▬` / `▬ SHIELD` bars instead of rings; the
  name sits below `mps`, nothing touches the crosshair, the speed numeral or the kill-bonus badge;
  the systems panel, chat and radar labels are unmoved.
- `before-1280x720-aleph.png` — baseline aleph: the same bottom row with two small (24 px) rings next
  to `ARMOR`/`SHIELD`; even empty they look like two more own gauges.
- `after-1280x720-aleph.png` — the same row with two empty slim bars; `SPEED: 0`, `RANGE: 900`,
  `TTI: 0:03` unchanged; the aleph's name is the neutral grey side colour (no side), as native.
  This is the image that drove design choice 8 (the earlier version had the pane's name floating
  between the four lower own rings).
- `before-1920x1080-ship.png` — baseline at the reported resolution: the two 64 px target rings
  (red side tint) sit below the name and read exactly like the own `ARMOR`/`SHIELD` rings above.
- `after-1920x1080-ship.png` — the native pane layout now shows `Peregrine` + smaller
  `: Heavy Interceptor`, the `100%` value inside its ring without crossing the stroke, `ARMOR ▬▬▬`
  and `▬▬▬ SHIELD` bars flanking the (empty here) preview column, `KB 21%` above them and the
  in-range ✕ at the left end of the bar row.
- `before-1920x1080-aleph.png` / `after-1920x1080-aleph.png` — same comparison with the
  non-damageable target: both rings → both bars empty (outline only), `SPEED: 0  RANGE: 900`,
  `TTI: 0:03`; the side colour is native's neutral grey.
- `before-5120x2160-ship.png` / `after-5120x2160-ship.png` — at 5120×2160 the HUD is physically
  small by design (P833: text does not grow with the window at desktop scale 1.0); the compact
  version shrinks the cluster and the pane while everything stays inside the viewport and the
  top-centre chat / right systems panel are untouched.
- `before-5120x2160-aleph.png` / `after-5120x2160-aleph.png` — the same at the largest size, aleph
  target, empty bars.

One judgement call visible in the captures: at 5120×2160/100 % the whole HUD is small on screen;
that is the shipped density rule (P833/P859), not something this flag changes — a 5120-wide display
normally reports a desktop scale > 1, which multiplies the HUD (and the compact geometry with it).

## Gate (light gate)

- `~/.dotnet/dotnet build AllegianceCore.slnx` (Debug): **0 warnings / 0 errors**;
  `-c Release`: **0 warnings / 0 errors** (logs: `.scratch/p1107/build-debug.log`,
  `build-release.log`).
- `tools/build-tools.sh`: **all 159 tool projects built**, 0 warnings / 0 errors (new:
  `p1107-compact-hud-proof`).
- New tests **3× alone**: `P1107CompactHudTests` 9/9 passed on each of three fresh hosts. The two
  directly affected existing suites (`P979VisualsOptionsTests`, `VisualFlagsTests`, which assert the
  Visuals-page row count and the `Describe()` line) were made table-driven / updated for the new row
  and then ran **3× together with the new tests: 37/37 passed each run**.
- One `tools/run-tests.sh --tier fast` (first run, receipt `TestResults/tiers-20261128T142045.*`,
  896 s): 16,263 cases, **16,249 passed / 7 failed / 7 not executed**. Four failures were real and are fixed
  (3× `P979VisualsOptionsTests` + 1× `VisualFlagsTests` hard-coded the pre-P1107 Visuals-page size
  of 16 rows and the pre-P1107 `Describe()` line; both now derive from `VisualFlags.Definitions`).
  The three remaining failures are ledgered, renderer/host-specific and reproduce on the untouched
  Classic skin:
  `P1091HudOverlapTests.Painted_mount_rows_and_entity_readouts_are_distinct` at 1920×1080 default
  scale, Classic and Modern, with observed digests `C100F69A43C7…` / `1093B39B5B9B…` — exactly the
  signature recorded in `docs/TEST_FLAKES.md` (P1091/P1096/P1099 software-GL entries), reproduced
  3/3 in isolation with 19/21 cases passing and zero layout errors; and
  `BS1BenchmarkSharingTests.Real_loopback_HTTP_transport_uploads_exact_preview` (empty upload
  payload), the ledgered loopback-HTTP family (`docs/TEST_FLAKES.md:1627,1642`). None of them
  touches HUD code.
- **Second complete Fast run** after those two test-file updates
  (`TestResults/tiers-20261128T143526.*/summary.json`, 687 s, run log
  `.scratch/p1107/fast2.log`): 16,263 cases, **16,250 passed / 6 failed / 7 not executed**.
  - `Allegiance.ClientCore.MonoGame.Tests`: 6114 cases, 6108 passed, 4 not executed, **2 failed** —
    the same two ledgered P1091 1920×1080 default-scale goldens (`C100F69A43C7…` Classic /
    `1093B39B5B9B…` Modern; 3/3 isolated reruns reproduce them with 19/21 passing).
  - `Allegiance.ClientCore.Tests`: 2538 cases, 2535 passed, **3 failed** —
    `P974SessionOwnershipTests.Overall_deadline_includes_the_join_answer_and_never_accepts_silence`,
    `P974SessionOwnershipTests.Dispose_drains_a_stalled_reconnect_and_closes_its_late_socket`
    (ledgered, `docs/TEST_FLAKES.md:501,11481`) and
    `BS1BenchmarkSharingTests.Timeout_retains_payload_and_retries_without_waiting_forever`
    (ledgered loopback-HTTP family, `:672`). All three ran **3× in isolation: 22/22 passed each
    run**.
  - `Allegiance.Core.Tests`: 6846 cases, 6842 passed, 3 not executed, **1 failed** —
    `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`, the ledgered intermittent
    miner-capacity family (`docs/TEST_FLAKES.md` P1085 table); passed **3/3 in isolation**.
  - 10 Fast executions exceeded the calibrated 15.5 s limit (all *passed*); they are the ledgered
    slow civ/gunner/loadout families, not new.
  - Every one of the 6 failures is outside this change (no HUD renderer, HUD model, options-table
    or test-infra code is involved) and all are already in `docs/TEST_FLAKES.md`, so nothing new was
    added to the ledger.

## Reproduce

```bash
# before/after PNGs + the measurement table + the element-overlap audit (offscreen GL)
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null LIBGL_ALWAYS_SOFTWARE=1 \
  dotnet tools/p1107-compact-hud-proof/bin/Debug/net10.0/p1107-compact-hud-proof.dll <outDir>
# the Fast tests
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests/Allegiance.ClientCore.MonoGame.Tests.csproj \
  --filter "FullyQualifiedName~P1107CompactHudTests"
# the flag itself, without touching the profile
dotnet run --project src/Allegiance.ClientCore.MonoGame -- --visuals compactHud=on
```

## Things Domingo should judge in flight

1. **Does the compact HUD feel less cluttered at 1920×1080 in the 20v20?** The cluster footprint is
   31 % smaller in area and the target pane no longer shows rings, but that is my reading of the
   static captures — only flight tells you whether the smaller labels are still readable in a
   turning fight.
2. **`100%` inside its ring** is drawn at 0.80 of the normal size (the only value that does not fit a
   48 px ring at full size). If it looks odd next to `78%`, the alternatives are a ~10 % bigger ring
   (≈53 px) or letting the value sit outside the ring — both cheap to switch.
3. **Bars flanking the (native) target-model preview column.** With a real preview the bars sit left
   and right of the model; without one the middle gap is visible. If you would rather have the bars
   stacked together under the name, say so and I will move them.
4. **The own status column** (`SIG`, `KB`, `AUTOPILOT`, `VECTOR LOCK`, `FLAG`, `EYE`) is one step
   smaller and on the tightened grid. Those words are drawn where native shows small ICONS, and the
   port's longer words already touched the gauge reservation before this change (measured, unchanged,
   not made worse); if it now reads too dense, the next step is to give those rows their own column.
5. **HUD size 50 %**: the compact step is suppressed there (the 11 px floor would be broken), so at
   50 % the labels are the same size as before. If you fly at 50 % regularly we should revisit the
   floor.
6. The pre-existing ring collisions at **HUD size 200 % on a 2160-tall window** are fixed by this
   flag's proportional spread; if you like the flag, the same proportional rule could be applied to
   the shipped (flag-off) spread without any visual change at 100 %.

## Review fixes (Claude, 2026-11-28)

Two defects found in review of the lane screenshots, fixed in the same branch; flag-off output unchanged
(the regenerated `before-*` images are byte-identical between runs and match the committed ones to within
host rendering noise: 1 pixel above a channel difference of 8 in a full frame).

| Defect (flag on) | Cause | Fix |
|---|---|---|
| Gauge percentages crossed their rings ("(100%", "(83%") at every resolution | `PercentScale` compared only the text WIDTH with the ring's diameter, but every percentage was also drawn with the dark text box (3 px wider each side), and a box that clears the stroke on the centre line still pokes into a circle at its corners | `CompactHudLayout.PercentScale` now fits the glyph INK box on the diagonal against the inner edge (radius − glow/2 − padding); the in-ring percentage is drawn without the text box and pinned on the ring centre. `InkFitsRing` added; `Gauge_percentage_fits_inside_the_compact_ring` rewritten (fails on the old width-only rule) |
| The speed unit "mps" drifted away from the numeral (1280x720: below KB 15 %; 1920: DOCK PILOT between them) and at 1280 a joined block could land on the crosshair | numeral and unit were placed as two independent measured texts; the tighter compact cluster freed rows that the DOCK PILOT/status text claimed first | compact HUD places numeral + unit as ONE block (`HudRenderer.DrawSpeed`, `SpeedBlockSize`), pre-placed in `ModernHudRenderer.PrepareLayout` just under a reserved crosshair box (`CompactHudLayout.CrosshairClearHalf`), so the primary instrument keeps that row and later text flows round it. Flag off: the original two `DrawText` calls |

After images in `COMPACT_MODERN_HUD_P1107_assets/after-*.png` were regenerated with the fixes
(`tools/p1107-compact-hud-proof`, `P1107_PASS`, 0 violations in every "after" audit at HUD size 50/100/200 %).
