# P574 — mines must explode WHERE a ship touches them (per hit, native ApplyMineDamage), not once at the field centre when the minefield dies, plus native explosion sounds for every world explosion

## Why this task exists

**Domingo (2026-10-06):** "if you touched one mine that one in particular
would explode, because that's how they worked on the original client."

**Native:** `Igc/mineigc.h:68-145` (`CmineIGC::HandleCollision`): an enemy ship
moving through an armed field (`c_dtArmTime = 1 s`) takes damage scaled by path
length, speed² and size; it computes `position1 = pHim + dV·tCollision` and
`position2 = position1 + dV·((c_radiusPlacement(10) + rHim)/|dV|) +
RandomPosition(10)`, i.e. **just ahead of the ship along its path**, and calls
`AddMineDamage(type, damage, time, launcher, position1, position2)`. Then
`shipIGC.h:1998-2035` keeps the worst hit per tick, and `ApplyMineDamage` does:
```cpp
if (m_maxMineTime > m_timeLastMineExplosion + 0.5f) {
    m_timeLastMineExplosion = m_maxMineTime;
    GetCluster()->GetClusterSite()->AddExplosion(m_maxMineP2, 1.5f, c_etMine);
}
```
So **a type-1 mine explosion plays at P2 for each hit, throttled to one per
0.5 s per ship**: a trail of violet pops in front of a ship ploughing through
a field (preset: count 4, radius 25, wave 50, violet; `trekigc.cpp:111-117`).

**Port today:**
- `src/Allegiance.Core/Model/Sector.cs:~2941-3062` mirrors
  `AddMineDamage`/`ApplyMineDamage` for **damage** (`MineHit` keeps `P1`/`P2`),
  but **no per-hit explosion** and no `timeLastMineExplosion` throttle.
- `src/Allegiance.Server/Server.Combat.cs:~688-700` sends
  `ExplosionType.Mine` **only on MINE_DESTROYED/expiry, at the field centre**
  (`m.Position`), citing the same `shipIGC.h:2024` line. That's a
  misattribution: native's line is the per-hit one.

## Scope

1. Port the per-ship `m_timeLastMineExplosion` (0.5 s) and emit the
   `ExplosionType.Mine` event **at the hit's P2** (native placement, including
   the random 10-unit offset, deterministic RNG where the sim requires it) to
   the same audience that sees the sector's explosions (`SendExplosionEvent`,
   scale 1.5f).
2. **Check the field-death path against native:** what does native draw when a
   minefield is used up (`KillMineEvent`) or expires (`CmineIGC::Update` →
   `Terminate`)? Keep the centre explosion only if native has it (cite it),
   otherwise remove it.
3. Bots and server-only paths unchanged apart from the event.
4. **"It goes off one by one"** (Domingo): the result must read as individual
   mines detonating in sequence as a ship crosses the field: a pop at each hit
   (0.5 s cadence) **and** the field visibly thinning per hit (native
   `SetMineStrength(m_fraction)` → `efgeo.cpp` SetStrength drops decals; P563
   restores the client side). Check which decals native drops (by index or
   nearest). If native drops by index, keep native; an **optional, off-by-default**
   "remove the decal nearest the pop" mode is fine if cheap. Show a frame strip of
   a scout crossing a field: pops one after another, the decal count falling.

5. **Explosion sounds for every world explosion** (Domingo: "mines had a sound
   associated with their explosion right?"). Native `WinTrek/trekigc.cpp:86-101`:
   every `AddExplosion` in the **player's current cluster** plays a positional
   `GenericSoundSource(position, velocity)` sound by scale: `> 100`
   `largeExplosionSound`, `> 10` `mediumExplosionSound`, else
   `smallExplosionSound`. So a mine pop (scale 1.5) plays the small sound and a
   ship death (scale = 2 × radius) usually the medium one. **Port gap:**
   `Audio/ExplosionSoundTier.cs:34-38` and `SoundManager.cs:307-309`
   (ef120/ef24/explosionshockwave) exist, but the only caller is the missile
   path (`Game1.Audio.cs:165-166`). Server `ExplosionEvent`s (ship/station/
   asteroid/mine) applied in `ClientWorld.Apply(ExplosionEvent)`
   (`ClientWorld.cs:~2040`) are **silent**. Play the native tier for each
   explosion event in the current sector, with the existing positional
   attenuation, without double-playing missile detonations already voiced by
   `MissileSoundPlanner` (check against `sounddef.mdl` ids). Tests: tier per
   scale, same-sector only, no double play; live: hear the mine pops and a ship
   death.

## Proof

- Failing-first tests: a ship flying through an enemy field produces
  explosion events at P2 (ahead of the ship, within (10 + r) ± 10 along its
  velocity), at most one per 0.5 s per ship; none for friendly fields or before
  the 1 s arm time; field death per native.
- Live (P572 tooling if landed, else the real client against a local server,
  real window): fly a scout through an enemy minefield → a trail of violet
  pops in front of the ship; screenshot and server log lines.
- Report `docs/reports/P574_PER_HIT_MINE_EXPLOSIONS.md`.

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
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
