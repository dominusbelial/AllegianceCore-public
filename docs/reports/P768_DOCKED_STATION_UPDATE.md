# P768 / SIM-0144 — Station health to docked players

## Outcome

**Passed as-is.** No production (`src/`) code was changed. The port already
satisfies the native rule.

## Native rule

`FedSrv/FedSrv.CPP:8247-8273` (`UpdateMissions`, `STAGE_STARTED`) sends every
docked **player** (`pship->GetStation() != NULL && pfsship->IsPlayer()`) a
single-station `STATIONS_UPDATE` carrying `stationID`, `bpHullFraction`, and
`bpShieldFraction`, gated by `timeStart - timLastStationUpdate >
dtimStationUpdateInterval` where `dtimStationUpdateInterval = 0.75f`
(`FedSrv/FedSrv.CPP:8162`). The native bug that resets `timLastStationUpdate`
every outer tick at `:8296` was **not reproduced**.

## Port behaviour verified

`src/Allegiance.Server/Server.Broadcast.cs:2294` (`BroadcastStationsUpdate`)
builds one per-side `STATIONS_UPDATE(57)` batch from the stations the side
sees (`st.SeenBySide(sideId)`) and sends it every
`Sector.c_nPassesPerUpdate` ticks. At the test server's configured
`UpdatesPerSecond = 20`, the cadence is:

```
10 ticks / 20 Hz = 0.50 s <= 0.75 s
```

The measured delivery in the new tests was **9 ticks = 0.45 s**; the
worst-case delivery is the cadence itself, **0.50 s** (worked out from
`c_nPassesPerUpdate` at 20 Hz, not measured).

### Seen-by-side cases

1. **Normal case** — the docked station is seen by its own side. The test
   docks a player at a friendly synthetic station, mutates its hull/shield
   fractions, and asserts the bot receives a `STATIONS_UPDATE` with the new
   fractions within the 0.75 s budget.

2. **"Could be unseen" case** — a side's own station can only be unseen if
   the P132 visibility sweep has not yet run. Reading
   `Sector.ResolveCrossSideVisibility` (`src/Allegiance.Core/Model/Sector.cs`)
   shows that the sweep always sets the station's own side to visible
   (`t.Body.SideVisibility.SetSideVisibility(own, true)`), independent of
   scanner range or other units. The second test uses a station with
   `ScannerRange = 0`, waits for the visibility sweep, confirms the station
   is still `SeenBySide`, and verifies the update still arrives on the same
   cadence.

The tests cover a player docked at its own side's station; for that case the
own-side rule guarantees the station is in the per-side batch. No dedicated one-station message is required.

## Tests

New tests in `tests/Allegiance.Core.Tests/DockedStationUpdateLiveTests.cs`:

- `Docked_player_receives_seen_own_station_update_within_native_cadence`
- `Docked_player_receives_own_station_update_when_own_station_has_no_scanner`

Existing station-update tests continue to pass:
`tests/Allegiance.Core.Tests/StationsUpdateLiveTests.cs`.

## Verification runs

- Build: 0 warnings, 0 errors (`Release`).
- New tests: 3/3 alone.
- Existing `StationsUpdateLiveTests`: 3/3 alone.
- One fast-tier run: `tools/run-tests.sh --tier fast`.
  - Core.Tests passed all 4963 fast tests.
  - ClientCore.Tests failed one unrelated test:
    `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`.
    That test passed 3/3 when run alone, so it is recorded as a load flake
    per `docs/TEST_FLAKES.md`.

## Evidence

Raw fast-tier logs and summary are outside the repo:
`~/allegiance-evidence/P768/`.

## Claude review fixups

- Case 1 now keeps the base type's scanner range (it was zeroed for both cases, so Case 1 duplicated Case 2).
- Arrival on the last allowed tick (exactly 0.75 s) now passes; the loop records `arrived` instead of inferring it from `ticks < maxTicks`.
- Native reset line corrected to `FedSrv.CPP:8296`.
- Dropped the uncited "docking is friendly-only" claim; the 0.50 s worst case is marked as derived, not measured.
