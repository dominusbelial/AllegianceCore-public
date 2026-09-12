# ORDER_CHANGE(93) — P150 (Stage 4: "return to base")

Task: command a FLYING (undocked) NPC ship to return to base — the second of
the three docked-vs-flying sub-branches of native `HandleOrderChange`, wired
into the same `HandleOrderChange` handler P148 built, consuming P149's one real
dependency (`Ship.FindNearestFriendlyStation`). Branch `master` at `8e13605`
(P149) going in.

---

## 1. Scoping: TWO named command slots, not the full 4-slot array — client send-site investigation

Wiring the return-to-base branch looked like it needed native's full 4-slot
`SetCommand` array (native `SetCommand(i, target, cid)` indexes
`m_commandTargets[i]`/`m_commandIDs[i]`, `src/Igc/shipIGC.h:721-731`, array
declared at `shipIGC.h:2631`). The branch's final store is
`pship->SetCommand(pfmOC->command, pmodel, pfmOC->commandID)`
(`src/FedSrv/FedSrv.CPP:7330`) — it writes to WHATEVER slot the client's
`command` field names.

Checking the real client's send sites resolves this: `FM_CS_ORDER_CHANGE` is
created in exactly **three** places in the client, and all three were read this
session (`src/WinTrek/WinTrek.cpp`):

- `WinTrek.cpp:3774` (`contextDockDrone`, the UI "dock/return" action) →
  `pfmOC->command = c_cmdAccepted` (3778), `commandID = c_cidGoto` (3782) when
  the drone is not stay-docked. **This is the trigger for "return to base".**
- `WinTrek.cpp:8838` (client-local autopilot target setter) →
  `pfmOC->command = c_cmdCurrent` (8842).
- `WinTrek.cpp:8903` → `pfmOC->command = c_cmdAccepted` (8907).

So the real client only ever sends `c_cmdAccepted` (1) or `c_cmdCurrent` (2) —
**never `c_cmdQueued` (0) or `c_cmdPlan` (3)** (those are server/AI-internal).
`c_cmdAccepted` is already the value this branch's trigger sends, and
`c_cmdCurrent` is already served by the existing P139 weapon-aim
`Ship.CommandTarget`. Therefore this task adds exactly **two new named slots**
— `Accepted` and `Queued` — not the full array, and does **not** build
`c_cmdPlan`. `Queued` is added only because the branch unconditionally CLEARS it
(`SetCommand(c_cmdQueued, NULL, c_cidNone)`, `FedSrv.CPP:7322`) and a future
`PickDefaultOrder` task will read it; nothing in this task's scope writes it as
a target.

## 2. The no-carrier-distinction simplification (documented, not dropped)

Native gates the non-carrier search on `pship->GetPilotType() != c_ptCarrier`
(`FedSrv.CPP:7307`), and the carrier arm does a shipyard-first search
(`c_sabmCapLand`, then `c_sabmRepair` fallback, `FedSrv.CPP:7312-7320`). This
port has **no carrier pilot-type/hull distinction at all** — `Ship.IsNpc` is a
single `bool` marker (P148), not a `PilotType` enum with drone archetypes — so
the `pilotType != c_ptCarrier` gate is **unreachable here**. The port always
takes the non-carrier path: a single
`target.FindNearestFriendlyStation(0x40)` call (`c_sabmRepair = 0x40`,
`Wopr/Wopr/Constants/StationAbilityBitMask.cs:16`). This is a stated, accepted
simplification — the carrier shipyard-first search is its own future slice
once/if a real carrier distinction is ever built. `Server.cs` documents this at
the call site and in the handler's doc comment.

## 3. How the `command`-field branch was handled — decoded-value switch, not hardcoded Accept

The final store targets the slot the client's decoded `command` field names
(`FedSrv.CPP:7330`). Rather than hardcoding `Accepted`, the port branches on
the decoded `req.Command` via a small `ApplyCommandSlot` helper
(`Server.cs`, added this task):

- `CmdAccepted` (1) → `Ship.AcceptedTarget`/`AcceptedCommandId`
- `CmdQueued` (0) → `Ship.QueuedTarget`/`QueuedCommandId`
- `CmdCurrent` (2) → **logged, not stored** (see below)
- anything else (incl. `c_cmdPlan`=3, out of scope) → **logged, not stored**

The `c_cmdCurrent` arm is logged-and-not-stored for a concrete, documented
reason: the port's `c_cmdCurrent` slot is `Ship.CommandTarget`, a `Model?` for
weapon-aim (P139), whereas stations in this port are `DataStationIGC` record
structs held in `Sector.Stations` (not `Model`s) — so a station target cannot be
represented in that slot. This is safe for every reachable case: the only send
site emitting `c_cmdCurrent` (`WinTrek.cpp:8842`) is a client-local autopilot
setter, and native's drone path is `DoNotExecute`-guarded (`FedSrv.CPP:7287`),
so no drone command round-trips into the generic non-drone fallback either. The
choice is exercised by a dedicated live test (below) proving the switch does
NOT misroute `c_cmdCurrent` into `Accepted`.

## 4. New `Ship` slots

Four new properties on `Ship` (added after `CommandTarget`, `Ship.cs`), each a
target + `short` command-id pair:

- `AcceptedTarget` (`DataStationIGC?`) + `AcceptedCommandId` (`short`,
  default `OrderChange.CidNone` = -1)
- `QueuedTarget` (`DataStationIGC?`) + `QueuedCommandId` (`short`,
  default `OrderChange.CidNone`)

**Typing deviation from the task brief, stated plainly:** the brief suggested
"a plain `Model?` target" matching `CommandTarget`. Stations are NOT `Model`s in
this port — they are `DataStationIGC` record structs (the exact return type of
`FindNearestFriendlyStation`, P149) held in `Sector.Stations`. So the new slots
are typed `DataStationIGC?`, the faithful representation of a station-targeted
command slot here; `Model?` would force a nonexistent Model wrapper. Documented
in the property XML comments.

## 5. `HandleOrderChange` extension — additive `else` on the `IsNpc` gate

Added after P148's stay-docked branch, leaving it intact. When `target.IsNpc`
AND `target.StationId is null` (flying — the sibling of Stage 2's docked-only
handling), the port mirrors the native `else` arm (`FedSrv.CPP:7304-7332`):

1. `StayDocked = true` (unconditional — `FedSrv.CPP:7321`);
2. clear the Queued slot (`QueuedTarget = null`, `QueuedCommandId = CidNone` —
   `SetCommand(c_cmdQueued, NULL, c_cidNone)`, `FedSrv.CPP:7322`);
3. `station = target.FindNearestFriendlyStation(0x40)` (P149; the port's always-
   non-carrier path);
4. only if `station` found — `ApplyCommandSlot(target, req.Command, station,
   req.CommandId)` (the `if (pmodel)` gate, `FedSrv.CPP:7323-7331`).

There is **no `commandID` gate** in this branch — confirmed by reading native:
the `else` arm never reads `pfmOC->commandID` (only the docked/stay-docked gate
at `FedSrv.CPP:7290` does). Stated in the code comment.

The enclosing gate (`STAGE_STARTED`, target resolution via mission-wide
`FindSameSideShip`) is exactly what P148's `HandleOrderChange` already does —
this task only extends the same handler; it does not rebuild the dispatch.

## 6. Explicit exclusions (accepted, documented — out of scope)

- **Launch** (docked, non-stay-docked) — needs `PickDefaultOrder`, confirmed
  far larger (Stage 2's own report). Still log-and-no-op.
- **Carrier shipyard-first search** — no carrier pilot-type distinction in this
  port (Section 2).
- **`c_cmdPlan`, `PickDefaultOrder`, `c_ttAnyCluster`** — all future stages.
  `c_ttAnyCluster`'s cross-sector warp BFS was already deferred by P149.
- **Chat acknowledgments** (stretch goal) — native sends
  `SendChat(... CHAT_INDIVIDUAL ...)` "Affirmative." + a team line
  (`FedSrv.CPP:7325-7328`); no reusable "server chats as ship X" helper exists
  (same precedent as P148). The state mutation stands alone as the proof point.
- **Generic `SetCommand` fallback for non-NPC targets** (`FedSrv.CPP:7335-7339`)
  — still needs the full 4-slot array; out of scope.
- No change to P103-P149 code beyond `HandleOrderChange`'s extension, the two
  slot-pairs on `Ship`, the new `OrderChange` slot/id constants, and the `Bot`
  helper overload.

## 7. Verification

- No VM. A real loopback `Bot`/in-process-server live test
  (`tests/Allegiance.Core.Tests/OrderChangeReturnToBaseLiveTests.cs`) boots a
  real connected bot (sector 1051), spawns an undocked NPC into the no-station
  fly sector 1052 (P111), places a real-type friendly repair-capable station
  there via the public `Mission.PlaceStation` seam, and issues `ORDER_CHANGE`
  (command slot `c_cmdAccepted`, commandID `c_cidGoto`). Asserts server-side:
  `StayDocked == true`, `QueuedTarget == null`, `QueuedCommandId == CidNone`,
  `AcceptedTarget` resolves to the placed station (StationId 5101) with the
  decoded `commandID` (5) stored, and the NPC remains undocked.
- Second test: no qualifying station in sector → `StayDocked`/Queued still
  update but `Accepted` stays unset (native's `if (pmodel)` gate, `FedSrv.CPP:7323`).
- Third test: `c_cmdCurrent` slot → neither `Accepted` nor the Model?-typed
  `CommandTarget` is written (proof the decoded-command switch is real and does
  not hardcode `Accepted`).
- Full existing suite passes unmodified (see 3-consecutive-run result below).
- Committed only after build + test clean on 3 consecutive runs.

Baseline: P149 checkpoint was 2734 tests going in; this task adds 3 →
(see full-suite totals recorded in the run logs).
