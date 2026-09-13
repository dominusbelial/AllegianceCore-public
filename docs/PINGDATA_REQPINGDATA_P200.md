# P200 — PINGDATA(194) / REQPINGDATA(195): server-wide latency roster

## Summary

`REQPINGDATA` is the last item in the original "confirmed real but blocked"
bucket: a client asks the server for every connected player's ping/packet-loss,
and the server answers with one `PINGDATA` entry per player. This stage ports
native `FedSrv.CPP:7743-7808` (`case FM_C_REQPINGDATA`) plus the 5-second
debounce `CFMConnection::PingQuery` (`messagecore.h:217-223`).

Scope delivered: two new codecs, a per-connection debounce, the dispatch, a
bot accumulator, a dumper formatter, and 7 tests (suite 2994 → 3001).

## What changed

### New wire codecs
- `src/Allegiance.Core/Wire/PingData.cs` — `PINGDATA(194)`,
  `{shipID: short, ping: ushort, loss: ushort}`, 10 bytes fixed
  (header 4 + 3×2). Verified against `Inc/Messages.h:1006-1010`.
- `src/Allegiance.Core/Wire/ReqPingData.cs` — `REQPINGDATA(195)`, a
  zero-field client trigger, 4 bytes fixed (header only), the same shape as
  `RandomizeTeams`/`StartGame`/`Suicide`. Verified against
  `Inc/Messages.h:1012-1013`.

### Dispatch (`Server.cs`)
- New per-connection sim-time stamp `_lastPingQuery` (`Dictionary<ConnectionId,
  double>`, sim-thread-only like its `_last*Tick` siblings) implementing the
  5-second debounce.
- New branch in `ProcessDatagram` for `ReqPingData.Fmid`, routing to
  `HandleReqPingData(from)`.
- `HandleReqPingData` ports the native handler exactly:
  - Debounce gate = native `PingQuery`: full roster iff this connection has no
    prior request OR more than 5s of sim time has elapsed; the timestamp is
    stamped only on the successful full send (matching native, which sets
    `t_lastPingQuery` only on the `true` path).
  - Full roster: iterate `_peerShips` (this port's "every connection with a
    live player") and emit one `PingData` per entry, then the terminator
    marker `{shipID = -1, ping = 1, loss = 0}` — all packed into a single
    frame addressed to **the requester only**.
  - Debounced: send only the unchanged-window marker `{shipID = -1, ping = 1,
    loss = 0}` to the requester.
- Both the roster and the marker go to the requester only; there is no
  broadcast anywhere in the handler — see "requester-only verified" below.

### Bot (`Bot.cs`)
- `_pingDatas` accumulator + `PingDatas` property + `ResetPingDataReceived()`;
  `case PingData.Fmid` in the receive switch; `SendReqPingData()` helper.

### Dumper
- `FormatPingData` for the capture-stream decoder, tagging the
  `{shipID=-1,...}` marker as `[window marker]`.

## ping/loss value sourcing — investigated, fallback used

Native computes `ping` from DirectPlay's
`DPN_CONNECTION_INFO.dwRoundTripLatencyMS` and `loss` from DirectPlay's own
packet-retry/drop counters via `CFMConnection::GetPacketLossData`
(`FedSrv.CPP:7769-7788`), keeping a per-connection `m_lastPackets`/`m_lastLost`
running delta (`messagecore.h:201-213`).

This port's transport (`src/Allegiance.Core/Transport/TcpTransport.cs`) is a
plain `System.Net.Sockets` TCP listener/stream with **no latency or loss
tracking of any kind**, and plain .NET sockets expose no managed, cross-platform
surface for round-trip-time or retransmission counters. I investigated the
task's suggested avenue — deriving a genuine per-connection RTT proxy from the
existing client-initiated `PING(35)`/`CS_PING` (P188) traffic — but that is a
request/echo with server-stamped time that yields the server's own processing
time, not a transport-level round trip, and wiring it into `PINGDATA` would
still be inventing a new semantic rather than measuring the DirectPlay value
native reports. Per the task's explicitly pre-approved fallback, entries carry
**`ping = 0, loss = 0`**: wire-complete and decode-correct, but deliberately
not a measured value. This matches this port's established precedent for this
exact shape of gap (GAME_STATE's `nFlags`/`nArtifacts`, always zero because
native's own equivalent counters are unpopulated in a comparable way). The
choice is documented in `PingData.cs` and `Server.HandleReqPingData`, not
silently defaulted.

## Whole-server (non-mission-scoped) iteration — confirmed

Native iterates `g.fm.GetConnections()` outright — every connection on the
whole server — and is deliberately **not** scoped to a mission or side
(`FedSrv.CPP:7757-7777`). This port reuses `_peerShips` (the all-connections
map) directly, matching that. Test `ReqPingData_roster_spans_all_missions`
proves it explicitly: a requester receives entries for players in a *different*
mission too, inverting this port's usual per-mission isolation convention.

## Divergences / verifications against live native source

- **Requester-only addressed to requester — now 100% pinned.** The brief noted
  the per-entry send target "was not 100% pinned this session." Re-reading the
  live handler, **every** `SendMessages` call — the per-entry batch at
  `FedSrv.CPP:7798` and the debounced marker at `FedSrv.CPP:7806` — targets
  `pfsPlayer->GetConnection()` (the requester). There is no broadcast anywhere
  in the handler. The port therefore sends the whole response to the requester
  alone; the must-fail test `ReqPingData_full_roster_is_requester_only` asserts
  a second live peer observes nothing.
- **The requester's own inclusion.** Native iterates all connections-with-a-
  live-player, so a requester that is itself a live player would appear in its
  own roster. The task's "3 other live players → exactly 3 entries" wording is
  satisfied literally by making the requester a *bare* connection (connected,
  not logged on → not in `_peerShips`), which is the model the primary test
  uses (`BareRequester`). This is a test-design reconciliation, not a port
  deviation — `HandleReqPingData` would include the requester's own entry if
  the requester had a live player, exactly as native does.
- **Debounce scope.** Native's `PingQuery` is per-connection and stamps only on
  the full path; the port mirrors both precisely. The marker sent on the
  debounced path is byte-identical to the terminator (`shipID=-1, ping=1`),
  exactly as native reuses the same `PINGDATA` wire shape with a sentinel
  `shipID`.
- **No new protocol was invented** for ping/loss beyond the pre-approved
  `0,0` fallback (see above).

## Tests (added in `tests/Allegiance.Core.Tests/PingDataReqPingDataLiveTests.cs`)

1. `ReqPingData_round_trips_exact_bytes` — codec: fmid 195, cbmsg=4.
2. `PingData_round_trips_exact_bytes` — codec: shipID/ping/loss offsets pinned.
3. `Terminator_marker_has_exact_bytes` — pins `{shipID=-1, ping=1, loss=0}`.
4. `ReqPingData_full_roster_is_requester_only` — ⭐ 3 live players + bare
   requester ⇒ exactly 3 entries + terminator, all requester-only (a second
   peer observes nothing). Must-fail-if-removed.
5. `ReqPingData_within_5s_is_debounced_to_marker_only` — ⭐ second request
   <5s ⇒ only the unchanged-window marker. Must-fail-if-removed (verified:
   disabling the debounce makes this test receive the full roster again).
6. `ReqPingData_after_5s_elapse_produces_full_roster_again` — sim clock
   advanced >5s (150 ticks @ 20 upd = 7.5s) ⇒ full roster returns.
7. `ReqPingData_roster_spans_all_missions` — ⭐ multi-mission whole-server
   non-scoping (mission B players appear in a mission-A requester's roster).
   Must-fail-if-removed (verified: disabling the dispatch makes it fail).

Removal proof performed: disabling the dispatch turned tests 4, 5 and 7 red;
disabling only the debounce (always full) turned test 5 red with the full
roster leaking through. Both mutations reverted.

## Verification

- Clean build (`/home/domingo/.dotnet/dotnet build`): 0 warnings, 0 errors.
- Full suite (4 runs): 3001 passed ×3 clean; the two interrupting failures
  were documented live-network flakes, both confirmed environmental:
  - `LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire`
    — green 3/3 in isolated reruns.
  - `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` —
    flaps 2/6 on the **pristine tree** (all P200 changes stashed), confirming
    a machine-state flake, not a regression; its file has zero references to
    any P200 symbol.

Suite baseline grew 2994 → **3001** (P199 → P200, +7).
