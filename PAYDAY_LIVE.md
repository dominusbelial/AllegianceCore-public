# PAYDAY_LIVE — live-VM confirmation that P51 works end-to-end

Date: 2026-09-02. Claude Code's own live pass, matching the standing
P4X→live-pass pattern (P41→P42, P45/46→live-pass, P47→P48→live-pass).
Real `Wopr.exe` client (Windows VM, `192.168.0.240`) against a freshly
built `AllegianceCore` server (commit `957af81`, P51's own commit).

## Method

A temporary one-line diagnostic was added to the native client's own
central message dispatch (`Allegiance/src/Wopr/Wopr/MessageReceiver.cs`,
the `FM_S_PAYDAY` case) to log every received `PAYDAY(114)` and its
`dMoney` value unconditionally — needed because `CommanderResearchAndExpand`'s
own `MessageReceiver_FMD_S_PAYDAY` handler is a no-op stub
(`CommanderResearchAndExpand.cs:697-703`, body entirely commented out), so
it produces no visible signal on its own. **Real, load-bearing finding**:
`CommanderResearchAndExpand` cannot even be attached today —
`TeamDirector.CreatePlayerThread`'s direct-connect path
(`TeamDirector.cs:441-492`, the only path this project's harness has ever
used) unconditionally hardcodes `StrategyID.ScoutExploreMap` regardless of
the `isCommander` flag; `CommanderResearchAndExpand` is only reachable via
`Strategy_OnStrategyComplete` after the lobby-mediated `ConnectToGame`
path, which direct-connect explicitly skips. So this pass verified the
wire message reaching a real native client through the same
strategy-agnostic dispatch point every prior live pass has used (matches
P48's own raw-fmid diagnostic technique) — a faithful proxy for "the
message is correct and consumable," not a claim that
`CommanderResearchAndExpand` itself was exercised. Diagnostic fully
reverted after (`git diff` empty vs `Allegiance` baseline).

Server: rebuilt `tools/vizfight explore` fresh from `957af81` (the
previously-running instance predated the P51 commit — a rebuilt binary on
disk does nothing to an already-running `dotnet run` process, learned the
hard way again). Client: managed-only rebuild of `Wopr.exe`
(`FZDebug-NoYellowComm`/x86/v145), reusing the native libs already built
for the P50 pass.

## A real connectivity trap hit and fixed along the way

First attempt produced **zero** `PAYDAY` receipts over 240s despite the
server correctly firing every 60s server-side. Root cause: the VM's
`Wopr.exe.config` had a **stale `ServerPort` (38879)** left over from an
earlier session's server instance — this run's server bound a fresh
ephemeral port (34023, `TcpTransport` binds `IPAddress.Any` + port 0 →
OS-assigned). The bot was silently failing to connect at all (0 players on
both sides for the whole run, confirmed via the server's own `[payday]`
log lines: `side 0 'Alpha': 0 players`). Fixed by updating the config's
`ServerPort` to the actual bound port; `ServerAddress` (`192.168.0.140`)
was already correct — a real IP of this host, confirmed via `ip addr`.
**Worth remembering for any future VM trial**: the vizfight backend binds
an ephemeral port every run; always confirm `Wopr.exe.config`'s
`ServerPort` matches the current instance's actual bound port before
blaming the feature under test.

## Result — CONFIRMED live, correct cadence and correct values

Second run, single real bot (`explorer0@BOT`, side 0/Alpha), 240s:

```
P51-LIVE: PAYDAY received, dMoney=5400
P51-LIVE: PAYDAY received, dMoney=675
P51-LIVE: PAYDAY received, dMoney=675
P51-LIVE: PAYDAY received, dMoney=675
```

- **First receipt (5400) is correct, not anomalous**: the server had been
  running with 0 players since boot, so Alpha's treasury had accrued
  675/tick × 8 ticks = 5400 by the time this bot joined — `delta =
  Money/playerCount` (`Server.cs`'s `OnPayday`, faithfully porting
  `DoPayday`) correctly pays out the full backlog to the first player on
  join, exactly matching the reference formula's own semantics (there is
  no separate "catch-up suppression" in the native design — a late joiner
  legitimately receives the accumulated treasury).
- **Steady-state (675 repeating, four times total, ~60s apart)**: matches
  the server's own independently-logged per-tick accrual for Alpha's
  civilization exactly (`[payday] side 0 'Alpha': ... accrued 675`,
  cross-checked directly against the server console log for the same run
  window) — `600.0f * (1 + IncomeMoney)` with Alpha's civilization
  `IncomeMoney = 0.125`. Cadence measured directly from the client's own
  receipt timestamps: ~60s apart, no drift, no double-fires, no missed
  cycles across 4 consecutive payouts.
- Server-side zero-player-side guard also independently reconfirmed live
  (Bravo, still 0 players throughout, correctly accrued-not-paid every
  cycle with no crash) — matches `PaydayTests.cs`'s own loopback coverage.

**This closes P51's own explicitly-deferred follow-up**: a real
`Wopr.exe` client genuinely receives `FM_S_PAYDAY` with correct,
cadence-accurate, formula-accurate values over a real TCP connection.

## Secondary finding, NOT fixed here — reported honestly

The server process crashed (unhandled `IOException`/`SocketException`
"connection abort" in `Server.SendPeer`, called from
`BroadcastShipStates`) when the bot's SSH-hosting session on the VM closed
and — per the project's own long-documented SSH-session-lifetime trap —
killed the child `Wopr.exe`, causing an abrupt TCP reset. `SendPeer`
(`Server.cs:220`) has no per-peer exception handling around the socket
write, so one peer's ungraceful disconnect takes the **entire** server
process down, not just that peer's connection. This is a pre-existing
connection-layer robustness gap, unrelated to PAYDAY specifically (it
would trigger identically on any ungraceful client disconnect during any
send), not something this pass fixes — flagged for a future task to
scope.

## Verification limits

- Confirms the message and its values, not `CommanderResearchAndExpand`
  itself — see the strategy-selection gap above. Actually exercising
  `CommanderResearchAndExpand` would need either a `TeamDirector.cs`
  change (temporary diagnostic, same discipline as this pass) or the
  lobby-mediated connect path, neither attempted here — out of scope for
  this pass, noted for whoever picks up docking/mining next (the
  economy chain's next real dependency on that class actually running).
- Single-bot, single-side observation. Multi-player-per-side payout
  splitting (`delta = Money/playerCount` with playerCount > 1) is only
  tested at the loopback/unit level (`PaydayTests.cs`), not live here.
