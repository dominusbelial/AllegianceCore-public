# P540c — revert P540b's EXPORT_WARP wire change: `destinationID` is the PARTNER WARP id (native), not a sector id. Fix the client side, which misread it

## Why this task exists

P540b (`0dc429dd`) changed `ExportWarp.FromWarp`
(`src/Allegiance.Core/Wire/ExportWarp.cs`) to overwrite the exported
`DestinationId` with the **resolved destination sector id**
(`rw.Destination.Id`), calling the raw value "a different id space". **That
breaks native wire parity:**

- Native `WarpDef` (`~/Development/Allegiance/src/Igc/igc.h:1722-1728`):
  `WarpID warpID; WarpID destinationID; …`. **`destinationID` is a warp id,
  the partner aleph.**
- The native client resolves it as a warp (`Igc/warpIGC.h:129-135`):
  `m_destination = GetMyMission()->GetWarp(m_warpDef.destinationID);`. The
  destination **sector** is the partner warp's own `clusterID`
  (`DataWarpIGC`, `igc.h:2506-2514`).
- With P540b's change, a native client or WOPR bot receiving EXPORT_WARP
  looks up a **warp whose id equals a sector id**: the wrong aleph or none.
  The raw value was never "another id space"; it's the warp-id space.

**The real bug was client-side:** the port's client treats `DestinationId`
as a sector:
- `Game1.FlightInput.cs:~2159-2169` `AllWarps(...)` feeds `w.DestinationId`
  into `StarmapGotoRouter.AllWarp` as the destination **sector** (P541's
  router and P540b's cross-sector autopilot both route over this).
- `src/Allegiance.ClientCore/State/SpectatorFrame.cs:166` exports
  `destSector = w.DestinationId` (same misreading).

P540b's live proof passed **because** the server now sends sector ids. It's
the classic circular case: the wire was bent to fit the client.

## Scope

1. **Revert the `ExportWarp.FromWarp` override.** Export the native
   `DestinationId` (partner warp id) unchanged. Update/replace the P540b
   `ExportWarpWireTests` case. Add a failing-first test: the exported
   `DestinationId` equals the partner warp's `WarpId`, and that partner's
   `ClusterId` is the destination sector.
2. **Client-side resolution, as native:** destination sector =
   `world.Warps[w.DestinationId].ClusterId` (the partner warp's sector).
   Apply it in `AllWarps` (`Game1.FlightInput.cs`), `SpectatorFrame.cs:166`
   and any other consumer (grep `DestinationId` across `src/` and `tools/`,
   including vizfight and allegiance-mcp's routing). If the partner warp
   isn't known yet, skip that edge. Never guess.
3. **Check the server side too:** confirm the server's own transit uses
   `ResolvedWarp.Destination` resolved through the partner warp, consistent
   with native. Cite the server code, and change nothing if it's already
   right.
4. **Keep** P540b's other fixes: the cross-sector goal, the eject cookie
   rotation, `AutopilotSteering.Pursue`. They're independent.

## Explicitly out of scope

- Any other autopilot behaviour.
- Warp visuals.

## Verification

- **Failing-first unit tests:** the wire round-trip keeps the partner warp id;
  the client resolver maps partner warp → sector; the router's next warp
  toward a target sector is unchanged on a real map (PCore/the server's map)
  after the fix.
- **Wire check against native:** decode the server's EXPORT_WARP bytes and
  show `destinationID` = the partner warp's `warpID` for at least 3 warps.
  Quote them.
- **Re-run P540b's live proof** (`P540bPodAutopilotCrossSectorLiveTests`) and
  P541's (`P541GotoFlyLiveTests`). Both must still pass with the native wire.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and proofs are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file**
  (`git status --untracked-files=all`). Confirm with `git log -1` and a
  clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P540c_WARP_DESTINATION_WIRE_PARITY.md`. Also add a
   dated correction note to `docs/reports/P540b_AUTOPILOT_CROSS_SECTOR.md`
   pointing here. Don't silently rewrite it.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
