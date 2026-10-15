# P508 — native own-ship model, part 2 (client): fly the ship locally, report pose every 0.15 s, apply SHIP_RESET; retire the predictor

## Why this task exists

**Part 2 of following native's own-ship model** (Domingo's direction,
2026-10-03). P507 made the server accept a client-reported pose within 20 m,
send `SHIP_RESET` with a new cookie on rejection, and keep a controls-only
fallback. Check that P507's commit exists (`git log --oneline | grep P507`).
If it doesn't, **stop and report**.

**Native's model, which Domingo directs us to follow; it was very efficient.**
The client **flies its own ship locally and tells the server where it is**.
The server only sanity-checks. Native is at `~/Development/Allegiance/src/`.
- **Client send** (`clintlib/clintlib.cpp:~1830-1870`): every
  **`c_dsecUpdateClient` = 0.15 s** (`Inc/Messages.h:226`, about 6.7 Hz),
  **non-guaranteed**, it sends `FM_C_SHIP_UPDATE`
  (`Inc/Messages.h:227-231`: `timeUpdate`, `cookie`, `ClientShipUpdate
  shipupdate`).
  - The payload is filled by `m_ship->ExportShipUpdate(...)`
    (`Igc/shipIGC.h:1173+`): time converted with `ServerTimeFromClientTime`,
    **position**, controls and stateM (`SetSC`), and
    orientation/velocity/turn rates/power (`SetOVTP`).
  - The current `m_cookie` goes with it.
- **Server receive** (`FedSrv/FedSrv.CPP:4851-4864`): the update is ignored
  unless `pfsPlayer->GetCookie() == pfmShipUpdate->cookie`, and only applied
  when the ship is in a cluster with no parent ship. Then
  `CFSPlayer::SetShipUpdate` (`FedSrv/fsship.cpp:1173-1200+`) drops anything
  older than the last accepted update and calls
  `CshipIGC::ProcessShipUpdate` (`Igc/shipIGC.cpp:3139-3210`):
  - out of date if `|lastUpdate − su.time| ≥ 1.5 s` (`c_susOutOfDate`);
  - `LegalPosition` check, otherwise `c_susInvalid`;
  - `CalculateShip(su.time, deltaT, &position, &velocity, &orientation)`
    advances the client's pose to server time;
  - **orientation is always accepted**;
  - **position and velocity are accepted if within 20 m** (`d < 400`) →
    `c_susAccepted`; otherwise `c_susRejected`.
- **On rejection**, the server sends **`FM_S_SHIP_RESET`** with its own
  `ExportShipUpdate` and a **`NewCookie()`**. Updates are then ignored until
  the client applies the reset and echoes the new cookie.

**The port today throws the pose away:**
- The **wire format already matches native**: `src/Allegiance.Core/Wire/ClientShipUpdate.cs`
  is the 39-byte `ClientShipUpdate` (`igc.h:2457`), and `ShipUpdate.cs` is
  fmid 24 with `Time`/`Cookie`.
- The client's `ConnectionSession.CommandControls`
  (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:~145-154`) sends
  **`Time = 0`, controls only, zero pose**, every frame.
- The server's `ShipUpdate` dispatch (`src/Allegiance.Server/Server.Dispatch.cs:~120-150`)
  sets Controls/StateM only: "the payload's position/velocity/orientation
  echoes are ignored as authority".
- `SHIP_RESET` (183) is listed as not supported
  (`src/Allegiance.Core/Wire/NotSupportedFedMsgs.cs:54`).
- The port bolted a server-authoritative predictor onto this (P447/P502/P505).
  Live, it still sits 15 m off with constant snaps
  (`~/monogame-client-run-p505-live.log`), and with prediction off the ship
  only moves in 20 Hz server steps, which feels sluggish. **Domingo rejected
  raising the server tick to 60 Hz**: it slows flying for everyone. This
  divergence from native is the root cause of both symptoms.

## Scope (client side)

1. **The client simulates its own ship locally every frame**, with the port's
   flight step (`ShipKinematics`, P448a) and the side-GA-scaled hull stats
   (`Game1.Prediction.cs` already resolves these). The rendered own-ship
   pose **is** that local simulation, at full frame rate, with no
   reconciliation, blending or snap. This replaces the P447/P502/P505
   `OwnShipPredictor` for the own ship. Retire it and its `[predict]`
   reconciliation. Make `--noPrediction` either a no-op, or keep it as a
   debug switch that renders the raw server pose, and document which.
2. **Send like native:** every **0.15 s** (`c_dsecUpdateClient`), send
   `SHIP_UPDATE` with the real `ClientShipUpdate`:
   - server-converted time (`ServerTimeFromClientTime`; reuse the server-clock
     estimate P464 built for turret updates);
   - position, orientation, velocity, turn rates, power, stateM and controls;
   - the current cookie.

   Controls must still reach the server promptly. Check whether native also
   sends on control changes (`smSendUpdateNow` is commented out natively) and
   match native. Note that the port's transport is TCP only, unlike native's
   non-guaranteed send; report any effect.
3. **`SHIP_RESET` handling:** snap the local simulation to the server's pose,
   adopt the new cookie, and resume sending.
4. **Transitions:** dock, undock, launch, warp, ripcord, death and respawn
   re-seed the local simulation from the server pose (what the predictor's
   freeze/resume edges did); reuse them.
5. **A live test, required (not just headless; two headless-only prediction
   fixes have already failed live).** Run the real desktop client against a
   real server with P507 (a local `Allegiance.Server` is fine), flying:
   cruise, hard turns, **throttle cut to −0.8 then decel** (P505's case),
   undock, warp. Report the server's acceptance stats (P507's log) — the
   target is **~0 rejections in normal flight** — and the client's
   reset count.

## Explicitly out of scope

- The server acceptance rules (P507).
- Remote-ship dead reckoning (P448b).
- Android-specific changes, beyond the shared library compiling and working
  the same.

## Verification

- Failing-first tests where applicable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`. Two of the
  last eight delegate sessions ended without committing.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P508_NATIVE_OWN_SHIP_CLIENT.md`.
