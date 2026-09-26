# P370 — ships that leave a viewer's sector (dock OR warp out) render as a frozen ghost forever

## Why this task exists

Live 4-client playtest (2026-09-24, real remote server, 4 real MonoGame
clients): Domingo warped one ship (8097, "Domingo1") from Tellus to
Bug Nebula while a teammate (8098, "Domingo3") stayed behind in
Tellus. Confirmed via the server's own authoritative log that they are
now in genuinely different sectors:

```
[warp] ship 8097 crossed from 'Omega' (1053) to 'Bug Nebula' (1054) via warp 2154
```

— yet Domingo3's client kept rendering Domingo1's ship, frozen at its
last known Tellus position. This is a real, live-confirmed bug, not a
guess.

**Root cause, confirmed by direct code read**
(`src/Allegiance.Core/Model/Sector.cs:437-461`,
`GetShipUpdateCandidates`): a ship that dies, docks, OR warps to a
different sector is `Remove`d from that sector's `_models` (confirmed
by the function's own doc comment: *"a ship dying, warping out or
docking all Remove from `_models`"*) and simply stops appearing in
future broadcasts to peers still in that sector. **Nothing ever
explicitly tells those peers "this ship is gone."** The client just
keeps whatever `ShipEntity.Latest` pose it last received, rendering it
forever.

**This is the exact same gap already fixed once, for one case only.**
`Game1.cs:1938-1945` (P344) excludes a ship from rendering when
`ship.IsDead` — a real, working, server-driven flag set only by a real
`KILL_SHIP(105)` broadcast sent to every peer who could see that ship.
That's the correct pattern. It was never generalized:

- **Docking**: already investigated earlier this session — the
  `SHIP_STATUS(90)`/`State=c_ssDocked` transition
  (`Server.Combat.cs:1668`, `OnShipDocked`) is sent **only to the
  docking peer's own connection**, never broadcast to same-side or
  cross-side peers who already knew about that ship.
- **Warping to a different sector**: confirmed live just now.
  `SET_CLUSTER` (the message that tells a ship it moved) is sent only
  to the ship that moved (`Server.Lobby.cs:2414` and other
  `SET_CLUSTER` construction sites), never to other peers who were
  tracking that ship in the sector it just left.

## Scope

1. **Build one unified "this ship is no longer in your view" signal**,
   generalizing the `IsDead` pattern rather than patching docking and
   warp-out as two separate one-off fixes. Consider: does it make
   sense to widen `IsDead`'s role/rename it (e.g. to something like
   `IsGone`/`IsNoLongerVisible`), or add a parallel flag/state that the
   SAME render-exclusion check sites already gate on? Look at every
   `ship.IsDead` check site found this session (`Game1.cs:1945, 2320,
   4352, 5567, 5764` and any others — re-grep, don't trust this list as
   exhaustive) and make sure the new "gone" condition is excluded
   everywhere a dead ship already is, not just the main render loop.
2. **Server side**: for both the docking transition and the
   cross-sector warp-out transition, broadcast an explicit "this ship
   left your view" notice to every peer who currently has that ship in
   their known set (same-side teammates AND cross-side peers who
   received a cross-side EXPORT for it — reuse whatever bookkeeping
   P369 already put in place for tracking which peers know about which
   ships, if applicable, rather than re-deriving it). Decide the
   cleanest real mechanism: it could be a new lightweight message, or
   piggybacking a "gone" signal onto an existing broadcast — read how
   `KILL_SHIP(105)` does it for death (both the wire message and the
   server-side broadcast-to-everyone-who-knew logic) and follow the
   same shape for consistency, since that one already works correctly.
3. Confirm the fix handles a ship COMING BACK into view too (e.g.
   undocking, warping back into a sector, or a fresh EXPORT to a peer
   who re-gains visibility) — the client must correctly un-set the
   "gone" state and resume rendering live updates when that happens,
   not stay permanently hidden once marked gone.

## Explicitly out of scope

- P369's own fix (own-ship/cross-side `SideId`) — already landed,
  confirmed live, don't touch.
- The `SeenBySide`/cross-side EXPORT gating logic P369 already
  corrected — reuse it, don't re-litigate it.
- Redesigning fog-of-war/visibility scope in general — this task is
  only about explicitly notifying a peer when a ship they ALREADY knew
  about stops being visible to them, for the three real trigger cases
  (death — already correct, docking, sector warp-out).

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce the exact scenario that surfaced
  this — at least 2 real clients, one warps to a different sector while
  the other stays behind watching it. Confirm the ship disappears from
  the peer who stayed behind's view promptly (not frozen), for both a
  same-side teammate pair and a cross-side pair. Also test the docking
  case the same way (one peer docks while another watches). Also test
  the reverse (undocking / warping back into view) to confirm the ship
  reappears and resumes live updates correctly.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The unified fix, committed on `master` (not pushed — Domingo
   pushes). Do not create a side branch.
2. Report doc `P370_SHIP_LEFT_VIEW_NOTIFICATION.md` in the repo root:
   the real mechanism chosen (new message vs. piggybacked signal, and
   why), what changed, and the live verification performed for all
   three transitions (docking, sector warp-out, and the reverse
   re-appear cases).
