# P973 — P959's two regressions: the P733 fallback finale duty and the P798 fog audit

Base: Claude's `master` at dispatch, `78c4f4e6` (P959 `cbd96ade` + P961 merged), in the P973
worktree. Work is in the working tree; nothing committed or pushed. Every native reference is
relative to `~/Development/Allegiance/src/` and was re-read for this report; port line numbers are the
working tree at the end of this task. Bot features stay bot-side and preset-scoped: the only product
files touched are `tools/allegiance-mcp/*`, plus the two test projects.

| Part | Verdict |
| --- | --- |
| 1 — P733 no-bomber-tech finale duty | **Fixed, real P959 regression.** P959 made the finale DUTY flag conditional on strike hardware, which also disabled P733's fallback *loadout* path (it is keyed off that same flag). The flag is duty again; P959's "no unarmed wave" rule moved to an explicit strike predicate the strike mass uses. Test pin kept (not obsolete) and green **10/10 alone**. |
| 2 — P798 fogged full loop, knowledge leak | **Reproduced (1/21 runs, 1/7 with the diagnostic) and fixed at the source.** The research commander picked the constructor's rock from **its own** world, while the side's knowledge record (the 1 Hz per-side intel the audit reads) can miss a far research rock that is discovered and consumed between samples. Build targets now come from the side's recorded rocks (`ResearchSnapshot(…, recordedRocks)`), the audit is attributed to the build's own match, and the requested diagnostic (build rock + per-mission intel rows + commander/bot world views) is printed with every run and every failure. **0/5 in the gate afterwards.** |
| 3 — `docs/TEST_FLAKES.md` | Ledgered: the P733 regression + resolution, the P798 build-target fix + audit attribution, the P798 no-station-kill base rate, and the four Core load flakes from the one fast-tier suite. |

---

## 1. P733 — a side with no bomber tech still gets the fallback finale duty

### 1.1 Symptom and reproduction (at master `78c4f4e6`)

`tests/Allegiance.ClientCore.Tests/P733ResearchTests.No_bomber_tech_finale_reaches_stage_over_and_automatic_next_match_twice`
failed **1/1** here (Claude's sweep: 3/3) at `P733ResearchTests.cs:103`:

```
Assert.True(Get<bool>(bots[0], "FinaleBomberActive"));
```

with the relaunch purchase in the same log line showing the **ordinary** role fallback instead of
P733's explicit finale fallback:

```
[loadout] P733-finale-0: dockType=115; requested role=Bomber unavailable/over budget; temporary
Interceptor; nearest duty=Fighter; … Weapon@0=120 'PW Mini-Gun 1' x1 …
```

Raw log: `~/allegiance-evidence/P973/p733_before_fail.log`.

### 1.2 Root cause (file:line)

P959's relaunch/activation block was:

```csharp
// tools/allegiance-mcp/BotManager.Finale.cs (cbd96ade:1153, pre-fix)
bool carriesStrike = loadout is not null;                 // SelectFinaleLoadout()
if (!carriesStrike && … ) carriesStrike = DamagesStation(stationType, carried);
bot.FinaleBomberActive = armed && carriesStrike;
```

`FinaleBomberActive` is not just a "can I hurt the station" bit — it is the finale **duty** flag, and
P733's fallback loadout path is keyed off it:

```csharp
// tools/allegiance-mcp/BotManager.TechLoadouts.cs:185-201 (RepickRelaunchLoadout)
if (bot.FinaleBomberActive && bot.Finale is { } run && run.Plans.TryGetValue(bot.Side, out var plan))
    target = …StationTypes[plan.TargetTypeId];
selected = PickTechLoadout(shop, role, target, allowRoleFallback: !bot.FinaleBomberActive, …);
if (selected is null && bot.FinaleBomberActive && target is { } station && research)
{
    selected = TechLoadoutSelector.SelectAvailableStrike(…);       // null: no bomber/anti-base tech
    selected ??= PickTechLoadout(shop, role, target, allowRoleFallback: true) is { } available
        ? available with { Reason = "research finale: no legal station-damaging loadout; …" + available.Reason }
        : null;
}
```

With `FinaleBomberActive == false` the first `PickTechLoadout` call already returns the ordinary role
fallback (`allowRoleFallback: true`), so `selected` is never null and P733's "research finale" branch
— the reason P733 pinned and the reason the reserved pilot knows it is on finale duty — is
unreachable. That is the whole failure: the flag lost its "duty" meaning and became a hardware test
that the very case P733 exists for can never pass.

### 1.3 What the right behaviour is (evidence)

* **P733's intent** (`docs/reports/P733_TECH_FINALE_AND_SPEED.md`): research finales reserve pilots
  even when the requested bomber role is not buyable; a verified station-damaging fallback gets the
  station-attack duty, and *"if no available loadout damages stations, they can launch a legal
  ordinary combat loadout; the reason is logged explicitly"*. The current rig's tech masks (civ 25 vs
  3) provide no legal station-damaging strike option at all, so the ending comes from the server's
  bounded `SHOWCASE_FINALE` deadline (`showcase.finaleTimeoutSeconds`, `Server.Showcase.cs`
  `ArmResearchFinale`), not from station damage. The test's name is exactly that contract: *no bomber
  tech → stage Over → automatic next match, twice*.
* **P959's intent** (`docs/reports/FINALE_STRIKE_MASS_P959.md` §2.2(b), §3.2): *"a strike bomber needs
  REAL station-damaging hardware … or the wing parks as 'armed' fighters that can never hurt the
  station (the b59 unarmed-wave signature)"* — i.e. a **wave-sizing/commit** rule: never count or
  commit a hull that cannot hurt the station.
* The two rules are about different questions (duty vs. strike participation). P959 answered the
  second one by overwriting the flag that answers the first. The pin is **not** obsolete: P733's
  contract (duty + explicit fallback reason + the server's bounded ending) still holds, and the
  stronger reading ("the side still reaches Stage Over and the next match") is what the test asserts.
  So the assertion stays exactly as P733 wrote it and the *implementation* is corrected.

### 1.4 Fix (file:line)

1. **The duty flag is duty again** — `tools/allegiance-mcp/BotManager.Finale.cs:1145-1157`
   (`PrepareFinaleRelaunch`): `bot.FinaleBomberActive = armed;` (with the reasoning in the comment).
   The strike loadout selection above it is unchanged: a member that *can* buy a strike loadout still
   gets one, and `AssignFinaleLoneWolf`/`Role = "bomber"` still run for every duty member.
2. **P959's rule as a predicate** — new `FinaleCarriesStrikeHardware(bot, plan)`
   (`tools/allegiance-mcp/BotManager.ReachableStrike.cs:43-55`): the member's **granted** inventory
   (`FinaleGrantedStrikeLoadout`, never the desired parts) must damage the plan's real station type,
   mounted on the hull it was granted for (`FinaleWingMemberReady`, P959's own predicate).
3. **The strike mass counts only those hulls** — `tools/allegiance-mcp/BotManager.StrikeMass.cs:285-297`
   (`FinaleStrikeMassTick`): the `armed` set that sizes the volley (`FinaleVolleyRequirement`), the
   `assembled` count that commits a wave, `CommitFinaleWave`'s expected-rounds denominator, the
   reachable-demand planner and the wave-end accounting all consume this one set, so a P733 fallback
   fighter can never be sized, staged, assembled or released as a strike bomber. Where a real strike
   loadout exists on the same side (the P959 mixed-wing case) the rule is unchanged.
4. **The arm does not erase an existing duty** — `tools/allegiance-mcp/BotManager.Finale.cs:499-509`
   (`ArmFinaleWing`'s refit branch): a reserved member without legal hardware clears only
   `FinaleGrantVerified` (so it still refits, P959) and keeps the P733 duty it may already hold from a
   fallback relaunch.

No server, sim, wire or purchase/launch rule changed; no grant, teleport or `CanBuy` bypass. The
fallback loadout is still an ordinary `BUY_LOADOUT` through the real shop.

### 1.5 Before / after

| Run | Result |
| --- | --- |
| master `78c4f4e6`, unmodified test, 1× | **fail** (`FinaleBomberActive` false at `P733ResearchTests.cs:103`) |
| this tree, fixed, **10× alone** | **10/10 pass** (4–5 s each), both matches reach `Stage Over` + automatic restart |
| finale/affected unit suites, **3× alone** (P959, P961, P896, P801, P745SiegeDuty, P819, P656ShowcaseFinale, P682b, P733FinaleDuty, P832gGoalDuty, P778ABomberPush tests) | **67/67 each time**, 0 skipped |

`[strike] side=0 finale arm ordered=1 ready=0 refits=1 wing=1 escorts=0 …` then, on the relaunch,
`[loadout] P733-finale-0: … research finale: no legal station-damaging loadout; proceeding with best
available combat loadout; bounded server finale armed; …` and `[finale] side 0 … bounded finale
armed; deadline=…`, i.e. the fallback duty and its explicit reason are back and nothing is committed
as a strike.

---

## 2. P798 — the fogged full loop and the rock its record never named

### 2.1 Reproduction

`P798FullLoopLiveTests.Fogged_showcase_runs_the_whole_loop_unattended_and_starts_the_next_match`
(Live, ~3.5 min per run, sequential unless stated):

| Arm | Runs | Result |
| --- | ---: | --- |
| master `78c4f4e6`, unmodified test, alone (load average 0.9–3.8) | 10 | 8 pass, 2 fail — both **`at least one station must be destroyed`** (`P798FullLoopLiveTests.cs:256`); knowledge leak **0/10** |
| master `78c4f4e6`, 4 copies concurrently (deliberate self-load, lane rule; load average up to **15.8**) | 4 | 4 pass; knowledge leak **0/4** |
| this tree, test instrumented with the P973 diagnostic, alone | 7 | **1 fail — the knowledge leak**, exactly Claude's message: `side 0 built on rock 12 (sector 2080) without having discovered it` |

So the leak is real: **1/7** once the diagnostic prints the build state (**1/21** over all runs of this
task, ~5% on this lane — it needs the build to land on one of the far research rocks, and Claude's
loaded runner hit it more often). The no-station-kill mode is a **different**, pre-existing failure
(ledgered, §2.4). Raw logs:
`~/allegiance-evidence/P973/p798_master_{1..10}.log` (10× master),
`p798_par_{1..4}.log` (parallel arm), `p798_leak_repro_rock12.log` (the reproduction with the
diagnostic: `build rock 12 … discoveredInMission=False … commander(mySide=0) known=False`).
Gate/build/evidence logs for every row of §4.1 are in the same directory.

The diagnostic's failing line (all of it bot-side, never server truth):

```
[p798-loop] build rock 12 sector 2080 side 0 discoveredInMission=False firstMissionCookie=1
  intelRows=[side=0 cookie=1 rocks=38 hasBuildRock=False; side=1 … hasBuildRock=False]
  clientWorlds=[commander(mySide=0) known=False sectorRocks=38 lowIds=[0,1,…,11,13] |
                bot(mySide=0)      known=False sectorRocks=38 lowIds=[0,1,…,11,13]]
```

### 2.2 Root cause (file:line)

Three facts combine:

1. **The build target comes from one peer's world.** `ResearchSnapshot` built the commander's rock
   candidates from `peer.World.Asteroids` (`tools/allegiance-mcp/BotManager.Research.cs:225-232`,
   pre-fix) and `ResearchCommander.TickPlan` picks
   `world.Rocks.FirstOrDefault(r => r.Available && (r.Capabilities & step.AsteroidClass) != 0 && !_reservations.ContainsKey(r.Id))`
   (`tools/allegiance-mcp/ResearchCommander.cs:232-245`) — the lowest asteroid id that matches, and
   the build order is an ordinary `CidBuild` (server `StartBuildIfLegal`,
   `Server.ShipCommands.cs:2296-2410`).
2. **The audit's record is a 1 Hz sample, not an event log.** The fog rule is checked against
   `SideIntel.Rocks`, filled by `ScanSideIntel`
   (`tools/allegiance-mcp/BotManager.Exploration.cs:143-240`) every
   `ScoutOpening.ScanIntervalSeconds` (1 s). Rocks are never pruned from `SideIntel`
   (`SideIntel.cs:576-587` prunes only enemy/miner sightings), so the record is durable — but only for
   rocks that were present in a side world at a sample.
3. **The showcase makes the window shorter than one sample.** The research map preparation *adds*
   buildable rocks at radius 3500/4200 u in each side's home sector with ids from a low counter
   (`src/Allegiance.Server/Server.Showcase.cs:85-135`; the observed `lowIds=[0..11,13]` are exactly
   side 0's added rocks). The research timescale divides the constructor's 30 s build
   (`Igc/buildingeffectigc.h:19-22,50-54`; `Sector.cs:675`) and this fixture's tick loop runs the sim
   ~20-50× faster than wall time, so a far rock can be **discovered, built on and consumed inside
   ~1-2 wall seconds**. The rock then stops existing in every client world (the build replaces it),
   which is why even the commander's own world no longer holds id 12 at report time.

The side really did discover the rock (its commander ordered the build on it); the *record* the audit
reads missed it. The fog rule is not violated by the world, but it is violated by the **bot-side
selection**: the commander is allowed to choose a target that the side's own knowledge record does not
hold.

### 2.3 Fix (file:line)

* **Build targets come from the side's own record.** `ResearchSnapshot(peer, side, recordedRocks)`
  (`tools/allegiance-mcp/BotManager.Research.cs:211-241`) filters the rock candidates to the side's
  per-mission intel (`SideIntel.Rocks`, the same record the fog audit reads — the union of everything
  any of that side's worlds has observed). The caller reads it before taking the commander gate
  (`:180-190`) so the intel lock is never taken inside `peer.Gate` (the intel scan and
  `SideIntelReport` take `peer.Gate`/`cs.Gate` under the intel lock; the read order is preserved).
  A constructor can now only be ordered to a rock the side has recorded, so the audit is exact by
  construction, and the record is durable — the race above cannot recur. Native's own rule is exactly
  this question: statics are exported per side only after `SeenBySide`
  (`FedSrv/fsmission.cpp:4169-4250`).
* **The audit is attributed to the right match.** `get_side_intel` keeps one row per
  (`Mission.Cookie`, side) (`SideIntelStore`, `SideIntel.cs:594-615`; the store is not cleared on
  rotation) and the test assigned `discovered[side]` *per row*, i.e. "the last row for this side" — a
  first-match build could then be audited against the next match's few-hundred-milliseconds-old
  knowledge. The test now uses the row of the match the build happened in (`first.Cookie`), which is
  strictly stronger, and `P973FogAuditTests`
  (`tests/Allegiance.ClientCore.Tests/P973FogAuditTests.cs`, Fast, 7 ms) pins the per-mission invariant.
* **The diagnostic the brief asks for.** Every run prints, and a failure carries, the build rock's
  discovery state per mission plus the commander's and a bot's own client world view:
  `[p798-loop] build rock N sector S side X discoveredInMission=… firstMissionCookie=…
  intelRows=[side=… cookie=… rocks=… hasBuildRock=…] clientWorlds=[commander(…) known=… | bot(…) known=…]`
  (`P798FullLoopLiveTests.DescribeClientRockKnowledge`, reflection over `_commanders`/`_bots`).
  The fog rule itself is unchanged: a build on a rock the building side's record does not hold still
  fails the test.

### 2.4 The other master failure (pre-existing, not P959)

`Assert.True(kill is not null, "at least one station must be destroyed")` failed **2/10** at master:
`[finale] waiting t=…s: side 0 … kills=0; side 1 … kills=0`, the 3600-sim-second match cap declared
the winner and `Station kills: Alpha=0, Bravo=0`. No ship kill ever happened, so the finale's P801
maturity gate (`kills>=1` in the last 5 minutes) never armed, the ordinary 1-2 hull strike waves only
scratched a shield that regenerated, and the cap ended the match. This is ordinary engagement luck, not
P959: `git diff 73eacf4d cbd96ade --stat` touches no ordinary strike/clash/engagement policy and no
P959 code path runs in those matches (the finale never armed). It is ledgered as a base rate in
`docs/TEST_FLAKES.md` and not fixed here (no task change to engagement policy).

## 3. Changes

| File | Change |
| --- | --- |
| `tools/allegiance-mcp/BotManager.Finale.cs:1145-1157` | `PrepareFinaleRelaunch`: `FinaleBomberActive = armed` (P733 duty) instead of `armed && carriesStrike`; comment records why the two questions are separate. |
| `tools/allegiance-mcp/BotManager.Finale.cs:499-509` | `ArmFinaleWing`: a hardware-less reserved member clears `FinaleGrantVerified` only — an existing P733 duty is not erased. |
| `tools/allegiance-mcp/BotManager.ReachableStrike.cs:43-55` | New `FinaleCarriesStrikeHardware(bot, plan)`: granted inventory + real station damage matrix + granted-hull match (P959's `FinaleWingMemberReady`). |
| `tools/allegiance-mcp/BotManager.StrikeMass.cs:285-297` | `FinaleStrikeMassTick`: the `armed` set (requirement sizing, assembly, commit, expected rounds, wave-end accounting) requires `FinaleCarriesStrikeHardware`. |
| `tools/allegiance-mcp/BotManager.Research.cs:211-241` | `ResearchSnapshot(peer, side, recordedRocks)`: the commander's build-target candidates are the side's recorded rocks (per-mission intel union), never one peer's world. |
| `tools/allegiance-mcp/BotManager.Research.cs:76-111` | The research loop reads the side's recorded rocks before the commander gate (lock order preserved) and passes them to the commander. |
| `tests/Allegiance.ClientCore.Tests/P798FullLoopLiveTests.cs` | Per-mission intel attribution (`first.Cookie`), the rock-discovery diagnostic (intel rows + commander/bot world views), doc comment records the defect. |
| `tests/Allegiance.ClientCore.Tests/P973FogAuditTests.cs` | New Fast pin for the per-mission intel invariant. |
| `GLOSSARY.md` | Two entries under the bot-policy index: "finale duty vs strike hardware (P733, P959, P973)" and "research builder rock selection (P798, P973)". |
| `docs/TEST_FLAKES.md` | P973 resolution section, the P798 no-station-kill base-rate entry and the fast-tier load-flake observation. |

No `src/` change (no server/sim/wire/AOT re-publish needed), no setting default changed, no new env
var, no compose/service change.

---

## 4. Gate

See the table at the end of this file (§4.1). Gate commands: whole-solution build with 0 warnings,
`tools/build-tools.sh`, P733 10× alone, P798 5× alone, the P745/P778A/P959/P961 finale suites 3×
alone, **one** full fast-tier suite, showcase never-parks 5× compared with the known base rate.

### 4.1 Results

`local.env` is present in the worktree (artwork + native-source paths), so nothing was skipped for
missing assets: every focused task/gate run reports **0 skipped**; the only skips in the one full
suite are the 3 known `MeshyPackTests` `NotExecuted` cases in the MonoGame project.

| Gate item | Result |
| --- | --- |
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **PASS — 0 warnings, 0 errors** |
| `tools/build-tools.sh` | **PASS — all 121 tool projects built, every one 0 warnings 0 errors** (2 m 38 s) |
| P733 `No_bomber_tech_finale_…_twice` 10× alone | **10/10 pass** (~4–5 s each), re-run on the final build |
| P798 `Fogged_showcase_runs_the_whole_loop_…` 5× alone (fixed tree) | **5/5 pass** (~3.5 min each); every run logs `discoveredInMission=True` for the build rock (`rock 19/22/11403/19/22`) |
| P745/P778A finale Live suites 3× alone (`P745FinaleSiegeLiveTests` + `P778ABomberPushLiveTests`) | **3/3 ×3 pass** (11 m 06 s + 2 m 45 s; 11 m 30 s + 2 m 37 s; 12 m 18 s + 2 m 37 s) |
| P959/P961/P896/P801/P745SiegeDuty/P819/P656ShowcaseFinale/P682b/P733FinaleDuty/P832g/P778A unit+affected finale tests 3× alone | **67/67 ×3 pass**, 0 skipped |
| New Fast pin `P973FogAuditTests` 3× alone | **3/3 pass** |
| One full suite (`tools/run-tests.sh --tier fast`) | **13,540 tests, 564.7 s — FAIL**: 4 `Allegiance.Core.Tests` failures, **each pass 3/3 alone** (load), plus 3 Fast executions over the calibrated 10 s limit (2 of them the same tests). Per project: Update 28/28, Lobby 139/139, MonoGame 4899 passed + 3 `NotExecuted` (known MeshyPack skips), ClientCore 2315/2315, Core 6133 passed / 4 failed, quarantine 19/19. No task test failed. Details + ledger entry below. |
| Showcase never-parks 5× (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`; base rate 5/5 in every recent report, 1 m 48 s–5 m 45 s) | **5/5 pass** (3 m 00 s, 4 m 45 s, 2 m 59 s, 5 m 45 s, 3 m 56 s) — unchanged vs the base rate |
| `tools/check-glossary-links.py` | **PASS — 659 distinct paths** (the two new GLOSSARY entries add three) |
| Public-repo hygiene (`scripts/publish-guard.sh` cannot run on an uncommitted tree; its checks were run over the working-tree diff) | **PASS — 0 `/home/<user>/` lines in `src tests scripts tools docker .github`, 0 secret patterns, 0 personal emails, 0 new media** |

Full-suite failures (non-task, host under other lanes' load): `NpcWingmanAttackLiveTests.Wingman_with_no_enemy_in_sector_stands_by`
(`travelled 161.517`), `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell`
(`cycle 1: miner should have docked and sold (sells 0)`, also 19.1 s), `DirectNpcBuildOrderLiveTests.Commander_directs_builder_drone_to_build_creating_real_station`
(`Assert.Single` empty, 3 s) and `AsteroidCombatLiveTests.Cross_side_missile_hits_asteroid_with_exact_computed_damage_and_it_survives`
(`missile must actually hit the asteroid`, 11.1 s) — all four pass **3/3 alone** on the same binary
(this task touches only `tools/allegiance-mcp/**` and two test files; the failing tests are Core server/NPC
cases). Ledgered in `docs/TEST_FLAKES.md` under P973.

---

## 5. Unfinished / risks (honest list)

1. **The P798 leak's trigger is narrow and rate-dependent.** It needs the research commander to build
   on one of the *far* research rocks (radius 4200) that get discovered and consumed inside one 1 Hz
   intel sample: **1/21 runs of this task (1/7 with the diagnostic)** on this lane, more often on
   Claude's loaded runner. The fix removes the possibility by construction (the commander's candidate
   set is the side's recorded knowledge), so the *rate* cannot be re-measured as a before/after pair
   on the same lane beyond "0/5 after, 1/21 before". A runner that still sees the message will get the
   full diagnostic (build rock, per-mission intel rows, commander and bot world views) printed with it.
2. **P798's other failure mode is not fixed.** `at least one station must be destroyed` failed 2/10 at
   master: the fleets never killed a ship, the finale's `kills>=1` maturity gate never armed, and the
   3600-sim-second cap ended the match with 0 station kills. Pre-existing engagement luck, no P959 code
   path in those matches; measured, ledgered, not touched (it is not this task's contract).
3. **The Commander's rock filter is a one-sample delay.** The research commander now sees a rock only
   once the side's intel recorded it (≤1 s of wall time). That is the intended "build what your side has
   seen" rule; it can delay an expansion by up to one scan interval, never skip one.
4. **P959's rule and P733's duty are still two flags-worth of concepts on one flag.** `FinaleBomberActive`
   is duty (P733); strike participation is `FinaleCarriesStrikeHardware` measured at the wave. A P733
   fallback member still takes the finale *hold* while it is in the target sector (`HoldFinaleStrike`
   gates on duty, pre-P959 behaviour) — it can never damage the station, so it changes no outcome, but
   the next person touching this code should not re-conflate the two.
5. The full fast-tier suite failed on 4 Core cases and 3 slow-fast executions, all load-attributed
   (each 3/3 alone on the same binary, none of them touched by this task). Per the lane rule that is a
   flake ledger entry, not a gate re-run; it is in `docs/TEST_FLAKES.md` under P973.

## Native intent

* **Design (native rules kept).** Research finales, "which loadout a docked pilot may buy", and the
  fallback to the best legal combat loadout are showcase/commander policy on top of native purchase
  and launch rules: hull/part purchases use station-local plus side tech and respect successors
  (`Igc/stationIGC.h:383-433`), a purchase is a real `BUY_LOADOUT` acknowledged by the server
  (`FedSrv/FedSrv.CPP:6189-6303`), and station damage needs a real anti-base round — a gun that the
  station's damage matrix does not let through is not a station weapon
  (`Igc/stationIGC.cpp:138-193`; `Igc/magazineigc.cpp:104-178`). The P733 duty flag does not change
  any of those; it decides which pilot flies where and which *reason* its legal purchase carries.
* **Era constraint.** Native Allegiance has no bot commander and no bounded showcase finale, so "a
  side that cannot buy a bomber still keeps its finale duty and lets the server's deadline end the
  match" is port policy, classified here (not a parity credit). The purchase, launch, damage and
  game-over rules it leans on are native and unchanged (`FedSrv/fsmission.cpp:3063-3085,3524-3546`).
* **Fog rule (P798).** Native exports static objects per side only after `SeenBySide`
  (`FedSrv/fsmission.cpp:4169-4250`) and keeps ore knowledge per side
  (`Igc/asteroidigc.h:280-285`); a constructor may only build on a compatible asteroid it reached
  (`Igc/shipIGC.cpp:2795-2815`). Neither the export rules nor the bot-side audit were weakened: the
  audit now compares a build with **its own match's** per-side knowledge and prints the rock's
  discovery state per mission.
* **Bugs fixed.** Two port-side defects, both introduced by P959's change set: (a) a duty flag
  overloaded as a hardware test, which orphaned P733's fallback loadout path; (b) a test audit that
  attributed a per-match intel row to "the side" instead of to the match the build happened in.
* **Modern approach.** A commander should keep a pilot on station duty even when its side's tech
  cannot hurt the station yet (the match must still end by the rule that says it ends), while a strike
  wave should only ever be sized, staged and released from hulls that really carry station-damaging
  hardware. P973 separates those two questions in code, so both P733's fallback and P959's massing
  rule hold at the same time. And a test that audits fog must ask its question of the match it is
  about.
