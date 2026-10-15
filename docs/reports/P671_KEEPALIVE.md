# P671 — Every client sends native's keep-alive ping; the server's read deadline only covers stalled frames

Task, 2026-10-14 (clash43). Fixes the P654-aftermath mass disconnect: a 200v200
spawn lost **205 of 390 bots within ~30–60 s** as `[conn-drop] reason=ReadErrorOrEof`,
because (a) the server's data-phase read deadline measured **idle time** rather than a
stalled in-progress frame (a client that simply sent nothing for 30 s was dropped), and
(b) **no managed client sent a keep-alive in every state** — docked/staged/pod bots were
silent. Native pings every 6 s from `BaseClient::CheckServerLag`.

Commits: `2ee472f3` (master, not pushed). Worktree proof run in `/tmp/p671-proof` (removed
at end).

---

## 1. Root cause (native fidelity)

Native `BaseClient::CheckServerLag` (`clintlib/clintlib.cpp:2050-2087`):

```cpp
const int g_cPingsTimeout = 5;                    // :2045
const DWORD g_sDeadTime = 30;                     // :2047
void BaseClient::CheckServerLag(Time now) {
  const float maxLagCheck = 6.0f;                 // :2052
  if (m_fm.IsConnected()) {
    if (m_lastLagCheck + maxLagCheck <= now) {
      if (m_cUnansweredPings > g_cPingsTimeout && (now - m_timeLastServerMessage) >= g_sDeadTime)
        OnSessionLost("Pings aren't coming back.", &m_fm);   // :2064-2068
      else {
        m_lastLagCheck = now;
        ...
        BEGIN_PFM_CREATE(m_fm, pfmPing, CS, PING)  // :2078
        ...
        m_cUnansweredPings++;                     // :2085
      }
    }
  }
}
```

The client sends `CS_PING` every **6 s** while connected **in every state**, and the
server answers by echoing the ping back with its `timeServer` stamped
(`FedSrv.CPP:4897-4911`, ported at `Server.Dispatch.cs:38-67`). Any server message
refreshes `m_timeLastServerMessage` and clears `m_cUnansweredPings`
(`clintlib/appmsg.cpp:25-46`, `:405`).

So native's model is: **the client is responsible for keeping the link warm (6 s ping);
the unanswered-ping counter + a 30 s no-server-message guard is how the client decides
the session is lost.**

The managed port had neither: `ConnectionSession.SendPing` existed but its only caller was
the MonoGame client's flight prediction (`Game1.Prediction.cs:219`, ~1 Hz); the `Bot`/
`BotClass` idle client sent nothing. And the server's read deadline (P654 H-02) was taken
once **before** `ReadFrame`, i.e. it measured idle time — dropping any client that sent
nothing for `DataFrameReadTimeout` (30 s), contradicting the method's own doc comment
(`TcpTransport.cs`, "never to an idle-but-healthy data session").

---

## 2. What changed

### 2a. Client keep-alive in the shared session — and every client

Added the native keep-alive to **both** real managed session clients:

- `ConnectionSession` (`src/Allegiance.ClientCore/Session/ConnectionSession.cs`) — used by
  the MonoGame client, the Explorer, the combat MCP bots, and the live-test harness.
- `Allegiance.Bot.Bot` (`src/Allegiance.Bot/Bot.cs`, aliased `BotClass` in the MCP
  harness) — used by the idle/weave MCP bots and the console bot.

Each sends `FM_CS_PING` every **6 s** while connected, in **every state**
(lobby/hangar/docked/flying/pod/dead), and implements the native unanswered-ping
session-loss rule (stop + signal lost when `>5` unanswered pings AND `>=30 s` since any
server message). Any inbound message refreshes the last-server-message timestamp and
clears the unanswered count (`appmsg.cpp:25-46`).

**Architecture note (important, discovered live):** the keep-alive thread is started in the
**constructor** and is **persistent** (survives reconnect; skips ticks while disconnected) —
**not** in `Connect()`. The MCP harness (`ObservedSession`/`StallWatchdog`) injects a
`TcpTransport` directly into the session's private `_transport` and calls
`Transport.Join(...)`, never the session's `Connect()` (`StallWatchdog.cs:64-73`,
`ObservedSession.cs:33-49`). Tying the keep-alive to `Connect()` left every MCP-seated bot
without a keep-alive — proven live (see §4b): 40 idle bots dropped at ~30 s until the
constructor-start fix.

**Unification with the MonoGame prediction:** the MonoGame flight prediction no longer
sends its own ~1 Hz ping; it now only **consumes** the session's ping echoes for the
own-ship server clock (`Game1.Prediction.cs`, `Game1.cs` `_lastPingSentAt` removed). One
keep-alive source, native cadence.

### 2b. Server: split idle vs frame read bounds (`TcpTransport.cs`)

The host's single pre-frame deadline is split into two:

| Bound | Default | Meaning | Native cite |
|---|---|---|---|
| `DataLinkIdleTimeout` | 30 s | silence **between** frames (link-dead) — a client that sends no bytes at all is dropped | `g_sDeadTime = 30` (`clintlib.cpp:2047`) |
| `DataFrameReadTimeout` | 30 s | a frame once it has **started** (first header byte seen) — header-then-stall / dribbling body | P654 H-02; now anchored after the first byte |

`ReadFrame` reads the first byte under the **idle** bound, then anchors the **frame**
deadline and reads the rest of the header + body under it. A healthy client never trips
either: it pings every 6 s.

The deadline is now honored on **both** socket kinds:
- non-blocking (pool) — existing P646 `Socket.Poll` park, re-checks the deadline each wake;
- blocking (thread-per-peer) — the blocking `Read` is bounded by a per-read socket receive
  timeout equal to the remaining budget (previously a blocking read parked forever and the
  deadline never fired in thread-per-peer mode at all — it only ever worked on the
  non-blocking pool path, which is what the 200v200 rig uses).

Config: `transport.idleTimeoutSeconds` / `ALLEGIANCE_TRANSPORT_IDLE_TIMEOUT_SECONDS`
(default 30), alongside the existing `transport.dataFrameReadTimeoutSeconds` /
`ALLEGIANCE_TRANSPORT_DATA_FRAME_TIMEOUT_SECONDS` (`Server.Lifecycle.cs`,
`ServerConfig.cs`). The rig's `dataFrameReadTimeoutSeconds: 3600` workaround is **not**
needed after this task (proven with defaults, §4).

---

## 3. Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Client sends CS_PING every 6 s, every state | `clintlib.cpp:2050-2087` | **design** | Ported faithfully (6 s) in both `ConnectionSession` and `Bot` |
| Unanswered-ping session-loss rule (>5 pings, >=30 s no server msg) | `clintlib.cpp:2064-2068` | **design** | Ported faithfully (`PingsTimeout=5`, `DeadTimeMs=30000`) |
| Any server message clears unanswered count | `appmsg.cpp:25-46,405` | **design** | Ported (any inbound resets counters) |
| Server echoes CS_PING with timeServer | `FedSrv.CPP:4897-4911` | **design** | Already present (`Server.Dispatch.cs:38-67`), kept |
| Server drops link-dead after 30 s silence | `g_sDeadTime=30` `clintlib.cpp:2047` | **design** | New `DataLinkIdleTimeout` (default 30 s); never fires for a pinging client |
| Server drops a stalled in-progress frame | P654 H-02 (native slowloris handling) | **design** | `DataFrameReadTimeout` now anchored after the first byte, preserving the P654 protection |
| — | — | **bug (managed)** | Pre-P671 the frame deadline also measured idle time (dropped silent clients); split it |
| — | — | **bug (managed)** | Blocking (thread-per-peer) reads never honored any deadline (parked forever); bounded receive timeout now |

---

## 4. Verification

### 4a. Failing-first tests (all fail on pre-P671 code, pass after)

`tests/Allegiance.Core.Tests/Transport/P671KeepAliveTransportTests.cs`
- `SilentPeer_IsDroppedBy_IdleTimeout` — a peer that greets then sends nothing is reaped by
  the idle bound (2 s). Fails pre-P671 (blocking read parked / 30 s only).
- `HeaderThenStallPeer_IsDroppedBy_FrameDeadline` — header then stop is reaped by the frame
  bound. Fails pre-P671 (blocking read parked forever).
- `PeriodicHealthyPeer_IsNotDropped` — a peer sending small complete frames periodically
  (below both bounds) is never dropped (guard).

`tests/Allegiance.ClientCore.Tests/P671KeepAliveLiveTests.cs`
- `IdleDockedClient_StaysConnected_PastIdleWindow` — a real seated-but-docked
  `ConnectionSession` (default 30 s server settings) stays connected for >40 s past the
  30 s idle window, never reports `Lost`, and its keep-alive pings at the 6 s cadence.
  Fails pre-P671 (silent client dropped at ~30 s; cadence check fails with 0 pings).

New-test isolated runs (3×, each as it happened): transport **3/3, 3/3, 3/3**; integration
**1/1, 1/1, 1/1**.

### 4b. Mandatory live checks (local server, temp worktree, `m2xspiderw.igc`, DEFAULT 30 s settings)

**Showcase combat (30 bots) — reach + fight + no drops.** `spawn_scenario
{preset:"showcase", size_scale:1, target_sector:"Alderan"}` → 15/side, all peers seated.
Over >180 s: **24 `[warp]` crossings into Alderan** (bots leave their home sectors and
reach the target), **4 credited combat kills** (`[combat] ship X killed by ship Y`;
23 `KILL_SHIP(105)` lines incl. ejections), **0 `[conn-drop]` established-peer lines**.
`[egress]` peers=30 steady (~27–48 kB/s/peer, gc gen2=0). **Zero data-session
ReadErrorOrEof drops with default 30 s settings** — the exact failure class fixed.

**Docked-bot hold (40 bots) — the P671 acceptance run.** Fresh server (default 30 s
settings), 40 `spawn_bot {behavior:"idle"}` bots (20/side) seated-and-docked (no movement,
no flight), held **>70 s**. Result: **647 `[ping]` echoes** (the 40 docked bots keep-aliving
at ~6 s — proof "the bots' sessions ping at 6 s"), **all 40 bots still `active:40` at the
end, ZERO bot ReadErrorOrEof drops** over a >60 s docked hold. The only ReadErrorOrEof
lines were (1) one pre-join greeting drop (the tester's own TCP port-probe) and (2) the MCP
observer probe (`mcp-probe`) torn down near the end by the harness — neither is a bot.
Before the keep-alive fix, the same 40 idle bots were all dropped at ~30 s.

### 4c. Build / suite

Whole-solution build: **0 warnings, 0 errors**. Full suite via `scripts/run-tests.sh`
(P471), exactly 2 runs on final code (`master` @ `2ee472f3`):

- **Run A:** Lobby/MonoGame/ClientCore MAIN clean; `Core.Tests` **4149 passed / 2
  failed** — `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` and
  `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`. Neither is in
  P671-touched code; both **pass 3/3 in isolation** (load flakes under full-suite
  contention). `AutoReloadOnEmptyTests` already listed in `docs/TEST_FLAKES.md` (too-tight
  pump category); `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`
  added to it. QUARANTINE: only known flakes (pass in isolation).
- **Run B: fully clean** — Lobby **12/12**; MonoGame **1977 passed / 3 skipped**;
  ClientCore **866/866**; Core.Tests **4151/4151**; every QUARANTINE test passed in
  isolation. Exit 0.

---

## 5. Modern alternative considered

| Area | Native (2000s) | Modern option | Chosen | Why |
|---|---|---|---|---|
| Keep-alive scheduling | per-client `CheckServerLag` poll every 6 s | heartbeat on the socket/connection layer (e.g. idle-RST probes, HTTP/2 ping) | Ported native (6 s app-level CS_PING) | The wire PING doubles as the server-clock sync source; app-level ping is the existing protocol and needs no new frame type |
| Server link-dead detection | 30 s idle drop | TCP keepalive + SO_KEEPALIVE | Ported 30 s `DataLinkIdleTimeout` | SO_KEEPALIVE's default OS tuning is ~2 h and can't distinguish the bot silence; the app-level idle bound is explicit and testable |
| Read deadline on blocking sockets | thread-per-peer parked forever | async I/O (no thread) | Bounded receive-timeout on the blocking read | Keeps the existing thread model; a bounded timeout is the smallest correct fix |

The core keep-alive (6 s ping) is native-faithful intent; no era constraint was copied.

---

## 6. Files

- `src/Allegiance.Core/Transport/TcpTransport.cs` — `DataLinkIdleTimeout`, split
  idle/frame read bounds, blocking-read deadline.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — keep-alive + loss rule
  (constructor-started, persistent).
- `src/Allegiance.Bot/Bot.cs` — keep-alive + loss rule (constructor-started, persistent).
- `src/Allegiance.ClientCore.Game/Game1.Prediction.cs`, `Game1.cs` — no double ping.
- `src/Allegiance.Server/ServerConfig.cs`, `Server.Lifecycle.cs` — idle-timeout config.
- `tests/.../P671KeepAliveTransportTests.cs`, `tests/.../P671KeepAliveLiveTests.cs`.
