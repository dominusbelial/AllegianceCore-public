# C++ client transport rewrite — archived documentation (T1-T10)

Archived from `~/Development/Allegiance` (the original C++ client/engine
repo, `port-transport` branch, merged to its local `master` 2026-08-30).
That repo has no active upstream reviewers, so this record lives here
instead, alongside the server it documents interoperating with.

## What this proves

The **original C++ `FedMessaging`** networking class — unmodified game
logic, only its transport swapped out — completed a real FEDMSG logon
handshake over a real TCP socket against **this repo's**
`src/Allegiance.Server`. Verbatim result (T7):

```
LOGONREQ cbmsg=1153 sizeof(FMD_C_LOGONREQ)=1128
OnAppMessage: fmid=2 cbmsg=40
  LOGONACK: fValidated=1 fRetry=0 shipID=8098 cookie=0xb5 reason='Welcome T7-LiveTest'
OnAppMessage: fmid=7 cbmsg=56
RESULT: LOGON_SUCCESS
```

Independently confirmed from this server's own log at the time: matching
`[logon] ... validated=True -> 'Welcome T7-LiveTest'` entries with real
assigned ship ids.

## What T1-T10 did, in order

| # | what |
|---|---|
| T1 | `ITransport`/`ITransportSite` seam + `DirectPlayTransport` wrapper (additive, zero behavior change) |
| T2/T2b/T2c | Wired `FedMessaging`'s lifecycle + send path onto `ITransport` |
| T3 | Wired the receive path (the harder half — cross-thread event queue) |
| T4 | Built a real `TcpTransport` (client-side), C++ port of `src/Allegiance.Core/Transport/TcpTransport.cs` in this repo |
| T5 | Gave `FedMessaging` a construction-time transport selector, default unchanged |
| T6 | Threaded the TCP choice through WOPR's real call chain (3 native/managed levels) |
| T7 | **Live proof**: flipped WOPR's call site + a standalone test harness — `LOGON_SUCCESS` against this server |
| T8 | Diagnosed (but didn't fix) a `Shutdown()` hang found by T7's live test |
| T9 | Dropped `<dplay8.h>` from the widely-included `messagecore.h` — the header is now transport-agnostic |
| T10 | Fixed the `Shutdown()` hang for real (cooperative non-blocking read loop) |

Each `TRANSPORT_Tn.md` in this directory is that task's own full evidence
(file:line citations, verbatim build/test output) — not a summary.

## Why this matters for this repo specifically

This server's wire protocol and `TcpTransport.cs` reference implementation
are now proven compatible with the **original game's own C++ client
networking code**, not just with this repo's own C# test harnesses. The
wire format, framing, and logon handshake in `src/Allegiance.Core/Wire/`
and `src/Allegiance.Core/Transport/TcpTransport.cs` are the ground truth
these C++ changes were ported against — see each `TRANSPORT_Tn.md`'s
"reference" citations.

Full narrative, including process lessons and things that didn't work
(T2's first attempt, T8's disproven fix hypothesis): the Domingo LLM Wiki,
`allegiance-transport-tcp.md`.
