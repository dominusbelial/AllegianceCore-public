# P117 — RELAUNCH_SHIP: the real effect (resupply + launch-slot reposition + broadcast)

Status: IMPLEMENTED. Build + full suite clean, 3 consecutive runs (2523 tests).

Branch: `master` (top `d13c6e2` P116). No sudo. No Windows VM.

---

## What this task delivered

P116 wired real `InGarage` detection into the per-tick pass but changed nothing.
P117 is the actual effect, the port of `FedSrvSiteBase::LandOnCarrierEvent`
(`FedSrv/FedSrv.CPP:12808-13031`) at this port's own, already-established
granularity:

1. **`Wire/RelaunchShip.cs`** (new) — the `FM_S_RELAUNCH_SHIP`(192) codec,
   reusing `BuyLoadoutRequest.cs`/`BuyLoadoutAck.cs`'s `ShipLoadout` +
   `ExpandedPartData` blob encoding for the `loadout` field.
2. **`Server.PerformRelaunch(Ship lander, Ship carrier)`** (new) — the port of
   `LandOnCarrierEvent`'s effect: restore the lander's ship-level energy/ammo
   pools, reposition at a real launch slot (or the no-slots backward fallback),
   and broadcast `RELAUNCH_SHIP`(192) sector-wide.
3. **Wired into P116's drain point** — `Server.SimPass` now calls
   `PerformRelaunch(lander, carrier)` for each `DrainCarrierLandings()` result
   instead of the P116 log-only stub. The lander's position change naturally
   ends the `InGarage` condition next tick (P116's own finding confirmed live,
   no latch needed).
4. **`Ship.NextLaunchSlot` / `Ship.GetLaunchSlot(int)`** (new) — the per-carrier
   round-robin launch-slot counter, the faithful port of `shipIGC.h:2168-2172`.
5. **`Bot.RelaunchShips`** observation + `Wire`/`Live` tests.

---

## The resupply scoping decision (ship-level pools), plainly

The task brief anticipated this and the implementation confirms it:

- This port tracks ammo/energy as **ship-level scalar pools** seeded at
  construction: `Ship.Energy = HullType.MaxEnergy; Ship.Ammo = HullType.MaxAmmo`
  (`Sim/Ship.cs:62-63`). There is **no per-part inventory** and **no
  `SuccessorPartType` concept** — confirmed: zero real references in
  `Data/DataPartTypeIGC.cs` or `Sim/MountedPart.cs` (the only textual hits are
  this task's own doc comments). `MountedPart`'s own doc says the live ammo
  lives on `Ship.Ammo` (`Sim/MountedPart.cs:30`).
- The native's resupply is two layers: a per-part `RefillPart` loop that
  restores each part's `Amount` (or `SHRT_MAX` for free parts) and **walks the
  best tech-available successor part type** (`RefillPart`, `FedSrv.CPP:12589-
  12618`), plus a ship-level scalar reset (`FedSrv.CPP:12855-12861`:
  `SetAmmo(SHRT_MAX); SetFuel(FLT_MAX); SetEnergy(GetMaxEnergy()); SetFraction(1.0)`
  plus a mounted shield's fraction).
- **Port:** restore the two scalar pools to their hull maxima —
  `lander.Energy = HullType.MaxEnergy; lander.Ammo = HullType.MaxAmmo`
  (`Server.cs PerformRelaunch`). This is exactly the native's ship-level
  reset at the port's granularity: `Ammo = SHRT_MAX` collapses to the finite
  `MaxAmmo`, `Energy = MaxEnergy` is verbatim. The per-part successor-upgrade
  walk is unportable as written (needs `SuccessorPartType` + `TechTreeBitMask`,
  which don't exist) and is out of scope — stated plainly, not a gap to fill.

### Confirmed findings

- **Fuel is a real, separate, pre-existing gap** (not invented here). Native
  `SetFuel(FLT_MAX)` (`FedSrv.CPP:12856`) has no target: `grep "Fuel"` in
  `Sim/Ship.cs`, `Model/Sector.cs`, `Server.cs` returns **zero consumers**
  (verified; the only hits are this task's own comments). `DataHullTypeIGC.MaxFuel`
  is decoded (`Data/DataHullTypeIGC.cs:32,210`) but nothing reads or restores
  it. Not this task's job to add.
- **Hull/shield fraction repair** — native also resets `Fraction = 1.0` and a
  mounted shield's fraction (`FedSrv.CPP:12859-12861`). These are **not**
  performed here: P117's scope is the consumable resupply (energy+ammo). This
  is a real, small, separately-citable follow-up (a damaged lander relaunches
  still damaged).
- **`RecalculateCollisions`** (`FedSrv.CPP:13006`) — N/A: this port has no
  rigid-body collision-resolution system (established P115/P116), so there is
  no post-move collision pass to call.
- **Autopilot command re-tasking** (`FedSrv.CPP:13024-13030`, the
  `SetCommand(c_cmdAccepted,...)` / `c_cidDefend` / `c_cidGoto` reassign for a
  GOTO-bound lander) — skipped, same precedent as `ShipStatusSpotted` (P113):
  this port models no AI/pilot-type/autopilot command concept. The only
  command-ish state is a single weapon-aim `Ship.CommandTarget` with no `c_cid*`
  IDs (`Sim/Ship.cs:444-456`). Stated plainly.

---

## The reposition + broadcast (the two native branches, faithfully)

Native (`FedSrv.CPP:12960-13023`):

- `vLaunch = pMission->GetFloatConstant(c_fcidExitStationSpeed)` (`FedSrv.CPP:
  12959`), and `c_fcidExitStationSpeed = 4` → `c_fExitStationSpeed = 50.0f`
  (`igc.h:63`, `missionigc.cpp:2689,2744`) — the port's own `ExitStationSpeed
  = 50f` (`Server.cs:158`). Reused verbatim.
- Branch A, **no launch slots** (`GetLaunchSlots() == 0`):
  `position -= backward·(radCarrier + radLander + vLaunch·0.5); velocity -=
  backward·vLaunch;` keep the carrier's orientation.
- Branch B, **real launch slot**: `slot = carrier.GetLaunchSlot()` (round-robin),
  `position += GetLaunchPosition(slot)·orientation; forward =
  GetLaunchDirection(slot)·orientation; position += forward·(radLander +
  vLaunch·0.5); velocity += forward·vLaunch;` then turn to `forward`.
- Small ±0.5 positional jitter on all three axes.

Ported in `PerformRelaunch` using the port's real decoded geometry:
`HullLandingBay.LaunchSlots` (position+direction, already scaled — P114) served
through the production `HullLandingBayCache` the live server attaches to every
sector (`Server.cs:333-369`). The slot's local frame is transformed by the
carrier's orientation via the port's `Vector3 * Orientation` operator
(`Orientation.cs:161`). The "turn to forward" step uses
`new Orientation(carrier.Orientation); .Set(forward)` (the port's forward-axis
constructor, `Orientation.cs:124-144`) as the stand-in for the native's
`TurnTo(forward)` — documented simplification; for `cap502` the slot direction
happens to be `(0,0,−1)` = identity forward, so the result is identity there.

`RecalculateCollisions` is N/A (see findings).

---

## The RELAUNCH_SHIP(192) codec

`Messages.h:987-995`:
```
DEFINE_FEDMSG(S, RELAUNCH_SHIP, 192)
  FM_VAR_ITEM(loadout);
  ShipID shipID;  ShipID carrierID;
  Vector position;  Vector velocity;
  CompactOrientation orientation;  Cookie cookie;
END_FEDMSG
```
Byte layout (confirmed against the native serializer, `messagecore.h:777-786`
var-items-first + `_Utility/Messages.cpp:392` `pBlobDst = pbFM + cbfm` where the
size argument is `sizeof(FMD_S_RELAUNCH_SHIP)`):

| offset | width | field |
|---|---|---|
| 0 | 2 | `cbmsg` |
| 2 | 2 | `fmid = 192` |
| 4 | 2 | `IB ibLoadout = 48` |
| 6 | 2 | `CB cbLoadout = 2 + 6·nParts` |
| 8 | 2 | `ShipID shipID` |
| 10 | 2 | `ShipID carrierID` |
| 12 | 12 | `Vector position` (4-aligned) |
| 24 | 12 | `Vector velocity` |
| 36 | 8 | `CompactOrientation` |
| 44 | 1 | `Cookie` (byte) |
| 45 | 3 | pad → 4-aligned `sizeof` = **48** |
| 48 | blob | `ShipLoadout`: short `hullID` + nParts × `ExpandedPartData` (6) |

Two `Vector` members force the struct to 4-alignment, so `sizeof = 48` (the same
trailing-pad model `Eject.cs:22-32` and `JoinedMission.cs` document), and the
var blob begins at that padded offset. The `loadout` blob is the same
`ShipLoadout` + `ExpandedPartData[]` shape `BuyLoadoutRequest`/`BuyLoadoutAck`
encode — reused, not reinvented.

**Cookie**: this port's `Cookie` is a single `byte`, sent as `0` (no per-player
cookie machinery) — the exact simplification already applied at
`Server.cs:3289,4616` and `Eject.ForSurvivor` (`Eject.cs:51-52`). Native calls
`NewCookie()` for a player (`FedSrv.CPP:13018`); not replicated.

**Broadcast**: the port's sector-wide helper `GetGroupSectorFlying(pcluster)`
equivalent. `SendRipcordSectorWide` (from P111/P112) is the generic
"send to every peer flying in a sector" broadcast; it is now factored into a
canonical `SendSectorWide(short sectorId, byte[] encoded)` (`Server.cs:2671-
2681`) with the ripcord name kept as a delegating legacy alias, and the relaunch
uses `SendSectorWide(lander.Sector.Id, ...)` — `GetGroupSectorFlying(pship->GetCluster())`,
`FedSrv.CPP:13019`.

---

## Native-versus-port scope honesty

Faithfully ported: ship-level pool resupply, both reposition branches,
round-robin `GetLaunchSlot`, the `RELAUNCH_SHIP` broadcast, sector-wide
delivery, `Cookie=0`.

Deliberately not ported (each is a real, separately-citable gap / later task):
- Per-part `RefillPart` successor-upgrade walk + desired-loadout fill
  (`FedSrv.CPP:12831-12854`) — needs per-part inventory + `SuccessorPartType` +
  tech-tree bitmasks, none of which exist in this port.
- Player desired-loadout preference system (`CFSPlayer::GetDesiredLoadout()`).
- Fuel tracking (confirmed absent; `MaxFuel` decoded but unused).
- Hull/shield fraction repair on relaunch (native does it; P117 scopes to
  energy/ammo).
- Autopilot command re-tasking (no AI command concept in the port).

---

## Real corpus geometry measurements (this task)

`cap502` (`HullLength` 300, `.cvh` radius → scale 17.570): **two real launch
slots** `launch1`/`launch2` at carrier-local pos x≈∓42.8, y≈−21.8, z≈−49.6,
dir `(0,0,−1)`; two landing bays; a `fig22` lander (Length 10.63, radius 5.3)
fits at carrier-local (−60,−32,70).

`faohshcar` (Length 300): **zero launch slots** — a single frame named `launch`
(not `launch1`), so `HullLandingBay.Reconstruct` yields none; a `fig22` fits at
(−10,−26,36).

**Correction to a P114 report claim**: P114 named `p1_car` as a carrier with
real launch slots, but scanning the real `.cvh` frames shows `p1_car` carries
only a single `launch` frame (⇒ 0 launch slots), exactly like `faohshcar`.
Real 2-slot carriers in the corpus include `cap502`, `cap504`, `raincarr`.
This task used `cap502` for the launch-slot branch and `faohshcar` for the
fallback.

---

## Verification (no VM)

**New tests, 12 total:**

- `tests/.../Wire/RelaunchShipWireTests.cs` (8) — byte-exact pinning of the
  full 48+blob layout (hull-only and one-part), round-trip of every field
  including the quantized orientation and the parts tail, and the reject paths
  (wrong fmid / too-short / mismatched cbmsg).
- `tests/.../LiveRelaunchTests.cs` (4) — real loopback Bots vs an in-process
  Server (the `LiveRescueTests` recipe), all real corpus hulls + real `.cvh`
  geometry through the production artPath cache the live server installs:
  1. **Depleted lander relaunches off `cap502` and is resupplied** — Energy
     `5 → 1200` (= `MaxEnergy`), Ammo `1 → 450` (= `MaxAmmo`); `RELAUNCH_SHIP`
     reaches both bots with correct `shipID`/`carrierID`; lander repositioned
     to a real launch slot outside the bay; **not InGarage on the next tick**.
  2. **Consecutive landings round-robin** across `cap502`'s two real slots —
     the two relaunches land at the +x slot then the −x slot (GetLaunchSlot
     modulo counter: 0→1, 1→0).
  3. **Zero-slot carrier (`faohshcar`) uses the backward fallback** — lander
     thrown to carrier.z − (radCarrier+radLander+25), velocity (0,0,−50).
  4. **No repeat after a successful relaunch** — 40 further ticks keep the
     lander out of the bay (race-free geometric self-termination proof).

**Full suite**: 2511 (baseline, re-confirmed) + 12 new = **2523**, all green on
3 consecutive runs. No regression; the documented pre-existing
`LiveChatSectorWireTests` / `LiveCombatWireTests` timing flakes did not fire.

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.

## Does this close the RELAUNCH_SHIP arc?

Functionally, yes — **modulo the explicitly-cited follow-ups**. 184–192 is now
real end-to-end at this port's architecture: a real carrier landing
(P116) triggers a real relaunch (P117) that resupplies the lander's ship-level
pools, repositions it at a real launch slot, and broadcasts the wire message
that tells every ship in the sector. The named, out-of-scope residuals are all
real and separate: per-part successor-upgrade / desired-loadout (needs per-part
+ tech-tree infra), fuel tracking (confirmed absent everywhere), and hull/
shield fraction repair on relaunch (native does it; not part of the energy/ammo
resupply scope). If any of those is wanted, it is a distinct follow-up task, not
a defect in this one.
