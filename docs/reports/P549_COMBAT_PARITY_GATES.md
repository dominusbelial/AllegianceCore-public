# P549 — Combat parity gates: friendly ramming, warp-bomb capability, missile arming delay, DamageTrack kill credit

Date: 2026-10-06 · Branch: `master` (not pushed) · Suite: 3× `scripts/run-tests.sh`

## Summary

Four combat-parity bugs from the P535/P538 audits (re-verified by Claude's
spot-check, wiki `allegiance-p1-p200-parity-audit`), each cited to native, each
fixed and proven failing-first:

1. **Friendly ramming deals damage in the port. Native suppresses it.** Native
   `CshipIGC::ReceiveDamage` returns `c_drNoDamage` for any positive damage from
   a friendly (same-side or allied) launcher while friendly fire is off
   (`shipIGC.cpp:1222-1228`). The port's `Ship.ReceiveDamage` had no such gate —
   the ship-ship collision pass only exempted friendly *lifepod* pairs, so
   non-pod teammates battered each other ("bot teammates bump constantly").
   Fixed by porting the FF gate into `ReceiveDamage` (FF is always off here; no
   toggle, so same-side collapses to the `pside == launcher->GetSide()` leg).
   The physical bounce still happens; only the hull harm is dropped.
2. **Any missile touching an aleph acts as a warp bomb.** Native requires the
   `c_eabmWarpBomb` capability (`missileigc.cpp:459-466`, `igc.h:774`) — and the
   reverse, a warp-bomb missile hitting a non-warp does **no** damage (the
   warp-bomb branch skips the damage path). The port's `ResolveWarpBombTriggers`
   armed a 15 s blast for *every* missile reaching an aleph, and the combat pass
   gave a warp-bomb missile full `Power` on a ship. Both directions fixed.
3. **Missile `readyTime` ignored.** Native sets `m_timeActivate = now +
   dataMissileType->readyTime` (`missileigc.cpp:104`) and gates both homing and
   thrust behind `now > m_timeActivate` (`:363`). The port's `Missile.OnUpdate`
   homed and thrust from tick 0. Fixed by storing `_timeReady` and gating the
   homing + thrust blocks (the position step still always runs — native
   `TmodelIGC::Update` advances regardless).
4. **Kill credit = last hitter.** Native credits the death to the first, highest
   total-damage bucket of the victim's `DamageTrack` (`shipIGC.cpp:1319-1331`;
   `common.cpp` `DamageTrack::ApplyDamage`/`sort`), falling back to the final
   hitter only when the top dealer left the mission. The port passed the last
   hitter straight through. Fixed by porting `DamageTrack` (buckets, sort,
   slot-decay) onto each `Ship` and crediting the top dealer at `KillShipEvent`.

## Native references (all cited)

### 1 — Friendly-fire gate (ramming)
- `src/Igc/shipIGC.cpp:1222-1228`
  ```cpp
  if (launcher &&
      (!GetMyMission()->GetMissionParams()->bAllowFriendlyFire) &&
      ((pside == launcher->GetSide()) || IsideIGC::AlliedSides(pside,launcher->GetSide())) &&
      (amount >= 0.0f)) { return c_drNoDamage; }
  ```
- Ship-ship collision calls `ReceiveDamage` for the non-exempt pair
  (`shipIGC.cpp:1141-1185`); the FF gate is what actually suppresses friendly
  non-pod ramming (the `bFriendly && lifepod` exemption at `:1166-1168` only
  covers pod pairs, and only when FF is ON — with FF off the gate covers all).
- Port: `src/Allegiance.Core/Sim/Ship.cs` `ReceiveDamage` (top of method),
  same-side only (no `AlliedSides` concept in this port — P190/P203 precedent).

### 2 — Warp-bomb capability
- `src/Igc/missileigc.cpp:459-466`
  ```cpp
  if (eabm & c_eabmWarpBomb) {
      position1 = Vector::GetZero();
      if (ot == OT_warp) pigc->WarpBombEvent((IwarpIGC*)pModel, this);
  }
  else if (pModel->GetAttributes() & c_mtDamagable) { /* damage */ }
  ```
  → only warp-bomb missiles trigger `WarpBombEvent`; a warp-bomb missile on a
  non-warp target deals no damage but still terminates (`KillMissileEvent`,
  `:512-513`).
- `src/Igc/igc.h:772-774`
  `c_eabmWarpBombDual=0x02`, `c_eabmWarpBombSingle=0x04`,
  `c_eabmWarpBomb = 0x02|0x04 = 0x06`.
- Port: `Data/DataMissileTypeIGC.cs` `HasWarpBombCapability` (`EabmCapabilities &
  0x06`); `Model/Sector.cs` `ResolveWarpBombTriggers` gate + the combat-pass
  ship-hit skip.

### 3 — Missile arming delay
- `src/Igc/missileigc.cpp:104` `m_timeActivate = now + dataMissileType->readyTime;`
- `src/Igc/missileigc.cpp:363` `if (now > m_timeActivate) { /* homing + thrust */ }`
- Port: `Sim/Missile.cs` `_timeReady = now + missileType.ReadyTime`; `armed =
  now > _timeReady` gates the P179 homing block and the thrust block.

### 4 — DamageTrack kill credit
- `src/Igc/common.cpp:2570-2665` `DamageTrack` (ApplyDamage `:2592`, sort
  `:2624`, slot decay `:2571-2581`)
- `src/Igc/igc.h:4927-4928` `c_dwDamageSlotInterval=5000`, `c_iDamageSlotCount=8`
- `src/Igc/shipIGC.cpp:1277-1278` `m_damageTrack->ApplyDamage(timeCollision,
  launcher, amount)` (full pre-shield amount)
- `src/Igc/shipIGC.cpp:1319-1331` kill-credit from the first bucket, gated on
  same-mission
- Port: `Sim/DamageTrack.cs` (new), wired in `Sim/Ship.cs`.

## Root cause / fix per item

### 1 — ReceiveDamage friendly-fire gate
**Root cause:** `Ship.ReceiveDamage` had no side gate; the only suppression was
the projectile pass (same-side rounds pass through) and the collision pass's
lifepod-only exemption. A friendly non-pod collision reached `ReceiveDamage`
with a same-side launcher and dealt full hull damage.

**Fix:** at the top of `ReceiveDamage`, before the experience multiplier and the
repair/damage branch (native order), return `DamageResult.NoDamage` when the
launcher is a same-side `Ship` and `amount >= 0` (repairs still land on friends,
native's `amount >= 0.0f` condition). The collision bounce (`ResolveShipShipCollision`
velocity/position work) is untouched, so ships still ricochet — they just don't
harm teammates.

### 2 — Warp-bomb capability
**Root cause:** `ResolveWarpBombTriggers` ran for every missile in a warp's
volume, and `ResolveCombatCollisions` dealt `Power` to any ship a warp-bomb
missile hit.

**Fix:**
- `ResolveWarpBombTriggers` skips missiles without `HasWarpBombCapability` (a
  stray non-warp-bomb rocket touching an aleph does nothing, exactly native).
- The combat-pass ship branch consumes a warp-bomb missile (remove + destroy
  notice) but deals no damage, matching the native skip of the damage path.

### 3 — Arming delay
**Root cause:** `Missile.OnUpdate` homed (P179 block) and thrust
unconditionally; `ReadyTime` was decoded but never consulted.

**Fix:** `_timeReady = now + ReadyTime` at construction; `armed = now > _timeReady`
guards both the homing block and the thrust block. Pre-arming the missile coasts
inert at its launch velocity (position still integrates — `TmodelIGC::Update`
always runs in native).

### 4 — DamageTrack
**Root cause:** no damage track existed; `KillShipEvent(now, this, launcher, …)`
passed the last hitter.

**Fix:** new `Sim/DamageTrack` with per-launcher buckets kept sorted descending
by total damage (native `DamageTrack::ApplyDamage` + `sort`), plus the native
8-slot × 5 s decay (`RetireSlot`), self-timed from `Ship.OnUpdate`. In the
positive-damage branch of `ReceiveDamage`, record the full pre-shield amount
against a same-mission Ship launcher; at death, credit the track's `TopDealer`
when it is in this mission, else fall back to the launcher (native
`shipIGC.cpp:1319-1331`).

## Scope boundaries / honest deviations

- **Ship-ship collision HP pool is `+=` and correct** — the "don't apply as
  written" note is honoured: the collision HP-pool shield-or-hull divergence
  from P535 applies only to the static ship↔station (`shipIGC.cpp:792-796`) and
  ship↔asteroid (`:962-965`) branches; ship-ship is the `+=` "terralthra fix"
  (`:1147-1157`) the port already matches (`Sector.cs:2445-2446`). Left as a
  P535 follow-up, not touched here.
- **Decay phase is per-track, not shared** — native cycles one global slot phase
  across all tracks via the mission's `DamageTrackSet`; this port gives each
  track its own 5 s self-timed slots. The 40 s retention and the retire-oldest
  semantics are identical; slot phase alignment is unobservable for kill credit.
  Documented in `DamageTrack.cs`.
- **Flash / `m_timeLastDamage` omitted** — the bucket's per-slot flash diagnostic
  (client-only red-flash hint) is not needed server-side; only the running total
  and slot partition that power decay are kept.
- **Explosion NoWarn/NoDebris, aleph mass limits, aleph exit wobble** — out of
  scope (task).

## Proof (failing-first + must-fail)

New tests, all in `tests/Allegiance.Core.Tests/`; each proved red on the pre-fix
code by temporarily reverting its wiring, then green with the fix restored:

| Test | Proves | Must-fail measurement (reverted) |
|---|---|---|
| `ShipShipCollisionTests.Friendly_ramming_deals_zero_damage_but_enemy_ramming_damages_under_same_geometry` | FF-off: same-side collision → 0 damage; identical geometry cross-side → real damage | friendly drop 0.344 (was damaging) |
| `ShipShipCollisionTests.Friendly_ramming_still_bounces_both_ships` | the same-side pair still ricochets (velocities change) with zero hull loss | n/a (guards against over-suppression) |
| `WarpBombTests.Domain_non_warp_bomb_missile_in_warp_radius_arms_nothing` | non-warp-bomb missile at an aleph → no WARP_BOMB, not consumed | armed 1 bomb (was bombing) |
| `WarpBombTests.Domain_warp_bomb_missile_hitting_a_ship_deals_no_damage` | warp-bomb missile on a ship → no damage, consumed | victim frac 1→0.132 (was damaging) |
| `MissileHomingTests.Missile_coasts_inert_until_readyTime_then_homes_and_thrusts` | before readyTime: no homing, no thrust; after: homes + thrusts | thrusted 50→69.7 + turned 3.4 rad before arming |
| `KillCreditDamageTrackTests.DamageTrack_reports_the_top_total_damage_dealer_not_the_last_hitter` | unit: buckets/sort/TopDealer | – |
| `KillCreditDamageTrackTests.Kill_credit_goes_to_the_top_damage_dealer_not_the_last_hitter` | A(60%) + B(50% final) → credit = A | credited ship 703 (last hitter B) |
| `KillCreditDamageTrackTests.Lone_killer_still_gets_the_credit` | regression: single killer still credited | – |

Existing homing/missile tests that assumed instant arming were updated to zero
`ReadyTime` on their corpus missile type via a record copy (`MissileHomingTests`,
`MissileLockTests`, `P467SideGaWeaponOutputTests`) so they keep isolating
homing/thrust; the arming behavior is covered by the new dedicated test.

## Suite

3 consecutive `scripts/run-tests.sh` runs on the final code (commit `eee58812`).
Core total 3753 (3745 baseline + 8 new P549 tests). Each run was clean except
the documented / load-dependent live-wire flakes; every flake was confirmed
**green in isolation** (same-project, no combat-symbol refs):

| Run | Lobby | ClientCore.MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1327/1327 | 529/530 (FullSessionKillIntegrationTests — flake, green in isolation) | **3753/3753 clean** |
| 2 | 12/12 | 1327/1327 | 529/530 (ShipDeleteGoneLiveTests — flake, green in isolation) | 3750/3753 (LiveStationRescue, ConquestTerritoryLive, AddPartTests — flakes, each green in isolation) |
| 3 | 12/12 | 1327/1327 | **530/530 clean** | 3748/3753 (StationUnderAttackAlert, LiveWinCondition, LiveTeamLobby, LiveChatSectorWire, ConquestTerritoryLive — flakes, each green in isolation) |

`AddPartTests`, `LiveChatSectorWireTests`, `LiveWinConditionTests` are in the
documented flake ledger (`docs/TEST_FLAKES.md`); the others are the known
load-dependent live-wire tests that pass in isolation. The one pre-commit
regression caught during development — `LiveInSpaceEjectPodAliveTests.Ejected_pod_is_born_alive_and_can_be_rescued` —
was my FF gate correctly suppressing a same-side lethal test shot; fixed to a
null (environmental) launcher (the test's own comment said launcher side is
irrelevant to ejection), and it passes in every run above.

## Live loopback proof

The task's suggested 5v5 `spawn_scenario` autonomous fight is documented
unreliable for producing the required evidence on this box: autonomous
same-loadout duels produced **zero KILL_SHIP even in 240 s** on standard
configs, and opposing-side combat bots can freeze in place (`docs/reports/P539b_MCP_BOT_REAL_HITS.md`;
the P549 unit tests reproduced no friendly-ramming damage and DamageTrack
credit deterministically instead). The four behaviours are therefore proven at
the sim level with real-Mission failing-first tests and must-fail reverts
(above table). The server's `[combat] ship X killed by ship Y` line
(`src/Allegiance.Server/Server.Combat.cs:1524`) is driven directly by the
`KillShipEvent` launcher, which P549 now credits from the victim's
`DamageTrack.TopDealer` — the exact path `KillCreditDamageTrackTests`
pins (credit goes to the top dealer, not the last hitter).


## Corrections to prior audits

**P535 — `docs/reports/P535_COMBAT_AUDIT.md` (dated correction, 2026-10-06):**
the row "Collision damage … | MATCH" (line 44) was a **false all-clear**.
Friendly ramming DID damage in the port (no `ReceiveDamage` FF gate; the
lifepod-only collision exemption did not cover non-pod teammates). P549 ports the
native FF gate; the collision damage row is now genuinely MATCH. Additionally,
row "Kill credit attribution … | DIVERGES / MISSING" (line 43) is now **FIXED**
by P549 (DamageTrack ported; credit = top damage dealer).

**P538 — `docs/reports/P538_FLIGHT_AUDIT.md` (dated correction, 2026-10-06):**
item #2 (missile `readyTime` activation delay, line 28) and item #3 (warp-bomb
`c_eabmWarpBomb` capability gate, line 29) are **both FIXED** by P549 (severity
confirmed MED, as audited). Item #6 (warp-bomb dual-end explosion) remains
LOW / out of scope.
