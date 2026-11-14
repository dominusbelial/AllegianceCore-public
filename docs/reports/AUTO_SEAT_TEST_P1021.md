# AUTO_SEAT_TEST_P1021 — the P763 side-select auto-seat live test

## Finding

`LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` failed because **the test read
the wrong clock**. The auto-seat timer, the sim clock and the lobby flow are correct; no product behaviour changed.

The test took `server.SimNow` at the moment the bot's receive thread surfaced `SET_CLUSTER` and treated that as the
seat time. Its pump runs one fixed 50 ms sim tick per roughly 1.2 ms of wall-clock, about 40 sim-seconds per real
second. The join burst takes 9–60 ms of real time to reach the client and be decoded, and at that rate the delay
reads as 0.35–2.65 extra sim-"seconds". Anything over 1 s of that pushed `elapsed` past the [28, 31] window.

Reproduced alone on the dispatch base (`a4c68e09`), 10 runs: **6 pass / 4 fail**, all four the same signature,
`Assert.InRange() Failure: Value not in range (28 - 31)`, actual **31.25, 31.40, 32.25, 32.40**.

A second 10-run cohort with temporary tracing (since removed) recorded both clocks:

| Run | Logon (sim s) | Server seated (sim s) | Seat − logon | Client saw it (sim s) | Old `elapsed` | Delivery (wall ms) | Old verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 0.05 | 30.05 | 30.000 | 30.45 | 30.20 | 11.7 | pass |
| 2 | 0.05 | 30.05 | 30.000 | 30.40 | 30.10 | 9.4 | pass |
| 3 | 0.00 | 30.00 | 30.000 | 30.90 | 30.70 | 22.1 | pass |
| 4 | 0.00 | 30.00 | 30.000 | 32.30 | 32.10 | 52.2 | fail |
| 5 | 0.05 | 30.05 | 30.000 | 32.70 | 32.45 | 59.8 | fail |
| 6 | 0.00 | 30.00 | 30.000 | 32.65 | 32.40 | 60.3 | fail |
| 7 | 0.00 | 30.00 | 30.000 | 30.80 | 30.60 | 20.8 | pass |
| 8 | 0.00 | 30.00 | 30.000 | 30.40 | 30.20 | 10.9 | pass |
| 9 | 0.00 | 30.00 | 30.000 | 32.30 | 32.10 | 52.3 | fail |
| 10 | 0.00 | 30.00 | 30.000 | 31.55 | 31.35 | 36.4 | fail |

Expected seat time: 30 s after logon. Actual, on the server: **30.000 s in all 10 runs**. The pump cost 1.23–1.24 ms
per tick in every run.

The old test also had a second, smaller error in the other direction: `start` was read when the roster reached the
bot, 0.20–0.30 sim-seconds after the logon the deadline is measured from. The window's lower bound of 28 was slack
for that, so the test could not have caught a timer that fired up to two seconds early.

I did not investigate why delivery of the join burst varies between 9 and 60 ms. `TcpTransport` sets `NoDelay` on
both ends, so it is not Nagle; beyond that it is unexamined, and it is not a seat-time question.

## Native citations (re-grepped)

The flag is a port addition; native has no auto-seat timer. What the code path touches in native, unchanged here:

- `WinTrek/teamscreen.cpp:2964-2981` — the team screen sends `C POSITIONREQ` with `iSide = m_sideCurrent` when the
  player picks a side; nothing is sent on a timer.
- `Inc/Messages.h:320` — `DEFINE_FEDMSG(C, POSITIONREQ, 39)`, "client requests position on a side".
- `FedSrv/FedSrv.CPP:6658-6691` — `FM_C_POSITIONREQ`: `NA` picks a side (`PickNewSide`), then `RequestPosition`.
- `FedSrv/fsmission.cpp:4729-4774` — `CFSMission::RequestPosition`: check, then `AddPlayerToSide` or a join request.

With the flag off (`SideSelectAutoSeatSeconds` null, zero or negative — the default) the port seats only a recorded
`POSITIONREQ`, as native does. The flag-off theory in the same test class
(`Flag_off_keeps_a_silent_peer_pending_past_31_seconds_and_an_explicit_positionreq_still_seats_it`, 3 cases) still
pins that and was not changed.

## Root cause (file:line)

- `tests/Allegiance.Core.Tests/LiveSideSelectTests.cs`, pre-fix lines 187-192 — `PumpUntil(... b.ShipAssignment is
  not null ...)` followed by `elapsed = server.SimNow - start` and `Assert.InRange(elapsed, 28.0, 31.0)`: a
  client-thread event timed on a sim clock that the same loop advances at ~40× real time.
- Same file, pre-fix line 178 — `start = server.SimNow` taken after `ConnectPending` returns, not at logon.
- Same file, line 52 (`PumpUntil`) — `TickSim` then `Thread.Sleep(1)`; the source of the ~1.2 ms tick.
- Product code that behaves as designed: `src/Allegiance.Server/Server.Dispatch.cs:328` (the pending record takes
  `_simNow` at logon), `src/Allegiance.Server/Server.Broadcast.cs:132` (fixed step per tick) and `:208` (the
  side-select step), `src/Allegiance.Server/Server.Lobby.cs:5783` (`AdvanceSideSelection`; the deadline test is at
  `:5798`, `now - ConnectSimTime >= autoSeatSeconds`).

## Fix

Test only, plus one read-only seam. The test now proves more than before.

1. New internal seam `Server.TestPendingSideSelectConnectSimTime(name)` (`src/Allegiance.Server/Server.cs:1117`):
   returns the sim time a pending peer's deadline is measured from, or null if that peer is not pending. It reads
   existing state and changes nothing.
2. The test steps the server alone, with no sleep, until the server itself has a seated ship, checking after every
   tick that the peer is still pending with the same logon time. It then asserts, using the same subtraction the
   server uses:
   - the last tick that left the peer pending was **short of 30 s** after logon (not seated late);
   - the seating tick is **at or past 30 s** (not seated early);
   - the two are exactly one tick apart, so the seat is in **[30, 30 + one tick)** — against the old [28, 31];
   - the peer is no longer pending, and there is exactly one ship, on a real side.
3. Delivery is then checked separately: the bot receives `SET_CLUSTER` naming that ship id. This wait is bounded at
   10 s of wall-clock and is never compared with the sim clock.

The 800-tick bound on the seat is the one the test already had. The receipt wait changed from 100 sim ticks to 10 s
of wall-clock; that bound is on transport delivery only, and is the same fail-loud bound P1020 used. No tier was
moved.

**Does it still bite?** With the server deadline temporarily shifted one tick (`autoSeatSeconds - 0.05`, then
`+ 0.05`; both reverted), the test failed each time: "seated early: 29.95000000000029 s after logon" and "seated
late: still pending at 30.00000000000029 s after logon". The old test passed for any seat between roughly 28 and
30 s.

Also changed:

- `docs/TEST_FLAKES.md` — resolution section at the top; all 51 earlier mentions of the test annotated "Resolved by P1021".
- `docs/parity/parity-matrix.csv` — the `lobby` row "AFK / auto-seat handling / Timed automatic side assignment"
  records how the on-case is now pinned. Status and weight unchanged.
- `GLOSSARY.md` — "Server-clock deadline check (P1021)".

Not changed, noted for the reader: `Silent_peer_is_auto_assigned_after_the_side_select_timeout` in the same class
also waits on the bot's `ShipAssignment`, but only for the end state within 800 ticks (200 ticks, about 250 ms of
real time, after the seat). It makes no timing claim and has no ledger rows, so I left it.

## Tests

| Test | Alone |
| --- | --- |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **10/10 pass** twice (first cut, then the final build), 0.33–0.79 s each (was about 1 s) |
| `LiveSideSelectTests` (whole class, 8 cases) | 8/0/0 × 3 |

Before the fix, same worktree, alone: 6 pass / 4 fail of 10.

## Gate

| Step | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | 0 warnings, 0 errors |
| `tools/build-tools.sh` | all 140 tool projects built |
| Changed live test alone | 10/10 pass (Passed 1 / Failed 0 / Skipped 0 each) |
| Whole `LiveSideSelectTests` class alone | Passed 8 / Failed 0 / Skipped 0, three times |
| `scripts/parity-scorecard.py`, `tools/check-glossary-links.py` | both pass |
| One full fast suite (`tools/run-tests.sh --tier fast --no-build`, 7,200 s bound) | **FAIL overall**: 14,483 cases, 607.3 s — **Passed 14,473 / Failed 3 / Skipped 7**, plus 6 slow-Fast exceedances |

The task's test **passed in the sweep in 0.038 s**, as did the other seven cases of its class, so the pass criterion
— 10/10 alone and passing in the one full suite — is met. The suite as a whole is still red for reasons outside this
task:

- Failed: `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at` (maxLineDeviation=283.0),
  `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump(enabled: False,
  dilation: "off")` (14.90 outside [14.94, 15.06]) and `P941InputLayoutTests.Keymap_store_picks_the_layout_from_the_file`
  (expected Native, actual Port). All pass 3/3 alone.
- Slow-Fast (limit 11.804 s): `P796VariedLoadoutTests` 13.719 s, `SC2NaniteSupportTests` 15.526 s, `P814GunnerTests`
  14.835 s, `P1000CivProfileTests` 15.807 s, `P750NativeAsteroidTypesTests` 12.601 s, `P999CivStrategyTests`
  16.418 s. All pass 3/3 alone.

All nine are ledgered in the P1021 section of `docs/TEST_FLAKES.md`; I did not investigate their causes. The P660
failure is a clock-window assertion of the same general shape as this task's, and may deserve the same look.

Not run: `scripts/publish-guard.sh` (it needs push arguments; nothing was pushed) and
`tools/run-training-tests.sh` (no training change). The change adds no secrets, emails, home paths or media. No HUD
or UI change, so no screenshots. Docker is not installed and no service, port or env var was added. Nothing was
committed.

---

**Native intent.** A player who has joined a game chooses a side by asking for it, and the server seats only that
request. Native never seats a silent player on a timer; a player who does not choose simply waits.

**Modern approach.** The port keeps native's wait as the default and offers the 30 s auto-seat as an opt-in server
flag for rigs with unattended clients. The live test pins the flag to the exact sim tick on the server's own clock,
and checks the client's receipt as a separate delivery step, so it no longer depends on how fast a test loop spins.
