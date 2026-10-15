# P542 — other ships' projectiles are invisible in the MonoGame client (bots' fire confirmed server-side): measure where they're lost, then fix

## Why this task exists

**Domingo (2026-10-04, 5v5 MCP bots on 221):** the bots were maneuvering and
"trying to dogfight, it looks like they have no weapons/ammo?". Then: "can
we fix the invisible projectiles? since we can confirm they were actually
firing".

**Server-side evidence (221 log, ~8 min, 20 Hz):** 528 `[projectile] ship N
fired -> PROJ_INST(12) type 85 …` lines from the 10 bots (Adv Scout,
default gun `PW A-B Omega 2`) and 21 `[autoreload]`. So the bots really fire
and the server really emits `PROJECTILE_INSTANCE`. Domingo saw **no bot
tracers** in his client. (The bots also rarely hit, which is a separate
issue fixed by P539/P539b; this task is only about *seeing* other ships'
shots.)

**The pipeline (verify every hop, don't assume):**
1. **Server send:** `src/Allegiance.Server/Server.Combat.cs:~55-104`. The
   shooter's own peer, plus same-side teammates **only once they hold the
   shooter's ExportShip** (the `_teammateExportSent` gate), plus cross-side
   viewers through `CrossSideViewers` (P369/P375), **excluded while the
   shooter is cloak-hidden**. The old MCP bots pulsed cloak 4 s out of every
   18 s (removed in P539, `a2f79ddc`). The log line ends with
   `-> {n} peer(s)`.
2. **Client receive:** `src/Allegiance.ClientCore/Session/ConnectionSession.cs:816-819`
   → `ClientWorld.Apply(ProjectileInstance)` (`State/ClientWorld.cs:1931-1952`).
   ⚠️ **P462 own-echo drop:** `OwnEchoDropPredicate`
   (`Game1.FlightInput.cs:2222`, `_ownShotEcho.ShouldDropServerEcho`) drops a
   server projectile as "our own echo" based on projectile type and position
   near our ship. Domingo flew the same hull, likely with the same
   projectile type as the bots, so **bot shots near him may be dropped as
   duplicates of his own**. Check the predicate's exact matching rule (type,
   radius, time window, and whether it checks *who fired*). PROJ_INST
   carries the shooter/launcher id; native de-dup is per own shot, not "any
   round of the same type near me".
3. **Client draw:** `Game1.Rendering.cs:~744-800`. A visibility radius of
   5,900 around our server position (`Game1.cs:279`), per-type style
   `ProjectileStyle.For(typeId, _projectileTypes)` (width and colour from
   the core, amber fallback; P465b), and camera-near cull
   (`ProjectileCameraCull`). Check type 85's resolved width and colour.

## Scope

1. **Measure first, live.** Add lightweight counters to the client log,
   e.g. a once-per-second `[proj-diag]` line with per-source-side counts:
   PROJ_INST received, dropped as own echo, pruned before first draw,
   radius-culled, camera-culled, drawn. Count by shooter: own / teammate /
   enemy. Pair them with the server's `[projectile] … -> n peer(s)` lines.
   Run a real fight: a local server plus MCP `spawn_scenario` bots plus a
   real client session (a headless client is fine), with the client's own
   ship near the bots and firing too.
2. **Find the real cause(s)** from those counts and fix them, faithful to
   native. Native draws other ships' rounds that the client receives or
   simulates; cite how native creates remote projectiles on the client
   (search `clintlib`/`WinTrek` for the projectile-instance handler, and
   for how firing state in ship updates drives remote weapon fire). Likely
   fixes:
   - (a) the own-echo predicate must match **only our own shots**, using
     the shooter id the message carries, not the projectile type alone
   - (b) any server delivery gap (teammates missing exports, cross-side
     gating) that drops visible shooters' rounds
   - (c) style/width/colour so every projectile type renders visibly
3. Keep the counters behind a debug flag, or keep them quiet (once per
   second, only when non-zero) so normal logs stay clean.

## Explicitly out of scope

- Bot aim/weapon choice (P539/P539b) and server hit detection.
- Missiles (separate pipeline, `FireMissile`/`MISSILE_UPDATE`).

## Verification

- **Failing-first unit tests** for whatever the root cause is. For example,
  if it's (a): an enemy round of the same type as ours, spawned next to our
  ship, is **kept**; our own echo is still dropped.
- **Live proof:** before/after `[proj-diag]` counts in a bot fight. After
  the fix, enemy and teammate rounds received ≈ drawn (minus legitimate
  radius/camera culls), with server `-> n peer(s)` matching client receipt.
  Quote the lines.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs **on the final code**. Cite `docs/TEST_FLAKES.md`. **Report only
  numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and live proof are done, BEFORE the
  3× suite run**, then amend with the suite numbers. Confirm with
  `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P542_REMOTE_PROJECTILES_VISIBLE.md`: the
   per-hop counts, the root cause(s) with file:line, native citation, the
   fix, tests and live proof.
