# P460 — sector map shows who owns each sector, and your probes

## Why this task exists

From the 2026-09-29 gameplay-parity pass. The commander's main situational
tool in the original is the sector map. The port's starmap
(`src/Allegiance.ClientCore.MonoGame/Rendering/StarmapRenderer.cs`)
draws sectors, warp edges, the current-sector highlight and the P374
threat icons. Its own header still lists "station/asteroid icons,
ownership coloring … later-scope follow-ups" (`StarmapRenderer.cs:17-19`).

**Native, what is actually live** (checked against the source; several
features are commented out):

- **Sector ownership rings.** `GetClusterOwners`
  (`~/Development/Allegiance/src/WinTrek/sectormap.cpp:11-50`) counts
  stations per side, **excluding `c_sabmPedestal` types**. The side with
  the most stations is the owner, and the runner-up is the secondary
  owner. The map draws `m_pimageOwnerHighlight` in the owner's side colour
  and `m_pimageSecondaryOwnerHighlight` in the secondary owner's colour on
  each sector (`sectormap.cpp:1050-1052, 1172-1190`).
- **Own-probe count** in the selected-sector info area: count this side's
  probes in the cluster whose probe type has no capabilities or
  `>= c_eabmRescue`, then draw the probe icon in own side colour plus the
  count (`sectormap.cpp:320-345`).
- **Not live in native, so do not build:**
  - Per-class station icons: `DrawStationIconsOfClass` sits inside a
    `/* … */` block (`sectormap.cpp:237-278`).
  - Per-sector "enemy miner" / "enemy" markers are commented out
    (`sectormap.cpp:1084-1093`).

  Threat icons, the part that replaced them, are already ported (P374).
  The coordinator's "miners and enemy markers" request therefore maps to
  what already exists. Note this in the report.

**Port data already available:**
- Stations with `SideId`/`ClusterId`, kept current on capture and destroy
  (`ClientCore/State/ClientWorld.cs:218, 1393-1422`).
- Probes with `SideId`/`ClusterId` (`ClientWorld.cs:259`,
  `ProbeEntity.cs:36,48`).
- Side colours via `ClientWorld.TryGetSideColor` (used at
  `Game1.Rendering.cs:184`).
- Station types (for the pedestal capability,
  `DataStationTypeIGC.SabmPedestal = 0x400`) via the client's
  `ShopCatalog.StationTypes`.
- The selected-sector info pane already ports the asteroid/He3 half of
  native's panel (`Rendering/SectorInfoPaneLayout.cs`, `SectorInfoPaneRenderer.cs`).

## Scope

1. **A pure `GetClusterOwners` port** (no graphics device; testable): given
   the stations and a pedestal predicate, return `(owner, secondary)` side
   ids, or none. Match native's tie-breaking: strictly-greater comparisons
   in side-id order.
2. **Draw the ownership rings** on each sector in the starmap: an owner
   ring and a thinner or inner secondary ring in the side colours, drawn
   so they don't hide the P374 threat icons or the current-sector
   highlight. Do this in both the compact and expanded starmap if both
   exist.
3. **Own-probe count** in the selected-sector info pane: the probe icon (or
   a simple glyph if no icon asset is loaded) in own side colour, plus the
   count, using native's capability filter.

## Explicitly out of scope

- Per-class station icons and enemy/miner markers (commented out in
  native, see above).
- **Issuing orders from the map** (selecting a buildable rock or station
  target for the commandee, `sectormap.cpp:1106-1169`). This is the named
  follow-up "P460b sector-map command picks". It needs the commandee
  model from P425/P461.
- Asteroid special-ability icons beyond what the info pane already shows.
- Any server change.

## Verification

- Pure tests for the owner computation: an empty sector → none; one side
  → owner only; 2 vs 1 → owner and secondary; pedestal stations ignored;
  a tie → native side-id order. **Write the owner-ring test against the
  current renderer's behaviour first and record that it fails** (no owner
  output exists).
- A pure probe-count test with the capability filter.
- Headless capture: the client can render offscreen (`Capture/`). Produce a
  before/after starmap PNG from a real connected session (two sides, one
  captured station) and reference it in the report.
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings/errors.
  Suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs.
  Name any flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check `git branch --show-current`.
2. `docs/reports/P460_SECTOR_MAP_OWNERSHIP_OVERLAYS.md`: the native
   live-versus-commented-out findings, the owner rule, the screenshots,
   and the P460b follow-up.
