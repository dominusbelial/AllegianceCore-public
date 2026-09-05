# P74 — undock's launch velocity sent the ship far off-course

## The bug

P69 fixed the immediate re-dock loop by repositioning an undocking ship
outside `DockRadius` and giving it an outward velocity, mirroring the
native reference's `CstationIGC::Launch`
(`stationIGC.cpp:373-382`, `SetVelocity(forward * vLaunch)`).

The direction chosen for that velocity — the station→ship offset at the
moment of undock — has no relationship to where the ship is actually
supposed to go. The server has no visibility into the client-side
`SetCommand`/`SetAutopilot` target the miner already set before sending
`FM_C_DOCKED` (`Miner.cs`'s dispatch handlers call those first, then
undock). Live-tested twice tonight with the real 2-bot swarm:

- Run 1: the arbitrary launch direction happened to roughly align with
  the real route to the assigned warp (confirmed via a temporary native
  diagnostic, P73) — the flight looked clean, converging steadily.
- Run 2: the arbitrary direction pointed nowhere near the real target.
  The ship built up real velocity in the wrong direction and traveled
  **~11,000 units** off-course before the native autopilot's own turning
  dynamics could claw it back onto a converging path — a real,
  user-visible "flies to nowhere first" defect (user's own words: "it's
  going to la la land").

## The fix

Drop the velocity kick entirely. Keep the position-only reposition
(moving the ship to just outside `DockRadius` along the station→ship
direction) — that alone already satisfies the actual P69 requirement
(clearing `DockRadius` stops the immediate re-dock; no persistent
velocity is needed for that, confirmed by P69's own original live test
before the velocity component was even added). Set `Ship.Velocity =
Vector3.Zero` instead of an outward kick.

This lets the native autopilot apply real thrust from a clean,
near-zero-velocity start in the *correct* direction on its very first
real control update, instead of first having to cancel out an unrelated
vector before making any real progress.

Removed the now-unused `ExitStationSpeed` constant.

## Tests

2290/2290 across 3 consecutive clean runs — pure server-side change, no
existing test asserted the removed velocity's presence.
