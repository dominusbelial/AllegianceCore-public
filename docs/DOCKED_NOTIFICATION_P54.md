# P54 — Diagnose + fix the missing `FM_S_DOCKED` notification

## Status: IMPLEMENTED + PROVEN via loopback; pending Claude Code's live-VM pass

Closes the open question DOCKING_LIVE.md flagged: P53 made the *wire* say
"docked" (`SHIP_STATUS(90)` carries `StationId` + `State = c_ssDocked`),
yet the native client's own resolved object accessor `ship.GetStation()`
never resolved non-null. Root cause (now traced to closure in the reference
tree): the resolved reference is populated by a **dedicated message**,
`FM_S_DOCKED` (fmid 61), which AllegianceCore never sent. This change adds
the codec and the send site. Server-side, `AllegianceCore`-only — same shape
as the P51/P52/P53 arcs.

---

## The diagnosis (every claim carries `file:line` read in the reference tree)

### Why `ship.GetStation()` never resolved — and what makes it resolve

1. `IshipIGC::GetStation()` reads a plain member `m_station`
   (`src/Igc/shipIGC.h:417-420`). It returns whatever
   `SetStation()` last wrote.
2. The **only** call site anywhere in the client that calls
   `m_ship->SetStation(<really non-null station>)` is inside the client's
   `FM_S_DOCKED` handler (`src/clintlib/appmsg.cpp:1113`):
   `m_ship->SetStation(m_pCoreIGC->GetStation(pfmDocked->stationID));`.
   That is precisely why the dedicated message exists.
3. The reference server sends that message when a player docks. In
   `CFSPlayer::Dock` (`src/FedSrv/fsship.cpp:1027-1045`) it sets the passive
   `ShipStatus` fields **and separately** sends `FM_S_DOCKED` to the docking
   player ("Tell them what station they're at", fsship.cpp:1038-1042). In the
   wider dock event `FedSrvSiteBase::DockWithStationEvent`
   (`src/FedSrv/FedSrv.CPP:13087-13177`) it latches the station state and the
   `SHIP_STATUS` broadcast reflects it (`pfsShip->ShipStatusDocked`,
   FedSrv.CPP:13110).
4. Conclusion: P53 shipped the `SHIP_STATUS` broadcast (the wire *state*
   half); the missing piece is the `FM_S_DOCKED(61)` *notify* — the message
   whose client handler is the only producer of `m_station`. The raw
   `SHIP_STATUS` wire integers alone never populate `m_station` (the client's
   ship-status apply path reads them into a `ShipStatus` struct, not into
   `m_station`), which is exactly what DOCKING_LIVE.md observed
   (`ship.GetStation() == null` despite correct wire fields).

### Is the client-side assert (`appmsg.cpp:1114`) a blocker? No.

The handler ends with `assert (m_ship->GetCluster() == NULL)`. The task
worried this requires the docking player's ship to have been *previously*
cleared from its cluster. Reading the full call chain shows the opposite —
it is a **self-enforced post-condition**:

- `FM_S_DOCKED` handler runs `m_ship->SetStation(station)`
  (`appmsg.cpp:1113`).
- `ShipIGC::SetStation` (`shipIGC.h:421-485`) calls
  `station->AddShip(this)` (`shipIGC.h:457`, comment "This also sets the
  ship's cluster to NULL"), then `assert(GetCluster()==NULL)`
  (`shipIGC.h:459`).
- `CstationIGC::AddShip` (`stationIGC.cpp:280-285`) calls
  `pship->SetCluster(NULL)` (`stationIGC.cpp:284`, "At a station implies
  not in a cluster").

So by the time `assert(m_ship->GetCluster() == NULL)` runs at
`appmsg.cpp:1114`, line 1113 already set it NULL internally. The assert is
guaranteed to hold on every FM_S_DOCKED receipt, for a bot spawned directly
at a station or one that flew in — the cluster value *before* the message is
irrelevant. The two candidate explanations the task proposed are therefore
moot:

- a same-message self-directed `SHIP_DELETE`: refuted as before
  (`appmsg.cpp:462` skips own ship), and — decisively — not needed.
- client-local physics (`DockWithStationEvent`): irrelevant, because the
  client-side docked-transition is **message-driven**, not physics-driven.
  The graphical client's concrete `IIgcSite` is `BaseClient`
  (`clintlib.h:674-676`), which does **not** override
  `DockWithStationEvent`; it inherits the default `returns true` no-op
  (`src/Igc/igc.h:4857`), same as the WOPR wrapper
  (`Wopr/AllegianceInterop/igcWrapper.h`). Neither clears a cluster as a
  side effect — confirming this was never the mechanism.

### Assert is live in the real build — but can never fire

`FZDebug-NoYellowComm` is an **assert-enabled** build: it defines `_DEBUG`,
**not** `NDEBUG` (`AllegianceInterop.vcxproj:204,223`; contrast the Release
configs `:295,313` which define `NDEBUG`). So the assert at `appmsg.cpp:1114`
is compiled in and armed in the exact config this project deploys — but per
the call-chain above it cannot fail. (This is the honest answer to "check for
NDEBUG… don't assume": it is not defined, yet the assert is unreachable as a
failure because `SetStation` enforces the invariant on the very preceding
line.)

### Why implementing is safe — the full reference sequencing

The reference achieves "own ship leaves cluster before FM_S_DOCKED is
processed" *because the handler itself does it*, not before. There is no
second server message (docking triggers only FM_S_DOCKED to the player +
SHIP_STATUS broadcast + SHIP_DELETE to *others*); `CFSPlayer::Dock` sends no
more than the two messages P53 + this task already cover. So the fix needs
only: a `FM_S_DOCKED(61)` codec and a single send to the docking peer at the
dock transition — exactly what this change implements. The peer owns a ship
that was docked, so `ship.GetStation()` will resolve non-null.

---

## What is proven (verified via loopback, not fabricated)

Implemented, built clean, and proven with a loopback `Bot` test over real TCP
(`tests/Allegiance.Core.Tests/DockDockingTests.cs`):

1. **Codec correctness** — `FM_S_DOCKED`(61) round-trips and emits the exact
   6 declared bytes (`cbmsg=6, fmid=61, StationID short`; `Messages.h:448-450`).
2. **Sent exactly once per dock event** — the docked peer receives exactly
   one `FM_S_DOCKED(61)` carrying the real `StationId`; after an
   `FM_C_DOCKED(48)` undock + automatic re-dock it receives exactly one more.
   Never a stream — attributable to the transition, not a periodic broadcast.
   (Test: `FriendlyDock_sends_exactly_one_DockedNotify_per_event_with_correct_station`.)
3. **To the correct peer only** — routing mirrors `OnShipWarped`: the notify
   is sent to whichever peer owns the docked ship; a dock on a non-peer ship
   (pure-sim) is logged and skipped.
4. **No regression** — full suite clean: **2248 passed, 0 failed, 3
   consecutive runs** (P53 baseline was 2245/2245; +3 new P54 tests).

## Files changed

| File | Change |
|---|---|
| `src/Allegiance.Core/Wire/DockedNotify.cs` *(new)* | `FM_S_DOCKED`(61) codec, single `short StationId`, 6 bytes. |
| `src/Allegiance.Server/ServerHost.cs` | Added `ShipDocked` event + raised from `DockWithStationEvent` after latching `Ship.StationId`. |
| `src/Allegiance.Server/Server.cs` | Subscribed `ShipDocked` at boot; added `OnShipDocked` → sends `FM_S_DOCKED(61)` to the owning peer. |
| `src/Allegiance.Bot/Bot.cs` | Collects inbound `DockedNotify` (mirrors `ShipStatuses`). |
| `tests/Allegiance.Core.Tests/DockDockingTests.cs` | Codec round-trip/exact-bytes + loopback exactly-once test. |

---

## What Claude Code's own live-VM pass must check

Standing P4X→live-pass pattern (as for P51/P52/P53). On the Windows VM with
a real `Wopr.exe` client against this server:

1. **Does `ship.GetStation()` finally resolve non-null after a real dock?**
   Instrument the native client (as DOCKING_LIVE.md did) to log
   `ship.GetStation()->GetObjectID()` alongside the raw `wireStationID`
   field on `FM_S_DOCKED` receipt. Expectation: after this change, they
   agree, where P53's pass saw `station=null` while `wireStationID=1051`.
2. **Does the assert at `appmsg.cpp:1114` fire/crash?** Per the tracing above
   it cannot (SetStation→AddShip→SetCluster(NULL) runs on the immediately
   preceding line), but the live pass is the definitive check: watch for a
   native assert dialog or an `AH_ASSERT`/abort in the WOPR process when the
   first FM_S_DOCKED is delivered to a bot spawned directly at a station
   (matching the DOCKING_LIVE setup). If it does fire despite the tracing,
   stop and treat this fix as unsafe (stand on P48's refused-to-guess
   precedent). If it does not (expected), the fix stands.
3. **End-to-end payoff:** a `CommanderResearchAndExpand` bot whose
   `UndockMinersAndStartMining` gate (`CommanderResearchAndExpand.cs:1555`)
   keys on `ShipState.c_ssDocked`/`GetStation()` should finally engage after
   a real dock.

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py
20260902_133359_6800f4`:

    20260902_133359_6800f4  start=13:34:08  dur=STILL RUNNING / no clean end
    msgs=124 tool_calls=72 api_calls=52  tokens_in=4,125,214 tokens_out=30,756

(captured near the end of the run; "STILL RUNNING" is expected — this session
had not closed when the log was taken.)
