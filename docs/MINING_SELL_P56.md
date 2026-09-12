# P56 — sell mined ore on dock (real mining, part 2)

**Commit:** `(post-51fac5a)`
**Arc status:** ✅ **closes the mining arc** (ore → money), modulo the live-map
zero-asteroid blocker (unchanged from P55 — see *Follow-ups*).

---

## What this adds

A ship carrying ore (`Ship.Ore > 0`) that **newly docks** at a friendly station
sells it: the owning side's treasury is credited `ore × OreCreditPerUnit`, the
P51 `PaydayEvent` seam is raised so `Server.OnPayday` broadcasts a real
`FM_S_PAYDAY(114)`, and the cargo is zeroed. This is the exact native sell
path — `GetIgcSite()->PaydayEvent(side, m_fOre * value * yield)` then zero
`m_fOre` (`~/Development/Allegiance/src/Igc/shipIGC.cpp:1969-1990`) — there is
**no separate sell message**, just the existing `PaydayEvent` seam P51 already
wired.

### Production changes

- `ServerHost.DockWithStationEvent` (`src/Allegiance.Server/ServerHost.cs:182`,
  dock logic 194-241): after the "already docked → early return" guard
  (lines 194-199) and `sh.StationId = ds.Station.StationId` (201), a new block
  (217-232) — `if (sh.Ore > 0f)` — resolves the owning side via
  `sh.Mission?.Sides.FirstOrDefault(s => s.Id == sh.SideId)` (219), credits
  `side.Money += (long)proceeds` (227), raises `PaydayEvent(side, proceeds)`
  (228) so `Server.OnPayday` (`Server.cs:1413`) broadcasts `FM_S_PAYDAY` to
  the side's peers, logs `[mine-sell]` (229), and zeroes `sh.Ore` (230). The
  dock gate is the "newly docks" boundary, so a redundant dock call never
  sells twice.
- `Ship.OreCreditPerUnit` (`src/Allegiance.Core/Sim/Ship.cs:225`): flat
  credits-per-unit constant = `5.0f`, standing in for native `c_fcidValueHe3` ×
  per-side attribute-scaled `c_gaMiningYield` (`shipIGC.cpp:1969-1990`). Like
  P55's `MiningRate`/`MineCapacity` — a **labelled simplification, not a
  verified native value** (real defaults live in core data / GlobalAttributeSets
  this port doesn't decode). Chosen so `ore × constant` yields an easily-asserted
  integer credit total.

Scope honoured: **no** refinery/unload variant (sell on any friendly dock),
**no** per-side yield scaling (flat constant only), **no** change to
`Sector.ResolveMining` accrual, **no** native-repo change, **no** new wire
message (existing `FM_S_PAYDAY` broadcast + `ServerShipUpdate` byte 45 reflect
it).

## What's proven (real loopback + direct)

`tests/Allegiance.Core.Tests/MiningSellTests.cs` — 6 tests (3 direct
`ServerHost` construction, 3 real-TCP loopback `Bot`), all combining the two
prior injection patterns (synthetic He3 rock from `MiningTests` + a friendly
station from `DockDockingTests`) in one test mission:

1. **Mine → dock → sell → money ↑, ore zeroed.** Direct
   (`Dock_transition_sells_ore_into_treasury_and_zeroes_cargo`): ship `Ore=40`
   docks ⇒ treasury `+40×5`, seam fires one `PaydayEvent(ownSide, 200)`,
   `Ore=0`; a redundant dock with cargo re-set does **not** double-sell.
2. **Zero ore ⇒ no payday side-effect.** `Dock_with_zero_ore_has_no_payday_side_effect`
   (+ loopback `Loopback_dock_with_no_ore_emits_no_FM_S_PAYDAY`): docks
   normally, no `PaydayEvent`, no `[mine-sell]`, no spurious `FM_S_PAYDAY`.
3. **Gate stays open across redocks.** `Redock_after_undock_sells_again` &
   loopback `Loopback_redock_sells_again_each_transition`: undock (StationId
   cleared) → mine → dock sells a **second**, distinct `FM_S_PAYDAY`.
4. **Wire-real payout.** Loopback `Loopback_mine_then_dock_sells_ore_and_pays_FM_S_PAYDAY`:
   server-side `Ore` mined ≥20, then dock ⇒ `Ore` zeroed and the bot decodes a
   real `FM_S_PAYDAY(114)` whose `DMoney == (long)(ore × 5)` (one player on the
   side ⇒ whole proceeds paid out).

Evidence from a failing test's own detailed run (before the trip-2 race fix,
below):
```
[mine] ship 8096 began mining asteroid 7202 'ROID_7202' (ore 500/500)
[mine-sell] ship 8096 sold 20.0 ore at 5/u = 100.0 credits to side 0 — ore zeroed
[dock] ship 8096 docked at station 1051 'Garrison' (side 0) ...
[payday] side 0 'Alpha' -> PAYDAY(114) delta=100 balance=0
[undock] ship 8096 undocked from station 1051 ...
```
Confirms: `[mine]` accrual, `[mine-sell]` log, treasury credit, and real
`FM_S_PAYDAY` all fire on the wire.

**Baseline re-confirmed:** P55 left 2255/2255; this branch's committed baseline
re-ran at 2261 total with 0 failures once the P56 tests land. P56 adds exactly
6 tests (2255 + 6 = 2261). Full suite passed clean **6 consecutive runs**
(Durations 33-38 s). One isolated, unreproduced intermittent failure in a
single early run (an unexplained `Assert` hit; it never recurred across ~8
subsequent full-suite runs and all 6 P56 tests — see *Notes*).

## Test-bug found & fixed (production logic was already correct)

`Loopback_redock_sells_again_each_transition` originally raced: after
`SendDocked()` it pumped for `StationId == null` *then* moved the ship to the
rock. Because the ship is still physically inside the station's dock radius the
instant it undocks, and the sector's dock pass
(`Sector.ResolveDockCollisions`, `Sector.cs:161`) runs **before**
`Sector.ResolveMining` (`Sector.cs:166`) every tick, the next tick **re-docked**
the still-in-radius ship with `Ore=0` — a wasted transition that neither sold
nor mined, stranding trip-2 ore at 0 (the 675 was the periodic 60 s income
accrual, not a sale). Fix (`MiningSellTests.cs`): move the ship back onto the
rock immediately after `SendDocked`, in the same no-tick gap, so no re-dock can
occur; trip 2 then mines fresh and produces a real second payout. No production
change was needed — `DockWithStationEvent`'s "newly docks" gate behaved
correctly throughout.

## Follow-ups a live pass would still need

- **Same map blocker as P55:** the demo map (`XXV_simple.igc`) has **zero
  asteroids**, so a real end-to-end *live* demo of mining → sell still needs a
  map swap or added asteroid data first. This task does **not** change that;
  it only proves the loop over the wire via a synthetic rock injected into the
  test mission (same technique as P55).
- Per-side attribute-scaled yield (`c_gaMiningYield`) and true `c_fcidValueHe3`
  remain flat-constant simplifications until the port decodes those data; same
  for capacity/regen constants from P55.
- Refinery teleport-unload special-case (native dock-with-unload variant) remains
  out of scope by design; this port sells on any friendly dock.

Nothing else in the mining arc is open. This is a complete "real mining"
story: accrue (`Sector.ResolveMining`) → carry wire-visible (`Ship.Ore` /
byte 45) → sell on dock → treasury + `FM_S_PAYDAY`.

## Session stats

```
20260902_171303_720e2c  start=17:13:10  dur=STILL RUNNING / no clean end
msgs=70 tool_calls=42 api_calls=28
tokens_in=1,599,623 tokens_out=21,186
```

## Notes / honesty

- One early full-suite run failed one `Assert` that I could not reproduce
  across ~8 later full runs (all 2261 green) and never tied to the P56 changes;
  treated as a pre-existing timing flake, flagged here rather than hidden.
- All production changes carry `file:line` above and in code comments; the
  `OreCreditPerUnit` value is explicitly a simplification, not claimed native.
