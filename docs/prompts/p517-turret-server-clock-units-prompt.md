# P517 — turret gunner updates use a mis-scaled server clock (tick index read as seconds) → probably always rejected; switch to the PING-based ms clock

## Why this task exists

P508 (`79384c14`, `docs/reports/P508_NATIVE_OWN_SHIP_CLIENT.md` §2,
"Server-converted time — the P464-estimator units trap") found a **real latent
bug** in the turret-gunner path.

- **The server's time gate** for a turret gunner's
  `ACTIVE_TURRET_UPDATE` is the port of native
  `CshipIGC::ProcessShipUpdate(const ClientActiveTurretUpdate&)`
  (`src/Allegiance.Server/Server.ShipCommands.cs:~170-200`). It drops anything
  outside **±1.5 s** of server sim time (`c_susOutOfDate`). Native is at
  `~/Development/Allegiance/src/`; see `FedSrv/fsship.cpp:~1205-1215` for the
  turret path.
- **The client's timestamp comes from P464's `ServerClockEstimator`**
  (`src/Allegiance.ClientCore.Game/Flight/ServerClockEstimator.cs`, used at
  `Game1.TurretGunner.cs:~21-22`). It treats `ServerShipUpdate.Time` as
  **seconds**, but that slot is the server **tick index**:
  `TickIndex = _loopIterations`, confirmed at `Server.Broadcast.cs:472` and
  noted at `ShipDeadReckoner.cs:128`. P464's own test feeds seconds and
  expects seconds, so it never caught this.
- **The result** is a timestamp roughly 20× off (the 20 Hz tick rate), so
  gunner updates are **likely always rejected as out of date**. A live
  gunner can't aim or fire properly. Verify this; don't assume it.
- **P508's fix for the own ship** is `OwnShipServerClock`
  (`src/Allegiance.ClientCore/Flight/OwnShipServerClock.cs`). It's fed by
  `FM_CS_PING(35)` echoes, where the server stamps
  `timeServer = (uint)(SimNow * 1000)` (`Server.Dispatch.cs`, native
  `FedSrv.CPP:4897-4911`). That gives server sim **milliseconds**,
  independent of the tick rate, through a least-squares fit.

## Scope

1. **Prove the bug live first.** Using P464's existing live turret test setup,
   or a minimal one: a gunner boards a turret, sends active-turret updates,
   and the server's gate decision is logged. Show updates are rejected as
   out of date today.
2. **Fix it:** the turret path uses the **same** PING-based server clock as
   the own ship (share `OwnShipServerClock`; no third clock implementation),
   in the units the server gate expects. Retire `ServerClockEstimator`, or
   fix its units and give it a test with **tick-index** input. Make sure no
   other caller depends on the wrong units; grep for all uses.
3. **Live proof:** the gunner's updates are **accepted**, the turret aims,
   and turret fire produces real projectiles and hits (P464's fire path). Show
   accepted versus rejected counts before and after.

## Explicitly out of scope

- Relaying a gunner's aim to **other** players. The server stores no
  `ClientActiveTurretUpdate` to relay (`Server.Broadcast.cs:~1711`, the P137
  deferral). Name it as a follow-up in the report.
- The own-ship model (P507/P508).

## Verification

- A failing-first test with tick-index input (the old estimator gives the
  wrong time), plus the live accept-count test.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P517_TURRET_SERVER_CLOCK.md`.
