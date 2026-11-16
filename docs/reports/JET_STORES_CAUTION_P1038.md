# P1038 — Jet HUD skin, story 4/9: stores block, master-caution panel, F4 inventory pane

Implemented in the `hermes/p1038` worktree of `AllegianceCore`, base commit `497e3507` (the P1037 merge). No commit,
no push, no stash, no remote host, no Docker (not installed here). Raw captures live outside the repo in
`~/allegiance-evidence/P1038/` (baseline capture, final Jet capture, sweep/build/compare logs, the asset and
re-encode scripts); the report's assets are the **33 PNGs +
`jet-capture.json`** under `docs/reports/JET_STORES_CAUTION_P1038_assets/` (2,000,250 bytes = 1.91 MB — inside the 2 MB task
cap; every PNG is a lossless RGB re-encode whose decoded pixels were compared byte-for-byte with the proof output).

This story is P972 §4.3 (the stores paragraph and the F4 inventory), §4.4 (**caution panel only — the RWR is
P1039**), §4.5 compact rules, and the §5.1 own-ship (17 rows) / §5.5 alert rows / §5.6 inventory (19 rows) Jet
columns. Jet stays presentation-only: every value is shared `HudModel`/`HudGauge`/`HudOwnStatus`/
`HudFrame.WeaponSystems`/`HudContext.MissileTrack`/`HudInventoryPane` data, nothing is invented (no invented zero,
no countdown the model does not carry), and no server, Core, wire, simulation, input-binding or audio path is
touched.

## 1. Zero-delta baseline first, then the comparison

The **unmodified** tree was built (`tools/hud2-proof`, Release) and captured **before any edit**:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1038/baseline
HUD2_CAPTURE_PASS 1848 combinations -> ~/allegiance-evidence/P1038/baseline   (manifest: commit 497e3507+dirty, driver llvmpipe)
```

After the change the same tree was re-rendered against that baseline:

```
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), 0 corrupt golden(s)   (final tree)
```

The P980/P988 matrix is unchanged: **22 scenarios × 7 resolutions (1920×1080, 2560×1440, 1280×720, 1600×900,
1366×768, 800×480, 3840×2160) × 3 sizes (0.75/1/1.5) × 2 backdrops (dark/bright) × 2 skins = 1,848
combinations** (924 Classic + 924 Modern), decoded-RGBA goldens plus a semantic element/visibility snapshot per
combination. The only differences are the pixels inside the fixture's recorded animation mask (the wall-clock
low-resource `!` badge at `ModernHudRenderer.cs:166`, documented in P980 §3 — 84 then 83 of 1,848 entries on the
two runs), and every entry reports `outside: 0`. **Classic and Modern stay pixel- (decoded RGBA) and semantically identical:
1,848/1,848 combinations, 0 pixel diffs, 0 semantic diffs.** Everything P1038 adds is Jet-only code plus
presentation-only fields neither shared renderer reads (`HudContext.MissileTrack`,
`WeaponStatusRow.IsLauncher`, the `HudModel` warning-text constants).

## 2. P1037 review follow-ups (Jet-only code, with before/after crops)

### 2a. Lock diamond vs target designator geometry

Measured geometry of the real frame (both shapes from the same projected radius, `Game1.Camera.cs`
`ComputeTargetSelectionBracket`/`ComputeLockBracket`): the designator is a **square** whose corner travels
`half·√2` from the centre, while a diamond of half-span `r` reaches only `r/√2` along that same diagonal
(`JetTargetGeometry.InverseSqrt2`). Before, with `radius = 120`: designator half = 126 → corner **178.2 px**,
diamond boundary at `141.2/√2` = **99.8 px** (partial 62 %) — the corners poked **78.4 px** out through the
diamond, and the readout started at the designator box edge, so `HOSTILE …` crossed the diamond's segments.

Fix (pure, unit-pinned):

* `JetTargetGeometry.LockDiamondHalf(state, fraction, screenRadius, toEdge, scale)` (`JetTargetGeometry.cs:108`) —
  the native per-state sizing **unchanged** (`LockBracketStyle.HalfSizeForFraction` = native
  `radiusBracket + 40·(1−lock)`, radarimage.cpp:325-332) with the P1037 readability floor, so the native shrink
  still drives the size change (`jet-locked` 141.2 → `jet-lockfull` 126.0 at radius 120).
* `JetTargetGeometry.DesignatorHalfInsideLock(half, diamond, scale)` (`:125`) — the designator contracts to
  `min(half, r/2 − gap/√2)`, which is exactly the condition for the corner (`half·√2`) to stay inside the diamond's
  boundary (`r/√2`) with `LockClearance = 6·scale` px to spare at **every** lock fraction.
* `JetHudRenderer.LockHalfAt` (`JetHudRenderer.cs:897`) feeds that diamond half to the designator when the lock is
  centred on it; the readout is placed at `cx ± max(half, lockHalf) + 8·scale` (`:800-812`), so no readout text can
  cross a diamond stroke (unit-pinned at 1080p).
* The readout also drops below the `HDG` plate and the two tape captions when it would cover them
  (`BelowIfCrossed`, `:880`), which removes the 800×480 collisions the new lock geometry exposed.

Measured in the proof's capture JSON (`lockFit`, computed from the same public functions):

| Scene | Radius | diamond half | designator half (natural → used) | corner → diamond boundary | gap | inside |
|---|---|---|---|---|---|---|
| `jet-locked` 1080 | 120, lock 62 % | 141.2 | 126.0 → 66.36 | 93.84 → 99.84 | **6.0** | yes |
| `jet-lockfull` 1080 | 120, lock 100 % | 126.0 | 126.0 → 58.76 | 83.10 → 89.10 | **6.0** | yes |
| `jet-locked` 800×480 | 120, lock 62 % | 135.8 | 120.6 → 65.08 | 92.04 → 96.04 | **4.0** | yes |
| `jet-locksmall` 1080 | 22, lock 62 % | 42.0 | 23.1 → 16.76 | 23.70 → 29.70 | **6.0** | yes |
| `jet-locklarge` 1080 | 145, lock 100 % | 152.25 | 150.0 → 71.88 | 101.66 → 107.66 | **6.0** | yes |

Before/after crops (opened and read): `lock-before-1920x1080-dark.png` shows the diamond with the square's corner
strokes **outside** its edges and the `LOCK 62%` / readout text crossing it; `lock-after-1920x1080-dark.png` shows
the four corner strokes closed inside the diamond with a visible gap, `LOCK 62%` above it and `HOSTILE / …`,
`1250 m CLO +43`, `HULL 060 SHLD 030` entirely to the right of the diamond. The same pair at 800×480
(`lock-before/after-800x480-dark.png`) shows the compact behaviour.

### 2b. Fixture mismatch (proof scenes)

`tools/hud2-proof` gave the Jet lock scenes `HudLock.ScreenRadius = 120` but no `HudTarget.ScreenRadius`, so the
designator fell back to the 34 px default — not what the game produces. The fixture now pins **one** projected
radius on both (`Target(... ScreenRadius: LockRadius(scenario))`, `Lock(..., LockRadius(scenario))`,
`Program.cs` `LockRadius`), and adds the two requested lock scenes: `jet-locksmall` (radius 22 → diamond 42 px,
the P1037 floor) and `jet-locklarge` (radius 145 → designator at the 150 px ceiling, diamond 152.25).

### 2c. Current-pitch numeral

`JetHudRenderer.cs:102,158,488`: the bare `00` under the ladder is now `PITCH +05` / `PITCH 00` (`PitchCaption`
+ the signed value), same position rules. Crops `pitch-before-1920x1080-dark.png` (reads `00`) and
`pitch-after-1920x1080-dark.png` (reads `PITCH 00`) are in the asset folder.

## 3. Changes

| File:line | Change |
|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetStoresPanel.cs:1` | **New** — pure stores geometry/gating: bottom-left block (`96·scale`, fixed 166 px bottom inset), 8 rows / 3 compact rows, segmented-bar geometry, the shared low/critical thresholds, the store vocabulary (`HULL/SHLD/ENG/FUEL/OXY/GUN`), the gun empty gate (`WeaponReloadTracker.EmptyEpsilon`), the launcher arming word and the annunciator flag set. |
| `.../Jet/JetCautionModel.cs:1` | **New** — the master-caution aggregation: 13 caution kinds, the P972 §4.4 priority bands, `Build(model, Span<JetCaution>)` (allocation-free), `RipcordSeconds(label)`, the 5-line / 2-line compact limits and the panel's right inset. |
| `.../Jet/JetInventoryPresentation.cs:1` | **New** — the Jet F4 pane presentation: native anchor/slide geometry (`InventoryPaneLayout`), the row/column grid, `StateWord` (EMPTY/MOUNT/ARM/READY), and `StatsRuns` which splits the row's own `StatsText` into `RNG/DMG/ROF`, `SHD STR/RECH`, `AB SPD/AB END`, `CLK STR/CLK END`. |
| `.../Jet/JetHudRenderer.StoresCaution.cs:1` | **New partial** — `DrawStores` (`:73`), `SegmentedBar` (`:228`), the annunciator glyphs (`:283-350`), `DrawCaution` (`:381`) with the cached caution lines and the shape glyph, `DrawInventory` (`:520`) and `DrawInventoryRow` (`:600`) with the selected/active markers, `EMPTY` outline, `QTY`/`A`, MOUNT/ARM bars and the cached stat columns. |
| `.../Jet/JetHudRenderer.cs:32` | The class becomes `partial`; `Coverage` grows to **64** ids (`:45`); the three draws run inside the skin's pass (`:306-308`). |
| `.../Jet/JetHudRenderer.cs:186,1371,1395` | `_headingPlate` / `_speedCaptionBox` / `_throttleCaptionBox` recorded by the existing readout and caption draws (both now return their rectangle). |
| `.../Jet/JetHudRenderer.cs:741-762,800-812,880` | Designator contracts inside the lock diamond (`LockHalfAt`), the readout clears the diamond and drops below a HUD plate/caption it would cover. |
| `.../Jet/JetHudRenderer.cs:947-956` | `DrawLock` uses the shared `LockDiamondHalf` (native shrink + floor) instead of its own copy of the formula. |
| `.../Jet/JetHudRenderer.cs:102,158,488` | `PITCH nn` numeral. |
| `.../Jet/JetTargetGeometry.cs:96-131` | `LockClearance`, `InverseSqrt2`, `LockDiamondHalf`, `DesignatorHalfInsideLock`. |
| `.../Rendering/HudModel.cs:222` | `HudContext.MissileTrack` (`MissileTrackState?`, null when the client did not supply it) — presentation only. |
| `.../Rendering/HudModel.cs:379-381,462-463` | `WarningOutOfBounds` / `WarningSectorCrowded` constants (Classic/Modern print the same strings; Jet maps the same bits to `BOUNDARY`/`CROWD`). |
| `.../Rendering/HudModelAdapter.cs:34,54` | `missileTrack` parameter → `HudContext.MissileTrack`. |
| `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:3300,3588` | Reads `world.MissileTrack` once per frame (plain data) and passes it into the model. |
| `src/Allegiance.ClientCore.Game/Flight/WeaponReloadTracker.cs:40,54,147,153,194` | `WeaponSlotDescriptor.IsLauncher` / `WeaponStatusRow.IsLauncher` (trailing, defaulted) so the stores block can tell a launcher from a non-ammo energy weapon. |
| `src/Allegiance.ClientCore.Game/Game1.Panes.cs:1256` | Sets `IsLauncher` from the part type (native `OT_magazine`/`OT_dispenser` family). |
| `tools/hud2-proof/Program.cs` | `--jet` grows from 47 to **74 scenes** (stores normal/low/critical, pod, flags, caution stack, ghost, F4 full, F4 empty, the two lock radii; dark/bright at 1920×1080 and 800×480 where the state is colour-sensitive); the fixture pins one radius on both lock shapes, drives `MissileTrack`/`Flashing`/`WarningMask`/`IsLauncher`, and records `storesVisible`, `gauges`, `caution`, `inventory*`, `missileTrack`, `flashing`, `lockFit` (schema 4). The layout audit now reports fill sizes and treats a fill taller than four rows as a **pane** whose own content may sit over the plates beneath it. |
| `tests/.../Skins/JetStoresCautionTests.cs:1` | **New** 59-test suite (§4). |
| `tests/.../Skins/JetSkinScaffoldTests.cs:365-420`, `JetFlightSymbologyTests.cs:249-256` | Coverage assertions updated to the 64 ids (22 → 64); the P1037 target-block sentinel test now confines its `HULL`/`SHLD` absence check to the data block's own band, because the stores block legitimately draws those words. |
| `GLOSSARY.md:1043` | New entry: `JetStoresPanel`, `JetCautionModel`, `JetInventoryPresentation`, the caution priority bands, the F4 pane columns. |

### Design decisions

* **Stores placement (justified).** Bottom-left at `96·scale`, bottom edge a **fixed** 166 px above the viewport
  bottom: right of the speed tape's tick labels (which end at ≈76 px at 1080p) and of its boxed readout plate,
  below the boresight row, above the two shared bottom-left text lines that are drawn at fixed offsets (cargo
  `h−132`, flight parts `h−158`, `Game1.InFlightUtility.cs`/`Game1.FlightParts.cs`) rather than at the HUD scale.
  The tapes, the centred ladder, the target data block (`w/2+70`), the radar/mini-map (bottom-right) and the chat
  pane (top centre) are all outside that band. The proof's layout audit measures **0 caption / 0 plate overlaps in
  all 74 scenes**.
* **Stores = grouping, not a reduced inventory.** The block reads only the six flight gauges (so `FUEL` becomes the
  pod's `OXY` with the same fraction, exactly as the shared model relabels it), the own-status flags, and
  `HudFrame.WeaponSystems` for the gun count and the launcher count/arming word. The F4 pane is the full native
  inventory — nothing is dropped from it.
* **State without a second hue.** Every store state is a word (`A`, `READY`, `NOT READY`, `DRAINED`, `EMPTY`,
  `MOUNT`, `ARM`) plus a segmented bar / marker box; critical ink is the one Jet hue's hot tone (P972 §4.1).
* **A pod's zeros are not an emergency.** Native zeroes the ammo and energy gauges in an eject pod
  (dialog.mdl:208,435,470,507), so those two store lines never raise a resource caution while `IsAdriftLifepod`;
  the pod's own line is `POD OXY <seconds>` from the shared oxygen countdown.
* **Caution panel reads, never owns.** `JetCautionModel.Build` is a pure read of the shared model: the damage line
  exists exactly while `HudFrame.Flashing` (so it blinks on `DamageFlashTracker`'s native 0.25/0.25/2.0 s clock),
  the missile line exactly while `HudContext.MissileTrack.IsTracked` (the world's `MISSILE_TRACK_WARNING(208)`
  snapshot), the resource lines exactly at the shared thresholds, the tips exactly while they are in
  `HudConsoleText.CriticalTips`, the badges exactly under `HudConsoleFormat.LivesVisible/KillsVisible`, the
  boundary/crowd lines exactly while `HudModel.Warnings` carries the mask bits. The panel has no acknowledge,
  suppression, delay, timer or sound member; a skin switch mid-caution therefore cannot restart the native warning
  loop (tested like P1037's lock test). **The mission countdown is not drawn**: `HudModel` carries no countdown
  reading (the `HudElement` row is `Available=false` for every renderer), so inventing one is out of the question;
  it stays on the shared pass like the sector/console text that belongs to P1040.
* **Caution layout.** Top-right, right-aligned 24·scale inside the edge; the panel's top is resolved from its own
  measured height so the stack always ends **above** the throttle tape's caption band (`h·0.2 − line height`), and
  the line pitch is at least the measured glyph height, so rows cannot overlap at either resolution. Full = up to
  five lines, compact = the two highest-priority hot lines (P972 §4.5).
* **F4 pane = the same model, layout and slide.** It draws only while the shared gate is open
  (`FlightVisible && (InventoryVisible || InventorySlide > 0)` with a resolved `InventoryPane`), at the native
  `SideBottomRight(-36,40)` anchor with `InventoryPaneLayout.BottomY`'s mini-map clamp and the **same 190 px
  × (1−slide) offset** (dialog.mdl:5387, 0.5 s). Keys, hit behaviour and the F4 open/close are the F4 binding, not
  the skin. The 214 px native line cannot hold the §5.6 stat columns at a legible stroke, so the Jet pane keeps the
  anchor/slide/row pitch but sizes its content to 480 native px and draws the row text at 0.72 × the pane scale (a
  table font). At 800×480 every row and column still fits — the only abbreviation is that a stat run is dropped
  when it would not fit the pane's right edge (the value stays in the shared model), and long part names use the
  native measured-fit rule.
* **One stat derivation.** `StatsRuns` splits the row's **own** `StatsText` (the builder's `$"{range} / {damage} X
  {rate}"`, `$"{strength} / {recharge} sec"`, `$"{topSpeed}mps / {endurance}"`, `$"{strength}% / {endurance} sec"`)
  into labelled columns; no formula is recomputed. The parse is cached per row slot by content equality (the
  builder allocates a fresh but equal string per frame), so a steady frame allocates nothing.
* **4K keeps the physical sizes.** `ModernHudScale.Core` (P833/P859) is the density product capped by the window
  budget, not `height/1080`, so at 3840×2160 on a 1.0 desktop the stores/caution/pane geometry is identical to
  1080p and the painter's 1.5 px floor keeps every stroke a whole physical pixel (unit-pinned).
* **Zero allocations.** Every readout string is cached by its integer value (the store percents, gun/missile
  counts, KB/SIG, the cloak strength, the caution lines) and the stat runs by content; `Build` fills a preallocated
  `JetCaution[]`; no closure, LINQ or collection expression runs inside `Draw`. Proved at **0 B / 100,000 frames**
  with stores + a five-line caution + an open 13-row F4 pane (§4).

## 4. Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetStoresCautionTests.cs` — **59 tests**:

* **stores gates**: every supplied field renders (HULL/SHLD/ENG/FUEL/GUN/KB/SIG with the shared gauges' own
  percentages); blank without telemetry or outside the instrument gate; the pod reads `OXY` with the shared fuel
  fraction; the gun `A` cue only at the shared empty epsilon; the missile row reads the launcher's own count and
  `READY`/`NOT READY`; each annunciator exists exactly under its own `HudOwnStatus` flag; cloak strength and
  `DRAINED`;
* **caution priority**: the immediate band (missile → damage → pod) precedes critical resources, which precede
  boundary/system, with a non-decreasing priority check over the resolved span; a pod's native-zeroed ammo/energy
  gauges never raise a resource caution (`A_pods_zeroed_ammo_and_energy_never_raise_a_resource_caution`), because
  those zeros are native's `IsNotEjected` zeroing, not an emergency;
* **damage-flash timing**: six samples across a `DamageFlashTracker` window (0.1/0.3/0.6/1.1/1.9/2.5 s) assert the
  panel's `DAMAGE` line is present exactly when the shared clock says lit, and that the window ends after 2.0 s;
* **no timer / no sound / no retrigger**: no `Sound`/`Ack`/`Timer` member on the model or the renderer; a
  Classic→Jet→Modern switch with a caution held keeps one panel per frame (9 submissions → 18), one shared-pass set
  per draw and one batch session per frame;
* **F4 rows**: all 13 native row kinds render with the shared pane's own key/name/quantity/state values and the
  `A` cue, every stat value is a field of that row's `StatsText`, and the vocabulary (`RNG/DMG/ROF`, `SHD
  STR/RECH`, `AB SPD/END`, `CLK END`) is present; selected/active is a shape; the pane's right edge is the native
  anchor and a mid-slide pane sits exactly 95 px (190 × 0.5) below the open one;
* **compact**: three store rows / two caution lines at 800×480, and 0 caption / 0 plate overlaps at both
  resolutions with the stores + caution + open F4;
* **P1037 follow-ups**: designator corner inside the diamond's diagonal at lock 0 / 0.25 / 0.62 / 1.0; the native
  shrink (open − full = 40 px) and the edge clip; a locked 1080p designator's readouts all start right of the
  diamond's vertex; `PITCH +05` present at both resolutions and still below the ladder;
* **4K**: at 3840×2160 on a 1.0 desktop the scale stays 1.0 (`ModernHudScale.Core`), the painter's stroke is at
  least the 1.5 px physical floor, the stores block keeps its 1080p column/row pitch and no label covers the SPD
  caption — no inflated sizes, no hairlines;
* **0 B/frame**: `P1038 JetHudRenderer.Draw (stores+caution+inventory): 0 B / 100000 frames = 0 B/frame`;
* **draw smoke** for the 21 `HudModel` states P1037 used: no throw, one shared pass each, the caution panel exactly
  when `JetCautionModel.Build` finds a caution, the stores exactly under the instrument gate with telemetry, and no
  sentinel text.

Existing suites stay green: `JetTargetLockTests` 71, `JetFlightSymbologyTests` 62, `JetSkinScaffoldTests` 45 (the
coverage literals updated) and the switching/catalog/migration suites — **303/303 tests in the `Skins` namespace**
(27 s).

```
P1038 run 1: Passed 59, Failed 0, Skipped 0 (552 ms)
P1038 run 2: Passed 59, Failed 0, Skipped 0 (545 ms)
P1038 run 3: Passed 59, Failed 0, Skipped 0 (566 ms)
```

Nothing here exceeds 10 s, so no `[Trait("Tier","Live")]` is needed and all 59 run in the fast tier.

## 5. Screenshots

`tools/hud2-proof --jet` at 1920×1080 and 800×480 on dark and bright backdrops; the proof renders 74 scenes (dark
and bright pairs at both resolutions for the new states), and the cited set is the 33 PNGs kept under the 2 MB cap
in `docs/reports/JET_STORES_CAUTION_P1038_assets/` (2,000,250 bytes = 1.91 MB with the capture JSON): every P1038
state at 1920×1080 and 800×480 dark, the bright pairs where colour contrast is the point (stores, caution, F4,
locked), the four core lock scenes and the six before/after crops. The proof writes
RGBA PNGs; each kept PNG is an RGB re-encode (`ffmpeg -pred mixed -compression_level 9`) whose decoded pixels were
compared byte-for-byte with the source (`cmp` on raw RGB) — the asset is pixel-identical to the frame the layout
audit measured. Every image below was opened and read; the read-back is summarised and the capture JSON's measured
numbers corroborate it.

| Image | What is actually visible |
|---|---|
| `jet-stores-1920x1080-dark.png` / `-800x480-bright.png` | Normal stores: `STORES` header and `HULL 82%`, `SHLD 70%`, `ENG 100%`, `FUEL 90%`, `GUN 80% 160`, `MSL 2 READY`, `KB 15 SIG 42`, `AUTO VLOCK FLAG` (the fixture's latched autopilot/vector-lock/flag), each row positioned in the lower-left block; the compact frame shows the same values on three rows with the annunciator words appended. |
| `jet-stores-low-1920x1080-dark.png` / `-800x480-dark.png` | Low resources (hull 45 %, shield 40 %, fuel 35 %): the numbers stay the same shape/ink; the caution panel appends `HULL 45%`, `SHLD 40%`, `FUEL 35%` in priority order under the hot lines. |
| `jet-stores-critical-1920x1080-dark.png` / `-800x480-dark.png` | Critical store scene: `MASTER CAUTION` with `DAMAGE` and `HULL 8%`, `SHLD 5%`, `ENG 2%`, `FUEL 10%`, `GUN 0% 0 A` (the empty-pool `A` cue), the hot `!` glyph before each line, and the stores block reading the same fractions. |
| `jet-pod-1920x1080-dark.png` / `-800x480-dark.png` | Adrift pod: the fuel row reads `OXY 63%` (same fraction), `GUN 0%` with the empty-pool `A`, `ENG 0%`; the caution panel shows exactly `POD OXY 95 s` (no resource line for the pod's structurally zeroed ammo/energy gauges) above the fixture's critical tip. |
| `jet-flags-1920x1080-dark.png` / `-800x480-dark.png` | Own-status annunciators lit: `EYE CLK 70 AUTO VLOCK FLAG ART`, each with its small shape glyph (eye oval, cloak chevron, autopilot bar, vector-lock corner, flag pennant, artifact diamond). |
| `jet-caution-1920x1080-dark.png` / `-bright.png` / `-800x480-dark.png` / `-bright.png` | A five-caution stack: `MSL TRACK 72%`, `DAMAGE`, `ENG 3%`, `HULL 5%`, `BOUNDARY` (missile/damage first, then the critical resources, then the boundary bit), the hot lines marked with the `!` glyph and the quiet one with an open square; at 800×480 the same scene shows exactly two lines (`MSL TRACK 72%`, `DAMAGE`) with no overlap of the `THR` caption. |
| `jet-ghost-1920x1080-dark.png` | Ghost lifecycle: `PILOT DOWN / GHOST`, the shared critical tip `Reloading ammo...`, `LIVES 3`, `KILLS 7`, `RIP 9`, then the stores block unchanged. |
| `jet-f4-1920x1080-dark.png` / `jet-f4-800x480-dark.png` | The open F4 pane (`Fig Adv` title): two weapons with `RNG 800m DMG 15 ROF 8` / `RNG 1200m DMG 20 ROF 5`, a turret occupant, `Observers: 2`, a `MOUNT` row, the launcher `ARM` row with `RNG 1125m DMG 150`, shield `SHD STR 500 RECH 3 s`, `AB SPD 220mps AB END 8`, `CLK STR 82% CLK END 12 s`, chaff `QTY 40`, and the two `S`/cargo rows with quantities; the selected Gatling row carries the open marker box and a solid active square. At 800×480 the same rows are present and legible inside the compact pane. |
| `jet-f4-empty-1920x1080-dark.png` | The empty-mount pane: `EMPTY` rows for `<small weapon mount>`, `<missile mount>`, `<shield mount>`, `<cloak mount>`, `<afterburner mount>` and `<cargo mount>`, each with its dashed open outline and eligible key; the out-of-ammo Gatling row keeps its hot `A`. |
| `jet-locksmall-1920x1080-dark.png`, `jet-locklarge-1920x1080-dark.png` | The two new lock radii: radius 22 (diamond 42 px, designator 16.8 px half) and radius 145 (designator at the 150 px ceiling, diamond 152.25) — both with the designator inside and the readout right of the diamond. |
| `jet-locked-1920x1080-dark.png` / `-800x480-dark.png`, `jet-lockfull-1920x1080-dark.png` / `-800x480-dark.png` | Partial (open 16-segment diamond, `LOCK 62%`) and full (closed diamond, `LOCK`, hot) locks with the designator inside; the 800×480 pair shows the compact layout with the readout clear of the diamond and of the `HDG` plate. |
| `jet-level-1920x1080-dark.png` / `-800x480-dark.png` | The reference frame used for the pitch crops: level flight, `HDG 045`, `PITCH 00` under the ladder, the tapes, the stores block and the caution panel (`Reloading ammo...` — the fixture's own critical tip). |
| `lock-before-1920x1080-dark.png` vs `lock-after-1920x1080-dark.png` | Before: the square corner strokes stick out through the diamond's edges and the readout crosses the diamond; after: the corners are inside with a gap and the readout starts to the right of the diamond's vertex. |
| `lock-before-800x480-dark.png` vs `lock-after-800x480-dark.png` | The same pair at 800×480. |
| `pitch-before-1920x1080-dark.png` vs `pitch-after-1920x1080-dark.png` | The ladder's current-pitch numeral: a bare `00` before, `PITCH 00` after. |

`jet-capture.json` (schema 4) records, per scene, the shared gates the three widgets read (`storesVisible`, the six
gauges with label/fraction/visibility, the resolved `caution` list with kind/priority/hot/value, the inventory
visibility/row count/slide, `missileTrack`, `flashing`, `lockFit`) and the layout audit (`CaptionOverlaps: 0`,
`BoxOverlaps: 0` in **all 74 scenes**).

## 6. Coverage

`JetHudRenderer.Coverage` grows from 22 to **64** `HudElement` ids — exactly the ids Jet now draws itself:

* **P1036/P1037 (22, unchanged)**: `Velocity direction marker`, `Roll tutorial cue`, `Speed`, `Thrust setting`,
  `Cockpit centre reticle`, `Selected target bracket`, `Target name`, `Target type description`,
  `Target side identity`, `Target hull`, `Target shield`, `Target range or sector`, `Target speed`, `Target ore`,
  `Target kill bonus`, `Target in weapon range`, `Target time to intercept`, `Missile lock flower`,
  `Ballistic lead marker`, `Aim hit-probability predictor`, `Chase weapon aim without target`,
  `Accepted and current order turn arrows`.
* **Stores (14, §5.1)**: `Hull integrity`, `Shield charge`, `Energy reserve`, `Fuel reserve`,
  `Ammunition reserve`, `Kill bonus`, `Signature percentage`, `Detection eye`, `Cloak active`,
  `Cloak strength and drained state`, `Autopilot status`, `Vector lock status`, `Carried flag`, `Carried artifact`.
* **Caution panel (10, §5.5)**: `Incoming missile warning`, `Damage flash timer`, `Lifepod oxygen endurance`,
  `Boundary and crowd warnings`, `Death banner`, `Critical tip text lines`, `Ripcord countdown`, `Lives remaining`,
  `Mission kills`, `Vote banner`.
* **F4 inventory (18, §5.6)**: `Flight equipment inventory pane`, `Equipment part identity and key`,
  `Equipment selected and active state`, `Equipment quantity and empty warning`, `Equipment mounting progress`,
  `Launcher arming progress`, `Equipment weapon range`, `Equipment shot damage`, `Equipment burst rate`,
  `Equipment shield strength`, `Equipment shield recharge seconds`, `Equipment afterburner top speed`,
  `Equipment afterburner endurance`, `Equipment cloak endurance`, `Inventory turret gunner identity`,
  `Inventory cargo slots`, `Inventory own ship name`, `Equipment empty slot affordances`.

Not claimed (and not drawn by Jet yet): `Joystick turning indicator`, `Training HUD callouts`, the radar rows,
the console/chat/command rows, `Sector location`, `Turret carrier identity`, `Accepted/Queued order text`,
`Money credits`, `Mission countdown`, `Eject pod identity panel`, `Command view ship summary` — P1040/P1042
territory or rows whose shared surface Jet still reaches through the Classic composition.

## 7. Parity and glossary

No `docs/parity/*.csv` row changes status: Jet is presentation-only (P972 §5 keeps every existing status; a new
look cannot promote a `done-visual` row). `scripts/parity-scorecard.py docs/parity/parity-matrix.csv` validates and
`hud` stays 111/111 rows, 330/330 weighted, TOTAL 99.61 %.

`GLOSSARY.md:1043` gains the P1038 entry (stores/caution/inventory pieces, the priority bands, the F4 columns).

## 8. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built**, each 0 warnings / 0 errors |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetStoresCautionTests` (3 processes) | **59/59 each run** (552 / 545 / 566 ms) |
| Existing skin tests | `dotnet test --filter FullyQualifiedName~Skins` | **303/303** (27 s) |
| Zero-delta (baseline first, then compare) | `hud2-proof --capture ~/allegiance-evidence/P1038/baseline` → `--compare back to it` | **`1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)`** |
| Jet evidence capture | `hud2-proof --jet ~/allegiance-evidence/P1038/jet-final` | `HUD2_JET_CAPTURE_PASS 74 scenes`; layout audit 0 caption / 0 plate overlaps in all 74 |
| Glossary links | `python3 tools/check-glossary-links.py` | `GLOSSARY.md link check OK (910 distinct paths checked)` |
| Parity CSV | `python3 scripts/parity-scorecard.py docs/parity/parity-matrix.csv` | `hud` 111/111 rows, 330/330 weighted (unchanged), TOTAL 99.61 % |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **14,984 tests, 555.6 s, FAIL**: Lobby 193 + Update 55 passed; MonoGame 5,709 passed / 4 not executed; ClientCore.Tests 2,451 passed; Core.Tests 6,565 passed / 3 not executed / **4 failed**. All four are ledgered load/live families (`P739TimerLiveTests…(delay: 5)` one-ULP family, `NpcWingmanDefendLiveTests…`, `MissileBroadcastTests…`, `GameOverPlayersWireTests…`); re-run alone they pass or reproduce intermittently (never 3/3), recorded in `docs/TEST_FLAKES.md` (P1038 section). The 4 calibrated-limit offenders are timing-only and already ledgered for their families |
| AOT publish | not run — **no `src/Allegiance.Server` or `src/Allegiance.Core` change** | n/a |
| `docker compose config` | not run — no service, port, store or env var changed | n/a |
| Training gate | not run — no training-affecting code | n/a |

## 9. Anything unfinished / caveats

* **Text-vs-text overlaps** remain in the fixture (informational only, reported in `jet-capture.json`): the ladder
  numerals and the designator/caution readouts can share a row of sky, exactly as the P1037 report recorded.
  Caption and plate overlaps are 0 in every scene.
* **A low-resource caution follows the gauge order** (Thrust, Fuel, Energy, Ammo, Hull, Shield), so a scene with
  several low stores lists them in that stable order, not by severity.
* **The mission countdown is absent from the caution panel** — `HudModel` carries no countdown value (the
  `Mission countdown` element is `Available=false` for every renderer), so no number is invented. It needs a shared
  game-state readout in a later story.
* **Accept/queued order text, sector and turret-carrier text are not drawn by Jet yet** (the console family is
  P1040): Jet's console phase still calls the shared chat/cargo/flight-parts passes and `Draw` calls the shared
  radar, ship-tag and pod-death passes, but the native console-text rows and the pod/death banner bitmaps are P1040
  work — Jet draws the ghost/damage/pod *cautions* instead and invents nothing in their place.
* **Compact abbreviations** (stated as required): the stores collapse to three rows and the annunciator suffix on
  the last row keeps the words only (no cloak strength number), the caution panel shows the two highest-priority
  lines, and inside the F4 pane a stat run is dropped when it cannot fit the pane's right edge (part names use the
  native measured-fit rule; the table font is 0.72 × the pane scale).
* **The F4 pane covers the throttle tape while open** (the native inventory pane owns the same side of the screen);
  the layout audit treats a pane taller than four rows as a pane, not a readout box, and skips its own content when
  checking text-vs-plate coverage.
* **The 1080p caution stack can reach five lines**; with more cautions the lowest-priority ones are not drawn
  (the shared model still carries them) — the compact limit is two by design.
* **No live/dynamic capture**: the proof renders the production renderer through a headless device (llvmpipe); no
  audio device here, so the "a skin switch does not restart a sound" rule is proved structurally (no sound member,
  one panel per frame across a switch) as in P1037.
* **New media** (33 PNGs + JSON, 2,000,250 bytes = 1.91 MB) needs Domingo's review before any push, per the lane rules.
* The brief file `P1038_BRIEF.md` is removed from the worktree root so it cannot enter the change set.

## Native intent

Native owns every value the new widgets show. The stores block presents the six gauges native draws in the
centre-HUD group (dialog.mdl:2403-2506, the hull/shield damage flash alternating on the native timer) and the
`DefaultCenterHudOn` own-status icons — eye (`dialog.mdl:2526-2534`, raw Detected bit), cloak
(`consoledata.cpp:563-575`), cloak strength (`consoledata.cpp:480-489`), autopilot (`console.cpp:793`), vector lock
(`consoledata.cpp:465-475`), flag/artifact (`console.cpp:847-863`), kill bonus (`console.cpp:824-828`) and
signature (`console.cpp:865-866`); the gun/ammo and launcher rows are the native inventory's own ready/armed
semantics (`consoledata.cpp:1157-1202`, `inventoryimage.cpp:299-346`). The caution panel aggregates only what
native already raises: the damage flash (`trekigc.cpp:4270-4283`, `WinTrek.cpp:8922-8955`, 0.25/0.25/2.0 s), the
low/critical gauge bands (dialog.mdl:2403-2506 and the glass HUD's `!`), the incoming-missile warning
(`MISSILE_TRACK_WARNING(208)`, `trekigc.cpp:5065`, whose loop stays with `MissileWarningController`), the boundary
and crowd warnings (the shared `ShipWarningMask`), the critical tips (`console.cpp:913-931`, 30 s training / 7.5 s
otherwise), death/ghost (`shipIGC.h:2465-2469`, `WinTrek.cpp:3653`), the pod oxygen (`LifepodHud`, `dialog.mdl`),
the ripcord countdown (`dialog.mdl:5139-5154`, `consoledata.cpp:521-532`), the lives/kills badges
(`console.cpp:812-821`) and the vote banner (`dialog.mdl:2114-2120`). The F4 plane is the native inventory itself
(`dialog.mdl:5385-5393`, `inventoryimage.cpp:299-346`) with the native `inv*.mdl` columns (key, selected/active,
quantity + `A`, mounting/arming bars, `GetRange/GetDamage/GetRate/GetShieldStrength/GetRechargeTime/
GetAfterburnerTopSpeed/GetAfterburnerTimeLeft/GetCloakTimeLeft`) and the native 0.5 s slide. Jet never re-derives
a stat, never invents a zero, an incoming-locker identity, a countdown or a sound: it is a second 2-D presentation
of the shared model in the one green hue (P972 §4.1), and every native timer/audio rising edge remains
authoritative.

## Modern approach

The three widgets are three pure, headlessly testable files (`JetStoresPanel` geometry/gating, `JetCautionModel`
aggregation, `JetInventoryPresentation` row/stat presentation) and one renderer partial that only composes them
through the P1035 `JetVectorPainter`; the renderer's `Coverage` grows explicitly instead of claiming native rows.
The caution panel is a read-only projection of the shared model, so the damage/missile/tip/badge timing cannot fork
from the world's clocks, and a skin switch is proven to produce no extra feedback. Every readout string is cached
by its integer value and every stat split is cached by content, proved at 0 B/frame with stores, a five-line
caution and a 13-row pane. The proof tool renders every scene twice (PNG + recording surface) so "no label covers a
caption or a readout plate" is a measured fact per scene, and Classic/Modern remain pixel- and semantically
identical, proven by the 1,848-combination decoded-RGBA comparison taken against a baseline captured before the
first edit.
