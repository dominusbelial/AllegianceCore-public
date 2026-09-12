# P69 — undock now actually launches the ship clear of the station

## The bug

`Server.cs`'s `FM_C_DOCKED`(48) undock handler cleared `Ship.StationId` and
did nothing else. `Sector.ResolveDockCollisions` (`Sector.cs:445-484`) gates
re-docking on `ship.StationId is not null` alone — a purely positional sphere
test (`DockRadius = 200f`) with no velocity/cooldown/"just launched" guard.
A ship that undocks while still physically inside `DockRadius` — which is
exactly how this project's own `tools/vizfight` explorer spawner places a
joining bot ("well inside DockRadius=200f — bot starts docked",
`Program.cs:485`) — gets re-latched on the very next tick.

Live-observed tonight (the P68 commander→miner dispatch/undock arc): the
miner's real `SendUndockRequest()` (P68, hand-sent `FM_C_DOCKED`) fires
correctly and the server's `[undock]` line confirms it every time — but
`[dock]` fires again 1-4 ticks later, forever, producing an endless
dock→undock→redispatch cycle. The commander's own "Kicking loafing miner"
re-fires every ~5s (its own CS_PING-driven cadence) because the miner never
visibly leaves; the miner's own dispatch handler re-runs identically each
time. Confirmed via `grep [undock]`/`[dock]` on the live backend log:
alternating pairs, same ship/station, indefinitely.

## The fix

Mirrors the native reference's own launch mechanic
(`CstationIGC::Launch`, `stationIGC.cpp:373-382`): on a successful undock,
reposition the ship just outside `DockRadius` along the station→ship
direction (falling back to a fixed direction if the ship is exactly at the
station's position — shouldn't happen given `DockRadius=200` vs. a ship
radius, but not assumed), and give it the native's own default
`c_fExitStationSpeed` (50 u/s, `missionigc.cpp:2689`) outward. This is a
simplified match to the native's own geometry-aware launch point
(`GetRadius() + pship->GetRadius() + vLaunch*0.5f + ...`), consistent with
this port's own established P53 simplification of the dock test itself to a
plain sphere check.

`Server.cs`: two new constants (`LaunchClearMargin = 50f`,
`ExitStationSpeed = 50f`) plus ~15 lines in the `FM_C_DOCKED` handler,
looked up via `Sector.Stations` (already resolvable from the `LiveShip`'s
own `Sector`).

## Tests

Two pre-existing tests had built their own test methodology around the old
buggy immediate-re-dock behavior (using it as a convenient way to trigger a
*second* dock transition), so they needed updating, not because the fix is
wrong but because it removes the exact race they were exploiting:

- `DockDockingTests.FriendlyDock_sends_exactly_one_DockedNotify_per_event_with_correct_station`
  — now explicitly flies the ship back into the station's radius after
  undocking (waiting for the undock to actually land server-side first,
  since this is a real TCP `FltBot` and `SendDocked()` doesn't block until
  processed) instead of relying on it re-docking on its own.
- `MiningSellTests.Loopback_redock_sells_again_each_transition` — same
  "wait for the undock to land before writing Position again" ordering fix,
  plus a `Velocity = Vector3.Zero` reset after manually repositioning onto
  the ore rock (otherwise the new outward launch velocity — set when the
  server actually processes the queued `FM_C_DOCKED` inside a later `Tick()`
  — would drift the ship off the rock before it reached the mining checkpoint).

2290/2290 across 3 consecutive clean runs (`~/.dotnet/dotnet test -c
Release`).

## Verification still needed

This closes the loop mechanically (undock now actually clears the ship
away) but the live 2-bot VM pass hadn't confirmed the miner visibly flying
toward its ordered cluster before this fix landed — that's the next
live-VM check.
