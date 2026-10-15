# P387 — investigate: a freshly-undocked NPC's first mining decision is delayed by tens of minutes (was under a minute)

## Why this task exists

Live-tested this session (2026-09-26, real debug-mode server, real client).
**The mine/sell/redock loop itself is confirmed solid** — not the problem:
both initial miners (P382, ships 8096/8097) completed a full cycle cleanly
and repeatably (mine to 150/150 ore, warp home, dock, sell via P386's fix,
auto-relaunch via P381). The real, still-unexplained problem is **how long
it took either ship to make its very first move after auto-undocking**.

## Confirmed timeline (real timestamps, `docker logs -t`)

```
12:28:39  both ships spawned docked (P382)
12:28:48  both auto-undocked (P381, exactly the correct ~10s)
   ...then NOTHING for both ships until...
12:55:19  ship 8097 (side 1, Plato — NO real player connected on this side)
          warps Plato -> Alderan
12:56:07  ship 8096 (side 0, Omega — the connecting player's side)
          warps Omega -> Alderan
```
**~26 minutes of total silence** for both ships after auto-undocking, then
both made their first move within about a minute of each other. Compare to
an earlier test *this same session*, on the same debug-mode topology, where
the equivalent first warp happened **under a minute** after auto-undock —
so this is a real regression in latency, not the loop's normal cadence.

**Suspicious (not proven) correlation**: a real client (`domingo1`, side 0)
connected during that 26-minute silent window, and both ships' first
decisions landed within ~1 minute of that connection — including ship 8097,
on the *opposite* side from the connecting player. This is the strongest
lead, but it is an observation from a single run, not a proven mechanism —
see Scope below.

## What's already been checked and ruled out

- **No exceptions anywhere** in the server log for the whole silent window.
- **No explicit peer-count gate exists** on the tick loop: `Server.Lifecycle.cs:686-697`
  (`Tick()`/`TickSim()`) call `SimPass()` (`Server.Broadcast.cs:31`)
  unconditionally every period; `grep`'d `Server.cs`/`Server.Broadcast.cs`/
  `Server.Lifecycle.cs` for `_peerShips.Count`/`ConnectionCount` — the only
  hits are unrelated stats bookkeeping and round-robin spawn-side selection,
  nothing gates *whether* a pass runs.
- **`ServerHost.Drain`** (`ServerHost.cs:934-944`, the "pump transport
  events" step inside `SimPass`) is a plain `ConcurrentQueue.TryDequeue`
  loop — non-blocking by construction, cannot stall the tick loop waiting
  for a connection.
- **`RunNpcAutoLaunch`** (P381, `Server.Docking.cs:1888-1911`) mutates the
  live `Ship` instance directly from `sector.Models.ToArray()` — the same
  collection the per-tick `Ship.OnUpdate` loop iterates, snapshotted only to
  avoid mutating a list mid-enumeration. No stale-copy/reference-identity
  issue here.
- **P386's fix only fires on `ServerHost.DockWithStationEvent`** (a real
  dock-*arrival* transition) — the initial miners are spawned
  *already docked* by P382 and never go through that event before their
  first undock, so P386 is very unlikely to be the direct cause of this
  specific first-decision delay (though not perfectly proven impossible —
  worth a quick sanity check, see Scope).
- **P384/P385 are client-only** (`Game1.cs`/`Program.cs` in
  `Allegiance.ClientCore.MonoGame`) — structurally cannot affect server-side
  timing.
- **The gating chain that leads into `DecideMiningOrSelling()`**
  (`Ship.cs:5999-6030`) reads correctly on paper for a fresh spawn:
  `PlanTarget`/`AcceptedTarget` both default `null`, `IsNpc`/`PilotType` are
  set explicitly by `SpawnNpcShip`, `StationId` is cleared by
  `UndockShipState`. Nothing found by static reading explains a 26-minute
  gap — this needs live instrumentation, not more code reading.

## Scope

1. **Reproduce first, with real timestamps, before touching anything.**
   Boot a real debug-mode server exactly as this session did (the checked-in
   `docker/compose.server.debugmode.json` + `docker/compose.override.debugserver.yml`,
   see `docker/README.md`), **do not connect any client**, and measure real
   wall-clock time from auto-undock to first `[warp]` line for both initial
   miners. Repeat at least 3 times to establish whether the delay is
   consistently long, or was an anomaly of this one run.
2. **Test the peer-connection correlation directly**: repeat the same
   measurement, this time connecting a real client partway through the
   silent window (or from the very start), and check whether that measurably
   and repeatably shortens the delay for *both* sides' ships (not just the
   connecting player's own side — that asymmetry, if real, is itself a big
   clue about the mechanism). Don't accept the single-run correlation
   observed this session as proven; confirm or deny it with repeated,
   controlled runs.
3. **If the correlation holds, find the actual mechanism** — likely
   somewhere in the transport/connection layer that this task's own review
   didn't check (the actual TCP accept/listen loop, whatever thread/loop
   structure calls `Tick()` repeatedly, thread pool starvation, GC pauses,
   or something scheduling-related that a live profiler/instrumented build
   would reveal far faster than more static reading). Add temporary,
   removable diagnostic logging (real sim-time + real wall-clock time) at
   each conjunct of the `Ship.cs:5999-6030` gating chain if needed to see
   exactly which tick, and which condition, finally flips true.
4. **Sanity-check P386 isn't secretly involved**: confirm on a build with
   P386's `ServerHost.cs` change temporarily reverted whether the delay
   still reproduces identically, to fully rule it out (or, if it turns out
   to matter, explain exactly how — don't just assert innocence, prove it).
5. Fix the actual root cause once found. If it turns out to be inherent
   platform/environment behavior (e.g. a genuine OS/container scheduling
   quirk under idle CPU load) rather than a code bug, say so plainly and
   explain why — don't force a code change onto a non-code cause.

## Explicitly out of scope

- The mine/sell/redock loop itself — confirmed working, don't touch.
- Any change to P379/P381/P382/P386's actual logic unless it's the proven
  root cause of this specific latency.
- P383/P384/P385 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: repeated real boots (at least 3, both
  with-a-connected-peer and without), each with real wall-clock timestamps
  from auto-undock to first `[warp]`, showing the fix produces a
  consistently short delay (comparable to the sub-minute figure observed
  earlier this session) regardless of whether/when a peer connects.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix (or, if the cause is environmental rather than a code bug, a
   clearly written explanation of why, with the repeated-run evidence),
   committed on `master` (not pushed — Domingo pushes).
2. Report doc `P387_NPC_FIRST_DECISION_LATENCY.md` in the repo root: the
   real root cause, how it was found, the fix (or explanation), and the
   repeated real-timing verification.
