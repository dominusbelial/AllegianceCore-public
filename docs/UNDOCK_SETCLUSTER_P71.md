# P71 — undocked ships never actually flew: the client's movement gate never reopened

## The bug

P69 fixed the immediate re-dock loop (ship undocks, stays inside
`DockRadius`, gets re-latched next tick). Live-testing that fix showed a
new symptom: the ship undocked and stayed clear of the station — no more
re-dock — but then just sat motionless forever, never making any further
progress toward its dispatched target.

Traced via the actual message chain, not guessed: `[ctrl]` (the server's
own log line for a received `FM_C_SHIP_UPDATE`(24), `Server.cs:1253`) never
appeared **even once** in a multi-minute live session, for either ship.
`BaseClient::SendUpdate` (`clintlib.cpp:1815`) — the only client-side call
site that ever constructs and sends that message — gates on
`flyingF()` (`clintlib.h:1198`):

```cpp
bool flyingF(void) const { return (m_ship != NULL) && (m_ship->GetCluster() != NULL); }
```

Docking nulls the client's own `m_ship`'s cluster reference
(`IshipIGC::SetStation`, `shipIGC.h:421-485`, via `s->AddShip(this)` →
`SetCluster(NULL)`), set client-side by the `FM_S_DOCKED`(61) handler
(`appmsg.cpp:1113`). Nothing in `FM_S_SHIP_STATUS`'s handler
(`appmsg.cpp:2713-2745`) restores it — that handler only updates
`PlayerInfo`/threat-list bookkeeping, never touches the ship's own model.
The **only** place client-side that ever calls `m_ship->SetStation(NULL)`
to restore the cluster reference is inside the `FM_S_SET_CLUSTER`(7)
handler itself (`appmsg.cpp:1044-1049`) — conditioned on
`if (m_ship->GetStation() != NULL)`. Without a fresh `SET_CLUSTER`
following the undock, the client keeps believing it's docked forever
(`GetStation() != NULL`, `GetCluster() == NULL`), `flyingF()` never
becomes true again, and `SendUpdate` never sends another real control
message — matching every observed symptom exactly.

This is the **identical** mechanism `Server.cs`'s existing `OnShipWarped`
handler already documents and handles for warp transits (`Server.cs:1900
-1946`, its own comment citing the same `appmsg.cpp:1028-1087` handler as
"the client-side sector reference the whole exploration/movement loop is
gated on") — it was just never extended to the undock path.

## The fix

`Server.cs`'s `FM_C_DOCKED` (undock) handler now also sends a fresh
`FM_S_SET_CLUSTER`(7) to the undocking peer, immediately after clearing
`StationId` and applying P69's launch-clear reposition — same shape
`OnShipWarped` already sends (`SectorId` unchanged since undock doesn't
cross sectors, `Ship` = `ServerShipUpdate.FromModel` reflecting the new
post-launch position/velocity, `Cookie = 0` reusing the same
already-established simplification `OnShipWarped` uses).

## Verification

2290/2290 across 3 consecutive clean runs (purely additive — one more
message sent on an existing code path, no existing test asserts its
absence). Live-VM retest: real 2-bot swarm, dispatch → undock → **real,
sustained flight** — sampled the miner's live position four times over
~15 seconds via the spectator page's own `window.__vstate()` debug hook:
`(820, 1560, 0)` → `(1230, 1560, -2656)` → `(3162, 1560, -6697)` →
`(4037, 2327, -9920)`, continuous progress with real course corrections
(yaw/pitch varying tick to tick, full forward throttle), gaining altitude
— genuine autopilot-driven flight, not a one-off nudge. `[ctrl]` messages
now arrive repeatedly (previously: literally zero in an entire session).

This is the first time in the whole project a bot has been watched
undocking and then **actually flying** under its own autopilot toward an
ordered destination, not just leaving the dock radius.
