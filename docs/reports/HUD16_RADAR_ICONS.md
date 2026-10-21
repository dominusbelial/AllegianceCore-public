# HUD-16 — native radar contact icons (ship, station, warp, rock, missile, probe, mine, treasure, tech)

Base: master `e96b9943` (worktree `pi/hud16`, uncommitted working-tree changes).
Model: DeepSeek V4.1 Flash, vision on.

Native citations are the checked-out tree at `~/Development/Allegiance/src/`; artwork
citations are `~/Development/Artwork/`, decoded with the port's own `MdlTextureDecoder`.

## What the native rule is

**Icon surface.** Every model's radar icon is a surface loaded once at model creation
from its IGC `iconName` plus `"bmp"` (`WinTrek/trekigc.cpp:2731-2744`):

```
IObject* WinTrekClient::LoadRadarIcon(const char* szName)
{
    if (szName && (szName[0] != '\0'))                       // :2735
        psurface = GetModeler()->LoadSurface(ZString(szName) + "bmp", true);
    else
        psurface = NULL;                                     // empty name = NO icon
    return psurface;
}
```

It is called from the generic model loader (`Igc/modelIGC.cpp:118`, via `LoadCVH`
`:127-136`, `LoadWarp` `:134-139`, `LoadMine` `:141-146`, `LoadDecal` `:276-279`) and
from the hull type (`Igc/hullTypeIGC.cpp:51`, used by every ship through
`Igc/shipIGC.cpp:4732`). Per-kind sources of the icon name:

| Contact | Icon name comes from | Native cite |
| --- | --- | --- |
| ship | hull type `iconName` | `Igc/hullTypeIGC.cpp:51` |
| station | station type `iconName` | `Igc/stationIGC.cpp:240-244` → `GetIconName()` |
| asteroid | `AsteroidDef.iconName` | `Igc/asteroidigc.cpp:35`, table `:126-137` |
| warp (aleph) | `warpDef.iconName` | `Igc/warpIGC.cpp:34` → `LoadWarp` |
| probe | `dataProbeType->iconName` | `Igc/probeigc.cpp:114` |
| mine | `dataMineType->iconName` | `Igc/mineigc.cpp:103` |
| missile | `dataMissileType->iconName` | `Igc/missileigc.cpp:50,60` |
| treasure | buyable's `GetIconName()`, else `cashicon`/`iconpu`/`flag`/`articon` | `Igc/treasureIGC.cpp:118-155` |
| treasure *useful tech* | **`icontechbmp`** override | `WinTrek/radarimage.cpp:224,719,890` |

**Colour.** `colorIcon` is the object's side colour, red while the damage-bucket flash
is lit, and the neutral constant (white) when the object has no side
(`radarimage.cpp:678-685`, neutral at `:1471`). The icon itself is never dimmed — the
`colorOther` brightness step (`:1094-1096`) applies to brackets and labels only.

**Placement and blend** (`radarimage.cpp:320-427`): after the bracket passes,
`DrawBlip` computes

```
directionCenter = normalize((0, height/5) - positionObject)      // :382-401
if (ucRadarState != c_ucRadarOnScreenLarge) positionIcon = (0,0)  // :403-406
else positionIcon = directionCenter * min(distanceToEdge, radiusBracket)  // :407-409
...
pcontext->Translate(positionIcon);
pcontext->SetBlendMode(BlendModeAdd);                             // :418
pcontext->DrawImage3D(psurfaceIcon, colorIcon, true);             // :419
```

so the surface is drawn **1:1, centred, additively**, at the blip itself for
small/off-screen states, and pulled inward by up to the bracket radius for the
on-screen-large state (native's comment: to avoid obscuring the ship).

> **Brief wording note.** `HUD16_BRIEF.md` says "off-screen-large state puts the icon
> at `directionCenter * min(...)`". The checked-out native code does the opposite:
> the offset exists only for `c_ucRadarOnScreenLarge`, and *every other state*
> (including off-screen) draws at `(0,0)` (`radarimage.cpp:403-409`). The brief also
> says "port that rule as written", so the port follows the code — and the test
> pins the code, not the sentence.

## What changed (port)

New, pure/data files (no graphics device on their surfaces):

| File | Contents |
| --- | --- |
| `src/Allegiance.ClientCore.Game/Rendering/RadarIconNames.cs` | `RadarIconNameCache`: builds the type-id → surface-stem tables **once** from the static core (`ShopCatalog` + the client's `_hullById`), maps `iconName → iconName+"bmp"` (`SurfaceStem`, the native loader), and returns `icontechbmp` for a useful-tech treasure. Per-instance names the wire already carries (`DataWarpIGC.IconName`, `DataAsteroidIGC.IconName`) and treasure resolution are memoised, so the per-frame path is dictionary reads only. |
| `src/Allegiance.ClientCore.Game/Rendering/RadarIconPlacement.cs` | `Offset(onScreenLarge, …)` = the `radarimage.cpp:382-409` rule: zero for every non-on-screen-large state, else `directionCenter * min(distanceToEdge, radiusBracket)` with the native `(0, height/5)` reference point, the `< 1 px` → `(0,-1)` degenerate, and the `-1/|d|` unit vector. |
| `src/Allegiance.ClientCore.Game/Rendering/RadarIconTextures.cs` | One `Texture2D` per stem, decoded once through `MdlTextureDecoder` (plus the `ImportImageFromFile` script form), preloaded at startup from every surface the core's type tables reference. **Blank art** (every pixel colour-keyed to alpha 0 — HUD-13's `enemybmp` finding) is treated as absent; `IsBlank` is the shared pure rule. Native's Windows case-insensitive stem lookup is reproduced with a lazy lower-cased file index (this checkout ships `Alephbmp.mdl` for the stem `aleph`). |

Modified:

- `Rendering/RadarBracketRenderer.cs` — a second, **additive** `SpriteBatch` pass
  (`BlendState.Additive`, `:481-556`) draws each contact's icon after the alpha
  bracket pass, exactly like native draws the icon last: 1:1, centred on
  `ScreenPoint + RadarIconPlacement.Offset(...)`, tinted by
  `NativeIconColour(sideColour, threatFlash)` (`:200`, side colour / red / white).
  The procedural shape is now the **fallback only**: `DrawContact` skips it when
  `_icons.Peek(c.IconStem)` resolved (`:399-400`).
- `Rendering/HudModel.cs` — `HudContact.IconStem` (last positional field, default
  null; no allocation, it is a string reference already owned by the core/wire).
  `Rendering/RadarBracketRenderer.RadarContact.IconStem` mirrors it for the direct
  API and is mapped on the stack.
- `Game1.Rendering.cs` — the radar gather fills `IconStem` for **every** kind:
  own ship and ships `Ship(hullId)` (`:3459`, `:3550`), stations
  `Station(StationTypeId)` (`:3595`), warps `Warp(wp.IconName)` (`:3624`), missiles
  `Missile(ms.TypeId)` (`:3659`), probes `Probe(ProbetypeId)` (`:3684`), mines
  `Mine(MinetypeId)` (`:3706`), treasures `Treasure(code, payloadId, isTech)`
  (`:3770`), asteroids `Asteroid(ast.IconName)` (`:3810`).
- `Game1.Lifecycle.cs` — builds the name cache from `_shopCatalog`/`_hullById` and
  preloads the surfaces once (`hud-16 radar icons: 117 surfaces, 0 blank, 0 missing`
  on this checkout), then attaches them to both radar renderers
  (`_radarRenderer`, `_modernHudRenderer`). `Game1.cs` disposes them on unload.
- `Rendering/ModernHudRenderer.cs` — `SetRadarIcons` forwarding to its own
  `RadarBracketRenderer` (the modern skin has a separate instance).
- `tools/hud16-proof/` — the real-client screenshot driver (see below).

Not changed: any label/stat tier, bracket sizing (HUD-13), command icons (HUD-15),
ore fills, eye icons, or anything server/sim.

## Blank-art findings

`RadarIconTextures` logs every surface it loads. The startup preload — every surface
the core's type tables reference (all hulls, stations, missiles, probes, mines, the
fixed treasure icons and `icontechbmp`) — is **117 surfaces, 0 blank, 0 missing**
(classic and modern run logs). The per-instance icons the wire names (the nine
asteroid `AsteroidDef.iconName`s and the map's `warpDef.iconName`, e.g. `aleph`)
load lazily on their first contact; the artwork test below decodes every one of
them too, non-blank:

```
[monogame] hud-16 radar icons: 117 surfaces, 0 blank, 0 missing
```

The `Requires=Artwork` test independently decodes **every** stem the core can
resolve (all hulls, stations, missiles, probes, mines, the nine asteroid rows, the
four fixed treasure icons, `icontechbmp`, and the `m2scatter.igc` warp icons) and
asserts each is non-blank: all pass. Two size facts were recorded rather than
assumed:

- The contact glyphs are 16×16 **except** the two `dericon` drone hulls
  (`DataHullTypeIGC` 800/801), whose native surface is 8×8. The port draws 1:1, so
  those two are 8×8 — the test pins "8 or 16 px square", not "16".
- Most part/development `IconName`s in `PCore001.igc` are empty. That is the native
  behaviour (empty name → NULL surface), so those treasures resolve to no icon and
  keep the port's procedural crate; only `ammo_icon`, `fuel_icon` and the fixed
  cash/powerup/flag/artifact icons exist as radar art.

No art was invented: whichever native surface exists is what is drawn, and the
procedural shape remains only where the native surface is absent, blank or
undecodable (exactly native's `if (psurfaceIcon)` gate at `radarimage.cpp:591-593`).

## Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Hud16RadarIconTests.cs` (13 tests):

| Test | Native rule pinned |
| --- | --- |
| `Icon_name_becomes_the_bmp_surface_stem_and_an_empty_name_means_no_icon` | `trekigc.cpp:2735-2742` |
| `Ship_icon_comes_from_its_hull_type` | `hullTypeIGC.cpp:51`; empty hull icon → none |
| `Station_missile_probe_and_mine_icons_come_from_their_type_tables` | `stationIGC.cpp:240-244`, `missileigc.cpp:50,60`, `probeigc.cpp:114`, `mineigc.cpp:103` |
| `Asteroid_and_warp_icons_come_from_the_instances_wire_icon_name` | `asteroidigc.cpp:35`, `warpIGC.cpp:34` |
| `Treasure_uses_its_own_icon_and_tech_overrides_with_icontechbmp` | `treasureIGC.cpp:118-155`, `radarimage.cpp:224,719,890` |
| `On_screen_icon_is_pulled_inward_by_the_clamped_bracket_radius` | `radarimage.cpp:382-409` (direction, `min(distanceToEdge, radiusBracket)`) |
| `Every_state_other_than_on_screen_large_draws_the_icon_at_the_blip` | `radarimage.cpp:403-406` |
| `Dead_centre_blip_points_the_icon_straight_up` | `radarimage.cpp:392-396` |
| `Icon_colour_is_the_side_colour_red_while_flashing_and_white_when_neutral` | `radarimage.cpp:678-685,1471` |
| `Per_frame_icon_name_resolution_allocates_nothing` | 1000 frames after warm-up, `GC.GetAllocatedBytesForCurrentThread()` delta **0 B** |
| `Every_core_contact_icon_resolves_to_a_real_non_blank_surface` (Requires=Artwork) | real artwork decode, per-stem non-blank + size |
| `The_gather_resolves_a_native_icon_stem_for_every_contact_kind` | production gather wiring (all nine `IconStem:` sites) |
| `The_renderer_draws_the_native_icon_additively_at_the_native_position` | production renderer wiring (additive pass, placement call, tint, fallback gate, 1:1 centred draw, loader/preload, blank detection) |

**How the "fails on today's code at runtime" rule was checked.** The wiring guards are
plain source-text assertions, so they compile against master. I backed up the six
modified production files, ran `git checkout HEAD --` on them (leaving the new
`RadarIcon*.cs` and the test file in place), and ran just those two tests against the
stale build: both failed **at runtime**, not at compile time —

```
Failed The_renderer_draws_the_native_icon_additively_at_the_native_position
  Assert.Contains() Failure: Sub-string not found
  Not found: "private void DrawNativeIcons(IReadOnlyLis"…
Failed The_gather_resolves_a_native_icon_stem_for_every_contact_kind
  Assert.Contains() Failure: Sub-string not found
  Not found: "var iconNames = _radarIconNames;"
```

then restored the files (build 0/0). The behavioural tests (name resolution,
placement, colour, allocation, artwork) call the new pure API, so on master they
fail at compile time by construction; each of those rules is *additionally* pinned by
the runtime-failing wiring guards above, which is the closest possible equivalent.

Runs: `Hud16RadarIconTests` **3× alone** — 13/13 passed each time. Fast tier
(`tools/run-tests.sh --tier fast`): **9591 tests, PASS** (273.6 s). Whole-solution
build: **0 warnings, 0 errors**. No `src/Allegiance.Server` or `src/Allegiance.Core`
change, so the AOT publish gate does not apply.

Allocation test (from the run): `HUD16 resolution allocation over 1000 frames: 0 B`.

## Screenshots (real client, 1920×1080, both skins)

Captured by `tools/hud16-proof` — the real `Game1` over TCP against a real local
`Allegiance.Server` (`m2scatter.igc`, 2 sides), through the production autoshot
backbuffer path. The proof freezes the client's own render pose and then moves the
**contacts** in front of it (the client's predicted own pose rejects teleports):
two real NPC ships spawned via `Server.SpawnNpcShip` (±90 m lateral at 195 m), a
real `OT_asteroid` export built from `NativeAsteroidTypes`, a real `OT_station` and
`OT_warp` re-exported at moved positions, a real `Probe` + `EXPORT(26)/OT_probe`, a
real `Missile` + `FIRE_MISSILE` (aimed at the own ship so Default LOD keeps it,
`radarimage.cpp:819-832`), and a real cash treasure + `TREASURE(28)`. The production
`ALLEGIANCE_RADAR_PROOF` path is not needed: the proof logs each contact's
`RadarProjection` pixel and crops around it.

Evidence in this repo: [`HUD16_assets/`](HUD16_assets/). Raw PNG frames, per-frame
logs, the icon-art dumps and the fast-tier logs are in
`~/allegiance-evidence/HUD16/` (`raw-classic/`, `raw-modern/`, `hud16-classic.log`,
`hud16-modern.log`, `fast-tier.log`).

### Classic skin

- **[`radar-icons-ships-classic-1920x1080.jpg`](HUD16_assets/radar-icons-ships-classic-1920x1080.jpg)** —
  opened and read: cockpit view. Upper-left, **Ship 8097** (enemy, 195 m, blue label)
  with the red nearest-enemy diamond (HUD-13 fallback) and the **cyan native miner
  hull icon** (`dn_himnradv`) drawn ~100 px inward from the blip; upper-right **Ship
  8098** (friendly, 195 m, yellow label) with the **same icon tinted gold** by the
  friendly side colour. Lower-left shows the `244 m` rock label and the small
  **brown-grey `meteoricon` rock glyph** to its lower right (the asteroid contact).
  Centre gauges, chat, starmap pane and speed readout are visible.
- **[`zoom-enemy-ship-classic.png`](HUD16_assets/zoom-enemy-ship-classic.png)** /
  **[`zoom-friendly-ship-classic.png`](HUD16_assets/zoom-friendly-ship-classic.png)** —
  4× crops: the magenta-red diamond with the blue label in one, and the identical
  16×16 miner glyph in cyan (enemy) versus gold (friendly) in both.
- **[`zoom-probe-classic.png`](HUD16_assets/zoom-probe-classic.png)** — the probe
  contact at screen centre: a small **gold/white antenna-like glyph** (the native
  `probebmp`) below the shield gauge, plus the darker probe 3-D model above it.
- **[`zoom-missile-classic.png`](HUD16_assets/zoom-missile-classic.png)** — the
  missile contact: the native **`misslebmp` missile glyph**, a horizontal
  dart shape tinted gold (own-side launcher), next to the friendly ship's chevron.
- **[`zoom-treasure-classic.png`](HUD16_assets/zoom-treasure-classic.png)** — the cash
  treasure: the native **`cashicon` "$" glyph** drawn over its `293 m` range label
  (the icon sits on the blip, the label block overlaps it at this range).
- **[`zoom-station-classic.png`](HUD16_assets/zoom-station-classic.png)** — the
  `354 m` station contact with its native **`dn_hsgar` garrison star glyph** in gold
  at the computed `directionCenter * min(distanceToEdge, radiusBracket)` offset.
- **[`zoom-aleph-classic.png`](HUD16_assets/zoom-aleph-classic.png)** — the aleph
  contact labelled `Tjeerd 333 m` with its native `alephbmp` glyph at the blip.
  **[`radar-icons-aleph-classic-1920x1080.jpg`](HUD16_assets/radar-icons-aleph-classic-1920x1080.jpg)**
  is the full frame of the same shot.
- **[`zoom-asteroid-classic.png`](HUD16_assets/zoom-asteroid-classic.png)** — the rock
  contact's `244 m` label; the brown `meteoricon` glyph is at the predicted
  `(847,665)` pixel (confirmed by pixel sampling: non-black pixels 844-848 × 662-663).

### Modern skin

- **[`radar-icons-ships-modern-1920x1080.jpg`](HUD16_assets/radar-icons-ships-modern-1920x1080.jpg)** —
  opened and read: the same three contacts against the modern circular percentage
  gauges: Ship 8097's red diamond + **cyan miner icon**, Ship 8098's **gold miner
  icon**, the `244 m` asteroid label with the small brown rock glyph, the `RADAR
  Default` readout bottom-left.
- **[`radar-icons-missile-modern-1920x1080.jpg`](HUD16_assets/radar-icons-missile-modern-1920x1080.jpg)**
  and **[`zoom-missile-modern.png`](HUD16_assets/zoom-missile-modern.png)** — the
  gold native missile glyph beside the friendly chevron.
- **[`zoom-probe-modern.png`](HUD16_assets/zoom-probe-modern.png)** — the gold probe
  glyph under the modern gauges (and the asteroid rock at bottom-left).
- **[`zoom-station-modern.png`](HUD16_assets/zoom-station-modern.png)** — the gold
  garrison star at the station contact, with the `354 m` label.
- **[`zoom-asteroid-modern.png`](HUD16_assets/zoom-asteroid-modern.png)**,
  **[`zoom-enemy-ship-modern.png`](HUD16_assets/zoom-enemy-ship-modern.png)**,
  **[`zoom-friendly-ship-modern.png`](HUD16_assets/zoom-friendly-ship-modern.png)** —
  the same icons under the modern skin.

The icons are 16×16 native pixels at 1:1 (native `DrawImage3D(psurface, color, true)`),
so they read as small glyphs in the full frames; the crops above are the proof of
which surface is on screen, and the `art-*.png` dumps in
`~/allegiance-evidence/HUD16/raw-*/` show the decoded native surfaces side by side.

## Parity matrix

Only the seven rows named in the brief were edited (as text, LF kept, exactly six
columns). All seven now record the native icon art and stay `partial` for their
pre-existing non-icon gaps (label/stat tiers, ore fills, aim tiers, per-contact
stats, the live dev-tech split); no row was promoted to `done` and none was marked
`n/a`.

## Unfinished / known differences

- Label/stat tiers, ore/miner fills, eye icons, the nearest-enemy bracket art
  (HUD-13), command icons (HUD-15) and the eye-space clamp remain as separate rows.
- The device-dependent additive pass is only covered by the real-client screenshots;
  unit tests cover the mapping, placement, colour and allocation rules (the
  `Texture2D` path needs a graphics device).
- Native interleaves each contact's icon with its own brackets
  (`radarimage.cpp:320-420`); the port groups all brackets (alpha pass) then all
  icons (additive pass). Only the overlap order between *different* contacts'
  brackets can differ, and only where they overlap.

## Claude review fixups

- **Placement Y axis was flipped.** Native image space is y-up (`engine/camera.cpp:174` maps eye Y straight to image Y), so the reference point `positionObject.Y - H/5` (`radarimage.cpp:363-365`) sits H/5 *above* the centre, and the dead-centre direction `(0,-1)` is straight *down* on screen. The WIP put the reference below centre. `RadarIconPlacement.Offset` now uses `y + H/5`; tests pin a blip between the reference and the centre moving up and the dead-centre case pointing down.
- **Round radar mode** now uses the image centre as the reference (no H/5 shift), per `radarimage.cpp:363-365`; tested.
- **Opacity was applied twice** (tint alpha × the Additive blend's source alpha); it now scales RGB only.
- **Screenshot claims corrected** (the screenshots were taken before the placement fix, so icon positions in them are the old, wrong ones):
  - `zoom-enemy-ship-classic.png` shows the red diamond, the label and the P372 side tag `^`; the cyan hull glyph is outside that crop.
  - The yellow glyph on the aleph label is Ship 8098's miner icon, not the aleph icon; the aleph's own icon is not identified in these shots.
  - The asteroid icon is not proven by these screenshots (only a 5×2 px pixel sample).
  - Visible and identified: the cyan ship glyph next to the enemy ship, the gold one next to the friendly ship, the station star, the missile dart, the treasure `$`.
