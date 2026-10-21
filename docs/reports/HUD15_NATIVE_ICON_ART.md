# HUD-15 — native command icons and mounting-bar background art

Base: master at dispatch `193e8ea3` (HUD-12/HUD-12b merged; HUD-13 is **not**
on master in this worktree). Branch/worktree `pi/hud15`, model DeepSeek V4.1
Flash. No commits, no pushes: everything is in the working tree. HUD-13 would
touch `RadarBracketRenderer`/bracket sizing, which this wave does not edit; the
shared files it may also touch (`HudRenderer.cs`, `ModernHudRenderer.cs`,
`HudConsoleText.cs`) get additive edits here (one new protected helper, icon
draws beside existing rows), so a merge is a normal textual merge — see
"Overlap with HUD-13" below.

Native citations are the checked-out tree at `~/Development/Allegiance/src/`;
artwork citations are `~/Development/Artwork/` (`dialog.mdl`, `inv*.mdl`,
`*.mdl` surfaces). All artwork numbers below were read with the port's own
`MdlTextureDecoder` and cross-checked with a throwaway header dump in
`.scratch/` (deleted at the end).

## What the native code does

### 1. Radar accepted/queued command icons

- `WinTrek/radarimage.cpp:228-235` — the constructor's load loop:

  ```c
  for (CommandID i = c_cidAttack; (i < c_cidMax); i++)
  {
      if (c_cdAllCommands[i].szAccepted[0] != '\0')
          m_psurfaceAccepted[i] = pmodeler->LoadSurface(c_cdAllCommands[i].szAccepted, true);
      if (c_cdAllCommands[i].szQueued[0] != '\0')
          m_psurfaceQueued[i]   = pmodeler->LoadSurface(c_cdAllCommands[i].szQueued, true);
  }
  ```

  The table is `c_cdAllCommands[c_cidMax]` (`Igc/common.cpp:41-52`), with the
  `CommandID` constants in `Igc/igc.h:893-913` (`c_cidAttack = 1`,
  `c_cidMax = 10`). The **load range starts at `c_cidAttack`**, so
  `c_cidDefault` (0) has an art entry in the table but a **NULL radar surface**
  — the one detail the mapping must reproduce. `WinTrek/artwork.h` has no
  separate `#define` for these (unlike `AWF_FLIGHT_*`); the stems come straight
  from the command table.
- `WinTrek/radarimage.cpp:1244-1253` — the label draw picks
  `m_psurfaceAccepted[data.m_cid]` or `m_psurfaceQueued[data.m_cid]` from the
  contact's command slot.
- `WinTrek/radarimage.cpp:1305-1326` — the glyph is drawn with
  `DrawImage3D(psurfaceIcon, data.m_color, true, offset)` at **1:1 surface
  size** (~16×16) in the label's **left column**, then the name/range text is
  offset one icon width to its right (`offset.SetX(offset.X() + xshift*0.5f)` at
  `:1305/:1388`); the label text keeps `colorOther` (`:1094-1096`).
  `data.m_color` is the full side colour (red only on a threat flash,
  `:682-684`), never the dimmed `colorOther`.
- Artwork sizes (decoded): `ackillbmp`, `accptbmp`, `acdefendbmp`,
  `acpickupbmp`, `acgotobmp`, `acrepairbmp`, `acjoinbmp`, `acminebmp`,
  `acbuildbmp` and the nine `qu*` partners are all **16×16** binary RGB565
  `.mdl` surfaces.

### 2. Console accepted/queued order glyphs

- `Artwork/dialog.mdl:1605-1620` (`QueuedOrderImage`) and `:1623-1640`
  (`AcceptedOrderImage`): `TranslateImage(BlendImage(ImportImage3D(
  "comqueuedbmp", Color(1,0,0), true), hudBlendMode), Point(-21, -13))` beside
  the 240-wide `medVerdana` line (`Color(1,0,0)` queued, `cortText` accepted).
- `Artwork/dialog.mdl:1641-1652` (`LastOrderImage`): the label at `Point(-13,0)`,
  queued at `Point(7,-20)`, accepted at `Point(7,-40)`; the pane places the
  group at `SideTopLeft(50,-37)` (`Artwork/dialog.mdl:5451`).
- Both glyphs are **16×16** white masks (tinted by the `ImportImage3D` colour),
  so the accepted glyph is yellow and the queued glyph red.
- `WinTrek/console.cpp:875-890` / `:893-908` build the `verb + " " + model`
  strings those rows show (the port already had the strings, not the glyphs).

### 3. Inventory mounting-bar track

- `Artwork/invdefault.mdl:43-52` (and the same block in `invsweapon.mdl`,
  `invshield.mdl`, `invsmine.mdl`, `invchaff.mdl`, `invsmissile.mdl`,
  `invcloak.mdl`, `invafterburn.mdl`):

  ```c
  MountingBar = ImagePane(ImportImage("mountingbarbkgndbmp", true),
      [(MountingBarGaugePane, Point(0,0))]);
  ```

- The gauge fill is the classic CortUI art
  `ImportImageFromFile("CortUI/classic/mountingbar.png", false)` with
  `GetMountedFraction` (`Artwork/invdefault.mdl:6-12`), i.e. the same 64×14
  picture as `mountingbarbmp.mdl` (verified byte-for-byte visually: dark red
  field, white "Mounting" letters).
- `mountingbarbkgndbmp.mdl` is a **64×14** surface: white "Mounting" letters on
  black. The fill's field is **dark red (123,0,0)**, not black.
- `WinTrek/consoledata.cpp:1157-1189` — `PartWrapper::GetReadyState()`: 0 =
  unmounted, **1 = `GetMountedFraction() < 1` (the mounting bar)**, 2 = launcher
  arming, 3 = ready.
- `effect/panes.cpp:31-40` — `GaugePane::Paint` BitBlts the left
  `m_value = int(xsize·v)` **columns** of the fill (a left-to-right reveal, not a
  horizontal squeeze).

## What changed in the port

### Radar command icons (both skins — the radar label path is shared)

- **New** `src/Allegiance.ClientCore.Game/Rendering/RadarCommandIcons.cs` — the
  pure port of `c_cdAllCommands` + the native load range:
  `ArtStem(cid, accepted)` returns `ackillbmp … acbuildbmp` / `qukillbmp …
  qubuildbmp` for ids **1-9** and **null for id 0 and anything outside
  `c_cidMax`**, exactly like the native arrays; `UseIcon(cid, artLoaded)` is the
  fallback rule (no art → keep the `A<id>`/`Q<id>` prefix).
- `HudNativeTextures` decodes the eighteen surfaces once through the existing
  `MdlTextureDecoder` path (`TryDecode`, `flipVertical: false`); the accepted
  ones are shared with the command bar's `VerbIcons` (no second GPU copy) and
  `RadarCommandIcon(cid, accepted)` is the lookup.
- `RadarLabelCache` now caches **two** name lines per contact: the historical
  prefixed line and the bare name+eye+rip line used when the glyph draws (both
  strings built only on a label change, so the per-frame path still allocates
  nothing — the HUD-5b fast path is untouched).
- `HudRenderer.DrawRadarLabelBlock` draws the mapped surface at the label
  block's left column, at native size (16 px × the skin's text scale), tinted
  with `RadarLabelColor(contact)` (the full side/flash colour, not the dimmed
  `colorOther`) and reserves its width in the measured block. Missing art (or
  `c_cidDefault`) leaves the `A<id>`/`Q<id>` prefix exactly as before.
- Artwork missing/undecodable → textures null → the prefix fallback; no new
  failure mode (the loader already logs and degrades).

### Console order glyphs (classic + modern)

- `HudNativeTextures` decodes `comacceptbmp` / `comqueuedbmp` (16×16).
- New `HudConsoleFormat.OrderIconSize/-Left/OrderIconBelowLine` and
  `OrderIconTopLeft(accepted, scale)` — the native arithmetic from
  `dialog.mdl:1605-1652`: x = `OrderTextX - 21` = 36, y = `OrderLabelTop +
  (20|40) + 13 - 16` = 54 (queued) / 74 (accepted), scaled with the skin.
- New `HudRenderer.DrawOrderLineGlyph(accepted, scale, tint)` draws the glyph at
  that rectangle; **classic** tints it `yellowText` (accepted) / red (queued)
  and **modern** uses its own yellow/alert palette. Both renderers call it
  before their existing text row; the text-only line is the fallback when the
  art is missing.

### Mounting-bar track

- `InventoryPaneTextures` decodes `mountingbarbkgndbmp` (64×14) and exposes
  `MountingBarBackground`.
- `HudRenderer.DrawInventoryRow` draws it in the `Point(130,5)` stats slot for
  `ReadyState == 1`, falling back to the old dark rect when the art is missing.
- **Bug fixed on the way:** the fill (`mountingbarbmp`, i.e. native
  `CortUI/classic/mountingbar.png`) was decoded through
  `MdlTextureDecoder.ToPremultipliedRgba`, whose near-black key (luminance ≤ 40)
  erased the fill's **dark red (123,0,0) field (luminance 36)** — only the white
  letters survived, so the "left-to-right reveal" was invisible and the track
  art alone would have shown no progress. The two bar fills now load with
  `LoadMdlOpaque` (raw RGB, alpha 255); the track keeps the keyed decode because
  its surround really is black. The reveal itself now uses the native column
  clip (`srcW = int(width·fraction)` source rect) instead of squashing the whole
  bitmap into `fillW` px.
- The **modern** skin keeps its own modern bar (`ModernHudRenderer`,
  `DrawModernInventoryRow`): the native 64×14 "Mounting" word art is a classic
  CortUI bitmap and does not fit the HUD-2 skin's flat palette/geometry, so it is
  deliberately not drawn there — the modern row still shows the same
  `MountedFraction` progress (`inventory-mounting-track-modern.webp`).

### Crash found by the proof (one-line fix, HUD-9 tip path)

The proof's first run hit a real client crash the moment the queued order
arrived: `NEW COMMAND RECEIVED — [Home] to accept` (and the pickup variant) were
pushed into the ordinary tip slots with an **em dash**, and those slots are
measured directly with `SpriteFont.MeasureString` (the only console row that
skips `HudFormat.Printable`). `System.ArgumentException: Text contains
characters that cannot be resolved by this SpriteFont` aborted the draw.
`Game1.Feedback.cs` `PushTip`/`PushOrdinaryTip` now sanitise the text once at
the slot write (`HudFormat.Printable`), so any server tip is safe in both
skins. Screenshots show the resulting `?` in those announcement lines. This was
required to reach the screenshots and is noted as a blocked-by fix, not a parity
claim.

## Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/Hud15CommandIconTests.cs`
(8 tests):

| Test | Pins |
| --- | --- |
| `Radar_command_icons_map_every_native_command_id_to_its_surface` | `c_cidAttack..c_cidBuild` → the exact `ac*`/`qu*` stems (`Igc/common.cpp:41-52`). |
| `Radar_command_icon_mapping_stops_where_the_native_load_loop_stops` | `c_cidDefault` (0), `c_cidMax` and the NA sentinel have **no** radar icon (`radarimage.cpp:228`). |
| `Radar_command_icon_needs_the_decoded_art_or_the_text_prefix_stays` | `artLoaded: false` (and no command slot / unmapped id) keeps the `A<id>`/`Q<id>` prefix — the fallback. |
| `Radar_label_cache_exposes_the_bare_name_for_the_icon_path` | the cache serves the prefixed **and** bare name line, and reuses the same bare instance across frames (no per-frame string). |
| `Order_line_glyphs_sit_at_the_native_accepted_and_queued_offsets` | `(36,54)` queued / `(36,74)` accepted from `dialog.mdl:1605-1652`, doubled at scale 2. |
| `Every_mapped_command_surface_and_glyph_decodes_at_its_native_size` | real artwork (SkippableFact/`Require.Artwork`): all 18 command surfaces 16×16, `comacceptbmp`/`comqueuedbmp` 16×16, `mountingbarbkgndbmp` 64×14. |
| `Renderers_draw_the_native_glyphs_and_fall_back_to_the_text_lines` | source guard: both skins call `DrawOrderLineGlyph`, the radar block uses `RadarCommandIcons.UseIcon` + `view.IconNameLine`, the loader decodes all three art groups, the inventory uses `MountingBarBackground` with the dark-rect fallback, no `$"` in the icon draw paths. |
| `Console_tip_slots_sanitise_non_ascii_before_the_font_measures_them` | the `PushTip`/`PushOrdinaryTip` sanitisation that unblocks the queued-order announcement. |

## Verification (gate)

- **Whole-solution Release build:** `dotnet build AllegianceCore.slnx -c Release`
  → **0 warnings / 0 errors**.
- **New tests 3× alone:** `Hud15CommandIconTests` **8/8 on each of 3 runs**.
- **Affected families 3× alone** (Hud10/Hud11/Hud12/Hud12b/Hud15/Hud5b/Hud6b/
  inventory/radar-label/radar-bracket tests): **135/135 on each of 3 runs**.
- **Renderer + scorecard 3× alone:** `ModernHudTests` **4/4** and
  `P674ParityScorecardTests` **8/8** on each of 3 runs.
- **ONE fast tier** (`TMPDIR` inside the worktree): **9,467 tests, 273.8 s,
  5 failures** — all `Allegiance.Core.Tests.PlayerBundlePathsTests`, the known
  `local.env` ancestor-discovery baseline documented by HUD-12/SC2 (not new).
  No HUD/radar/inventory test failed. Summary:
  [`HUD15_assets/fast-tier-summary.json`](HUD15_assets/fast-tier-summary.json);
  raw log `~/allegiance-evidence/HUD15/fast-tier.log`.
- One intermediate filtered run showed the **documented allocation-sampling
  flake** `Hud12bReviewFixTests.Chat_line_truncation_matches_the_native_clip_and_is_cached_per_source_and_width`
  (7208 B / 6408 B over 10k frames); it passed **3/3 alone** unchanged and is
  recorded in `docs/TEST_FLAKES.md` (HUD-15 section). No other test failed in
  any run.
- **Not run / not triggered:** `src/Allegiance.Core` and
  `src/Allegiance.Server` are unchanged (`git status` shows only
  `Allegiance.ClientCore.Game`, tests, tools and docs), so the conditional AOT
  publish and the 5× showcase never-parks run are not triggered. No
  rig/mini-runner/221 runs.
- **Scorecard:** `python3 scripts/parity-scorecard.py` validates both CSVs; the
  `hud` area moves **11 → 15 of 108** eligible rows (`13.89%`), four rows
  `partial → done` and two rows keep `partial` with updated notes.

## Matrix changes (`docs/parity/parity-matrix.csv`, only these six lines)

| Row | Before → after | Note now says |
| --- | --- | --- |
| Radar accepted order brackets | partial → **done** | native `m_psurfaceAccepted` drawn 1:1 in the contact colour at the label's left column, `A<id>` prefix gone when the art loads; mapping/fallback pinned; glyph blends through the HUD's alpha batch (native additive) and is centred in the port's measured line. |
| Radar queued order brackets | partial → **done** | same for `m_psurfaceQueued`; `Q<id>` only without art or for `c_cidDefault`. |
| Radar command verb labels | partial → **done** | command-id → icon mapping now drawn from the native surfaces instead of the prefixes; prefixes are the fallback, pinned by the new tests. |
| Accepted order text | partial (unchanged status) | `comacceptbmp` (16×16, `yellowText`) now drawn at the `Point(-21,-13)` LastOrderImage offset; **the row stays partial because the port still substitutes medVerdana** (font out of scope). |
| Queued order text | partial (unchanged status) | `comqueuedbmp` (16×16, red) now drawn at the same offset; still partial for the font. |
| Equipment mounting progress | partial → **done** | real `mountingbarbkgndbmp` 64×14 track + native left-to-right fill reveal, plus the fill-decode fix; x=130 exact, the port keeps its 2 px row inset (3 px lower than native's lower-left `Point(130,5)`). |

## Screenshots (real client, opened and described)

All five were captured by `tools/hud15-proof` — a real `Allegiance.ClientCore.MonoGame`
1920×1080 window against a real local `Allegiance.Server` on `m2scatter.igc`,
through the production autoshot backbuffer path. The orders travel the real
path: `Server.QueueCommandForPlayer` writes the queued slot, the client's bound
ACCEPT key (`Home`) edge sends `ORDER_CHANGE`, the server promotes and
broadcasts the accepted slot. Raw PNGs and both run logs:
`~/allegiance-evidence/HUD15/` (`raw/classic`, `raw/modern`,
`classic-run.log`, `modern-run.log`); hashes/provenance in
[`HUD15_assets/screenshots.json`](HUD15_assets/screenshots.json). The five
screenshots total **336 KB** and the whole asset directory (**352 KB**, including
the fast-tier summary) is well under the 2 MB cap.

| Scenario | Asset |
| --- | --- |
| Both order lines with the native glyphs + both radar command icons (classic) | [`orders-accepted-queued.webp`](HUD15_assets/orders-accepted-queued.webp) |
| Radar accepted-order contact with its native icon (and the queued one) | [`radar-accepted-command-icon.webp`](HUD15_assets/radar-accepted-command-icon.webp) |
| Inventory row mid-mount with the native track | [`inventory-mounting-track.webp`](HUD15_assets/inventory-mounting-track.webp) |
| Modern skin, same order glyphs | [`orders-accepted-queued-modern.webp`](HUD15_assets/orders-accepted-queued-modern.webp) |
| Modern skin inventory (its own bar, no native word art) | [`inventory-mounting-track-modern.webp`](HUD15_assets/inventory-mounting-track-modern.webp) |

What is actually visible in each frame (opened with the read tool and read at
full size, then cropped/zoomed to check the glyphs):

- **orders-accepted-queued** — cockpit at 1080p. Top-left `Current orders:` in
  yellow, the **red chevron** (`comqueuedbmp`) beside the red
  `attack Ship 8099` line and the **yellow checkmark** (`comacceptbmp`) beside
  the grey `goto Ship 8097` line. Radar: `Ship 8097 2870 m` upper-left with the
  native `acgotobmp` glyph (arrow + burst) immediately left of the name, and
  `Ship 8099 1857 m` at the left edge with the `qukillbmp` reticle glyph. The
  selected-target ring is on 8097; the queued-order tip reads
  `NEW COMMAND RECEIVED ? [Home] to accept`.
- **radar-accepted-command-icon** — cockpit with the two ordered contacts moved
  ahead. `×Ship 8097 776 m` upper-right with the `acgotobmp` glyph left of the
  name, the selection ring and its `Ship 8097 / 947 m` label below;
  `Ship 8099 1092 m` mid-left with the `qukillbmp` glyph left of the name. The
  two order lines and their glyphs are still visible top-left.
- **inventory-mounting-track** — the classic flight inventory at the right:
  `Adv Scout` header, ready row `1. ER Nanite 3 400m / -37 X 4`, and
  `9. Retro Booster` with the 64×14 bar showing the native **"Mounting"** word
  art and the **dark-red fill over its left half** (client
  `fraction 120/240 = 0.50`, logged). The row's right side shows the remaining
  black track with the rest of the word.
- **orders-accepted-queued-modern** — the modern skin: the same two glyphs
  (red chevron, yellow check) beside the order lines, and `Ship 8097 2861 m` /
  `Ship 8099 1849 m` radar labels carrying the same `acgotobmp` / `qukillbmp`
  art. So the native glyphs fit the modern skin unchanged.
- **inventory-mounting-track-modern** — the modern skin's `9. Retro Booster` row
  uses its own white 50% bar in a bordered slot (no native word art), which is
  the documented skin decision.

## Remaining differences / unfinished

- **Radar command glyph blending and placement.** Native draws the icon with
  `BlendModeAdd` (`radarimage.cpp:1300,1305`); the port's single HUD batch is
  alpha-blended, so a white mask is tinted rather than added. The port also
  centres the glyph on the name line inside its measured label block instead of
  reproducing native's stacked left column exactly. Art, size (1:1), colour and
  the command-id mapping are native and test-pinned.
- **Order-line glyph placement follows the port's shifted row anchors.** The port
  draws the order text rows at the native *bottom* offsets
  (`OrderQueuedTop/OrderAcceptedTop`), and the glyph is placed with the same
  native arithmetic, so it sits 3 px above the port's (taller, substituted) text
  line rather than exactly 13 px under the native lower-left; the font itself is
  out of scope and keeps both order-text rows `partial`.
- **Modern inventory mounting track** intentionally keeps the HUD-2 modern bar
  (the 64×14 classic word art does not fit that skin); the same
  `MountedFraction` is shown.
- **Launcher arming** (`ReadyState 2`) still has no native `armingbarbkgndbmp`
  art in the port; that row stays `partial` for its own reason (the client has no
  `GetArmedFraction`), unchanged by this wave.
- **Bracket radii** (HUD-13) and the gauge fonts (out of scope) are untouched.

## Overlap with HUD-13

`pi/hud13` is not merged into this base. This wave does not touch
`RadarBracketRenderer`, bracket sizing, gauge layout or fonts. The shared-file
edits are additive: a new protected helper + two call sites in
`HudRenderer.DrawConsoleText`, one glyph branch in `DrawRadarLabelBlock` (the
`nameLine`/`lineCount`/`blockWidth` locals are the only changed lines there),
one track branch in `DrawInventoryRow`, and the same two icon calls in
`ModernHudRenderer.DrawConsoleText`. `HudConsoleText.cs` only gains constants.
