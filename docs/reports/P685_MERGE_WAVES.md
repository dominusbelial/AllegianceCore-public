# P685 — merge parity waves 4–7 into integ7

Resolved the five conflicted working-tree files while retaining integ7's P663
treasure behavior, P664 station sounds, P682 next-match resets, and P684 broadcast
pipeline alongside P674–P681. No Git write, staging, commit, or merge continuation
was performed. The index still contains the original unmerged entries; Claude
must stage these resolutions and commit the merge.

## Resolutions

- `Mission.cs`: only Started arms or fires periodic payday, using the loaded core
  interval and income. Retained treasure elapsed calculation, fractional per-sector
  regeneration, and phase advancement even when developments disable income.
  Both existing development opt-outs suppress income and cash generation;
  noncash treasure accrual remains independent of income. The separate initial
  starting-money payout is preserved. Added three P663 cross-branch cases and
  two P676 income cases for the merged controls.
- `sim-rules-matrix.csv`: audited all 473 identities against both merge stages;
  none is lost or has a worse status than either side. SIM-0134 takes P676's
  implemented payday rule, SIM-0135 takes P663's implemented regeneration,
  SIM-0208 keeps P674's tested but partial warp callback, and SIM-0209 retains
  the partial pickup callback while acknowledging the implemented generator.
  P663's SIM-0129 and P664's SIM-0421–0424 retain their implementation evidence;
  the P674–P681 improvements also survive. Updated stale payday cross-references
  and normalized the mixed line endings. [Row audit](P685_assets/merge-audit.json).
- `Content.mgcb`: exact union of both manifests, **468 unique build assets**
  (465 integ assets and 427 wave assets), including station cues, `accept`,
  `unmount`, and `vo_sal_playerwaiting`. Every source asset exists.
- `FedMsgDispatchCoverageTests`: recomputed **101 consumed** (84 native plus
  17 port additions) and **18 unconsumed**, accounting for all 102 supported
  native server messages. JOINED_MISSION and OBJECT_SPOTTED remain handled;
  integ7's SCORE_ROSTER_DELTA remains in the consumed registry.
- `TEST_FLAKES.md`: kept both histories, including P655c's correction and the
  P676/P678/P680/P681 observations.

## Merged scorecard

Both matrices validate. Complete rows alone earn credit; partial rows earn zero,
and `na`/`n/a` are excluded. Interface: **350/410 eligible rows, 1310/1482 weight**.
Simulation: **159/437 eligible rows, 586/1333 weight**.

```text
parity-matrix.csv: validated 438 rows
area | weighted (credit/weight) | unweighted (complete/eligible)
commander | 100.00% (81/81) | 100.00% (19/19)
commands | 90.19% (533/591) | 85.26% (162/190)
fedmsg | 86.02% (443/515) | 85.60% (107/125)
lobby | 76.47% (39/51) | 75.00% (9/12)
parts | 88.24% (135/153) | 85.00% (34/40)
screens | 86.81% (79/91) | 79.17% (19/24)
TOTAL | 88.39% (1310/1482) | 85.37% (350/410)
sim-rules-matrix.csv: validated 473 rows
area | weighted (credit/weight) | unweighted (complete/eligible)
callbacks | 33.33% (66/198) | 33.33% (22/66)
constants | 95.56% (129/135) | 94.87% (37/39)
periodic | 25.53% (12/47) | 20.00% (3/15)
sounds | 23.74% (141/594) | 18.42% (42/228)
updates | 65.33% (228/349) | 60.92% (53/87)
warnings | 100.00% (10/10) | 100.00% (2/2)
TOTAL | 43.96% (586/1333) | 36.38% (159/437)
```

## Verification

Build: **0 warnings / 0 errors**. [Build log](P685_assets/build.log).
Conflict-marker scan and `git diff --check` are clean.

Focused verification passed without failures or skips:

| Check | Passed |
| --- | ---: |
| P663/P674/P676/P681, payday and starting-money cases | 108/108 |
| Client dispatch, treasure, P675/P676/P678/P680 | 38/38 |
| Station sounds and wave UI/audio | 50/50 |
| Pipeline identity, snapshots, shared encoding, tiers, allocation and roster | 43/43 |
| P682 two-match combat and retirement/reset cases | 8/8 ×3 |
| Enabled-pipeline 15v15 next-match showcase | 1/1 |
| Real MCP purchase/death/pod/relaunch/refire | 2/2 |
| Autonomous miner cycles and mining/docking/sale/PAYDAY | 9/9 |

The [two-match proof](P685_assets/two-match-proof.json) retains shots, hits and
positive kills in both games for every port/native × pipeline off/on combination,
three repetitions each. The [showcase](P685_assets/showcase-proof.txt) migrated
30/30 bots with 2/2 humans connected, fresh identities and finale state, and zero
simulation-stage failures.

The separate **15v15, 90-second local battle** asserted positive credited kills
and recorded **30/30 bots reached, 12 kills (10/2), 17,869 shots, 9,996 hits,
55.9% hit rate, zero friendly hits**. See [battle receipt](P685_assets/battle-proof.txt).
The [relaunch](P685_assets/relaunch-proof.txt) and [mining](P685_assets/mining-proof.txt)
proofs use production local TCP paths; the mining group includes autonomous
repeated mine/sell cycles and exact wire PAYDAY delivery.

Unfiltered full-suite results (assemblies run sequentially, including live-timing cases):

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Lobby | 19 | 0 | 0 |
| Core | 4,539 | 0 | 0 |
| ClientCore | 996 | 0 | 0 |
| MonoGame | 2,185 | 0 | 3 |

**7,739 passed, zero failed, three existing optional Meshy-pack skips.** No
isolation retries were needed. All 15 verification invocations exited zero;
validation finished with `uncleared: []`. No assertions, deadlines, quarantine
traits, or skip conditions were relaxed. See the
[invocation ledger](P685_assets/results.json), [summary](P685_assets/summary.json),
and the per-run compressed TRX/logs in [P685_assets](P685_assets).
[Source fingerprints](P685_assets/verified-files.sha256) still match the tested
files. The [Core preservation extract](P685_assets/core-preservation-proof.json)
contains 119 passing treasure, economy, wave ordnance and wingman cases from
the full suite.

Reproduce from the repository root with local artwork/native-source paths in
`local.env`:

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p685/nuget-cache dotnet build AllegianceCore.slnx --no-restore -m:8 -v:m -p:UseSharedCompilation=false -nodeReuse:false
python3 scripts/parity-scorecard.py
python3 docs/reports/P685_assets/validate.py all
```

The runner executes assemblies sequentially, enables the existing local P660
showcase, keeps live-timing cases in the full suites, and saves logs, compressed
TRX, and JSON counters. These checks use local loopback servers and the real
artwork/native-source corpus.
