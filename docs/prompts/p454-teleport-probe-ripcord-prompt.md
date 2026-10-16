# P454 — you can't ripcord to a teleport probe

## Why this task exists

A gameplay-parity pass found that teleport probes do nothing in the port. In
the original game, dropping a teleport probe in a forward sector and having
the team ripcord onto it is a core strategic move: rushes, forward staging,
and getting miners' escorts home. The port's P435 shop sells the probe
dispensers, including teleport types, and they deploy through the `N` key.
But the ripcord search never considers a probe, so there's no reason to
drop one.

- **Native ripcord target search**, `CshipIGC::FindRipcordModel`
  (`~/Development/Allegiance/src/Igc/shipIGC.cpp:3718-3900`), per candidate
  cluster:
  1. **The command target** (`c_cmdCurrent`, falling back to
     `c_cmdAccepted`), if the side sees it and it is in that cluster
     (`:3780-3793`). If it is a **same-side probe** with
     `GetCanRipcord(ripcordSpeed)`, it is the target (`:3803-3814`).
     (The command-target *ship* case at `:3815-3831` is out of scope here.)
  2. Otherwise a friendly `c_sabmRipcord` **station**, nearest to the goal
     position if there is one (`:3835-3838`).
  3. Otherwise, for players only (`m_pilotType >= c_ptPlayer`), **same-side
     probes** with `GetCanRipcord(ripcordSpeed)`. Search the cluster's probes
     **backwards** so the most recently dropped one wins. If there is a goal
     position, pick the one nearest the goal instead (`:3840-3890`, the
     non-allied branch at `:3866-3888`).
  4. Otherwise ripcord-target ships (`:3890+`, already ported).
  - `ripcordSpeed = m_myHullType.GetRipcordSpeed()` (`:3798`).
- **`GetCanRipcord`** (`Igc/probeigc.h:248-254`):
  `dt = probeType.RipcordDelay; return dt >= 0 && (lastUpdate − time0) >= dt &&
  (lastUpdate + ripcordSpeed + 1) <= timeExpire`. The probe is usable only
  when it is a teleport type (`dt >= 0`), after its activation delay, and when
  it won't expire before the ripcord lands. **No capability-bit lookup is
  needed.**
- **Landing**, `FedSrvSiteBase::UseRipcord` (`FedSrv/FedSrv.CPP:13440-13500`):
  a non-station anchor places the ship at
  `anchor.pos + RandomDirection · (anchor.radius + ship.radius + 25)`, with
  velocity `dir · c_fcidExitStationSpeed` and turn rates zeroed. The energy
  cost applies to **ship** anchors only; a probe anchor costs nothing.
- **Port**:
  - `FindRipcordModel` (`src/Allegiance.Server/Server.cs:783-812`) has only
    the station tier and the ripcord-target-ship tier. Its doc comment
    (`Server.cs:751-782`) explicitly defers "the command-target probe/ship
    tier (`shipIGC.cpp:3802-3833`) and the friendly-probe tier".
  - `RipcordAnchorStillLegal` (`src/Allegiance.Core/Model/Sector.cs:1296-1330`)
    handles only Station and Ship anchors. Any other type returns `false`
    ("unsupported anchor type"), so a probe anchor would abort at once.
  - `PerformRipcordTeleport` (`src/Allegiance.Server/Server.Docking.cs:505+`)
    has Station and Ship branches.
  - The probe has `DeployTime` and `TimeExpire`
    (`src/Allegiance.Core/Sim/Probe.cs:105,121`), and `DtRipcord` is decoded
    (`src/Allegiance.Core/Data/DataProbeTypeIGC.cs:57,114`) but never read.
  - `DataHullTypeIGC.RipcordSpeed` is decoded (`Data/DataHullTypeIGC.cs:37`).
  - The request entry point is `HandleRipcordRequest` → `FindRipcordModel`
    (`Server.Docking.cs:~390-410`), reached from the P414 ripcord key.

## Scope

1. **`Probe.CanRipcord(double now, float ripcordSpeed)`**: a direct port of
   `GetCanRipcord`, using `ProbeType.DtRipcord`, `DeployTime` and
   `TimeExpire`, with the citation.
2. **`FindRipcordModel` probe tiers**:
   - (a) The command-target probe tier. If the ripper's command target is a
     same-side `Probe` in the target sector that `CanRipcord`, return it.
     Check what `Ship.CommandTarget` can hold for a probe, and document it if
     a probe can't currently be a command target.
   - (b) The friendly-probe fallback tier, **after** the station tier and
     **before** the ripcord-target-ship tier, players only. Pick the most
     recently deployed same-side `CanRipcord` probe in the target sector, or
     the one nearest the goal position when one exists.
   - Use a `Ship.RipcordTarget` with the probe object type.
3. **Anchor legality**: add a probe branch to `RipcordAnchorStillLegal`. The
   probe must still exist in that cluster, still be same side, and still
   `CanRipcord`. That makes an expiring or destroyed probe abort the ripcord,
   as native's per-tick re-check does.
4. **Landing**: add a probe branch to `PerformRipcordTeleport` using native's
   non-station placement (radius + 25 offset, random direction, exit-station
   speed, zero turn rates, move to the sector), with **no energy cost**. Reuse
   the ship-anchor branch's placement code; don't duplicate it.
5. Check that the client-side RIPCORD_ACTIVE / HUD handling
   (`ClientWorld.RipcordActive`, `ClientWorld.cs:493-510`) copes with a probe
   anchor object type. Fix only a real crash or blank. Report what you find.

## Explicitly out of scope

- The command-target **ship** tier (`shipIGC.cpp:3815-3831`). That's a
  follow-up.
- Allied ripcord (`bAllowAlliedRip`). The port has no alliances.
- Native's client-local "Teleport active in <sector>" text
  (`WinTrek/trekigc.cpp:3019-3033`, `ActivateTeleportProbe`) and
  `DestroyTeleportProbe`. Possible small follow-up.
- NPC/drone use of teleport probes.
- **Armed probes (gun towers).** Native `probeigc.cpp:70-137,352-528`, deferred
  in the port at `Sim/Probe.cs:39-45`. It's a named follow-up from the same
  parity pass, not scoped yet.
- Other named follow-ups from the same pass, not scoped yet: enemy-spotting
  SHIP_STATUS reports (`fsship.cpp:237` `ShipStatusSpotted`) and the in-flight
  turret seat switch (`TK_OccupyNextTurret`, see
  `docs/reports/P416_TURRET_SEAT_KEYS.md`).
- Station capture (P450), station alerts (P451) and minefields (P452/P453).

## Verification

- **A wire-level test that fails on the unfixed code first.** A loopback
  player deploys a real teleport probe (a `PCore001` probe type with
  `DtRipcord >= 0`) in sector X through the real FIRE_EXPENDABLE path,
  then flies to sector Y, where there is no friendly ripcord station in X.
  After the activation delay, a ripcord request to X must go active, anchored
  on the probe, and on completion the ship is in X within
  `probe.radius + ship.radius + 25` of the probe. Record in the report that
  this fails before the fix: no model is found, so the ripcord is refused.
- Additional tests:
  - Before the probe's `DtRipcord` delay, the probe is not chosen.
  - A non-teleport probe (`DtRipcord < 0`) is never chosen.
  - A friendly ripcord station in X wins over the probe.
  - With two teleport probes, the most recently deployed wins.
  - The probe expiring or being destroyed mid-countdown aborts the ripcord.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Bare `dotnet` is a broken snap stub; always use the full
  path.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive
  clean runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and you must name any you see.

## Deliverables

1. The change plus tests, committed on **`master`** (not a side branch, not
   pushed; Domingo pushes). Check `git branch --show-current` before
   committing.
2. Report doc `docs/reports/P454_TELEPORT_PROBE_RIPCORD.md`. Include the
   pre-fix failing test evidence, the tiers added and their order, anchor
   legality, landing placement, whether a probe can be a command target
   today, and the client HUD finding.
