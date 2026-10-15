# P390 — debug-mode ShipSpawner ignores the player's explicit side choice, stranding them in enemy territory

## Why this task exists

Live-tested this session (2026-09-26, real debug-mode server, real client
reconnect). Server log at the exact moment:

```
[debugtopology] peer 2 spawned: ship 8099, hull Lxy Scout, side 1, sector 1060 'Plato'
[quitside] ship 8099 left side (reason SwitchingSides): QUIT_SIDE(126) whole-mission to 0 peer(s)
[join] ship 8099 -> side 0: JOIN_SIDE(125) whole-mission to 0 peer(s)
[sideselect] 2 'domingo1' chose side 0 (ship 8099)
```

The player explicitly chose side 0. The server correctly updated their
`SideId` to 0 and re-ran leader/owner election — but their ship physically
stayed placed at Plato (side 1's home), because the placement itself
happened *before* the side-select response, via the debug topology's own
spawner, which has no way to know what the player would choose. The
player was left side-0-but-sitting-at-an-enemy-station — unable to dock
(a station only accepts same-side ships), which is exactly what was
reported live as "docking not working."

## Confirmed root cause

`Server.Lifecycle.cs:291-311` — the debug topology's `ShipSpawner` closure:
```csharp
ShipSpawner = (core_, mission_, shipId) =>
{
    int n = spawned++;
    short sideId = (short)(n % 2);            // side 0 → Omega, side 1 → Plato
    ...
```
Its own signature (`Func<StaticCore, Mission, short, (Ship, Sector)>`,
`Server.cs:319`) has **no side parameter at all** — it can't know what the
connecting player will choose, so it always places by its own internal
round-robin counter (`spawned++`), independent of any side-select.

`Server.Lobby.cs:1824-1837`, `AssignShip`:
```csharp
var (ship, sector) = ShipSpawner is not null
    ? ShipSpawner(mission.Core, mission, shipId)
    : SpawnDefaultShip(mission, shipId, requestedSideId);
```
The **default** path (`SpawnDefaultShip`) already receives and honors
`requestedSideId` — its own doc comment (`Server.Lobby.cs:1869-1879`, P368)
states the correct principle explicitly: "the requested side is threaded in
BEFORE placement so a single correct `PlaceShip` puts the ship in that
side's real home sector." The custom-spawner branch has no equivalent —
`ShipSpawner(...)` is called with **no side information whatsoever**, and
whatever side/sector it happens to pick is just accepted, even when it
contradicts the side the player explicitly asked for a moment later in the
same connection sequence (`CompleteSideSelection`,
`Server.Lobby.cs:1849-1889`, which calls `AssignShip` then separately calls
`TryJoinSide(live.Ship, requestedSideId)` — that call only updates
SideId/leadership/roster bookkeeping, it does not reposition the ship).

## Explicitly constrained scope — read before touching anything

**Do NOT change the `ShipSpawner` delegate's signature.** It is consumed
by ~150 existing call sites (`grep -rn "ShipSpawner\s*="` across `tests/`,
`tools/vizfight`, `tools/livecombat`, `tools/chatdemo`,
`tools/hullhit-proof`) — every single one is a closure matching the exact
current 3-parameter shape. Adding a parameter would require touching all
of them for a change that's only actually needed by ONE closure (P379's).
That is a wildly disproportionate blast radius for this fix. The
reconciliation must happen in the ONE caller (`AssignShip`), not by
threading new information down into every spawner.

## Scope

1. In `AssignShip` (`Server.Lobby.cs:1830-1837`), after calling
   `ShipSpawner(...)`: if `requestedSideId` is non-null AND the returned
   `ship.SideId` doesn't match it, **reposition** the ship into the
   requested side's actual home — don't just silently accept the mismatch
   and don't just patch `SideId` without moving the ship (that's the
   current, broken, half-fixed state after `TryJoinSide`).
2. **Resolving "the requested side's home sector" generically** (this must
   work for debug mode's synthetic Omega/Plato AND, if a custom spawner is
   never installed, the normal production path already handles this via
   `SpawnDefaultShip`/`HomeSectorForSide` — don't duplicate that lookup,
   reuse it if it's already side-id-keyed and reachable from here; if it
   isn't generically reachable, find the smallest correct way to resolve
   "this side's home station's sector," e.g. via `mission.Sectors`'
   stations filtered by `SideId`, which is exactly how P379's own closure
   picks `home` today — `home.Stations.First(st => st.SideId == sideId)`.
   Investigate and confirm the real, existing mechanism rather than
   inventing a second one).
3. Preserve the debug topology's hull-mix logic (Scout for the first
   connection per side, Fighter for the second, `Server.Lifecycle.cs:295`)
   when repositioning — a corrected placement should still land in the
   right slot/position pattern (`Server.Lifecycle.cs:299-304`), not just
   teleport the ship to the station's exact center. Decide whether to
   re-derive this from the corrected side's own connection count, or
   whether accepting a slightly-off position (still within the home
   sector, near the station) is an acceptable simplification — make a
   real judgment call and say so plainly in the report, don't silently
   pick one.
4. Confirm the reverse case too: what happens today when a player
   connects and does NOT pick an explicit side (`requestedSideId == -1`,
   the auto-assign path, `Server.Lobby.cs:1886-1889`)? Does the debug
   spawner's round-robin placement and the auto-assigned `SideId` actually
   agree in that case (they should, since nothing overrides the spawner's
   own choice)? Verify this live, don't assume.

## Explicitly out of scope

- Changing `ShipSpawner`'s delegate signature or touching any of its ~150
  existing closures — see the constraint above.
- Any other debug-mode topology behavior (P379/P381/P382/P386/P387/P388) —
  unrelated, don't touch.
- The unrelated `ShopModel`/`PartInfoLayout` crash fixed live this session
  (already committed, separate issue) — don't re-touch.

## Verification — real proof, not just build/test

- **Live proof, required**: boot a real debug-mode server, connect a real
  client, explicitly choose side 0 — confirm the ship actually lands in
  Omega (not Plato), and successfully docks at the Omega Debug Garrison.
  Repeat explicitly choosing side 1 — confirm it lands in Plato and docks
  there. Repeat with no explicit side chosen (auto-assign) — confirm
  whichever side is auto-assigned matches the sector the ship is actually
  placed in.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected. Given the sheer number of tests that
  set `ShipSpawner`, run the full suite (not a subset) to catch any
  unexpected interaction.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P390_DEBUG_SPAWNER_SIDE_MISMATCH.md` in the repo root: the
   reconciliation mechanism chosen, the hull-mix/positioning judgment
   call from point 3, and all three live verifications performed.
