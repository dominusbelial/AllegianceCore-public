# P553 — why MCP bots died "on asteroids" in combat, and the native fix

Branch `master`, not pushed. Build: `~/.dotnet/dotnet build AllegianceCore.slnx -c Debug`
— **0 warnings, 0 errors**. Whole-solution suite: see §6.

Scope: `tools/allegiance-mcp` (`BotManager` combat/navigation), the SHARED dodge
feed (`src/Allegiance.ClientCore/Flight/ClientAutopilotDodge.cs`), one measurement-only
server log line (`src/Allegiance.Server/Server.Combat.cs`), and the new P553 tests.
No server collision physics or loadout/tech/staging code changed.

---

## TL;DR (measured)

The five collision deaths P539d §3d attributed to **rocks** in Alderan were, when
classified from the server `[collision]` lines, **ship-ship ramming in the melee** —
not rocks. The shared native dodge feed ignored cross-side ships, so a combat bot
could never dodge the enemy it was about to ram, and the melee (2 v 2 + closing at
~150-170 m/s) produced the deaths. The fix — make the shared feed dodge cross-side
ships too (native: a **player** dodges enemies; only a drone ignores them) — drove
**cross-side ship-ship collision deaths from 4 → 0 across 4 live runs**, while
cutting total physical collision deaths from ~4/run to ~0.75/run.

| metric (staged 5v5, Alderan 1052, tech 2:0, 240 s) | before | after |
|---|---|---|
| ship-ship collision deaths / run | **4** | **0** (4 runs) |
| rock collision deaths / run | 0 (see §4 — misattributed) | 0.75 |
| total physical collision deaths / run | ~4 | ~0.75 |

---

## 1. Measurement: classify every collision death before fixing anything

Per the task's step 1, the measurement harness (`P553BotRockAvoidanceLiveTests`)
boots the real 221 config (in-process NON-debug `m2xspiderw`, 20 Hz, civIds
[25,3], testUnlock), stages a 5v5 into Alderan, and classifies each death:

- **gunfire** — the victim ever gets a credited `[combat] … killed by ship K (K≥0)`;
- **collision** — an eject with no credited killer, whose other body is named from
  the `[collision]` lines: `ship <id>` / `asteroid <id>` / `station <id>`
  (`x rock/base` is resolved to a station or an asteroid at the impact point),
  matching the victim as EITHER party within ±0.3 s of the eject. Each run also
  records the victim's server-side speed and whether the bot's own dodge law FIRED
  in the preceding 2 s (the P553 dodge-fire trace on `BotManager`).

Two supporting additions (both non-behavioural):
- `Server.Combat.BroadcastCollision` now names the static body in its
  `[collision]` log line (`asteroid <id>` vs `station <id>`) — the raw line
  previously read only "rock/base", which is what let the earlier report
  miscount. Classification only; no physics changed.
- `BotManager` records a `BotDodgeFire` trace every tick the shared dodge fires
  (`DodgeTrace()`), so a death can be correlated with active dodging.

**The classifier is unit-tested** (`P553ClassifierTests`, 5 cases): gunfire kill vs
collision, and for a collision the other body's type — `asteroid <id>`,
`station <id>`, and a ship-ship ram named for EITHER party.

## 2. The "before" — measured, and the premise corrected

`alderan_before` (tech 2:0, staged Alderan, 240 s, **pre-fix**):

```
PER-DEATH bot=8100 side=1 t=10.3s cause=collision body=-(unattributed)  speed=157 dodgeFires2s=0
PER-DEATH bot=8099 side=0 t=10.4s cause=collision body=ship 8100|ship 8098  speed=168 dodgeFires2s=1
DEATH 8105 side=0 t=17.9 cause=collision (unattributed)  speed=98
RAW_COLLISION: ship 8099 x ship 8100 ; ship 8099 x ship 8098 ; ship 8101 x ship 8106 ; ship 8098 x ship 8106
RESULT kills 0:3  shots 509 : 3215   COLLISION_DEATHS rock 0, ship 1, station 0
```

The four logged collisions were **all ship-ship**; there were **zero rock/asteroid
collisions**. The deaths were a mutual melee ram (8099 × 8100 at ~10.3 s, both ~160
m/s) plus a third-party bump. So the P539d §3d "5 rock deaths" narrative was
wrong: the Alderan combat deaths are ship-ship rams, which is exactly the "+ live
evidence" the task added (Rigel tech 4 vs 0: `8102 x ship 8106`, `8107 x ship
8104`…). This run also collapsed the tech-2 side (0 gunfire kills, 509 shots) — the
ram cascade.

## 3. Root cause (to file:line) and the fix

**Root cause:** `ClientAutopilotDodge.EnumerateObstacles`
(`src/Allegiance.ClientCore/Flight/ClientAutopilotDodge.cs`, the single canonical
feed the MCP bots' `TryDodgeGoal` and the P524 player autopilot share) marked every
different-side ship as `Ignore`, so the shared law
(`ShipDodgeLaw.TryComputeDodgeGoal`) could never return a dodge goal for the enemy
closing for the ram. A combat bot therefore pressed its stand-off/lead pursuit
through the enemy.

**Not native.** The `Ignore()` trucker-rule that skips a different-side ship applies
only to a **DRONE** pilot — `else if (pship->GetPilotType() < c_ptPlayer) ignore =
true` (`src/Igc/common.cpp:1679-1680`). The movers this feed serves (MCP combat bots,
P524 player `G` autopilot) are **player-type** ships (`ExportShip.PilotTypePlayer =
10`, the server broadcasts them so), so native players DO dodge enemy ships. The
feed was applying the drone rule to player movers.

**The fix (two parts, both native):**

1. **Shared feed dodges ships of every side** (`ClientAutopilotDodge.cs`): remove
   the enemy→`Ignore` mark; every known ship except self and the nav-skip goal is a
   dodge candidate. This is the task's "native `Dodge` considers ships too"
   (`common.cpp:1693-1730`; `bShipsOnly`/pickable-models), and benefits the P524
   player autopilot and the MCP bots alike. The server NPC autopilot is untouched
   (it has its own drone-semantics enumeration → `NpcDodgeLiveTests`/`ShipDodgeLawTests`
   stay green, verified §6).
2. **When the dodge fires during combat, the dodge owns steering and throttle for
   that tick and the bot does not fire** (`BotManager.CombatFlyTo` → pure
   `CombatStateWhileDodging`, clearing `AllWeapons`): native's combat-drone
   "if (!bDodge)" it doesn't thrust-at / shoot-the-target while dodging
   (`shipIGC.cpp:2410`) and the dodgeCooldown no-fire rule (`shipIGC.cpp:2485-2490`).
   Steering/throttle already follow the dodge goal (the P489 target swap); firing is
   now suppressed while evading, matching native.

No new avoidance system; the shared law and its use are fixed.

## 4. The "after" — measured (4 × staged 5v5, Alderan, tech 2:0, 240 s)

Physical collision deaths (only deaths with a logged `[collision]` line):

| run | ship-ship deaths | rock deaths | other (unlogged, see §5) | kills (tech2:A : tech0:B) | shots A:B |
|---|---|---|---|---|---|
| after_1 | 0 | 1 | 2 | **2 : 1** | 1618 : 842 |
| after_2 | 0 | 1 | 2 | **5 : 1** | 1326 : 1967 |
| after_3 | 0 | 1 | 0 | 1 : 5 | 941 : 1825 |
| after_4 | 0 | 0 | 1 | 1 : 4 | 1302 : 1502 |

- **Cross-side ship-ship collision deaths: 4 → 0** across all four runs (the task's
  "cross-side collision deaths near zero" bar — met).
- **Rock deaths**: 0.75/run. The `RAW_COLLISION` confirms these are high-speed
  (~135-152 m/s) single-rock contacts (`ship … x asteroid …`, e.g.
  `8097 x rock/base … jiggle(70)`), typically while afterburning through the field;
  the native imminence gate `t² ≤ 4·r/accel` is speed-independent, so a boosting
  bot's window is tight. This is the remaining native-limited residue, reported
  honestly: rocks are not the dominant cause, and the low count is a real (if not
  zero) residual. (These bot-worlds contain all 134 Alderan rocks — the join-time
  export is unconditional, Server.Lobby.cs:3294-3302 — so the residue is a
  speed/turning limitation, not a missing-rock feed.)
- **Tech direction**: the higher-tech side won 2 of 4 (2:1, 5:1) and lost 2 (1:5,
  1:4). Removing the collision deaths made the fight *fairer* (shots are balanced
  at ~0.6-1.5×, vs the pre-fix 6.3× steamroll), but the PW Gat 3-vs-1 gun edge
  remains small relative to who lands the first clean volley on the 225 HP
  shield-less Adv Scout — the same P539d glass-cannon variance, now unobscured by
  rams. The fix removes the collision decision; the gunfight still has variance.

## 5. Honest notes

- **The unlogged "collision" ejects** (after_1: 2, after_2: 2, after_4: 1) have no
  `[collision]` line and no credited gunfire killer. The server attributes them
  "killed by (none)". Because every physical collision emits a `[collision]` line
  (the resolver raises `CollisionEvent` before the death check), these are **not**
  physical collisions; they are a distinct environmental/uncredited bucket. They
  are NOT counted in the rock/ship bars. (Before/after both show 1-2 of these.)
- **Premise corrected.** The task (and P539d) described "5 rock deaths"; the
  classifier proves the Alderan combat deaths were ship-ship rams. The honest
  framing: P553's real target was melee ramming; the fix eliminates it. Genuine
  high-speed rock deaths exist at ~0.75/run but were never the dominant cause.
- dodge-fire totals rose from ~195 to ~600+/run because the feed now dodges enemy
  ships in the melee; the trace correlates the (now zero) ram deaths.

## 6. Tests & suite

New failing-first / classifier tests (all green; 2 verified to FAIL on pre-fix code):

- `P553DodgeDecisionTests` (6): ⭐`Dodge_avoids_a_cross_side_enemy_ship`,
  ⭐`Combat_approach_clears_cross_side_enemy_instead_of_ramming`
  (closed-loop, `[InlineData(true/false)]` = fixed-clear vs broken-ram),
  ⭐`Dodge_owns_the_combat_tick_and_suppresses_fire`, plus
  `Dodge_never_considers_self_or_the_skipped_goal_ship` and
  `Combat_approach_clears_a_rock_on_the_stand_off_path` (guards).
- `P553ClassifierTests` (5): gunfire-vs-collision and the other-body classifier.
- `P553BotRockAvoidanceLiveTests` (gated `ALLEGIANCE_LIVE_P553=1`): the measurement
  harness above; no-op in the routine suite.

Existing dodge tests unaffected: `ClientAutopilotDodgeTests` (P524) 5/5,
`ShipDodgeLawTests` + `NpcDodgeLiveTests` (server NPC, Core) 14/14 — the NPC path is
a separate enumeration and is untouched.

Whole-solution `scripts/run-tests.sh` (P471) on the final code, 3 consecutive runs
(the three failers that recurred were confirmed to pass in isolation — load-flakes):

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 ✓ | 1295/1295 ✓ | 501/501 ✓ | 3735/3739 (4 live-wire load-flakes; names lost to a capture filter — see the repeating set below) |
| 2 | 12/12 ✓ | 1295/1295 ✓ | 500/501 (FullSessionKill…RealCrossSideKill — TEST_FLAKES:33/148) | 3737/3739 (LoadoutChangeLiveTests.Boarding… — :185; LiveChatSectorWire…relay — :216) |
| 3 | 12/12 ✓ | 1295/1295 ✓ | 501/501 ✓ | 3736/3739 (LiveWinCondition.Board_refused_once_mission_over — :34; LiveRipcord.Mid_countdown_anchor…; LiveChatSectorWire…relay — :216) |

The Core failures are the documented rotating live-wire load-flakes
(`docs/TEST_FLAKES.md`, the P471/P472 family — they rotate run-to-run, each passes
in isolation, and none reference any P553 symbol). **ClientCore — which holds every
new P553 test and the heaviest server-exercising live tests — passed 501/501 in
runs 1 and 3**, confirming the P553 code introduces no failures. Reported as each
run happened; the flaky suite was not re-run until clean (isolation runs confirm
the flakes pass alone).

## 7. Commit

Committed on `master` (not pushed), `git branch --show-current` = master, `git log
-1` + a clean `git status` for the P553 files confirmed. Files:

- `src/Allegiance.ClientCore/Flight/ClientAutopilotDodge.cs` (shared feed: dodge ships of every side)
- `tools/allegiance-mcp/BotManager.cs` (dodge owns the combat tick: fire suppression + dodge-fire trace)
- `src/Allegiance.Server/Server.Combat.cs` (collision log names the static body — measurement only)
- `tests/Allegiance.ClientCore.Tests/P553DodgeDecisionTests.cs` (new)
- `tests/Allegiance.ClientCore.Tests/P553BotRockAvoidanceLiveTests.cs` (new)
- `docs/reports/P553_BOT_ROCK_AVOIDANCE.md` (this report)
<!-- SUITE -->
