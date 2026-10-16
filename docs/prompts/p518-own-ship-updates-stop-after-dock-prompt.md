# P518 — native own-ship model breaks after the first dock: client SHIP_UPDATEs stop being processed → can't dock/undock, ship "flies through" stations

## Why this task exists

**Domingo's live playtest of P507+P508 on 221 (2026-10-03):** "the lite
interceptor is unable to dock/undock … neither the scout was able to dock, it was
flying through". It's not hull-specific. Domingo has been rolled back to the
pre-P508 client (`9bd9ae8d` + `--noPrediction`, controls-only via P507's
fallback) until this is fixed.

**Evidence:**
- **Server**, 221's `[ctrlak] peer 1 ship-update summary`, which is
  **cumulative**: `accepted` climbed 9 → 76 → 142 → 209 → 275 → 342 → 404
  during the first flight, then the first dock
  (`[dock] ship 8098 docked at station 4328`). From that moment it stays
  **frozen at `accepted=404 rejected=1 out-of-date=0 invalid=0`** for every
  later summary, through a Lt Interceptor purchase + undock, a dock, a Scout
  purchase + undock, and over 10 minutes of flight.
  - So after the first dock the server **processed none** of the client's
    updates: no accept, no reject, nothing counted.
  - Its copy of the ship stays where it was, so it never docks again and
    others would see the ship parked, while the client's local sim flies
    freely through station meshes (it has no station collision).
- **Client log:** `~/monogame-client-run-p508-dockbug.log`. Note `[sim] hull
  reconfigured (respawn/lifepod/hull-swap)` after each launch, and no
  `SHIP_RESET` after the first.

**Candidates (verify, don't assume):**
1. **The client stops sending.** `MaybeSendOwnShipUpdate`
   (`src/Allegiance.ClientCore.Game/Game1.Prediction.cs:~163-178`) returns
   without sending unless `_ownShipSendArmed`, `!_ownSim.Frozen`, and
   `_ownServerClock.Ready` all hold. Docking is a step-5 **freeze** edge
   (`Game1.Prediction.cs:~95-130`). If the resume never fires after undock
   (for example a StationId/NA comparison or state mismatch, or the hull
   reconfigure path), or `_ownShipSendArmed` is never re-armed, the client
   goes silent.
2. **Cookie mismatch.** P507 drops a mismatched-cookie `SHIP_UPDATE`
   **before counting it**. The client re-reads `mine.Cookie` every frame
   (`:~75`). If dock, undock or the hull swap re-creates or resets the own
   `ShipEntity`, or the server issues a new cookie at dock/launch that the
   client never receives (P507 delivers it in `SET_CLUSTER` at join/warp and
   in `SHIP_RESET`), every update would be silently dropped. Check what
   **native** does with the cookie and with `SET_CLUSTER` on dock and launch.
   Native is at `~/Development/Allegiance/src/`; see `FedSrv.CPP`
   dock/launch handling and `NewCookie`.
3. **Server-side state:** after undock the ship isn't "in a cluster with no
   parent" in the P507 gate's view, so updates are skipped without being
   counted.

**Why P508's live test missed it:** `NativeOwnShipFlightLiveTests` spawned
the ship flying free and **never docked**. P508's report says undock/warp
were proven at unit level only.

## Scope

1. **Instrument before fixing.** P507's summary must also count **dropped**
   updates by reason: cookie mismatch, ship not in cluster or docked, and
   anything else. A silent drop is what hid this bug. The client should log
   when it stops or resumes sending, and why.
2. **Reproduce live:** a real `ConnectionSession` client with the P508 sim
   against a real P507 server. Undock, fly, **dock**, buy a different hull,
   **undock**, fly back and **dock again**, then warp. Show updates stop
   being processed after the first dock today (failing first).
3. **Fix the root cause or causes,** matching native's cookie and
   `SET_CLUSTER` behaviour on dock/launch, and the freeze/resume edges.
4. **Live proof:** the same sequence shows `accepted` keep climbing after
   every dock/undock, **the ship docks on the server every time**, no silent
   drops, and ~0 rejections. Include a hull swap and a warp in the sequence.

## Explicitly out of scope

- The P507 acceptance rules, unless step 2 proves them wrong against native.
- The turret clock (P517).

## Verification

- The failing-first live dock/undock/re-dock sequence test.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P518_OWN_SHIP_AFTER_DOCK.md`.
