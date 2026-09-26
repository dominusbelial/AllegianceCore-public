# P387 — First-decision latency of freshly-undocked NPC miners

**Status:** Fixed on `master` (not pushed — Domingo pushes). Verified with repeated real boots.
**Root cause:** not a transport/connection bug at all — the debug-mode mission was held in `NotStarted` (the whole sim frozen) until a *player* joined, so the autonomous NPC miners never made their first decision without a player.

---

## Symptom

A freshly-auto-undocked NPC miner's first decision (pick a mining target, warp to
Alderan) was delayed by tens of minutes — observed ~26 minutes of total silence
(12:28:48 auto-undock → 12:55:19 first warp) on the real debug-mode server this
session, when no player was connected. With a player connected from the start,
the equivalent first warp happened in under a minute. The single-run observation
looked like the first decision was gated on a peer connection.

## What the investigation found (the real mechanism)

The first-decision latency had **nothing** to do with the transport/connection
layer. It was a mission-**stage** gate:

1. A fresh mission is born `MissionStage.NotStarted` (`Mission.cs:70-73`).
2. `Mission.Update` **early-returns and freezes the entire sim** while the stage is
   not `Started`/`Starting` (`Mission.cs:562-566`): it advances the mission clock
   and returns without ticking any sector/ship. So no `Ship.Update`, no
   `DecideMiningOrSelling` (`Ship.cs:6029-6030`), no movement, no warp.
3. The mission only leaves `NotStarted` via `TryAutoStart` (`Server.Lobby.cs:379`),
   which is called **only** from `RecomputeSideReady` (`Server.Lobby.cs:324`) —
   i.e. only when a **player joins and readies**.
4. P381's `RunNpcAutoLaunch` (`Server.Docking.cs:1888`) is **not** stage-gated
   (it's a server `SimPass` step), so the docked-at-boot initial miners still
   **auto-undocked** at ~10 s — but with the sim frozen they then just floated,
   never ticking, never deciding, for however long the server sat playerless.

So the two observed behaviors were the **same** mechanism, seen from both sides:

- **Fast case** (player connected from ~start): mission auto-started via the
  player's ready-up → sim unfreezes → both sides' ships decide within ~1 min.
- **Slow case** (no player for 26 min): mission stayed `NotStarted` → sim frozen →
  both ships sat undocked-but-inert until the player finally connected, which
  started the mission and unblocked **both** sides' ships ~1 min later.

That "both sides wake together right after a peer connects" signature looked like
an asymmetric peer-dependence, but it was just mission-start unfreezing the whole
sim at once. The peer connection was **correlated** with the fix to the latency,
not a **cause** of it — the cause was the missing mission start.

## Why debug mode made this visible

P379/P381/P382 built the debug topology specifically for **unattended autonomous
testing** (synthetic Omega/Plato garrisons, each side's miner spawned docked at
boot, P381 auto-launch, the whole mine/sell/redock loop). That only makes sense if
the mission **runs** without a human player. But the P180/P188 ready-up hold
(P188 routed the auto-start through a 15 s `STAGE_STARTING` countdown) left the
mission pinned in `NotStarted` until a player readied — so the autonomous demo
silently froze until someone connected. Production (debugMode off) is *supposed*
to await players, so the hold is correct there; it's only the debug test harness
that needs the mission to start by itself.

## The fix

In **debug mode only**, auto-start every mission at boot so the sim runs
immediately with no player required.

`src/Allegiance.Server/Server.Lifecycle.cs`, in `Boot()` right after the P382
initial-miner spawn:

```csharp
if (live.DebugMode)
{
    Console.WriteLine("[boot] DEBUG MODE ACTIVE — auto-starting mission(s) at boot (no player required) ...");
    foreach (var mission in missions)
        TryAutoStart(mission);
}
```

`TryAutoStart` passes vacuously with no occupied sides (`IsAllOccupiedSidesReady`
finds no unready *occupied* side to veto), so the 15 s briefing countdown begins
immediately at boot; the sim ticks during `Starting` too, so the miners decide
right away. Production (debugMode off) is byte-for-byte unchanged — it still waits
for player ready-up (pinned by the new control test below).

## Why P386 is provably not involved (scope-4 check)

- **Mechanism:** P386 clears `PlanTarget`/`AcceptedTarget` only at the
  `ServerHost.DockWithStationEvent` (real dock-**arrival**) transition. The
  initial miners are **spawned already docked** (P382) and never traverse
  `DockWithStationEvent` before their first undock — P386's code path never even
  executes for them pre-first-decision. And the first-decision block
  (`Ship.cs:5999-6030`) already requires `PlanTarget == null` &&
  `AcceptedTarget == null`, which holds trivially for a fresh spawn — a stale plan
  (what P386 fixes) is not present.
- **Experimental isolation:** the P387 must-fail test below reproduces the exact
  freeze **with P386 intact and only the P387 fix removed** (mission stays
  `NotStarted` → sim frozen → miner never decides). The freeze is caused solely by
  the missing mission start, entirely independent of P386. Neither P386 nor the
  mine/sell/redock loop is on the failing path.

## Tests added (`InitialMinerSpawnLiveTests.cs`)

1. **`Debug_mode_auto_starts_at_boot_and_first_miner_decides_without_any_player`** —
   the **must-fail discriminator**: debugMode ON, NO peer ever connects; asserts
   the mission auto-started at boot (`Stage != NotStarted`), the docked miners
   auto-undock (P381), acquire an Alderan mining target, **and actually warp into
   Alderan**, all unprompted. **Goes RED** when the P387 fix is removed (verified:
   `Assert.NotEqual(NotStarted, mission.Stage)` fails, mission stays `NotStarted`).
2. **`Normal_mode_with_no_player_stays_notstarted_and_miner_never_decides`** — the
   **control**: debugMode OFF, no player → mission legitimately stays `NotStarted`
   (production unchanged), the P381-auto-undocked miner never decides. Guards
   against widening the auto-start beyond debug mode.

## Repeated real-boot verification (real wall-clock, real debug-mode server, m2xspiderw)

All runs on the live-test host (192.168.0.221, `docker compose` debug-mode server,
the checked-in `compose.server.debugmode.json`). Metric = time from the P381
`[npc-autolaunch]` line to the ship's first `[warp] ... crossed` line
(`docker logs -t`). Ship 8096 = side 0 (Omega), 8097 = side 1 (Plato); the ~50 s
spread between them is the travel time from each garrison to its own Alderan warp,
not decision latency.

**Before fix (this session, observed):** ~26 min (12:28:48 → 12:55:19) with no player.

**After fix — 3 no-client runs (0 peers connected throughout):**

| Run | 8097 first warp (Plato) | 8096 first warp (Omega) | peers |
|-----|--------------------------|--------------------------|-------|
| 1 | 32.7 s | 85.9 s | 0 |
| 2 | 32.7 s | 86.0 s | 0 |
| 3 | 32.7 s | 86.0 s | 0 |

Every run: both ships auto-undock at ~10 s, countdown elapses to `STAGE_STARTED`
at ~15 s, and both ships warp on their own with **zero** peers.

**After fix — with a real peer connected partway (Scope 2):** the Allegiance.Bot
console client (`p387peer2`) connected ~5 s after boot and stayed connected the
whole run; the peer **neither shortened nor delayed** the first warps:

| Peer | 8097 first warp (Plato) | 8096 first warp (Omega) |
|------|--------------------------|--------------------------|
| connected @~5 s | 32.9 s | 92.9 s |

First-decision timing is now **independent of whether/when a peer connects** — the
original peer correlation is denied as a mechanism; the true cause was the missing
mission start.

## Notes / out-of-scope observations

- **Game-over on last-peer-leave (pre-existing, unrelated):** if a peer connects
  and then disconnects leaving 0 players, the mission is declared Over via the
  inactive-sides check (observed: `[gameover] mission #0 -> Stage Over ... 0
  peer(s)`), which halts the autonomous miners too. This is a separate
  game-lifecycle behavior, not the first-decision latency, and out of P387's
  scope (the mine/sell/redock loop and this specific latency). Flagged for
  awareness, not changed.
- The mine/sell/redock loop itself was confirmed working and was **not touched**,
  per scope.

## Deliverables

- Fix: `src/Allegiance.Server/Server.Lifecycle.cs` (debug-mode boot auto-start).
- Tests: `tests/Allegiance.Core.Tests/InitialMinerSpawnLiveTests.cs` (2 new).
- Whole-solution build: 0 warnings, 0 errors.
- Committed on `master` (not pushed).
