# P712 — P705 tech-tree merge onto parity waves 8–10

## Implementation

The working tree resolves all three marked conflicts and an additional, automatically
merged duplication in the server bucket event seam. No Git command, commit, push,
rig connection or deployment was performed by the agent. The requested repository
hygiene tests may invoke their own read-only Git queries as part of the real suites.
The merge index is intentionally untouched for the dispatcher.

`Bucket.SetTimeAndMoney` is the sole funding-snapshot API. Both previous
`SetFunding` callers now use it; the duplicate API was removed. A snapshot updates
money/time, derives the managed completion/latch from the native time threshold,
and then notifies observers. Full obsolete-research snapshots therefore mark the
bucket complete without applying development effects; a partial replacement or
`SetEmpty` permits a later actual completion. `SetEmpty` and `Terminate` each have
one implementation, preserving P691's public API and notification-before-removal
ordering.

Both the side's update pass and its technology-reconciliation pass iterate stable
bucket identities and check live ownership. Removing an earlier, current or later
bucket cannot shift the iteration onto the wrong element, invalidate an enumerator,
or update a removed bucket. Each surviving bucket is advanced once in catalog
order. Reconciliation runs before the update pass and after each funded, unfinished bucket update,
checking development and building techs, so a completion can obsolete research,
transfer station investment, or remove prerequisites before the next bucket advances.

`ServerHost` now has one concrete bucket-change event and one interface callback.
The duplicate P703 field/callback and its overlapping lifecycle subscription were
removed. One server handler carries progress, empty and authoritative money
snapshots, retaining the side audience and the whole-mission prosperity audience.
This also removes the two independent broadcasts previously attached to an empty
notification. The existing donation wallet/status transaction owns its one status message; its AddMoney callback is suppressed only for that bucket, so wallet debit and notification keep native ordering while direct snapshots still broadcast.

`docs/TEST_FLAKES.md` preserves the union: P699c, P705 and P703 observations all
precede the unchanged shared ledger.

## Native evidence

All paths in this table are relative to `~/Development/Allegiance/src/` and were
read directly from that checkout.

| Rule | Native source |
| --- | --- |
| Funded buckets accrue elapsed milliseconds, complete at the joint money/time gate, clamp to proportional funded time, and update their clock every call | `Igc/bucketigc.h:40-73` |
| Completion state is determined by the invested-time threshold | `Igc/bucketigc.h:175-178` |
| Development completion retains full investment; repeatable production resets after its typed callback | `Igc/bucketigc.h:180-256` |
| Authoritative funding snapshots update time and money before MoneyChange | `Igc/bucketigc.h:274-279` |
| Donations cap at the outstanding cost and notify even for zero actual spend | `Igc/bucketigc.h:281-292` |
| Emptying clears money and time before Emptied | `Igc/bucketigc.h:294-299` |
| Termination notifies while the bucket still belongs to its side, then removes it | `Igc/bucketigc.cpp:87-103` |
| A side advances attached buckets once in list order | `Igc/sideigc.h:41-51` |
| Both building and development tech changes trigger bucket reconciliation | `Igc/sideigc.h:160-184,194-204` |
| Obsolete development is silently marked full; lost research prerequisites discard investment | `Igc/sideigc.h:619-636` |
| Obsolete station construction transfers money/time to its successor, then empties the predecessor | `Igc/sideigc.h:640-662` |
| Other unbuyable funded production is emptied | `Igc/sideigc.h:667-670` |
| Donation wallet debit and MONEY_CHANGE precede one BUCKET_STATUS; prosperity uses the mission audience | `FedSrv/FedSrv.CPP:7154-7196` |

Stable snapshot traversal and the managed completion latch are implementation
choices for these native rules. Authoritative client progress snapshots are the
existing managed-port adaptation.

## Diagnostic performance correction

The initial merged loop checked technology masks after every catalog entry, even
idle and already-completed buckets. P705's first 4/35 proof timed out at its
unchanged 300-second deadline while research was still progressing: simulation
had reached 11,979.8 seconds, below the approximately 15,300 seconds needed by
this matchup in P705's earlier successful proof. The other three cases passed.
This is retained as a diagnostic failure, not classified as a load flake. The unchanged diagnostic repeat also timed out; see the [timeline](P712_assets/diagnostic-proof/4-35.json) and [last sample](P712_assets/diagnostic-proof/4-35-sample.json).

The corrected loop checks again only after funded, unfinished work, the only
`Bucket.Update` branch that can invoke completion/progress callbacks
(`Igc/bucketigc.h:44-71`). All buckets still update their clocks. Both development
and building tech changes are still reconciled before the next bucket can run.
A controlled idle-catalog benchmark (2,000 ticks, two sides, 622 buckets) measured
636–646 ms and 758,928,168 allocated bytes initially, versus 15.4–15.8 ms and
12,528,168 bytes after the correction. See [measurements](P712_assets/side-pass-benchmark.json)
and [benchmark source](P712_assets/SidePassBenchmark.cs). This quantifies the
avoidable side-pass overhead; it does not claim an equivalent whole-server speedup.

No full suite had started before this correction. Initial focused checks and the
failed proof are retained in the [diagnostic ledger](P712_assets/verification.json); final certification uses the corrected code.

The corrected-build Core triplets exposed a second deterministic merge overlap:
`LiveBucketTests.Full_funding_plus_build_time_fires_completion_seam_only` received
two zero-time funding statuses. It failed its unchanged single-message assertion
in all three exact-method retries. The event-driven MoneyChange broadcast and the
native donation transaction both sent status. The server now suppresses that
callback broadcast only for the bucket currently inside the donation transaction,
using `try/finally` to restore the scope. The transaction retains its single
wallet-debit-then-status sequence (`FedSrv/FedSrv.CPP:7154-7196`). Direct funding
snapshots continue through the shared handler. The live P691 audience test now
checks this direct snapshot after a donation for both side and prosperity scopes.
No assertions or deadlines were weakened. Diagnostic failures are preserved in
[the intermediate Core ledger](P712_assets/fixed_core/verification.json).

## New regression coverage

`P712BucketMergeTests` adds three methods / five cases:

- A real development-completion callback changes development techs. Reconciliation
  marks another paid development obsolete without completing it again. Its money
  callback terminates a previous/current/future bucket (three cases), plus another
  future bucket. Remaining buckets advance without skips; another tick verifies
  no repeated development effects.
- A completion removes the prerequisite base without changing development techs.
  Later paid research is emptied and terminated from inside the empty callback
  before it can complete, while its surviving successor still advances.
- Full and partial funding snapshots, then `SetEmpty`, exercise the shared
  completion state and ensure snapshot application itself fires no completion.

## Final focused load-flake review

The third scheduled P705 invocation passed 25/3, 1/2 and 4/35 but timed out
in 101/1 match two at simulation time 18,609.25. Match one had completed;
side 0 obtained its upgraded launch in match two, while side 1's five bots
remained in hull 14 despite continuing research. The underlying cause is not
established. The [failed timeline](P712_assets/diagnostic-proof/final-focused-101-1.json)
and [last sample](P712_assets/diagnostic-proof/final-focused-101-1-sample.json)
are preserved.

That exact data case then passed **3/3 additional isolated reruns**, both
matches each time, with unchanged binaries, assertions, 300-second deadline,
tick batching, map parameters and movement/economy rules. See the
[isolation summary](P712_assets/isolation-101-summary.json) and full timelines
[1](P712_assets/isolation-101/run-1.json),
[2](P712_assets/isolation-101/run-2.json),
[3](P712_assets/isolation-101/run-3.json). This meets the brief's load-flake
rule and is recorded in `docs/TEST_FLAKES.md`. No full suite had started when
this isolation triplet finished.

## Verification

The whole-solution build passed with **0 warnings / 0 errors**. See
[build log](P712_assets/build.log), [selected exact methods](P712_assets/final/selected-methods.json),
[final invocation ledger](P712_assets/final/verification.json), and
[source fingerprints](P712_assets/source-fingerprints.json).

The final focused cohort completed **186 exact methods × 3 invocations**:
**558 invocations / 884 case passes / 1 timed-out case / 0 skips**. The single
101/1 timeout passed its additional unchanged exact-case triplet, as documented
above. All five new regression cases passed three isolated runs each. The
standalone funding-status repair check also passed 3/3 before this cohort.

The unchanged showcase never-parks gate passed **10/10 standalone runs**, with
zero failures/skips. The maximum observed speed-below-1 interval with a target
was **2.015 seconds**, below the unchanged five-second assertion. Every run
observed at least 24/30 ships at their objectives, real shots/hits/kills and
zero simulation-stage failures. See [all ten summaries](P712_assets/showcase-results.json).
The conditional fresh-base comparison was not needed because no gate failed;
[P707's recorded held-escape result](P707_WAVES_PARKING.md) was also 10/10.

The first Core sweep completed **4,760 pass / 1 fail / 0 skip**. Its
`CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate`
compared the current server hull 33 against the earlier export's hull 92.
The unchanged exact method then passed **3/3 isolated reruns**, with no source,
assertion or receipt-deadline changes. This meets the lane's load-flake rule;
see [isolation results](P712_assets/core-export-isolation.json) and the ledger.

Exactly two complete suite sweeps are running; final results will replace this
paragraph when complete.
