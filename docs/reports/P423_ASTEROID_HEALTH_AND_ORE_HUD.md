# P423 — Targeted asteroids show a health bar and a real He3/ore-level readout

## Summary

Allegiance's original client shows two real, distinct readouts when a player
targets an asteroid: a generic health bar (any *damageable* object) and a
separate He3/ore-content readout that only exists for `OT_asteroid` targets.
This port had both gaps: (1) its `HudRenderer` target readout was wrongly
restricted to ships based on a documented-but-false premise about native's own
gate, and (2) **no live per-asteroid ore/fraction data reached the MonoGame
client over the wire at all**. This stage fixes the wire decode, broadens the
health-bar gate to any damageable target, and adds the distinct asteroid ore
readout.

**Everything below is display-only.** No mining/simulation behaviour changed.

---

## 1. The real wire — ASTEROIDS_UPDATE(179) already carries the data

The task asked to *trace* whether the real wire protocol already carries live
per-asteroid depletion. The answer is **yes — and this port's server already
sends it every cadence tick; the MonoGame client just never decoded it.**

- `FM_S_ASTEROIDS_UPDATE` (179), defined at `src/Igc/Messages.h:917-928`,
  is the per-side periodic sync. Each `AsteroidState` record carries:
  - `ore` — the rock's **raw current ore pool**: `pas->ore =
    short(pasteroid->GetOre())` (`fsship.cpp:909`), the same pool mining drains
    (`MineOre`, `asteroidigc.h:221-233`).
  - `bpFraction` — the rock's **structural health fraction**:
    `pas->bpFraction = pasteroid->GetFraction()` (`fsship.cpp:911`), the
    damage pool (`asteroidigc.h:172-174`).
  - These two fields are genuinely **not redundant**: `ore` is remaining
    resource content; `bpFraction` is structural integrity. Native's
    `ModelData::GetOre` reads the former (ore fraction), its
    `ModelData::GetPercentHitPoints` reads the latter (health).
- The port's server already broadcasts this each cadence tick per-side,
  baking the live sim values in:
  `Server.BroadcastAsteroidsUpdate` (`src/Allegiance.Server/Server.Broadcast.cs:1247-1296`)
  → `AsteroidsUpdate.FromAsteroids` (`src/Allegiance.Core/Wire/AsteroidsUpdate.cs:205-215`),
  reading live `Asteroid.OreRemaining` / `Asteroid.Fraction`.
- **Gap found:** the MonoGame client's `ConnectionSession` receive switch had
  **no `case AsteroidsUpdate.Fmid`** and `ClientWorld` had no
  `Apply(AsteroidsUpdate)` — the wire data was decoded only by `Allegiance.Bot`
  (`Bot.cs:1772`), never by the game client. `ClientWorld`'s `Asteroids` table
  held only the *static* `DataAsteroidIGC` definition (from
  `EXPORT(26)/OT_asteroid`), never the running condition.

### What this stage wires

- `ConnectionSession.cs` now dispatches both `AsteroidsUpdate(179)` and
  `StationsUpdate(57)` into the world.
- `ClientWorld` gained live per-asteroid state
  (`AsteroidLiveState{OreFraction, Fraction}`) and live per-station state
  (`StationLiveState{HullFraction, ShieldFraction}`), populated by
  `Apply(AsteroidsUpdate)` / `Apply(StationsUpdate)`.
  - The wire carries raw `ore`; the **ore fraction** is derived against the
    static `DataAsteroidIGC.OreMax` (from EXPORT), matching the sim's own
    `Asteroid.OreFraction = OreRemaining/OreMax` (`Asteroid.cs:99`).
  - The health fraction is the div-240 BytePercentage (`CompactQuant.Percent`,
    `Utility.h:1342-1358`).
- Stations were included because a station is a real damageable target with an
  armour pool — `STATIONS_UPDATE(57)` already carries `bpHullFraction` /
  `bpShieldFraction` (`StationsUpdate.cs`, from `Station.ArmorFraction` /
  `Station.ShieldFraction`), and the server broadcasts it per-side
  (`Server.Broadcast.cs:1160-1209`).

---

## 2. HUD gate fix — the health bar is NOT ship-only

The old `HudRenderer.DrawTargetSelectionBracket` rendered the health bar only
inside `if (v.CharacterName is { })`, with a comment claiming this "matched
native's own 'if it is a ship' gate". That premise is **false**. Native's real
function, `ModelData::GetPercentHitPoints` (`WinTrek/consoledata.cpp:202-221`),
gates only on:

```cpp
if ((pmodel->GetAttributes() & c_mtDamagable) == 0)
    return 1.0f;   // not damageable -> full (no bar)
```

— a generic "is this object damageable" check independent of object type.
Asteroids (`c_mtSeenBySide`, `asteroidigc.cpp:36`) and stations are damageable,
so native genuinely draws this bar for a targeted asteroid or station too.

**Fix:** `ComputeTargetSelectionBracket` (`Game1.Camera.cs`) now populates the
fractions for every damageable target kind:
- `TargetKind.Ship` → hull/shield from the ship's `ServerShipUpdate` (unchanged).
- `TargetKind.Asteroid` → structural `Fraction` (health bar) + `OreFraction`.
- `TargetKind.Station` → hull/shield from the live station sync.

`HudRenderer.DrawTargetSelectionBracket` now draws the generic hull/shield bars
whenever the corresponding fraction is present (any damageable target), while a
**pilot name** still renders only for a ship (only a ship has a pilot).

---

## 3. The distinct asteroid ORE readout

Native has a **second, separate** readout for asteroids only:
`ModelData::GetOre` (`WinTrek/consoledata.cpp:449-460`) is gated specifically on
`OT_asteroid` and returns the rock's real remaining ore
(`GetAsteroid()->GetOre()`), a distinct UI data-binding from the generic
hit-points fraction. This is the "level of He3" Domingo asked for.

`DrawTargetSelectionBracket` now renders, when `OreFraction != null` (asteroid
target only), a labelled **`ORE <n>%`** row plus its own `DrawBar` beneath the
generic health bar — reusing the exact `DrawBar` fraction primitive the
existing health bars use, so it is visually consistent with the rest of the HUD.
For a ship target `OreFraction` is null, so nothing changes there.

---

## 4. Verification

### Automated tests — `tests/Allegiance.ClientCore.Tests/ClientWorldLiveAsteroidOreTests.cs`

Added 5 tests (all pass):

1. `Decoded_Ore_Fraction_Matches_Real_Server_Asteroid_After_Partial_Mine` —
   builds a **real** sim `Asteroid`, mines it partway via `MineOre`
   (1000→600 = 0.60), packs it with the exact `AsteroidsUpdate.FromAsteroids`
   the server's broadcast uses, routes it through `ClientWorld`'s dispatch, and
   asserts the decoded `AsteroidLive` ore fraction equals the sim's live
   `Asteroid.OreFraction`.
2. `Depleting_More_Decreases_Client_Ore_Fraction` — two successive updates at
   increasing depletion show the client ore readout falling 0.80 → 0.20.
3. `Decoded_Health_Fraction_Uses_Div240` — the div-240 BytePercentage health
   decode (240→1.0, 120→0.5, 60→0.25).
4. `Entity_Without_Export_Still_Records_Health_But_Zero_Ore_Fraction` —
   the health fraction survives before the static EXPORT (OreMax) arrives;
   ore fraction is guarded (0) when OreMax is unknown.
5. `Station_Live_Fractions_Decoded_From_StationsUpdate` — station hull/shield
   decode for the station health bar.

**Must-fail proven:** with the `case AsteroidsUpdate` dispatch in
`ClientWorld.Apply(IFedMessage)` neutralized, 4/5 tests go red; restored after.

### Live proof — `tools/asteroid-ore-proof` (real in-process server + real wire + real HudRenderer)

Harness boots a real `Allegiance.Server` on `m2xspiderw.igc` (134 asteroids),
injects the spawner so the pilots sit in the asteroid sector, joins a real
`ConnectionSession`, then over the **real wire**:

- The rendering client received **133 live asteroids** via
  `ASTEROIDS_UPDATE(179)` — `ClientWorld.AsteroidLive` populated (proves the new
  dispatch works end-to-end).
- It mined a real in-mission sim asteroid partway through the real
  `Asteroid.MineOre` path, pumped the wire, and observed the client readout
  track the server's real value **exactly**:

  ```
  server OreFraction 1.000 -> 0.400 -> 0.120
  client readout      1.000 -> 0.400 -> 0.120   (DECREASED each step)
  ```

- It rendered the **real `HudRenderer`** offscreen and vision-checked:
  - `asteroid_ore_partial` → health bar + **`ORE 40%`** readout
  - `asteroid_ore_depleted` → **`ORE 12%`** readout (bar visibly shrank)
  - `ship_health_regression` → pilot name `Target_Enemy` + health bars, **no
    ORE label** — health bar still works exactly as before.

Pixel measurement of the ORE-fill bar in the two asteroid frames: **26px wide
at 40% → 7px wide at 12%** (the DrawBar inset predicts ~27 and ~8 px), an
airtight numeric that the readout tracks real ore depletion; the ship frame has
no amber ORE fill at all.

Evidence: `.verify_p423/p423_asteroid_ore_{partial,depleted}.png`,
`.verify_p423/p423_ship_health_regression.png`.

### Build / suite

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full suite run **twice** (all four test assemblies), with every failure
  re-verified in isolation / against the pristine tree:
  - `Allegiance.ClientCore.MonoGame.Tests` — **826/826 green both runs**.
  - `Allegiance.Lobby.Tests` — **12/12 green both runs**.
  - `Allegiance.ClientCore.Tests` — 234 pass; the 3 failures (ShipDeleteGone,
    JoinFlow.EntityTable, FullSessionKill) are pre-existing live-wire flakes:
    two fail identically on the **pristine** tree (stash), one passes in
    isolation. The 5 new P423 tests pass consistently (16 ms).
  - `Allegiance.Core.Tests` — 3505–3507 pass of 3511; the 4–6 failures are the
    documented live-wire timing set (LiveTurretGunner machine-state flake,
    MultiMissionDisconnect environmental, plus intermittent LiveNetwork
    races) — each of the specific failing tests passes in isolation (5/6) or is
    the documented persistent machine-state flake (LiveTurretGunner, fails on
    the pristine tree too).
  - No P423 change touches any failing test's code path.

---

## Scope boundary

- **Display only.** No mining/simulation behaviour changed.
- P368–P422 untouched.
- Warp targets remain non-damageable (they carry no fraction on any wire
  message) — the health bar correctly stays absent for a warp target.
