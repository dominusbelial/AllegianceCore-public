# Kill Bonus damage multiplier — P278

Kill Bonus (KB): a live per-ship kill-streak that scales a pilot's OUTGOING
weapon damage AND repair output, ported into `AllegianceCore` from native
FreeAllegiance. Distinct from `PlayerScore.CombatRating` (the P215 Elo-style
session rating) — that is a persistent-feel stat; this is a combat-damage
modifier. This stage makes the mechanic REAL: kills are now consumed by the
actual damage/repair application path.

## Native citation (authoritative source, verified in the reference tree)

The modern FreeAllegiance source names the KB counter "experience"
(`CshipIGC::m_experience`) and exposes it through a multiplier accessor.

- Counter field: `CshipIGC::m_experience` — `src/Igc/shipIGC.h:2610`.
- Seed in constructor: `SetExperience(1.0f)` — `src/Igc/shipIGC.cpp:52`;
  `Reset(bool bFull)`: `m_experience = bFull ? 1.0f : 0.0f` —
  `src/Igc/shipIGC.h:496`.
- Increment: `AddExperience()` → `m_experience += 1.0f` — `src/Igc/shipIGC.h:1829`.
- Multiplier: `GetExperienceMultiplier()` —
  `src/Igc/shipIGC.h:1812-1817`:

      const float c_maxBonus = 0.5f;
      const float c_halfExperience = 4.0f;
      return 1.0f + c_maxBonus * m_experience / (m_experience + c_halfExperience);

  i.e. `1 + 0.5·c/(c+4)`, asymptotically capping at `1.5` (= +50%).
- Applied to DAMAGE AND REPAIR, once, before the repair/damage branch, only when
  the launcher is a ship:
  - ship target: `src/Igc/shipIGC.cpp:1238-1242`
    ("Student 8/3/2022 applying experience modifier (kb) earlier to apply to
    repairs");
  - station target: `src/Igc/stationIGC.cpp:142-145`;
  - asteroid target: `src/Igc/asteroidigc.h:139`; missile `missileigc.h:105`;
    probe `probeigc.h:99`.
- Kill credit: in the kill-handling block,
  `src/FedSrv/FedSrv.CPP:11824` —

      if ( (pside != psideCredit) && !pside->AlliedSides(pside,psideCredit) )
          ((IshipIGC*)pcredit)->AddExperience();

  (comment: "#ALLY: kgjv dont up KB when FF is on - Imago 7/3/09") — note KB
  does NOT rise on an allied cross-side kill, a stricter gate than the plain
  cross-side `AddKill`.
- Reset on real death: `src/FedSrv/FedSrv.CPP:11783-11787` —

      bKilled = (!pmp->bEjectPods) || bLifepod || (amount < 0.0f);
      ...
      if (bKilled)
          pship->SetExperience(0.0f);

  Under this port's always-enabled eject pods (`bEjectPods`), a player ship is
  `bKilled` only when it IS a lifepod (or `amount<0`, unreachable in combat), so
  destroying the pilot's eject pod resets the streak to 0 — exactly the AFS
  trigger ("if your own pod is destroyed"). An ejection (the `!bKilled` branch)
  deliberately does NOT reset it.

## Relationship to the wiki / discrepancy

The AFS wiki (`freeallegiance.org/FAW/index.php/AFS/Pods_and_Killbonus`) states
`KB = 50 × kills/(kills+4)`, starting at 10 after your first kill, capping at 50
and (mis)labeled "+150% damage output".

- Native's formula is structurally IDENTICAL: its `0.5·c/(c+4)` is exactly
  `50·c/(c+4)` percent — so `multiplier = 1 + KB/100` with `KB = 50·c/(c+4)`,
  cap 50.
- The wiki's "+150%" is a MISSTATEMENT — the cap is +50% (factor 1.5), not
  +150%.
- The wiki's "starting at 10 AFTER your first kill" implies 0 kills → 0 bonus.
  Native's ctor seeds `m_experience` at `1.0` (`shipIGC.cpp:52`), which would
  give a brand-new ship an immediate 10% tier. **SEED DIVERGENCE (documented):
  this port seeds the counter at 0** — the kill count — matching the wiki's
  "after your first kill" framing and the task's "1 kill → 10 ... reset to 0"
  verification. A 1.0 seed would have given every fresh weapon shot 1.1× and
  broken ~24 established exact-damage tests (forbidden by the task's baseline
  requirement). The formula, the +1-per-kill increment, and the reset-on-real-
  death trigger are ported faithfully from native; only the construction seed
  differs.

## Implementation (production)

1. `src/Allegiance.Core/Sim/Ship.cs` — per-ship Kill Bonus state (native
   per-ship scope), the exact native multiplier, and the increment/reset:
   - `KillBonus` (raw streak counter, seeded 0),
   - `KillBonusPercent` = `50·c/(c+4)` capped at 50 (the KB readout),
   - `ExperienceMultiplier` = `1 + 0.5·c/(c+4)` (native `GetExperienceMultiplier`),
   - `AddKillBonus()` (native `AddExperience`) and `ResetKillBonus()`
     (native `SetExperience(0)`).
2. `src/Allegiance.Core/Sim/Ship.cs::ReceiveDamage` — `if (launcher is Ship ls)
   amount *= ls.ExperienceMultiplier;` placed before the repair/damage branch
   (native shipIGC.cpp:1238-1242), so BOTH weapon damage and nanite repair scale.
3. `src/Allegiance.Core/Sim/Station.cs::ReceiveDamage` — the same multiplier on
   station targets (native stationIGC.cpp:142-145); a station was already
   nan-repairable (P277), so the repair leg is live.
4. `src/Allegiance.Server/Server.cs::BroadcastKillShip`:
   - reset on real death: `if (bKilled) victim.ResetKillBonus();` right after the
     P107 `bKilled` gate (native FedSrv.CPP:11783-11787) — pod destroyed → 0;
   - kill credit: `if (!AlliedSides(...)) attackingShip.AddKillBonus();` in the
     P215 kill-credit block (native FedSrv.CPP:11824), gated on the stricter
     non-allied test, on the credit SHIP (per-ship scope, independent of peer).

`PlayerScore.CombatRating` and its Elo formula are untouched (separate mechanic,
living alongside). The nanite gun itself (P277) is unchanged — this wires KB into
the repair path it already uses.

## Verification

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Debug` → **0 warnings, 0 errors**.

### Full test suite
`dotnet test tests/Allegiance.Core.Tests -c Debug`, 3 consecutive runs →
**3143 passed each time; the only failure is the pre-existing, documented
`LiveTurretGunnerBindingTests` machine-state flake** (zero refs to P278 symbols,
fails on the pristine tree too). New total 3144 (3130 pre-existing + 14 new:
10 pure + 4 live).

### Live proof (real server + real bots over loopback TCP)

`KillBonusLiveTests`:

- Sequential real kills (server `BroadcastKillShip` credit path) climb the
  shooter's KB per formula — "1 kill → 10", approaching but never exceeding 50:
  ```
  shooter starts at KB 0
  after kill #1: KB counter=1 KB%=10      (expect 10)
  after kill #2: KB counter=2 KB%=16.67   (expect 16.67)
  after kill #3: KB counter=3 KB%=21.43   (expect 21.43)
  after kill #4: KB counter=4 KB%=25      (expect 25)
  ```
- Real end-to-end projectile kill: `real projectile kill: KB 0 -> 1 (10%),
  PlayerScore.Kills=1` — the whole chain (real projectile → ReceiveDamage →
  KillShipEvent → BroadcastKillShip credit) both banks the kill and bumps KB.
- Real damage scaled by KB, same target/armor matrix, 0 vs non-zero KB:
  `real-core hit drop KB0=0.015000 vs KB1=0.018750 (ratio 1.250002, multiplier
  1.25)` — a 4-kill streak dealing exactly 1.25× the damage per hit.
- Own pod destroyed resets the streak:
  `own pod destroyed -> KB reset to 0 (multiplier 1)` — a lifepod hull killed by
  positive damage is `bKilled` (bLifepod) → `ResetKillBonus()`.

### Must-fail-if-removed (each wiring proven discriminating)
- Removing the `AddKillBonus()` credit → both KB-climb live tests red.
- Removing the `Ship.ReceiveDamage` multiplier → both damage-scaling tests red.
- Removing the `ResetKillBonus()` on `bKilled` → the pod-reset test red.

## Out of scope (unchanged)
- `PlayerScore.CombatRating` Elo formula.
- The nanite gun weapon itself (P277) — already landed; KB is wired into the
  repair path it shares (Ship/Station ReceiveDamage negative-amount branch).
- Native reference source (`~/Development/Allegiance`) — read-only, untouched.
