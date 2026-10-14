# P637 — march mode: one side's bombers stuck at home + march combat/observability fixes

## TL;DR

On the 2026-10-13 rig (build `87d3501b`, 221 server + 390 bots, native 200v200 **march**), 27/27
side-1 Stealth Bombers never left home while side-0 bombers marched. Investigation + fix across
six scopes:

1. **Bombers stuck / not reaching their objective** — bombers were being treated as ordinary
   fighter-wing members: same rendezvous target, a fighter route, and staging + release gated on
   the *fighter wing's* assembly. A bomber has its OWN objective (the enemy home sector), so it
   was grouped into a staging group that could never match its own target, and staged/held for a
   release that never fit it. Fixed so every non-rendezvous role gets its **own target, own route,
   own staging group** and — for bombers — **skips the fighter staging barrier entirely** and
   routes straight to the enemy base.
2. **Local reproduction + live proof** — march showcase on the REAL m2xspiderw map (crucial: the
   server's `debugMode:true` P379 debug topology replaces the map with a 3-sector mini-map
   Omega/Plato/Alderan; the earlier repros ran on it and bombers legitimately couldn't route to
   the real homes 1050/1051). On the real map, with the fix: **bombers of BOTH sides reach the
   enemy home sector** (side-0 4/4 at Organia 1051; side-1 3/4 at Ganymede 1050, the 4th en
   route) **and the fight scores kills** (18), 182 warp crossings.
3. **Role→target→route→staging visibility** — `list_active_bots` now reports `role`, `targetSector`,
   `routeIndex`, `route`, `stagingSector`, `stagePhase`, `velocity`, `speed`, `idle` so Claude can
   check a live march per bot.
4. **Relaunched bots idle at home** — a bot that died, docked its pod and relaunched was reset into
   the staging barrier (`StagePhase=0`); its wing had already released and advanced, so it held at
   home forever. A relaunched combat bot is now a **reinforcement**: it is placed in the released
   phase and routes straight to its target to rejoin the fight.
5. **P622 follow-up — focus fire** — the P622 "least-engaged first" pick spread the fleet
   1-per-target (slow 1v1 duels). Default is now **fill-to-cap**: the most-engaged eligible target
   is filled to its class cap before starting another (nearest/threat first, caps/tiers/lone-miner
   hold kept). `ALLEGIANCE_P622_LEAST_ENGAGED=1` restores the spread for A/B. **Measured: focus vs
   spread is close — at 30v30 focus 11 / spread 13 kills; at 75v75 focus 8 / spread 15 kills
   (180 s).** Spread edged focus at these densities; see §5 for the honest discussion + the run
   variance caveat.
6. **`list_active_bots` positions stale with the decision snapshot OFF** — positions are now read
   from the bot's OWN ClientWorld live entity (fresh regardless of the snapshot flag) with
   velocity/speed, and an `idle` flag (speed≈0 for >5 s while not docked).

Whole-solution build **0 warnings / 0 errors** (Release). Full suite via `scripts/run-tests.sh`:
2 consecutive runs, numbers in §Verification.

## Root cause (primary, scope 1)

In the march showcase every bot — bombers included — was spawned with the RENDEZVOUS as
`TargetSector`, assigned to a fighter route wing, and staged+released against the fighter wing:

- `SpawnScenarioModern` assigned every slot the rendezvous target and a route from
  `routePlans` (home→rendezvous) — `tools/allegiance-mcp/McpTools.cs` (spawn loop).
- `DriveCombat` staged at the route's penultimate sector and held until
  `StageReleaseRequired(side, routeIndex)` returned true — `tools/allegiance-mcp/BotManager.cs`.
- `StageReleaseRequired` grouped by `(side, routeIndex)` only, so a bomber whose own goal differed
  was aggregated into — and forced to wait on — a fighter wing's assembly.

Result: a bomber aimed at the enemy home could be stuck in a fighter wing's staging group (wrong
sector), or hold forever for a release condition that never fit its own target.

### Fix (file:line)

- **Per-role target** — `BotManager.ResolveRoleTarget` (`BotManager.cs`, new pure rule): a BOMBER's
  own target is the enemy home sector; every other role keeps the rendezvous. Wired into
  `SpawnScenarioModern` (`McpTools.cs`) via each slot's `Showcase.HullRole` (`AddSideFleet` now
  records the role on the slot).
- **Own route** — bombers get their own routes home→enemy-home (`bomberPlans`, discovered with the
  same `FindRoutes` used for fighter wings), assigned to their own wing (never a fighter wing).
- **Staging grouping by goal** — `StageReleaseRequired(side, goal, routeIndex, …)` keys each
  release group by `(side, goal, routeIndex)`; a bomber (goal=enemy home) is never in — or waited
  on by — a fighter wing (goal=rendezvous), even sharing a route index. Pure rule
  `BotManager.SameStagingGroup`. The staging narrate also counts only same-goal bots.
- **Bombers skip the fighter staging barrier** — `bot.UseStaging = … && role != "bomber"`
  (`SpawnBot`): a non-rendezvous role routes straight to its own target instead of participating in
  the fighter fair-contact convergence. This is the task's "…or none".
- **Warp-map freshness** — `TransitWarp` replaces the sticky `bot.WarpDest ??=` cache (which froze a
  partial warp-destination map built before the client's EXPORT warp batch landed, permanently
  blinding cross-country routes): it rebuilds the ~map-size dest map on demand, so a bomber can fly
  every hop of its own route.

### Reproduction note (important)

The first local repros ran with the P572/live-proof server config `liveSettings.debugMode:true`,
which applies the **P379 debug test topology** (`Server.Lifecycle.cs`, gated on debugMode): the map
is replaced by a 3-sector mini-map — "side 0 home Omega(1053)/side 1 home Plato(1060), single warp
each to Alderan, Alderan sealed". On that mini-map bombers *cannot* route to the real garrison
sectors 1050/1051 (they don't exist as routable warps), reproducing a stuck-bomber look-alike that
was a **test-harness artifact, not the bug**. The real rig runs the full m2xspiderw map. With
`debugMode:false` (the real topology) the bug reproduces as designed and the fix is verified.

## Scope 4 — relaunched bots idle at home (file:line)

`UpdatePodState` reset a relaunched bot into the staging barrier (`StagePhase=0`). Its wing had
usually already released and advanced, so the reinforcement held at home (the observed "back at
home with target null"). Now on relaunch the bot is placed in the **released** phase
(`StageReleasedPhase`/`IsStageReleased`, `BotManager.cs`) and routes straight to its target — a
reinforcement rejoins the fight instead of re-entering a barrier its wing left.

## Scope 5 — focus fire (P622 follow-up)

`CombatTargetSelector.Pick` previously picked the LEAST-engaged eligible target (1-per-target
spread). It now **fills the MOST-engaged eligible target up to its class cap before starting
another** (nearest tie-break), preserving P622's caps, threat tiers and lone-miner hold
(`PickBy(concentrate:…)`, `CombatTargetSelector.cs`). `ALLEGIANCE_P622_LEAST_ENGAGED=1` restores the
old spread for measurement.

## Scope 6 — live positions + idle

`ActiveBots()` now reads each bot's pose + velocity from its OWN ClientWorld live entity
(`TryBotOwnLiveMotion`), which is fresh regardless of the decision-snapshot flag (P636d `off`), and
adds `velocity`, `speed` and an `idle` flag (`IsBotIdle`: speed < 1 m/s for > 5 s while not docked).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Bomber objective = enemy home | none (scenario policy; native bombers are just ships) | design | Bombers get the enemy home sector as their own target (scope policy out of scope to debate). |
| Staging / release barrier | none in native (wingmen engage immediately) | modern (P539d harness) | Fair-contact convergence is a harness concern; bombers opt out. |
| Within-tier nearest target | `FindTargetShip(…c_ttNearest)`, src/Igc/shipIGC.cpp:2030-2057 | design (native tie-break) | Kept as the tie-break inside a tier. |
| Engager caps / threat tiers / lone-miner hold | none in native | modern (P622 harness) | Kept unchanged. |
| Target concentration (focus-fire) | none in native | modern (P637) | Fill-to-cap default; spread retained behind switch. |
| Warp-destination lookup | warp/partner walk, src/Igc/common.cpp:677-707 | design | Now refreshed per consultation instead of a sticky partial snapshot. |

## Modern alternative considered

- **Task assignment (Hungarian/auction)** for target choice would give globally-optimal spreading,
  but is O(n³) and overkill for a greedy per-tick harness; the fill-to-cap heuristic reaches the
  same goal cheaper (carried forward from P622's rationale).
- **Interest-managed warp streaming** (modern relevance filtering) would give every client the full
  warp table cheaply; the immediate fix is the per-consultation rebuild so a bot never caches a
  partial map. The underlying "client lacks far warps" case is a server-broadcast question already
  flagged by P635's aleph-transit ceiling — out of this task's scope (kept minimal).

## Failing-first

- The P637 unit-test symbols (`ResolveRoleTarget`, `SameStagingGroup`, `IsStageReleased`,
  `PickBy`, `IsBotIdle`) do not exist on pre-P637 code → the new test file fails at compile time
  (the repo's accepted failing-first form, see P622CombatTargetSpreadTests).
- Demonstrated runtime-failing: reverting the selector to spread
  (`PickBy(false, …)`) makes `FocusFire_FillsMostEngagedTargetBeforeStartingAnother` and
  `FocusFire_SecondBotFillsSameTarget` fail (2 failed / 0 passed); restored afterward.

## Verification

- **Unit tests** — `tests/Allegiance.ClientCore.Tests/P637BomberRoutingAndStagingTests.cs` (new:
  role→target, staging-group-by-goal, relaunch-release, focus-fire vs spread, idle) +
  `P622CombatTargetSpreadTests` updated for the focus-fire default. Filtered run: **30/30 pass**.
- **Whole-solution build** — `dotnet build -c Release -m:8` → **0 warnings, 0 errors**.
- **Live march (real map, debugMode off, 30v30, target Alderan)** — bombers of BOTH sides reach the
  enemy home sector: side-0 bombers 4/4 at Organia (1051), side-1 bombers 3/4 at Ganymede (1050)
  (the 4th en route); **18 kills**, **182 warp crossings**. Verified via `list_active_bots` fields
  (`role=Bomber`, `targetSector`=enemy home, `sector`==target on arrival) + server `[warp]`/
  `[combat] killed` lines.
- **Scope-5 A/B (kills over 180 s)** — 30v30: focus **11** vs spread **13**; 75v75: focus **8** vs
  spread **15**. Both produce kills (combat verified); spread edged focus at these densities —
  reported honestly. Numbers are single-pair samples; transit variance is significant at low
  density, so this wants an arena-mode/high-density A/B before concluding (recommendation below).
- **Full suite** — `scripts/run-tests.sh` equivalent (the four test assemblies, sequential, the P471
  runner's exact order), 2 consecutive runs on the final code:

  | Assembly | Run 1 | Run 2 |
  |---|---|---|
  | Allegiance.Lobby.Tests | 12/12 | — (identical 12/12) |
  | Allegiance.ClientCore.MonoGame.Tests | 1949 passed / 3 skipped | — (identical) |
  | Allegiance.ClientCore.Tests | 693/694 (1 known load-flake) | 694/694 |
  | Allegiance.Core.Tests | 4006/4011 (5 known load-flakes) | 4010/4011 (1 known load-flake) |

  Every failure is a **known ledered load-flake** in `docs/TEST_FLAKES.md` — live-wire
  receipt-timing tests that fail intermittently under load (e.g.
  `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` line 404;
  `LiveChatSectorWireTests.Individual_AllSector_…` line 216). None are bot-related or new tests;
  the P637/P622 routing-and-staging tests all pass. Reported each run as it happened, per the P471
  rule (no re-running a failed test until it comes up clean).

## Notes for Claude

- **Republish the Mac bot build** and verify on the rig.
- On the rig, confirm the P379 debug topology is OFF for the march showcase (it hides the real map
  and reproduces a fake "stuck bomber"). The March-mode bomber check must run on the full
  m2xspiderw map.
- `ALLEGIANCE_BOT_SYNC_WINGS=on` still restores the side-wide fighter wing barrier (unchanged);
  `ALLEGIANCE_P622_LEAST_ENGAGED=1` restores the P622 spread for the focus-fire A/B.

## Recommendation (scope 5, from measured data)

Focus-fire did not increase total kills at 30v30 / 75v75 over 180 s (spread edged it: 13 vs 11 and
15 vs 8). The concentration hypothesis (gang up → faster kills) is not borne out at these sparse
densities, where transit/contact dominates. Recommended next step: an **arena-mode** A/B (both
fleets spawn in the target sector) at 100v100 to isolate the target-assignment effect from transit
variance, then decide whether fill-to-cap or spread is the default. The change is shipped as the
directive requested, with the spread switch retained for exactly this measurement.
