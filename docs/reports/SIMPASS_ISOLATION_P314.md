# SIMPASS_ISOLATION_P314.md

## Relationship

Finding **H2** of the glm-5.3 full-codebase stability review
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`):

> **H2**: No exception isolation around `SimPass` — a live, reachable example:
> `ExportShip.Encode()` throws when a ship's part count exceeds 255
> (`byte.MaxValue`), and nothing caps mounted-part count before that path runs.
> One ship crossing 256 parts kills the whole server process on the next tick.

Review remediation line 155-156: "Wrap `SimPass` stages in try/catch that logs
and continues — converts H2-class failures from full outages into dropped ticks."

Scope: this stage implements that recommendation wholesale (per-stage barriers),
closes the one provably-reachable H2 crash trigger (the part-count cap), and
adopts a documented policy for `Notable` subscriber exceptions (the vizfight
event feed).

## Exclusions (out of scope this task)

- **P311 / P312** (accept-loop greeting timeout, non-blocking sends) and the
  **turret-slot infinite loop (P313)** — separate root causes already landed on
  `master` before this stage; exception isolation does not fix an infinite loop
  (it never throws), so it is explicitly untouched here.
- **H3/H4/H6 and every other H/M finding** from the same review — separate tasks.
- Native reference source (`~/Development/Allegiance`) — read-only, not touched.

## What changed

### 1. Per-stage exception barriers in `SimPass` (`src/Allegiance.Server/Server.cs`)

Every stage of `SimPass()` now runs inside a shared barrier helper:

```csharp
private bool Step(string stage, Action action)
{
    try { action(); return true; }
    catch (Exception e) {
        Console.WriteLine($"[simpass] stage '{stage}' threw {e.GetType().Name}: {e.Message}");
        Console.WriteLine(e.ToString());
        lock (_statsLock) _simPassStageFailures++;
        return false;
    }
}
```

A thrown stage is logged with its stage name (enough context to diagnose which
pass failed), that stage is skipped for the tick, and the server keeps
simulating on the next cycle. Stages are independent — one failure never
cascades into its siblings.

Stages barriered (25 total):

1. `drain` — `_host.Drain(HandleTransportEvent)` (contained a previously-unprotected
   `ReleasePeer` escape path alongside the per-message-caught datagram handlers)
2. `update` — `foreach mission: m.Update(now)` (`Mission.Update`)
3. `startCountdowns` — `AdvanceStartCountdowns`
4. `sideSelection` — `AdvanceSideSelection`
5. `ballots` — `ResolveBallots` loop
6. `ripcordAborts`
7. `ripcordTeleports`
8. `rescues`
9. `stationRescues`
10. `carrierRelaunches`
11. `treasurePickups`
12. `buildingEffectInterruptions`
13. `buildingEffectCreations`
14. `broadcastShipStates` — **where the reachable H2 crash lived**
15. `broadcastStations`
16. `broadcastAsteroids`
17. `broadcastProbes`
18. `broadcastShipsUpdate`
19. `broadcastMissileLock`
20. `broadcastMissiles`
21. `broadcastOwnShipMining`
22. `broadcastOwnShipKillBonus`
23. `broadcastMissileTrackWarning`
24. `broadcastScoreRoster`
25. `broadcastLeadIndicator`

The tiny bookkeeping locks (`_simNow += _period`, `_loopIterations++`) are left
unbarriered (pure counters, cannot throw; the iteration counter must always
advance). Diagnostics exposed as `Server.SimPassStageFailures` (monotonic) so a
test/operator can observe that a bad tick degraded instead of killing the server.

### 2. Part-count cap in `MountPart` (`src/Allegiance.Core/Sim/Ship.cs`)

The wire's 1-byte `nParts` field (`ExportShip.Encode`, `Wire/ExportShip.cs:103-104`)
caps a ship's mounted-part count at `byte.MaxValue` = 255. `_parts` only ever
grows through `MountPart` (`_parts.Add`, `Ship.cs:1955`), so the cap is enforced
THERE as the port's single upstream choke point:

```csharp
public MountedPart? MountPart(...)
{
    if (_parts.Count >= byte.MaxValue)
        return null;                 // refuse the 256th part
    ...
}
```

This makes it impossible for any ship to reach 256 parts through ANY path —
`BUY_LOADOUT`'s `ApplyLoadout`, `TREASURE_ACK`, `Promote`'s inventory copy, and
cargo re-equip all funnel through `MountPart` — so the provable crash trigger is
closed on top of the general isolation. The return type widened to `MountedPart?`;
no caller used the value (verified across src/ and tests/), so the refusal is
silent unless a caller chooses to log it.

The two server handlers that add parts from untrusted input now detect the
refusal and log it:
- `ApplyLoadout` (`Server.cs`) — a refused part is counted `skipped` (so the ACK's
  `fBoughtEverything` correctly goes false) and logged.
- `TREASURE_ACK` fresh-mount branch — a refused mount drops the buffered offer and
  logs instead of broadcasting a bogus `ADD_PART`.

### 3. `Notable` subscriber policy (`src/Allegiance.Server/Server.cs`)

New `Notify(string)` is the **sole** raiser of the public `Notable` spectator
event, wrapped in its own try/catch: a misbehaving subscriber (the vizfight event
feed) is logged and skipped, never allowed to stall or kill the server from any
code path (inside a `SimPass` stage OR inside a datagram handler). All 14 former
`Notable?.Invoke(...)` call sites plus `OnNotable` now route through `Notify`.

## Verification

### Live proof (the required "keeps running, doesn't die")

`SimPassExceptionIsolationTests.SimPass_contains_a_throwing_export_stage_and_keeps_running`
reproduces the concretely-reachable H2 crash on a real in-process `Server` with two
real loopback bots on the same side. Reaching 256 real parts is impossible now (the
cap), so the equivalent poisoned state is injected directly into the private `_parts`
list of the teammate ship (256 parts — the legacy exploit state), which makes the
teammate-export in `BroadcastShipStates` hit the exact `ExportShip.Encode` throw:

```
EXPORT nParts=256 exceeds the wire's 1-byte nParts field (max 255)
```

Pre-P314 that exception escaped `SimPass` → `TickSim` → the top-level catch and
terminated the process (the must-fail run shows exactly this stack, killing the
test). With the barrier it is logged under `[simpass] stage 'broadcastShipStates'`,
that broadcast is skipped for the tick, and the server **keeps ticking normally on
the next cycle** — asserted via `LoopIterations` continuing to advance while
`SimPassStageFailures` grows monotonically.

### Must-fail discrimination (each fix proven to be what keeps its test green)

Temporarily reverting each change turned the corresponding test red:

- Removing the `MountPart` cap → 300 parts mount, `ExportShip` would throw → cap
  test red (`Expected 255, Actual 300`).
- Removing the `broadcastShipStates` barrier → the 256-part teammate export escapes
  and kills the server → isolation test red with the exact H2 stack trace.
- Stripping `Notify`'s try/catch → a throwing spectator subscriber propagates →
  notable test red.

### Tests

3 new tests in `tests/Allegiance.Core.Tests/SimPassExceptionIsolationTests.cs`:
1. `MountPart_refuses_to_exceed_255_equipped_parts` — cap ceiling + 255-part
   `ExportShip.Encode` round-trips without throwing.
2. `SimPass_contains_a_throwing_export_stage_and_keeps_running` — live isolation proof.
3. `Throwing_notable_subscriber_is_contained_not_fatal` — Notable subscriber policy.

### Build / suite

- Whole-solution build: **0 warnings, 0 errors**.
- Full `Allegiance.Core.Tests` suite run 3x consecutively; only the documented
  pre-existing flakes interrupt (primarily
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`,
  which fails on the pristine tree too). Counts recorded in the commit log.

## Notes for reviewers

- The per-stage barrier is intentionally coarse-grained (one stage = one log
  line) to satisfy "which stage" diagnosability; ship/peer context already rides
  in the logged exception message where the throwing code emits one.
- A failed `Mission.Update` no longer aborts the remaining per-tick passes; it
  logs and the later observation/broadcast stages run on the (possibly stale) sim
  state for that tick, recovering next tick. This matches the task's "skip that
  broadcast, keep simulating" direction.
