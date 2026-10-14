# P549 — combat parity gates: friendly ramming does no damage (native), only warp-bomb missiles detonate alephs, missiles honour their arming delay, and kill credit uses DamageTrack (top damage dealer, not last hitter)

## Why this task exists

From the P535/P538 audits, as **re-verified by Claude's spot-check
(2026-10-05; wiki `allegiance-p1-p200-parity-audit`)**:

1. **Friendly ramming damages in the port. Native suppresses it.** P535
   rated collisions MATCH, a false all-clear. Native `ReceiveDamage` drops any
   positive damage from a friendly or allied launcher when friendly fire is
   off (`Igc/shipIGC.cpp:1222-1228`). Port `Ship.ReceiveDamage`
   (`src/Allegiance.Core/Sim/Ship.cs:~3930+`) has no side gate (its remark
   at ~3890 says only the projectile pass enforces it); the ship-ship
   collision pass (`Sector.cs:~2452-2466`) exempts only friendly lifepods.
   Player-visible: bot teammates bump constantly.
2. **Any missile touching an aleph acts as a warp bomb.** Native requires
   `c_eabmWarpBomb` (`igc.h:774`; `missileigc.cpp:459-466`). Port
   `Sector.cs:~3266-3287` has no capability check: any stray missile
   schedules a 15 s explosion at the aleph. **Also the reverse:** natively a
   warp-bomb missile hitting a non-aleph does no damage (the warp-bomb
   branch skips the damage path, `missileigc.cpp:461-466`).
3. **Missile arming delay (`readyTime`) ignored.** Native sets the
   activation time at `missileigc.cpp:104` and gates homing and thrust at
   `:363`. Port `Missile.cs:~215-250` has no gate; `ReadyTime` is read only
   at `DataMissileTypeIGC.cs:124`.
4. **Kill credit = last hitter.** Port `Sim/Ship.cs:~4023` passes `launcher`
   straight to `KillShipEvent`. Native credits the first DamageTrack
   bucket, sorted by total damage, highest first (`shipIGC.cpp:1320-1333`,
   `common.cpp` `DamageTrack::ApplyDamage`/`sort` ~2592-2640; the client
   creates it via `CreateDamageTrack`). Port DamageTrack faithfully
   (buckets, decay if native has one, sort) and credit the kill from it,
   including collision kills.

## ⚠️ Don't apply as written (spot-check)

- P535's "collision HP pool = shield-OR-hull" fix applies **only** to the
  static ship↔station (`shipIGC.cpp:792-796`) and ship↔asteroid
  (`:962-965`) branches. **Ship-ship is `+=`** (`:1147-1157`, the "terralthra
  fix"), and the port already matches it (`Sector.cs:2445-2446`). Fix it
  only in the station/asteroid branches, or leave it out and list it as a
  follow-up.

## Scope

Fixes 1–4, each cited to native. Keep lifepod exemptions and FF settings
semantics (if the port has a friendly-fire setting, honour it exactly as
native's `ReceiveDamage` does).

## Explicitly out of scope

- Explosion NoWarn/NoDebris flags (cosmetic), aleph mass limits, aleph
  exit wobble (separate).

## Proof

- Failing-first tests: a teammate collision → 0 damage to both (FF off), an
  enemy collision still damages; a non-warp-bomb missile reaching an aleph →
  no aleph explosion, a warp-bomb missile → native behaviour, a warp-bomb
  hitting a ship → no damage; a missile before `readyTime` neither homes
  nor thrusts; kill credit goes to the top damage dealer when the last
  hitter dealt less.
- **Live loopback:** a 5v5 MCP bot fight (`spawn_scenario`, 120 s). Quote
  zero friendly-collision damage events and the `[combat] … killed by` lines
  matching DamageTrack.
- Report `docs/reports/P549_COMBAT_PARITY_GATES.md`, plus dated correction
  notes in P535 (collision MATCH) and P538 (severity of the warp-bomb item).

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
