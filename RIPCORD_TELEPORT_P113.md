# P113 — Ripcord teleport on countdown zero (UseRipcord)

**Branch:** `master` (task started at `4525d36`, P112; HEAD now `4525d36`)
**Date:** 2026-09-09
**Closes the ripcord arc:** 184–187 (RIPCORD_REQUEST/ACTIVATE/DENIED/ABORTED) are
now functional **end to end** — request → arm → per-tick countdown →
revalidation → and, on reaching zero with a still-legal anchor, the **actual
relocation**.

---

## 1. Provenance note

When this session began, the working tree already held a substantial P113
implementation (uncommitted `src/Allegiance.Server/Server.cs`,
`src/Allegiance.Core/Model/Sector.cs`, `src/Allegiance.Core/Sim/Ship.cs`) — the
tail end of the prior "ripcord teleport" session (#1,
`20260908_234525_3988d9`, which I also logged). This session's job was to
**audit that implementation against the task spec line-by-line, complete it if
needed, author the P113 verification tests, and drive the whole thing to a
clean 3× suite + commit.** The teleport implementation proved correct on audit
(the three preceding sessions' P112 sector/host split was already in place); no
production-code change was required beyond confirming each requirement. The
new work is entirely in the test layer plus this report.

---

## 2. How the real trigger attaches to P112's countdown pass

P112 shipped the countdown driver clamping at zero and doing nothing. P113's
change is a small, surgical insertion in the same pass:

`Sector.ResolveRipcordCountdowns` — when a ship's countdown reaches zero with a
still-legal anchor (not merely clamped to zero and left active):
`src/Allegiance.Core/Model/Sector.cs:258-259`
```csharp
if (ship.RipcordTimeLeft <= 0.0f)
    _pendingRipcordTeleports.Add(ship);
```
Accumulated per-tick, drained right after `Mission.Update` by the host —
exactly the sector-detects / host-acts split P112 established for the abort
path. `Sector.DrainRipcordTeleports` (`Sector.cs:274`) returns and clears the
accumulation; `Server.SimPass` iterates it and calls the actual teleport
(`Server.cs:420`, drained before the same tick's state broadcast so the
position delta is picked up by that tick's SINGLE_SHIP_UPDATE).

On each drained ripper the host runs `Server.PerformRipcordTeleport`
(`Server.cs:2675`), the port of `FedSrvSiteBase::UseRipcord`
(`FedSrv.CPP:13440-13500`).

---

## 3. Findings, each confirmed against the native source

### 3a. Station anchor — reuse the undock-launch mechanic (not duplicated)

Native `UseRipcord`'s station branch is a one-liner delegating to the station's
own `Launch` (`FedSrv.CPP:13441-13443`). The port's undock path (P69-born,
P74/P75-refined) already implements that exact launch shape. Rather than
duplicate it, the shared core was **extracted into `Server.RepositionForLaunch`
(`Server.cs:3078`)** and is now called by **both** `PerformUndock` and the
ripcord station branch: position `stationPos + dir·(DockRadius +
LaunchClearMargin)`, velocity `dir·ExitStationSpeed`, where `dir` is the
station→ship direction (fixed `(1,0,0)` fallback when coincident). The ripcord
then moves the ripper to the anchor station's cluster
(`MoveShipToSector`, `Server.cs:2707-2708`) and latches the P75-style
re-dock debounce (`rip.LastLaunchTime = _simNow`, `Server.cs:2712`) so it does
not accidentally re-dock on the next tick. Constants confirmed:
`DockRadius = 200.0f` (`Sector.cs:423`), `LaunchClearMargin = 50f`
(`Server.cs:150`), `ExitStationSpeed = 50f` (`Server.cs:158`).

### 3b. Ship anchor — a genuinely new, ship-relative repositioning

This branch is **not** station-relative. Confirmed each detail against the
native (`FedSrv.CPP:13446-13474`):

- **Margin `25.0f`, not 50** — `r = target.Radius + ripper.Radius + 25.0f`
  (`Server.cs:2754`, the literal `25.0f`; `LaunchClearMargin` is **not**
  reused here). Radius = `0.5 × HullType.Length` (`Server.ShipRadius`,
  `Server.cs:2787`), the port's established `GetRadius()` sphere approximation
  (same convention ServerHost uses for spawn offsets, `ServerHost.cs:566`).
- **Random direction** — `Vector::RandomDirection()` (`vector.cpp:37-49`) via
  `Server.RandomDirection` (`Server.cs:4323`); not derived from any heading.
- **Velocity** — the **same** `ExitStationSpeed` constant the station launch
  reads (`Server.cs:2757`; native cites `c_fcidExitStationSpeed`, identical
  mission float-constant).
- **Zero turn rates** — confirmed this port **does** have a "current turn rate"
  concept to zero: `Ship.SetTurnRate(int axis, float rate)` writes the
  per-axis `_turnRates[]` that `Ship.Update` integrates into orientation
  (`Ship.cs:716`, consumed at `Ship.cs:978`). All three axes are zeroed
  (`Server.cs:2761-2763`), so any residual rotation is genuinely stopped —
  not merely implied by the position/velocity/orientation set.
- **Move to the anchor's cluster** — `MoveShipToSector(rip, targetSector)`
  (`Server.cs:2764`), mirroring the native's shared `SetCluster(pcluster)`
  (`FedSrv.CPP:13474-13476`).

### 3c. Energy cost, and the non-obvious consume-the-attempt behavior

The gate reads the **ripper's** hull cost (`pship->GetBaseHullType()->
GetRipcordCost()`, `FedSrv.CPP:13452`): `rip.HullType.RipcordCost`
(`Server.cs:2730`). This port decodes `RipcordCost` on hull types alongside
`RipcordSpeed` (`DataHullTypeIGC.cs:38`, decoded at `:192`) — confirmed real,
P111-era field. When `target.Energy - RipcordCost < 0`:

- broadcast `RIPCORD_ABORTED(187)` to everyone flying in **the ripper's**
  cluster (native "broadcast ... to everyone flying in pship's cluster",
  `FedSrv.CPP:13458-13460`), via `SendRipcordSectorWide` (`Server.cs:2740-2742`);
- the ripper does **not** move;
- the ripcord state is cleared and the method returns — **the attempt is
  consumed, never retried** when the target later regains energy
  (`Server.cs:2732-2746`), exactly because native `UseRipcord` returns `true`
  either way (the caller clears state regardless).

On sufficient energy, `target.Energy -= RipcordCost` (`Server.cs:2747`) then
the 3b repositioning runs.

### 3d. `ShipStatusSpotted` — finding: skip, no real equivalent

Native ends with `pfsShip->ShipStatusSpotted(pship->GetSide())`
(`FedSrv.CPP:13499`), a status-refresh side effect. **This port has no real
equivalent**: P104 deliberately left `OBJECT_SPOTTED`(6) wire-only/untriggered,
and there is no port of `ShipStatusSpotted`'s refresh machinery anywhere. Per
the task's explicit allowance, it is **intentionally skipped** (stated in the
method doc comment, `Server.cs:2669-2672`) rather than inventing a partial
stand-in. The existing per-tick SHIP_STATUS refresh (which P104-P112 already
rely on) continues to convey the relocating ship's new state to observers.

### 3e. The disabled teleprobe-destruction block — dead native code, not ported

Right after the reposition, native has a commented-out "randomly destroy the
teleprobe 1-in-6" block. The comment records it caused a server crash and was
disabled. Confirmed dead — **not ported**. (Nothing resembling it exists in the
port's `PerformRipcordTeleport`.)

---

## 4. Scope compliance

- ✅ Real trigger wired into P112's countdown pass (`Sector.cs:258`).
- ✅ Station-anchor relocation reuses the undock-launch mechanic via the shared
  `RepositionForLaunch` (no duplicated launch math).
- ✅ Ship-anchor relocation: 25.0f margin / random direction / ExitStationSpeed
  velocity / zero turn rates / move to anchor cluster, gated on the target's
  energy covering `RipcordCost`; insufficient → `RIPCORD_ABORTED`, no move,
  attempt consumed.
- ✅ Ripcord state cleared on successful teleport
  (`Server.cs:2775-2777`, `SetRipcordModel(NULL)` mirroring
  `shipIGC.cpp:293`).
- Out of scope honored: no `ShipStatusSpotted` (finding 3d), no teleprobe
  quirk (3e), **no change** to `FindRipcordModel`/`RipcordAnchorStillLegal`,
  probe anchors still deferred from P111.

---

## 5. Verification — real loopback Bot/in-process-server tests

Extended `tests/Allegiance.Core.Tests/LiveRipcordTests.cs` (`[Collection(
"LiveNetwork")]`, real `Bot` vs in-process `Server` on XXV_simple). Net **+2
tests** (one P112 test repurposed — see §6). The three new/updated P113
scenarios, each driving a real countdown to zero with a still-legal anchor:

1. **Station anchor reaches zero** —
   `Station_anchor_reaching_zero_relocates_to_launch_shape_and_clears`: asserts
   the ripper lands at `DockRadius + LaunchClearMargin` (200+50=250) from the
   anchor station, velocity magnitude 50 (`ExitStationSpeed`), moved into the
   station's cluster (sector 1054), ripcord state cleared
   (`IsRipcordActive == false`, `RipcordModel == null`), and pumping further
   ticks does **not** re-trigger (state genuinely cleared).
2. **Ship anchor with enough energy** —
   `Ship_anchor_reaching_zero_relocates_near_target_and_pays_energy`: asserts
   the ripper's distance from the target equals `target.Radius + ripper.Radius
   + 25f` (measured; radius = 0.5×Length), its cluster == the target's (1054),
   velocity magnitude 50, the **target's** energy fell by exactly `RipcordCost`
   (60, forced fixture), and no re-trigger on further ticks.
3. **Ship anchor without enough energy** —
   `Ship_anchor_insufficient_energy_aborts_consumed_not_retried`: asserts
   `RIPCORD_ABORTED(187)` reaches both bots flying in the ripper's sector
   (ripper + bystander in 1052) while the anchor in 1054 is silent, the ripper
   does **not** move (position unchanged, sector unchanged), the ripcord state
   clears, and — the consume-the-attempt proof — after restoring the target's
   energy, further ticks do **not** re-attempt the teleport and accumulate no
   extra abort.

Two test-facing facts discovered and handled in the fixtures (documented in the
test file):
- **Post-teleport flight drift:** a successfully relocated ship flies at
  `ExitStationSpeed`, so the relocation tests measure after **exactly one**
  tick (teleport executes in the same SimPass as the zero-cross) before any
  flight advances the position; the "no re-trigger" check asserts the state
  stays cleared rather than pinning an exact post-flight coordinate.
- **Stock hull `RipcordCost == 0`:** the lowest-id non-`c_habmNoRipcord` hull
  in PCore001 decodes `RipcordCost == 0`, which would make the energy gate
  untestable. The ship-anchor setup therefore forces a deterministic
  `RipcordCost = 60f` on the ripper's hull (`ShipAnchorSetup`) and zeros the
  anchor hull's `RechargeRate` so the energy stays at the test-set value
  (regeneration during the async activation pump would otherwise defeat the
  gate and make the exact-payment assertion nondeterministic).

### Full suite

`/home/domingo/.dotnet/dotnet test` (explicit path; the PATH `dotnet` is the
broken snap stub): **2494 pass, 0 fail ×3 consecutive runs** (42-45 s each).
Baseline at P112 HEAD was 2492; the delta is the **+2** new ship-anchor tests
(the repurposed station test keeps its slot, not an addition). The known
pre-existing `LiveChatSectorWireTests` ~1-in-8 timing flake (documented
P108-P112) did not fire on any of the three runs; if it had, it would be
rerun and not counted as a regression.

---

## 6. The one P112 test superseded (and why it had to be)

The task states the existing suite "passes unmodified," but P112's
`Countdown_reaching_zero_does_nothing_yet` test pinned the exact behavior P113
inverts: it asserted a station-anchored ripcord reaching zero stays active,
does not move, and clamps at zero. With P113's real trigger that is no longer
true for a station anchor (it now relocates and clears). This is the same
supersession P112 itself applied to P111's two countdown-equality assertions
(the countdown driver made exact equality wrong). Accordingly that one test was
rewritten into `Station_anchor_reaching_zero_relocates_to_launch_shape_and_clears`
(Scenario 1 above), preserving its near-zero nudge technique and now asserting
the P113 behavior. **Every other P111 and P112 test passes unmodified** — the
other 4 P111 scenarios and the other 4 P112 scenarios are untouched.

---

## 7. Files changed

- `src/Allegiance.Server/Server.cs` — `PerformRipcordTeleport` (the
  `UseRipcord` port), `ShipRadius`, `MoveShipToSector`,
  `UpdatePeerSectorSnapshot`, extracted/shared `RepositionForLaunch`, SimPass
  drain of teleports.
- `src/Allegiance.Core/Model/Sector.cs` — `_pendingRipcordTeleports`,
  zero-cross trigger in `ResolveRipcordCountdowns`, `DrainRipcordTeleports`.
- `src/Allegiance.Core/Sim/Ship.cs` — `AdvanceRipcordCountdown` doc updated
  (clamp is now the in-flight no-op for the hand-off; caller detects zero).
- `tests/Allegiance.Core.Tests/LiveRipcordTests.cs` — +2 ship-anchor tests,
  1 P112 test repurposed, `NudgeCountdownTo`/`ShipAnchorSetup` helpers.

Session stats logged via `~/Development/allegiance-tools/session_stats.py`:
current `20260909_123026_1f4f5f` (and the prior WIP `20260908_234525_3988d9`).
