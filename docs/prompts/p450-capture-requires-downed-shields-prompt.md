# P450 — station capture ignores shields: a troop transport takes a full-health base

## Why this task exists

A gameplay-parity pass found that the port's station capture has no
shield gate. In the original game, capture is the payoff of a bomber
run: bombers knock a base's shields down, then a troop transport
(`c_habmBoard` hull) docks and takes it. In this port, a lone board-capable
hull captures a fully shielded enemy base just by reaching its dock.

- **Native gate** (`~/Development/Allegiance/src/Igc/shipIGC.cpp:849-857`),
  in the ship-vs-station collision, enemy branch:
  ```
  else if (m_myHullType.HasCapability(c_habmBoard) &&
           (!GetMyMission()->GetMissionParams()->bInvulnerableStations) &&
           (pStation->GetShieldFraction() < GetMyMission()->GetFloatConstant(c_fcidDownedShield)))
  {
      if (pStation->InGarage(...))
          if (GetMyMission()->GetIgcSite()->DockWithStationEvent(this, pStation)) break;
  }
  ```
  A hostile board hull reaches `DockWithStationEvent` only when the
  station's shields are below `c_fcidDownedShield`. Otherwise it falls
  through to the normal bounce/collision-damage path at
  `shipIGC.cpp:719-840`. `DockWithStationEvent` → `FedSrv.CPP:13100-13105`
  → `CaptureStation` is the *second* half of the gate. It is not the whole
  gate.
- **`c_fcidDownedShield`** is set in `Igc/missionigc.cpp:2745`
  (`= c_downedShield`). The port already uses it as the literal `0.1f` with
  that citation at `src/Allegiance.Core/Sim/Ship.cs:3873` (see also
  `Ship.cs:64`). Reuse that value and don't invent a new one. If you promote
  it to a shared named constant, keep the citation.
- **Port** (`src/Allegiance.Core/Model/Sector.cs:2992-3008`): the
  hostile-capture branch checks only (a) a different side, (b)
  `HabmBoard`, and (c) the runtime exists. Its comment states: "Confirmed
  reading the native: there is NO shield/hull gate on capture — it is purely
  a docking-time check (FedSrv.CPP:13100-13105)." **That comment is wrong.** It
  read only the FedSrv half and missed the IGC collision gate quoted above.
  The runtime station's live shield value is `Station.ShieldFraction`
  (`src/Allegiance.Core/Sim/Station.cs:182`).
- **`bInvulnerableStations`** is **not decoded** in this port. That is
  stated at `Sim/Station.cs:57`, `Sim/Station.cs:239-241` ("default =
  vulnerable") and `Sim/ClusterWarnings.cs:15-16`. Follow that existing
  precedent: treat it as the default `false` and note it in the comment.
  Don't add mission-param decoding here.

## Scope

1. In the hostile-capture branch of `Sector.cs`'s dock pass, add the native
   shield gate: capture proceeds only when the resolved runtime's
   `ShieldFraction < 0.1f` (`c_fcidDownedShield`). When shields are up, the
   hostile board ship must **not** capture and must **not** dock. It falls
   through exactly like the existing non-board rejection (`continue`), which
   is the port's current stand-in for native's bounce.
2. Replace the wrong comment with an accurate one. Cite both halves
   (`shipIGC.cpp:849-857` collision gate + `FedSrv.CPP:13100-13105`
   capture) and the `bInvulnerableStations` = not-decoded, default-false
   precedent.
3. Update any existing test that relied on capture-at-full-shields. For
   example, `tests/Allegiance.Core.Tests/StationCaptureLiveTests.cs:199-227`
   asserts `ShieldFraction == 1f` before capture. Drop that station's
   shield below 0.1 first, through a real damage path if one is reachable
   in that test, otherwise through the narrowest existing test seam, and
   say which you used. Don't weaken what the test asserts about the capture
   itself.

## Explicitly out of scope

- Native's full collision **bounce and collision damage** for a board ship
  that hits a shielded enemy base. A separate pass covers ramming and
  collision damage. The port's existing `continue` rejection is enough here.
- Decoding mission params (`bInvulnerableStations`, `bAllowFriendlyFire`,
  alliances).
- A proper `InGarage` geometry test instead of the point-in-sphere
  `DockRadius`.
- Station "under attack" alerts (P451), minefields (P452/P453) and teleport
  probes (P454). Those are separate tasks in this batch.
- Named follow-ups from the same parity pass, not scoped yet: armed probes
  (gun towers), enemy-spotting SHIP_STATUS reports (`ShipStatusSpotted`),
  and the in-flight turret seat switch.

## Verification

- **A test that fails on the unfixed code first.** A hostile `HabmBoard`
  ship reaches the dock of an enemy station at **full shields** and must
  **not** capture: the station keeps its side, and no `CaptureStationEvent`
  or STATION_CAPTURE is raised. Run it against the unfixed code and record
  in the report that it fails (captures). Then show it passes after the fix.
- A second test: the same approach with the station's shield below 0.1
  captures, with the existing post-capture assertions intact.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Bare `dotnet` on this box is a broken snap stub, so always
  use the full path.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive
  clean runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and you must name any you see.

## Deliverables

1. The fix plus tests, committed on **`master`** (not a side branch, not
   pushed; Domingo pushes). Check `git branch --show-current` before
   committing.
2. Report doc `docs/reports/P450_CAPTURE_REQUIRES_DOWNED_SHIELDS.md`. Include
   the pre-fix failing test evidence, the gate added, the corrected comment,
   which test seam lowered the shield, and the `bInvulnerableStations`
   decision.
