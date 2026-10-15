# P414 — ripcord request key (full protocol + server state machine exist; client never sends it)

## Why this task exists

Same key-binding survey as P413. This is the bigger of the two orphaned
mechanics: native's `TK_Ripcord` ("Ripcord (teleport)") has a complete,
already-implemented server-side state machine (P111-P113, P172, and
P405's countdown maneuver/thrust freeze, all this session) — but nothing
in the client ever sends the request message that starts it.

## Confirmed real mechanic, already working server-side

`FM_C_RIPCORD_REQUEST` (184) — `src/Allegiance.Core/Wire/
RipcordRequest.cs`: one field, `SidRipcord` (the target sector to
escape to, or `NA`/-1 to cancel an active countdown). Server dispatch:
`RequestRipcordCore` (`Server.Docking.cs`, port of
`FedSrvSiteBase::RequestRipcord`, `FedSrv.CPP:13319-13378`) — a full
state machine: naming a real target (re)activates the countdown
(`RipcordActivate`/`RipcordDenied` response), naming `NA` cancels
(`RipcordAborted` response). The countdown itself, its per-tick
revalidation, the P405 maneuver/thrust freeze, and the teleport are all
already correct and tested.

**Confirmed gap, both directions**:
- **Outbound**: nothing in `Allegiance.ClientCore`/
  `Allegiance.ClientCore.MonoGame` ever constructs or sends a
  `RipcordRequest`.
- **Inbound**: nothing in the client decodes or reacts to
  `RipcordActivate`/`RipcordDenied`/`RipcordAborted` either (grepped,
  zero real code hits — only a pre-existing "ripcord aborted" sound
  asset already loaded in the audio content pipeline, unused by any
  handler).

## A real, already-built integration point to reuse

P403 (buoy-targeted starmap "goto" command, landed) already added
sector-selection state to the MonoGame client — `_selectedSectorId`,
used when the minimap/starmap is expanded
(`Game1.FlightInput.cs`, the `_minimapExpanded && _selectedSectorId is
{ } gotoCluster` branch that currently calls `IssueStarmapGoto`).
**Reuse this exact selection mechanism for ripcord's target sector** —
native's real player flow is "pick a destination, then ripcord there,"
which is precisely what a selected starmap sector already represents in
this port. Don't build a second, parallel sector-picking UI.

## Scope

1. Add a key (check `KeyBindingTable.cs` for what's free) that, when a
   sector is selected on the expanded starmap (reusing P403's
   `_selectedSectorId`), sends a real `RipcordRequest` naming that
   sector. Decide and document plainly what happens if the key is
   pressed with no sector selected (send `NA`/cancel? no-op? — native's
   own semantics for an ambiguous request are worth checking in
   `RequestRipcordCore` before deciding).
2. Add cancel: pressing the same key (or a documented alternate/modifier)
   while a ripcord is already active sends `SidRipcord = NA` to cancel —
   check `ship.RipcordModel`-equivalent client-side state (or infer from
   the last `RipcordActivate` received) to know whether one is active.
3. Decode and handle the three response messages client-side:
   `RipcordActivate` (start/refresh the countdown — surface it in the
   HUD somehow, matching whatever this port's convention is for other
   timed states), `RipcordDenied` (feedback that the request failed —
   reuse whatever notification-text convention already exists for
   other denied actions), `RipcordAborted` (countdown cancelled — this
   is exactly where the already-loaded "ripcord aborted" sound cue
   belongs; wire it in).

## Explicitly out of scope

- Any change to the server-side ripcord state machine, countdown,
  revalidation, or teleport — already correct (P111-P113, P172, P405),
  confirmed working, don't touch.
- Any new HUD countdown-timer widget beyond a reasonable, simple
  reuse of existing notification/text conventions — if a full visual
  countdown ring/bar seems clearly warranted, note it plainly as a
  judgment call rather than over-building speculatively.
- P368-P413 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: select a sector on the starmap, press the
  new key, and confirm server logs show a real `RIPCORD_REQUEST`
  arriving and the countdown starting (`RipcordActivate` received
  client-side); confirm the ship actually freezes maneuvering/thrust
  during the countdown (P405) and teleports on completion.
- **Live proof, required**: press the key with no valid target and
  confirm `RipcordDenied` is received and surfaced, not silently
  dropped.
- **Live proof, required**: cancel an active countdown and confirm
  `RipcordAborted` is received and its sound cue plays.
- A test proving the outbound message encodes the selected sector id
  correctly.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P414_RIPCORD_REQUEST_KEY.md` in the repo root: the key
   chosen, the no-selection/cancel semantics decided, the response
   handling added, and the verification performed.
