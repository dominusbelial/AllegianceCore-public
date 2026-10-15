# P490 — final live proof: capture needs downed shields (P450) and side elimination (P455/P455b)

## Why this task exists

P485 (`701cf14b`, `docs/reports/P485_LIVE_PARITY_PROOF.md`) fixed the frozen
bots, and proved **P451** live: shields declined and the base-under-attack
alert fired. Two proofs are still open:

- **P450 capture.** The boarding hull, a Hvy Troop Transport with
  `c_habmBoard`, never reached the garrison, because it was bounced by the
  transit-hub asteroid field.
- **P455/P455b elimination.** A single bomber left the garrison's hull at
  1.0.

**P489** gives the client bots native's dodge law. Check that its commit
exists (`git log --oneline | grep P489`). If it doesn't, **stop and
report**.

## Scope

1. **`capture_attempt` live, on a normal map and the debug map.** The
   report must show:
   - `refusalConfirmed: true` at full shields;
   - shields then driven below the downed-shield constant by the escort;
   - `captured: true`, with the station's observed side change.

   If refusal can't be observed because docking at full shields isn't
   attempted, fix the preset's choreography so it really tries first.
2. **Elimination.** Add a size argument to `bomber_run` if one doesn't
   exist, or a dedicated elimination preset (this was the planned P474b).
   Use enough bombers with real anti-base weapons to destroy the defender's
   **last restart station**. On a 2-side map the report must show:
   - the station's death broadcast;
   - `SIDE_INACTIVE`;
   - "&lt;side&gt; is no more.";
   - `GAME_OVER` with native's "won by outlasting all other sides." reason
     text;
   - then P439/P480's rotation back to NotStarted, with players kept seated.
3. If a live run exposes a real bug in P450 or P455, report it with evidence
   and a named follow-up, and **don't** fix it here.

## Explicitly out of scope

- The bot steering (P489).
- A minefield preset, which is still a named follow-up.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report the actual numbers you measured. Don't claim a pass rate you
  didn't observe.** P482's "0/20" claim didn't reproduce: it measured 1/15
  when re-run.
- **Commit before you finish.** The chain's guard stops on a missing commit,
  and P482's session ended without one.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`, then `git log -1` to confirm the commit
   exists.
2. The report `docs/reports/P490_LIVE_PROOF_CAPTURE_ELIMINATION.md`.
