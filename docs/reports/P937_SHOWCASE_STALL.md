# P937 — why research showcase matches stall (investigation report)

**Verdict.** The stall is a **port regression in the showcase research-plan
cache**, introduced by the P929 civilization rotation. On every match where
`civRotation=alternate` shifts the civilizations one side (b65 matches 2 and 4,
`civShift=1`), `PrimeShowcasePlans` has only cached plans for the *configured*
`games[].civIds` pairing, while `SelectShowcasePlans` looks them up by the
*rotated* civilization. Both lookups miss, so `mission.ResearchPlans` is empty:
no `RESEARCH_BRIEFING` is sent, the bot research commander is never seated, not
one credit is donated, and the bot finale cannot send
`ShowcaseFinaleRequest` because it requires the briefing's epoch. The server
therefore never arms a research deadline and the match has no end condition at
all. Matches 2 (9 h) and 4 (4 h) are exactly the rotated matches; matches 1 and
3 are exactly the unrotated ones and both finished.

Nothing about He3, money, asteroid supply, home position or civilization
balance blocks the plan: on the stalled matches the research economy never
starts.

Base `0d52f721` (worktree `hermes/p937`). No source file was modified; the only
committed-intent artefacts are this report and `P938_SHOWCASE_STALL_FIX_DRAFT.md`.
No commit, no push, no stash, no remote host was contacted.

---

## 1. Method

(`<evidence>` = a local, uncommitted directory of filtered rig logs; not part of the repository.)

Evidence (read-only, never copied into the repo):

| File | Content |
|---|---|
| `<evidence>/b65-server-filtered.log.gz` | rig server log, batch b65, 2026-11-05T03:07:48Z → 17:55:42Z, 412 286 lines (timestamps are ISO-8601 UTC per line) |
| `<evidence>/err.worker-1.log.gz` | bot-driver worker 1 (233 598 lines; the commander worker: `[commander]`, `[research]`) |
| `<evidence>/err.worker-2.log.gz` | bot-driver worker 2 (141 521 lines; combat drivers) |

Worker lines carry no absolute timestamps. They were anchored to server UTC with
the worker's own `[stall-minute] utc=…` rows (one per minute, 886 per worker),
which bracket every line to within one minute; cross-checks use events both
sides log (`[finale] trigger … at t=1800s` on the worker vs `[finale] research
showcase … armed` on the server 1–4 s later). All excerpts below are quoted
verbatim except for elided middle sections (`…`).

Read/zgrep only; nothing was decompressed into the worktree. Two read-only
reproductions were built under the worktree's `.scratch/` (now deleted):

* `mapcheck` — runs `MissionMapGenerator.Generate` with the match-2 recorded
  seed and prints the asteroid census (reproduced `[boot] … seed=1123059318
  … (4 sectors / 8 warps / 2 stations / 80 asteroids)` exactly).
* `rotcheck` — boots `Allegiance.Server.Server` with the showcase research
  config, `CivIds=[18,4]`, `CivRotation="alternate"`, and rolls three matches,
  printing each mission's `ResearchPlans` and home station types.

Native reference (the FreeAllegiance source) was re-grepped for every
native citation below.

---

## 2. The four matches of b65

| # | Window (UTC) | Map | Side 0 / Side 1 | Plans | Research activity | First devcomplete | Constructors | Bucket donations | Finale trigger | Server deadline armed |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 03:07:50 – 03:58:25 | `m2xspiderw.igc` | Gigacorp(18) / Phoenix(4) | `generated` / `generated` | 582 `[research]` rows | 03:08:24 | 25 | 244 | 03:43:28 | 03:43:29 (side 0) |
| 2 | 03:58:25 – 12:57:07 | `gen:SingleRing` | Phoenix(4) / Gigacorp(18) | **none / none** | **0 rows, 0 `[commander]`** (see note) | **never** | **0** | **0** | 04:30:42 | **never** |
| 3 | 12:57:07 – 13:47:06 | `gen:PinWheel` | Gigacorp(18) / Phoenix(4) | `generated` / `generated` | 584 rows | 12:57:53 | 19 | 245 | 13:32:08 | 13:32:11 (side 1) |
| 4 | 13:47:06 – 17:55:42 (log end) | `m2xspiderw.igc` | Phoenix(4) / Gigacorp(18) | **none / none** | **0 rows, 0 `[commander]`** | **never** | **0** | **0** | 14:22:08 | **never** |

Durations: 50 m 35 s, **8 h 58 m 42 s**, 49 m 59 s, **4 h 08 m** (still running
when the batch was redeployed).

Matchup receipts (verbatim, trimmed):

```
2026-11-05T03:07:50.198483989Z [matchup] mission #0 match=1 seed=2121854344 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) … civRotation=alternate civShift=0 map=m2xspiderw.igc …
2026-11-05T03:58:25.423000028Z [matchup] mission #0 match=2 seed=116006601 side0=4:Phoenix(none) side1=18:Gigacorp(none) … civRotation=alternate civShift=1 map=gen:SingleRing …
2026-11-05T12:57:07.986694682Z [matchup] mission #0 match=3 seed=346321653 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) … civRotation=alternate civShift=0 map=gen:PinWheel …
2026-11-05T13:47:06.562814773Z [matchup] mission #0 match=4 seed=1646500660 side0=4:Phoenix(none) side1=18:Gigacorp(none) … civRotation=alternate civShift=1 map=m2xspiderw.igc …
```

**The correlation is total**: `civShift=1` ⇒ `(none)` ⇒ no research, no
deadline, hours of play; `civShift=0` ⇒ plans ⇒ deadline ⇒ match ends. It is
dictated by match parity, not by map type (the two stalled matches are one
generated and one file map; the two finished matches are likewise one of each).

*Note on the `[research]` counts: 1 184 rows in worker-1 in total — 12 before the
first UTC anchor (boot), 582 in match 1, 6 in the first ~20 s of match 2 (the
match-1 commander's last rows before its world resets), 584 in match 3, none in
match 4.*

---

## 3. Q1 — where does the research plan stop in matches 2 and 4?

**It never starts. There is no plan object at all, so there is no "next
development/station/tech".**

Per side (identical on both stalled matches, both sides):

* `mission.ResearchPlans` has no entry for the side: `(none)` on the matchup
  receipt above. `MatchupDetails` prints exactly this when the dictionary lacks
  the side (`src/Allegiance.Server/Server.Lifecycle.cs:1272-1279`).
* No `RESEARCH_BRIEFING` packet is sent for the side
  (`Server.TeamCiv.cs:242-245` returns null without a plan). Server-side count of
  briefing re-sends in the whole batch: **214 rows at 03:08 (match 1) and 214 at
  12:57 (match 3); zero during matches 2 and 4** — including the joiner bursts at
  03:58:26 and 13:51 that would have carried it.
* The bot commander loop therefore skips the side every tick
  (`tools/allegiance-mcp/BotManager.Research.cs:79-81`: `if (briefing is null …
  continue;`), no `ResearchCommander` is ever constructed
  (`BotManager.Research.cs:97-104`), and the one `[research]` telemetry row per
  side per 10 s is never printed. Worker-1 rows: 1 184 in total — 582 in match 1,
  584 in match 3, 6 stale rows in the first 20 s of match 2 (the match-1
  commander before its world resets), and nothing in match 4; worker-2 owns no
  commanders and never logs `[research]`.
* Consequently the first plan step is never funded. Step 1 of both generated
  plans is the 10-credit development `Weedcore 03`; in match 1 it completed at
  03:08:24, 34 s after start, from a 10-credit donation at 03:08:22
  (`[bucketdonate] 3: player 8100 donated 10 into bucket 213 …`). In matches 2
  and 4 there is **no `[bucketdonate]` row at all**, no `[constr]`, no `[build]`,
  no `[devcomplete]`/`[devcompleted]`, no `[sidetech]` for 9 h and 4 h
  respectively.

First telemetry rows of a working plan (match 1, both civs), i.e. what *would*
have been next:

```
[research] side=0 plan=generated step=1/168 idle '' … status=waiting for mission start
[research] side=0 plan=generated step=2/168 drone '.TowerAC' … status=building bucket
[research] side=0 plan=generated step=3/168 drone '.Caltrop' …
[research] side=0 plan=generated step=4/168 station 'Refinery (Lt)' … status=building bucket
[research] side=1 plan=generated step=1/186 idle '' …
[research] side=1 plan=generated step=2/186 drone '.TowerAC' …
[research] side=1 plan=generated step=4/186 develop 'Retro Booster' …
```

So the candidate blockers named in the brief can all be excluded for matches 2
and 4:

| Candidate blocker | Evidence it is **not** the blocker |
|---|---|
| money | `[payday] side 0 'Alpha' -> PAYDAY(114) delta=28037 balance=98` at 04:00:09 and every 60 s for nine hours; totals over match 2: side 0 $15 144 958 (667 paydays), side 1 $18 930 423 (663 paydays). A 10-credit bucket was never funded. |
| He3 | 36 He3 asteroids exist (see §5); mining never stops; ore sold in match 2 = 13 061 for $1 092 389. |
| missing station / constructor | any constructor is itself a plan step ("station" steps fund a constructor bucket, `ResearchCommander.cs:197-236`); with no plan nothing is ordered, so their absence is a consequence, not a cause. |
| missing asteroid type | `PrepareResearchMap` never ran for these matches (no `[research-map]` row); that is a consequence of the empty plan dictionary, see §5. |
| bot commander state / refused order | there is no commander and no order; worker-1 logs **zero** `[commander]` rows in both matches. |

From when: from the first tick of each match — 03:58:25 for match 2 and
13:47:06 for match 4 — because the plan is already absent at assembly.

---

## 4. Q2 — match 1 vs match 4 (same map, swapped civs)

Both matches use `m2xspiderw.igc` with identical homes
(`homeSwap=off homeMatch=1/4 homes=0,1 home0=1050 home1=1051`), so side 0 is in
Ganymede and side 1 in Organia in both. The civilizations are swapped by
`civRotation=alternate` (`civShift=0` vs `civShift=1`), and **that swap is the
only difference that matters**: match 4 has no cached plan for the rotated
pairing.

Early-match comparison (first 5 minutes):

| Observable | Match 1 (03:07:50) | Match 4 (13:47:06) |
|---|---|---|
| `[research-plan]` rows | 2 (civ18 168 steps, civ4 186 steps) at 03:07:50 | **0** |
| `[research-map]` rows | 10 at 03:07:50 | **0** |
| `RESEARCH_BRIEFING` packets | 214 | **0** |
| first `[devcomplete]` | 03:08:24 (34 s) | **none in 4 h** |
| first `[constr]` | 03:08:59 (`Refinery (Lt)`, side 0) | **none** |
| `[bucketdonate]` rows | 244 | **0** |
| mining | 30 mine-start transitions, 27 sells | 1 020 transitions, 81 sells (economy runs) |
| paydays | 64 + 67 | 291 + 290 (money flows) |

So it is **not side- or home-position dependent** (identical homes, opposite
outcome), **not civ dependent** (the same civ pair appears on both sides of the
pair — Gigacorp as side 0 in match 1 and as side 1 in match 4, with identical
homes — yet only the unrotated match develops technology), and **not chance** (deterministic parity: 2/2 rotated
matches stalled, 2/2 unrotated matches finished, across two different map
types). Within the two finished matches the research economy also looks
normal — wallets in the millions by the end of the match (worker row at
13:46:45: `wallet=8095194` side 0) — while the stalled matches never move a
single credit into a bucket.

---

## 5. Q3 — He3: confirmed or refuted?

**Refuted as the root cause of the stall.** He3 was never exhausted, it is not
exhaustible (both engines regenerate it), and it cannot explain a match in which
no technology work is attempted at all.

### 5.1 The match-2 map, exactly

Recorded generation and reproduction:

```
2026-11-05T03:58:25.418042615Z [boot] game 'Test Mission' generating map: topology=SingleRing seed=1123059318 (drawn) (4 sectors / 8 warps / 2 stations / 80 asteroids)
2026-11-05T03:58:25.423035095Z [gameover] P439: mission #0 finished -> rebuilt fresh STAGE_NOTSTARTED (4 sectors, 2 sides, 8 warps, 2 stations, 80 asteroids)
```

Re-running `MissionMapGenerator.Generate` with `SingleRing`, `seed=1123059318`,
`teams=2`, `civIds=[4,18]` and the default resource counts reproduces the
recorded topology and object counts exactly: 4 sectors / 8 warps / 2 stations / 80 asteroids, and

```
capability=0x01 count=36   (He3, 83.33 ore each)
capability=0x08 count=36   (generic buildable)
capability=0x10 count=8    (special: Uranium class only)
sector 0 'Karlo'    home=True  asteroids=22 he3=10
sector 1 'Cirt'     home=False asteroids=18 he3=8
sector 2 'Oblivion' home=True  asteroids=22 he3=10
sector 3 "L'Sau"    home=False asteroids=18 he3=8
asteroid 27 'He2615' cap=0x01 sector=1 pos=(1097,11,-37)
```

The server log names the same rock at the same coordinates:
`[mine] ship 8417 began mining asteroid 27 'He2615' (ore 83.33/83.33) at
Vector3 { X = 1097.2902, Y = 10.623074, Z = -37.13075 }`.

So match 2 had **36 He3 rocks (≈3 000 ore)**, 36 generic buildable rocks and
8 special rocks — and **no Silicon/Carbon (0x20/0x40) rock at all**
(`MissionMapGenerator.cs:1186-1194` emits every generated special as the plain
`AabmSpecial` 0x10 class when `_fileMapResearch` is null; only the file-map
top-up path sets the per-path U/Si/C classes). Native cycles the special types
from the enabled research paths instead (`Igc/asteroidigc.cpp:179-224`
`GetSpecialAsterioid`/`NumberSpecialAsteroids`, used at
`Igc/common.cpp:3043-3060`). For a research showcase this gap is covered by
`PrepareResearchMap`, which tops the home cluster up to "one per planned
construction plus one reserve" per `AsteroidClass`
(`src/Allegiance.Server/Server.Showcase.cs:96-127`); it simply did not run in
matches 2 and 4 because it iterates the (empty) plan dictionary. That is why
match 3, the other generated map, logs top-ups and match 2 logs none:

```
2026-11-05T12:57:07.985558445Z [research-map] side=0 capability=32 available=4 planned=3 added=4
2026-11-05T12:57:07.985562223Z [research-map] side=0 capability=64 available=4 planned=3 added=4
```

### 5.2 He3 is not exhaustible (native rule, ported)

Native: `CasteroidIGC::Update` regrows a mineable rock toward `oreMax` every
tick at a rate proportional to the deficit — `Igc/asteroidigc.h:47-56`. The
rock is never removed. The port implements the same rule
(`src/Allegiance.Core/Sim/Asteroid.cs:36-39`, `Sector.cs:4912-4914` load
`c_fcidHe3Regeneration`). Native maps are generated with a per-sector He3
count (`Igc/common.cpp:3020-3040`) from `nPlayerSectorMineableAsteroids` /
`nNeutralSectorMineableAsteroids` (`Igc/igc.h:1205-1206`, defaults 2/4 at
`Igc/igc.h:1324-1325`). "Field depleted" in native means the miner's
`MineOre` returns less than requested and it re-picks an order
(`Igc/shipIGC.cpp:2240-2247`), not a dead field.

Observed in match 2: 18 distinct rocks were logged at ore fraction 0.000 and
mining continued to the last full hour (26 sells in 11:00-12:00). Rock 27's ore
fraction oscillated between 0.00 and 0.96 all match while it kept yielding.

### 5.3 So what was the "Miner requesting unoccupied He3 asteriod" flood?

That voice is `droneTooCrowdedSound = 1342` and it fires on the **enemy
lockout** branch, not on depletion: native complains when another *side* is
already at the asteroid (`Igc/shipIGC.cpp:2185-2205`). The port's arbitration
mirrors it (`src/Allegiance.Core/Model/Sector.cs:4933-4952`, complaint at
:4945). The complaint count per match is 345 in match 2 (side 0: 178, side 1:
167), 2 in match 4, 0 in matches 1 and 3 — i.e. it tracks the small single-front
map, not the stall.

Mechanism in b65 match 2: `SingleRing` puts both homes one hop from the neutral
sector `Cirt` (sector 1), and rock 27 is the nearest He3 rock for both sides.
12 396 of 12 940 mine-start transitions in the match were on that one rock, from
**135 different ships of both sides** (e.g. `8542`, `8547` are side 1 and
`8417`, `8537` are side 0). Both sides are in the rock's lockout set at the same
time, so neither mines; the complaint fires with the rock still at ~0.9 ore
(`04:01:18 … ore fraction 0.964`). Note the port also auto-targets the *first
eligible rock in sector order* for every player ship in the sector
(`Sector.cs:4922-4927`, a documented port extension), and its autonomous miner
selection is nearest-only with a same-side reservation filter
(`src/Allegiance.Core/Sim/Ship.cs:2588-2618`) whereas native also applies
`c_ttLeastTargeted` (`Igc/shipIGC.h:2297-2303`, counting rule
`Igc/common.cpp:591-612`). Both facts concentrate fleets on one rock; neither
stops the research economy, which is separately dead for the missing plan.

### 5.4 Mining totals (money is not the constraint)

| Match | sells | ore sold | credits | payday credits (both sides) |
|---|---|---|---|---|
| 1 | 27 | 1 596 | 141 958 | 9 473 213 + 1 503 228 |
| 2 | 273 | 13 061 | 1 092 389 | 15 144 958 + 18 930 423 |
| 3 | 28 | 2 467 | 212 325 | 8 740 252 + 1 475 915 |
| 4 | 81 | 4 798 | 402 286 | 6 983 353 + 8 728 685 |

With `incomeMultiplier=5000` the treasury pays $28–35 k per side per minute for
the whole stall; the wallets are never the limit.

---

## 6. Q4 — root causes, with citations and classification

### 6.1 Primary: research plans are lost on rotated matches (port bug)

The chain, in code order:

1. `Server.Lifecycle.cs:1204-1242` (`PrimeShowcasePlans`) caches a selection per
   `(game, side, civilization)` and — for fixed civ ids — only for the
   *configured* one: `var civs = random ? LegalLobbyCivs(core)… :
   [game.CivIds[side]];` (`:1221-1223`).
2. `Server.Lifecycle.cs:1342` rotates the resolved civ array before assembly:
   `var civIds = MatchCivilizations.Rotate(g, MatchCivilizations.Resolve(g, core,
   civSeed), homeMatch);`
3. `MatchCivilizations.cs:38-44`: `shift = (match - 1) % teams`, so match 2 gets
   `[4, 18]` and match 4 `[4, 18]` while match 1/3 keep `[18, 4]`.
4. `Server.Lifecycle.cs:1244-1251` (`SelectShowcasePlans`) looks up
   `(game, side, civIds[side])` — `(game,0,4)` and `(game,1,18)` on rotated
   matches, keys that were never primed — and **silently skips every miss**.
5. `Server.Lifecycle.cs:1387-1390` passes the therefore-empty dictionary to
   `PrepareResearchMap` and `Server.Lifecycle.cs:1403-1407` writes nothing into
   `mission.ResearchPlans`.

`git show c231a20e` (P929) touches only the rotation call site; the plan cache
was not adapted. Reproduction (`rotcheck`, three matches, research tech mode,
`CivIds=[18,4]`, `civRotation=alternate`):

```
match 1: side0=civ18:plan(civ18) side1=civ4:plan(civ4) | … | cap=3600
match 2: side0=civ4:NONE     side1=civ18:NONE     | … | cap=3600
match 3: side0=civ18:plan(civ18) side1=civ4:plan(civ4) | … | cap=3600
```

and the reproduced log carries the same `side0=4:Phoenix(none)
side1=18:Gigacorp(none)` matchup line and no `[research-plan]`/`[research-map]`
rows for match 2.

Side effects of the same miss:
* `PrepareResearchMap` never runs → no branch/special rocks are topped up (for
  match 4's file map the baked rocks may already cover some classes; for match
  2's generated map it is decisive, since that map has no Si/C rock at all —
  see §5.1);
* no briefing → no commander → no donations → no developments, constructors or
  stations.
(Home station *types* were checked and are **not** affected:
`Mission.ReplaceUnavailableStationTypes` (`src/Allegiance.Core/Model/Mission.FileMap.cs:15-28`)
retypes a baked station whose required techs the receiving side cannot satisfy,
and generated maps are drawn with the rotated civ array
(`Server.Lifecycle.cs:1350`); the reproduction prints each side's restart station
type equal to its civilization's initial type in all three matches.)

Classification: **port bug** (P929 × P825/P802 interaction). Not native: the
per-match civ rotation is a port experiment
(`src/Allegiance.Server/ServerConfig.cs:227-230`: "P929 side-order experiment:
off (default) or alternate …"), and native keeps the mission's `rgCivID`
untouched when a side's civ changes (`FedSrv/fsmission.cpp:5272`, the assignment
is commented out), so a native mission always has the plan of the civ it was
created with.

### 6.2 Secondary: the showcase deadline is only a client request (port orchestration gap)

* The bot arms the server deadline only at the `ShowcaseFinale` maturity trigger
  (`tools/allegiance-mcp/BotManager.Finale.cs:825-836`), and only if a
  `ResearchBriefing` with a parseable epoch exists:
  `if (Guid.TryParseExact(peer.World.ResearchBriefing?.Epoch, "N", out var
  epoch)) peer.SendShowcaseFinale(epoch, researchPeer.Side);` — otherwise it is
  **silently skipped**.
* The trigger is time/kill based, not plan-completion based
  (`ShowcaseFinale.cs:39-52`: ≥30 min, relaunch share ≥ 0.5, ≥5 recent kills).
  **The brief's premise "the finale only arms when a side's commander finishes
  its research plan" is not what the code does**: in match 1 the trigger fired
  at t=1801 s with the plan at step 62/186 (33 %), and in match 3 at t=1800 s
  with step 67/186 (36 %).
* `ArmResearchFinale` is reachable only from that packet
  (`Server.Showcase.cs:24-37`, `Server.Dispatch.cs:106-108`). There is no
  server-side fallback deadline for a research showcase, so one silent skip
  means the match has no end condition. Evidence — the four triggers and the two
  armed deadlines:

```
worker-1: 2026-11-05T03:43:28 [finale] trigger sides=[0,1] at t=1801s: … kills=123 …
server:   2026-11-05T03:43:29.649589434Z [finale] research showcase epoch=a65f8a18… armedBy=0: bounded finale armed; deadline=3050.05; …
worker-1: 2026-11-05T04:30:42 [finale] trigger sides=[0,1] at t=1800s: … kills=385 …      (no server [finale] row)
worker-1: 2026-11-05T13:32:08 [finale] trigger sides=[0,1] at t=1800s: … kills=511 …
server:   2026-11-05T13:32:11.769447565Z [finale] research showcase epoch=6dc0c5fb… armedBy=1: bounded finale armed; deadline=38563.6; …
worker-1: 2026-11-05T14:22:08 [finale] trigger sides=[0,1] at t=1800s: … kills=263 …      (no server [finale] row)
```

Worker-2 shows the same two silent triggers at 04:30:43 and 14:22:10. Both
matches then had zero station kills (`[finale] Enemy base shattered: none` at
the match-2 draw), zero bomber loadouts ("no bomber hull passes the side +
station tech gate" — initial techs only), and no `[strike]` rows at all.

Classification: **port orchestration gap** (showcase policy: the deadline is a
port-only `ShowcaseFinaleRequest`; native always has a match clock —
`FedSrv/fsmission.cpp:2337-2342`). It is what converts the plan bug into an
unbounded match; it is not itself the root cause.

### 6.3 Mitigation already on master: P930 match cap (showcase policy)

P930 (`b5fa1d6b`, merged after this batch's server was booted) sets
`goals.GameLengthSeconds = showcase.matchCapSeconds` (default 3600) on every
showcase preset (`Server.Lifecycle.cs:1338-1341`) and ends the match through the
native countdown criterion (`Server.Periodic.cs:47-60`;
`Igc/fsmission.cpp:2337-2504`). It bounds the symptom (a stalled match now ends
after 60 min by kills), but it does **not** restore the research economy: with
the plan bug still present, every rotated match is a 60-minute non-research
brawl. The b65 binary predates it — the matchup receipts carry no `cap=` field
and match 2 ran 8 h 58 m.

### 6.4 Not causes (checked)

| Hypothesis | Verdict | Evidence |
|---|---|---|
| He3 exhaustion | **refuted** | §5; regrowth is native and ported, 36 rocks, mining continuous |
| Money | **refuted** | §5.4; $28–35 k/min paydays, zero donations |
| Missing asteroid classes | **consequence**, not cause | `PrepareResearchMap` skipped only because the plan dictionary is empty (§5.1) |
| Home position / side bias | **refuted** | identical homes, opposite outcomes (§4) |
| Civilization balance | **refuted** | same civs appear on both sides across the pair (§4) |
| Bot combat/finale logic | **contributing** | the trigger fired on schedule in both stalled matches; only the request was skipped (§6.2) |
| Server simulation / load | **refuted** | `[tidi] scale=1` throughout; `[stall-minute]` shows no starvation; the ordinary economy and combat run |

---

## 7. Confidence

| Claim | Confidence | Basis |
|---|---|---|
| Plans are missing on exactly the rotated matches, in both sides | **verified** | matchup receipts (`(none)`), zero `[research-plan]`/`[research-map]` rows, no `[research]` rows after the first 20 s, zero `[commander]` rows, zero donations/dev-completes/constructors, plus code reproduction (`rotcheck`) |
| Cause is the `(game, side, civ)` plan cache vs rotated civ ids | **verified** | `Server.Lifecycle.cs:1219-1233` / `:1244-1251` / `:1342`; P929 diff leaves the cache untouched; reproduction reproduces the `(none)` line |
| No deadline because the finale request needs the briefing epoch | **verified** | `BotManager.Finale.cs:831-836`; four triggers, two armed rows, correlation exact |
| Finale trigger is not plan-completion based | **verified** | `ShowcaseFinale.cs:39-52`; match 1 armed at plan step 62/186 |
| Match-2 map = 4 sectors / 80 asteroids / 36 He3 / 8 U-only specials | **verified** | recorded seed reproduced (`mapcheck`), rock 27 name/position matches the log |
| He3 is not exhausted and regenerates | **verified** | native `asteroidigc.h:47-56`, port `Asteroid.cs:36-39`; ore time series and continuous sells |
| The "unoccupied He3" flood is enemy lockout on one shared rock | **verified** (mechanism) / **likely** (that it is worth a separate fix) | `shipIGC.cpp:2185-2205`, `Sector.cs:4933-4952`; 12 396/12 940 transitions on rock 27 from 135 ships of both sides |
| Generated maps lack Si/C special classes | **verified** | `MissionMapGenerator.cs:1186-1194` + reproduction showing `0x20/0x40 = 0`; native `asteroidigc.cpp:179-224` |
| Match 2 ended only because the operator injected `DrawRef0/1` clients | **likely** | peers `DrawRef0`/`DrawRef1` connect at 12:54:12 and again at 12:55:46; `[ballot] #0 OfferDraw side 0` at 12:56:08, `#1 AcceptDraw` accepted → `[gameover] … (draw via AcceptDraw ballot)` at 12:57:07. No bot code issues `#draw` (`grep OfferDraw tools/allegiance-mcp` is empty) |
| Match 4 was still running when the batch was redeployed | **verified** | log ends 17:55:42 mid-combat, no `[gameover]` for match 4 |

---

## 8. What is unfinished / out of scope

* No code was changed (task rule). The fix is specified in
  `P938_SHOWCASE_STALL_FIX_DRAFT.md`; Claude decides the lane.
* The bot-side silence (`no briefing → no request`) is only classified, not
  instrumented; the fix draft proposes a one-line diagnostic as optional.
* Generated-map special classes and the miner `c_ttLeastTargeted` gap are real
  parity gaps found while answering Q3 but are **not** required for this stall;
  they are listed as follow-ups in the fix draft.
* Rig/Live replay of the fix (a fresh b65-style batch: four matches, two
  rotations) is Claude's lane; this task only had read-only evidence.

## Appendix — evidence paths and reproduction commands

* Server log: `<evidence>/b65-server-filtered.log.gz`
* Worker logs: `<evidence>/err.worker-1.log.gz`, `err.worker-2.log.gz`
* Reproduction harnesses (deleted with `.scratch/`; recreate as in the fix
  draft's test section): `dotnet run` on a scratch console that references
  `src/Allegiance.Core` (map census, `SingleRing` seed 1123059318) or
  `src/Allegiance.Server` (three-match rotation boot with
  `showcase.techMode=research`).
* Filtering used for the numbers above: per-line UTC prefix from the server log;
  worker lines bucketed by the preceding `[stall-minute] utc=` anchor.
