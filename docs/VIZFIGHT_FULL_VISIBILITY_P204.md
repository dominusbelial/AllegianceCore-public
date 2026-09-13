# P204 — vizfight: export every sim object, not just ships

## Summary

`tools/vizfight` previously exported only ships per tick. This task adds a
`models` array alongside the existing `ships` array, covering every other
live `Model` subclass in the world (`Projectile`, `Missile`, `Mine`,
`Chaff`, `Treasure`), plus a one-time `sector` static-geometry broadcast
(stations/asteroids/warps) for the fight scenario, which previously only
`ExploreMain` sent.

## What changed

- `Program.cs`: `AllModels(Srv)` (the same `Missions→Sectors→Models`
  traversal `AllShips` already used) + `SerializeModel(Model)` — real fields
  only, read from each class, not invented: `Projectile`/`Missile` expose
  `Position`/`Forward`, no side field of their own — side is derived from
  `(Launcher as Ship)?.SideId` for client colour-matching. `Mine` carries
  `SideId` directly. `Chaff` needs only `Position` (no heading field
  exists). `Treasure` exposes `Code` and, for a flag treasure specifically,
  `Data.TreasureId` is the owning side — the same field P201's flag-pickup
  handler reads (`Server.cs:8167`).
- `TickFrame` and `ExploreTickFrame` both now emit `models` alongside the
  untouched `ships` array.
- The fight scenario's `Main()` now broadcasts one `sector` message per
  mission sector at boot (reusing the existing `SectorMessageJson`/message
  shape `ExploreMain` already used — not a second format), cached and
  replayed to late-joining spectators via `SpectatorHub.Seeder`.
- `wwwroot/index.html`: a per-`kind` colour/scale table (`MODEL_STYLE`) and
  a generic sphere geometry, with the same spawn/update/prune lifecycle the
  ship path already has (`modelState`/`modelMeshes`, reusing the existing
  `GHOST_TICKS` staleness rule — short-lived kinds need no separate expiry
  timer since they simply stop appearing once removed from `Sector.Models`).
- **Real fix beyond the brief**: the old `sector` message handler used to be
  the *signal* that flipped the page into "explore mode" (`if
  (!exploreMode) { exploreMode = true; ... }`). Since the fight scenario now
  also sends `sector` messages, that would have wrongly flipped combat runs
  into explore-mode rendering. Fixed by classifying mode once, from the
  first ship-bearing tick, based on whether ships carry a real `sector`
  field (explore ships always do; combat ships never do) — a new
  `maybeAdoptCombatFocus()` adopts the fight's single sector as focus once
  classified, since combat ships have no `sector` to nominate one from the
  way explore's existing logic does.

## Verification (independently re-run, not the delegate's own claim)

- `dotnet build`: 0 warnings, 0 errors, whole solution.
- **Fight scenario** (`dotnet run --project tools/vizfight -- 7721`): boots
  clean, 8 real bot peers connect and launch, ran a full combat cycle with
  no exceptions. Connected directly to the WebSocket (a small Python client,
  no library) and confirmed real data: **147 of 200 tick frames carried a
  non-empty `models` array**, `kind: "projectile"` present with real
  positions from actual weapon fire; all **13 sector messages** broadcast
  correctly with accurate station counts (1, 1, 0×11).
- **Explore scenario** (`dotnet run --project tools/vizfight -- explore
  7727`): boots clean. Connected a real `Allegiance.Bot` instance, then the
  WebSocket client, and confirmed a real ship-bearing tick with
  `sector: 1050` (a real, non-null value) — the exact signal the new
  mode-classification logic keys on. No exceptions over the run.
- Note: the fight/explore-mode scenarios only produce `projectile`-kind
  models in this pass (this demo has no mines/missiles/chaff/treasure
  spawning today) — the `mine`/`missile`/`chaff`/`treasure` serialization
  branches were verified by code review (real fields, matching class
  definitions) but not exercised by live data, since nothing in either
  existing scenario creates those objects yet. The upcoming showcase
  scenario (P205) is what will exercise them for real, treasure/flag
  included.
