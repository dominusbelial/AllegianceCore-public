# COMBAT_SCORING (P215) — real kills, deaths, and the Elo combat rating (player-scoring Stage 1)

Branch `master`, on top of P214. Makes `PlayerScore` genuinely reflect combat
for the first time: this task wires real kill/death counters and the native
Elo-style **combat rating** into `Server.BroadcastKillShip` (the `KillShipEvent`
→ kill-broadcast handler that fires on every real ship death). This is Stage 1
of the staged player-scoring plan (`allegiance-player-scoring-plan`): the
highest-leverage slice, with **zero** dependency on any other unbuilt system.
`Score`/`Rank` stay unmutated (Stages 2/3).

## Background / relationship to native (verified against the live source)

- **`Wopr/AllegianceInterop/igcWrapper.h:2160-2228`** — the managed
  reconstruction of native `PlayerScoreObject`; the kill/death/rating members
  this task ports are `AddKill()`/`AddDeath()` (`igcWrapper.h:2209-2214`),
  `GetCombatRating()`/`SetCombatRating()` (`igcWrapper.h:2218-2219`), and the
  underlying native `m_cKills`/`m_cDeaths`/`m_fCombatRating` fields
  (`src/Igc/igc.h:5940,5942,5950`).
- **`src/Igc/common.cpp:2706-2717`** — the real Elo-style update, ported
  verbatim as `PlayerScore.AdjustCombatRating`:
  ```
  k = (ratingAdd + killeeRating − killerRating) / ratingDivide
  killerRating = killerRating·(1−k) + k
  killeeRating = killeeRating·(1−k)
  ```
  The two tuning constants are IGC core-file float constants
  `c_fcidRatingAdd` = **28** and `c_fcidRatingDivide` = **29**
  (`src/Igc/igc.h:92-93`). Their real values are read from the **loaded core**
  (`PCore001.igc`'s `OT_constants` payload → `DataConstantsIGC.FloatConstants`)
  exactly as the port already reads other core float constants — not hardcoded.
  Empirically decoded from `PCore001.igc`: `FloatConstants[28] = 1.25`
  (`c_crAdd`, `missionigc.cpp:2719`) and `FloatConstants[29] = 100`
  (`c_crDivide`, `missionigc.cpp:2720`).
- **Combat-rating neutral seed = `0.0f`** — `PlayerScoreObject` ctor
  `m_fCombatRating(0.0f)` (`igc.h:5510`) and
  `PersistPlayerScoreObject::Reset()` (`igc.h:5453`).
- **Killer credit gate — `FedSrv.CPP:11811-11821`**: native awards `AddKill()` +
  `AdjustCombatRating(...)` only when the launcher is a real side-owned
  **SHIP**, the victim was **not** a lifepod, and the killer is on a
  **different** side (`pcredit && !bLifepod && (psideCredit != pside)` then
  `if (type == OT_ship) pfsshipCredit->AddKill() + AdjustCombatRating(...)`).
  A suicide/environmental/team kill therefore never grants kill credit or a
  rating swing.
- **Victim death — `FedSrv.CPP:12026-12032`**: `if (pfsShip->IsPlayer())
  pfsPlayer->AddDeath()` — the victim's death is counted on **every real death**
  regardless of who (or what) killed them.

## What changed

### `src/Allegiance.Server/PlayerScore.cs`
- Added three members:
  - `int Kills` (native `m_cKills`, `igc.h:5940`),
  - `int Deaths` (native `m_cDeaths`, `igc.h:5942`),
  - `float CombatRating` (native `m_fCombatRating`), defaulting to the native
    neutral seed **`0.0f`** (`igc.h:5453,5510`).
- Added `AddKill()` (`m_cKills++`, `igc.h:5819`) and `AddDeath()`
  (`m_cDeaths++`, `igc.h:5837`).
- Added the pure static `AdjustCombatRating(ratingAdd, ratingDivide, killer,
  killee)` — the exact port of `common.cpp:2706-2717`. It is **static and
  parameterised by the two constants** (not taking a mission) so the math is
  independently testable; the wiring passes the core-read constants in.
- Added `CombatRatingAddIndex = 28` / `CombatRatingDivideIndex = 29`
  (internal consts) for the wiring.
- Rewrote the class doc comment: the old "nothing ever mutates these values
  from combat" claim is now false for these three and the comment says so
  plainly; `Score`/`Rank` remain unmutated.

### `src/Allegiance.Server/Server.cs` — `BroadcastKillShip`
Placed the scoring **after** the P107 eject gate (an ejection is NOT a death —
native calls `AddEjection` in its `!bKilled` branch) and **before** the
observer-targets gate (native counts the victim's death regardless of who is
watching). Two parts:

1. **Victim death** — if the victim has an owning peer (`owner`, the exact
   `_peerShips.Where/FirstOrDefault` reverse-lookup the method already uses),
   `_peerShips[owner].PlayerScore.AddDeath()`.
2. **Killer credit** — if `launcher is Ship attackingShip && attackingShip !=
   victim && !bLifepod && attackingShip.SideId != victim.SideId` (the faithful
   FedSrv.CPP:11811-11821 gate; the self-kill is kept explicit although the
   same-side gate already covers it), resolve the attacker's owning peer with
   the **same** reverse-lookup pattern, and if it's a real peer call
   `AddKill()` plus — only when the victim is also a real peer —
   `AdjustCombatRating(fc[28], fc[29], killerScore, victimScore)`.

### Simplifications, stated plainly
- **Partial peer participation.** Native carries a score object even on drones
  (a `CFSShip` private-data exists for every ship); this port has a
  `PlayerScore` only on `_peerShips` entries (real players). So: a bot victim
  has no death counter and no rating holder — the player killer still banks the
  kill counter, but the two-object rating update is **skipped** (native would
  fold the drone's `0.0` rating in; we skip rather than fabricate a holder). A
  bot killer gets no credit (it has no `PlayerScore`). Neither crashes and only
  whichever side is a real peer is updated — exactly the "only update whichever
  side is actually a real peer" behaviour the task asked for.
- The rating update runs only when **both** participants are real peers (the
  only case both ratings exist to combine). `Score`/`Rank` are untouched
  (Stages 2/3).

## Explicitly out of scope (unchanged)
- `CalculateScore`'s full formula (Stage 2) — `Score` stays unmutated.
- `GAME_OVER`/`GAME_OVER_PLAYERS`/`RANDOMIZE_TEAMS` rank-ratio logic (Stage 3) —
  `Rank` untouched.
- Any other `PlayerScoreObject` counter (assists, ejections, warps/asteroids
  spotted, techs, flags, artifacts, rescues, repair, base kills/captures, time,
  win/loss flags) — each needs its own gameplay event this port mostly doesn't
  have yet.
- Any client change (`Allegiance.ClientCore`, MonoGame) — purely server-side
  sim state.

## Tests — `tests/Allegiance.Core.Tests/PlayerScoreCombatScoringTests.cs` (8 new)

Pure unit tests (no server), `PlayerScoreCombatRatingTests`:
- `Adjust_combat_rating_matches_native_formula_exactly` — equal ratings →
  symmetric update with the hand-derived numbers (winner 0.50625 / loser
  0.49375).
- `Lower_rated_killer_beating_higher_rated_killee_gains_more_than_the_reverse`
  — the classic Elo asymmetry, asserted **quantitatively**: the 0.1-rated
  upset winner over a 0.9-rated killee gains 0.01845 vs the 0.9 favourite's
  0.00045 over the same gap.
- `Combat_rating_defaults_to_native_neutral_seed_zero` — seed is 0.0f.
- `Add_kill_and_add_death_increment_the_right_counters`.

Live tests (real in-process `Srv` + real `FltBot` peers), `LiveCombatScoringTests`
(`[Collection("LiveNetwork")]`):
- `Real_two_peer_kill_updates_both_player_scores` — real projectile combat,
  real death; asserts shooter `Kills=1`/`CombatRating=0.0125` and victim
  `Deaths=1`/`CombatRating=0` (the exact `k=(1.25+0-0)/100` result). Uses a
  **non-lifepod** fight hull on distinct sides, with the victim placed on a
  non-mission side so the port's faithful `EjectSurvivor` fails and the damage
  becomes a real death (see the technique note below).
- `Player_killing_an_unowned_bot_ship_only_credits_the_player` — unowned NPC
  victim: player banks the kill, no death, no rating swing; no crash.
- `Bot_killing_a_player_only_counts_the_victim_death` — unowned NPC killer:
  victim's death counted, no credit/rating; no crash.
- `Self_kill_and_environmental_death_do_not_touch_combat_rating` — self-kill
  (launcher == victim) and environmental (launcher == null) count the death but
  never touch rating or grant credit.

The three non-combat edge cases (bot, self-kill, environmental) drive the real
`BroadcastKillShip` handler directly via reflection (the repo's established
"testing private members" pattern) because staging them through live projectile
combat is impractical; the two-peer real-combat case drives the genuine kill
path end to end.

### Technique note: forcing a real death for a non-lifepod victim
Faithful to native's `bKilled` gate, a player-owned **non-lifepod** ship that
takes lethal damage **ejects and survives** unless `EjectSurvivor` fails
(`Server.cs:10769-10785`). To get a real combat **death** (which is what yields
BOTH a death counter AND cross-side kill credit), the victim must be unable to
eject — placing it on a side that is not in the mission makes `EjectSurvivor`
return false ("side not in mission sides — cannot eject"), turning the damage
into a genuine death while the victim remains a real peer (a `PlayerScore` is
keyed by ownership, not side), so both sides still score.

## Verification
- Whole-solution `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` clean
  (0 warnings, 0 errors).
- New tests: 4 pure + 4 live, all pass in isolation; **"must fail if removed"**
  proven — stashing the `Server.cs` wiring turns all 4 live tests red.
- Full-suite baseline `Allegiance.Core.Tests` now **3034** total with the +8.
  Three full-suite runs completed; every interruption was a documented /
  pre-existing live-network flake and none referenced my symbols:
  `LiveSuicideShipDeleteTests.Combat_death...`,
  `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`,
  `LiveCombatWireTests.Real_two_party_combat...`, and
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  (the last proven pre-existing by failing identically on the **pristine** tree
  with my source changes stashed and my test file moved aside). No
  non-documented failure observed in any run.
- Live console evidence from `Real_two_peer_kill_updates_both_player_scores`:
  `[scoring] victim ship 8097 death +1 (player peer)`,
  `[scoring] killer ship 8096 kill +1 (player peer)`,
  `[scoring] combat rating adjusted on both sides`, and
  `P215-LIVE: shooter 8096 Kills=1 CR=0.0125; victim 8097 Deaths=1 CR=0`.

## Scope boundary (what this is and isn't)
This is the smallest slice that makes `PlayerScore` mean something in combat:
a live per-match kill/death tally and a felt per-kill skill-rating swing. It is
still **connection-lifetime-only** (no persistence — the standing architectural
limitation), and `Score`/`Rank` are deliberately untouched. Stages 2 (the
rate-limited `CalculateScore` formula) and 3 (`GAME_OVER`/rank-ratio) build on
the counters this stage now populates.
