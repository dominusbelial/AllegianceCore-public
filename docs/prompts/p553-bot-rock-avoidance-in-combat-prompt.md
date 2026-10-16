# P553 — MCP bots still die on asteroids in combat (5 collision deaths in Alderan's 134-rock field) although the native dodge law is wired in: find why it fails during engagement and fix it the native way

## Why this task exists

**P539d** (`78831d20`, `docs/reports/P539d_FAIR_CONTACT_SHOWCASE.md` §3d/§3e):
in travel-with-staging mode on the real 221 rendezvous (Alderan 1052, **134
asteroids**), the tech-2 side won the gunfight 2:0 but took **5 collision
deaths vs 1**. Arena mode in Alderan (offset 950) lost 0:5 to "134 rocks and
a long approach". Only the open sector (Rigel 1059, offset 300) let tech
decide. Domingo wants bots that can fight **anywhere**, including asteroid
sectors (most of the map).

**The dodge law is already wired in** (verify):
- `tools/allegiance-mcp/BotManager.cs:1307-1323`: `CombatFlyTo` calls
  `TryDodgeGoal` (`:1370+`), the shared native dodge law
  (`Allegiance.Core.Sim.ShipDodgeLaw` / `ClientAutopilotDodge`, P489/P524) fed
  from the bot's `ClientWorld` (asteroids, stations, known ships).
- Combat engagement uses it: `EngageShipAt` (`:1485+`) →
  `CombatFlyTo(bot, StandOffLeadGoal(...), 15f, 160f, stateM)` (`:1553`).
- P539d added cross-side collision avoidance and stand-off on approach.

**So why do bots still hit rocks?** Find out. Candidates to check, then
confirm by measurement:
- Aim/orientation overriding the dodge: the bot keeps turning to its firing
  solution, so the dodge goal is computed but the steering still faces the
  target. **Native:** the combat drone calls
  `Dodge(this, m_commandTargets[c_cmdPlan], &state)` and **when dodging it
  doesn't pursue/aim normally** (`Igc/shipIGC.cpp:2363-2410`, the
  `if (!bDodge)` at `:2410`). The general `Dodge` is `Igc/common.cpp:1693-1790+`
  (closest approach via `solveForImpact`; window `t² ≤ 4r/accel` when
  `tMax < 0`; `GotoPlan::SetControls` uses it at `:1919-1932`, `:2001-2003`).
- The dodge time window is too short at combat speeds, the asteroid radius
  is wrong in the bot's world, or rocks aren't all in its world (export or
  visibility).
- Collisions might be **ship-ship**, not rock: **classify every collision
  death by the other object's type** (asteroid / ship / station) from the
  server's `[collision]` lines before fixing anything.

## ➕ Live evidence from 221 (2026-10-05, after P539d): ramming in the dogfight is also a target

A 5v5 staged into **Rigel 1059** (open sector) with **tech 4 (PW Gat Gun 3) vs
tech 0 (PW Gat Gun 1)** on 221 (MCP at `78831d20`), ~240 s:
- Shots 1215 : 1197 (staging works), gunfire kills **2 : 0** for the tech-4 side.
- But the tech-4 side's **both** deaths were **ship-ship collisions in Rigel**
  (`[collision] … 8102 x ship 8106`, `8107 x ship 8104`, `8107 x ship 8106`, all
  cross-side, sector 1059), and one side-B death was a collision too.
- A transiting bot (8096) scraped the **same Alderan rock 5 times** in a row
  (`[collision] … 8096 x rock/base sector 1052 …`).

So **close-range dogfight ramming between ships** is an explicit target too,
not only rocks. P539d's cross-side avoidance holds on the approach, not in
the melee. Apply the same native rule: native `Dodge` considers ships too
(`bShipsOnly` / pickable models, `common.cpp:1693-1730`; `c_gpmDodgeShips`).
Live bar addition: in the Rigel tech 4 vs 0 staged run, **cross-side
collision deaths near zero**, and the higher-tech side wins on gunfire.

## Scope

1. **Measure:** run the staged 5v5 in Alderan (221 config) and log, per
   collision death, the object type and id, the bot's speed and state
   (engaging / standing off / transiting), whether `TryDodgeGoal` fired that
   tick, and the steering actually applied.
2. **Root-cause** to file:line (MCP harness, shared dodge law, or world
   data) and **fix it the native way**: when the dodge fires during combat,
   the dodge owns steering and throttle for that tick (native `if (!bDodge)`).
   Keep firing only if native does. Don't add a new avoidance system; fix
   the shared law and its use.
3. If the shared law has a bug, fix it in the shared module, so the server
   NPC autopilot and the P524 client autopilot (which share it) benefit.
   Re-run their existing tests.

## Explicitly out of scope

- Arena mode, staging, tech mechanics (P539d/P539b), except using them.
- Server collision physics (native-matched).

## Proof

- Failing-first tests: a combat bot with a rock on its stand-off path steers
  around it (closed-loop sim); dodge pre-empts aim when native says so; the
  collision-death classifier.
- **Live (local server, 221 config, staged 5v5 in Alderan, 240 s × 3):**
  rock collision deaths before vs after (target: near zero), ship-ship
  collisions before vs after, and the gunfire kill split. Then **3 × tech 2
  vs 0 staged runs in Alderan**: the higher-tech side should now win, or show
  with numbers what still decides.
- Report `docs/reports/P553_BOT_ROCK_AVOIDANCE.md`.

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
