# P1039 — Jet HUD skin story 5/9: RWR-style radar (+ item 0: 800×480 cross-element text overlaps)

Follow-up to P1038c (`docs/reports/JET_LAYOUT_FIXES_P1038C.md`, merged at `87bfdd53`), built in the
`hermes/p1039` worktree of `AllegianceCore` at Claude's master. No commit, no push, no stash, no remote host, no
Docker (not installed here). Scratch lived in `.scratch/p1039/`; raw captures, the zero-delta baseline, the compare
log and the gate logs live outside the repo in `~/allegiance-evidence/P1039/`. The task's evidence is
`docs/reports/JET_RWR_P1039_assets/` — 10 PNGs + `jet-audit-summary.json`, **11 files, 1,815,086 bytes = 1.73 MiB**
(under the 2 MB cap).

Jet stays presentation-only: every value is the shared `HudModel`/`HudFrame`/`HudContext` data plus the one
presentation-only `HudContact.RelativeBearingRadians` field this story adds (P972 §4.4 / slice 3C). No server, Core,
wire, audio, input, contact-eligibility, targeting, command or Tab-starmap change.

**Status**

| Item | Status |
|---|---|
| 0 — 800×480 cross-element text overlaps (`TextOverlapCross`, placement priority, compact designator readout, F4 pane vs master caution) | **done** |
| 1 — shared `HudContact.RelativeBearingRadians` + snapshot builder + unit tests | **done** |
| 1 — Jet radar renderer (square tactical scope / circular RWR, all 27 §5.4 rows walked) | **done** |
| 1 — radar scenes in `hud2-proof --jet` (96 scenes: square/round, sparse/dense, 4 LODs, all kinds/overlays, null-bearing fallback, 1920×1080 + 800×480 dark/bright + one 4K) + draw cost | **done** |
| Audit — `TextOverlapCross` + all existing Jet classes | **0** in every class except one listed exception (`jet-locked` 1920, ×2 backdrops) |
| Gate — baseline-first zero-delta, 0 B/frame, tests 3×, build 0/0, tools build, one fast tier | zero-delta **1,848 / 0 pixel / 0 semantic**; build 0/0; tests 3× green; 0 B/frame test green; tools build + fast tier as logged in §8 |

## 1. Zero-delta baseline first

The **unmodified** tree was built (`tools/hud2-proof`, Release) and captured before the first edit:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1039/baseline
HUD2_MANIFEST 1848 combinations, commit 87bfdd538d34+dirty, driver llvmpipe
HUD2_CAPTURE_PASS 1848 combinations in 9.1 min
```

After all changes the same tree was compared against it:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)
```

1848 = 22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins (924 Classic + 924 Modern). **Classic and
Modern stay pixel- and semantically identical: 1,848/1,848, 0 pixel diffs, 0 semantic diffs.** The 83 animation-only
entries are inside the fixture's recorded animation mask (the wall-clock `!` badge), every entry `outside: 0`. The
shared `HudContact` field is a trailing optional parameter defaulting to `null`, so no Classic/Modern pixel moves —
and neither renderer reads it (test-pinned).

## 2. Item 0 — the 800×480 cross-element overlaps, before → after

### 2.1 The counted class: `TextOverlapCross`

`tools/hud2-proof`'s recording surface now stamps every text run and every stroke with the ELEMENT that drew it
(the renderer calls `IJetVectorSurface`'s optional `IJetElementAudit.Element(id)` once per block,
`JetVectorPainter.cs:56-67`, `:328-333`). `TextOverlapCross` counts any two text boxes **from different elements**
that intersect (`tools/hud2-proof/Program.cs`, `TextOverlapCross` block). Two stated exceptions:

1. **Two fixed instrument-furniture marks** (ladder numerals, `ACC`/`CUR` order words, heading/tape graduations)
   may share sky — they are one navigation instrument and native draws the order arrows over the same ladder
   (`WinTrek/indicator.cpp:786-791`). They stay informational in `TextOverlaps`.
2. **An overlap hidden behind a later near-opaque plate** (the F4 pane, alpha ≥ 240/255 — the same threshold
   `LegibleBehindPane` uses) is not visible to the pilot and is not counted.

`stroke-vs-text` gained the one stated exception the brief names: a **world-anchored marker** (target designator,
lock cue, ballistic lead, order chevrons, target data block) may cross the **fixed radar scope** (frame, bands, mode
caption, contact label), because the scope cannot move and the marker is anchored to the projected target.

### 2.2 The defects, before → after

* **Designator readout over `-10`, `CUR`, `PITCH 00`** (P1038c `jet-locked-800x480-dark.png`). The old code
  clustered pixel offsets (`rightLimit`, `leftLimit`, `BelowIfCrossed`) and the 800×480 block still landed on the
  ladder's numeral column. **Now**: `JetReadoutPlacement` (`src/.../Jet/JetReadoutPlacement.cs`) implements a
  **placement priority, not nudges**: slots `right → left → below → above` (`DesignatorOrder`), each candidate
  clamped to the viewport gutter and tested against the frame's registered FURNITURE (every ladder numeral, the
  `PITCH` caption, the heading band + plate, both tape captions and label columns, `ACC`/`CUR`, the stores block,
  the caution panel, the target data block, the radar scope, the F4 pane). Within a slot the search is bounded:
  the preferred position, then the block slid along the slot axis by one/two blocks **away from the anchor**, then
  slid sideways by half a block. The first clear candidate wins; if every candidate is blocked the least-covered
  one is used, still clamped on screen. The lock caption uses `above → below → right → left` and additionally
  avoids the designator block already committed this frame.
* **`LOCK 62%` on the `HOSTILE` name line**: the caption now avoids the designator readout rect
  (`_targetReadoutBox`) and clears the cue's **outer radius** (not only its axis extent), so a caption that slides
  to a side cannot enter a corner arc's diagonal. Measured in `jet-audit-summary.json`: `jet-locked` 800 places the
  readout in slot 2 (below) and the caption in slot 2, both `clear: true`; 1920 places the readout in slot 1 (left)
  and the caption in slot 3 (above) for `jet-locksmall`.
* **Compact form**: at 800×480 (`h < 700`) the designator name is truncated with an ASCII ellipsis at
  `JetTargetGeometry.CompactNameWidth = 72` native px × scale (`FitCompactName`, cached by source name). **Fields
  kept**: relationship word, name (shortened), range, closure, hull, shield and their two bars — no row is dropped.
  The HUD fonts bake ASCII 32…126, so `...` stands in for the typographic ellipsis.
* **F4 pane over `Reloading ammo...`** (P1038c `jet-f4-800x480-bright.png`). The rule is now explicit: **the
  master-caution panel is the higher-priority element** — it is never moved or shrunk. The pane keeps every row and
  every column and **shrinks its own scale** (`JetInventoryPresentation.FitScale`, floor `MinPaneScale = 0.72`) so
  its top starts below the caution panel + 4 px gutter. At 800×480 with 12 rows the pane drops from native scale
  0.9 to ≈0.87 (pane top ≈ 88 px instead of ≈69, i.e. below the caution's bottom); at 1920×1080 the native scale is
  kept untouched. The F4 scene's `LegibleBehindPane` is 0 and the pane's rows no longer intersect the caution text.
* **The WIP tag** is suppressed while the F4 pane is present (`DrawTag`): the modal table owns the bottom band and
  its sliding rows pass through the tag's line.
* **Compact stores annunciator row**: the word gap is `2·scale` px, so the six lit annunciators end before the
  ladder's left numeral column.

Per-scene audit (`jet-audit-summary.json`): all seven pre-existing classes plus `TextOverlapCross` are **0 over 96
scenes** except one listed exception (below).

### 2.3 The one listed exception

`jet-locked` at 1920×1080 (both backdrops): the designator readout's left slot shares ~22 px with the `+30` ladder
numeral (`ladder:+30@877,289 x target:Enemy fighter...@556,283`). Every other candidate is blocked — the cue ring
(radius ≈ 200 px) plus the throttle band plus the ladder's numeral column leave no clear slot at that geometry, so
the least-covered candidate is used, still clamped on screen. **Rule stated**: a ladder numeral is background
furniture drawn before the readout, the numeral keeps its full ink and the readout its full text; native's own
selection readout is likewise drawn over the ladder. This is the single intentional exception the brief allows
("each remaining hit listed as an intentional exception with a reason").

## 3. Item 1 — the shared bearing (the only shared change)

* `HudContact.RelativeBearingRadians` (`src/.../Rendering/HudModel.cs:171`) — optional `float?`, trailing parameter,
  default `null`. Presentation only: no wire field, no inclusion rule, no targeting/command path.
* `RadarBearing.Relative(ownForward, planeNormal, delta)` (`src/.../Rendering/RadarBearing.cs:37`): the signed angle
  about the **sector plane normal** from the own forward's planar projection to the contact offset's planar
  projection. `0` = dead ahead, **positive = the ship's LEFT** (heading grows toward +Y — the heading tape's own
  sense, `JetFlightGeometry.Attitude`). **Roll invariance by construction**: only the forward axis and the plane
  normal enter, so the ship's own right/up axes are never read. **Null** for a zero offset, no usable planar
  forward (a ship pointing along the plane normal) or a zero plane normal; a contact ON the plane normal has no
  honest 2-D bearing and is null too — the renderer then falls back to the projected scope.
* Snapshot builder: `Game1.Rendering.cs:4325-4328` computes the own forward from `ownEnt.Latest.Orientation` and one
  `Bearing(...)` local from the same render poses the native projection uses; every contact constructor passes it
  (`:4368` own ship, `:4464`, `:4516`, `:4549`, `:4601`, `:4653`, `:4697`, `:4810`, `:4861`). The own ship carries
  `0`.
* Unit tests (`tests/.../Skins/JetRadarTests.cs`): front/behind/left/right, above/below → null, roll invariance,
  zero offset / vertical attitude / zero normal → null, and the "Classic/Modern never read it" contract.

## 4. Item 1 — the Jet radar renderer

`src/.../Jet/JetRadar.cs` (pure geometry) + `src/.../Jet/JetHudRenderer.Radar.cs` (drawing).

* **Scope**: a square in the bottom-right quadrant, above the minimap panel, left of the throttle tape's
  readout/IDLE-REV band (`JetRadar.Scope(viewport, scale, rightLimit)`, right edge also clears the tape's caption
  column). Side = `clamp(0.32·h, 110·scale, 360·scale)`; the square and the round variant occupy the **same
  measured rectangle**.
* **The one shared option** `HudSettings.RoundRadar` (`:498` in the proof) changes only the border: **square
  tactical scope** when off (corner brackets + edge lines + axis cross), **circular RWR** when on (inscribed circle
  + four axis ticks). Contact positions, labels and every reading are identical between the two.
* **Own-relative bearing + range bands**: `ContactPoint` places a contact at `centre + (sin, cos)` of its bearing ×
  the range fraction × `IconRadiusFraction`; the range scale is the frame's own maximum range rounded up to a
  1/2/5×10ⁿ value (`ScopeRange`, floor 1000 m) with four equal bands labelled in metres — no sensor range is
  invented, and a contact at/beyond the rim is pinned to it (native's edge clamp).
* **Null-bearing fallback**: the contact's ordinary projected viewport point mapped linearly into the same
  rectangle (`ContactPoint`), never a fabricated direction. The own ship sits at the centre (range 0).
* **Shapes per class** (P972 §5.4 rows 2–9): ship triangle (hostile = closed + inward tick, friendly = dashed, own =
  double triangle), station square + centre tick, aleph open circle + four radial ticks, rock open pentagon, missile
  dart + `M`, probe cross + `P`, mine diamond, treasure diamond + `T`, tech double diamond. **Overlays**: selected
  corner bracket, accepted `ACC` bracket, queued dashed bracket + `QUE`, nearest-enemy double bracket, subject
  square + `SUB`, threat-flash dashed circle + `!`, flag pennant, artifact diamond, ripcord radial line + `RIP n`,
  highlight = hot label brightness only (no invented bracket), command verb `ACC`/`QUE` from the contact's own
  command id.
* **Labels with the native LOD rules**: the label payload is `RadarLabelModel.Resolve(contact, mode, commandCamera)`
  — the native per-kind LOD tier (`radarimage.cpp:703-961,1199-1441`), never re-derived here — drawn monochrome and
  **clipped to the scope's inner rectangle**: the name row is dropped if it does not fit, then the range, then the
  bars, then the whole label (native clips at its image edge too). Hull/shield/ore are fills-only micro-bars.
* **Range fade mapped to monochrome opacity**: `RadarLabelDeclutter.Fade(contact, preset)` with the level preset's
  own `FadeNear/FadeFar`, so the level-driven fade is the same one Modern uses, expressed as ink alpha.
* **Mode label**: `RDR All|Default|Target|None` — the same four modes and words as native's `c_szRadarLODs`
  (`radarimage.cpp:2-5`); the RWR never bypasses the LOD.
* **Ordering for the audit**: glyph pass → glyph-word pass → label pass (a stroke submitted after a text would read
  as a strikethrough across it), and the scope is drawn after the flight furniture but before the world-anchored
  target/lock symbology. The scope is **suppressed while the F4 pane is open** (the modal table owns that band,
  exactly as native's inventory pane covers the radar).
* **0 B/frame**: index loops, literals, the label model's cached range strings and a cached `RIP n`; pinned by the
  allocation test (200 steady frames < 32 KB). The proof's full-frame cost is in §7.

### 4.1 The 27 §5.4 radar rows (`docs/reports/UI_SKINS_P972.md:399-430`)

| # | HUD parity row | Jet equivalent | Status |
|---|---|---|---|
| 1 | Radar viewport and round border | Square tactical scope (option off) / circular RWR (option on), same rectangle | **implemented** |
| 2 | Radar ship contacts | Triangle; hostile closed + tick, friendly dashed, own double | **implemented** |
| 3 | Radar station contacts | Square + centre tick | **implemented** |
| 4 | Radar warp contacts | Aleph open circle + radial ticks | **implemented** |
| 5 | Radar asteroid contacts | Open rock pentagon; special-rock label preserved (native tier) | **implemented** |
| 6 | Radar missile contacts | Dart + `M`, no inferred locker identity | **implemented** |
| 7 | Radar probe and mine contacts | `P` cross / mine diamond, native tiers | **implemented** |
| 8 | Radar treasure and tech contacts | `T` diamond / tech double diamond | **implemented** |
| 9 | Radar accepted order brackets | `ACC` open corner bracket at the same contact | **implemented** |
| 10 | Radar queued order brackets | `QUE` dashed bracket | **implemented** |
| 11 | Radar nearest enemy bracket | Double hostile bracket (same nearest-enemy field) | **implemented** |
| 12 | Radar threat damage flash | Hot dashed circle + `!` (existing threat timing) | **implemented** |
| 13 | Radar command subjects | Subject square + `SUB`, same recipient set | **implemented** |
| 14 | Radar carried flag marker | Flag pennant at the bracket radius | **implemented** |
| 15 | Radar carried artifact marker | Artifact diamond | **implemented** |
| 16 | Radar ripcord marker | `RIP n` radial marker + rounded seconds | **implemented** |
| 17 | Radar construction highlight | Hot label brightness only, no invented bracket | **implemented** |
| 18 | Radar name labels | Monochrome labels from the native allow-set (first tier, then clip) | **implemented** |
| 19 | Radar range labels | Metre values attached to the surviving labels; none when native clips it | **implemented** |
| 20 | Radar hull and shield bars | Two monochrome fill bars, same native stats tier | **implemented** |
| 21 | Radar ore and miner fill bars | Fill bar from the same oreSeen/miner data | **implemented** |
| 22 | Radar friendly detection eyes | `EYE` in the same label column/gate (`DetectionEye`) | **implemented** |
| 23 | Radar friendly ripcord seconds | `RIP n`, same rounded seconds/cap | **implemented** |
| 24 | Teleport lead-up sparks | Shared world-space particles — Jet adds no duplicate effect | **shared (unchanged)** |
| 25 | Radar command verb labels | Vector command marker + `ACC`/`QUE`, same command id | **implemented** |
| 26 | Radar LOD modes | All/Default/Target/None + `RDR <mode>`; the RWR never bypasses them | **implemented** |
| 27 | Radar range fade | Level-driven fade mapped to monochrome opacity; no extra sensor rule | **implemented** |

Nothing is deferred: row 24 is the only "shared" row (the shared teleport particles remain authoritative), and no
row needs a reason for deferral.

## 5. What changed

| File:line | Change |
|---|---|
| `src/.../Rendering/HudModel.cs:171` | `HudContact.RelativeBearingRadians` (optional, trailing, default null). |
| `src/.../Rendering/RadarBearing.cs` (new) | The pure own-relative bearing (sector-plane signed angle, roll-invariant, documented null cases). |
| `src/.../Game1.Rendering.cs:4325-4328` | Own forward + `Bearing(...)` local from the same render poses. |
| `src/.../Game1.Rendering.cs:4368,4464,4516,4549,4601,4653,4697,4810,4861` | Bearing passed to every contact constructor. |
| `src/.../Rendering/Jet/JetRadar.cs` (new) | Scope rectangle/round radius, range bands, bearing/fallback placement, ring radii. |
| `src/.../Rendering/Jet/JetHudRenderer.Radar.cs` (new) | The radar renderer: frame, bands, mode word, glyph pass, glyph-word pass, label pass, `0 B/frame`. |
| `src/.../Rendering/Jet/JetReadoutPlacement.cs` (new) | The placement priority (`right/left/below/above`, caption `above/below/right/left`), bounded slides, least-covered fallback. |
| `src/.../Rendering/Jet/JetHudRenderer.cs:237-259,342,396` | Per-frame furniture/readout registry, radar in the pass order. |
| `src/.../Rendering/Jet/JetHudRenderer.cs:917,943,969` | Compact name truncation + the placed readout rect. |
| `src/.../Rendering/Jet/JetHudRenderer.cs` (`DrawLock`, `DrawTargetDesignator`) | Slot placement, cue outer-radius clearance, caption avoids the readout. |
| `src/.../Rendering/Jet/JetHudRenderer.cs` (`DrawTag`) | Suppressed while the F4 pane is present. |
| `src/.../Rendering/Jet/JetInventoryPresentation.cs:119-170` | `MinPaneScale`, `FitScale` (pane-vs-caution rule), scale-aware `LeftAt/WidthAt/HeightAt/TopAt/TitleTopAt/HeaderTopAt/RowTopAt`. |
| `src/.../Rendering/Jet/JetHudRenderer.StoresCaution.cs:87-91,464-470,613-620,644-660` | Stores/caution/pane registered as furniture; pane drawn last as the overlay; fitted pane scale. |
| `src/.../Rendering/Jet/JetHudRenderer.StoresCaution.cs` (compact annunciators) | `2·scale` word gap so the row clears the ladder column. |
| `src/.../Rendering/Jet/JetTargetGeometry.cs:47` | `CompactNameWidth = 72` (native px × scale). |
| `src/.../Rendering/Jet/JetVectorPainter.cs:56-67,328-333` | Optional `IJetElementAudit` channel + `Element(id)`. |
| `tools/hud2-proof/Program.cs` | Element-stamped runs/strokes, `TextOverlapCross` (+ the two exceptions), radar scenes, `jet-radar-*` model generator, per-scene round option, placement in the JSON, `HUD2_JET_RADAR_COST`, schema 7. |
| `tests/.../Skins/JetRadarTests.cs` (new) | 16 tests: bearing, scope/bands/fallback, every kind + overlay, round option, pane hiding, placement priority, compact name, pane-vs-caution, 0 B/frame. |

No `docs/parity/*.csv` row changes status (presentation only).

## 6. Audit (96 Jet scenes)

```
HUD2_JET_AUDIT stroke-vs-text 0, text-outside-plate 0, text-off-screen 0, icon-too-small 0,
               text-in-plate 0, cue-vs-readout 0, legible-behind-pane 0,
               text-overlap-cross 2 over 96 scenes
```

`TextOverlapCross` 2 = the single listed exception in §2.3, counted on both backdrops of `jet-locked` 1920. Every
other scene — including all 21 radar scenes (square, round, dense 63-contact, dense 4K, kinds, nobearing, all four
LODs) — is 0 in every class. The radar scenes' labels are inside the scope by construction (clamped to the inner
rectangle), which is why `text-off-screen` stays 0 at 800×480 and at 4K.

## 7. Draw cost (dense battle, same scene)

`jet-radar-dense` — 63 contacts with labels at LOD active, 1920×1080, 120 frames after a warm-up:

```
HUD2_JET_RADAR_COST scene=jet-radar-dense 1920x1080 contacts=63 jet=1.292 ms/frame modern=1.470 ms/frame ratio=0.88
```

The Jet radar frame costs ~0.88× the Modern radar frame on the same contact set (both whole-frame draws, so the
shared elements are included and the radar dominates at this density). Steady-state allocation is 0 B/frame (test).

## 8. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built**, 0 warnings / 0 errors |
| New/affected tests 3× alone | `--filter JetRadarTests\|JetStoresCautionTests\|JetTargetLockTests` ×3 | **168/168 each run** (`Passed 168, Failed 0`) |
| Skins namespace | `--filter FullyQualifiedName~Skins` | **341/341** (was 325; +16 Jet radar tests) |
| Zero-delta | `--capture` before the first edit → `--compare` after | **1,848 combinations, 0 pixel, 0 semantic** (83 animation-only) |
| Jet capture + audit | `hud2-proof --jet` | `HUD2_JET_CAPTURE_PASS 96 scenes`; audit line above |
| Fast tier | `tools/run-tests.sh --tier fast` | **15,050 tests, 748 s, 5 failed / 7 not executed, 12 calibrated-limit offenders (timing only)** — every failure is a ledgered load family, each **3/3 alone** (`~/allegiance-evidence/P1039/flakes3x.log`): `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (class 2 tests, 3/3 runs green; one interleaved run of the pair failed once in nine), `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` (delay 5/7), `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster`, `ChaffTests.Live_firing_mounted_probe_launcher_creates_probe_decrements_ammo_and_broadcasts`. The two HUD allocation draws that appear as timing offenders pass alone in 1 s total (`~/allegiance-evidence/P1039/fast.log`). |
| AOT publish | not run — no `src/Allegiance.Server` or `src/Allegiance.Core` change | n/a |
| Training gate | not run — no training-affecting file changed (HUD renderer + proof tool + tests only) | n/a |

## 9. Screenshots (each opened and read)

The vision model's prose was unreliable at the pixel-font densities (it read the square frame's rings as
"concentric circles"), so every claim below is tied to the geometry in `jet-audit-summary.json` (the scope
rectangle, slot indices, clear flags) and to the audit counts, not to prose alone.

| Image | What is visible |
|---|---|
| `jet-radar-1920x1080-dark.png` | Bottom-right square scope: four-sided frame with corner brackets, four band rings with metre labels (…250, 1250, 2500, 5000), the `RDR All` mode word bottom-right, contact marks with name/range labels on the right side of the scope; the throttle tape and its IDLE word stay clear of the scope's right edge. |
| `jet-radar-800x480-bright.png` | The same square scope at the compact window over bright space: the frame, rings, tiny labels; the scope's right edge stops before the throttle band. |
| `jet-radar-round-1920x1080-dark.png` | The circular RWR variant: one inscribed circle with four axis ticks, the same band rings, mode word and contact marks as the square capture (same rectangle, same labels). |
| `jet-radar-round-800x480-dark.png` | The compact circle: frame + rings inside the same 154 px square band. |
| `jet-radar-dense-1920x1080-dark.png` | 63 contacts packed in the scope: triangles, squares, circles, pentagons, darts, crosses, diamonds, `M`/`P`/`T`/`SUB`/`RIP 7`/`!` marks, `ACC`/`QUE` verbs, labels clipped inside the frame. |
| `jet-radar-dense-800x480-dark.png` | The same dense set at 800×480: no label leaves the scope, no label crosses the throttle band. |
| `jet-radar-dense-3840x2160-dark.png` | The 4K frame: the same vectors crisp, no sub-pixel hairline loss. |
| `jet-radar-nobearing-1920x1080-dark.png` | Every contact placed by the **projected fallback** (null bearing): the marks keep their projected positions inside the scope, no fabricated direction. |
| `jet-locked-800x480-dark.png` | The designator readout placed below the cue, `LOCK 62%` above it, none of them on a ladder numeral, `CUR` or `PITCH`. |
| `jet-f4-800x480-bright.png` | The F4 pane below the master-caution panel: the caution header and its line stay visible above the pane's top edge. |

## 10. Anything unfinished / caveats

* **One intentional exception** (§2.3) in `jet-locked` 1920: the readout's least-covered slot shares ~22 px with
  the `+30` ladder numeral. Every other scene is 0 in every class.
* **Radar label nodes are text, not icons**: `icon-too-small` covers the icon glyphs the renderer reports; the
  radar's procedural marks are strokes/fills, so the class does not apply to them (0 by construction).
* **The vision model could not read the scope's shape reliably** at these densities; the geometry and audit
  numbers are the evidence, and the images were still opened.
* `docs/TEST_FLAKES.md` was not touched: the affected tests are headless and run in ~90 ms each run.
* Four existing Jet smoke tests were updated for the radar contract (the shared Classic radar pass is no longer
  composed for Jet — `resources.Radar` is now 0, and the pass order is `tags → Jet's own pass → pod/death`); that
  is the story's intended change, not a behaviour regression.
* The brief file `P1039_BRIEF.md` stays untracked (excluded through `.git/info/exclude` in the shared git dir).
* New media (10 PNGs + 1 JSON, 1.73 MiB) needs Domingo's review before any push.

## Native intent

Native's radar is the allowed-set projector: `RadarImage::DrawBlip` places every nearby object at its projected
position or pins it to the real direction at the edge (`WinTrek/radarimage.cpp:286-427`, the eye-space clamp in
`WinTrek.cpp:7882-7960`), the per-kind LOD decides which icons exist (`radarimage.cpp:703-980`), and the label tier
decides which readings reach the pilot (`:1199-1441`). The round option is native's own
`m_bRoundRadar` inscribed circle (`WinTrek.cpp:7726`, `radarimage.cpp:167-173`), the four LOD modes and their words
are native's (`radarimage.cpp:2-5`, `WinTrek.cpp:9955-9975`), and the important-mask brightness dim is
`radarimage.cpp:1094-1096`. The Jet RWR draws that same snapshot — never a wider one — as a bearing/range
instrument, which is why the only shared addition is a value derived from the poses the projection already uses
(no new sensor, no new eligibility rule, no targeting change).

## Modern approach

The scope is a pure function of the shared model: geometry and placement live in `JetRadar` (headlessly testable),
the drawing in one partial file, and the placement priority in `JetReadoutPlacement` (pure, unit-pinned slot order
and coverage arithmetic). The proof stamps every run and stroke with its element so `TextOverlapCross` is a fact in
`jet-capture.json`/`jet-audit-summary.json`, the placement and its slot/clear flag are recorded per scene, and the
dense-frame cost is measured against the Modern radar on the identical contact set. Classic and Modern remain
pixel- and semantically identical, proven by the 1,848-combination comparison against a baseline captured before
the first edit.
