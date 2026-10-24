# P801 — bomber massing: gather, then hit the station together

Branch `pi/p801`, base master `8bf49ac7` (after P791 support and P798 fog/scouts). Production changes are
**only** in `tools/allegiance-mcp/**` plus tests, this report and one coverage row. No Core, server, client,
wire, damage or regeneration constant was changed (the native damage/regen rules are only *read*).

## 1. Why — the trickle the rig measured

Batch 20 on rig 221 (7 full 200v200 matches, 2026-10-23) ended every match on the finale deadline by kills;
no garrison fell. Match 7's station `4329` took almost every SRM Anti-Base 2 hit from a **full** shield:
`[station-damage]` shows shield `1.0 -> 0.5`, then a later hit again starts from a full shield; one hit
reached hull `1.0 -> 0.784`, and later the hull read `0.658` and rising. The old flow was:

- strike waves committed when 3 pilots were within 3,600 units of a rally aleph, or after a 90 s timeout
  with 2 (`ResearchTactics.cs` SC7/SC8), then members arrived and fired at different times;
- the finale converted bombers **one at a time at their next docked relaunch**
  (`BotManager.Finale.cs PrepareFinaleRelaunch`), so finale bombers streamed in individually;
- P778a escorts pushed and P791 support repaired, but nothing made bombers wait for each other.

Consequence: the shield regenerated between arrivals, and the 40/s armour regen outran the stream.

## 2. The volley arithmetic (real PCore001 data)

Verified from the real core via `StaticCore.Load(...PCore001.igc)` (the test asserts every number below):

| record | data |
|---|---|
| Garrison types **44 / 115 / 315 / 415 / 715 / 815** | armour **20,000**, shield **10,000**, armour regen **40/s**, shield regen **100/s**, defense armour **7**, shield **10** |
| Garrison (Str) types **45 / 410 / 710 / 810** | armour **25,000**, shield **15,000**, armour regen **50/s**, shield regen **150/s**, defense 7/10 |
| SRM Anti-Base 1 (missile **39**, launcher part **157**) | power **400**, load time **4 s**, damage type 8, magazine 2 on a Stealth Bomber |
| SRM Anti-Base 2 (missile **40**, launcher part **158**) | power **500**, load time **4 s**, damage type 8, magazine 2 on a Stealth Bomber |
| SRM Anti-Base 3 (missile **601**) | power **500**, load time **3.5 s**, magazine 2 on a Stealth Bomber |

Native rules encoded by `StrikeVolley` (`tools/allegiance-mcp/StrikeVolley.cs:31-282`):

- damage = `power * DamageConstants[damageType & 0x1f][defense]` for the station type's armor/shield
  defense bytes (`src/Igc/stationIGC.cpp:138,159`; `Allegiance.Core/Data/DataStationTypeIGC.cs:271-281`).
  Row 8 against defense 7 = **10.0** and defense 10 = **10.0**, so one AB2 round is
  `500 * 10 = 5,000` shield points = **shield 1.0 -> 0.5** on a 10k shield (the rig's measured hit).
  One AB1 round is 4,000 = 0.4 shield.
- shield overflow becomes hull damage
  `overflow * maxShield * dtmArmor / (maxArmor * dtmShield)` (`stationIGC.cpp:176-193`):
  for a 20k/10k garrison each AB2 overflow round takes **0.25 hull = 5,000 armour**.
- regeneration is the native `CstationIGC::Update` order (`stationIGC.cpp:192-210`): **armour regenerates
  while armour < 1; the shield only regenerates once armour is full**. This is why a scratched garrison
  is stubborn: it does not heal its shield while its hull is damaged.
- a magazine's rounds are spaced by the missile load time (4 s for AB1/AB2).

Exact kill arithmetic from a fresh station (simulated, and the test asserts it):

| target / loadout | shield rounds | hull rounds | rounds to kill | bombers (2-round magazine) | requirement with one spare |
|---|---|---|---|---|---|
| Garrison 20k/10k, AB2 | 2 | 5 (2 + 5 = 7)* | **7** | **4** | **5** |
| Garrison 20k/10k, AB1 | 3 | 5 | **8** | **4** | 5 |
| Str Garrison 25k/15k, AB2 | 3 | 5 (0.2 hull/round) | **8-9** | **5** | 5 |
| 20k/10k with a heavy bomber (5-round magazine) | 2 | 5 | 7 | **2** | 3 |

\* Six rounds reach about 4% hull: the 40/s armour regen during the 4 s magazine spacing eats the 6th
round's margin. The task said "about 6 hits"; the exact data says **7 rounds** (or 8 AB1), which is why a
3-bomber/2-missile strike is just short and a 4-bomber mass lands the kill. `StrikeVolley.Compute` adds
**one spare bomber when the side's roster can field it** (margin for a miss, a ram or a defender kill);
if the roster is smaller it asks for the exact kill count, still released together.

The "shield-regen window" is `0.5 * maxShield / shieldRegen` = **50 s** for both garrison classes
(`ShieldRefillSeconds`; the all-important half-shield refill). `StrikeVolley` requires the whole volley to
land inside **20 s**, which covers a five-round magazine's 4 s spacing and is far shorter than 50 s.

The finale feed uses the **actual purchased loadout** (`StrikeVolley.FromParts` → missile power/load time/
magazine amount, and station-damaging guns), the **actual station type** of the observed target, and the
side's real roster. In the acceptance run that produced `required=5` (Stealth Bombers with 2-round AB
magazines: 4 to kill + 1 margin).

## 3. Design

### 3.1 Volley requirement (`StrikeVolley.cs`)

`Compute(stationProfile, loadouts, hullsAvailable)` replays the real rounds against the native damage/regen
law and returns the smallest group that kills inside one window, plus the margin. Pure and race-agnostic:
the same function is used for every civ's station types and any hull/missile the loadout selector grants.
`Replay` is also the loopback proof's engine.

### 3.2 Mass at a staging point (`ResearchTactics.cs:418-527`)

For a target with a real station profile, the wave now:

1. picks the same rally/aleph as before (`ChooseRally`) and computes the requirement;
2. holds all visible bombers at that rally until `_visibleRequired` are actually there (or, if the side
   cannot field the requirement, waits for the whole roster);
3. waits for the assigned lone wolves to reach their own entry alephs;
4. sets **one shared go time** (`StrikeGoLeadSeconds = 5`) and commits every member on that tick (`reason=massed`);
5. never substitutes "2 at the timeout": a full group or an abort. `StagingHardCapSeconds = 180` aborts the
   station with `reason=insufficient-volley`, puts it on a 120 s cooldown and lets the next boundary re-pick.
   The [strike] telemetry carries `required`, `assembled`, `wait`, `visible`, `loneWolves`, `goIn`.

Legacy behaviour is retained exactly when a target has no core profile (the SC7/SC8/SC9 unit fixtures), so
those pins still hold. The only one-bomber/zero-rally legacy shortcut is also preserved.

### 3.3 Fire together + composition (1 visible element + ≤ 2 lone wolves)

- The visible element is the gathering group at the rally and keeps P778a's escorts: `SelectEscorts` only
  assigns protectees from the visible members, and the existing escort/push code is untouched.
- Up to **2 cloak-capable strike hulls** are picked by `BeginStage` (real hull can mount `ET_Cloak` and the
  purchased loadout carries a working cloak — the same gate `CloakPolicy` uses). Each is routed to a
  **different aleph into the target sector** (`lanes`, excluding the visible group's rally), so it never
  joins the visible lane; at the go time it also gets the station attack order.
- If the side fields no cloak-capable strike hull, `loneWolves=0` and the strike is all visible (logged,
  not silent).
- Cloak rules are unchanged: firing replaces the control state with the weapon mask, dropping
  `cloakActiveIGC` (`src/Igc/shipIGC.cpp:3055-3070,3314,3359`; `CloakPolicy.Apply` refuses a cloak while a
  weapon bit is set), energy throttling is native `cloakigc.cpp:80-121`, ripcord drops the cloak
  (`shipIGC.h:1839-1840`), and detection is signature × scanner range (`shipIGC.h:353`; port
  `Allegiance.Core/Sim/Ship.cs:529-549`, `SeenBySide`). Nothing here changes those rules.
- Telemetry per strike: `[strike] ... commit ... visible=N loneWolves=M required=...` and one
  `[strike] ... finale release ship=... role=visible|lone-wolf delta=+X.Xs cloak=true|false
  nearestEnemy=... undetected=true|false` per member (see limitations for the honest cloak result).

### 3.4 Finale feed (`BotManager.StrikeMass.cs`)

`FinaleStrikeMassTick` runs under the finale lock once a second. It:

1. computes `RequiredBombers` from the finale target's real type and each armed bomber's real loadout
   (same `StrikeVolley`);
2. counts armed bombers actually at the staging point — a **3,500-unit standoff** from the station
   (5,000 and a lateral 2,500 offset for a lone wolf), which is outside the station's hull but close
   enough to fire immediately;
3. commits one wave when the quota is assembled (or releases one group at the 180 s cap) and sets a shared
   `GoTime`; each member holds at the standoff until `GoTime`, then the normal station attack runs, so
   P778a push and P791 repair still see the group;
4. a **relaunch that missed the wave holds for the next mass** — it never attacks alone (`ShouldRelease`);
5. **spent** bombers (already released this life) are excluded until a relaunch reloads them, so a wave
   cannot be re-committed with empty magazines; and
6. samples the target's live hull/shield for the outcome line (`shield before -> after`, `hull before -> after`,
   `hitsInWindow`, `outcome`).

## 4. Before / after

### Before (brief + coverage rig)

- 7 matches, 200v200, **0 Garrison kills**; every match ended on the deadline by kills.
- Coverage rig: **57 AB missile hits and 4 station kills in 2 matches**, 0 Garrison kills.
- Match 7 station 4329: AB2 hit shield `1.0 -> 0.5`, then the next hit started from a full shield; hull
  `1.0 -> 0.784` then rising to 0.658. Hits were minutes apart.

### After — P801 fogged acceptance match (`training_6`, research, native fog, 600 s wall cap)

- **3 massed finale commits**, `required=5`, `assembled=5/5/5`, `visible=3/3/3` + **2 lone wolves** each,
  waiting 19.5/24.5/1.0 s at the staging point; **0 aborted** stages.
- **167 positive anti-base missile hits** (all logged station damage was AB missiles: 81×AB3, 53×AB2,
  33×AB1), **37 station kills**, **7 Garrison kills**:
  `2080 Base`, `2081 Enemy Base`, and Str Garrisons `9201`, `9205`, `9208`, `9218`, `9220`.
- The match reached game over inside the cap after the station kills (264 s wall).
- Inter-hit gap on one station in that match: median **14.2 s**, p25 **7.1 s**, p75 30.3 s (vs the
  before/routine >50 s trickle). Largest number of logged AB hits on one station inside 20 s: **3**;
  the loopback proof under clean conditions lands **10/10 rounds in one window**.
- The live number is lower than the ideal 10 because defenders kill some bombers and some rounds miss;
  the mass still gives enough simultaneous damage to kill garrisons, which the trickle never did.

### Loopback proof (real `Station` model, not a reimplementation)

`P801StrikeLoopbackLiveTests` boots a real server, takes two real **Str Garrisons (types 110/210,
25k/15k, 50/150 regen)** and feeds the same Stealth Bomber AB2 rounds:

| schedule | result |
|---|---|
| massed: 5 bombers = 10 rounds inside one 20 s window | real station **dead** after 10 hits (armor 0, shield 0) |
| trickle: the same 10 rounds one minute apart | real station **alive** after 240 s (armour 1.0, shield 0.188) |

`hitsInWindow massed=10 trickle=2`. The pure replay agrees on the outcome. The real station needs 10
rounds where the raw-type replay kills at 9 because the live station applies the side's Global Attributes
to its effective max armour/shield (`Station.cs` EffectiveMax*); the planner deliberately uses the raw
station-type durability and therefore errs on the safe side.

## 5. Tests

New files:

- `tests/Allegiance.ClientCore.Tests/P801StrikeMassTests.cs` (8 tests):
  - `Real_garrison_data_matches_the_native_damage_arithmetic` (real 44/115/315 values, row-8 matrix,
    50 s half-shield refill);
  - `Requirement_scales_with_station_and_loadout_not_with_race` (AB1 vs AB2, Str garrison, heavy bomber
    magazine, every class-0 garrison type in the core);
  - `The_same_bombers_massed_kill_and_trickled_never_do` (pure replay; massed 7-round kill, 60 s trickle
    never kills);
  - `Massed_wave_holds_until_the_required_volley_assembles_then_releases`;
  - `An_unassemblable_volley_aborts_instead_of_trickling_in`;
  - `Lone_wolves_take_their_own_aleph_and_join_only_the_shared_go_time`;
  - `Without_a_cloak_hull_the_strike_is_all_visible`;
  - `Finale_bombers_wait_for_the_shared_go_and_a_relaunch_waits_for_the_next_wave`.
- `tests/Allegiance.ClientCore.Tests/P801StrikeLoopbackLiveTests.cs` (1 live/loopback test, real server
  stations, massed vs trickle; fast, no network).
- `tests/Allegiance.ClientCore.Tests/P801StrikeMassLiveTests.cs` (`[Trait("Tier","Live")]`, one fogged
  unattended showcase match, parses the server's own `[station-damage]` lines and the bots' `[strike]` lines).

**Fail on master:** the new tests use `StrikeVolley`, `required/assembled/loneWolves` event fields and the
massed/abort/lone-wolf/go-time behaviour, none of which exist on master. Behaviourally the staging tests
fail on the old policy (it commits 3 at the rally or 2 at the 90 s timeout, never holds for a requirement,
never aborts and has no lone wolves), the finale-feed test fails on the old one-at-a-time relaunch, and the
loopback massed-vs-trickle test has no old-policy equivalent to call. Nothing in the existing suite changed
its expected strings: legacy `[strike]` lines remain byte-identical (the mass fields are appended only when
`Required > 0`).

Affected existing tests also re-run: `SC7CommanderOrdersTests`, `SC8StrikeWaveTests`, `SC9StrikeWaveTests`
(they exercise the same policy and log pins).

## 6. Gate (light gate + never-parks)

| gate | result |
|---|---|
| whole-solution build (`dotnet build AllegianceCore.slnx`) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **72/72 tool projects built** |
| new + affected tests, 3× alone | **47/47 passed on each of 3 runs** (SC7+SC8+SC9+P801 unit+loopback) |
| new Live-tier P801 test, 3× alone (180 s cap) | **3/3 passed** (3 m 03 s each) |
| one Fast-tier suite (`tools/run-tests.sh --tier fast`) | **10,126 tests, 284.6 s, PASS** |
| showcase never-parks, 5× | **5/5 passed** (5m46, 5m45, 6m27, 6m02, 5m36; no base-rate regression) |
| `P798FullLoopLiveTests` regression (finale/waves touched) | **1/1 passed** (5 m 06 s) |
| fogged acceptance match (600 s cap) | **passed**; 7 Garrisons and 37 stations killed, game over |

No flakes were observed; nothing was added to `docs/TEST_FLAKES.md`.

Evidence: the 7 MB raw acceptance log is at `~/allegiance-evidence/P801/p801_acceptance_fogged_600s.log`;
test stdout for the acceptance and loopback runs is beside it. Small in-repo evidence:
`docs/reports/P801_assets/p801_summary.json`, `strike_lines.txt`, `station_kills.txt`,
`acceptance_summary.txt`.

## 7. Rig recipe (Claude)

Run the normal 200v200 march on **221** with **fog on** (no `showMap`, no `target_sector`);
`preset=showcase`, staging on, the research config used by P798 (`finale_after_minutes≈0.25`,
`finale_bombers=12`, `loadout_mode=best`). Then read the server log:

```bash
# massed strike edges: required vs assembled, visible vs lone wolves, go time
grep '\[strike\]' server.log | grep 'required=' | grep ' commit '
# per-member release and cloak honesty
grep '\[strike\]' server.log | grep 'finale release'
# station hits / kills (the server's own damage seam)
grep '\[station-damage\]' server.log | grep -c 'killed=False'
grep '\[station-damage\]' server.log | grep 'killed=True'
# did the match end by station elimination or the deadline?
grep '\[finale\] deadline winner' server.log
grep -E '\[gameover\]|\[scoring\].*baseKills' server.log | tail
```

Garrison kills: the `[station-damage]` name is the *mission* name (`Base`, `Enemy Base`, `Garrison (Str)`);
classify by the station type from the mission (class 0 = Garrison) rather than the name. Report per match:
massed commits (required/assembled), station kills, Garrison kills, and whether the match ended by station
kill/elimination or the deadline. Watch for `abort` lines — they are the fallback when a side cannot field
the volley and must be zero in a healthy 200v200 run.

## 8. Limitations / unfinished

- **Lone-wolf cloak is honest, not guaranteed.** In the acceptance run 2 of 5 logged lone-wolf releases
  reported `cloak=false` (the native energy/weapon gates dropped it; `nearestEnemy` was also close).
  Earlier 180 s runs produced `cloak=true undetected=true`. The telemetry records what happened rather than
  claiming invisibility. If Domingo wants the lone wolves to abort/rejoin when the cloak drops, that is a
  follow-up policy choice, not a native-rule change.
- **After a finale plan target dies the plan is not retargeted.** Surviving finale bombers finish other
  observed stations individually (many of the 37 kills above came after the two home Garrisons fell). The
  mass mechanism applies to the plan target and to relaunches while that target lives. Retargeting the plan
  to the next station would be a small follow-up; it was left out to keep this change bounded.
- The pure planner uses raw station-type durability, not GA-scaled `EffectiveMax*`; the real station is
  slightly tougher (loopback needed 10 rounds vs the pure 9), so the requirement is conservative.
- The `[strike]` `hitsInWindow` counter samples the client's live station fractions at 1 Hz and undercounts
  a burst; the report/acceptance numbers use the server's own `[station-damage]` lines instead (167 hits,
  max 4 in 20 s on one station). The loopback test gives the clean 10/10 in one window.
- Rig numbers (the 200v200 march) are Claude's per the lane rules; this worktree ran the loopback and one
  unattended in-process fogged match, not 221.
