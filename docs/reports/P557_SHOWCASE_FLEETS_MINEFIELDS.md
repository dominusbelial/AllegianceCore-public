# P557 — Showcase scenarios: mixed-hull fleets with native loadouts + native layer-drone minefields + wingman mines/missiles

Status: implemented, unit-tested, live-demonstrated (dogfight mines/missiles), 3× suite run (documented pre-existing live-wire flakes only), committed on `master` (not pushed).

## Goal (Domingo, 2026-10-05)

Record 15v15 bot battles for YouTube that showcase **more ship types** (fighters,
interceptors, scouts, bombers) and **minefields** during the scene, with mines used
even in the dogfight.

## What was built

All in `tools/allegiance-mcp` (no game-rule changes), on top of P555 (hull equipment
masks) and P556 (tech hull/shield chains):

1. **New pure module `Showcase.cs`** — hull-role classification, player-buyable test,
   showcase fleet composition, mine-dispenser selection, native wingman-behaviour
   decisions (mine/missile/strafe), and the minefield-placement default. Everything
   is dependency-free of a live server (takes the real `StaticCore` explicitly), so it
   is unit-provable against `PCore001.igc`.

2. **Mixed-fleet composition in `spawn_scenario`** — `fleet_a` / `fleet_b` (or one
   `fleet` for both sides) as `[{hull, count, tech?}]`. Each entry spawns `count` bots
   of that hull with its **native preferred loadout advanced by `tech`** (P555/P556),
   and staging/arena work over the whole mixed fleet. Each hull is validated
   (player-buyable, real mountable native loadout); invalid hulls are specific errors.

3. **Showcase preset** — `preset:"showcase", size_scale:N` composes a deterministic,
   documented mix per side from the real core's player-buyable hulls: fighters as the
   main line, then interceptors, scouts, bombers/heavies, ~15 per side × `size_scale`.
   It gives a real mountable **mine dispenser** to ~⅓ of each entry and enables native
   wingman `UseMines`/`UseMissiles`/`Strafe`.

4. **Native wingman behaviour flags** ported into the MCP combat law — `WingmanBehaviourBitMask`
   (`UseMines`, `UseMissiles`, `Strafe`), per-bot with a scenario default, behaving
   exactly as native (see citations below). The bot really fires its dispenser/launcher
   on the wire and the dropped mines are native mine objects.

5. **Minefields via native layer drones** — `minefields:{side,count,at}` invests the
   side's **layer-drone bucket** (found by PilotType), waits for the drone, and orders
   it to `build` at the location (default: in front of the enemy's arrival aleph in the
   rendezvous sector). Graceful per-field error rows when a side has no layer bucket.

## Native citations (every ported rule)

| Rule | Native |
|---|---|
| Wingman flags `Strafe`/`UseMissiles`/`UseMines` | `src/Igc/igc.h:785-794` (`c_wbbmStrafe 0x01, c_wbbmUseMissiles 0x02, c_wbbmUseMines 0x04`) |
| Mine-fire: UseMines + mounted ET_Dispenser whose expendable is OT_mineType, fire when a different-side **wingman/player** is within **2500 u** | `src/Igc/shipIGC.cpp:2651-2665` |
| Missile-fire: UseMissiles + mounted magazine, fire when lock ≥ 1.0 and evasionSpeed < maxEvasionSpeed (= 20 + maxLock·30) | `src/Igc/shipIGC.cpp:2584-2596` |
| Strafe: close-range `state = leftButtonIGC` | `src/Igc/shipIGC.cpp:2639-2644, 2598-2607` |
| Pilot types (`c_ptWingman=2, c_ptPlayer=10, c_ptLayer=5`) | `src/Igc/igc.h:685-692` |
| Layer drone lays minefields ("New minefield requesting location.") | `src/FedSrv/FedSrv.CPP:11524-11540`; build order `src/Igc/shipIGC.cpp:2324-2326,2826,4055` |
| Drone-only hulls group −1 → excluded (wire bots can't fly them) | P470 |
| Mine objects / attrition / visibility | P452 (`Sector.ResolveMineDetonations`), P453 |

The mine rule is **not** "only when chased" — it fires whenever an enemy wingman/player
is within 2500 u, exactly native.

## Failing-first unit tests — `tests/.../P557ShowcaseFleetTests.cs` (16, all pass)

Pure over the real corpus: fleet parse + validation (real resolve, unknown / drone-only /
zero-count errors); per-hull native-preferred loadout (fighter gun; scout native
`Sm Shield 1` post-P555); showcase composition (≥3 distinct hull types/roles, exact
15×scale, deterministic); mine-dispenser selection (a mounting mine dispenser, honestly
null when none); minefield placement default (in front of the arrival aleph, never
overshooting); wingman decisions (2500 u rule, lock + evasion gate, flag helpers).
```
Passed! - Failed: 0, Passed: 16, Total: 16  (P557ShowcaseFleetTests, + 14 existing pure MCP tests = 30/30)
```

## Live proof (separate-process `Allegiance.Server` over TCP :6073 + standalone `allegiance-mcp`)

Config `src/Allegiance.Server/server.p557.json` (Test Mission, XXV_simple.igc, sides
Alpha/Bravo, `liveSettings.testHarness` + `testUnlock` + `adminCheats` enabled). Calls
issued over the MCP stdio (NDJSON) protocol.

**(a) Per-side hull mix + loadouts (from the tool output).** `spawn_scenario
{preset:"showcase", target_sector:"Ceto"}` returned, per side (15 bots each):
```
sideA fleet: Adv Fighter ×7  [Hvy Booster, Booster 1, Counter 1, Mine Pack 1, PW Gat Gun 1,
                              Sm Shield 1, SRM Dumbfire 1, Mine Pack 1]  carriesMines=true carriesMissiles=true
             Hvy Interceptor ×3 [PW Mini-Gun 1, Afterburner 1, Pulse Probe, Counter 1]
             Adv Scout ×3      [Counter 1, EWS Probe 1, PW Gat Gun 1, MRM Seeker 1, Sm Shield 1, Mine Pack 1]
                               carriesMines=true carriesMissiles=true
             Stealth Bomber ×2 [Counter 1, EW Sniper 1, EW Utl Can 1, Sig Cloak 1, Sm Shield 1, SRM Anti-Base 1]
                               carriesMissiles=true
```
(identical for sideB). 30/30 granted (zero warnings/failures); server log confirms real
mine-dispenser grants: `[loadout] ... granting real launcher part 13 ('Mine Pack 1',
Dispenser@0 subtype MineLauncher, ammo 2) — P435`.

**(e) Mines dropped in the dogfight + missiles fired (UseMines/UseMissiles).**
```
dogfight mine-dispenser fires ([p557]) ..... 2546
missile launches ([p433] fired) .............. 386
native mine objects created ([mine] deployed)  28
MINE_DESTROYED ................................. 28
bot DEATHs ................................... 10  (4 gunfire, 6 collision)
```
Quote lines:
```
[p557] bot mcp-b026-4387 (ship 8169) fired mine dispenser (part 13) — enemy within 2500u (native shipIGC.cpp:2651)
[p433] bot mcp-b016-3622 (ship 8159) fired real missile launcher at lock 1.0 -> SendFireMissile (ready 0.80s)
[fireexpendable] 40 ship 8147 fired mine 12 (id 5519) -> FE(87)+EXPORT(26)/OT_mine to 16 same-side peer(s); dispenser 2 -> 1
[mine] ship 8150 deployed -> EXPORT(26)/OT_mine 12 pos Vector3 { ... } life 5 -> 16 same-side peer(s)
[projectile-hit] ship 8154 hit target 8169 sector 1053 absorbedByShield=True
```
So bots carrying `Mine Pack 1` (a native mine-type dispenser) **really fired their
dispenser on the wire** (server `HandleFireExpendable` → `Ship.DeployMine`) and **missiles**
(`SendFireMissile`). The dropped mines are native `OT_mine` objects (P452 attrition,
P453 visibility) exported sector-wide.

**What the live run could NOT demonstrate on this test map**
- **(b) minefields laid via layer drones**: the XXV test map's side has **no unambiguous
  layer-drone bucket** (`FindLayerBucketName` returns none/a shared-named tower). The
  minefield path is wired and degrades to an honest per-field error row; it needs the
  **221 map** (where Domingo records), which has the real layer bucket. This is the
  follow-up to complete in situ.
- **(c) ships taking MINE damage / mine-kills**: the dropped dogfight mines have native
  life ~5 s and expired (28 MINE_DESTROYED) before a ship physically overlapped them in
  this crowd; no mine-damage line was observed. Getting mine *hits* (not just drops)
  needs a denser chase or the static 221 field.
- **(d) ≥3 hull types ENGAGING quantified**: all four hull types converged and fought
  (all bots engaged; gunfire kills observed), but per-hull kill attribution wasn't tallied.

## How to run a showcase (for Domingo's 221 recordings)

```bash
# 1. server (separate process) with harness/unlock grants
cd src/Allegiance.Server && dotnet bin/Release/net10.0/Allegiance.Server.dll --config server.p557.json
# (edit server.p557.json games[].map + civIds/sideNames for the 221 battle map)

# 2. MCP (stdio) pointed at it
dotnet bin/Release/net10.0/allegiance-mcp.dll 127.0.0.1 6073

# 3. the calls (via any MCP client / Claude Code .mcp.json):
spawn_scenario { preset:"showcase", target_sector:"Alderan", size_scale:1,
                 minefields:{ side:"both", count:1 } }
#   -> per-side mixed hulls + loadouts, mine dispensers on a slice, both minefields
#      laid at the enemy arrival alephs, then the fight resolves
# smaller / one-sided mines:
spawn_scenario { fleet_a:[{hull:"Fighter",count:10},{hull:"Interceptor",count:3},{hull:"Scout",count:3}],
                 fleet_b:[{hull:"Fighter",count:8,tech:2},{hull:"Bomber",count:4}],
                 target_sector:"Alderan" }
get_perf_stats {}   # live kill/hit/TTK per side during the recording
```
Watch the server log for `[fireexpendable] ... fired mine ... -> OT_mine`, `[p557] ... fired
mine dispenser`, `[p433] ... fired real missile`, and P452 mine-damage lines while recording.

## Suite (3 consecutive runs on the final code)

Whole-solution build: `0 errors, 0 warnings`. Full suite executed project-by-project
(the monolithic `scripts/run-tests.sh` exceeded this session's 420 s tool cap). Numbers
below are exactly what each run measured; every failure is a **documented pre-existing
live-wire load-flake** in `docs/TEST_FLAKES.md` (rotating member-to-member, none in P557
code, all 16 P557 tests green in every run).

| project | run 1 | run 2 | run 3 |
|---|---|---|---|
| Allegiance.Core.Tests | 3744/3745 | 3744/3745 | 3744/3745 |
| Allegiance.ClientCore.Tests | 528/530 | 529/530 | 529/530 |
| Allegiance.ClientCore.MonoGame.Tests | 1295/1295 | 1295/1295 | 1295/1295 |
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |

Run-1 flakes: `StationUnderAttackAlertLiveTests.Second_hit_within…`,
`FullSessionKillIntegrationTests…CrossSideKill`, `CrossSideCombatVisibilityLiveTests…
across_warp_return`. Run-2: `LoadoutChangeLiveTests.Boarding_and_leaving…`,
`RetriedConnectLiveTests…`. Run-3: `SideEliminationLiveTests…`,
`ClientWarpApproachSlowHullLiveTests.Legacy_200_450…`. All listed in `TEST_FLAKES.md`
(TLF test → status `load-flake` / `suspect (P472)`).

## Files

- `tools/allegiance-mcp/Showcase.cs` (new) — the pure P557 module.
- `tools/allegiance-mcp/McpTools.cs` — `spawn_scenario` fleet/showcase/minefields +
  wingman args; `SpawnScenarioModern`; helpers.
- `tools/allegiance-mcp/BotManager.cs` — `MaybeFireMine`, native gate in `MaybeFireMissile`,
  strafe in `EngageShipAt`, `FindMineDispenser`, `WingmanBehaviour`/`MineDispenserPartId`
  on `ActiveBot`, `FindLayerBucketName`, `FindArrivalWarp`, `FindWarpToSector`.
- `tests/Allegiance.ClientCore.Tests/P557ShowcaseFleetTests.cs` (new) — 16 tests.
- `src/Allegiance.Server/server.p557.json` (new) — the showcase server config.

## Follow-ups (not in scope / not done here)

- Prove the layer-drone **minefield** on the 221 map (bucket present) + mine **damage**.
- Per-hull combat-behaviour by hull class beyond the native wingman flags (native
  distinguishes little else; existing combat law kept, noted as follow-up).
