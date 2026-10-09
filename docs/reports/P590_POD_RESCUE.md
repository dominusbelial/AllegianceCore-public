# P590 — Pod rescue: contact rescue for client-driven ships + c_cidPickup + bot pickup

## Problem

Domingo (2026-10-08, live on 221 @ `5e9eabff`, 15v15 showcase): "why are bots not
saving me when they see me podded? Also if I fly to them while podded and reach
any friendly I should be instantly rescued." Server log showed many ejections and
**0 `PLAYER_RESCUED`**.

## Root cause

The contact-rescue *mechanics* were not broken. A live, in-process repro (the
established loopback `Srv` + `FltBot` harness, real `PCore001` hulls) proved:

1. A real ejected pod, **driven by its own client via native pose updates**,
   rescues the instant it reaches a friendly rescue-capable ship —
   `Sector.ResolveRescueEvents` → `Server.PerformRescue` → `PLAYER_RESCUED`,
   exactly the native contact flow (`shipIGC.cpp:1027-1035`,
   `FedSrv.CPP:13033-13074`).
2. The practical reason nothing rescued anyone live is that the **enabling
   flows were missing**: bots never diverted to pods (no proactive pickup AI),
   there was no pickup request (c_cidPickup), and the only rescue path was the
   player manually threading a tiny pod into a moving friendly — which in
   practice never happened.

The one real mechanical trap the repro surfaced is the **stale-pose-cookie
freeze**: native rotates the pod's cookie on eject (`NewCookie()`,
`FedSrv.CPP:12019`); a pose-driving agent that does not consume the EJECT
message's fresh cookie has its pod's pose updates dropped whole by the P507
gate, freezing the pod at the eject point (never flyable, never rescueable).
The real client consumes it (`ClientWorld.Apply(Eject)`, P540b); the test
harness did not — fixed below.

## Changes

- **`Ship.PickupTarget`** (`Sim/Ship.cs`) — the friendly pod a rescue-capable
  ship is diving for.
- **`ApplyNpcAutopilot` pickup branch** (`Sim/Ship.cs`) — the assigned
  diverter steers to the pod's live position through the existing shared
  `SteerToward` primitive (last goal branch ⇒ only wins when goto/plan/mine/
  layer/attack/defend are unset: the combat/task-priority rule). Arrival radius
  is set *well inside* the contact threshold so the rescue pass fires reliably
  (the `SteerToward` throttle ramp reaches 0 exactly at the arrival radius;
  settling on the threshold can leave the ship a hair outside). Goal self-clears
  when the pod is rescued/docked/gone → "returns to task".
- **`HandleOrderChange` c_cidPickup branch** (`Server.ShipCommands.cs`) — a
  lifepod commanding a same-side rescue-capable friendly arms the diverter
  (`PickupTarget`), and for a **real-player** rescuer streams a
  `QueuedCommandUpdate(CidPickup, Ship <pod>)` to its client ("prepare for
  recovery", native `trekigc.cpp:4896`). Validation mirrors native
  `LegalCommand` combos (`shipIGC.h:1661-1678`).
- **`ApplyDroneCommand` c_cidPickup arm** (`Server.ShipCommands.cs`) — the
  individual-chat→NPC path (P469) now arms an NPC diverter for pickup
  (previously fell through the `default:` "no matching arm").
- **`ResolvePodPickupAssignments`** (`Server.Broadcast.cs`) — the proactive
  per-tick pass: every flying friendly pod with no diverter yet gets the nearest
  **idle** same-side rescue-capable AI ship within `PodPickupRange` (5 km)
  assigned; at most one diverter per pod; engaged/tasked ships (any active goal
  slot) are never assigned.
- **Bot harness** (`Bot.cs`) — consumes the EJECT message's rotated pose cookie
  (`PoseCookie`, `Ejects`) mirroring `ClientWorld.Apply(Eject)`, so client-
  driven pod tests can fly the pod after eject.

## Native fidelity (cited)

- Contact rescue: `shipIGC.cpp:1027-1035` (friendly lifepod ↔ rescue-capable
  pair, direction-free); `FedSrv.CPP:13033-13074` (RescueShipEvent →
  PLAYER_RESCUED → nearest friendly station). Ported by the existing
  `ResolveRescueEvents`/`PerformRescue`; unchanged here.
- c_cidPickup: issued by the pod to a rescue-capable friendly
  (`WinTrek.cpp:10666-10680`); the rescuer shows "prepare for recovery. Press
  [insert] to accept." (`trekigc.cpp:4893-4897`); legality
  `shipIGC.h:1661-1678`.
- Eject cookie rotation: `FedSrv.CPP:12019` / P540b.

## Tests (`P590PodRescueTests.cs`, 5 tests)

- **P1** — a real ejected pod driven client-side (rotated cookie) onto a
  friendly → instant `PLAYER_RESCUED`, pod docked at a friendly station.
- **P2** — pod issues a c_cidPickup order to a rescue-capable NPC → diverter
  armed, flies to the pod, contact rescues it.
- **P2b** — pod issues c_cidPickup to a real-player rescuer → the rescuer
  peer receives a `QueuedCommandUpdate(CidPickup, Ship <pod>)` ("prepare for
  recovery") and is marked `PickupTarget`.
- **P3a** — proactive pass assigns the nearest idle diverter to a flying pod
  unprompted (at most one) and it rescues it.
- **P3b** — an ENGAGED (AttackTarget-set) rescue-capable diverter is never
  proactively assigned even when nearest (combat priority).

### Failing-first

- Disabling the proactive pass → **P3a/P3b fail** (no diverter ever armed).
- Disabling the `HandleOrderChange` c_cidPickup branch → **P2b fails** (no
  queued order delivered).
- P1 fails on today's harness (before the EJECT-cookie consume) because the
  pod freezes at the eject point.

### Coverage of P578 / P583

P578's rescue messages and P583's +5 rescue score ride the `PLAYER_RESCUED(16)`
wire and the rescuer's `AddRescue()` in `PerformRescue` — both already verified
by the existing `LiveRescueTests` / `LivePlayerScoringFeedsTests`, which pass
alongside the new suite (18/18 related live tests green).

## Build / test status

- Whole solution build: **0 warnings, 0 errors**.
- `P590PodRescueTests` 5/5 pass, stable over 8 consecutive class runs.
- Related live rescue/scoring suites pass (18/18).
- Full suite via `scripts/run-tests.sh` — 3 consecutive runs on the final
  code:

  | run | Lobby | MonoGame | ClientCore | Core |
  |-----|-------|----------|-----------|------|
  | 1 | 12/12 | 1654/1654 | 561/561 | 3835/3838 |
  | 2 | 12/12 | 1653/1654 | 561/561 | 3835/3838 |
  | 3 | 12/12 | 1654/1654 | 560/561 | 3835/3838 |

  Every failure in all three runs is a **pre-existing documented load-flake**
  in `docs/TEST_FLAKES.md` (no test in this task failed in any run):
  - Run 1: `AutoReloadOnEmpty…RELOAD129` (L52), `TcpLoopbackProof…ClientHalf`
    (L403), `LiveTreasurePickup…Rescue_ship_picking_cash_treasure` (L157).
  - Run 2: `ChatTargetCompose…ComposeDefault_Team` (live-wire), 
    `LiveStationRescue…Lifepod_reaching` (L156), 
    `ConquestTerritory…Conquest_win_in_one_mission_leak` (L31), 
    `AddPartTests…Part_pickup` (L247).
  - Run 3: `ShipDeleteGone…CrossSide_WarperLeavesSector` (L51), 
    `AddPartTests…Part_pickup` (L247), 
    `GameOverPlayers…Win_by_elimination` + `LiveRipcord…Ship_anchor` (the
    GameOverPlayers / LiveRipcord live-wire timing families documented at
    TEST_FLAKES L36/L182/L439 — members rotate run-to-run).

## Honest gaps / follow-ups

- The client-side *input* to issue a pickup (a pod player targeting a friendly
  and pressing the command key) is not bound in the MonoGame client this task;
  the server/command path it drives is implemented and tested. Binding it is a
  small client follow-up (the existing `SendOrderChange` pickup path is ready).
- A **client-driven** peer (real player or MCP bot) is never server-
  autopiloted by design (the port's `CHATBUOY` rule: the controller owns its
  flight). The proactive pass therefore diverts **AI-driven (`IsNpc`)**
  ships autonomously; a client-driven peer receives the c_cidPickup order /
  `PickupTarget` and its MCP/controller decides to comply. Full MCP-controller
  diversion is an MCP-side follow-up.
- No real-window 2560×1440 client screenshot was captured for this task (no
  visual changes were made — this is server/AI/command logic); the proof is the
  in-process live-network suite, the same harness every prior P-series live
  proof in this repo uses.
