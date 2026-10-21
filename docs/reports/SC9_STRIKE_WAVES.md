# SC-9 — strike waves made of real bombers, aimed at soft stations, with an escort

Author: Pi lane (DeepSeek V4.1 Flash), branch `pi/sc9`, base `master` at
`0fab95a54dcc03189e4663b3c7425943187ef382` (later than the brief's `e96b9943`).
Scope: `tools/allegiance-mcp` and `tests/Allegiance.ClientCore.Tests` only.
**Nothing under `src/` changed**, so the light gate does not trigger an AOT publish.

This is bot AI for the research showcase, not native parity. Every claim below cites
the port's own file:line unless a native rule is explicitly named; the native defend
rule used by the escort is cited from `~/Development/Allegiance/src/`.

## Status against the brief

| Brief item | Status | Evidence |
|---|---|---|
| 1. Strike pilot = anti-base-capable hull; quorum counts the whole roster | **complete** | `BotManager.ResearchTactics.cs:48-51`, `:9-20`, `:33`; quorum `ResearchTactics.cs:126,138,236-237`; tests `Capability_…`, `Roster_quorum_…`, `A_roster_of_one_…` |
| 2. Re-pick the target at every wave boundary, rally changes with it | **complete** | `ResearchTactics.cs:151-190,348-349,482-501,509-520`; observation `BotManager.ResearchTactics.cs:65-77`; tests `The_next_wave_…`, `A_new_station_…`, `A_shield_damaged_…`, `Observed_station_health_…` |
| 3. Escort the wave with a native defend order; do not treat a defend as "engage that friend" | **complete** | `ResearchTactics.cs:65-66,270-273,312-324,330-344,372-439`; guard `BotManager.cs:2220-2225`; native `shipIGC.cpp:2373-2404,2687-2696`; port `BotManager.Orders.cs:31-38,109-120`; tests `A_committed_wave_…`, `Escorts_are_released_…`, `An_escort_re_points_…`, `A_defend_ship_order_…` |
| 4. Measure `escorts`, station health, `arrived`/`peakOnTarget`, `dur` in the `[strike]` lines | **complete** | `ResearchTactics.cs:12,25-29,330-343,354-364`; `BotManager.Research.cs:115-117,158-168`; tests `A_wave_counts_…`, `Strike_log_lines_…` (and the updated SC8 log test) |
| Not chosen: (b) rally move, (d) top-up, (e) stricter launch gate | left alone | unchanged code paths |

## What the old code did (verified in `git show HEAD:…`)

1. **The target was picked once and kept for the whole match.** The old pick ran only
   while the target was null (`HEAD:tools/allegiance-mcp/ResearchTactics.cs:134-136`),
   and only `station-gone` cleared it. That is why every wave in the rig batch aimed at
   the enemy Garrison.
2. **"Strike pilot" was a role, not a capability.** `HEAD:…/BotManager.ResearchTactics.cs:25`
   used `FinaleBomberActive || Role == "bomber"`; a bomber relaunched on a temporary
   Scout/Fighter (`BotManager.TechLoadouts.cs:88`) still counted.
3. **The quorum counted only launched pilots.** `HEAD:…/ResearchTactics.cs:184-185`
   used `strikePilots.Length` (launched, live, non-pod) for both the timeout quorum and
   the full group, so a docked roster could commit a wave of one.
4. **Nothing escorted the wave.** The non-strike loop intercepted enemy bombers near a
   home station and otherwise focused local enemies
   (`HEAD:…/ResearchTactics.cs:231-238`); no friendly defend order was ever sent.
5. **Any ship-target order adopted the ship as the focus-fire target.**
   `HEAD:tools/allegiance-mcp/BotManager.cs:2214-2221` returned `pq.TargetId` for every
   ship order (repair excepted), so a defend order naming a friendly would have made
   P431 chase the friend (`BotManager.cs:2767-2778`).

## What changed

### 1. Capability and roster quorum

`BotManager.ResearchTactics.cs:48-51`:

```csharp
internal static bool IsStrikeCapable(bool finaleBomber, string role, short hullId)
    => (finaleBomber || role.Equals("bomber", StringComparison.OrdinalIgnoreCase))
        && Catalog.RepresentativeCore().HullTypes.TryGetValue(hullId, out var hull)
        && hull.HasCapability(DataHullTypeIGC.HabmThreatToStation);
```

This is the native `c_habmThreatToStation` capability bit
(`~/Development/Allegiance/src/Igc/igc.h:735`, `hullTypeIGC.h:243-246`), decoded in the
port at `src/Allegiance.Core/Data/DataHullTypeIGC.cs:203,235`, and it is the same bit
the loadout selector uses to classify a bomber (`TechLoadoutSelector.cs:52`). No hull
names or ids are involved: a `Role == "bomber"` bot currently flying a temporary
Fighter/Scout is an ordinary non-strike pilot (and therefore a legal escort).

The snapshot computes the side's roster from the same helper over every non-pod bot –
docked or launched – and returns it with the pilots/targets
(`BotManager.ResearchTactics.cs:9-20`). The policy receives it as the new
`strikeRoster` argument (`ResearchTactics.cs:126`) and uses it in
`timeoutQuorum = Math.Min(2, roster)` and
`fullGroup = roster >= StrikeGroupSize && atRally >= StrikeGroupSize`
(`ResearchTactics.cs:236-237`). A wave of one is now only possible when the whole
roster is one. `-1` keeps the SC8 launched-count behaviour for callers without a
roster; production always passes the real count (`BotManager.Research.cs:115-117`).

### 2. Re-pick at every wave boundary

While `_waveMembers.Count == 0`, the policy validates the target, builds an observed
station signature (id + damaged + garrison + sector) and re-runs
`PickStrikeStation` when the target is null or the signature changed
(`ResearchTactics.cs:151-185`, signature at `:348-349`). A changed pick also re-runs
`ChooseRally` and restarts `_rallySince` (`:175-181`). While a wave is in flight the
target is untouched (`:184-190`), preserving SC7's "commit, don't flick"; a station
that disappears in flight still ends the wave once (`:184-190`).

`PickStrikeStation`'s order is unchanged (`:482-501`): damaged first, non-Garrison
before Garrison, then distance from the strike group's local centroid, then id.
`ChooseRally` is unchanged (`:509-520`).

**"Damaged" now includes a knocked-down shield.** `ObserveTacticalTargets` reads
`StationLive.TryGetValue(...)` and sets `Damaged = HullFraction < 1 || ShieldFraction < 1`,
also carrying the two observed fractions into `Target.Hull`/`Target.Shield` (default
`-1`) for the log (`BotManager.ResearchTactics.cs:65-77`;
`StationLiveState(HullFraction, ShieldFraction)` at
`src/Allegiance.ClientCore/State/ClientLiveState.cs:54`). The old rule was hull-only
(`HEAD:…/BotManager.ResearchTactics.cs:45-47`).

The cache is O(stations) per quiet tick; a signature change is the only trigger that
runs the O(stations × strike pilots) pick.

### 3. Escorts

Constants `EscortsPerMember = 2` and `MaxEscorts = 6` (`ResearchTactics.cs:65-66`).
On commit, `CommitWave` calls `SelectEscorts` (`:312-324`): non-strike, non-support
pilots, preferring the rally sector and then the nearest to the rally point, at most
`min(MaxEscorts, members × EscortsPerMember)`, assigned round-robin to the wave members
(`:372-389`). Orders are `CidDefend`, type `Ship`, target = the protectee.

`MaintainEscorts` (`:395-414`) drops an escort that left the snapshot and re-points an
escort whose protectee left the wave to another live member (a changed order). Orders
go out after the wave's attack orders and before the non-strike loop (`:270-273`), so
`MaxOrdersPerTick = 2` (`:75`) still bounds the whole tick: 3 attacks + 6 defends take
about five 250 ms research ticks (the production loop waits 250 ms at
`BotManager.Research.cs:150`). The non-strike loop skips escorts while the wave lives
(`:273`); when the wave ends `EndWave` clears the assignments (`:330-344`) and the next
non-strike order supersedes the defend.

The port already flies this native rule: `DriveCommanderOrder`'s
`CidDefend && !enemy` branch (`BotManager.Orders.cs:109-120`) acquires enemies within
1500 of the protectee and keeps the chase inside 3500 / the protectee's cluster
(`PickCommanderDefenseTarget`, `BotManager.Orders.cs:31-38`). That is native
`shipIGC.cpp:2687-2696` (acquire within 1500) and `shipIGC.cpp:2373-2404`
(return when the protectee is >3500 away or in another cluster). The port follows the
protectee across sectors via `EnsureCombatPath` (`BotManager.Orders.cs:95-101`).

**Bot guard.** A fresh `CidDefend`/`Ship` order is accepted but returns
`OrderedTarget = null` (`BotManager.cs:2220-2225`), so P431 never sets `TargetId` to
the friendly protectee (`BotManager.cs:2767-2778`). A `CidAttack`/`Ship` order (and
the existing goto-to-enemy-ship case) still adopts its target. No existing test
encoded "defend ship ⇒ ordered target": the P431 ship-order test uses `CidGoto`
(`OrderAcceptTranslateTests.cs:29,34-35`, `AttackCmd = OrderChange.CidGoto`), P728's
same-command test uses `CidAttack`, and SC7's friendly-ship goto test uses `CidGoto`.

### 4. Measurement in the `[strike]` lines

`Target` now carries `Hull`/`Shield` (default `-1`, `ResearchTactics.cs:12`);
`StrikeEvent` carries `Escorts`, `StationHull`, `StationShield`, `Arrived`,
`PeakOnTarget`, `DurationSeconds` (defaults keep the old constructors compiling,
`:25-29`). The wave tracks `_waveArrived` (members ever seen within
`StrikeOnTargetRadius` of the station) and `_wavePeakOnTarget` (most at once) each tick
(`:204,354-364`); `EndWave` records them with `time - _committedAt` (`:330-343`).

`StrikeLogLine` (`BotManager.Research.cs:158-168`) now emits, one line per edge:

```
[strike] side=1 wave=2 commit members=3 atRally=3 target=station#100 sector=2 escorts=6 stationHull=0.812 stationShield=0.400 reason=full
[strike] side=0 wave=2 end members=3 arrived=2 peakOnTarget=3 escorts=4 dur=45.0s stationHull=0.812 stationShield=0.400 reason=no-members-in-target
```

Fractions are quoted to 3 decimals and `-1` when unknown. Still one line per commit
and one per end, never per tick.

## Tests

New file: `tests/Allegiance.ClientCore.Tests/SC9StrikeWaveTests.cs` (14 tests).
The changed SC8 file is `tests/Allegiance.ClientCore.Tests/SC8StrikeWaveTests.cs`.

### Red run (phase-1 additive API + today's behaviour)

The record fields/parameters were added first while the policy kept today's behaviour
(role-only strike flag, launched-count quorum, no escorts, no re-pick). The full
SC8+SC9 run was **1 test run: 30 tests, 12 SC9 failures**, logs at
`~/allegiance-evidence/SC9/red-sc9.log`.

| Failed test | Runtime failure |
|---|---|
| `Roster_quorum_counts_the_whole_roster_not_the_launched_pilots` | `Assert.DoesNotContain` — the lone launched pilot committed at the timeout |
| `Capability_requires_the_hull_station_threat_flag_not_the_requested_role` | `Assert.False` — a role="bomber" fighter-hull bot was accepted |
| `Observed_station_health_marks_a_shield_damaged_station_damaged` | `Assert.True` — shield 0.3/hull 1.0 was not marked damaged |
| `The_next_wave_re_picks_a_soft_station_and_re_rallies_through_its_aleph` | `Assert.Contains` — no goto to the new aleph (old target kept) |
| `A_new_station_does_not_change_the_target_while_a_wave_is_in_flight` | `Assert.Contains` — after the wave, the old rally was still used |
| `A_shield_damaged_station_is_preferred_over_an_undamaged_one` | `Assert.Contains` — no order for the shield-damaged station |
| `A_committed_wave_gets_the_nearest_non_strike_fighters_as_defend_escorts` | `Assert.Equal` — no escort orders |
| `Escorts_are_released_when_the_wave_ends_and_get_ordinary_orders` | `Assert.Equal` — end reason/escort count differed |
| `An_escort_re_points_to_a_live_member_when_its_protectee_leaves_the_wave` | `Assert.Single` — no defend order at all |
| `A_defend_ship_order_is_accepted_without_adopting_the_protectee_as_a_target` | `Assert.Null` — the friendly was adopted as `OrderedTarget` |
| `A_wave_counts_arrived_and_peak_on_target_from_a_scripted_path` | `Assert.Equal` — station hull was `-1`, not the observed 1.0 |
| `Strike_log_lines_carry_escorts_and_station_health_and_wave_arrival` | `Assert.Equal` — old string format |

Two new tests intentionally pass on today's code and are not red evidence:
`A_roster_of_one_still_commits_its_lone_pilot_after_the_timeout` is the SC8 one-pilot
case restated against the roster, and `Per_tick_cost_…` is a wall-time guard.

The damaged-preference test was rewritten during implementation to drive the whole
observation→pick path. To keep its red evidence honest, I re-ran it with only the
observation reverted to today's hull-only rule: both
`Observed_station_health_…` and `A_shield_damaged_…` failed with `Assert.True`
(`~/allegiance-evidence/SC9/red-damage.log`). The final tree restores the new rule.

### Green run

`SC8StrikeWaveTests` + `SC9StrikeWaveTests`: **30/30 pass**
(`~/allegiance-evidence/SC9/green-final-sc9.log`); SC9 alone **14/14 pass**
(`~/allegiance-evidence/SC9/sc9-after-damage-rewrite.log`).

### Changed SC-6/7/8 tests

No SC-6 or SC-7 test was changed.

| Test | Change | Why |
|---|---|---|
| `SC8StrikeWaveTests.A_side_with_one_strike_pilot_still_attacks_after_the_timeout` (`SC8StrikeWaveTests.cs:160`) | passes `strikeRoster: 1` on both ticks | the quorum is now stated against the roster, not the launched count; otherwise the SC8 case would be indistinguishable from a docked roster |
| `SC8StrikeWaveTests.Strike_wave_log_lines_match_the_rig_format` (`:287`) | expected strings updated to the new `[strike]` format | the commit line now carries `escorts`/`stationHull`/`stationShield`; the end line carries `members`/`arrived`/`peakOnTarget`/`escorts`/`dur`/health |

## Per-tick cost

Test: `Per_tick_cost_with_107_pilots_and_300_targets_stays_within_the_base`
(`SC9StrikeWaveTests.cs:365`). Tagged `[Trait("Tier","Live")]` because it takes ~7 s,
over the 10 s Fast budget. 1000 ticks, 107 pilots, 300 targets, fastest of 3 samples,
all measured in the same run (so the machine speed cancels out). `base` is the
pre-SC9-shaped tick (no enemy station, same 300 targets); `cached` is 100 stations with
an unchanged signature (the production path); `repick` toggles the observed damage
every tick to force `PickStrikeStation` on every tick (the un-cached worst case).

| Run | base (no station) | cached (100 stations) | repick every tick |
|---|---|---|---|
| 1 | 988.6 ms | 700.1 ms | 713.9 ms |
| 2 | 985.3 ms | 700.3 ms | 704.7 ms |
| 3 | 970.2 ms | 684.7 ms | 697.2 ms |

All runs are within 1.5× of base (in fact below it: replacing 100 ships with stations
makes the non-strike target scans cheaper). The pick-specific cost is the
`repick - cached` delta: 4–14 ms per 1000 ticks, i.e. **4–14 µs per boundary tick** in
the un-cached worst case. Logs:
`~/allegiance-evidence/SC9/perf-solo-{1,2,3}.log`.

## Gate

| Gate | Result |
|---|---|
| Whole-solution build | **0 warnings, 0 errors** (final tree, `~/allegiance-evidence/SC9/build-final2.log`) |
| New + affected tests 3× alone (56 tests: SC6/SC7/SC8/SC9 policy, P431, P728, SC6 observation) | **3/3 pass, 0 failed** (`affected-postrewrite-{1,2,3}.log`) |
| ONE fast tier | **PASS, 9656 tests, 271.0 s** (`fast-tier-final.log`). The first attempt failed only because the new per-tick test took 10.061 s and tripped the 10 s Fast limit; it is now `Tier=Live` (kept: `fast-tier.log`). |
| Showcase never-parks 5× | **5/5 PASS**, 1 m 49 s – 1 m 52 s (SC7/SC8 base rate 1 m 43 s – 1 m 53 s). Captured longest "target but speed < 1" in runs 2–5: 2.019 s / 2.004 s / 2.014 s / 1.904 s. Run 1 passed at normal verbosity (no summary line). |
| AOT publish | **not run** — `src/Allegiance.Server` and `src/Allegiance.Core` did not change |

No screenshots are cited in this report; every result above is a text log in
`~/allegiance-evidence/SC9/`.

The showcase logs contain **0 `[commander]` and 0 `[strike]` lines**: the 150 s
never-parks test never reaches research strikes (same as SC7/SC8). It is a
bot-behaviour/load guard here, not the strike measurement.

## For the rig (Claude)

The `[strike]` lines now let you score the brief's targets directly:

- every commit: `target=station#… sector=… escorts=E stationHull=H stationShield=S`;
- every end: `members=K arrived=A peakOnTarget=P escorts=E dur=Ds` plus the last
  observed station health;
- the combat log still has one `[commander] … command=1 target=Station#…` line per
  attack order, so "no commit member lacks `HabmThreatToStation`" can be checked
  against the loadout lines.

Targets to read: ≥3 anti-base hits on one station within 30 s; ≥1 station kill per
side per match; no commit with a non-`HabmThreatToStation` hull; no `members=1`
while the roster is ≥2; `arrived ≥ 2` in most waves. `EscortsPerMember`/`MaxEscorts`
are constants if the escort size needs tuning.

## Unfinished / risks (honest list)

- **No rig run.** The showcase never starts a research commander in 150 s, so there is
  no in-repo evidence of the actual hit/kill outcome; the unit tests are the evidence
  for the rules, as the brief says.
- **Lifepod follow-up (out of scope, not fixed).** The rig saw an SRM Anti-Base hit
  from hull 444 (ship 8295, t=1288.95); the Lifepod still carries whatever permits
  that. Recorded here as required; no code touched.
- **Same-tick deferral.** After a wave ends mid-tick, the re-pick runs on the next
  250 ms research tick, not earlier in the same tick. A changed pick restarts the
  rally clock, so the next wave waits its own timeout.
- **Escort count at the end edge** is the number still assigned when `EndWave` runs;
  a member-gone end can include an escort that left the snapshot in that same tick
  (it is pruned on the next tick). The count is a measurement aid, not a rule.
- **Roster conservatism.** A bot whose current hull lacks the station-threat bit
  (bomber on a temporary fighter, or a hull missing from the core) does not count in
  the roster, and a non-participating/spawn-pending bot with a capable hull does
  count. The latter can only delay a commit; it cannot create a wave of one.
- **No native AI source** exists for the re-pick or the escort-size policy; those are
  port AI policy, cited to the port. The escort's *flying* rule is the native defend
  rule cited above.
- **First fast-tier attempt failed** on the new per-tick test's 10.061 s wall time;
  the fix was the required `Tier=Live` tag, not a code change, and the final run
  passes.

## Claude review fixups (at merge)
- **Pick order:** non-Garrison stations now always outrank a Garrison; damage only orders stations within that.
  Before, a scratched Garrison (hull or shield < 1, sticky once the hull is hit) outranked every pristine soft
  station, which could bring back the batch-15 "Garrison all match" lock. Pinned by
  `A_damaged_Garrison_does_not_outrank_an_undamaged_soft_station`.
- **Rally timer:** a re-pick restarts the rally window only when the rally aleph itself changes, so candidates trading
  places as shields regenerate cannot keep the 90 s timeout from firing.
- **Roster count:** now uses the same `Cs is not null && IsParticipating` filter as the pilot snapshot, so a
  disconnected bomber cannot hold a lone pilot at the rally.
- The two tests that read the representative core are `[SkippableFact]` + `Require.Artwork()`.
- Red-run count corrected to 12.
- Watch on the rig: dead escorts are not replaced, and a released escort keeps its defend order until re-tasked.
