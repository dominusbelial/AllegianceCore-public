# GHOST_SHIP_SECTOR_MEMBERSHIP_P307 — fix ghost-ship sector-membership desync

Three independent read-only reviews of `src/Allegiance.Server/Server.cs`
(kimi-k3, glm-5.3, and earlier jackod-9b-coder) all converged on the same
root-cause bug: relocation sites that move a ship between sectors reassigned
only the `Ship.Sector` back-reference without updating the sector `_models`
lists — the lists every real per-tick pass actually walks. A ship whose
`Sector` says "here" but whose `_models` membership is stale becomes a
server-side ghost: ticked by the wrong sector (or none at all), frozen,
invisible, never updated. See
`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-kimi-k3.md`
(#1) and `.../allegiance-server-review-glm53.md` (C1).

## Root cause

Sector membership is two facts kept together: `Ship.Sector` (a back-ref) AND
membership in that sector's `_models` list. The correct idiom already existed
in the file as `MoveShipToSector` (`Server.cs:9196`). Four relocation sites
skipped the list update and only reassigned the back-ref:

1. **Death-respawn**, `BroadcastKillShip` — the worst case. The combat/death
   pass removes the dead ship from its old sector's `_models` (Sector.cs, the
   death contract), so a respawn that only sets `Ship.Sector` strands the
   respawned ship in **no** sector's list at all → frozen, invisible,
   un-tickable until reconnect.
2. **`PerformTeleport`** — the ship stayed ticked by the **old** sector under
   new-sector coordinates; peers in the new sector never saw it arrive.
3. **`PerformRescue`** (ship-ship) — same pattern.
4. **`PerformStationRescue`** (P301 station-rescue placement) — same pattern
   (the identical relocation line in the companion rescue path).

## The fix (all in `src/Allegiance.Server/Server.cs`)

Hardened the existing `MoveShipToSector` helper to be exact-once-safe, then
routed all four sites through it.

- **Helper** `Server.cs:9196` — now always converges on the invariant "the ship
  ends up in exactly the target sector's `_models` (not the old one's, not
  none), with the back-ref in sync." It tolerates a ship a death pass has
  **already** removed from its old sector's list (the remove is a harmless
  no-op) and a same-sector move where the ship may already be present (the
  `Contains` guard prevents a double-add). Strict superset of the original
  logic; provably behavior-identical for the existing ripcord callers.
- **Respawn** `Server.cs:12129` — `MoveShipToSector(victim, destSector)`.
- **Teleport** `Server.cs:9822` — `MoveShipToSector(ship, destSector)`.
- **Rescue (ship-ship)** `Server.cs:8689` — `MoveShipToSector(rescuee, destSector)`.
- **Rescue (station, P301)** `Server.cs:8755` — `MoveShipToSector(rescuee, destSector)`.

The **destination sector each path chooses is untouched** — only which `_models`
list the ship ends up living in is fixed.

## Live proof (in-process server + loopback bot harness)

New `tests/Allegiance.Core.Tests/GhostShipSectorMembershipTests.cs` (3
tests) drives a real in-process `Srv` with the established `ShipSpawner` /
`FltBot` harness and asserts the P307 invariant — the relocated ship is in
the destination sector's `_models` **exactly once** and in no other sector's —
plus that a teleported ship, once undocked, genuinely **ticks** in the
destination sector's `GetShipUpdateCandidates()` (not as a phantom in the old
sector). Real captured output:

```
GHOST-RESPAWN: ship 8096 died in sector 1051 -> respawned docked at station 1051 in sector 1053; _models membership: dest 1, elsewhere 0 — consistent (P307)
GHOST-RESCUE:  lifepod 8096 rescued in sector 1052 -> placed docked in sector 1053; _models membership: dest 1, fly 0 — consistent (P307)
GHOST-TELEPORT: ship 8096 tp station 1051 (sector 1053) -> station 9100 (sector 1051); _models membership: dest 1, old 0; undocked -> ticks in 1051 ship-update candidates, not in 1053 — consistent (P307)
```

**Must-fail proof:** reverting the four site patches (restoring the bare
`X.Sector = destSector;`) makes all 3 tests fail on the exact assertion
`must be in destination sector ... _models exactly once; found 0` — i.e. the
pre-fix ship is in NO destination list, confirming the root cause and that
these tests are discriminating, not always-green.

## Build / test status

- `dotnet build src/Allegiance.Server` → **0 warnings, 0 errors**.
- New suite `GhostShipSectorMembershipTests` → **3/3 pass**; goes red when the
  fix is removed.
- Related live suites (death/respawn, rescue, teleport, ripcord, eject) → **36/36 pass**.
- Full `Allegiance.Core.Tests` suite run 2× — see run log (only the documented
  pre-existing flakes, e.g. `LiveTurretGunnerBindingTests` timing).

## Out of scope (separate tasks)

- **P308** — glm-5.3 C2: `EjectSurvivor` / respawn never restore hull fraction,
  so an ejected/respawned lifepod is born `Dead`. That is Dead-flag semantics,
  not `_models` membership; P307 changes membership only. (Consequence: a
  respawned, docked pod is still excluded from `GetShipUpdateCandidates` by the
  pre-existing Dead + docked gates — unrelated to the membership invariant this
  task restores.)
- **P309** — the mission-stage-lie in the join burst.
- Every other finding from either review not named above.
- The native reference (`~/Development/Allegiance`) — read-only, untouched.
