# P999 — Why Gigacorp wins, and a specification-driven bot strategy

Gigacorp wins **34/56 completed local matches (60.7%)**, including **25/40
three-side matches (62.5%; 95% Wilson interval 47.0–75.8%)**. The 400-bot
three-side subset is **20/26 (76.9%)**; the earlier 210-bot subset is only
**5/14 (35.7%)**, so pooled results hide a major cohort difference. A supported,
testable explanation is an interaction: Gigacorp's fast research and economic
specifications fit the commander's exhaustive serial schedule, while the other
civilizations' strengths are only partly expressed by generic fleet/loadout
policies. The deadline also rewards absolute kill throughput: Gigacorp and
Omicron have nearly identical three-side K/D, despite very different win rates.
These observations do **not** establish that native human-play balance is broken
or identify an additive causal percentage for each explanation.

The local evidence corrects the dispatch tally: 40 three-side, eight two-side,
and eight **four-side** completed matches, no recorded draw. All 56 outcomes
have `[gameover]` receipts. The quoted 31/52 and five no-winner results cannot be
reproduced from these copies. Batches b95–b102 and b107 are absent; they are not
counted as losses, draws or zero-activity matches. Since b103 the available nine
batches contain 14 Gigacorp wins in 18 matches (77.8%, Wilson 54.8–91.0%), rather
than the dispatch's 15/20. An unfinished match-3 announcement is not a draw.

Scope: base `069090f6`, branch `codex/p999`; research, documentation, and an
**offline tool/test helper only**. No files under `src/`, server or MCP behavior,
authored plans, core data, broadcast or P998 loot/lifepod areas changed. No rig,
mini, Mac or public host was contacted; no commits, pushes or stash operations.

## Evidence, reproducibility and uncertainty

The parser reads the 28 available batch summaries and 185 worker logs, not a
new match run. Source identifiers, byte counts and SHA-256 hashes are in
[inventory.csv](P999_assets/inventory.csv). Raw logs, machine paths, bot names,
core JSON and build/test output remain in ignored `.scratch/p999/`. The public
[match ledger](P999_assets/matches.csv) has one row per finalized match/side;
[win-rate aggregates](P999_assets/win-rates.csv) retain the grouping and sample
sizes. All 185 worker files have zero conflicting membership mappings and zero
unmapped loadout selections. Mapping uses explicit side-bearing receipts and
unique starting-hull identity, never bot-name ordinal guesses.

`[matchup]` supplies civ, seed and slot; `[gameover]` supplies the authoritative
outcome. Deadline `values=` supplies side kills/deaths. Summary `match … side …`
rows preserve **only** station-kills/research/ship-kills from `[skills]`, not the
complete skills block. `[kills]` preserves pod/final-death causes. The b79 first
match ended without a deadline score line: its result and skills count, but its
three missing kill/death scores remain unknown. There are 39 scored three-side
matches per civ, not 40 with an invented zero.

The stated 400-bot population applies only from b91 in these copies:

| Batches | Sides | Controllable bot slots per side | Total bots | Completed matches |
|---|---|---|---|---|
| b76–b82 | 3 | 70 / 70 / 70 | 210 | 14 |
| b83–b86 | 4 | 53 / 53 / 52 / 52 | 210 | 8 |
| b87–b90 | 2 | 105 / 105 | 210 | 8 |
| b91–b94, b103–b106, b108–b112 | 3 | 134 / 133 / 133 | 400 | 26 |

Counts are distinct initially selected bot identities, excluding probe peers.
Per-bot metrics below divide side totals by **bot-match slots**, not by lives,
observations or the advertised 400. Team counters can include NPC interactions;
these are normalized team-output indices, not individual human-bot K/D.
For missing combat scores, their bot slots also leave the combat denominator.

Wilson intervals describe binomial sampling uncertainty, assuming independent
matches; rotations within a batch are correlated and configurations/software
were not randomized across this historical sequence. A deterministic 10,000
replicate, seed-999 bootstrap resampling whole batches gives three-side win
intervals of Gigacorp 47.5–77.5%, Phoenix 5–30%, Omicron 10–30% (20 batches;
[cluster intervals](P999_assets/cluster-intervals.csv)). Neither interval is a
causal treatment interval. Two-/four-side samples are too small for firm ranking.

Worker `[research]` has **no simulation timestamp**. Its roughly ten-second
snapshots can be bounded by nearby `[intel]`/`[opening]`/`[scout]`/`[gallery]`
process-clock diagnostics in the same client generation. Bounds spanning more
than 60 seconds, missing endpoints or a rotation are left unknown. Reported
research times are first-observation worker-clock intervals or their midpoints,
not exact server completion times; actual completion can precede a receipt by a
snapshot/network delay. `step` in that CSV is the current commander step when
observed, which can be the next step, not the research's exact plan index.
[Research events](P999_assets/research.csv) preserve those distinctions. An exact
step-versus-server-sim-time analysis has **zero timestamped completion receipts**
in the supplied summaries/workers; `[research-done]` was not saved there.

## Win rates, slots and opponents

| Sides | Civ | Wins / matches | Rate | 95% Wilson |
|---|---|---|---|---|
| 2 | Phoenix | 3/8 | 37.5% | 13.7–69.4% |
| 2 | Gigacorp | 5/8 | 62.5% | 30.6–86.3% |
| 3 | Phoenix | 7/40 | 17.5% | 8.7–31.9% |
| 3 | Omicron | 8/40 | 20.0% | 10.5–34.8% |
| 3 | Gigacorp | 25/40 | 62.5% | 47.0–75.8% |
| 4 | Phoenix | 0/8 | 0.0% | 0.0–32.4% |
| 4 | Omicron | 2/8 | 25.0% | 7.1–59.1% |
| 4 | Gigacorp | 4/8 | 50.0% | 21.5–78.5% |
| 4 | Iron Coalition | 2/8 | 25.0% | 7.1–59.1% |

Actual population changes the three-side picture substantially:

| Actual bots | Civ | Wins / matches | 95% Wilson | K/D | Kills / scored bot | Research / match | Station kills / match |
|---|---|---|---|---|---|---|---|
| 210 | Gigacorp | 5/14 | 16.3–61.2% | 1.232 | 5.363 | 134.357 | 2.429 |
| 210 | Phoenix | 5/14 | 16.3–61.2% | 1.126 | 5.144 | 83.429 | 2.214 |
| 210 | Omicron | 4/14 | 11.7–54.6% | 1.381 | 4.745 | 60.571 | 3.357 |
| 400 | Gigacorp | 20/26 | 57.9–89.0% | 1.452 | 7.324 | 139.038 | 2.692 |
| 400 | Phoenix | 2/26 | 2.1–24.1% | 0.812 | 4.846 | 80.038 | 0.692 |
| 400 | Omicron | 4/26 | 6.2–33.5% | 1.412 | 5.682 | 61.538 | 2.192 |

The 400-bot cohort alone is **20/26 Gigacorp wins (76.9%)**, versus 5/14
(35.7%) at 210 bots: a descriptive **41.2 percentage-point** difference. Phoenix
falls from 35.7% to 7.7%; Omicron from 28.6% to 15.4%. Research counts remain
similar across the two populations (G ~134 vs ~139; P ~83 vs ~80; O ~61 vs ~62),
so a research-volume explanation alone cannot explain the chronology. G's kills
per bot rise ~36.6%, P's fall ~5.8%, and O's rise ~19.8%.

The early cohort has combat scores for 13/14 matches per civ; the 400-bot
cohort has 26/26. Population, three/six/eight-worker partitioning, chronological software changes,
and match/map conditions were not independently varied. This is **not** an
identified causal effect of raising the bot count. It is a major confounder and
a reason to freeze population/build/worker layout in the follow-up A/B. The
[population cohort ledger](P999_assets/population-cohorts.csv) retains all
formats; fairness observed at 210 bots must not be generalized to 400.

The all-format totals are:

| Civ | Wins / matches | Rate | 95% Wilson |
|---|---|---|---|
| Phoenix | 10/56 | 17.9% | 10.0–29.8% |
| Omicron | 10/48 | 20.8% | 11.7–34.3% |
| Gigacorp | 34/56 | 60.7% | 47.6–72.4% |
| Iron Coalition | 2/8 | 25.0% | 7.1–59.1% |

Home slots are the side positions in the `homeSwap=off` receipts, not inferred
physical coordinates. The available complete three-side matches include **only
slots 0 and 2 for Gigacorp**. Slot 1 is in the announced but unfinished third
rotation. Home slot, match order and map rotation are therefore confounded:

| Sides | Civ | Side / home slot | Wins / matches | 95% Wilson |
|---|---|---|---|---|
| 2 | Phoenix | 0 | 2/4 | 15.0–85.0% |
| 2 | Phoenix | 1 | 1/4 | 4.6–69.9% |
| 2 | Gigacorp | 0 | 3/4 | 30.1–95.4% |
| 2 | Gigacorp | 1 | 2/4 | 15.0–85.0% |
| 3 | Phoenix | 0 | 4/20 | 8.1–41.6% |
| 3 | Phoenix | 1 | 3/20 | 5.2–36.0% |
| 3 | Omicron | 1 | 1/20 | 0.9–23.6% |
| 3 | Omicron | 2 | 7/20 | 18.1–56.7% |
| 3 | Gigacorp | 0 | 10/20 | 29.9–70.1% |
| 3 | Gigacorp | 2 | 15/20 | 53.1–88.8% |
| 4 | Phoenix | 0 | 0/4 | 0.0–49.0% |
| 4 | Phoenix | 1 | 0/4 | 0.0–49.0% |
| 4 | Omicron | 1 | 1/4 | 4.6–69.9% |
| 4 | Omicron | 2 | 1/4 | 4.6–69.9% |
| 4 | Gigacorp | 0 | 1/4 | 4.6–69.9% |
| 4 | Gigacorp | 3 | 3/4 | 30.1–95.4% |
| 4 | Iron Coalition | 2 | 0/4 | 0.0–49.0% |
| 4 | Iron Coalition | 3 | 2/4 | 15.0–85.0% |

Opponent identities are not collapsed across formats:

| Civ | Opponents | Wins / matches | 95% Wilson |
|---|---|---|---|
| Phoenix | Gigacorp | 3/8 | 13.7–69.4% |
| Phoenix | Omicron + Gigacorp | 7/40 | 8.7–31.9% |
| Phoenix | Omicron + Gigacorp + Iron Coalition | 0/8 | 0.0–32.4% |
| Omicron | Phoenix + Gigacorp | 8/40 | 10.5–34.8% |
| Omicron | Phoenix + Gigacorp + Iron Coalition | 2/8 | 7.1–59.1% |
| Gigacorp | Phoenix | 5/8 | 30.6–86.3% |
| Gigacorp | Phoenix + Omicron | 25/40 | 47.0–75.8% |
| Gigacorp | Phoenix + Omicron + Iron Coalition | 4/8 | 21.5–78.5% |
| Iron Coalition | Phoenix + Omicron + Gigacorp | 2/8 | 7.1–59.1% |

Gigacorp wins 10/20 first three-side matches from slot 0 and 15/20 second
matches from slot 2. Omicron wins 7/20 from slot 2 in first matches and 1/20 from
slot 1 in second matches. This supports a position/order interaction but cannot
isolate map geometry. Map labels survived truncation in the eight two-side
matches only (16 of 168 side-exposure rows); most `[matchup]` lines end before
map/geometry fields. Home-sector IDs are also truncated for some four-side rows.
There is no defensible map-adjusted effect size or evidence for Gigacorp winning
from *every* home slot in these copies.

## Combat, research and economy measured from the receipts

Three-side aggregates (40 matches/civ, combat scores available in 39/civ):

| Civ | Deadline kills / deaths (n=39) | K/D | Research / match | Station kills / match | Podded / match | Oxygen deaths / match |
|---|---|---|---|---|---|---|
| Gigacorp | 30302 / 21474 | 1.411 | 137.4 | 2.6 | 656.5 | 146.35 |
| Phoenix | 21439 / 24800 | 0.864 | 81.225 | 1.225 | 696.525 | 190.15 |
| Omicron | 24041 / 17092 | 1.407 | 61.2 | 2.6 | 619.325 | 113.3 |

| Civ | Bot-match slots / scored slots | Kills / scored bot | Deaths / scored bot | Research / bot | Station kills / bot | Oxygen / bot |
|---|---|---|---|---|---|---|
| Gigacorp | 4451 / 4381 | 6.917 | 4.902 | 1.235 | 0.023 | 1.315 |
| Phoenix | 4438 / 4368 | 4.908 | 5.678 | 0.732 | 0.011 | 1.714 |
| Omicron | 4451 / 4381 | 5.488 | 3.901 | 0.55 | 0.023 | 1.018 |

Gigacorp's three-side kill throughput is about **26% above Omicron and 41%
above Phoenix per scored bot slot**. Its aggregate K/D (1.411) is essentially
Omicron's (1.407), not a broad survival advantage. Phoenix's K/D is 0.864.
Gigacorp completes 1.69 times Phoenix's and 2.25 times Omicron's developments
per match. Omicron and Gigacorp both average 2.6 hostile station kills, but their
winning criteria differ: all eight Omicron wins use station-kills; Gigacorp's 25
wins comprise 13 station-kills, 11 kills and one non-deadline result (b79 first
match). In two-side play Gigacorp wins 5/8, four by kills and one by station-kills;
Phoenix wins 3/8, two by station-kills and one by kills. These small arms do not
establish a two-side imbalance.

This is consistent with the actual timeout rule, not a claim that research
itself awards victory. `Mission.Countdown.cs:60–73` first tries a unique station-
kill maximum when that preset criterion is enabled; otherwise it ranks
**absolute side kills**, then fewer deaths. It does not rank K/D. The station-
kill criterion is the existing P914 policy extension. The distinct `[skills]
ship-kills counter is **not** interchangeable with the deadline kill tally; both
are retained separately in the ledger.

Two- and four-side combat cohorts are retained separately (n=8 matches per listed civ; all eight have combat scores):

| Sides | Civ | Matches / combat n | Kills/deaths | K/D | Research / match | Station kills / match | Oxygen / bot |
|---|---|---|---|---|---|---|---|
| 2 | Phoenix | 8/8 | 5733/4812 | 1.191 | 106.125 | 1.125 | 1.814 |
| 2 | Gigacorp | 8/8 | 5917/4395 | 1.346 | 147.5 | 0.375 | 1.127 |
| 4 | Phoenix | 8/8 | 2438/1945 | 1.253 | 89.625 | 0.875 | 1.67 |
| 4 | Omicron | 8/8 | 2153/1411 | 1.526 | 61.75 | 3.125 | 1.41 |
| 4 | Gigacorp | 8/8 | 2460/1984 | 1.24 | 129.625 | 2 | 1.264 |
| 4 | Iron Coalition | 8/8 | 1380/1299 | 1.062 | 113.375 | 3.625 | 1.137 |

Their per-bot research/kill/death/station indices are retained in [win-rates.csv](P999_assets/win-rates.csv), rather than pooled into the three-side comparison.

Death causes (all 40 three-side matches; each cell is podded / finally killed):

| Civ | Gun | Missile | Mine | Collision | Out of bounds | Oxygen final | None final |
|---|---|---|---|---|---|---|---|
| Gigacorp | 22053 / 10394 | 2855 / 4710 | 450 / 323 | 160 / 54 | 742 / 570 | 5854 | 0 |
| Phoenix | 23685 / 11529 | 2063 / 3909 | 1875 / 918 | 46 / 63 | 192 / 177 | 7606 | 1052 |
| Omicron | 20184 / 7366 | 2818 / 4297 | 819 / 440 | 137 / 64 | 815 / 664 | 4532 | 75 |

These are death **counts by cause**, not damage by weapon subtype. Oxygen
accounts for about 26.7% of Gigacorp's cause-coded final deaths, 30.1% of
Phoenix's and 26.0% of Omicron's. `none` includes non-ballistic removal/cleanup;
cause totals can differ slightly from the earlier frozen deadline score. Nothing
here justifies assigning every oxygen loss to a hull-design flaw or changing the
P996/P998 pod behavior. A per-hull kill/death rate needs authoritative hull-at-
kill and killer-hull records; selection messages plus repeated pod navigation
messages cannot supply it.

### Where the observed lead begins

Use **only first matches of three-side batches** here: n=20/civ, avoiding the
match-2 clock offset and format differences. The table shows the median number
of completed development names in the last bounded snapshot at or before the
worker-clock horizon. It is not a direct damage or combat lead:

| Worker-clock seconds | Gigacorp (n=20) | Phoenix (n=20) | Omicron (n=20) |
|---|---|---|---|
| 60 | 2.0 | 2.0 | 1.0 |
| 120 | 5.0 | 4.0 | 3.0 |
| 180 | 9.0 | 6.0 | 5.0 |
| 240 | 13.0 | 9.0 | 6.0 |
| 300 | 17.0 | 12.0 | 8.0 |
| 600 | 24.0 | 15.0 | 15.0 |
| 900 | 43.0 | 24.5 | 18.0 |
| 1200 | 60.0 | 27.5 | 23.0 |
| 1800 | 93.0 | 43.5 | 38.0 |
| 2700 | 130.0 | 57.0 | 59.0 |

The first coarse research-count separation is between 60 and 120 seconds,
when Gigacorp moves from parity with Phoenix (2 versus 2 at 60) to 5 versus 4;
it already exceeds Omicron at 60. Some of Gigacorp's early researches only catch
up to equipment other civs already own, so this is **not** the first proven
combat-power crossover. First observed key milestones make that limitation
concrete. Cells give median midpoint seconds and timed observations / eligible
first three-side matches; unreached/censored cases are not imputed:

| First observed completion | Gigacorp | Phoenix | Omicron |
|---|---|---|---|
| Patroller | 244.2 (20/20) | not observed / not applicable | not observed / not applicable |
| Bomber | 325.8 (20/20) | 334.5 (20/20) | 510.2 (20/20) |
| Supremacy | 579.2 (20/20) | 600.0 (19/20) | 1036.2 (20/20) |
| Shipyard | 2391.0 (14/20) | 3531.0 (1/20) | 762.8 (20/20) |
| PW Gattling Gun 2 | 682.8 (20/20) | 766.0 (19/20) | 1300.2 (20/20) |
| GA: Ship Hull 1 | 640.5 (20/20) | 692.5 (19/20) | 1189.5 (20/20) |
| Upgrade Supremacy | 854.2 (20/20) | 1289.5 (16/20) | 1724.5 (19/20) |
| GA: PW&EW Damage 1 | 1295.2 (20/20) | 1852.5 (11/20) | 2957.2 (6/20) |

Shipyard is observed in 15 Gigacorp first matches, but only 14 have usable
clock bounds; Omicron advanced Supremacy is observed in all 20, with 19 timed.
Phoenix gun-damage GA is observed in 13, with 11 timed. Conditional medians favor
survivors and must not be read as a completed-tournament unlock schedule.

In **b112 match 1**, first Supremacy receipts are bounded at G/P 559–574 seconds
versus O 1025–1028; advanced Supremacy appears for G at 854 and O at 1700–1715,
while Phoenix never reaches it. Phoenix ends on step 28/186 with 26 developments
and no stations; its last reported wallet is 315,742. Gigacorp reaches all 149
developments, although it later repairs a lost home prerequisite; Omicron ends
with 70 developments while repairing a prerequisite. Station destruction and
repair amplify the timing difference after the opening, so later low Phoenix
research volume is partly an outcome of losing infrastructure.

There is **no timestamped kill/damage series** in these saved server summaries.
The first exact in-match lead in cumulative kills, fleet EHP, damage or income,
and its cause, is therefore unidentifiable. The earliest supported opportunity
window is Gigacorp's Patroller receipt around 244 seconds and then its earlier
advanced Supremacy around 854 seconds, not a proven lead from the initial spawn.

### What bots choose, and what is actually observed

[Hull selections](P999_assets/hulls.csv), [part selections](P999_assets/loadouts.csv)
and [role receipts](P999_assets/roles.csv) describe candidate purchases, including
retries. The hull CSV retains five-minute worker-clock windows from the first
diagnostic in each generation, with `boundary` and `unknown` cases; it is not
fleet occupancy over simulation time. Initial candidate counts and the most
common later hull choices are:

| Civ | Initial receipts | Initial hulls | Later receipts | Top later hulls |
|---|---|---|---|---|
| Gigacorp | 2851 | Scout 2851 | 38305 | Scout 18601; Patroller 5748; Bomber 4416; Lxy Fighter 2398; Fighter/Bomber 1901 |
| Phoenix | 2851 | Fighter 1608; Scout 1243 | 39328 | Scout 17503; Fighter 10502; Bomber 6008; Enh Fighter 3120; Adv Fighter 2128 |
| Omicron | 2440 | Fighter 1372; Scout 1068 | 29976 | Scout 12834; Fighter 6433; Bomber 4858; Enh Fighter 4317; Adv Fighter 1421 |

Initial Gigacorp choices are all Scouts, whereas Phoenix and Omicron choose
Fighters for approximately 56% of initial slots and Scouts for the rest. Logs
confirm actual mounts in the selected loadouts: an initial Gigacorp Scout has a
single PW Gat Gun 1, Sm Shield 1, two SRM Dumbfire 1s, a mine/probe dispenser and
four counters when the duty requests them; Phoenix's initial Fighter commonly
selects two guns, Booster 1 and five Dumbfires (variety can choose other legal
missiles/guns). Support selections instead mount a nanite gun. These examples
are traceable in b112's workers and the aggregate part census.

A selection is logged before buy/launch confirmation (`BotManager.cs:782–789`;
refits in `BotManager.TechLoadouts.cs:213`). `p682` reports confirmed **rotation**
launches, and `cloak-policy` includes an actual current hull for a nonrandom
subset of pilots. [observations.csv](P999_assets/observations.csv) preserves these
separately. They corroborate some fielded hulls, but do not establish complete
hull occupancy, actual granted loadouts over every life, or kills/deaths by hull.
There is no defensible per-hull K/D table in the available evidence.

Income and miner census are also absent: worker wallets mix grants/paydays,
donations, spending and replacements; `enemyMiners` is fogged enemy intel, not an
own-side fleet count. At worker-clock 300/600/900 seconds the first-three-side-
match median wallets are G 64,479/128,090/186,951; P 51,581/118,570/197,110;
O 67,958/129,992/199,571 (n=20 each). All are cash-rich by 600; G is not richest
at 900. This supports investigating research time/serialization and infrastructure
loss, rather than asserting a measured income or miner-volume advantage. Actual
income per minute, miners alive/mined ore, weapon-type damage, per-hull kills/
deaths, and victim-side station-loss counts are **unknown**, not zero. Three-side
station losses cannot be reconstructed by summing enemy station-kill credits.

## Core specifications and current policy

Loaded core: PCore001, version `990051329`, SHA-256 `F73AA7BBCE7414140F6691366F8829EA58829727660A8A86C114B29A83A55DE8`.

The full reproducible [specification census](P999_assets/core-specs.md) includes
all ten playable core civs: starting/no-development bits, starting station's
global/local techs, every starting hull including drones, raw/effective starting
hull stats, shield compatibility, hardpoints, equipment masks, ammo, magazine/
dispenser/chaff budgets, five cargo slots, all reachable human hulls, early/late
weapons, each station branch's required/effect/local bits, baseline serial costs/
times and every reachable GA-changing development. It is not restricted to the
three rig civs. Relevant base attributes are:

| Civ | Start human hulls | Home ID | Payday factor | Starting-money addition | Ship armor / shield | Speed / thrust | Gun / missile damage | Mining rate / yield / capacity | Dev cost / time |
|---|---|---|---|---|---|---|---|---|---|
| Dreghklar | Scout, Lt Interceptor | 26 | 1.125 | 0.15 | 0.9/1 | 1.2/1 | 1/1.1 | 0.65/1.35/0.95 | 1.25/0.5 |
| Technoflux | Scout | 40 | 0.85 | -0.2 | 1/1 | 0.9/1 | 1/1 | 2/1/0.55 | 0.75/3 |
| Ga'Taraan | Scout, Fighter | 44 | 1 | 0 | 1/1.1 | 1/1 | 1/1 | 0.8/1/1 | 0.8/1 |
| Phoenix | Scout, Fighter | 60 | 1 | 0 | 0.9/1.25 | 1/1 | 1/0.95 | 1/1.1/1 | 1/1.5 |
| Omicron | Scout, Fighter | 715 | 1 | 0 | 1/0.95 | 1/1 | 1.05/1 | 1/1/0.85 | 1/2.5 |
| Gigacorp | Scout | 15 | 1.25 | 0.25 | 0.85/1.1 | 1.1/0.9 | 1/1 | 1/1/1.25 | 1/1 |
| Iron Coalition | Scout, Lt Interceptor | 115 | 0.875 | -0.125 | 0.95/1 | 1/1 | 1.1/1.1 | 0.85/1/0.75 | 1/1 |
| Bios | Scout, Fighter | 210 | 1 | 0 | 0.9/0.9 | 1/1 | 1/1 | 1/1/1 | 0.5/5 |
| Belters | Scout, Fighter | 315 | 0.75 | 0 | 1.05/1 | 1.15/1 | 1/1 | 0.6/1.25/0.8 | 1.25/2 |
| Rixian | Scout [SR], Fighter | 415 | 1 | 0 | 0.9/1 | 1/1 | 1.15/1 | 0.9/1/1 | 1/0.1 |

Starting techs are the civ's base bits plus enabled mission path switches;
the starting station's global and individual local masks gate purchases. Base
home IDs for G/P/O are 15/60/715, all named Garrison. No-development mode uses a
separate mask. `ResearchSettings.cs:22–35` and `ResearchTree.cs:80–89` implement
those existing distinctions; station locals must not be unioned into a fictional
super-station. The appendix prints exact masks instead of inventing a translated
label for every core bit.

`GasBaseAttributes` is multiplicative, with development factors multiplied onto
it (`Igc/igc.h:935–945`). Development bucket price/time are frozen using the
civ-base factors at creation (`Igc/bucketigc.cpp:70–73`); buying later GA does not
retroactively make those buckets cheap. A nominal 120-second development costs
120 seconds for G, 180 for P, 300 for O, before any showcase research-time scale.
Station/constructor buckets use their authored time, not that development factor.
The initial Supremacy buckets are G/P 10,000 and 120 seconds; O 10,000 and 300.

IncomeMoney is a **fractional addition** to base payday, not currency. Native
`FedSrv/fsmission.cpp:1999–2002` adds BonusMoney to the starting-money setting;
`:2535–2546` applies `1 + IncomeMoney` to base payday, then adds station income.
Thus Gigacorp's +0.25 means 25% more base payday, with 1.25 mining capacity;
Phoenix has 1.1 mining yield and Omicron 0.85 capacity. Gigacorp starts with an
enhanced miner and constructor: the loaded large constructor is 3,600 raw HP,
80 speed versus P/O 2,880 HP and 60 speed (G's base speed GA further raises 80
to 88). This is an authored asymmetry, not a measured live miner/income count.

The relevant initial combat hulls, before developments and mounted-part mass:

| Civ | Hull | Armor HP | Shield 1 HP | Speed | Bare thrust/mass | Raw signature | Fixed guns | Gun ammo | Magazine budget | AB mask |
|---|---|---|---|---|---|---|---|---|---|---|
| Gigacorp | 210 Scout | 170 | 165 | 176 | 27 | 0.75 | 1 | 800 | 10 | 16 |
| Phoenix | 187 Scout | 180 | 187.5 | 160 | 30 | 0.75 | 1 | 800 | 15 | 16 |
| Phoenix | 190 Fighter | 270 | 187.5 | 110 | 25 | 1 | 2 | 600 | 25 | 3 |
| Omicron | 710 Scout | 200 | 142.5 | 160 | 30 | 0.75 | 1 | 960 | 10 | 16 |
| Omicron | 715 Fighter | 300 | 142.5 | 100 | 25 | 1 | 2 | 720 | 20 | 3 |

Gigacorp's basic Scout has 335 armor+shield HP versus Phoenix Scout 367.5,
Phoenix Fighter 457.5 and Omicron Fighter 442.5. It is faster, but starts with
one gun and no forward booster. **All three basic Scouts** have afterburner
mask 16, not just Gigacorp; ordinary Booster 1 has mask 2 and cannot fit. The
retro parts (188/208) have negative thrust and the existing support scorer
returns their negative MaxThrust, then requires a positive score
(`TechLoadoutSelector.cs:211–215,555–559`). The logs' empty Scout AB slot is
therefore consistent with current policy. This is an audit finding, not a new
behavioral fix. Gigacorp is uniquely forced into that Scout for every initial
human slot; a basic Phoenix Fighter can mount the forward booster immediately.
The magazine-size budgets translate into two Dumbfires on G/O Scouts, three on
Phoenix Scout, five on Phoenix Fighter and four on Omicron Fighter.

GA developments subsequently change mobility, durability, weapon range/damage,
mining, signature and station attributes. For example the complete per-civ
appendix lists the actual factors for Ship Hull/Shield, Missile Track, Ripcord,
PW&EW Damage, Faster He3, He3 Yield and their prerequisites. It also exposes the
five structural branch families rather than assuming there is only Supremacy:
Supremacy's fighters/guns, Tactical's stealth/energy/missile equipment, Expansion's
interceptors/mining/transport, Shipyard classes/capitals, and Garrison's common
ships/support/utility. Civ-specific classes and cross-branch requirements are
retained as bit dependencies, not hard-coded into the prototype.

### Existing generated commander and fleet choices

There are four authored files: `civ3-plan_{a,b}.json` and
`civ25-plan_{a,b}.json`. G18/P4/O5 use `generated`; **Iron Coalition 25 in b83–b86
uses authored plan_a**, so “every rig civ is generated” has that exception.
`Server.Lifecycle.cs:1271–1278` and `ResearchPlan.cs:63–82` choose the fallback.
`ResearchTree.Generate` (`ResearchTree.cs:92–119`) is one exhaustive algorithm;
it reads civ masks and scaled node costs but has no goal, opponent or strategic
power-spike score. The MCP recognizes the exact canonical generated plan and
regenerates it with `prioritizeResearch: true`
(`ResearchCommander.cs:76–88`). Its execution is still **serial**: one current
step/repair, `_index` advancing only on completion (`:263–270`). Authored orders
are preserved. The resulting G/P/O schedules have 168/186/147 steps, as b112 logs.

Roles are not entirely ignorant of core data: purchase gates, mounts, capability
flags, budgets, successors, weapon damage tables, rescue probes and bomber strike
hardware are data-driven. But the strategic mix is generic:

- `Showcase.ComposeShowcaseFleetCount` (`Showcase.cs:135–155`) assigns 46%
  Fighter, 20% Interceptor, 20% Scout, 14% Bomber when gunners are off; with gunners
  it uses 41/20/20/14/5 including Heavy. These are not per-civ strengths.
- `TechLoadoutSelector.cs:476–480` scores base hulls by sensor×speed, HP×speed
  or HP×fixed guns. `:524–550` scores weapons against a core-wide mean of fighter
  defense types. It does not rank using the owning side's current GA or the
  actual opponent civs' shield/armor mix. Civ identity constrains legality and
  seeds variety, rather than defining a strategic role portfolio.
- Varied mode (`:135–149`) prioritizes the least-used legal hull/part group;
  it is deliberately coverage-oriented. Its logged mode is varied in these
  batches. Support, bomber strikes, rescue scouts and field layers have explicit
  best-selector exceptions (`BotManager.TechLoadouts.cs:79–88`). One-in-three
  fighter defensive mine duty can request a Scout (`:111–116`).

Consequently “the bots never use civ attributes” is too broad: legality and
physical simulation do, and some selectors read capabilities/stats. The missing
link is **joint strategic valuation** of the civ's research, GA, fieldable
hardware, roles and opponents. No new behavior is wired by this task.

## Assessing each explanation

| Hypothesis | Quantified support | What remains unproved |
|---|---|---|
| Native specifications favor Gigacorp in this trio | Dev time 1.0 vs 1.5/2.5; base payday 1.25 vs 1/1; capacity 1.25 vs 1/0.85; enhanced starting economic hulls. G basic Scout armor+shield is only 335 vs P Fighter 457.5, so an initial raw combat advantage is not supported. | Native human win balance; how much of the 62.5% is attributable to any single attribute. Other core civs research faster still, e.g. Rixian 0.1 and Dreghklar 0.5. |
| Generic serial research suits G by accident | G completes 137.4 developments/match vs 81.225/61.2; advanced Supremacy first observed median ~854 vs ~1289/1724 seconds in first three-side matches. The algorithm has no power-spike goal or concurrent portfolio. | An A/B effect of reordering alone. Intrinsic bucket time, travel, station losses and survival interact. |
| Hull/loadout/role selection leaves strengths unused | G starts 100% Scouts, P/O ~56% Fighters; fixed role fractions and least-used variety persist; negative-thrust retro parts score below zero; own GA/opponent-specific defenses are absent from the ordinary selector's rank. | Actual per-hull K/D, mount-hitbox accuracy, or a win-rate gain from any proposed new ranking. The observed initial disadvantage cannot alone explain G winning later. |
| Home/map geometry contributes | G is 50% (10/20) from slot 0/first match vs 75% (15/20) slot 2/second; O is 35% vs 5% across its observed slots. | Slot vs map vs match-order causality; G slot-1 outcomes; a map-adjusted coefficient. Full three-match rotations and geometry receipts are required. |
| Deadline scoring and infrastructure snowball amplify it | G and O K/D ~1.41 but G kill throughput/bot is 26% higher; G gets 11/25 three-side wins by absolute kills, O 0/8. b112 Phoenix loses all stations while retaining >315k wallet. | Counterfactual outcome under another goal or without early infrastructure loss. Goal/core/server behavior must remain unchanged for the proposed strategy A/B. |
| Population/build/worker cohort contributes | G three-side wins rise from 5/14 at 210 bots to 20/26 at 400 (+41.2 points), while its research count changes only ~134 to ~139 per match. G kill throughput/bot rises ~36.6%. | A causal population effect, worker-partition effect, or specific historical code regression; those changed together and no randomized control exists. |
| Oxygen is the main cause | P has 1.714 oxygen deaths/bot-slot vs G 1.315 and O 1.018; it contributes to attrition. | That oxygen explains most of the win gap or which hull/navigation defect caused it. Historical batch revisions and exposure are not controlled; P998 is out of scope. |

These supports are overlapping, not additive “shares of the win rate.” Assigning
percentages summing to 100 would require controlled interventions that the task
explicitly forbids running here. The strongest testable claim is a **serial
commander × research-time/tree-shape interaction**, compounded by stock role
policy and timeout scoring. The evidence does not justify nerfing core data.

## Specification-driven strategy design

The profile belongs to the **loaded core**, not a lookup from civ ID to a named
path. A modded civ receives the same process. Its shared product drives both
commander goals and the intended fleet; research is valued for what the bots
can legally equip and deliver.

1. **Build a CivProfile.** Include base/no-development techs, station-local
   purchase contexts, all valid prerequisite alternatives and successor rules;
   every hull's mobility, armor/shields, energy/ammo, mass/turning, scan/signature,
   mount compatibility, cargo/magazines and crew requirements; part/projectile/
   missile damage matrices; starting economy and mining cycle; and each
   development's frozen cost/time and accumulated GA. Station path “names” are
   labels inferred from their unlocks; they are never control-flow keys. Cache by
   core identity/version, settings and civ. Invalid/unavailable parts never earn
   utility simply because a side-global mask says their name exists.
2. **Score feasible goal portfolios.** Compute prerequisite routes to concrete
   spikes (e.g. a sustained anti-ship loadout, a deliverable anti-base wing, better
   miner turnaround, or a rescue/repair capability). Score incremental delivered
   benefit per funding and critical-path completion time, using mission goals,
   map routes the side knows, and opponent civ specifications/public observed
   research. Counter the opponent's real armor/shield mix and mobility. Weight
   utility by the fleet that can actually purchase/use the equipment, not the
   maximum theoretical capital ship. Account for constructor travel, build site
   knowledge, station survival and crew before declaring a power spike delivered.
3. **Choose a primary and a counter/support path.** Compare multiple legal
   prerequisite routes; prune obsolete/redundant alternatives and value shared
   prerequisites. Select 2–3 close feasible portfolios (not mandatory named
   Supremacy/Tactical/Expansion scripts), choose deterministically from a stable
   hash of core identity, match seed, civ, settings, goals and opponent profile.
   Bound variety around useful options; retain a chosen opening until the spike
   completes or survival/goal evidence invalidates it. Inputs, chosen targets,
   alternative scores and rejection reasons must be logged.
4. **Schedule funding rather than one blocking list.** Slow-research civs should
   fund independent useful buckets while earlier ones mature and constructors
   travel, obeying existing native bucket/time/donation gates. Protect a defense/
   miner-replacement reserve; do not delay every useful development behind a
   blocked constructor or let every cheap development delay a needed station.
   Separate research, construction, prerequisite repair and fleet delivery jobs;
   allow bounded parallel investment where the native rules permit it. Replan
   lost/unavailable prerequisites without parking the fleet or endlessly waiting
   on an unseen/consumed rock. All construction intel remains side-observed.
5. **Derive hardware and role shares from the same profile.** Optimize legal
   complete loadouts with current GA, sustained ammo/energy, actual damage versus
   opposing armor/shields, acceleration after mounted mass, range, booster type,
   signature/scan, and missile magazine/lock behavior. A role is a duty, not a
   hull name. Weight an affordable fighter/scout/strike/support/recon mix against
   those duties, preserving recovery and bomber hardware honesty. A cheap fast
   G Patroller can cover a gap before its primary research path matures; P's
   shield/missile capacity and O's PW/turning/energy tradeoffs enter numerically,
   rather than requiring hand-authored civ branches.
6. **Keep showcase mechanisms exercised.** Reserve a configurable bounded
   coverage budget (initial proposal 15% of suitable slots/resources, with an
   explicit feasibility exception) for still-unexercised reachable hulls, guns,
   missiles, mines, probes, chaff, repair/rescue, turret/gunner/boarding/ripcord and
   goal-specific routines. Preserve existing native legality/visibility and the
   support/finale obligations. Schedule remaining reachable research/stations/
   drones as a coverage tail after the opening; a mechanism unavailable to the
   loaded civ is not a failing quota. Combat pilots always retain an actionable
   goal while research or coverage waits. Coverage receipts must distinguish
   selected, granted, launched, fired/hit and completed mechanisms.

No profile is allowed to read hidden enemy positions or server-only state. The
profile has deterministic initial choices; adaptation uses only public civ/
goal metadata and the side's own observations. The design must explicitly avoid
turning “variety” into a second all-civ cheapest-first plan. It should also avoid
rewarding a theoretical shipyard capital that no current role/crew will field.
The offline prototype below intentionally does not claim those online guarantees.

## Offline prototype and comparison

Pure function:
`tools/p999-civ-strategy/CivStrategyScorer.cs` `Generate(tree, coreName, seed,
variant, opponentCivs)`; executable I/O is confined to `Program.cs`. No source,
server, MCP or SinglePlayer project references it. Core tests link the helper.

The prototype uses each target's known-valid baseline prefix, backward-slices
tech-bit providers and **exact-replays** the slice. If a local/successor condition
makes a slice invalid it retains the valid full prefix. It scores these
opportunities, executes the best opening route, then completes all still-
reachable coverage steps in precomputed opportunity order. It is a feasible
heuristic, not proof of a globally cheapest path. All final plans validate.

Power considers per-real-station legal hulls/parts and successors, base plus
completed GA, fitted shields/missiles, fixed-gun damage versus opponents' starting
armor/fitting-shield and station defense classes, a 30-second energy/ammo window,
bare mobility/turning/signature, recon and nanite repair. Economy is explicitly a
dimensionless index of base payday, available station income and mining factors,
not predicted credits/sec. Starting-money addition affects the funding scale.
It takes no clocks, I/O, hidden game state or process-global random state.

For dimensions skirmish/siege/recon/economy/support, three policy weight vectors
are (0.50,0.20,0.10,0.15,0.05), (0.25,0.50,0.05,0.15,0.05) and
(0.30,0.15,0.10,0.40,0.05). Let R be the larger of initial power, 10% of
full reachable power, and 1 in each dimension. Opportunity utility is weighted
`log((R + after)/(R + initial))`, divided by
`1 + routeCost/(10000 * max(0.1, 1 + BonusMoney)) + routeBucketSeconds/300`.
A stable FNV-based seed preference multiplies it by 0.98–1.02. Normalizing each
power dimension prevents zero initial siege power from dominating merely because
its raw units are large. This is modern policy, not a native combat formula.

Seed 999 with opponent list 18,4,5 yields these opening routes. Costs/times are
**serial bucket** lower-level models at researchTimeScale=1, unlimited funds and
legal rocks; constructor travel/building effects are excluded:

| Civ | Variant | Opening route | Funding | Seconds | Score |
|---|---|---|---|---|---|
| Gigacorp | skirmish | Bomber | 10000 | 120 | 0.099 |
| Gigacorp | siege | Bomber | 10000 | 120 | 0.216 |
| Gigacorp | economy | Bomber | 10000 | 120 | 0.072 |
| Phoenix | skirmish | Shipyard → Upgrade Light Class → Upgrade Medium Class | 40000 | 480 | 0.097 |
| Phoenix | siege | Bomber | 10000 | 180 | 0.159 |
| Phoenix | economy | Shipyard → Upgrade Light Class → Upgrade Medium Class | 40000 | 480 | 0.068 |
| Omicron | skirmish | Shipyard → Upgrade Medium Class | 25000 | 600 | 0.076 |
| Omicron | siege | Bomber | 10000 | 300 | 0.12 |
| Omicron | economy | Shipyard → Upgrade Medium Class | 25000 | 600 | 0.049 |

The actual P942-generated baseline is used for the comparison, not the
older canonical cheap utility ordering. The full **first 40 steps** for every
variant and all three civs are printed side by side in
[plan-comparison.md](P999_assets/plan-comparison.md), together with cumulative
funding/time for key hulls, weapons and every station branch. Representative
model deltas for the skirmish variant (step / seconds / cumulative funding):

| Civ | Spike | Today | Offline skirmish |
|---|---|---|---|
| Gigacorp | Bomber | 19 / 2170 / 67510 | 1 / 120 / 10000 |
| Gigacorp | Patroller | 14 / 1570 / 35010 | 2 / 240 / 15000 |
| Phoenix | Bomber | 14 / 2355 / 57510 | 5 / 840 / 70000 |
| Phoenix | Upgrade Medium Class | 143 / 25005 / 708010 | 3 / 480 / 40000 |
| Omicron | Bomber | 14 / 3925 / 57510 | 3 / 900 / 35000 |
| Omicron | Upgrade Medium Class | 21 / 5815 / 112510 | 2 / 600 / 25000 |

These differences demonstrate targeted ordering, not measured improvements to
wins or live unlock times. In particular, the prototype likes a cheap direct
Bomber opening for Gigacorp and a Shipyard/class spike for P/O. Native faction
help recommends other viable openings too, notably Omicron Expansion. The tool
has **not** proved its own chosen opening better than those alternatives.

Known limits before any integration: the evaluator uses maxima rather than a
purchased/crewed role portfolio; it can overvalue large unfielded hulls. Damage
class averaging is approximate, not a mixed-weapon time-to-kill simulation.
Hitboxes, gun convergence, projectile delivery, mounted mass, afterburner flight,
shield regeneration timing, turret crews, constructor travel and attrition are
not simulated. The economy profile can share an opening with another profile;
three variants do not guarantee three different first steps. A minimum route is
not guaranteed by backward slicing; canonical baseline prefixes can omit a useful
redundant alternative. The coverage tail retains opening opportunity scores,
not adaptive marginal utility, and a utility station can still block the serial
live commander if wired in naively. Therefore this output is **not ready to be
plugged into production as-is**. The follow-up design addresses those limits.

## Follow-up tasks and the rig A/B

1. **Profile/scoring and observability.** Behind default-off `bots.civStrategy`,
   implement/cache immutable core-derived profiles and feasible role/crew-aware
   portfolio scores. Retain the existing off path exactly. Add bounded match-
   stamped receipts for match seed, civ, home slot, map, chosen plan/alternatives,
   server-sim research completions, actual granted hull/part lifecycle, damage by
   weapon/target hull, miners/ore/payday and victim-side station losses. Avoid
   per-tick logging or protocol changes; only add telemetry needed to identify
   the timing/attribution gaps found here. Reflect any new configuration/env in
   the required operator/compose documentation when actually introduced.
2. **Commander and fleet integration.** Use the same profile for mission-goal-
   aware primary/counter paths, bounded parallel funding/construction/repair,
   effective loadout choices and adaptive role mix. Keep ordinary player actions,
   native purchase/fog gates, support/finale hardware checks, coverage tail,
   rotation/recovery and skirmish sharing. Add deterministic offline and local
   socket/live tests before enabling the flag anywhere. Keep P998 and core data
   untouched; HUD changes are not required.
3. **Controlled rig qualification (Claude later).** Paired same-seed off/on arms,
   same build/core/config except the default-off strategy flag. Complete all
   three home rotations, counterbalance arm order, retain 400 actual bots and
   include both three-side G/P/O and a separately analyzed two-side control.
   At least **six completed matches per arm** with each civ exposed to all three
   slots is an engineering minimum, not evidence of fairness; extend if
   confidence intervals remain wide. Use at least two full rotations/arm and
   matched map/goal seeds. Do not mix the historical four-side IC cohort into
   the three-side denominator. Randomize seed/portfolio while preserving paired
   replay. Expand to other playable and generated/modded cores after the trio.

Required A/B metrics and acceptance:

| Metric | Required comparison |
|---|---|
| Per-civ win rate | Wins/exposures by format, slot, map, goal and seed; Wilson and batch/seed-cluster intervals; report all censored/failed matches. Target a narrower civ spread, not a forced scripted 1/3 result. |
| Match length | Median/p95 simulation seconds and capped-match fraction, with n; no improvement manufactured by removing hard matches or changing timeout rules. |
| K/D and combat | Per-civ team and per-bot throughput, K/D spread, damage by weapon and victim hull, station kills **and losses**, pod/oxygen causes; show when cumulative leads appear. |
| Research and economy | Milestone server-sim times and critical-path waits, research/station/constructor counts, miner uptime, ore and income; normalize by actual bot slots and simulation minutes. |
| Showcase coverage | Reachable development/station/drone counts plus granted/fired/hit mechanism quotas for guns, missiles, expendables, repair/rescue, gunners/boarding/ripcord and enabled goals; do not trade the showcase away for one rush. |
| Tick p95 | Same telemetry windows/population and paired seeds. Must not regress; declare a measurement tolerance before the run and rerun any statistically ambiguous increase. Also report bot decision-pass p95 and stall watchdog receipts so CPU cost is not displaced off-server invisibly. |
| Never-parks | Run the existing P700/P707 cohort unchanged, including its five-second idle-with-target bound and arrival threshold, then the new research-mode rotations. Require no new stuck constructor/research goal or idle combat pilot; keep existing deadline/recovery guarantees. |

Six matches/arm gives broad intervals, especially for each home slot. A small
functional pass should authorize a larger balanced sample, not a claim that a
win-rate change is established. No such A/B was run or authorized to the rig here.

## Verification and handoff

The report was written before the final gates. Results on the final code:

| Check | Passed | Failed | Skipped | Evidence |
|---|---:|---:|---:|---|
| `dotnet build AllegianceCore.slnx` | 1 build | 0 | 0 | 0 warnings, 0 errors; `.scratch/p999/solution-build.log` |
| `tools/build-tools.sh` | 134 projects | 0 | 0 | Every tool built with 0 warnings/errors, including P999; `.scratch/p999/build-tools.log` |
| `P999CivStrategyTests`, isolated run 1 | 15 | 0 | 0 | `.scratch/p999/test-results-1/p999-1.trx` |
| `P999CivStrategyTests`, isolated run 2 | 15 | 0 | 0 | `.scratch/p999/test-results-2/p999-2.trx` |
| `P999CivStrategyTests`, isolated run 3 | 15 | 0 | 0 | `.scratch/p999/test-results-3/p999-3.trx` |
| Python audit fixtures, isolated run 1 | 7 | 0 | 0 | `.scratch/p999/python-tests-1.log` |
| Python audit fixtures, isolated run 2 | 7 | 0 | 0 | `.scratch/p999/python-tests-2.log` |
| Python audit fixtures, isolated run 3 | 7 | 0 | 0 | `.scratch/p999/python-tests-3.log` |

That is **45 C# and 21 Python test passes**, with no failures or skips. Each C#
run exercises all ten core civs × three profiles against catalog validation and
actual mission-side buy gates, plus determinism/immutability, modded identity,
closed-path/research, development-time and opponent-defense tests. Test durations
were 59/55/55 seconds; no unrelated failures required a flake-ledger entry.

Glossary links pass (771 distinct paths); the default parity scorecard validates
all registered matrices; `git diff --check` passes. The 13 public artifact files
reproduce byte-for-byte from the saved offline core/audit outputs. Working-tree
secret, personal-path/email/address, Markdown-link and media checks pass; there
are no new media files. The commit-range publish hook is not represented as a
working-tree scan: no commits were created. No new native rule or parity credit
is claimed. Final logs remain ignored in `.scratch/p999/`.

- No `src/` changes: the brief's light gate does not require the full fast suite.
  No training/HUD/live behavior changed, so no Live lessons or visual captures.
- `local.env` is present; artwork-backed tests must report Skipped explicitly.
  NuGet audit remains enabled and Cbor remains pinned at 9.0.24. Restores/builds
  use the existing local package cache as their source to honor no-public-host
  contact; no fresh online vulnerability lookup is claimed.

Reproduction is documented in
[the tool README](../../tools/p999-civ-strategy/README.md). `core.json` records core
SHA/version, seed and opponents; `render.py` emits the spec census, baseline/
variant plans and sanitized tables. Public artifacts contain no personal paths,
addresses, hostnames or unreviewed media. All long-running commands were bounded with `timeout`; all started processes
finished before handoff. Changes remain in this working tree, uncommitted and
unpushed.

## Native intent

The core's civilization record contains names, masks, GA and starting objects,
**not a narrative faction description** (`Igc/igc.h:2071–2083`). Intent comes from
the shipped faction help and authored hull/part descriptions, checked against
the actual core rather than treated as balance constants. The local artwork
`hlpGigaCorp.mml:3–23` describes economic capacity, enhanced starting miners/
constructors, light infrastructure and Patroller/carrier or Tactical openings,
balanced by weak hull/handling and a Scout-only start. `hlpPheonix.mml:3–25`
(spelling as shipped) emphasizes shields, missile capacity, Transceiver/Vanguard
and special weapon synergies, with slower research and weak hull/missile damage.
`hlpOmicron.mml:3–21` emphasizes turning, PW, tele-refineries and Expansion/
advanced-tech rushes, and explicitly recommends overlapping investment for
five-minute research/constructors. Other faction help covers multiple paths too;
for example `hlpBios.mml:19,27` discusses partial investment around ten-minute
research. Help text can drift: use the extracted core for numeric truth.

Native Wopr is precedent for separate primary/secondary paths, not a
specification-driven strategy to copy. The locally verified
`Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:40–106,1110–1210` contains
literal named tech queues and primary/secondary path execution;
`:1290–1352` chooses a next techbase from available U/C/S rocks and existing
bases, not from per-civ hull/GA utility. It does not establish a native claim
that one civilization should win 60% of these bot matches.

The prototype reuses existing native-equivalent availability/mount/scaling code;
it ports **no new native gameplay rule**, so no new parity-completion row is
claimed. Native references were rechecked locally: GA multiplication
`Igc/igc.h:935–945`; development scaling `Igc/bucketigc.cpp:70–73`; mount masks and
five cargo mounts `Igc/hullTypeIGC.h:163–175,199–212` and `Igc/igc.h:845`; money
`FedSrv/fsmission.cpp:1999–2002,2535–2546`. Existing implementation files retain
their parity tracking. Native sources were read-only references.

## Modern approach

Treat the loaded civ specifications as a joint research/equipment/role problem,
keep native mechanics and public observation boundaries, and measure delivered
power and coverage rather than research volume alone. The saved data supports a
serial-schedule interaction and a carefully controlled default-off experiment;
it does not support a core nerf, an invented per-hull K/D, or an unmeasured claim
that this offline scorer already fixes the win-rate gap.
