# NT-1 — triage of the two untriaged nightly failures

Base: master `30c8912c` (re-checked at dispatch: `git log -1` = `30c8912c`).
Worktree: `/home/domingo/allegiance-pi-nt1`, branch `pi/nt1`. No commit/push; changes
left in the working tree. Raw logs/TRX are outside the repo at
`~/allegiance-evidence/NT1/`, linked below.

## Summary

| Failure | First bad commit | Classification | Fix |
|---|---|---|---|
| `BroadcastSnapshotTests.Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes` (4/4 cases) | `1baf07b0` P762 mixed sim + interface wave | **design** — intentional native-parity wire change; the test's frozen golden was stale | regenerate the golden at `30c8912c`; equality checks unchanged |
| `SC2NaniteSupportLiveTests.Research_x16_showcase_support_repairs_ships_and_stations_on_both_sides` | `bb82bb5c` SC-5 cloak actually used in rig matches | **design** — legitimate native-parity loadout/research-plan change removed the natural station-damage stimulus on one side; support repair itself is not broken | scenario/fixture adjustment (training-style station damage per side), threshold untouched |

Both failures were reproduced at the base before any edit. No threshold, timeout,
tier, retry, skip or quarantine was added or changed.

---

## 1. BroadcastSnapshotTests — stale frozen broadcast golden

### First bad commit (real `git bisect`)

Range `fac0da07` (green) … `e96b9943` (red), `--first-parent`, filter
`FullyQualifiedName~BroadcastSnapshotTests.Recorded_ticks`:

```text
good 50830ab5
bad  074c15a8
good 73296846
bad  1baf07b0
good 98944527
first bad commit: 1baf07b0  P762: mixed sim + interface wave
```

`98944527` passes 4/4; `1baf07b0` fails 4/4. Transcript:
`~/allegiance-evidence/NT1/bisect-summary.txt`.

### Root cause — exactly one wire byte changed, and it is native-correct

I instrumented the test's frozen transcript (temporary local dump, removed afterwards)
and compared the per-peer frames of `98944527` (good) and `1baf07b0` (bad). For **all
four cases** the frame counts and every lane/tick/guarantee match; the only differing
bytes are:

* **offset 29** of every `FM_S_HEAVY_SHIPS_UPDATE` (176) packet, and
* that packet's trailing **4-byte checksum**.

15,120 heavy packets differ per case. Offset 29 is `bpTargetShield`
(`src/Allegiance.Core/Wire/HeavyShipsUpdate.cs:380,495,573`); at the good commit it
carried a real fraction (`0, 12, 24, … 240` from the fixture's `SetShieldFraction`),
at the bad commit it is `255` in every one of the 15,120 packets. Full diff:
`~/allegiance-evidence/NT1/broadcast-byte-diff.txt`.

The change is P762's native target-fraction switch
(`src/Allegiance.Server/Server.Broadcast.cs:3004` `ResolveTargetFractions`, and
`src/Allegiance.Core/Sim/Ship.cs:574` `HasMountedShield`). Native:

* `FedSrv/FedSrv.CPP:4079` sets only `bpTargetHull` to 255;
* `FedSrv/FedSrv.CPP:4093-4098` writes the ship target's hull, and writes
  `bpTargetShield` **only if `GetMountedPart(ET_Shield, 0)` is non-null**;
* `FedSrv/FedSrv.CPP:4108-4114` writes hull + shield for a station;
* `FedSrv/FedSrv.CPP:4117-4122` writes hull only for probe/asteroid/missile;
* the native client applies the shield byte only when a shield is mounted
  (`clintlib/appmsg.cpp:752-754`).

The fixture's 144 target ships have no mounted shield part, so native never sends a
shield fraction for them. The port must pick a deterministic byte; it uses 255, the
same "unknown/absent" convention as `bpTargetHull` (`FedSrv.CPP:4079`). That value is
never consumed by a native client for a shieldless target. P762's own test pins this
exact rule (`tests/Allegiance.Core.Tests/P762TargetFractionLiveTests.cs:107-109`,
"shield stays 255 (no mounted shield)"). This is an intentional native-parity change,
not a code regression: the test's golden was not regenerated with it.

### Why all four cases changed for the same reason

The fixture mounts no shields, and the shield byte is per-recipient target state, so
every case (native on/off × parallel on/off) changed in the same 15,120 packets at the
same offset. The test's protective assertions are untouched and still pass:

* `off`/`on`/`encode-once` still produce identical bytes, order and lanes per peer;
* only the `Assert.StartsWith(golden, …)` pin was failing at `tests/Allegiance.Core.Tests/BroadcastSnapshotTests.cs:287`.

### Later intentional wire changes included in the HEAD golden

The first bad commit is P762, but the hash at HEAD (`30c8912c`) also reflects
intentional native-parity wire work merged after it. Frame comparison
`1baf07b0 → 30c8912c` (same fixture) shows only these changes:

| Commit | Wire effect | Frames |
|---|---|---|
| `61726b83` P773 | retires the port-only `OwnShipKillBonusUpdate` (fmid 212) | −15,120 |
| `c185229e` P775 | removes lazy `PLAYERINFO` (fmid 10); joiner gets every ship as `ShipStatus` (fmid 90) | −10,224 / +68,194 |
| `c49b4d61` P774 | in-mission roster shape (fmid 209→218) | 144 |

So the regenerated golden is the correct pin for the current intentional wire, not a
masking of an unintended diff.

### Fix

`tests/Allegiance.Core.Tests/BroadcastSnapshotTests.cs:280-286` — golden regenerated
from the `30c8912c` transcript:

```text
(false,false) BF92D4A2003D66CC
(false,true)  D74C1E1854875051
(true,false)  3C1876D181A6A011
(true,true)   E06266AEDF1980C0
```

The comment above it records the three intentional changes and their native
references. No production code changed.

### Verification (3× alone)

`~/.dotnet/dotnet test tests/Allegiance.Core.Tests/... --filter "FullyQualifiedName~BroadcastSnapshotTests.Recorded_ticks"`

| Run | Result | Duration |
|---|---|---|
| 1 | 4/4 passed | 43 s |
| 2 | 4/4 passed | 45 s |
| 3 | 4/4 passed | 43 s |

---

## 2. SC2NaniteSupportLiveTests — station repairs were never stimulated on one side

### The nightly range premise was wrong

`939b163a` (the "green" night) **does not contain the test**: the file was added by
`a00e842f` (Batch 7 / SC-2, 2026-10-19), which is inside the quoted range. The green
night was simply a night with no such test. Running the exact test at `a00e842f`:

```text
a00e842f PASS (9 m 13 s)
```

I then bisected the rest of the range with the real test (`a00e842f` good … `fac0da07`
bad, `--first-parent`, filter `FullyQualifiedName~SC2NaniteSupportLiveTests.Research_x16`):

```text
bad  a4979210
good c4211708
bad  bb82bb5c
good 7dbf9a35
first bad commit: bb82bb5c  SC-5: cloak actually used in rig matches
```

Transcript: `~/allegiance-evidence/NT1/bisect-summary.txt`; scripts:
`bisect2.sh` in the same directory.

### Root cause — one side never takes station hull damage

At HEAD the test fails with `side=0 ship=405 station=373; side=1 ship=503 station=0`
(20 m 08 s, `~/allegiance-evidence/NT1/sc2-head.log` and `sc2-live-head.log`). The
end-of-match SC-1 counters show why:

```text
side 0: station-damage 0    nanite-ship 417  nanite-station 373
side 1: station-damage 13   nanite-ship 503  nanite-station 0
```

The repair machinery is healthy: side 0's five support pilots perform 373 real
station repairs, ships are repaired naturally on both sides, and the controlled
sibling test still passes. The failing side's stations never take hull damage, so
there is nothing for its support pilots to repair and the counter cannot move.

The same log shows the stimulus disappeared on side 0's strike side:

* every side-0 strike wave targets only `station#1`, side 1's **Garrison**, and all
  four end with `arrived=1 peakOnTarget=1 … stationHull=1.000 stationShield=1.000`
  — the pilots arrive but their temporary Fighter/Scout loadout cannot damage that
  station type;
* side 0's research finale cannot find a station-damaging loadout:
  `[finale] side 0 … no buyable bomber strike loadout: no bomber hull passes the
  side + station tech gate … targetType=44`;
* side 1's finale *does* get strike refits and damages side 0's forward stations
  (13 `station-damage` events, one `station-kill`).

This is exactly what SC-5 changed. SC-5 is legitimate native-parity work (a real
mounted cloak is now used; native gate `Igc/shipIGC.cpp:1599-1610`, report
`docs/reports/SC5_CLOAK_ON_RIG.md`), but it also:

* moved each civ's tactical/stealth research branch ahead of its anti-base branch in
  `data/showcase-plans/PCore001/civ25-plan_a.json` and `civ3-plan_a.json` (large plan
  rewrites in `bb82bb5c`), and
* ranks cloak first in scout/strike loadout equipment
  (`tools/allegiance-mcp/TechLoadoutSelector.cs:118-123,357,384`; native mounted-cloak
  gate `shipIGC.cpp:1599`).

Inside this match's ~1,900 simulation seconds that leaves side 0 without the
station-damaging loadout the scenario used to get for free. The repair code itself did
not regress; the scenario's *only* station-damage source became one-sided. Classified
**design / scenario fragility after an intentional native-parity change**, not a
support-regression.

### Fix — adjust the scenario (threshold untouched)

`tests/Allegiance.ClientCore.Tests/SC2NaniteSupportLiveTests.cs:66-91`:

* the ticker now drains a small sim-thread action queue (same pattern as the sibling
  test below), so fixture actions never race `TickSim`;
* while a side still has fewer than 10 `NaniteStation` repairs, each full own station
  of that side is set to 0.5 hull — the same training-style fixture damage the
  one-pilot test in the same file already uses
  (`SC2NaniteSupportLiveTests.cs:158`, "Training-style fixture placement/damage only").

Everything after the stimulus is production: the five support pilots per side purchase
legal nanite loadouts, fly real routes/warps, fire real repair projectiles, and the
SC-1 counters record `Ship.ReceiveDamage`/`Station.ReceiveDamage` repairs. The
`>= 10` minimum, fleet size (90 bots, 5 support per side), 1,200 s wall bound, match
duration and `SimPassStageFailures` assertion are unchanged. No quarantine/tier/retry
change.

> The original SC2 report's "no scripted damage" sentence described the pre-SC-5
> qualification runs. For this regression test the station damage stimulus is now
> staged as above; the ship repairs remain completely natural. This report supersedes
> that sentence for the test.

### Verification (3× alone)

`~/.dotnet/dotnet test tests/Allegiance.ClientCore.Tests/... --filter "FullyQualifiedName~SC2NaniteSupportLiveTests.Research_x16"`

| Run | Result | Duration | Last trace before `Complete()` |
|---|---|---|---|
| 1 | PASS | 2 m 29 s | `side=0 ship=29 station=318; side=1 ship=6 station=325` |
| 2 | PASS | 1 m 53 s | `side=0 ship=6 station=316; side=1 ship=10 station=323` |
| 3 | PASS | 1 m 43 s | `side=0 ship=31 station=318; side=1 ship=3 station=332` |

`Complete()` requires **both** `NaniteShip >= 10` **and** `NaniteStation >= 10` on
**both** sides, so the last trace is one 5 s sample before the ship counters crossed;
the logged traces and the pass result together show both counters satisfied on both
sides. Live logs: `~/allegiance-evidence/NT1/sc2-live-fix1..3.log`.

---

## Light gate

| Step | Command | Result |
|---|---|---|
| Solution build | `~/.dotnet/dotnet build AllegianceCore.slnx -c Debug` | **0 warnings / 0 errors** |
| Tool projects | `tools/build-tools.sh --drop-bins` | **70/70 built, 0 warnings / 0 errors** (`~/allegiance-evidence/NT1/build-tools.log`) |
| Fixed test 1, 3× alone | `dotnet test … --filter "FullyQualifiedName~BroadcastSnapshotTests.Recorded_ticks"` | **4/4 PASS ×3** |
| Fixed test 2, 3× alone | `dotnet test … --filter "FullyQualifiedName~SC2NaniteSupportLiveTests.Research_x16"` | **PASS ×3** |
| Fast tier, once | `tools/run-tests.sh --tier fast` | **PASS — 9,965 tests, 281.8 s**; infrastructure checks PASS (`~/allegiance-evidence/NT1/fast-tier.log`) |

The Fast-tier run was after both edits. No AOT publish was run: neither
`src/Allegiance.Server` nor `src/Allegiance.Core` changed (both edits are test-only).

## Out of scope / not touched

`P703ResearchCommanderLiveTests`, `P765GameOverStallTests`,
`P745FinaleSiegeLiveTests`, `P706DispatchProfilingTests`, the nightly win/osx
`NU1101` runner issue, and any gameplay/behaviour source code. No thresholds,
retries, tiers, skips or quarantines were changed. `NT1_BRIEF.md` remains untracked.

## Evidence

Raw logs, bisect scripts/transcripts and the per-frame byte diff:
`~/allegiance-evidence/NT1/` (39 MB). The working tree contains only the two test
edits and this report.
