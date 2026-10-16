# P539 — allegiance-mcp combat bots: lead-aimed fire in a cone, stand-off instead of ramming, a real dogfight loadout, no cloak pulsing, lifepods fly home

## Why this task exists

**Domingo's live 5v5 on 221 (2026-10-04, `spawn_scenario {size:5,
target_sector:"Alderan"}`, 10 Adv Scout bots + Domingo):** "I could see the
bots maneuvering and trying to dogfight, it looks like they have no
weapons/ammo? I was the one that destroyed the bots … they were also ramming
each other."

**Measured from the 221 server log over ~8 minutes:** the bots **do fire**
(528 `[projectile]` events, 21 `[autoreload]`), but **all 10 bots combined
scored 4 hits on ships** (plus 12 `target -1`, i.e. no ship). Domingo's one
ship scored **194 ship hits** (110 on 8123, 81 on 8117, 3 on 8119) and got
the decisive kills. Every bot updated at a clean 20 Hz (`get_perf_stats`
interval mean 0.0503 s, p95 0.0503 s), so this is **behaviour, not
transport or rendering**.

**Root causes in `tools/allegiance-mcp` (traced by Claude):**
1. **No lead / intercept aiming.** `EngageShipAt` (`BotManager.cs:~1381-1405`)
   calls `CombatFlyTo(bot, tp, 40f, 220f, stateM)` with `tp` = the target's
   **current** position, and holds `ShipControlState.AllWeapons` every tick.
   Fixed forward guns pointed at where a scout *is*, not where it *will be*
   when the round arrives, mostly miss.
2. **Ramming:** the same call uses an **arrival radius of 40 units** at the
   enemy's position. The bot tries to fly *into* its target. There's no
   stand-off band, overshoot or breakaway, so dogfights collapse into
   collisions.
3. **Test-convenience weapon:** without `weapons`, the loadout uses
   `Catalog.DefaultWeaponPart` (`Catalog.cs:200-216`), "the cheapest
   dispersion-0 real gun", which here is **`PW A-B Omega 2`** (part 216).
   That's a determinism choice for tests, not a dogfight weapon. The rest
   of the default loadout (`Catalog.cs:~370-392`) was Booster 1 (23), **Sig
   Cloak 3 (34)** and Sm Shield 1 (21).
4. **Cloak pulsing:** `EngageShipAt` turns `CloakActive` on for 4 s out of
   every 18 s (`:1395-1399`), a vizfight demo "beat". Mid-fight that makes
   ships drop out of visibility and targeting (the bot target scan skips
   `IsNotVisible`).

**Reuse, don't reinvent:**
- Lead math already exists, native-faithful: `ShipDodgeLaw.SolveForImpact(dp,
  dv, speed, radius, out dir)` (used by probes `src/Allegiance.Core/Sim/Probe.cs:538`,
  P491) and `Ship.SolveForImpact` (missiles, `Missile.cs:236`).
- The port has a native wingman/drone combat leg (`UpdateWingmanCombat` in
  `src/Allegiance.Core/Sim/Ship.cs`). Native drone combat lives in
  `~/Development/Allegiance/src/Igc/shipIGC.cpp` (wingman/`c_ptWingman`
  combat: turn-to-face the lead point, fire when the angle is small, within
  weapon range). Read both and mirror native's aim and fire gating where it
  fits a network bot.
- Weapon range and speed come from the real part/projectile data
  (`core.PartTypes` / projectile types). Don't use literals.

## Scope (harness only: `tools/allegiance-mcp`)

1. **Lead-aimed steering:** aim at the intercept point from
   `SolveForImpact`, using the bot's weapon projectile speed from the real
   data, the relative position/velocity from `cs.World`, and the target's
   radius.
2. **Fire gating:** hold `AllWeapons` only while the target is inside a small
   aim cone (derive the cone from target angular size or native's gate,
   cited) **and** inside weapon range (projectile speed × lifespan from the
   real data). Otherwise don't fire.
3. **Stand-off, no ramming:** keep a stand-off band (e.g. a minimum
   separation from both radii), and overshoot or break away and re-attack
   instead of driving to the enemy's position. Keep the existing
   imminent-collision branch in `CombatFlyTo` working.
4. **Dogfight default weapon:** when `weapons` is omitted, pick a sensible
   anti-ship gun that mounts on the hull (cite the selection rule in the
   report). Keep the dispersion-0 path available for tests that need
   determinism (an explicit parameter or test-only default), not as the
   live default.
5. **Cloak:** no periodic cloak pulsing by default (make it opt-in if kept).
6. **Lifepod handling.** When a bot's ship is beaten, the server turns *that
   same ship* into the pilot's lifepod. It swaps to a lifepod hull (seen:
   hull 444 / 113), applies an outward burst and logs `[eject] player ship N
   ejected -> Lifepod hull …`. That's a port of native
   `FedSrv.CPP:11944-12006` (`src/Allegiance.Server/Server.Combat.cs`
   ~1960-2017). The MCP bot doesn't notice and **keeps running its combat
   routine in an unarmed pod**, chasing and ramming until it's shot down or
   its 300 s oxygen runs out (P279). Domingo saw this as "bots won't die".
   Fix: detect the lifepod hull or state from the bot's own world view, stop
   combat, and fly to the nearest friendly station to dock, as a human
   pilot would. Then resume normally after the respawn or relaunch path the
   server already provides (`[lifepod] … docked Lifepod at station …`).
   Cover it with a unit test on the state decision and in the live proof:
   count pods that reached a station vs pods that idled.
7. **Stats:** extend `get_perf_stats` (or the scenario summary) with
   **per-bot shots fired, ship hits and kills** taken from real wire/world
   events, so hit rate is measurable from MCP.

## Explicitly out of scope

- Any game/server code (combat, damage, collision, eject/lifepod), vizfight,
  and the MonoGame client.
- **The MCP probe client becoming side 0's team leader and mission owner**
  (`[lead] side 0 new team leader ship 8113 'mcp-probe'`, bare hull, never
  launches). Note it as a follow-up; it involves server leadership rules.
- Bombers/missile tactics beyond keeping `MaybeFireMissile` working.

## Verification

- **Failing-first unit tests** on pure helpers (pull aim, fire-gate and
  stand-off decisions into testable static functions): a crossing target
  needs a lead point that differs from its current position; fire is off
  outside the cone or range and on inside; the steering goal never equals
  the target's position (stand-off); the default weapon isn't the
  dispersion-0 test gun; a lifepod-hull bot chooses "go dock" over combat.
- **Live headless proof, before and after:** a local in-process server (not
  the 221 test server), `spawn_scenario {size:5, target_sector:<a shared
  sector>}` at the server's default 20 Hz **and once at 10 Hz** (`--ups 10`; 221 showed bots firing only ~8 shots each at 10 Hz vs ~50 at 20 Hz, so the fire loop must cope with either rate) for a fixed window (e.g. 5 min). Report **per-side shots, ship
  hits, hit rate and kills** from the new stats, and **collision count**
  between bots (from the server log), plus **lifepods docked vs. lingering**. The "before" numbers come from the
  current code. The bar is a clearly higher ship-hit rate, actual bot-on-bot
  kills, and far fewer bot-bot collisions.
- Keep existing allegiance-mcp tests and `verify_stdio.py`-style checks green.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs **on the final code**. Cite `docs/TEST_FLAKES.md` for flakes.
  **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and live proof are done, BEFORE
  starting the 3× full-suite run**, then amend or add a follow-up commit
  with the suite numbers. Confirm with `git log -1`. A previous session
  exited while "waiting" on its suite and never committed.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P539_MCP_COMBAT_BOTS.md`: the causes with
   file:line, native citations for the aim and fire gating, the before/after
   live table, test results, and the probe-leadership follow-up note.
