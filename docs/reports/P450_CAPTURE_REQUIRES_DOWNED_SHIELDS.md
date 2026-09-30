# P450 — Station capture requires downed shields

## Summary

Gameplay-parity fix: in native Allegiance, a hostile troop transport
(`c_habmBoard` hull) can only **capture** a station when the base's shields are
DOWN. This port's hostile-capture branch (`Sector.cs`'s dock pass) had no shield
gate, so a lone board-capable hull captured a fully-shielded enemy base simply by
reaching its dock. P450 adds the missing shield gate, corrects the misleading
comment that claimed there was no gate, and adds/adjusts the live-loopback tests.

Scope note: this task deliberately does **not** port native's full collision
bounce or collision damage for a boarded-but-shielded base (a separate ramming
pass covers that); the port's existing `continue` rejection stands in for the
bounce.

---

## Root cause (two halves of one native gate)

Native capture is gated in **two places**, and the old port comment had read only
the second:

1. **IGC collision gate** (`shipIGC.cpp:849-851`), in the ship-vs-station
   collision, enemy branch — a hostile board hull reaches
   `DockWithStationEvent` ONLY when the base shields are below
   `c_fcidDownedShield`:
   ```cpp
   else if (m_myHullType.HasCapability(c_habmBoard) &&
            (!GetMyMission()->GetMissionParams()->bInvulnerableStations) &&
            (pStation->GetShieldFraction() < GetMyMission()->GetFloatConstant(c_fcidDownedShield)))
   ```
   Above the threshold the ship falls through to the normal collision
   bounce/damage path (`shipIGC.cpp:719-840`).

2. **Server capture half** (`FedSrv.CPP:13100-13105`): `DockWithStationEvent`
   → `CaptureStation` — re-sides the station. This is the *second* half.

`c_fcidDownedShield` is set in `Igc/missionigc.cpp:2745`
(`= c_downedShield`), literal **0.1f**. The port already uses that exact literal
with the citation at `Ship.cs:64` and `Ship.cs:3873`; P450 reuses the value
verbatim rather than inventing one.

---

## The gate added

`src/Allegiance.Core/Model/Sector.cs`, hostile-capture branch
(`ResolveDockCollisions`):

```csharp
// P450 shield gate (shipIGC.cpp:851): a hostile board hull only
// boards when the station's shields are DOWN (ShieldFraction
// strictly below c_fcidDownedShield). Shields up -> the boarding
// attempt is rejected with the same `continue` fall-through as a
// non-board hull -- no capture, no dock ...
if (rt.ShieldFraction >= DownedShieldFraction) continue;
```

Placed after the runtime-resolution (`rt`) null check and before the capture
seam, so a shielded base rejects the hostile board ship exactly like a non-board
hull: no capture seam raised, no `STATION_CAPTURE`, no dock — the port's
stand-in for native's bounce. Ordering matches native: the shield check precedes
`DockWithStationEvent`/`CaptureStation`.

A shared named constant was promoted with the citation kept:

```csharp
/// The shield "down" threshold c_fcidDownedShield ... (missionigc.cpp:2745),
/// whose literal value is 0.1f -- the same value already used at
/// Ship.cs:64,3873 ... Reused verbatim, not re-derived.
private const float DownedShieldFraction = 0.1f;
```

## Corrected comment

The old comment read only the FedSrv half and asserted "there is NO shield/hull
gate on capture — it is purely a docking-time check". It is replaced with a
comment citing **both** halves — `shipIGC.cpp:849-851` (collision gate) +
`FedSrv.CPP:13100-13105` (capture) — and stating that a shielded base falls
through to the `continue` rejection.

## `bInvulnerableStations` decision

`bInvulnerableStations` (part of native's gate at `shipIGC.cpp:850`) is a mission
param this port does **not** decode. Following the existing precedent already
stated at `Sim/Station.cs:57`, `Sim/Station.cs:239-241` ("default =
vulnerable") and `Sim/ClusterWarnings.cs:15-18`, P450 treats it as its native
shipped default **`false`** (stations vulnerable) and records that in the
comment. No mission-param decoding was added (out of scope).

---

## Tests

### Test seam used to lower the shield

The harness (`StationCaptureLiveTests`) constructs the live runtime `Station`
directly and owns it, so the **real damage path** is reachable in-test:
`Station.ReceiveDamage(...)` (the same sim method the combat pass drives, ported
from `stationIGC.cpp:172`). A neutral, launcher-less hit drains the shield pool
with no amplification (`DamageConstant` returns 1.0 for a null launcher). The
harness base has `MaxShieldHitPoints = 100`, so a single `100f` hit drops
`ShieldFraction` from 1.0 exactly to **0.0** (absorption bottoms out at the
shield pool; armor untouched). A `launcher: null` call avoids the friendly-fire
early-return and any experience multiplier — a pure, deterministic shield drain.

```csharp
bas.ReceiveDamage(0, 100f, 0.0, Vector3.Zero, Vector3.Zero, launcher: null);
Assert.Equal(0f, bas.ShieldFraction, 3);   // shields down < 0.1 -> capturable
```

I chose the real `ReceiveDamage` damage path (reachable in this test) over any
thinner seam, per the task's preference ordering.

### Test 1 (NEW) — full shields ⇒ no capture [red first]

`Hostile_boardable_ship_cannot_capture_full_shield_station`: a hostile
`c_habmBoard` ship parked at the dock of an enemy base at **full shields**
(`ShieldFraction == 1f`) must NOT capture: the station keeps its side, armor and
shield are untouched (no post-capture fudge, no heroic shield boost), the ship
does NOT dock, and no `STATION_CAPTURE` reaches any peer.

**Pre-fix (unfixed code) failing evidence** — run with the gate temporarily
disabled to reproduce the original behaviour:

```
Failed Allegiance.Core.Tests.StationCaptureLiveTests.Hostile_boardable_ship_cannot_capture_full_shield_station [1 s]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
  Stack Trace:
     at ...StationCaptureLiveTests.Hostile_boardable_ship_cannot_capture_full_shield_station()
        .../StationCaptureLiveTests.cs:line 282
```

Line 282 is `Assert.Equal(seat.SideA, bas.SideId)` — the station's side flipped
from 0 (SideA) to 1 (SideB): on unfixed code the hostile board ship **captured
the full-shield base**. Red confirmed.

**Post-fix**: the same test passes — the station keeps its side, no dock, no
broadcast.

### Test 2 (UPDATED) — shields down ⇒ capture, existing assertions intact

`Hostile_boardable_ship_docks_and_captures_enemy_station` previously asserted
`ShieldFraction == 1f` immediately before capture (which the new gate makes
impossible). It now lowers the base's shield to 0.0 through the real
`ReceiveDamage` path first, then asserts the capture proceeds with **all the
existing post-capture assertions intact**: station re-sides to the capturer,
`ArmorFraction` exactly halved (1.0 → 0.5), `ShieldFraction` = the heroic-engineer
value (0.7), a real `STATION_CAPTURE(58)` reaches every peer with the correct
stationID/sidOld/sidNew/shipIDCredit, and the ship docks at its newly-captured
base.

The `ShieldFraction == 0f` pre-capture assertion was substituted for the old
`== 1f`; nothing about what the capture itself must do was weakened.

### Verification run

Whole-class run `--filter FullyQualifiedName~StationCaptureLiveTests`:
**4 passed, 0 failed** (the 3 pre-existing tests + the new full-shield test).

---

## Build & full-suite verification

- Whole-solution build via `/home/domingo/.dotnet/dotnet build` (bare `dotnet`
  is a broken snap stub): **0 warnings, 0 errors**.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, run 3 times. Every
  run hit only the **pre-existing live-wire timing flakes** (allowed carve-out);
  no non-flaky test failed, and no station-capture test ever failed. Failures
  seen, by run:
  - Run 1 (12 core): ConquestTerritoryLiveTests ×3, GameOverPlayersWireTests
    ×2, LiveChatSectorWireTests, LiveCountdownStartPingTests, LiveMoneyTests,
    LiveTurretGunnerBindingTests, LiveWinConditionTests, ShipsUpdateLiveTests,
    AddPartTests, JoinFlowLiveTests, ShipDeleteGoneLiveTests.
  - Run 2 (12 core): ConquestTerritoryLiveTests ×3, GameOverPlayersWireTests
    ×2, LiveMoneyTests, LiveTurretGunnerBindingTests, plus AsteroidMinedLiveTests,
    LiveRelaunchTests, LiveRipcordTests, LiveTreasurePickupTests,
    ShipsUpdateLiveTests (different subset — run-specific).
  - Run 3 (8 core): ConquestTerritoryLiveTests ×3, GameOverPlayersWireTests ×2,
    LiveMoneyTests, LiveTurretGunnerBindingTests, LiveWinConditionTests.
  The varying subsets between runs is the signature of load/timing flakiness.
- **These flakes predate P450**: the recurring set (ConquestTerritoryLiveTests,
  GameOverPlayersWireTests, LiveMoneyTests, LiveTurretGunnerBindingTests,
  LiveWinConditionTests) was re-run on a **clean tree** (changes stashed) and
  failed the same way with a shifting subset (1, 2, and 5 failures across three
  clean-tree attempts). P450's change is confined to the hostile station-capture
  branch and its own test file; none of the flaking tests exercise station
  capture, and the dedicated `StationCaptureLiveTests` class is stable (4/4)
  across every run.

## Out of scope (unchanged)

- Native's full collision bounce and collision damage for a board ship vs a
  shielded base (separate ramming pass).
- Decoding mission params (`bInvulnerableStations`, `bAllowFriendlyFire`,
  alliances).
- A proper `InGarage` geometry test instead of the point-in-sphere `DockRadius`.
- Station under-attack alerts (P451), minefields (P452/P453), teleport probes
  (P454).
- Armed probes, `ShipStatusSpotted`, in-flight turret seat switch.
