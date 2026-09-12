# BOARD_SHIP — P108: Phase 3 cluster E (part 1): BOARD_SHIP / BOARD_NACK / LEAVE_SHIP

Establishes the **foundational occupancy slice** of the gunnery/multi-crew
concept: a player abandons their own ship to occupy a turret mount (or
observer slot) on another player's ship. Turret aim/fire (gunner binding,
P109) and the periodic turret-state broadcast (P110) are deliberately out of
scope — this task is the **state-transition mechanism alone**.

Branch `master` at P107. The task's own ship-death interaction was checked
against what P103/P105 built (the LeaveShip hull-restore reuses the exact
side's-Lifepod resolution the P105 death-respawn uses). No Windows VM needed.

---

## The "no dedicated success ack" finding — confirmed with test evidence

The task's scoping win: **on a successful board, the native sends no dedicated
acknowledgement message at all.** Read in full:

- `FedSrv.CPP:6385-6418` (`case FM_C_BOARD_SHIP`): on success neither
  `BoardShip()` nor the internal `LeaveShip()` produces a wire ack — the only
  thing they do on success is call `ShipStatus->SetState(...)` /
  `SetParentID(...)` / `SetHullID(NA)` and then
  `g.fm.SendMessages(..., FM_GUARANTEED, FM_FLUSH)` to flush whatever
  `SHIP_STATUS` updates were already queued.
  - Success-path flush calls: `FedSrv.CPP:2461-2470` (board) and the
    `LeaveShip()` internal at `FedSrv.CPP:2312-2316`.
  - The **only** failure acknowledgement is `BOARD_NACK`(178), sent to the
    requester alone (`pfsPlayer->GetConnection()`, `FedSrv.CPP:6412-6416`).
- The port therefore introduces **no new success-ack codec** and sends no
  success message. The boarded/left state is reflected through this port's
  existing per-tick `SHIP_STATUS` machinery (`BroadcastShipStates` →
  `MakeOwnShipStatus`, the same per-tick refresh the P58/P62/P63 teammate-
  visibility work established). `MakeOwnShipStatus` now derives the boarded
  wire state (State=c_ssTurret/c_ssObserver, ParentID=carrier id, HullID=NA)
  directly from `Ship.ParentShip`/`Ship.TurretId`, so the existing periodic
  refresh broadcasts the change with **no new message**.

**Live test evidence** (`LiveBoardShipTests`):
- `Board_with_equipped_turret_and_leave`: after the successful board, asserts
  `boarder.AllFmids` contains **no** `BOARD_NACK.Fmid` and **no**
  `LeaveShip.Fmid`, and that the boarded state (State=c_ssTurret,
  ParentId=carrier, HullId=NA) arrived only via `SHIP_STATUS(90)`.
- `Board_without_equipped_turret_becomes_observer`: same negative — no
  `BOARD_NACK` — and the observer state arrived via `SHIP_STATUS(90)`.
- `Board_nonexistent_ship_nacks_only_requester` proves the corollary: the
  **failure** path DOES emit a real `BOARD_NACK(178)` naming the requested
  parent id, to the requester only (the same-side carrier peer got none).

Wire evidence for the failure ack is a real frame, not an invented one:
`FedSrv.CPP:6412-6416` (`BOARD_NACK`, `sidRequestedParent`). The native leaves
`sidRequestedParent` unset (the `pfmBoardNack->sidRequestedParent;` statement
at `FedSrv.CPP:6414` is a no-op); we populate it with the requested id (the
same "strictly more useful than garbage" rationale `EnterLifepod` documents).

---

## What landed

### Wire codecs (round-trip byte-exact, `Wire/BoardingWireTests.cs`)
- `Wire/BoardShip.cs` — `FM_C_BOARD_SHIP`(103), `ShipID sidParent`
  (dual-purpose: NA ⇒ leave, else board). 6 bytes.
- `Wire/BoardNack.cs` — `FM_S_BOARD_NACK`(178), `ShipID sidRequestedParent`.
  6 bytes.
- `Wire/LeaveShip.cs` — `FM_S_LEAVE_SHIP`(94), `ShipID sidChild`. 6 bytes.
All matched `Messages.h:656-658, 686-688, 913-915`. Added to the
`FedMessageCatalog`/`FedMessageDumper` ported set + formatters.

### Ship state (`Sim/Ship.cs`)
- `Ship.ParentShip` (nullable `Ship?`)
- `Ship.TurretId` (nullable `short?`; null = NA observer, a turret slot
  otherwise)
- `Ship.ChildShips` (read-only list) + `AddChildShip`/`RemoveChildShip`
  (idempotent), mirroring native `AddChildShip`/`DeleteChildShip`
  (`shipIGC.h:1470-1479`) and the parent↔child invariant of `SetParentShip`
  (`shipIGC.cpp:3645-3689`).

### Server handler (`Allegiance.Server/Server.cs`)
- Dispatch `fmid == BoardShip.Fmid` → `HandleBoardShip`.
- `HandleBoardShip` (`FedSrv.CPP:6385-6418`): dual-purpose NA/board, gated on
  mission `STAGE_STARTED`; `BOARD_NACK` on failure to the requester alone.
- `PerformBoard` (`FedSrv.CPP:2322-2475`): self-exclusion, carrier-is-a-player,
  both-docked precondition, turret-slot walk, collapsed observer gate, money
  refund, parent/child/turret state transition, dock inheritance.
- `PerformLeave` (`FedSrv.CPP:2277-2320`): the boarding-related `LEAVE_SHIP`
  sender confirmed at `FedSrv.CPP:2312-2316`; clears parent/turret, restores
  the side's Lifepod hull, broadcasts `LEAVE_SHIP(94)` to the whole side.
- `ChooseTurretSlot` (`FedSrv.CPP:2347-2402`): the turret-slot search, reused
  verbatim-shaped; occupancy from `ChildShips`, equipment from
  `MountedPart.MountId` (both already tracked).
- `MakeOwnShipStatus` extended to derive boarded wire state (no new message).

### Simplifications, stated plainly
- **Money refund** — native credits the player's individual money and sends
  `MONEY_CHANGE` (`FedSrv.CPP:2433-2442`); this port has no per-player wallet
  and no `MONEY_CHANGE` codec, so the refund (hull price + Σ part prices,
  `shipIGC.h:396-406`) is deposited into the boarder's **side treasury**
  (`Side.Money`) — the P51-59 money primitive — where payday distributes it.
- **Observer gate collapses** — native `(turret != NA) || CanCheat()`
  (`FedSrv.CPP:2406`); no cheat/admin concept, so per the P100/P103 precedent
  observer boarding is allowed unconditionally.
- **Turret aim/fire (gunner) binding deferred** — native
  `SetTurretID`→`SetGunner` (`shipIGC.h:1405-1437`) not reproduced. Occupancy
  only. **Natural next step: P109.**

---

## Verification summary

- 3 consecutive clean full-suite runs: **2447 tests pass** (2431 baseline + 16
  new), 0 warnings. The 16 new tests = 11 codec `[Fact]`s in
  `Wire/BoardingWireTests` + 5 `[Fact]`s in `LiveBoardShipTests` (4 live proofs
  + 1 pure-unit turret-slot walk). The 4 live tests were also run 10/10 in a
  tight loop after fixing a role-matching bug in the live harness (hull-based,
  not socket-order-based, identification).
- Scenario 1 — board equipped turret slot: `ParentShip`/`TurretId=2` set,
  boarder in carrier's `ChildShips`, money refunded (+500 to side treasury),
  and **no** `BOARD_NACK`/`LEAVE_SHIP`/ack — boarded state only via
  `SHIP_STATUS` (State=c_ssTurret, ParentId=carrier, HullId=NA). ✅
- Scenario 2 — no free equipped turret slot: boarder becomes observer
  (`TurretId` null), not rejected; wire `State=c_ssObserver`. ✅
- Scenario 3 — nonexistent ship: real `BOARD_NACK(178)` naming the requested
  parent id, to the requester only (same-side carrier peer got none). ✅
- Scenario 3b — cross-side target: same-side `GetShip` resolution ⇒ NACK. ✅
- Scenario 4 — leave (sidParent=NA while boarded): `ParentShip`/`TurretId`
  cleared, hull restored to the side's Lifepod, real `LEAVE_SHIP(94)`
  broadcast to the whole side (both peers) naming the correct child id. ✅
- Scenario 5 — all three codecs round-trip byte-exact. ✅
