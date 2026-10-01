# P460 — Sector Map: Ownership Rings + Own-Probe Count

**Status:** Implemented, tested, headless-captured. Committed on `master` (not pushed).
**Repo:** `/home/domingo/Development/AllegianceCore`
**Native reference:** `src/WinTrek/sectormap.cpp`

## Summary

The starmap overlay (`src/Allegiance.ClientCore.MonoGame/Rendering/StarmapRenderer.cs`)
previously drew only clusters, warp edges, the current-sector highlight and the
P374 threat icons. This stage adds the two pieces native's sector map shows that
were still flagged as follow-ups:

1. **Per-sector ownership rings** — who owns each sector, from the client's real
   station list (native `GetClusterOwners`, sectormap.cpp:11-50). Drawn in both
   the expanded starmap and the compact minimap.
2. **Own-probe count in the selected-sector info pane** — the probe glyph (own
   side colour) + count, with native's capability filter (sectormap.cpp:328-345).

Issuing orders from the map (**P460b**) is out of scope here (see Follow-ups).

## Native findings: what is actually live versus commented out

Verified against the source checkout, as the brief requested:

- **Live:** `GetClusterOwners` (sectormap.cpp:11-50) counts stations per side,
  **excluding `c_sabmPedestal` types**, picks the most-stations side as owner and
  the runner-up as secondary owner. The map blits `m_pimageOwnerHighlight` in the
  owner's side colour and `m_pimageSecondaryOwnerHighlight` in the secondary
  owner's colour (sectormap.cpp:1172-1190).
- **Live:** the own-probe count (sectormap.cpp:328-345) counts this side's probes
  in the sector whose probe type has **no capabilities or `>= c_eabmRescue`**
  (the Imago filter), drawn as the probe icon in the own side colour + count.
- **Not live (commented out in native) — deliberately NOT ported:**
  - Per-class station icons `DrawStationIconsOfClass` sits inside a `/* … */`
    block (sectormap.cpp:237-278).
  - Per-sector "enemy miner"/"enemy" markers (sectormap.cpp:1084-1093).

The coordinator's "miners and enemy markers" request maps to the P374 threat
icons that already replaced them, so that requirement is already satisfied and
nothing further was built for it.

## The owner rule (ported exactly)

`SectorMapQueries.GetClusterOwners` replicates native's algorithm:

- Count each side's stations in the sector, skipping station types whose
  capability mask has `c_sabmPedestal` (`DataStationTypeIGC.SabmPedestal = 0x400`,
  resolved via the client's `ShopCatalog.StationTypes`).
- Select owner = most stations, secondary = runner-up, walking side ids in
  ascending order with **strictly-greater** comparisons (sectormap.cpp:33,43), so
  a tie resolves to the **lower** side id as owner and the higher as secondary.
- A sector with no countable stations has no owner and draws no ring.

## Implementation

**Pure logic** (`src/…/Rendering/SectorMapQueries.cs`, graphics-free, unit-tested):
- `GetClusterOwners(stations, isPedestalType) -> ClusterOwners?` — native port above.
- `CountOwnProbes(probes, clusterId, ownSideId) -> int` — native probe filter
  (`caps == 0 || caps >= 0x2000`), the `0x2000 = c_eabmRescue` threshold sourced
  from igc.h:781.

**Data plumbing:**
- `ProbeEntity` now carries `ProbetypeId` (was dropped in P260); populated from
  `DataProbeExport.ProbetypeId` in `ClientWorld.Apply(ExportProbe)`.
- `ShopCatalog` now carries the core's `ProbeTypes` table (it already carried
  `StationTypes`), so probe capabilities resolve client-side by probe type id.

**Renderer:**
- `StarmapRenderer.Draw` / `DrawMini` accept an optional `owners` map + a
  `sideColor` resolver and paint the owner ring (outer, thicker) and secondary
  ring (inner, thinner) in the two sides' colours — mirroring
  `m_pimageOwnerHighlight` / `m_pimageSecondaryOwnerHighlight`. Rings are drawn
  **first** (underneath the cluster icon / threat icon / selection outline) so
  they never hide the P374 threat icons or the current-sector highlight (a
  deliberate relaxation of native's draw-after-threat ordering, per the brief).
- `SectorInfoPaneLayout.Build` gained an optional own-probe count; the pane
  renderer draws it as a distinct glyph row — a filled diamond in the **own
  side's colour** + the count (stand-in for native's `m_pprobeIcon`, whose art is
  not available to this port; documented simplification).

**Game1 wiring** (`Game1.Rendering.cs:DrawStarmap`): computes per-cluster owners
from `world.Stations` under the gate lock with the pedestal predicate, snapshots
the two owners' side colours, computes the selected sector's own-probe count from
`world.Probes` + `ShopCatalog.ProbeTypes`, and passes it all to the renderers.

## Verification

### Unit tests (11 new, `SectorMapQueriesTests`)
- Owner computation: empty sector → none; all-pedestal → none; one side → owner
  only (no secondary); 2 vs 1 → owner + secondary; pedestal stations ignored;
  **tie → lower side id wins** (native side-id order); 3-way keeps the runner-up.
- Probe count: capability filter (0 and ≥0x2000 count; mine-style 0x0100 does
  not), sector and side gates, inclusive `>= c_eabmRescue` boundary, zero case.
- Info pane integration: `Build(…, probeCount)` sets `ShowsOwnProbes` without
  changing the asteroid display lines.

**TDD note (brief requirement):** the owner-ring "before" state was captured
against the *current renderer* behaviour first — the pre-P460 renderer produces
**no owner output**, proven by the headless before/after diff (below): the
pre-change frame has zero ownership pixels; the post-change frame adds exactly the
two ring clusters.

### Headless capture (real connected session, two sides)
Harness at `/tmp/p460map` (throwaway): boots a real in-process server
(`XXV_simple.igc`, 2 teams), joins two real `ConnectionSession`s (Alpha side 0,
Bravo side 1), waits for the graph + stations, then renders the **real**
`StarmapRenderer` offscreen to PNG. Evidence in `.verify_p460/` (untracked):

| File | Contents |
|---|---|
| `p460_before_full.png` | Expanded starmap, pre-P460 (no owners) — no rings |
| `p460_after_full.png` | Expanded starmap with ownership rings |
| `p460_after_info.png` | Selected enemy sector + info pane showing the `PROBES` row |
| `p460_before_mini.png` / `p460_after_mini.png` | Compact minimap before/after |

Session had 13 clusters / 38 warps / 2 stations / 2 owned sectors:
- **Ceto (1053)** — Alpha (my side, gold) ownership ring.
- **Quaoar (1061)** — Bravo (blue) ownership ring; selected for the info pane.

Pixel-diff of `before` vs `after` full: the **only** change is 185 pixels forming
two rings — blue `(0,138,217)` on Quaoar (Bravo) and amber `(188,160,0)` on Ceto
(Alpha). The mini-map diff is 45 blue pixels on Quaoar (the compact view keeps the
current sector's gold highlight clean and only rings non-current owned sectors).
The info pane's `PROBES 0` row rendered with the own-side-coloured diamond glyph;
the live session had no probes deployed, so the count is 0 (the counting logic
itself is pinned by the pure `CountOwnProbes` tests above).

### Build & suite
- `/home/domingo/.dotnet/dotnet build` — **0 warnings / 0 errors** (whole repo).
- `/home/domingo/.dotnet/dotnet test` — the suite housing all P460 work
  (`Allegiance.ClientCore.MonoGame.Tests`, 1053 tests incl. the 11 new) ran **3
  consecutive clean runs**. `Allegiance.Lobby.Tests` (12) clean.
- **Pre-existing environmental live-test flakes (NOT P460 regressions), verified
  by failing on clean HEAD with P460 stashed:**
  - `Allegiance.Core.Tests`: `LiveTurretGunnerBindingTests` (the documented
    TurretGunner flake), plus a rotating set of live tests whose composition
    varies run to run (8–9 failures of ~3624: `ConquestTerritoryLiveTests`,
    `GameOverPlayersWireTests` conquest variants, `LiveMoneyTests`,
    `ShipUpdateBudgetLiveTests`). Two consecutive full runs with identical P460
    code gave **9 then 8 different failures** — non-deterministic live-network
    flakiness on this Wayland box, not a stable regression.
  - `Allegiance.ClientCore.Tests`: `ShipDeleteGoneLiveTests.DockOut_…`,
    `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` (both fail on clean
    HEAD too).
  None of these touch the P460 code paths (stations/probes/starmap rendering).
  The brief's "3 consecutive clean runs" is met for the P460-affected suite; a
  full-repo "clean" is blocked by these environment-level live flakes, which are
  named here per the brief.

## Files changed
- `src/…/Rendering/SectorMapQueries.cs` *(new — pure owner + probe-count queries)*
- `src/…/Rendering/StarmapRenderer.cs` *(ownership rings in Draw + DrawMini)*
- `src/…/Rendering/SectorInfoPaneLayout.cs` *(own-probe count in the pane)*
- `src/…/Rendering/SectorInfoPaneRenderer.cs` *(probe glyph row)*
- `src/…/Rendering/ShopCatalog.cs` *(carries the core ProbeTypes table)*
- `src/…/State/ProbeEntity.cs` *(preserves ProbetypeId)*
- `src/…/State/ClientWorld.cs` *(populates ProbetypeId)*
- `src/…/Game1.Rendering.cs` *(wires owners + probe count into the starmap)*
- `tests/…/SectorMapQueriesTests.cs` *(new — 11 tests)*

## Follow-up: P460b (named, out of scope)
**Issuing orders from the map** — selecting a buildable rock or station target for
the commandee (native `sectormap.cpp:1106-1169`: buildable-asteroid / station
highlight + click-to-command). Requires the commandee/order model from P425/P461;
deliberately not attempted here.
