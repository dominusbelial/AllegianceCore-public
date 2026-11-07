# P952 — NPC defender bisect and native-rule audit

The first bad commit is **`0d71403120411e84dc0e2256439de06bc97db9c2`
(P872)**, integrated into master by `e13e22b5`. This is an outdated test
setup exposed by a correct native change: the defender fixture has no weapon,
and native requires a damaging weapon in mount zero before acquiring a threat.
The test setup is corrected; production NPC behavior stays as authored.

Base: `bb840670`, branch `codex/p952`. No commits or pushes. The supplied
worktree's Git metadata is read-only; `git bisect` ran in a local shared clone
under `.scratch/p952/bisect/`, with its own writable Git metadata and a copy of
`local.env`. No stash, remote hosts, or other agents were used. All .NET commands
use `~/.dotnet/dotnet`; build/test scratch and temporary files stay inside
`.scratch/p952/`. Local artwork and native-source configuration are present.

## Bisect

Command: `git bisect start 48cdb2de a2f73c13`, then `git bisect run` with a
wrapper that builds `Allegiance.Core.Tests.csproj` and runs only
`NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`.
A build failure would return 125; no selected revision failed to build.
The first pass preserved the historical fixtures' own settings. The second pass
explicitly enabled immutable snapshots, encode-once, parallel builds, pool
transport, native ship updates, native GotoPlan, and server GC (the lane's
winning baseline); it selected the same five commits with the same results.
The table applies to **both** bisects (10 individual executions in total).
No production flag defaults were changed. The historical replay retains the
fixture inputs; the pinned replay uses environment overrides rather than edits
to any historical checkout. Both endpoints were supplied by the nightly
evidence in the brief, rather than counted as new test runs here.

| Revision | Build warnings/errors | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| `0295ecb6` | 0/0 | 1 | 0 | 0 |
| `4d84fffd` | 0/0 | 1 | 0 | 0 |
| `9a8115db` | 0/0 | 0 | 1 | 0 |
| `be83e4f7` | 0/0 | 1 | 0 | 0 |
| `0d714031` | 0/0 | 0 | 1 | 0 |

Every failure is the phase-one `Assert.Same(e1, wingman.AttackTarget)` at
original test line 179: expected enemy Ship, actual null. The bisect identifies
P872, rather than either proposed P865b/P869b suspect. Raw commands and results:
`.scratch/p952/bisect-git.log`, `bisect-run.log`, `bisect-results.txt`, and
`bisect-<full revision>-{build,test}.log` / `-results/defender.trx`.
The authoritative pinned-baseline replay is recorded in
`.scratch/p952/bisect-baseline-{git,run}.log`, `bisect-baseline-results.txt`,
`baseline.env`, and `bisect-baseline-<full revision>-results/defender.trx`.

## Mechanism and native check

P872 adds `Ship.HasDamagingPrimaryWeapon` and the early return in
`EvaluateWingmanDefend` (culprit `src/Allegiance.Core/Sim/Ship.cs:7913`;
dispatch source `:8170`). Its own report (`P872_REPORT.md`, SIM-0261) explicitly
calls out that new guard, citing the then-recorded line 2683. Re-grepping the
read-only native checkout for this task locates the actual branch at
**`Igc/shipIGC.cpp:2680`**: plan Defend, weapon at mount zero, projectile power
positive. The cooldown and charge-centered 1500-unit search follow at
`:2682-2690`; acquiring a threat writes the Attack plan at `:2695`.

`Server.SpawnNpcShip` constructs and places the drone without mounting weapons.
The old live fixture set only `DefendTarget`, so its assumption that this
unarmed ship would acquire an enemy became invalid at P872. The paired
`P872NativeBehaviourTests` already prove armed acquisition and unarmed
non-acquisition; the correct fix is to give the live acquisition fixture a
compatible damaging primary weapon. No production guard is removed.

Native return-to-charge behavior remains the test's intended contract:
**`Igc/shipIGC.cpp:2373-2406`** checks accepted Defend, abandons a threat more
than 3500 units from the protectee (`:2379-2381`), and restores the Defend plan
(`:2400-2406`). The live test continues to require acquisition, actual pursuit,
disengagement after the enemy flees, and actual movement toward the charge.

## Fix

The acquiring defender now mounts a compatible damaging primary from the real
core, then asserts `HasDamagingPrimaryWeapon` before assigning Defend. Its hull,
positions and all original behavioral assertions are preserved. The other
retargeting test stays unchanged. Existing P872 positive/negative tests pin the
armed/unarmed rule; they are included in the repeated affected-test checks.
The live proof is linked on existing parity row SIM-0261 and in the glossary.
The corrected fixture's first isolated run measured acquisition at 838 units,
pursuit to 219 units from the charge, disengagement after the enemy was moved
9500 units away, and return movement from 879.8 to 834.5 units. It passed its
first isolated run (1/0/0). The unmodified
dispatch-base fixture failed three isolated runs (0/3/0), all at original line
179. Evidence: `.scratch/p952/defender-base-{1,2,3}/defender.trx` and
`defender-fixed-first/defender.trx`.

## P798 comparison

The unmodified historical fixtures both passed the shorter `training_6.igc`
case: culprit `0d714031` **1/0/0**, 90 s; parent `c7e91ed2` **1/0/0**, 89 s.
These revisions predate the current native-GotoPlan/pool/native-update defaults.
The pair with the lane's required winning settings also passed at both
revisions: **1/0/0 each**, about 76 s total test duration, 73 s measured opening.
At both revisions, both sides saw **100% of sectors, 4/4 alephs and 67 rocks**;
first enemy stations were observed after **45 s / 41 s**. Side 0 raided a miner,
side 1 made a support call, and both chose **clash sector 2081, resource 36.3**.
The visibility audit checked 134 rocks and found **zero leaks**.

**P953 handoff:** P872 alone does not reproduce the P798 regression. The
culprit/parent pair both forms the clash with both historical defaults and the
winning baseline. This does not certify the later nightly revision or identify
the separate P798/SC4 culprit; no P798/SC4 fix was attempted.
Pinned-baseline evidence:
`.scratch/p952/p798-baseline-{0d714031,c7e91ed2}-results/p798.trx`.
The direct defender comparison at those same revisions is **fail at culprit,
pass at parent** (0/1/0 versus 1/0/0), in
`.scratch/p952/defender-pair-{0d714031,c7e91ed2}-results/defender.trx`.
Historical-default evidence: `.scratch/p952/p798-{0d714031,c7e91ed2}-results/p798.trx`.
P798/SC4 changes remain outside this task.

## Validation

The solution build passed with **0 warnings / 0 errors**. All repeated
baseline-setting checks passed with **zero skips**:

| Check | Passed / Failed / Skipped per run | Runs |
| --- | --- | --- |
| Defender acquisition/pursuit/disengage/return, alone | 1 / 0 / 0 | 3 |
| Defender closer-threat switch, alone | 1 / 0 / 0 | 3 |
| P865b carrier-retask + rock-disguise tests | 8 / 0 / 0 | 3 |
| P869 ownership + dispatch + review tests | 47 / 0 / 0 | 3 |
| P872 native-behavior tests, including armed/unarmed guards | 11 / 0 / 0 | 3 |

An earlier run without pinned baseline overrides saw one intermittent failure
in the **unchanged** closer-threat switch test (expected e2, actual e1 at the
final retarget assertion). Its three subsequent isolated reruns passed, as did
all three pinned-baseline runs. The observation is recorded in the flake ledger.

`tools/build-tools.sh` passed: **all 118 projects**, every build with zero
warnings/errors. Exactly **one** Fast tier (the lane's full-suite gate) ran:
`tools/run-tests.sh --tier fast --no-build --parallel 1`, with results under
`.scratch/p952/fast/`. **13,383 Passed / 25 Failed / 3 Skipped** across 13,411
cases in 1136.3 s. All nine infrastructure checks passed. Both defender tests
passed in-tier. The three skips are the optional private Meshy pack checks;
artwork-backed tests ran. Eighteen executions exceeded the runner's calibrated
11.138 s Fast limit (factor 1.114). **The full gate is FAIL**, not waived or
rerun. Quarantine contributed 19 passes and no extra failures/retries.

Invocation limitation: globally pinning the lane baseline also overrode
intentional legacy fixture settings. **Twelve** tests reproduce those conflicts
(port ship updates, thread-per-peer transport, snapshot-off and serial-builder
checks), then pass **39/0/0 across 36 control invocations** with conflicting
process overrides removed so their explicit configuration applies. No fixture
or production defaults changed. The original full gate remains failed.

Every failed or over-time method ran three isolated invocations: **35 methods,
105 invocations, 80/52/0 case executions**. The repeated failures are those
configuration conflicts, five already-ledgered bundle-path parameter cases
(scratch TMPDIR inherits ancestor `local.env`), and one intermittent MissileLock
failure (pass/pass/fail). The six other assertion-failure methods pass ×3 alone;
the profile method also passes ×3 with scratch `XDG_CONFIG_HOME`. P777's exit
measurement passes its assertions ×3 but remains near/over the calibrated time
limit (11.206/10.945/11.383 s). These observations and per-method P/F/S counts
are recorded in `docs/TEST_FLAKES.md` and `P952_assets/validation.json`.

Showcase never-parks: **base 5/0/0, changed tree 4/1/0** (Passed/Failed/Skipped), five isolated runs each.

Parking-limit failures: base 0/5, changed tree 0/5.

`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
ran alternately against unmodified dispatch `bb840670` and the changed tree,
one invocation at a time, using the same winning settings and local artwork.
No P700 or production source differs between those versions. Each observation
window, target-idle measurement and overall test outcome is retained. Load is
the host’s one-minute load average captured at invocation start:

| Pair | Base P/F/S, duration, target idle, starting load | Changed tree P/F/S, duration, target idle, starting load |
| --- | --- | --- |
| 1 | 1/0/0, 190.8s, idle 2.040s, load 21.56 | 1/0/0, 218.7s, idle 1.804s, load 13.67 |
| 2 | 1/0/0, 292.6s, idle 1.437s, load 15.37 | 0/1/0, 484.1s, idle 1.905s, load 10.29 |
| 3 | 1/0/0, 245.5s, idle 2.004s, load 5.80 | 1/0/0, 206.1s, idle 2.927s, load 7.36 |
| 4 | 1/0/0, 288.9s, idle 1.309s, load 9.44 | 1/0/0, 180.0s, idle 2.108s, load 6.10 |
| 5 | 1/0/0, 294.7s, idle 1.920s, load 4.59 | 1/0/0, 222.6s, idle 2.105s, load 24.22 |

Failed invocations (the five-run rates above retain these failures):

- fixed run 2: Most showcase ships must reach their objectives.

Raw evidence: `.scratch/p952/showcase-{base,fixed}-{1..5}/showcase.trx`
and the corresponding `-load.txt` files. The public JSON includes all ten
outcomes, durations and emitted P700 measurements. All ten observation windows
stayed below the five-second parking limit. The changed tree's one overall
failure was arrival (21/30 reached after 480 s), with longest target idle
1.905 s. The five-run overall rates are retained as observed; this test-only
fixture correction changes no source used by the P700 client test project.

No AOT publish or Live-training gate is applicable: this change touches tests/docs only and
changes no Core/Server implementation or training behavior.

The full tier runs with local artwork/native configuration, the winning
baseline settings, server GC, and one Fast-project worker. A scratch-only
`tailscale` shim exits without enumerating peers: the existing absent-CLI test
otherwise falls back to the actual CLI and probes the real tailnet. This
ensures the requested local-only lane never probes remote machines. NuGet
sources are the local package cache with auditing disabled. Raw outputs remain
under `.scratch/p952/`. After validation, the historical clone and temporary
runtime contents were removed; raw logs/TRX, commands and the local-only
Tailscale shim are retained for review.

The glossary link check (621 paths), parity scorecard validation, and
`git diff --check` pass. The edited parity CSV contains zero CR bytes.

Compact reviewable evidence: [bisect log](P952_assets/bisect.log) and
[validation counts/measurements](P952_assets/validation.json). Raw logs/TRX
stay in ignored task scratch; no raw test archives or media are added.

## Native intent

An unarmed guard escorts; a guard with a damaging primary may acquire threats
near its protectee. A fleeing threat beyond 3500 units causes a return to
Defend. Preserve those native rules and repair the fixture's missing premise.

## Modern approach

Use a real compatible core weapon and explicit setup assertions, retaining the
existing end-to-end movement checks. Local automated bisect and structured TRX
counts make the diagnosis and gate results reviewable without changing NPC
policy or addressing the separate showcase task.
