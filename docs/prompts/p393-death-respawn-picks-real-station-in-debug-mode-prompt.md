# P393 — death/lifepod respawn sends debug-mode players to the real home station, not the arena garrison

## Why this task exists

Live-tested this session, right after P390 fixed the same class of bug for
the initial-join path. Domingo died (via self-destruct) while playing in
the debug-mode Omega/Plato/Alderan arena and respawned at **Ganymede** —
the real production home station, not the debug arena's synthetic Omega
garrison. This strands a debug-mode player back in the real, otherwise-
unused part of the map every time they die, defeating the same
self-contained-arena purpose P379/P390 exist for.

## Confirmed root cause

`FindFriendlyStation` (`Server.Combat.cs:1434-1443`), called from the
death/lifepod respawn path (`Server.Combat.cs:1346`, `BroadcastKillShip`):
```csharp
private static DataStationIGC? FindFriendlyStation(Ship ship)
{
    if (ship.SideId is not { } sideId || ship.Mission is not { } mission)
        return null;
    foreach (var sector in mission.Sectors)
        foreach (var station in sector.Stations)
            if (station.SideId == sideId)
                return station;
    return null;
}
```
Its own doc comment (`Server.Combat.cs:1417-1433`) is explicit: this is a
**documented P105 simplification** with no proximity/nearest-search at
all — it returns "**any** station whose `SideId` equals the ship's,
first-found in the mission's deterministic sector-then-station order."
That's a reasonable simplification when a side has exactly one station
(true for every normal production game) — but debug mode's topology
(P379) gives side 0 **two** same-side stations: the real Ganymede (part
of the original map, enumerated early) and the synthetic Omega garrison
(added later at boot via `PlaceStation`). "First in mission order" reliably
picks Ganymede, every time, for every side-0 death — not a spatial
judgment at all, purely enumeration order.

**Exact same bug shape P390 already fixed, different call site.** P390's
`ResolveSideHomeSector` (`Server.Lobby.cs:1904-1934`) solves precisely
this — "when a synthetic debug garrison exists for this side, prefer it;
otherwise fall back to the generic real-home answer" — for the
*join/side-select* path. `FindFriendlyStation` needs the identical
preference, for the *death-respawn* path.

## Scope

1. Give `FindFriendlyStation` the same debug-garrison-first preference
   `ResolveSideHomeSector` already established. Don't duplicate the
   garrison-matching logic blindly — look at whether `ResolveSideHomeSector`
   (or a small piece of it) can be reused/called from here directly given
   `FindFriendlyStation` needs to return a `DataStationIGC` (a station),
   not a `Sector`; a small shared helper (e.g. "the debug garrison station
   for this side, if any") that both `ResolveSideHomeSector` and
   `FindFriendlyStation` call is likely the right shape — decide and
   justify in the report rather than copy-pasting the `OmegaDebugStationId`/
   `PlatoDebugStationId` `HashSet` check a third time.
2. Preserve the exact existing fallback behavior (first-in-mission-order
   same-side station) for every case where no debug garrison exists for
   that side — production must be byte-for-byte unaffected.
3. Confirm this is genuinely the only respawn-relevant caller of the
   "any friendly station" pattern — grep for other call sites of
   `FindFriendlyStation` and for any other place death/respawn logic might
   independently resolve a destination station, so this isn't a partial
   fix that leaves a sibling code path with the same bug.

## Explicitly out of scope

- The join/side-select path itself (`ResolveSideHomeSector`,
  `CompleteSideSelection`) — P390 already fixed it; reuse, don't re-touch
  its logic beyond factoring out a shared helper if that's the right
  shape.
- P368-P392 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: boot a real debug-mode server, connect a real
  client, choose a side, die (self-destruct or combat), and confirm the
  lifepod respawn lands at that side's synthetic debug garrison (Omega or
  Plato), not the real Ganymede/Organia. Repeat for both sides.
- **Regression, required**: confirm a normal (debugMode off) game's death
  respawn still lands at whatever real station it did before this change —
  the fallback path must be provably unaffected.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P393_DEBUG_RESPAWN_STATION.md` in the repo root: the shared
   helper (or reuse) chosen, and both live verifications performed.
