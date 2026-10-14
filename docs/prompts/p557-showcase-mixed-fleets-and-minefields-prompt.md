# P557 — showcase scenarios: mixed-hull fleets (fighters, interceptors, scouts, bombers …) with native loadouts, plus minefields laid by native layer drones before the fleets meet

## Why this task exists

**Domingo (2026-10-05), recording 15v15 bot battles on 221 for YouTube:** "we
also want to see the use of mine fields during the scene, we need to showcase
more types of ships, fighters etc".

**Today:**
- `spawn_scenario` (`tools/allegiance-mcp/McpTools.cs:~71`) takes **one `hull`**
  for every bot (default Adv Scout). `spawn_bot` takes a per-bot hull but has
  no staging/arena. So a battle is 30 identical ships.
- **Minefields already exist natively and in the port:** native **layer
  drones** (`c_ptLayer`; bought through the research bucket,
  `FedSrv.CPP:11524-11540`, "New minefield requesting location."; told
  where to lay via the build order, `Igc/shipIGC.cpp:2324-2326, 2826, 4055`).
  Port: P459 (`Sim/Ship.cs:1484-1501, 5803`;
  `Server.Economy.cs:847-921`, `PilotTypeLayer`, sound 330). Minefield
  attrition is P452 (`Sector.cs:2923-3010`); cross-side mine visibility is P453.
  The MCP already has `invest` (buy a drone-type bucket) and `order_drone`
  (`build`, `goto`, …, `McpTools.cs:~141-173, 862`).
- **P555** (`ab5fedc5`) fixed hull equipment masks, so native preferred
  loadouts (shields, chaff, dispensers, missiles) now mount per hull;
  **P556** gives tech levels hull + shield chains. Build on both.
- Wire bots can't fly drone-only hulls (P470: drone hulls group id −1). Use
  only player-buyable hulls.

## Scope (`tools/allegiance-mcp`; no game-rule changes)

1. **Fleet composition per side:** `spawn_scenario {…, fleet_a: [{hull, count,
   tech?}, …], fleet_b: […]}` (or one `fleet` used for both sides). Each entry
   launches `count` bots of that hull with **its native preferred loadout**
   (P555) advanced by `tech` (P556). Staging and arena work over the whole
   mixed fleet (staging waits for all; arena mirrors all). Validate each
   hull (player-buyable, exists in the side's civ/core, mountable loadout)
   and report the resolved per-hull loadouts in the tool output.
2. **Showcase preset:** `spawn_scenario {preset: "showcase", target_sector,
   size_scale?}` builds a sensible, readable mix per side from the side's
   **civ** hull list (e.g. fighters as the main line, interceptors,
   scouts, a couple of bombers or heavy hulls where available), ~15 per
   side by default. Document the composition rule.
3. **Minefields via native layer drones:** `spawn_scenario {…, minefields:
   {side: "a"|"b"|"both", count, at: "rendezvous" | "aleph:<from sector>" |
   {sector, x, y, z}}}` invests in the side's layer drone-type bucket
   (the existing `invest` path; the 221 `testUnlock` money covers it), waits
   for the drone to spawn, then orders it to build at the location (the
   existing `order_drone build` path, native semantics). Default placement:
   in front of the **enemy's arrival aleph** in the rendezvous sector, so the
   arriving fleet flies into it. Report when each field is laid (server
   log) and its position.
4. **Combat behaviour by hull class** (small, native-cited): bombers and
   heavies shouldn't dogfight like scouts. Pick a target class per hull role
   if native drone code distinguishes it; otherwise keep the existing
   combat law and note it as a follow-up. Don't invent tactics.

5. **Mines and missiles in the dogfight: native wingman behaviour flags**
   (Domingo: "during dogfight it can be used too right? when being chased the
   mine field does the job"). Native `WingmanBehaviourBitMask`
   (`Igc/igc.h:785-794`): `c_wbbmStrafe 0x01`, `c_wbbmUseMissiles 0x02`,
   **`c_wbbmUseMines 0x04` ("needs mines already equipped")**,
   `c_wbbmInRangeAggressive 0x40`, `c_wbbmTempSectorAggressive 0x80`. In the
   wingman combat branch (`Igc/shipIGC.cpp:~2630-2680`), with `UseMines` and an
   `ET_Dispenser` whose expendable is a mine type, the drone sets
   **`mineFireIGC` whenever an enemy wingman/player is within 2500 units**
   (`:2651-2665`). Strafe and missiles are handled in the same block. **The port
   has none of these flags** (no `WingmanBehaviour` anywhere in `src/`).
   - Port the flags into the MCP combat law, **behaviour exactly as native**
     (the 2500 u rule, the mine-type check, strafe/missile handling as cited),
     as per-bot options with a scenario default. Read the whole native
     block; don't invent "only when chased".
   - Give mine dispensers to part of each showcase fleet: use the hull's
     native preferred dispenser if it's a mine type, else the existing
     `launcher` path with a real mine dispenser that mounts (P555 masks).
     The tool output lists who carries mines and missiles.
   - Make sure the bot really fires its dispenser/missile launcher on the wire
     (the same mount/fire path a player uses), and that dropped mines are the
     native mine objects (P452 attrition, P453 visibility).

## Explicitly out of scope

- New hulls, art or balance changes.
- Mine/missile behaviour beyond native's wingman flags.

## Proof

- Failing-first unit tests: fleet parsing and validation; per-hull loadout
  resolution (P555/P556); preset composition; minefield placement default
  (enemy arrival aleph).
- **Live (separate-process server over TCP + standalone MCP, 221 config
  incl. testUnlock):** `preset: "showcase"` 15v15 staged into Alderan with
  `minefields: {side: "both", count: 1}` laid first. Show:
  (a) per-side hull mix and loadouts from the tool output;
  (b) both minefields laid (server log lines + positions near each arrival aleph);
  (c) **ships taking mine damage** (server log, P452 attrition lines) and any
      kills attributed to mines;
  (d) the fight resolving with ≥ 3 hull types on each side engaging;
  (e) **mines dropped in the dogfight** by bots with `UseMines` (server log of
      mine objects created by those ships) and **pursuers damaged by them**,
      plus missiles fired by `UseMissiles` bots.
  Quote the log lines and give per-hull kill/death counts.
- A short "how to run a showcase" section in the report, with the exact MCP
  calls Domingo can use for recordings.
- Report `docs/reports/P557_SHOWCASE_FLEETS_MINEFIELDS.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
