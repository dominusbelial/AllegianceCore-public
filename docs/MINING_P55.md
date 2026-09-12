# P55 — real asteroid mining (part 1: the ore, not the sale)

Branch: `master` tip `35eed05` (DOCKED_NOTIFICATION_LIVE) — verified
`git branch --show-current` prints `master`, `git log --oneline -1` = `35eed05`.

This task lands the first half of native mining: a ship can park beside a
depletable asteroid and accumulate ore, with correct wire visibility. The sell
step (ore → credits) is deliberately **not** here — it is the separate P56 task
(see *What remains*). Scope per the brief: no crowding, no per-side
attribute scaling, no unload variants, no native-repo (`Allegiance`) changes,
no VM involvement.

---

## What was built

| Piece | Where |
|---|---|
| Runtime `Asteroid` model (stateful counterpart to static `DataAsteroidIGC`: position + depletable ore pool, nothing else) | `src/Allegiance.Core/Sim/Asteroid.cs:59` |
| Sector mining pass (accrual / depletion / cap / pool, per-sector tick) | `src/Allegiance.Core/Model/Sector.cs:529` (`ResolveMining`) |
| Flat-rate/capacity/regen constants | `Sector.cs:211,225` (`MiningRate`, `He3Regeneration`), `Ship.cs:208` (`MineCapacity`) |
| `Ship.Ore` / `Ship.IsMining` | `Ship.cs:195,219` |
| Synthetic asteroid injection (`Mission.PlaceAsteroid` → `Sector.AttachAsteroid`) | `Mission.cs:381`, `Sector.cs:89` |
| Wire the real `MineAsteroidEvent` (server-side seam → host) | `ServerHost.cs:147`, `Server.cs:196` |
| Ore on the existing ship-fractions wire byte | `ServerShipUpdate.cs:83,127` |

### 1. Runtime `Asteroid` model — `Sim/Asteroid.cs`
Mirrors the static-vs-runtime split P53 used for stations (static
`DataStationIGC` + docked state on the ship): it wraps a
`DataAsteroidIGC` record and carries exactly the two pieces of runtime state
mining needs — the depletable pool (`OreRemaining`, seeded from the record's
`Ore`) and a last-reported-ore watermark that throttles the seam. Implements
`IAsteroidIgc` (`IgcMarkers.cs:18`), so it is passed verbatim as the
`MineAsteroidEvent` argument. Deliberately **not** a `Model` (does not join the
models loop); mining detection and regeneration are sector passes.

### 2. `Ship.Ore` — `Ship.cs:195`
Plain `float`, starts at 0. Unlike `StationId` (nullable — "not docked" is a
meaningful absence), a cargo quantity has no "absent" state: `0` already
encodes empty. Mirrors native `m_fOre` (`shipIGC.h:2634`). `Ship.IsMining`
latches on the first satisfied tick so the start-mining log fires once per
engagement. `Ship.MineCapacity = 150.0f` is a flat constant — see *Constants*.

### 3. Mining detection — `Sector.ResolveMining` (`Sector.cs:529`)
Arrival-triggered, not command-gated — the exact P53 docking precedent. The
brief's stated native gate is reproduced faithfully:

```
(distance² < radius²) && (velocity² < 1.0),  radius = asteroid.Radius + ship.Radius + 30.0f
```

(`shipIGC.cpp:1957-1961`, adjacency from `shipIGC.cpp:1955`). Because this
server does not track client-issued high-level commands server-side (P53's
documented simplification), the trigger is purely geometric against *any*
minable rock — no `c_cidMine` target needed. Per tick:
`minedOre = dt·MiningRate`, clamped by `Ship.MineCapacity`, then drawn from the
rock via `Asteroid.MineOre` which returns the actual (≤ requested) amount
(`shipIGC.cpp:2211-2247`). One rock per ship per tick (`break`).

**No crowding, no side gate** (explicit scope): any number of ships may mine
the same rock; asteroids are unowned, so no friendly-only filter. This is a
documented simplification, not hidden behaviour.

### 4. Wire
Two channels, no new wire type:
- `ServerShipUpdate` byte 45 — the **already-reserved** ore fraction slot
  (`shipIGC.h:1125,1148` `SetOre(capacity, current)`; the port had it as
  `OreFraction => CompactQuant.DePercent(_b[45])` but `FromModel` hard-coded
  0). `FromModel` now fills it from `Ship.Ore / Ship.MineCapacity`
  (`ServerShipUpdate.cs:127`) — the same "rides an existing message" pattern
  P53 used for `StationId`/`ParentId`. No new field was needed: it sat
  reserved-but-unused, exactly like P53 predicted.
- `MineAsteroidEvent` site seam — raised in-loop on the sim thread
  (`Sector.cs` in the accrual branch), throttled to the native `> 3.0`
  ore-change threshold (`asteroidigc.h:246-251`, enforced inside
  `Asteroid.MineOre`). `ServerHost` overrides it (`ServerHost.cs:147`) and
  `Server` subscribes a logger (`Server.cs:196`). The host does **not**
  broadcast a new message — byte 45 already carries the ore; the seam is the
  "engine observed mining" callback the reference host would relay/score.

### 5. Logs (bracketed tag style)
- `[mine] ship N began mining asteroid ...` on the start transition (`Sector.cs` start branch).
- `[mine] ship N is full (.../... ore) — mining paused` on cap (`Sector.cs` cap branch).
- `[mine] asteroid N ... tapped: yielded X of Y requested` on pool-limited yield (`Sector.cs` tap branch).
- `[mine-event] asteroid ...` from the host (`Server.cs` `OnMine`).

---

## Constants — labelled simplifications

| Constant | Value | Stands in for | Native source / why a literal |
|---|---|---|---|
| `Sector.MiningRate` | `100.0` ore/s | `dT · ga.GetAttribute(c_gaMiningRate)` | `shipIGC.cpp:2212`. `c_gaMiningRate` is a per-side tech attribute index (`igc.h:611`); its default lives in core data this port doesn't decode. Picked large so tests reach assertions in a few ticks. |
| `Sector.He3Regeneration` | `0.025` | `c_fcidHe3Regeneration` in `dOre = dT·regenConst·(oreMax−ore)` | `asteroidigc.h:47-56`. Mission float-constant index (`igc.h:75`) set from core data. Picked small so a drained rock is observably held near-empty (the "pool limits mining" behaviour). |
| `Ship.MineCapacity` | `150.0` | per-side `GlobalAttributeSet`-scaled capacity | native capacity is attribute-scaled; flat literal sized so a synthetic test reaches it in `150/(0.25·100)=6` ticks. |

None of these is claimed as a verified native value — each is a plainly-labelled
stand-in (the actual defaults live in `.igc` core data / per-side attributes
this port does not decode).

**Regeneration / depletion choice:** native asteroids **do not deplete-forever**;
`CasteroidIGC::Update` (`asteroidigc.h:47-56`) continuously regrows a
He3-capable rock toward `oreMax` at a rate proportional to its deficit. This
port reproduces that formula (with the flat regen constant). Consequence,
inherited faithfully: a parked miner is *never* permanently starved — "mining
stops when the pool hits zero" manifests as the pool limiting the per-tick
yield (`Asteroid.MineOre` returns less than requested), i.e. ore accrual
collapses to the regen trickle rather than a hard stop. No respawn mechanic
beyond this continuous regrowth (task's "does not respawn" option was rejected
because native demonstrably regenerates; the regrowth *is* the native answer).

---

## What's proven (all 3 consecutive clean runs: 2255/2255, then re-confirmed)

Loopback **without a VM**, on a **synthetically injected** asteroid (the demo
map `XXV_simple.igc` has **zero** asteroids — its own boot summary sums
`Sector.Asteroids` to 0, verified, not a display bug). Tests:
`tests/Allegiance.Core.Tests/MiningTests.cs`.

1. **Accrual** — a ship parked on a rock accrues Ore linearly in dt
   (`Near_rock_accrues_Ore_linearly_and_rock_pool_depletes_in_lockstep`): 3
   ticks at 25 ore/tick ⇒ ~75 ore.
2. **Depletion** — the rock's pool shrinks in lockstep with the ship's Ore
   (asserted both unit- and TCP-level), within regeneration drift.
3. **Cap halts mining** — Ore reaches exactly `MineCapacity` then holds flat;
   the `MineAsteroidEvent` stream stops (no continuous emission once full)
   (`Cap_hits_and_accrual_halts_until_drained`).
4. **Pool gates yield** — a small rock (80 < 150 capacity) caps accrued ore
   well below capacity; post-tap accrual collapses to the regen trickle
   (`Small_pool_gates_yield_and_accrual_collapses_to_regen_trickle`).
5. **Wire visibility** — real TCP bot decodes a non-zero `OreFraction` (byte
   45) matching server `Ore/MineCapacity`, after nudging the parked ship's
   position to force the dirty fmid-78 broadcast
   (`Loopback_synthetic_rock_mines_and_ore_reaches_the_wire`).
6. **No regression** — a ship nowhere near any rock (unit and TCP) accrues 0
   ore, is never flagged mining, emits no seam calls, keeps broadcasting normal
   flying `SHIP_STATUS(90)` (StationID NA / State Flying)
   (`Ship_far_from_any_asteroid_is_unaffected`, `Loopback_default_spawn_is_unaffected_and_statuses_stay_normal`).
7. **Codec** — `FromModel` fills byte 45 and it round-trips
   (`FromModel_fills_byte45_ore_fraction_from_ShipOre`).

Verified via: unit `RecordingIgcSite` mission (deterministic `0.25` steps, no
TCP) + real loopback TCP `Bot` on `XXV_simple.igc` with a rock injected through
`Mission.PlaceAsteroid` inside `Server.ShipSpawner` (same placement-control
pattern as the dock/warp suites).

---

## What remains (next work — be aware)

- **P56 — selling ore (the deliberate follow-up).** Real finding from the
  brief: the native sell path is literally another `PaydayEvent` call —
  `GetIgcSite()->PaydayEvent(side, m_fOre * value * yield)` then zero
  `m_fOre`, both at an unload station and at a refinery teleport-unload
  variant (`shipIGC.cpp:1969-1990`). So P56 reuses the exact seam P51 already
  wired — genuinely small — but it is not this task's, so **a live full
  mine-then-sell loop is not yet provable** after this task lands. Also still
  needs the ore→credits value constant / yield (attribute-scaled natively).
- **Real live-VM pass.** Not possible yet on the current demo map: `XXV_simple`
  contains **zero asteroids**, so a native client pointed at a live server can
  fly, dock, payday — but has nothing to mine. Whoever picks up the live pass
  must first either swap the map for one with asteroids or add asteroid data to
  the current map. Until then the synthetic-asteroid loopback is the only
  viable verification path. Flagged here so the live pass isn't a surprise.
- Out of scope and un-implemented (documented, not hidden): crowding/contention
  between ships on one rock; per-side attribute-scaled rate/capacity/yield; the
  station vs. refinery unload variants; any respawn beyond native regrowth.

---

## Session stats

`python3 ~/Development/allegiance-tools/session_stats.py 20260902_154907_40e7b4`
→ `20260902_154907_40e7b4 start=15:49:15 msgs=129 tool_calls=72 api_calls=57 tokens_in=6,376,293 tokens_out=56,491` (dur "still running" — this session).
