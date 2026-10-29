# P877 — bot minelaying tactics: shake a pursuer with mines, and mine the alephs

Author: Pi lane, base `master` at dispatch (`87ff1eef`). Scope: `tools/allegiance-mcp`
and `tests/Allegiance.ClientCore.Tests` only. **Nothing under `src/` changed** — the
server sim, mine detonation and all wire behaviour are untouched, so no AOT-publish
gate applies.

This is bot policy (human tactics), **not** native AI. Native wingmen only drop a
mine at any different-side combat actor within 2,500 u (`Igc/shipIGC.cpp:2651-2665`,
ported in `BotManager.Dispensers.cs`) and never mine their own wake or an aleph.
Every rule below is cited to the port's own file:line; native citations are read-only
reference (`~/Development/Allegiance/src/`).

## Step 1 — the data check (the "5 s mine pack" reading is CORRECT, not a loader bug)

Every artwork core (`PCore001/002/003/005b/007/011/013`, all identical) decodes the
same mine table, and native consumes `DataMineTypeIGC.lifespan` as **seconds**:

- the value lives in the `DataExpendableTypeIGC` base (`igc.h:1947`, used by
  `mineTypeIGC.cpp:15-27` verbatim);
- `mineigc.cpp:113`: `m_timeExpire = m_time0 + dataMineType->lifespan`, and
  `Time + float` treats the float as seconds (`zlib/ztime.h:149-153`:
  `m_dwTime + d * fResolution()` with `fResolution() = 1.0e3f` — ms clock).
- the port's `DataMineTypeIGC` decode (`src/Allegiance.Core/Data/DataMineTypeIGC.cs`,
  `Lifespan` at offset 56) matches byte-for-byte, verified against the full corpus.

**There is no loader/units bug to stop for.** Domingo's screencast of fields of
hundreds of prox mines persisting minutes is explained by the data: prox dispensers
carry 2 charges with a 2 s load time (`LauncherExpendableSize` 2, `LoadTime` 2 s), so
continuous laying by several scouts keeps a field visibly present — each individual
mine still lives 15–21 s. (Community cores may also differ; the artwork corpus is
authoritative here.) The port's 15–21 s reading was correct; **no stop-and-report**.

### The verified mine table (real cores, `ALLEGIANCE_ARTWORK_PATH`)

| Launcher part | Mine type | Lifespan (s) | Radius (u) | Power | Endurance | DmgType | Charges | Price | Carrier tech (launcher bits) |
|---|---|---|---|---|---|---|---|---|---|
| 830 | Caltrop Mine | **600** | 200 | 400 | 2000 | 16 | 10 | 1000 | base tech bits 1+17 (bit 17 = Rixian base techs on PCore001; **no development grants it**) |
| 59 | Prox Mine 1 | 15 | 100 | 400 | 2000 | 16 | 2 | 0 | bit 356 (all civs' base techs) |
| 58 | Prox Mine 2 | 18 | 100 | 500 | 2500 | 16 | 2 | 0 | bits 92+229+356 (devs 127 "Prox Mine 2", 205 "Plasma Generator 2") |
| 57 | Prox Mine 3 | 21 | 125 | 600 | 3000 | 16 | 2 | 0 | bits 27+229+356 (devs 126 "Prox Mine 3", 206 "Plasma Generator 3") |
| 113 | EMP Mine 1 | 18 | 200 | 250 | 2500 | 13 | 2 | 0 | bits 200+231 (devs 246/247 "EMP Mine 1/2") |
| 114 | EMP Mine 2 | 21 | 250 | 300 | 3000 | 13 | 2 | 0 | bits 201+231 (devs 246/247) |
| 13 | Mine Pack 1 | 5 | 75 | 700 | 1400 | 16 | 1 | 0 | bits 213+230+356 (devs 129 "Mine Pack 1", 207 "Hvy Plasma 1") |
| 14 | Mine Pack 2 | 5 | 75 | 900 | 1800 | 16 | 1 | 0 | bits 212+230+356 (devs 128 "Mine Pack 2", 208 "Hvy Plasma 2") |

Load time is 2 s for every mine type. **Lifespan threshold decision:** the data
separates cleanly — nothing lives between 21 s and 600 s — so the pre-existing
60 s floor (`Showcase.PreLaidMineMinLifespan`) selects exactly the caltrop-class
fields; any threshold in that gap is equivalent. Kept at 60 s and documented.

**Research finding:** no development on PCore001 unlocks the Caltrop's launcher bits
(bit 17 exists only in the Rixian civ's base techs), so "let the strategy research it
when it fits" has nothing to research on this core — aleph-field duty is data-driven
(`TechLoadoutSelector.PickAlephMineLauncher`): it lights up for any side whose tech
set can buy a field-grade mine (the Rixians) and stays dormant otherwise. The
showcase's civ3/civ25 `plan_a` files already develop the `.Caltrop` *layer drone*
(commander-built mine layers) — a separate, unchanged mechanism.

## Step 2 — escape mining (a pursued bot shakes its pursuer)

Pure decision (`Showcase.cs`, P877 section):

- `PursuitOpeningSpeed` — the rate of change of |pursuer − bot|; closing is negative.
- `IsPursuitBehind` — the pursuer is in the bot's REAR hemisphere (dot with forward
  < 0). A dispenser drops at the ship's position, so a mine laid now stays on the
  pursuer's line exactly when the pursuer is behind a forward-moving bot.
- `ShouldEscapeMine` — drop only when ALL of: pursuer behind, within the mine-useful
  2,500 u (`PursuitMineRange`), closing or holding (opening speed <
  `PursuitOpeningTolerance` = 5 m/s), and the bot is in danger: fleeing
  (`CombatEscapeUntil`), hull ≤ 0.5, shield ≤ 0.35, or locally outmatched (more enemy
  than friendly combat actors in sector, the bot itself counting).

Wiring (`BotManager.Dispensers.cs`): the native undirected "any enemy within 2500 u"
trigger is **no longer a bot trigger** — an enemy merely AHEAD gets no mines
(`Showcase.EnemyWithinMineRange` stays as pure NPC-parity code). Load time and the
`covered` spacing check are respected unchanged. Drop log carries
`escape=true pursuer=<id>`.

## Step 3 — aleph fields (strategy-level duty bots)

`tools/allegiance-mcp/BotManager.AlephField.cs`:

- **Duty assignment** (`BotManager.cs` spawn branch): one combat bot in four
  (per-side rotating slot counter, same pattern as the P796 defensive mine layers),
  fighters and scouts only. The duty is dormant unless the bot actually mounted a
  field-grade mine (`Showcase.IsFieldGradeMine`).
- **Loadout** (`BotManager.TechLoadouts.cs` → `TechLoadoutSelector`): duty bots rank
  field-grade mine launchers first, so a side that can buy one mounts it; the
  purchase reason says `aleph-field duty (one in four combat slots)`. Reseat path
  keeps the duty.
- **Navigation** (`TryDriveAlephField`, hooked after the research-siege drive): the
  duty bot resolves the enemy's arrival aleph in its rendezvous sector via the
  existing P591d machinery (`FindEnemyArrivalWarp` over the probe's real map graph),
  else a home-station aleph under threat (enemy combat ship in the home sector or any
  adjacent one). It flies to `Showcase.PlaceInFrontOfArrivalAleph` (the P557 default
  geometry, 400 u standoff toward the sector centre — now with its first caller),
  yields to live engagements outside drop range, and stations at the point while the
  dispenser law lays the field.
- **Drop gate** (`BotManager.Dispensers.cs`): `arrivalField` = on duty + assigned
  sector + near the assigned warp + `Showcase.PreLayMineAtArrivalAleph` (lifespan ≥
  60 s). Log carries `alephField=true warp=<id>`.

## Step 4 — coverage counters (`[sc4]` / `WeaponCoverage`)

Mine blast damage raises **no wire event** (only the projectile path raises
`VICTIM_DAMAGED`, `Sector.cs:2401`), so hits are observed the way the bots actually
see them: the launcher's own cross-side `HEAVY_SHIPS_UPDATE` stream carries the
victims' hull fractions. A drop registers its likely victims (the pursuer; every
enemy combat ship in the field sector) as watched targets for the mine's lifespan; a
watched ship's streamed hull+shield fraction decreasing while the mine is live counts
the hit. Kills: a watched escape-mine victim that dies inside the window
(`KILL_SHIP` + death credit) counts once.

| Counter | Meaning | Incremented by |
|---|---|---|
| `escapeMineDrops` | mines laid while pursued | real `SendFireExpendable` path |
| `pursuerHits` | pursuers damaged by escape mines | watched-victim fraction decrease |
| `pursuerKills` | pursuers killed by escape mines | `KILL_SHIP` inside the window |
| `alephFieldDrops` | arrival-aleph field mines laid | real drop path, duty bots |
| `shipsDamagedOnAlephExit` | ships damaged by aleph fields | watched-victim fraction decrease |

Reported in the end-of-match table (`P877Lines()`) and exposed as test seams. Added
to the showcase coverage map: `docs/showcase/SKILLS.csv` rows `escape-mines` and
`aleph-fields`; the policy decisions are recorded in `docs/parity/p819-bot-policy.csv`.

## Step 5 — tests (all in `tests/Allegiance.ClientCore.Tests`)

`P877EscapeMineTests.cs` (11 tests, 3× alone green):

- Pure: `PursuitOpeningSpeed…`, `IsPursuitBehind…`, `ShouldEscapeMine…` (pursued vs
  ahead vs out-of-range vs opening vs healthy; each danger signal alone),
  `PreLayMineAtArrivalAleph_lifespan_thresholds`.
- Real-core data: `Mine_data_table_and_field_grade_launcher_resolution` (the table
  above, exactly one field-grade launcher, 60 s floor separation, field-civ pickup).
- Dispenser law: escape drops behind but not ahead/opening/out-of-range
  (`Escape_mine_drops_behind…`), the danger gate (`Healthy_bot_with_parity…`),
  wingman-flag and coverage gates, and the duty bot's arrival-field drop
  (`P877AlephFieldDropTests`: caltrop lays, prox stays dormant, off-duty/other-sector
  stays dormant). Placement geometry is exercised by the wake drops (the mine lands
  at the ship's position on the pursuer's line).
- Counters: `Coverage_counters_attribute_escape_and_aleph_mine_damage` (drop →
  watched-victim hull decrease → hit; kill; aleph bucket; P877Lines output).
- Live loopback: `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` — a
  real in-process server, a chased scout (hull chewed server-side to 0.3, one
  pursuer client chasing, one unflown wingman) drops its wake mine through the real
  dispenser law, the pursuer flies through it and the mine blast damages it
  (hull 1.0 → 0.817 observed), with the counters lighting from real wire traffic:
  `laid-while-pursued=1 pursuerHits=1`.

## Verification (light gate)

| Gate | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 93 tool projects built, 0/0** |
| New tests 3× alone | **11/11 × 3 passes** (P877 filter) |
| ONE `tools/run-tests.sh --tier fast` | my lane fully green (all 1973 ClientCore.Tests fast tests pass, incl. the 11 P877 tests); the only failures are the **already-ledgered** `NpcWingmanDefendLiveTests.Defender_disengages…` (pre-existing base failure, P872 section) and `AddPartTests.Part_pickup_decline…` / once `FuelPoolAndAfterburnerTests…` load flakes — each passes in isolation (3/3 for FuelPool), ledgered in `docs/TEST_FLAKES.md` |
| Showcase never-parks 5× | **5/5 PASS** (3 m 39 s – 5 m 57 s per run, `Local_showcase_never_parks_with_target_for_five_seconds`) |
| Skirmish-sized local game, new counters non-zero | **the live loopback proof above**: escape drops = 1, pursuer hits = 1, pursuer damage observed on the real sim |
| `scripts/parity-scorecard.py` / `tools/check-glossary-links.py` | pass |

## Unfinished / risks (honest list)

- **Claude's 200v200 rig run is the acceptance test.** The local proofs cover the
  decision rules, the drop laws and one live chase; the fleet-level effect (how many
  fields a showcase match actually lays, whether escape mines visibly save bots) is
  what the rig measures. State plainly: **this lane's local evidence is necessary,
  not sufficient — the rig decides.**
- **Aleph fields are Rixian-only on PCore001.** The 600 s Caltrop's launcher bit 17
  exists in no development, so field duty lights up only for sides whose base techs
  carry it. If Domingo wants field-laying Ga'Taraan/Iron Coalition bots, that is a
  core-data change (or a TEST-unlock decision), not bot policy.
- **No live aleph-field drop proof.** The arrival-field drop branch is proven at
  dispenser level with the real core data and the counters at decision level; a live
  loopback of the navigation + field laying (civ 201 sides, real warp graph) was not
  built — the rig run covers it.
- **Watched-victim hit attribution is per drop-victim pair** (one counted hit per
  mine per victim); a mine that damages several ships over its life counts the extra
  victims only via new drops or kills. This matches "pursuer hits" semantics but is
  not a raw detonation count.
- **`Mine Pack` 5 s lifespan is unchanged and still useless for everything but
  point-blank escape** — by design: the danger gate + 2,500 u window is exactly the
  case the 5 s pack covers.
- The loadout preference only re-ranks the Dispenser slot for duty bots; a duty bot
  on a hull whose dispenser slot cannot fit the caltrop (none observed on PCore001
  scout/fighter hulls) stays dormant rather than failing the purchase.

## Native-intent footer

Native evidence: the wingman mine rule (`Igc/shipIGC.cpp:2651-2665`) is an NPC AI
behaviour — undirected, range-only. Everything P877 adds (wake drops against a
pursuer, aleph pre-laying, duty assignment, the 60 s field-grade threshold) is
**human tactic ported as bot policy**, not a native AI port: no native source
implements it, and the brief explicitly scopes it to skirmish/showcase. The native
2,500 u wingman trigger remains available for NPC parity
(`Showcase.EnemyWithinMineRange`) and server behaviour is untouched. The one place
native data settled a question was the lifespan units check (`mineigc.cpp:113` +
`zlib/ztime.h:149-153`): the port was right, the screencast's persistent fields are
continuous laying, not a bug.
