# P1085 — Backfill probe recovery

Implementation and affected qualification are complete. Builds and all affected tests pass; the clean full Fast gate remains unmet on existing Core receipt/timing families. Changes remain in the working tree on base `d3268557c`. No commits, pushes, deployment, VPS access or bot-host access.

## Brief items

| Item | Status | Evidence |
|---|---|---|
| Probe recovery preserving seating and readiness | done | Both new loopback methods passed 3 isolated runs: exhausted reconnects, refill to 2v2, initial validation failure, disposal, original side preference, PLAYER_READY and commander registration. |
| Bounded exponential retry with jitter for probe and commanders | done | Schedule theory passed all 3 rows in 3 fresh hosts; 3 outage proofs measured paced fresh probe and commander dials. |
| Stalled liveness signal and operator documentation | done | All 3 not-ready reasons passed 3 isolated runs, including deduplication and rearming. Operator guide documents the 600-second signal and continued retry policy. |
| Loopback outage/refill proof and existing backfill regressions | done | 51/51 cases passed across 39 exact-method hosts: 13 methods × 3 runs, including existing rotation/refill, five-seat AFK and mixed 10v10 recovery scenarios. No skips. |
| Build, tools, repeated affected tests and full suite | not done | Solution and all 156 tools built with 0 warnings/errors; affected tests passed 51/51. Full Fast remains red on existing receipt/timing families. The seven flagged methods ran 3 times alone: 83/84 passed; mining was pass/fail/pass. Unrelated failures outside this brief prevent certifying a clean gate. |

## Implementation

`BotManager.Backfill.cs` creates and readies a private replacement session after terminal probe loss, disposal or failed initial validation. An active owned reconnect window gets to finish first. Recovery reuses the startup probe's roster-index preference or normal server auto-pick; it waits for SIDE_ROSTER after LOGONACK so the preference cannot race the incoming roster. The selected side registers the fresh probe in `_commanders`, replacing the old probe alias and any superseded commander. Publication uses the behavior and research owner gates. Old and failed sessions are disposed through the existing telemetry/ownership cleanup.

`BackfillDriver.cs` tracks its maintenance task and drains it on shutdown. Only a not-ready tick with no pending admission can authorize replacement. Admissions remain blocked until that maintenance operation has completed and the next roster snapshot is read. A changed probe also gets the normal two-second roster settling period, even when its connection-local generation equals the old probe's generation. Commander maintenance runs independently of the probe's research briefing, which owned reconnect clears, and deferred sides do not prevent other sides from recovering.

`BackfillRetry.cs` supplies monotonic wall-time admission delays: **1, 2, 4, 8, 16, 32, then 60 seconds**, with up to 20% positive jitter and a strict 60-second maximum. Successful seating resets the sequence. Probe recovery, per-side commander creation and failed combat admissions use this policy. Existing supervisor recovery had an inline fleet-specific delay rather than a reusable admission helper. Owned transport resume behavior and route selection remain unchanged.

The chosen liveness response is one `[backfill] stalled seconds=… reason=…` log after **600 continuous wall seconds** without a ready roster. Reasons distinguish probe loss/disposal/reconnect, world not ready and stage over. A ready roster rearms the signal. The driver continues trying, retaining surviving sessions during a partial outage; forced process exit would discard those sessions. `[backfill] probe lost …` and `[backfill] probe restored …` occur once per recovery episode; retry diagnostics occur once per actual failed attempt. `docs/PUBLIC_PERPETUAL_BATTLE.md` explains the signals and operator response. No systemd unit change is needed.

This is external orchestration. Existing native active-player readiness is reused (`WinTrek/WinTrek.cpp:8188-8195`), with no new game rule. Tracking: `docs/parity/p1085-backfill-recovery.csv`; glossary entry added. Server code, bot strategy and transport configuration are unchanged.

## Isolated qualification

Every method ran **3 times alone in a fresh process**, including all theory rows. Artwork was present and no qualification case skipped.

| Coverage | Passed cases |
|---|---:|
| Exponential schedule, jitter bounds, cap after many failures, success reset | 9/9 |
| Stall threshold, changing reason deduplication, readiness rearming | 9/9 |
| Link outage, actual owned reconnect exhaustion, probe/commander recovery and 2v2 refill | 3/3 |
| Never-validated startup and later disposed probe; forced roster side 1 and native ready echo | 3/3 |
| Existing P1071/P1078 seat-policy methods | 18/18 |
| Existing human departure, refill, match rotation and finale membership | 3/3 |
| Existing five-seat AFK/wake live scenario | 3/3 |
| Existing mixed TCP/WebSocket 10v10 reconnect route preservation | 3/3 |
| **Total** | **51/51** |

The outage fixture forwards real TCP bytes between the local server and driver, closes every original bot socket, and rejects all dials long enough for every original session to exhaust its real reconnect window. It verifies at least three fresh failed probe and commander admissions with exponential spacing before restoring the link, then requires a new probe, disposal of original sessions, both sides at the target seat count and the correct commander alias/ready state. The separate startup/disposal case verifies the original optional side preference. The outage proof uses a one-second owned reconnect window and two seats per side; production driver retry delays are unchanged. Deterministic schedule tests cover the 60-second cap after 10,000 failures, and liveness tests cover the 600-second threshold without a ten-minute wall-clock sleep. Captured logs show exactly one lost/restored pair per recovery episode: 9 pairs across the six live proof executions, without per-tick duplicates.

Sanitized exact-method results, dial timings, state-transition counts and source hashes: [validation.json](BACKFILL_PROBE_RECOVERY_P1085_assets/validation.json). Raw machine-specific logs remain in ignored test-output and scratch directories; only the sanitized receipt is part of the change. Development testing exposed and corrected the cleared-briefing dependency and ACK-before-roster preference race; those failures were not classified as flakes. Final review also tightened the admission/recovery boundary before the complete final-source repetition set above.

## Broad qualification

- Full solution build: **0 warnings / 0 errors**, SDK **10.0.404**.
- `tools/build-tools.sh`: **all 156 tool projects built**, zero warnings/errors. The initial attempt encountered a missing shader dependency in the read-only package cache; restoring it into the scratch package cache resolved the environment failure without code changes.
- Full Fast sweep: **16,148 cases, 16,139 passed / 2 failed / 7 skipped**, **573.1 seconds**. All nine infrastructure checks passed. The two failures are the already-ledgered warp-transit and miner-capacity receipt methods. Five assertion-passing cases exceeded the calibrated **11.525-second** limit; every exact method is already in the flake ledger. All seven distinct failed/over-limit methods ran 3 times alone, including every theory row: **83/84 cases passed across 21 hosts**. Warp transit passed 3/3; mining was pass/fail/pass. Every timing case passed below the same limit in isolation. **Fast remains red.**
- The initial all-tier invocation continued into extra Live, including a passing 605-second flight soak. It was stopped during the unrelated real-window training replay matrix to reserve the brief’s three-hour cap for required flake qualification. Live has **no complete verdict**, and Perf did not run. Partial output reported **40 passes / 4 skips / no failures**; exact per-project counts are retained in the receipt. The required repository light gate is the full Fast sweep; affected Live recovery scenarios already passed 3 isolated runs each.
- All glossary paths and all parity matrices validated; final whitespace and added-content hygiene checks passed. Source hashes match the receipt and HEAD is unchanged. No media was added to the change.
- Infrastructure final direct recheck: **9/9 passed in 0.031 seconds**, below the calibrated Fast limit.

## Broad-run isolation details

| Method | Full Fast observation | Three isolated runs | Maximum case seconds per host |
|---|---|---|---|
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Passed, civ 25: 14.142 s | pass / pass / pass; 6/6 cases | 4.913 / 4.807 / 4.956 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | Passed, civ 35: 12.345 s | pass / pass / pass; 9/9 cases | 5.987 / 5.700 / 5.398 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | Passed: 14.822 s | pass / pass / pass; 3/3 cases | 3.200 / 3.202 / 3.203 |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Unexpected queued mining receipts after capacity | pass / **fail** / pass; 2/3 cases | 1.794 / 1.806 / 1.757 |
| `P1000CivProfileTests.Every_loaded_civ_has_immutable_specs_and_legal_routes` | Passed, civ 3: 11.939 s | pass / pass / pass; 30/30 cases | 1.562 / 1.551 / 1.562 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Passed, civ 5: 11.934 s | pass / pass / pass; 30/30 cases | 4.385 / 4.432 / 4.408 |
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | Only old-sector status reached the peer | pass / pass / pass; 3/3 cases | 0.303 / 0.309 / 0.303 |

The single isolated failure is the same `Assert.Empty` mining-receipt family as
the sweep failure: additional ASTEROID_MINED reports were queued after capacity.
It is recorded in `docs/TEST_FLAKES.md`; no assertion, timeout, calibration,
quarantine, filter or game behavior was changed to obtain a pass. The full Fast
gate therefore stays red, with this limitation explicitly left `not done` in the
brief table. Fixing unrelated Core mining/warp tests or server behavior is outside
the requested driver change. All requested implementation and affected-test
proofs passed.

## Environment and reproduction

The SDK location explicitly requested by the brief does not exist in this workspace. Verification used the installed local SDK instead; this is the command-location deviation. Existing artwork resolution supplied the corpus. NuGet package/HTTP and profile caches used writable scratch directories because the sandbox makes the usual user directories read-only. No VPS or bot host was accessed.

`DOTNET` below denotes that installed SDK:

```sh
"$DOTNET" build AllegianceCore.slnx -v:quiet -nologo
DOTNET="$DOTNET" tools/build-tools.sh
"$DOTNET" test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj --no-build --filter 'FullyQualifiedName=Allegiance.ClientCore.Tests.P1085BackfillRecoveryLiveTests.Exhausted_probe_and_combat_sessions_are_replaced_and_both_sides_refill_after_outage'
DOTNET="$DOTNET" tools/run-tests.sh --tier fast --no-build --results-dir TestResults/p1085-fast
python3 tools/check-glossary-links.py
python3 scripts/parity-scorecard.py
```

The isolated repetition inventory and individual outcomes are retained in the linked receipt, including the exact names of all 13 methods.

The actual broad invocation used `--tier all --results-dir TestResults/p1085-all`; its Fast stage completed before the extra Live continuation was stopped. The command above reproduces the repository light gate directly.
