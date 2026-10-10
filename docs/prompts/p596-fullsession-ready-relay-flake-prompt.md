# P596 — fix the PLAYER_READY relay failure behind `FullSession_ThreePeers_…RealCrossSideKill` (fails 4/5 in isolation)

## Why this task exists

`tests/Allegiance.ClientCore.Tests/FullSessionKillIntegrationTests.cs:196`
`FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill` is
ledgered in `docs/TEST_FLAKES.md:33` as a 1/3 "live-wire timing, suspect
(P472)" flake. **It is now much worse.** Measured 2026-10-10 on master
`754ec51c`, run **alone** (`dotnet test tests/Allegiance.ClientCore.Tests
--filter "FullyQualifiedName~FullSession_ThreePeers"`, nothing else running):
**1 pass, then 4 failures in 5 runs**. P581c also saw it fail in isolation on
`adfd696d`. A test that fails ~70% of the time alone isn't load noise: either
the product or the test has a real problem.

Every failure is the same assertion, in **phase 2, before any kill happens**:

```
FullSessionKillIntegrationTests.cs:261
ready relay never reached both same-side peers' rosters
```

```csharp
peer1.SendPlayerReady(fReady: true, shipId: id1);
int relayed = PumpUntil(server,
    () => peer1.World.Roster.ReadyByShipId.TryGetValue(id1, out var r1) && r1 &&
          peer0.World.Roster.ReadyByShipId.TryGetValue(id1, out var r0) && r0, 4000);
```

So peer1's PLAYER_READY toggle doesn't reach peer1's and/or peer0's
`Roster.ReadyByShipId` within 4000 pump ticks.

## Scope

1. **Find the real cause. Don't widen the timeout first.** Determine for
   failing runs:
   - Does the server receive the ready message at all?
   - Does it relay it, and to whom?
   - Does each peer's client decode it into `Roster.ReadyByShipId`?

   Add temporary diagnostics if needed. Check against native: the ready
   message and its relay (`Inc/Messages.h` `DEFINE_FEDMSG` for the ready
   message; `FedSrv` handler; `clintlib/appmsg.cpp` client handler). Cite
   file:line. Candidates to check, not conclusions:
   - Ordering: the ready is sent before the server considers peer1 fully
     joined/seated, or before the mission/side state the relay requires.
   - The relay is sent to a side or roster that doesn't include the sender
     itself, or to peers not yet in that side's list.
   - `ReadyByShipId` is keyed differently from `id1`: ship id vs player id,
     or a ship id changed by a relaunch or hangar seat (P5xx changes).
   - `PumpUntil` doesn't pump the client peers' receive loops in the case
     that fails.
2. **Find when it got worse:** bisect between the P472 era (1/3) and now
   with a quick loop (≥ 10 isolated runs per probe commit). Name the commit
   and the change that raised the failure rate.
3. **Fix the root cause** in product code if the product is wrong (that
   matters live: players' ready state not reaching teammates). Fix the
   test only if the test is wrong, and say exactly why.
4. Update `docs/TEST_FLAKES.md`: remove or adjust the line with evidence.

## Out of scope

Other ledgered flakes; the kill/death phases after phase 2 (unless the
same root cause reaches them); lobby UI.

## Verification

- **≥ 30 consecutive isolated passes** of this test after the fix (show the
  loop and the count), plus ≥ 10 passes with the rest of
  `Allegiance.ClientCore.Tests` running.
- A focused wire test proving the ready relay reaches **both** same-side
  peers (and not the cross-side peer, if that's the native rule) that
  fails before the fix.
- Whole-solution build 0 warnings/0 errors; suite 2 consecutive runs on the
  final code via `scripts/run-tests.sh`.

## Deliverables

Commit on `master` (not pushed); report
`docs/reports/P596_FULLSESSION_READY_RELAY.md` (cause, native citations,
bisect result, fix, before/after pass rates).

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 2 consecutive runs **on the final code** (reduced from 3 per Domingo, 2026-10-09: the flakes are known/ledgered). Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 2× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 2× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

## ⛔ Shared-environment rules (Domingo, 2026-10-10)

- **Never modify, build in, check out or run from `~/allegiance-play2` or
  `~/allegiance-deploy`.** Those are Domingo's live play client and the 221
  deploy source. For live proofs, create your **own temporary worktree**
  (`git worktree add /tmp/<task>-proof HEAD`), use it, and remove it when
  done.
- **Never connect to, deploy to, or restart the 221 server (`192.168.0.221`).**
  Use a local server on this workstation (as the P572 tooling does).
- Stop every local server, client and bot process you started (by exact PID)
  before ending your session.

## 🔎 Reverse engineering the native binaries (when a task needs compiled-code evidence)

The native C++ source (`~/Development/Allegiance/src`) stays the primary
reference. When you need what the **compiler actually produced** (resolved
constants, inlined macros, vtable slots, struct offsets), symbol-aware
decompiles of our FZDebug builds are available. Use the CLI, not MCP:

```bash
G=~/Development/allegiance-tools/ghidra/ghidra-pdb.sh
B=~/Development/allegiance-binaries
$G $B/server/AllSrv.exe list 'CFSMission::'                     # names + addresses
$G $B/server/AllSrv.exe decomp 'CFSMission::GetCountSides$' 1   # decompiled C
# also: $B/client/Allegiance.exe, $B/interop/AllegianceInterop.dll
```
Projects are pre-analysed (calls take seconds); never re-import or delete
`$B/ghidra-projects`. Cite the function name + address in your report.
