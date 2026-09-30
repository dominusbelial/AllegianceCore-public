# P443 — no starting money and no station income: seed the side pot and add station income to payday

Date: 2026-09-30 · Landed on `master` (not pushed; Domingo pushes) · Branch `master`

## Summary

A mission previously started every side with an empty treasury (the port had no
`StartingMoney` anywhere), so the moment P441 makes purchases cost money a fresh
player would have 0 credits to spend. And each payday accrued only the base
`600 × (1 + IncomeMoney)`, omitting the reference formula's per-station income
term — with a comment claiming station income was "always zero here because the
server has no stations," a claim that has been false since stations became
runtime objects. This task seeds each side's starting pot at a real game start,
pays it out immediately (native `DoPayday` right after entering the game), and
adds Σ over the side's stations of `station.Type.Income` to every payday.

Files changed:
- `src/Allegiance.Core/Model/Mission.cs` — two new named constants
  (`StartingMoneyPerSide = 16000`, `StartingMoneyMultiplier = 1.0`), the new
  `ApplyStartingMoneyAndInitialPayday(double now)` seeding + immediate-payout
  entry point, the station-income term in the periodic payday pass, and the
  stale "server has no stations" comment corrected.
- `src/Allegiance.Server/Server.Lobby.cs` — `CompleteStartCountdown` now calls
  `mission.ApplyStartingMoneyAndInitialPayday(now)` at the real Started
  transition (and passes the sim time through `AdvanceStartCountdowns`).
- New test `tests/Allegiance.Core.Tests/StartingMoneyStationIncomeLiveTests.cs`
  — 3 live loopback wire tests (Bot + in-process Server).

No existing test's expected amount changed (see "Existing tests touched").

## Pre-fix failing-test evidence

The three new live tests were written first and run against the **unfixed**
code (`d5df96ec` era, before any P443 change). All three failed at runtime, each
for a reason that demonstrates the missing feature:

| Test | Pre-fix failure |
|---|---|
| `Real_start_seeds_starting_money_pays_immediately_then_next_at_60s` | `Expected: 18400 Actual: 675` — at Started the bot's first PAYDAY is the 60s periodic one (675 = 600×1.125 for Dreghklar), **not** an immediate starting-money payday of 18400 (= 16000×1.15). |
| `Owned_stations_add_their_income_to_the_periodic_payday` | `Expected: 4855 Actual: 675` — payday is the base-only 675 (600×1.125); the 4180 of placed station income (2000+2000+180) is missing. |
| `Captured_station_moves_its_income_to_the_new_owner_on_next_payday` | `Expected: 2675 Actual: 675` — the 2000 SpecMine(Si) income never contributes to either side's payday. |

(The test harness references the native literal `16000` rather than
`Mission.StartingMoneyPerSide` so the suite still compiles against the unfixed
code and the evidence is a genuine runtime failure, not a compile error.)

## Design decisions

### 1. Seeding lives on the real Started transition, not every Started tick

`Server.CompleteStartCountdown` is the port's single real Started-transition
choke point (the only place in `src` that sets `Stage = Started` besides tests
flipping it directly). Seeding + immediate payout happen there, mirroring native
(money seeded at game start, `fsmission.cpp:1992-2001`; `DoPayday()` after
entering the game, `fsmission.cpp:2278`). A test that flips `Stage` directly to
`Started` bypasses it — which is correct and deliberately keeps the dozens of
existing live tests (which flip the stage as a convenience) untouched, exactly
as native only seeds money on an actual start.

### 2. Seed formula and the fStartingMoney = 1.0 honesty note

Per side, at start:

```
seed = StartingMoneyPerSide × (fStartingMoney + civ.BonusMoney)   // 0 if the multiplier ≤ 0
     = 16000 × (1.0 + civ.BonusMoney)
```

`StartingMoneyPerSide = 16000.0f` is the reference mission's hardcoded
`c_fStartingMoney` (`missionigc.cpp:2752`), the same override-the-core-slot
pattern `IncomePerPayday`/`SecondsBetweenPaydays` already follow. The
`MissionParams.fStartingMoney` multiplier is a **named stated default of 1.0**:
the native mission-params header holding its real default is **not** in this
checkout (only `AGCGameParameters.cpp:1058,1074` and WinTrek's
"StartingMoneyValues" combo, `newgamescreen.cpp:194,1009`, reference it), so,
following this repo's honesty-about-unrecoverable-native-values convention, we
adopt 1.0 — the value that makes a zero-bonus civ seed exactly 16000. No config
is added. The `≤ 0 → 0` guard (`fsmission.cpp:2001-2002`) is preserved (taken
through a local so the const can't be constant-folded away).

### 3. Immediate payout reuses the existing seam — no second payout path

`ApplyStartingMoneyAndInitialPayday` seeds each side's `Side.Money`, then calls
`Site.PaydayEvent(side, seed)` per side — the exact seam the periodic payday
uses (`Site.PaydayEvent` → `ServerHost.PaydayEvent` → `Server.OnPayday`), which
splits by player count, broadcasts `FM_S_PAYDAY(114)`, credits wallets and
deducts the treasury. Nothing is duplicated.

### 4. The periodic cadence stays start + 60s

`ApplyStartingMoneyAndInitialPayday` also anchors `NextPaydayTime = now + 60s`
at the Started-transition time. This matters in the real flow: the mission
spends its 15s countdown in `Starting`, and the payday block arms `NextPaydayTime`
on the first Starting tick — 15s before Started. Anchoring it at the real start
makes the first periodic payday exactly 60s after the immediate one, never 15s
early and never immediately again. Directly-flipped missions (which skip this
method) keep the existing first-Started-tick arm.

### 5. Station income in the payday pass

Each periodic payday now accrues:

```
income = (long)(IncomePerPayday × (1.0 + civ.IncomeMoney)) + Σ station.Type.Income
```

`StationIncomeFor(side)` sums `station.Type.Income` over every station runtime
in the mission whose live `SideId == side.Id` (`fsmission.cpp:2538-2546`). It
reads the live `Sector.StationRuntimes` (not a snapshot), so a station's
`Station.Capture` re-siding and its destruction (`RemoveStation` on terminate)
both fall out of current ownership automatically. `DataStationTypeIGC.Income`
was already decoded (`DataStationTypeIGC.cs:37,276`) and is now read. The stale
comment at the old `Mission.cs:592-593` ("always zero here because the server
has no stations") is corrected.

### 6. Debug mode: no special case

There is no economy precedent for `liveSettings.debugMode` (its uses are the
lifepod timer, P379 spawn topology, P387 auto-start, P388 no-game-over), so no
special case is added. The P387 debug auto-start reaches `Started` through the
same `StartCountdown → AdvanceStartCountdowns → CompleteStartCountdown` path, so
it gets starting money exactly like a normal start (at boot with no players yet,
the seed is kept in the side treasury and paid out once players join). Confirmed
by inspection of `Server.Lifecycle.cs` / `Server.Lobby.cs` — the P387 path is
unchanged and flows through `CompleteStartCountdown`.

### 7. P439 mission rotation

P439 has landed. Rotation (`RotateMissionAfterGameOver`) rebuilds a **fresh**
`Mission` object (so `NextPaydayTime = 0` and every side's `Money = 0` again),
and that fresh mission reaches `Started` via the same `CompleteStartCountdown`
on its next real start (directly in debug mode, or after the players ready up
otherwise). Therefore a rotated fresh mission is seeded with starting money when
it starts, exactly as intended; no extra handling was needed because the seed
is keyed to the real start, not to a per-mission-instance latch.

## Real per-civ starting amounts (PCore001)

From the loaded core (civilization records), `seed = 16000 × (1 + BonusMoney)`:

| Civ | BonusMoney | StartingMoney |
|---|---|---|
| Dreghklar (1) | +0.15 | 18400 |
| Technoflux (2) | −0.20 | 12800 |
| Ga'Taraan (3) | 0 | 16000 |
| Phoenix (4) | 0 | 16000 |
| Omicron (5) | 0 | 16000 |
| Gigacorp (18) | +0.25 | 20000 |
| Iron Coalition (25) | −0.125 | 14000 |
| Bios (35) | 0 | 16000 |
| Belters (101) | 0 | 16000 |
| Rixian (201) | 0 | 16000 |

The live tests use the standard 2-team config (civs Dreghklar and Ga'Taraan), so
the asserted seeds are **18400** and **16000**.

## Real per-station-type incomes (PCore001)

`DataStationTypeIGC.Income`, read from the core (all other station types are 0,
including Garrison type 15 — the only station type the standard test missions
own):

| Station type id | Name | Income |
|---|---|---|
| 21 | SpecMine (Ur) | 1250 |
| 22 | SpecMine (Ca) | 1250 |
| 23 | SpecMine (Si) | 2000 |
| 27 | SpecMine (He) | 750 |
| 57 | SpecMine | 100 |
| 73 | [T]ransceiver | 180 |

The tests place real types: SpecMine(Si) ×2 (2000 each) + Transceiver ×1 (180)
= **4180** of station income, on top of the side's base `600 × (1 + IncomeMoney)`.

## Verification

New live wire tests (Bot + in-process Server) run against the fixed code:
1. **Real start** — two bots seat during the pre-game hold, both sides ready up,
   the 15s countdown runs to Started. Right at Started each bot receives exactly
   one `FM_S_PAYDAY(114)` equal to its side's starting seed (18400 / 16000), and
   its server wallet equals that seed. 10s later still exactly one payday each
   (not immediately again); the next payday is the periodic one ~60s later with
   the base-only income.
2. **Station income** — a side owning SpecMine(Si)×2 + Transceiver earns exactly
   `600 × (1 + IncomeMoney) + 4180` in its periodic payday (4855 for Dreghklar),
   more than the pre-fix base-only 675.
3. **Capture re-sides income** — a SpecMine(Si) owned by A adds 2000 to A's
   payday; after `station.Capture(B)` it adds 2000 to B's payday and no longer to
   A's, on the next payday.

Suite tallies (whole-solution build: 0 warnings / 0 errors):

- `Allegiance.Core.Tests` — **three consecutive full runs** of the final code,
  each `3531 passed / 8 failed / 3539 total`. Every failure is pre-existing and
  none is attributable to P443 (the new `StartingMoneyStationIncomeLiveTests`
  and the existing `PaydayTests` pass every run). Across the three runs the
  failures were:
  - **stable, pre-existing on pristine master** (identical every run): the 5
    conquest/game-over tests — `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`, `GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side`, `ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over`, `ConquestTerritoryLiveTests.Flags_disabled_capture_broadcasts_nothing_and_does_not_end`, `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` — plus `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`;
  - **documented pre-existing live-wire timing flakes** (rotating per run, each
    passes in isolation): `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` (run 2; verified passes 3/3 in isolation), `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire` (run 2), `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` (run 3), `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (run 3). These match the flake ledger from P441 (`LiveMoneyTests.Board_refund`, `LiveChatSectorWireTests`, `LiveRipcordTests`, `WarpBombTests`, …).
- `Allegiance.Lobby.Tests` — **12/12 clean.**
- `Allegiance.ClientCore.MonoGame.Tests` — **868/868 clean.**
- `Allegiance.ClientCore.Tests` — 302/305 then 303/305 (two runs): the failures
  are the pre-existing, P441-documented set — `ThreePeerSeededSessionTests`
  (flake), `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` — none P443-related.

So P443 introduces **zero** new test failures across the whole solution; the
baseline already carried the named pre-existing failures above (verified on
pristine master in P441 and consistent across P443's runs).

## Existing tests touched

**None asserted old amounts that needed changing.** The existing payday tests
remain valid unchanged for two structural reasons that this task respects:
- `PaydayTests` flips `Mission.Stage = Started` directly, which bypasses the
  started-transition seed (deliberate — see design 1), so no starting money
  enters their treasuries; and
- the standard wire-test missions own only Garrison stations (type 15,
  `Income = 0`), so the station-income term adds nothing to their expected
  payday values.

Both were re-run and pass unchanged. New tests added: 3 (listed above).

## Out of scope (per brief, unchanged)

- Server buy pricing (P441), client wallet/HUD (P442), shop prices (P468),
  BuildingTechs (P444).
- Reading `c_fcidIncome` / `c_fcidSecondsBetweenPaydays` from the core instead
  of the hardcoded constants.
- He3 ore value (`Ship.OreCreditPerUnit = 5`), a known labelled simplification.
