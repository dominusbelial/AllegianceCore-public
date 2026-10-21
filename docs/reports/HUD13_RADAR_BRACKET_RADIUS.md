# HUD-13 — radar brackets sized from each contact's projected screen radius

Base: master at dispatch `193e8ea3` (`pi/hud13` worktree). Model: DeepSeek V4.1 Flash
(pi lane, vision on). No commits or pushes — every change is in the working tree.

Native citations are the checked-out tree at `~/Development/Allegiance/src/`; artwork
citations are `~/Development/Artwork/`, decoded with the port's own
`MdlTextureDecoder`.

## What the native rule is

`RadarImageImpl::DrawBlip` sizes every bracket per contact, every frame
(`WinTrek/radarimage.cpp:307-319`):

```
float radiusObject = pts->GetScreenRadius();
{   static const float maxRadius = sqrt2 * 80.0f;
    if (radiusObject > maxRadius) radiusObject = maxRadius; }     // :307-312
float radiusBracket = 1.05f * radiusObject;                       // :314
float distanceToEdge = pts->GetDistanceToEdge();                  // :315
if (radiusBracket > distanceToEdge + c_flBorderSide)              // :316-319
    radiusBracket = distanceToEdge + c_flBorderSide;
```

`c_flBorderSide = 10.0f` (`WinTrek/radarimage.h:20`). The radius itself comes from
the ThingSite the frame loop fills (`WinTrek/WinTrek.cpp:7928-7941`):
`fScreenRadius = angleToPixels * asin(radius / distance)` with
`angleToPixels = rectImage.Width / FOV` (`WinTrek.cpp:7712`), floored at
`CmdViewMaxIconScreenSize` = 7 px (`WinTrek/cmdview.h:56`); an off-screen blip is
forced to that 7 px and `distanceToEdge = 0` (`WinTrek.cpp:7876-7878`), while an
on-screen blip's `distanceToEdge` is the distance from the placed point to the
nearest image-rect edge (`WinTrek.cpp:7944-7956`).

Every surface is then drawn with that one radius through `DrawExpandedBlip`
(`radarimage.cpp:255-277`): nearest enemy `m_psurfaceEnemy` `enemybmp`
(`:334`, `artwork.h:68`), subjects (`:340-342`), flag (`:343-345`), artifact
(`:346-347`), ripcord with `colorIcon` (`:349-350`), the command/selection target
`m_psurfaceTargeted` `targetedbmp` (`:352`, `artwork.h:70`) and last-fired
(`:356-357`).

## What changed

**Pure sizing rule — `src/Allegiance.ClientCore.Game/Rendering/RadarScreenRadius.cs` (new).**
`Project(worldRadius, distanceToEye, viewportHeight, projectionM22)` reproduces the
native angular radius (`viewportHeight/2 * |M22| / distance` is the same
px-per-radian scale as `width/fov`), applies native's 7 px floor
(`WinTrek.cpp:7931-7941`) and its `sqrt2*80` cap; `DistanceToEdge` is native's
min-distance-to-edge (`WinTrek.cpp:7944-7956`); `ForContact(onScreen, …)` returns
both, forcing the 7 px icon radius for an edge-clamped blip exactly like
`WinTrek.cpp:7876-7878`. All pure, allocation-free, headless-testable.

**Renderer — `Rendering/RadarBracketRenderer.cs`.** New
`BracketRadius(screenRadius, distanceToEdge)` (`:193`) is the `DrawBlip` formula
above (cap → ×1.05 → edge clip, reusing `LockBracketStyle.BorderSide` = the native
`c_flBorderSide`). `BracketRadiusFor(RadarContact)` (`:207`) feeds it from the new
per-contact fields, falling back to the pre-HUD-13 15 px only for a bare contact.
`DrawOverlayBrackets` (`:446`) now computes one `bracketRadius` per contact and
passes it to every surface — the old `const float bracketRadius = 15f` is gone,
and the procedural fallbacks (open corners, pennant, ripcord ring, threat ticks)
are scaled to the same radius. The nearest-enemy mask now draws the real
`enemybmp` through `DrawExpandedBlip` (procedural diamond only when the art is
absent, `:479`); the selected target draws `targetedbmp` the same way (`:464`),
with the P436 procedural highlight remaining as the no-art fallback (the art is
what native draws, so with the art present it replaces the invention).

**Snapshot — `Rendering/HudModel.cs`, `Game1.Rendering.cs`, `Game1.Camera.cs`.**
`HudContact` carries `float? ScreenRadius, float? DistanceToEdge` (defaults keep
old callers working; no allocation — two floats inside the reused struct).
`Game1.DrawRadar` fills them for *every* contact kind through one local
`Bracketing(site, objectRadius, worldPos)` helper (`Game1.Rendering.cs:3362`) built
on the same projection the selected-target/missile-lock bracket uses:
`Game1.ProjectedRadius` (`Game1.Camera.cs:221`) now delegates to
`RadarScreenRadius.Project`, so the two can never drift apart. Object radii are
the same world radii the LOD code already resolves (hull length/2, station type
radius, warp radius, probe/mine radius, asteroid radius); missiles and treasures
have no client-side model radius on the wire, so they take native's minimum
(unused: neither carries an overlay bracket).

**Art — `Rendering/StatusTextures.cs`.** `RadarEnemy` (`enemybmp`,
`artwork.h:68`) and `RadarTargeted` (`targetedbmp`, `artwork.h:70`) are decoded
through the same HUD-12 `.mdl` path and handed to the renderer by the existing
`SetNativeArt`. The artwork tree ships the file as `Targetedbmp.mdl` (capital T;
native's stem is lowercase and Windows loaded it case-insensitively), so the
loader tries `targetedbmp` then `Targetedbmp`.

**One asset finding.** `~/Development/Artwork/enemybmp.mdl` decodes to a 32×32
surface whose every pixel is `0x0000` — it is entirely black, so the colour-keyed
premultiply makes it fully transparent (verified by decoding the file with
`MdlTextureDecoder`: `maxLum=0`, 1024/1024 pixels zero; `Targetedbmp.mdl` and
`hasflagbmp.mdl` in the same tree decode with real content). Drawing it would
silently show nothing, so `StatusTextures.IsFullyTransparent` (`:129`) now treats
an all-alpha-0 surface as missing art and the renderer keeps its procedural
diamond fallback — the same visual result as a missing file, and the reason the
nearest-enemy bracket in the screenshots is the red diamond rather than the
native star. The pixel trace is the honest answer here; nothing else in the tree
has an `enemybmp` copy (`find ~/Development -name 'enemybmp.mdl'` → one file).

## Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Hud13RadarBracketTests.cs` (8 tests,
all pure/headless):

| Test | Pins |
| --- | --- |
| `Bracket_radius_is_1_05_times_the_projected_screen_radius` | `1.05 × radiusObject` (`radarimage.cpp:314`) |
| `Bracket_radius_caps_the_object_radius_at_sqrt2_times_80` | the cap (`:307-312`) |
| `Bracket_radius_clips_to_the_edge_plus_the_native_border_side` | `distanceToEdge + c_flBorderSide` clip (`:316-319`, `radarimage.h:20`) |
| `Contact_bracket_uses_the_projection_and_keeps_an_edge_contact_on_screen` | near-large > far-small from the projection; 7 px floor/cap; an off-screen contact takes 7 px + its clamped edge distance so its bracket stays inside the viewport |
| `Contact_without_a_projection_keeps_the_legacy_fixed_radius` | only a bare contact falls back to 15 px |
| `Per_frame_contact_bracket_fill_does_not_allocate` | 4 vs 4000 contacts per frame stay flat (HUD-11/12 pattern) |
| `A_fully_transparent_decoded_surface_counts_as_missing_art` | the `enemybmp` rule |
| `Production_fills_each_contacts_native_bracket_inputs` | source guard: `DrawRadar` calls `RadarScreenRadius.ForContact`, every kind carries the two fields, the renderer has no `const float bracketRadius = 15f`, `DrawExpandedBlip` is never fed 15, and both new art names are loaded |

Gate (all on the final tree):

- `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
- Affected tests 3× alone (`--filter "FullyQualifiedName~Hud|FullyQualifiedName~Radar"`):
  **313 passed / 0 failed**, three runs identical (`Hud13…` is 8 of them).
- AOT publish: **not run** — neither `src/Allegiance.Server` nor `src/Allegiance.Core`
  was touched (light-gate rule).
- Fast tier, final tree: **PASS, 9467 tests, 272.2 s**
  (`tools/run-tests.sh --tier fast`). An earlier final-tree run failed only
  `Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`
  (expected 0, observed 6672) — the documented load flake
  (`docs/TEST_FLAKES.md:211`); it passed **3/3 alone**, and the next whole fast-tier
  run was clean.
- `python3 scripts/parity-scorecard.py`: `parity-matrix.csv: validated 665 rows`,
  every row 6 columns; `hud` **16/108** complete (5 rows flipped; the 2 `n/a` rows
  were not touched). `git diff --numstat` on the CSV shows exactly 7 changed lines.

Evidence: [`HUD13_assets/verification.json`](HUD13_assets/verification.json).
Raw logs + source PNGs: `~/allegiance-evidence/HUD13/` (`raw/`,
`hud13-classic-run.log`, `hud13-modern-run.log`, `fast-tier*.log`).

## Screenshots (real client, 1920×1080)

Captured by `tools/hud13-proof` — the real `Game1` over TCP against a real local
`Allegiance.Server` on `m2scatter.igc` (2 sides), through the production autoshot
backbuffer path. The proof spawns three enemy miners 210 m / 928 m / 2145 m ahead
of the player, broadcasts a real `GAIN_FLAG` to the near and far ones (so the
*same* `hasflagbmp` surface is drawn at two projected radii) and sets the real
`_autoTarget` to the middle one (native `targetedbmp`). The production
`ALLEGIANCE_RADAR_PROOF=1` line on that frame reads:

```
contacts=7 ships=3 enemies=3 nearestEnemy=8097 rangeLabels=True
  Ship 8097 @196,348 on nearest  range=212 m  screenRadius=108.5 distanceToEdge=195.64
  Ship 8098 @1234,422 on          range=929 m  screenRadius=23.69 distanceToEdge=422.48
  Ship 8099 @749,423 on           range=2144 m screenRadius=10.17 distanceToEdge=423.41
```

so the three `radiusBracket` values are 113.9 / 24.9 / 10.7 px (none clipped:
each is well inside `distanceToEdge + 10`).

- **[`radar-bracket-radius-classic.webp`](HUD13_assets/radar-bracket-radius-classic.webp)** —
  classic skin, opened and read: cockpit view; **Ship 8097 (212 m)** upper-left is
  enclosed by a **large red diamond bracket** (half-diagonal ≈ 114 px; this is the
  nearest-enemy fallback, because `enemybmp.mdl` is the all-black surface described
  above) with **four blue-tinted `hasflagbmp` quadrant glyphs** spread ≈ 80 px from
  the contact; **Ship 8098 (929 m)** on the right shows the **native `targetedbmp`
  bracket** as a bright blue square frame around the blue contact square, with its
  white name/range label and shield/hull bars; **Ship 8099 (2144 m)** in the upper
  middle shows the *same* flag quadrants collapsed into a tiny cluster ~16 px from
  the contact. Centre gauges, the bottom target pane (`Ship 8098: Adv Miner … RANGE:
  928`) and `SPEED: 0` are visible.
- **[`radar-bracket-radius-modern.webp`](HUD13_assets/radar-bracket-radius-modern.webp)** —
  modern skin, opened and read: the same three contacts and the same three bracket
  sizes (large red diamond + blue flag corners on 8097, `targetedbmp` frame on
  8098 with the modern `Ship 8098 928 m HULL 100% SHLD 0%` readout, tiny flag
  cluster on 8099) against the modern circular percentage gauges.

Both are ≤ 68 KB; the pair is **131 KB**, well under the 2 MB evidence cap.
Provenance/hashes: [`HUD13_assets/screenshots.json`](HUD13_assets/screenshots.json).

## Parity matrix

`docs/parity/parity-matrix.csv`, 7 lines changed, LF, 6 columns throughout.

| Row | Before → after | Why |
| --- | --- | --- |
| Radar nearest enemy bracket | partial → **done** | native `enemybmp` loaded and drawn with `DrawExpandedBlip` at the contact's `radiusBracket`; fallback diamond at the same radius (this tree's art is blank), pinned by tests |
| Radar threat damage flash | partial → **done** | last gap was the fixed 15 px; `lastfiredbmp` now drawn at the native radius (data/flash rule unchanged, HUD-12 tests still pin it) |
| Radar carried flag marker | partial → **done** | `hasflagbmp` quadrant radius now native-sized; visible at two sizes in the screenshots |
| Radar carried artifact marker | partial → **done** | same `DrawExpandedBlip` sizing path as the flag row |
| Radar ripcord marker | partial → **done** | `ripiconbmp` drawn at the native radius (still `colorIcon`) |
| Radar command subjects | partial → **partial** (note updated) | radius gap closed; the row also names a missing data path (the port composer has no individual/group recipient selection), so it stays partial per the brief |
| Selected target bracket | partial → **partial** (note updated) | the radar contact now draws the real `targetedbmp` at the projected radius; the row's remaining gap is the port's 3D-view selection box (a port extension native has no equivalent of) rather than the radius |

`hud` scorecard: **16/108** (11 `done` + 5 flipped; 2 `n/a` rows untouched).

## Unfinished / remaining gaps

- **`enemybmp.mdl` in this artwork tree is blank** (all-black 32×32). The port
  loads it exactly as native does and the renderer falls back to the diamond when
  the decoded surface has no visible pixels; a good artwork copy would light the
  native star up with no code change. Worth reporting upstream of the artwork tree
  rather than "fixing" in code.
- **Command subjects** still cannot show individual/group recipients: the port
  composer has no such recipient lists (documented, out of HUD-13 scope).
- **Selected target**: the 3D-view selection box (`HudRenderer.DrawTargetSelectionBracket`)
  remains a port extension with its own thickness; native only draws the targeted
  blip. With `targetedbmp` present the P436 procedural pulsing highlight is now
  replaced by the native surface (deliberate: native fidelity over the port
  invention; the fallback still pulses when the art is missing).
- `Highlight` (native `c_maskHighlight`, the flag-treasure mask) still draws the
  port's white ring at a fixed 17 px: native sets that mask but never draws a
  bracket for it (`c_maskHighlight` appears only in the label/brightness tests,
  `radarimage.cpp:639,707,1094`), so the port ring is its own visual and HUD-13
  left it alone rather than inventing a size rule.

## Scratch

Work happened in `.scratch/` inside the worktree (git-excluded via
`.git/info/exclude`); it is deleted at the end. The report links nothing from it —
raw frame PNGs/logs live in `~/allegiance-evidence/HUD13/`, and the committed
assets are the two webp files plus `screenshots.json`/`verification.json`.
