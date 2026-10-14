# P539b — MCP bot combat: native default loadout + per-side tech levels (bots are shield-invincible; possible CanMountPart game bug), fix the hit metric (it isn't attributed to the shooter), then real, predictable kills

## Why this task exists

P539 (`a2f79ddc` + report `091549a4`) reported a bot hit rate of
**29.4% (20 Hz) / 38.1% (10 Hz)** with ~4,400 "ship hits" per 240 s run, yet
**0 kills**, even in an **8v2 over 600 s with 2,965 "ship hits"**. It
explained this as "a shield-regen equilibrium: the dogfight gun's sustained
dps does not out-strip Adv Scout shield regen". **That claim is false.**
Claude computed it from PCore001:

| | Value |
|---|---|
| Adv Scout (hull 13) | 225 hull HP + `Sm Shield 1` (part 21) **150** strength, **RateRegen 2/s**, shield DefenseType 8, hull DefenseType 1 |
| `PE Vul Can 2` (part 197, P539's dogfight pick, verified to be the **best** mountable gun by effective damage vs this hull) | Power **6**, dmgType 4, DtimeBurst **0.075 s ⇒ 13.3 shots/s**, ×1.0 vs shield and ×1.0 vs hull (`DamageConstants`) |
| 1 shooter at the claimed 29% | ≈ **23 dps**, so a 375-HP target dies in **~16 s** |

2,965 real hits × 6 ≈ 17,800 damage on two 375-HP ships with 2/s regen
would have killed them dozens of times. **So the counted "hits" aren't
real damaging hits by the bots.**

**Root cause of the bad metric (found by Claude, verify it):**
`tools/allegiance-mcp/BotManager.cs:1480-1502` `SampleCombatStats` adds
`bot.ShipHits++` for **every** `PROJECTILE_HULL_HIT` in the bot's world
(`cs.World.TakeNewHullHits()`) whose target is a different-side ship,
**without checking who fired it**. Every bot in a sector receives every hit
event, so with N teammates each team hit is counted ~N times. "Shots" are
counted at the **fire decision** in `EngageShipAt`, not from real fired
projectiles. Hit rate is therefore meaningless as reported.

**Also unresolved from P539:** 115 bot-bot collisions in 240 s at 20 Hz
(only halved from 235). The stand-off isn't really holding.

**Ground truth exists:** in the real game on 221 (10 Hz run), bots did
kill each other (`[combat] ship 8103 killed by ship 8100`,
`ship 8101 killed by ship 8106`), so bot damage works end to end.

## ⚠️ New evidence (2026-10-04, live on 221 at `c129fb1f` with P539 bots): bots are invincible

Domingo: "bots still feel invincible, I rammed and fired constantly at them,
zero damage". The server log for his ship 8109: **618 hits on bots, every
one `absorbedByShield=True`** (397 on 8106, 153 on 8108, 68 on 8102). P539's
`Catalog.DogfightParts` (`tools/allegiance-mcp/Catalog.cs:~273`) reuses
P483's "lowest PartId that mounts" rule for the shield, which picks
**`Lrg Shield 2` (part 45): MaxStrength 3,750, RateRegen 35/s, DefenseType
14**. With `PE Gat Gun 3` (×0.5 vs that defense type) a player does 25 dps
at 100% accuracy against 35/s regen, so **the shield can never drop.**

**The native default loadout** (`DataHullTypeIGC.PreferredPartsTypes`,
`src/Allegiance.Core/Data/DataHullTypeIGC.cs:50`; native
`preferredPartsTypes[c_cMaxPreferredPartTypes]`, `Igc/igc.h:1801`,
`GetPreferredPartTypes`, `:3844`) for the **Adv Scout (hull 13)** is
`PW Gat Gun 1` (33), **`Sm Shield 1` (21, 150 strength, 2/s regen)**,
plus launchers/expendables 80, 81, 153.

**Possible GAME bug, not just harness:** the port's
`hull.CanMountPart(Sm Shield 1)` returns **false** for the Adv Scout,
although `Sm Shield 1` is in that hull's **own native preferred loadout**.
That's why the original bots' requested shield was silently dropped (they
flew unshielded, which is why Domingo could kill them in the first 20 Hz
game). If the port's mount check (`CanMountPart`, the P334/P483 hardpoint
policy, `Server.ShipCommands.cs:~2491`, native `shipIGC.h:1030`) is wrong,
**players can't fit their hull's default shield either** (the P519 shop and
the ship-launch loadout). Verify against native's `CanMount` line by line,
using the real part masks for hull 13 and part 21.

## Scope (`tools/allegiance-mcp`, plus server-log-based measurement)

0. **Bot loadout = the hull's native preferred loadout** (`PreferredPartsTypes`,
   including launchers where the MCP launcher path supports them), not
   "highest power" or "lowest PartId that mounts". Keep P539's dogfight gun
   as an explicit opt-in only if it's in the preferred list or the caller
   asks for it. **And investigate the `CanMountPart` × `Sm Shield 1` × Adv
   Scout contradiction.** If the port's mount rule disagrees with native, fix
   it in the game code (`Allegiance.Core`/`Server`), add a failing-first
   test (hull 13 can mount part 21, plus a representative sample of every
   hull × its preferred parts), and note the player-facing impact (shop,
   launch loadout) in the report. If native truly can't mount it either,
   prove that with the masks and say how native's preferred list handles
   it.

0b. **Per-side tech level for showcases** (Domingo: "we need the ability to
   spawn them with different levels of tech, so we will know for sure who
   will win during the showcase").
   - Add an optional **`tech_level`** per side to `spawn_scenario` (e.g.
     `tech_a`, `tech_b`; default 0) and per bot to `spawn_bot`, plus the
     scenario presets if cheap.
   - **Definition, native-derived:** level 0 = the hull's native
     `PreferredPartsTypes` loadout (scope 0). Level N = each preferred part
     advanced **N steps along its native upgrade chain**,
     `DataPartTypeIGC.SuccessorPartId` (`src/Allegiance.Core/Data/DataPartTypeIGC.cs:60`;
     native `successorPartID`, `Igc/igc.h:1827`), stopping at the end of a
     chain or at the last successor that still **mounts** on the hull. Report
     the resolved loadout per level in the tool output (part names), so a
     showcase operator sees exactly what each side flies.
   - If the server gates purchases on side tech (`ttbmRequired`) outside the
     test-unlock mode, say how the bots obtain higher-tier parts (the
     existing MCP grant path) and make sure it works on 221's
     `testUnlock.allTech` config.
   - Optionally (only if trivial and native-supported): a hull tier via the
     hull successor chain. Otherwise list it as a follow-up.
   - **The bar:** predictable outcomes. Run **3 repeats** each of
     `tech_a=2 vs tech_b=0` and `tech_a=0 vs tech_b=2` (5v5, 240 s, local
     server). The higher-tech side wins every time on kills (and the
     reversal flips it), and an **equal-tech** run is roughly even. Report
     per-run kills and deaths per side.
1. **Attribute everything to the shooter, from authoritative data.**
   - **Shots:** count the bot's real `[projectile] ship <id> fired` events,
     or its own PROJ_INST echo carrying its ship as launcher. Not fire
     decisions.
   - **Hits:** count only hull/shield hits whose **shooter/launcher is this
     bot**. Use the launcher id on the hit message if the wire carries it;
     otherwise correlate on the server side, e.g. from the server log's
     `[projectile-hit] ship <shooter> hit target <id>` lines. Split by target
     side: **enemy / friendly / none (`-1`)**.
   - **Damage:** track each enemy target's shield and hull fraction over time
     from ship updates, so "hits" can be checked against real damage taken.
2. **Measure with the corrected metric** in the same local-server 5v5
   (`spawn_scenario {size:5, target_sector:<shared>}`, 240 s, 20 Hz). Report
   the real per-side shots, enemy/friendly/none hits, enemy damage dealt,
   kills and collisions. **Say plainly how wrong P539's numbers were.**
3. **Then make bots actually kill,** based on what the corrected numbers
   show. Likely candidates, to be confirmed by measurement: friendly-fire
   blocking or lining up behind teammates (add a "don't fire if a friendly
   is in the line of fire / cone" gate), aim quality, too-wide stand-off
   range vs the gun's reach (`Speed × Lifespan` = 840 for Vul Can 2), and
   continued collisions. Keep native fidelity: cite native drone combat
   (`Igc/shipIGC.cpp` wingman combat) for any gate or range rule.
4. **Correct P539's report** (`docs/reports/P539_MCP_COMBAT_BOTS.md`) with a
   short dated note pointing to P539b, and strike the "shield-regen
   equilibrium" claim and the inflated hit rates. Don't silently rewrite.

## Explicitly out of scope

- Server combat, damage and collision code (bot damage works on 221). **Exception:** the `CanMountPart` fix in scope item 0, if native disagrees.
- Client projectile visibility (P542).
- ~~Weapon choice (Vul Can 2 is verified best vs Adv Scout).~~ Superseded: bots use the hull's native preferred loadout (scope 0).

## Verification

- **Failing-first unit tests** (also: `tech_level` N resolves each preferred part N successor steps and stops at an unmountable successor; level 0 equals `PreferredPartsTypes`):
  - a hit fired by a teammate isn't counted for this bot
  - a hit on a friendly is counted as friendly, not enemy
  - shots come from real fire events, not decisions
  - (if added) the friendly-in-line-of-fire gate holds fire when a
    teammate is in the cone
- **Live proof** (local server, 5v5, 240 s, 20 Hz): real **bot-on-bot
  kills > 0** (target: several per side), with corrected shots, hits and
  hit rate, enemy damage dealt, friendly-hit count, and collisions
  (target: well under P539's 115). Cross-check the kill count against the
  server log's `[combat] … killed by …` lines. Quote them.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and live proof are done, BEFORE the
  3× suite run**, then amend with the suite numbers. Confirm with
  `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P539b_MCP_BOT_REAL_HITS.md`: the metric bug with
   file:line, corrected before/after numbers, the kill fix, native citations
   and tests, plus the correction note in P539's report.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
