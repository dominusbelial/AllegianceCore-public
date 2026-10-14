# P507 — native own-ship model, part 1 (server): accept the client's reported pose (ProcessShipUpdate) + SHIP_RESET + cookie

## Why this task exists

**Domingo's live playtest (2026-10-03):** prediction stutters, and without it
flight feels sluggish, even alone on a LAN with 0.9 ms RTT. **Decision: follow
native's pattern.**

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

## Scope (server side + wire)

1. **Cookie.** Store a per-player cookie, sent in the join/LOGONACK path the
   way native does. Check what native sends and where; the port currently
   has `Cookie = 0` and "no per-peer cookie is stored",
   `Server.Combat.cs:~2122`. Ignore `SHIP_UPDATE`s whose cookie doesn't
   match.
2. **`ProcessShipUpdate` port.** For the peer's own ship (in a cluster,
   no parent), apply native's rules exactly:
   - the out-of-date window;
   - `LegalPosition`;
   - advance the client pose to server time with the port's flight step,
     `CalculateShip` (reuse `ShipKinematics`, P448a, not a second integrator);
   - **always accept orientation**;
   - accept position and velocity within 20 m.

   Always also apply controls and stateM, as today. Keep the existing P180
   NotStarted gate. Between updates, the server keeps simulating the ship
   with the last controls, as it does now.
3. **`SHIP_RESET` (183)** codec (native layout) and send-on-reject, with a new
   cookie and the server's exported pose. Remove it from
   `NotSupportedFedMsgs`.
4. **Backward compatibility while P508 lands:** a client that still sends
   `Time = 0` and a zero pose must keep working, controls-only, as today.
   Detect it (for example a zero/invalid pose or time) and fall back to the
   current behaviour, documented. Test bots (`Allegiance.Bot`, MCP, vizfight)
   use the controls-only path and must keep working.
5. **Logging:** a per-peer acceptance summary (accepted, rejected,
   out-of-date and invalid counts, plus mean reject distance) about every 10 s,
   so the live test can see it working.

## Explicitly out of scope

- The client sending its pose and handling `SHIP_RESET` (**P508**, next).
- Changing broadcasts to other peers.
- Cheat detection beyond native's rules.

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
2. The report `docs/reports/P507_NATIVE_OWN_SHIP_SERVER.md`.
