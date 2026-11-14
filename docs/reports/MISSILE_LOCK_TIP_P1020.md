# MISSILE_LOCK_TIP_P1020 — the missile-lock partial-tip live test

## Finding

`MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` failed because **the test raced
itself**. The client tip logic and the server's lock build and broadcast are correct and match native; no product
behaviour changed.

The test's spawner set `ship.CommandTarget = target` at spawn. The server therefore started building the lock on the
first sim tick, during the test's *fast* join pump (`PumpUntil`, about 1.7 ms of wall-clock per tick), long before the
client was seated. Each `TickSim` advances a fixed 50 ms of sim time, and the chosen missile (`mis06`, `LockTime` 1 s)
gains 0.05 per tick dead ahead, so the lock is full after 20 ticks, about 35 ms of wall-clock. The owner-only
`MISSILE_LOCK_UPDATE(204)` stream is best-effort and latest-wins, so the first frame the client applied was simply
wherever the lock had got to. The test's slow pump (40 ms per tick) only began after that.

Captured on the dispatch base with temporary tracing (since removed), three runs alone:

| Run | Server frames before the first client frame | First lock the client applied | Result |
| --- | --- | --- | --- |
| 1 | 18 | 0.850, then 0.900, 0.950, 1.000 | pass, 1 s |
| 2 | 19 | 0.900, then 0.950, 1.000 | pass, 1 s |
| 3 | 28 | 1.000, and 1.000 from then on | fail, 38 s |

In run 3 the client went from "no lock" to "full" in one observed frame, posted "Missile lock acquired", and the test
then waited 900 ticks × 40 ms for a partial tip that could no longer be posted — the 38 s signature in every ledger
row. The pass/fail split is just whether the join took fewer or more than 20 ticks, which is why the rate drifted from
intermittent to 3/3 as the join path grew, and why it never tracked host load.

I did not bisect by commit. The race has been in the test since it was written; which later commit lengthened the join
past 20 ticks would not change the fix.

## Native citations (re-grepped)

- `WinTrek/console.cpp:935-939` — each frame reads `magazine->GetLock()` of the local `ET_Magazine` mount 0.
- `WinTrek/console.cpp:941-943` — partial tip when the lock is in (0,1) and the previous frame's was not.
- `WinTrek/console.cpp:944-946` — `else if`: acquired tip when the lock is 1 and the previous was not.
- `WinTrek/console.cpp:947-948` — `else if`: lost tip when the lock is 0, the previous was not, and `!m_fTargetChanged`.
- `WinTrek/console.cpp:950-952` — previous lock stored, `m_fTargetChanged` cleared; set at `console.cpp:1002`.
- `Igc/magazineigc.cpp:102-105` — lock only runs when loaded with ammo; ripcord zeroes it.
- `Igc/magazineigc.cpp:123-128` — target gates (same cluster, hostile, damageable or warp for warp bombs, visible).
- `Igc/magazineigc.cpp:134-143` — range gate; the lock is zeroed first, so leaving range drops it to 0 in one update.
- `Igc/magazineigc.cpp:145-146` — `maxLock == 0` gives lock 1 immediately (native's own 0 → 1 jump).
- `Igc/magazineigc.cpp:158-167` — build: `lock += v * dt / lockTime` (× hull ECM for a ship), `v = (lockAngle − angleOffBow) / lockAngle`.
- `Igc/magazineigc.cpp:170-175` — clamp to [0, 1].

Because native's tips are one `if / else if` chain over two consecutive frames, a lock that goes 0 → 1 between two
observed frames posts only "acquired" in native too. What the failing run showed is native behaviour for the frames
the client saw.

## Root cause (file:line)

- `tests/Allegiance.ClientCore.Tests/MissileLockLiveTests.cs` (pre-fix line 109, `ship.CommandTarget = target;` in the
  spawner) — the lock starts before the client exists.
- Same file, pre-fix `SlowPumpUntil` and its comment — the pacing assumption was right about coalescing but applied
  too late; the partial phase was already over (or nearly) when the slow pump started.
- Product code that behaves as designed: `src/Allegiance.Core/Sim/Ship.cs:4823` (`UpdateMissileLock`),
  `src/Allegiance.Server/Server.Broadcast.cs:3594` (`BroadcastMissileLock`, coalescing send at `:3610`),
  `src/Allegiance.ClientCore/State/ClientWorld.cs:6021` (`Apply(MissileLockUpdate)`),
  `src/Allegiance.ClientCore/State/MissileLockFeedback.cs:51` (`TipFor`).

## Fix

Test only, and it proves more than before:

1. The spawner no longer targets the enemy. After join and detection, the test waits until the client world has
   applied a lock frame for the armed launcher with **no target and lock 0**, and asserts no tips yet.
2. The test then sets `CommandTarget` and drives the server with a new `LockstepPumpUntil`: one sim tick, then wait
   until the client world holds that tick's (wire-quantized) lock value, then the next tick. Nothing depends on
   wall-clock pacing or on a receive thread keeping up.
3. New assertions: the first tick with a target gives a partial lock and exactly the partial tip; the build is
   monotonic frame by frame with several partial frames and stops on the full edge; the tips are exactly
   [partial, acquired] in that order; after the target leaves range the lost tip is the third and last, with the
   target id unchanged (a real "lost", not a retarget).

Observed client sequence, identical every run: `0.050 0.100 … 0.950 1.000`, then `0.000` (21 frames).

Two focused unit tests pin the rule that explains the old failure
(`tests/Allegiance.ClientCore.Tests/MissileLockFeedbackTests.cs`):
`None_Straight_To_Full_Posts_Only_Acquired`, and `ClientWorld_Posts_Tips_On_The_Frames_It_Applies` (frames
0, 1, 1 post only "acquired"; a stepped build posts partial, acquired, lost once each, in order).

Also changed:

- Stale native line numbers in comments corrected to the re-grepped ones (`console.cpp:935-950`, `941-948`) in
  `MissileLockFeedback.cs` and `ClientWorld.cs`. Comments only.
- `docs/parity/parity-matrix.csv` — new `hud` row "Missile lock console tips"; the rule had no row.
- `docs/TEST_FLAKES.md` — resolution section at the top; all 59 rows naming the test annotated "Resolved by P1020".
- `GLOSSARY.md` — "Lock-stepped pump (P1020)".

No timing limit or tier was changed. The per-frame wait in `LockstepPumpUntil` fails loudly after 10 s if a lock frame
is never applied; the 900-tick bounds are the ones the test already had.

Not changed, noted for the reader: `MissileTrackWarningLiveTests` and `ShopLauncherMountNumberingLiveTests` have their
own `SlowPumpUntil`, but they wait for end states (tracked, full lock), not a transient one, so they are not exposed
to this race.

## Tests

| Test | Alone |
| --- | --- |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **10/10 pass**, about 1 s each (plus 3/3 on the first cut) |
| `MissileLockFeedbackTests` (14 cases, 2 new) | 14/0/0 × 3 |

Before the fix, same worktree, alone: pass / pass / fail (38 s).

## Gate

| Step | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | 0 warnings, 0 errors |
| `tools/build-tools.sh` | all 140 tool projects built |
| Changed live test alone | 10/10 pass (Passed 1 / Failed 0 / Skipped 0 each) |
| New and changed unit tests alone | 14/0/0 × 3 |
| `scripts/parity-scorecard.py`, `tools/check-glossary-links.py` | both pass |
| One full fast suite (`tools/run-tests.sh --tier fast`, 7,200 s bound) | **FAIL overall**: 14,375 cases, 543.6 s — **Passed 14,366 / Failed 2 / Skipped 7**, plus 5 slow-Fast exceedances |

The task's test **passed in the sweep in 0.989 s** (it was the standing ~38 s failure and slow-Fast offender), so the
pass criterion — 10/10 alone and passing in the one full suite — is met. The suite as a whole is still red for
reasons outside this task:

- Failed: `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` ("B never saw A's
  death … after 2500 ticks") and `NpcWingmanRangeMaintenanceLiveTests.Wingman_throttle_responds_to_target_closing_speed`
  (expected 16, actual 272). Both pass 3/3 alone.
- Slow-Fast (limit 11.563 s): `P814GunnerTests` 15.136 s, `P796VariedLoadoutTests` 14.002 s, `SC2NaniteSupportTests`
  12.364 s, `P999CivStrategyTests` 16.349 s, `P1000CivProfileTests` 15.418 s. All pass 3/3 alone at 0.3–5 s per case.

All seven are ledgered in the P1020 section of `docs/TEST_FLAKES.md`; I did not investigate their causes.

Not run: `scripts/publish-guard.sh` (it needs push arguments; nothing was pushed). The change adds no secrets, emails,
home paths or media. No HUD or UI change, so no screenshots. Docker is not installed and no service, port or env var
was added. Nothing was committed.

---

**Native intent.** The pilot gets one console line each time the missile lock changes state — building, acquired,
lost — judged frame to frame from the magazine's lock, and never repeated while the state holds; switching target
does not nag "lost".

**Modern approach.** The server owns the lock and streams it to the owner on a best-effort, latest-wins channel; the
renderer-only client applies native's exact frame-to-frame rule to the frames it receives. The live test starts the
lock only once the client is in sync and lock-steps the server, so it checks the tip edges on a known sequence
instead of relying on timing.
