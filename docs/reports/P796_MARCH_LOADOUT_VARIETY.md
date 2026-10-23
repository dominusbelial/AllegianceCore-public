# P796 — loadout variety in normal (march) mode: every legal weapon gets used, with a coverage table

Relationship: replaces the arena-based P793 brief (arena removed in P795); Domingo 2026-10-22: "we never tried with
all the models/weapons", then "focus on scaling the full normal mode". Base: `master@37ea36f2` (P790), branch `pi/p796`.
Production edits are confined to `tools/allegiance-mcp/**` (+ tests, this report, coverage rows). No Core/server/client/wire
change.

## Design

### 1. Variety within legality (`TechLoadoutSelector.SelectVaried`)

The existing legality is untouched: the varied selector reuses the exact `Create(...)` context of the best selector —
research unlocks per the side's real techs **and the dock's local mask** (`stationIGC.h:383-433`), hull/part buy gates
(`hullTypeIGC.h:163-213`, `TechTreeBitMask.RequiredTechsBuyable`), successor obsolescence, mount masks (`CanMountPart`),
the shared energy/ammo pool, and the wallet. Nothing that `best` could not legally buy becomes buyable; nothing `best`
could buy is excluded from the rotation pool.

Policy — **least-recently-used across the side, seeded weighted inside the least-used group** (the brief offers
"LRU across the side, or weights by value with a coverage bonus"; this is both):

- **Hull pick**: among the dock-legal, nonobsolete, player-buyable hulls of the bot's role, the least-equipped group
  wins; inside the group the pick is weighted 1:4 by `HullScore` (the best selector's own role score), so better hulls
  are favoured but no legal hull starves.
- **Primary gun**: rotates the least-equipped fitting weapon across ALL the chosen hull's fixed mounts (weighted 1:4 by
  `WeaponScore` DPS-vs-fighter-armour, the best selector's own score). Remaining mounts keep the best-path DPS pick from
  the leftover energy/ammo/budget pool.
- **Equipment** (shield / afterburner / magazine / dispenser / chaff / cloak): same rotation per slot with the best
  path's slot order (scouts still reserve budget for stealth first) and `SupportScore` weights.
- **Determinism**: `VarietyState(side, seed, ordinal, hullUse, partUse)` → `Random(FNV-1a(seed, ordinal, civ, dock,
  role))`. Same seed + same usage snapshot = same selection (proved by test). Purely data-driven: no civ names, ids or
  hull names anywhere in the policy.

**Choice of default — `varied` for `preset:"showcase"`** (overridable with `loadout_mode:"best"`, explicit `varied`
 honoured everywhere): showcase exists to exercise the real fleet in normal mode, and the run that motivated P796
 flew ~15 of 295 buyable hull entries precisely because `best` is a per-role argmax. `best` stays the default for
 every other mode (homogeneous, mixed-fleet, `spawn_bot`, run_hull_matrix, scenarios) — those measure named
 configurations, where a fixed argmax is the point.

**Role rules kept (mandated exceptions route to the unchanged best selector):**

- Bombers keep the anti-base strike loadout (`SelectBomber` station-damage ranking) — finale/strike loadouts unchanged.
- Support pilots keep the nanite selector (`SelectSupport`, negative-power lead gun).
- Medevac scouts keep the rescue-probe ranking (`PickRescueProbeLauncher`, P779b-1).
- Defensive mine layers (one-in-three fighter duty) rotate through `SelectVaried(..., preferMines: true)` but fall
  back to the best layer if the rotated pick would carry no mine dispenser — a defensive layer without mines fails
  its duty.
- Role-fallback purchases (locked role temporarily flying fighter/scout) keep the best selector.

Plumbing: `spawn_scenario` gains `loadout_mode` (`best`|`varied`) and `loadout_seed` (default 796, the task id);
`BotManager.ConfigureLoadoutVariety` sets the run mode; per-side usage lives in `LoadoutVariety` (one process-wide
store, the existing one-BotManager-per-process harness invariant), and each bot carries its own seed
(`seedBase + botId × 1_000_003`) so purchase sequences are reproducible per seed. Every varied selection is marked
`varied seed=… ord=… hullUse=…;` in its `[loadout]` reason line.

### 2. Coverage telemetry (`WeaponCoverage`)

Built **only from events the bots already receive** — no new wire, no per-tick logging:

| Row | Source |
|---|---|
| gun fire events | rising AllWeapons edge of the bot fire law (native `shipIGC.cpp:2600`); every mounted weapon part fires together |
| missile launches | the real `SendFireMissile()` (`MaybeFireMissile`, shipIGC.cpp:2584-2596 gate) |
| mine / probe / chaff drops | the real `SendFireExpendable()` calls (Dispensers SC4 laws, medevac P779b-1) |
| hits | `PROJECTILE_HULL_HIT(213)` broadcasts, shooter-attributed via the P539b `SourceId` to the shooter's most recent fire (≤5 s window), deduped across the observers sharing the victim's sector (quantised position + 1 s bucket hash) |
| damage dealt | `VICTIM_DAMAGED(217)` — the victim's own unicast; every showcase actor is a harness bot, so the union over bots covers the whole fight |
| kills | `KILL_SHIP(105)` deaths (bDeathCredit) via the world's `EventLog`, hull-attributed from the observed ship table |
| hull bought / launched | the harness's own real `BUY_LOADOUT(fLaunch)` acks (the whole showcase fleet is harness-controlled) |
| expendable types (missile/mine/probe/chaff) | launch records above plus first-sighting of `FireMissile`/probe/mine object ids and `CreateChaff` type ids in the bot worlds |

The manager loop sweeps each combat bot's world ~1 Hz (bounded drains, ~nothing per tick) and prints:
- a bounded **`[gallery]` once-per-minute summary** (two lines, whatever the fleet size);
- an **end-of-match table** when a bot world's mission stage leaves `Started` (and again at manager dispose), including
  every equipped item that never fired **with the reason when known** (passive equipment; missile gate never satisfied;
  mine 2500 u proximity law never qualified; chaff law never qualified; fire gate never satisfied);
- the same data on demand via the new MCP tool **`get_weapon_coverage`**.

Known attribution limits (documented, not hidden): damage/hit attribution uses each shooter's most recent fire within
5 s, so a hit from a weapon whose trigger lapsed just before impact can be credited to the shooter's other gun; chaff
has no object id on the wire and is deduped by position bucket; non-harness actors (server drones) contribute
expendable-type rows only.

### 3. Civ pair as a run parameter

A side's civ comes from the server config's `Games[0].CivIds` (`Server.Lifecycle.cs:1121` — `civ = game.CivIds[side]`;
the loopback tests set it the same way). PCore001's 10 civs: 1 Dreghklar, 2 Technoflux, 3 Ga'Taraan, 4 Phoenix,
5 Omicron, 18 Gigacorp, 25 Iron Coalition, 35 Bios, 101 Belters, 201 Rixian. Five matchups cover all ten
(no civ names in code — the pairs are data):

| Matchup | CivIds | Command (on the rig) |
|---|---|---|
| IC vs GT (the P730 baseline pair) | `[25, 3]` | set `Games[0].CivIds=[25,3]`; `mac-bots-spawn.sh 13 march` |
| Dreghklar vs Rixian | `[1, 201]` | set `Games[0].CivIds=[1,201]`; `mac-bots-spawn.sh 13 march` |
| Bios vs Belters | `[35, 101]` | set `Games[0].CivIds=[35,101]`; `mac-bots-spawn.sh 13 march` |
| Technoflux vs Phoenix | `[2, 4]` | set `Games[0].CivIds=[2,4]`; `mac-bots-spawn.sh 13 march` |
| Omicron vs Gigacorp | `[5, 18]` | set `Games[0].CivIds=[5,18]`; `mac-bots-spawn.sh 13 march` |

Each run: watch `[gallery] coverage:` minute lines, then `get_weapon_coverage` (or the end-of-match table) for the
per-part/per-hull table. A 200v200 march on 221 (`size_scale` 13 → `count_a/b` 195-200) exercises the same
per-role rotation across both sides' real tech trees; different civs have different legal part sets, so the
rotation pool is per-side by construction.

## Per-part coverage table from a real run

Loopback showcase (the existing live-test harness: in-process server, `preset:"showcase" count_a=15 count_b=15
target_sector:"Alderan" staging:true finale off, varied loadouts`), 4.4 min of match time — full log
`docs/reports/P796_assets/loopback-coverage-265s.txt` (raw run in `~/allegiance-evidence/P796/loopback-coverage-run.log`):

```
P796 coverage table (t=265s): parts=20 expendables=9 hulls=10
part | kind | equipped | fired | hits | damage
 21 | Sm Shield 1 | shield | 46 | 0 | 0 | 0
 23 | Booster 1 | afterburner | 14 | 0 | 0 | 0
 26 | Lt Booster 1 | afterburner | 4 | 0 | 0 | 0
 33 | PW Gat Gun 1 | gun | 48 | 128 | 48 | 3876
 41 | Hvy Cloak 1 | cloak | 3 | 0 | 0 | 0
 50 | EW Utl Can 1 | gun | 6 | 2 | 0 | 0
 59 | Prox Mine 1 | mine-launcher | 19 | 19 | 0 | 0
 80 | Counter 1 | chaff-launcher | 26 | 28 | 22 | 2349
 81 | EWS Probe 1 | probe-launcher | 4 | 4 | 0 | 0
 85 | Counter 2 | chaff-launcher | 22 | 2 | 2 | 397
 91 | ER Nanite 1 | gun | 4 | 0 | 0 | 0
 92 | EW Disruptor 1 | gun | 7 | 40 | 51 | 1218
 104 | MRM Quickfire 2 | missile-launcher | 2 | 2 | 1 | 0
 120 | PW Mini-Gun 1 | gun | 12 | 30 | 18 | 2244
 125 | Afterburner 1 | afterburner | 3 | 0 | 0 | 0
 150 | SRM Dumbfire 1 | missile-launcher | 30 | 50 | 35 | 5651
 153 | MRM Seeker 1 | missile-launcher | 4 | 12 | 17 | 1069
 154 | MRM Seeker 2 | missile-launcher | 4 | 4 | 10 | 330
 155 | LRM Hunter 1 | missile-launcher | 1 | 1 | 0 | 0
 157 | SRM Anti-Base 1 | missile-launcher | 1 | 0 | 0 | 0
expendable-type | kind | fired | hits | damage
 11 | mine | 38 | 0 | 0
 21 | chaff | 56 | 22 | 2349
 22 | probe (utl23) | 8 | 0 | 0
 27 | chaff | 3 | 2 | 397
 32 | missile (mis05) | 100 | 35 | 5651
 35 | missile (mis06) | 21 | 17 | 1069
 36 | missile (mis06) | 6 | 10 | 330
 37 | missile (mis07) | 1 | 0 | 0
 59 | missile (mis08) | 4 | 1 | 0
hull | bought | launched | killed
 114 Scout 8/8/1 · 121 Fighter 14/14/2 · 132 Adv Stl Fighter 1/1/0 · 223 Mine Layer Drone 0/0/1
 410 Scout 20/20/3 · 414 Enh Fighter 1/1/1 · 415 Lt Interceptor 6/6/1 · 420 Stealth Bomber 1/1/0
 444 Lifepod 0/0/2 · 486 Adv Stl Fighter 1/1/0
```

**Gate: 87 % of equipped FIRE-CAPABLE parts fired** (13/15 — guns + launchers; the four shield/afterburner/cloak rows
never fire by nature). A second run (`loopback-coverage-92s.txt`, 1.5 min) reached 80 % earlier in the match.

**Never fired, with reasons (the end table's list):**

| Part | Kind | Reason |
|---|---|---|
| 21 Sm Shield 1 / 23 Booster 1 / 26 Lt Booster 1 / 125 Afterburner 1 / 41 Hvy Cloak 1 | passive | passive equipment — never fires |
| 91 ER Nanite 1 | gun | fire gate never satisfied — the support pilots' repair gun fires only at friendly targets (negative-power lead gun, shipIGC.cpp:2608); no friendly hull damage occurred in window |
| 157 SRM Anti-Base 1 | missile-launcher | missile gate (lock ≥ 1.0 + target evasion below max, shipIGC.cpp:2584-2596) never satisfied — the bomber's anti-base loadout never got a full lock in this window |

Both are honest engine-side reasons, not telemetry gaps: the nanite gun's role rule and the anti-base missile's
lock law are the unchanged P778/P791 behaviours.

**No engine/server gap surfaced**: every equipped item that did not fire is explained by its own (unchanged, native-
cited) firing law or by being passive equipment — nothing needs a separate server fix to become fireable. Weapons
that a given hull cannot legally mount are outside the rotation pool by the existing legality, not silently skipped.

## Tests

All in `tests/Allegiance.ClientCore.Tests/`, real `PCore001.igc`:

- **`P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases`** (civs 25/3/35):
  over 140 simulated purchases per (role × dock), `varied` equips **every** dock-legal hull of the role and **every**
  legal part of the side's full techs that fits the role at least once; never an illegal or unaffordable mount
  (`TechLoadoutSelector.Legal` + `Price <= budget` + energy/ammo fit asserted per purchase). Roles
  Fighter/Interceptor/Scout/Heavy; bombers/support/medevac are deliberately excluded (best selector, mandated).
- `Varied_is_deterministic_per_seed_and_seed_sensitive` — same (state, seed) → identical selection; seeds 796 vs 42
  differ within 40 ordinals.
- `Best_selector_is_untouched_by_varied_purchases` — `Select` returns bit-identical results before/after a block of
  varied purchases.
- `Varied_bombers_and_support_keep_the_best_role_loadouts` — bomber vs station target: `SelectVaried` == `Select`.
- `Coverage_tracker_rows_report_equipped_fired_hits_damage_kills_and_never_fired_reasons` — the tracker's
  record/report rows on synthetic events (equipped/fired/hits/damage/hull rows, never-fired reasons per kind).
- **`P796CoverageLiveTests.Loopback_showcase_with_varied_loadouts_builds_weapon_coverage`** (`[Trait("Tier","Live")]`,
  LiveNetwork collection) — the real loopback showcase above: purchases, triggers, hits, damage, kills and expendable
  launches all observed via the ~1 Hz sweeps; asserts the ≥80 % fire-capable share; prints the full table; exercises
  the `get_weapon_coverage` tool path.
- Affected existing test updated: `P658SupervisorTests.OptionsDefaultAndInternalWorkerAreDistinct` — tool count
  11→12 (supervisor 14→15) for the new `get_weapon_coverage`.

**Tests that fail on master:** every P796 test is new; on master the test project does not compile —
`TechLoadoutSelector.SelectVaried`/`VarietyState`, `WeaponCoverage`, and `LoadoutVariety` do not exist there, and the
showcase default `loadout_mode=varied` does not exist. That compile failure is the master-failure evidence; no
pre-existing behavioural test changed meaning (`P658`'s tool-count assert is the one literal update).

## Verification (light gate) — all green

- Whole-solution build: 0 errors, 0 warnings. `tools/build-tools.sh`: all **70** tool projects built.
- `P796VariedLoadoutTests` 3× alone: 8/8 each (16 s, 16 s, 16 s; worst single test 5.5 s — inside the Fast 10 s budget).
- `P796CoverageLiveTests` 3× alone: passed (1:57 / 1:29 / 2:21).
- `P704TechLoadoutTests` (affected selector) 3× alone: 8/8 each.
- `P730ShowcaseLiveTests.Two_local_showcase_matches…` (showcase now defaults to varied) 3× alone: passed.
- `P658SupervisorTests` (tool count) 3× alone: 28/28 each.
- Fast-tier suite `tools/run-tests.sh --tier fast`: **PASS, 10 029 tests** (first run caught a real failure — the P658
  tool-count assert — plus 3 over-budget tests, both fixed; see `TestResults/tiers-20261023T050658…/summary.json`).
- **Showcase never-parks 5×** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`, now
  running the varied default): 5/5 passed, 1:50-1:57 each — no regression vs the base rate; no flakes.
- No `src/Allegiance.Server`/`src/Allegiance.Core` change → no AOT publish required by the gate.

Raw logs: `~/allegiance-evidence/P796/` (loopback runs). Small summaries committed under `docs/reports/P796_assets/`.

## Rig recipe for Claude (200v200 march on 221)

1. Pick a civ pair from the table above (5 pairs cover all 10 civs); set `Games[0].CivIds=[X,Y]` in the server config.
2. `mac-bots-spawn.sh 13 march` (200v200 march).
3. Collect the `[gallery] coverage:` minute lines from the bot host log and the end-of-match table (printed when the
   match stage leaves Started, or on manager stop).
4. `get_weapon_coverage` any time for the live JSON (per part / expendable type / hull rows + never-fired reasons).
5. Expected: ≥80 % of equipped fire-capable parts fired over a full match; every unfired item carries a reason;
   the rotation spreads purchases across every dock-legal hull of each role.
