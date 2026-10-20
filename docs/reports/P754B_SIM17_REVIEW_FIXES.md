# P754b — fix round on P754 (simulation callbacks), from Claude's review

Base: branch `pi/p754b`, created from `pi/p754` (`6beab457`); `master` merged in as `81daa57f` (brings P751's ripcord lookup + ship-damage
alerts in `ServerHost.cs` / `Server.Combat.cs`). Changes remain in the working tree — no commits, no pushes. Native citations are relative to
`~/Development/Allegiance/src/`.

## What the review flagged and what changed

P754's six callback rows were marked `implemented` while the `ServerHost` overrides only raised `Action` hooks nobody subscribed to and
returned a constant. This round routes the port's existing production logic (or the rule that logic enforces) through each callback, so the
return value comes from the real native rule, and pins it with a test. Three dead hooks were deleted (`TreasurePickedUp`, `Rescue`,
`RipcordContinued`); the two remaining ones (`CarrierLanding`, `RipcordUsed`) are now subscribed by production
(`Server.Lifecycle.BindHostAndTransport`).

| Row | Weight | Status | Native rule | What now decides the answer |
| --- | ---: | --- | --- | --- |
| SIM-0182 `TerminateModelEvent` | 3 | implemented (fixed placement) | `Igc/modelIGC.cpp:55-61` | `Sector.Terminate(model)` notifies then evicts (native's order: notify before `SetCluster(NULL)`); `Sector.Remove` is silent again, so a sector **move** no longer fires it |
| SIM-0205 `LandOnCarrierEvent` | 3 | implemented (real body) | `FedSrv.CPP:12814-13031` (`return true` at `:13029`) | `ServerHost.LandOnCarrierEvent` → `CarrierLanding` → `Server.PerformRelaunch` (the port of the native body: resupply, launch-slot placement, `RELAUNCH_SHIP`); the sector treats it as consumed (`break`, `shipIGC.cpp:1053-1057`) |
| SIM-0206 `RescueShipEvent` | 3 | implemented (real return) | `FedSrv.CPP:13033-13076`, return at `:13073-13075` | `ServerHost.RescueShipEvent` asks the owning `Server`'s `FindFriendlyStation` (port of `FindTarget(... c_sabmRestart)`, `:13060-13064`) — no station ⇒ `false`, exactly `pstation ? DockWithStationEvent(...) : false` |
| SIM-0209 `HitTreasureEvent` | 3 | implemented (rule moved in) | `FedSrv.CPP:13558-13576` | the flag refusal gates (`:13562-13568`) now live in `ServerHost.HitTreasureEvent`: second flag / own-side flag ⇒ `false`, sector does not queue, treasure stays |
| SIM-0212 `ContinueRipcord` | 3 | implemented (rule moved in) | `FedSrv.CPP:13379-13437` | `RipcordRules.AnchorStillLegal` = the whole native boolean (flag carrier, side/alliance, cluster-less anchor, `IsRipcordTarget`/LtRipcord hull mask); the sector no longer has its own copy |
| SIM-0213 `UseRipcord` | 3 | implemented (real body) | `FedSrv.CPP:13440-13504` (always `true`) | `ServerHost.UseRipcord` → `RipcordUsed` → `Server.PerformRipcordTeleport` (the port of the native body) and returns true, so the attempt is consumed inline instead of being deferred |

### SIM-0182 — TerminateModelEvent only on real termination

P754 raised the event from `Sector.Remove`, which is also the **move** primitive (`Server.Docking.MoveShipToSector` used by ripcord, docking,
respawn, logoff hand-off — the exact call sites the review named). Fixed in `src/Allegiance.Core/Model/Sector.cs:389-420`: `Remove` is once
again a plain list edit, and the new `Terminate(model)` raises `IIgcSite.TerminateModelEvent` and then evicts — `CmodelIGC::Terminate`'s order
(`modelIGC.cpp:60-61`, notify before `SetCluster(NULL)`), idempotent so a death path plus the sector's own dead-model sweep cannot double-fire.

Every destruction funnel now calls it: ship death (`Server.Combat.FinalizeDeadShip`, `Server.Combat.cs:2006`) and logoff
(`Server.Lifecycle.RetireShipOnDisconnect`, `Server.Lifecycle.cs:1956`); treasure destroy (`Server.Docking.cs:1026`, the
`KillTreasureEvent → ptreasure->Terminate()` half, `FedSrv.CPP:12509`); the model classes' own end-of-life paths (Projectile, Treasure,
Probe ×3, Missile ×2, Mine, Chaff); and the sector's in-tick kills (`Sector.cs:1994,2027,2055,2088,2116,2183,2268,2582,2847-2848,3071,3819`).

Not covered, stated plainly: stations, asteroids and warps are not `Model`s in this port and have no move path, so they raise nothing
(native's `CstationIGC` etc. are `CmodelIGC`s). Native's **server** site does not override this callback at all (only the client's
`BaseClient::TerminateModelEvent`, `clintlib.cpp:2812`), so an unsubscribed `ServerHost.ModelTerminated` hook is faithful.

### SIM-0205 — LandOnCarrierEvent is the landing

`FedSrvSiteBase::LandOnCarrierEvent` *is* the landing (refill the lander's free/partially-gone expendables, buy back the desired loadout,
place it on a launch slot, broadcast `RELAUNCH_SHIP`, `:12814-13031`) and its only return is `true` (`:13029`). The port's implementation of
that body is `Server.PerformRelaunch` (`src/Allegiance.Server/Server.Docking.cs:1186`), so `Server.Lifecycle.cs:784` wires
`ServerHost.CarrierLanding` to it; `ServerHost.LandOnCarrierEvent` (`ServerHost.cs:1259-1266`) invokes it and returns true. The sector's
carrier pass (`Sector.cs:1416-1439`) therefore calls the site and queues nothing for a mission-bound landing (the native `break`), and the
P117 `carrierRelaunches` drain step in `Server.Broadcast.cs` was removed. Mission-less sectors keep the P116 detection queue for the bare-sector
fixtures.

Residual (unchanged by this round, restated for honesty): `PerformRelaunch` restores ship-level energy/ammo but does not remount free or
depleted parts / buy the desired loadout — the port has no free-refill or desired-loadout model.

### SIM-0206 — RescueShipEvent returns the station rule (and a P754 gating bug fixed)

Native returns `pstation ? DockWithStationEvent(pshipRescued, pstation) : false` (`:13073-13075`). `ServerHost.RescueShipEvent`
(`ServerHost.cs:1280-1282`) asks the owning Server, through `RescueDestination` (wired in `Server.Lifecycle.cs:786` to
`FindFriendlyStation(rescuee) is not null`), so only rescues with a real destination are queued and `Server.PerformRescue`
(`Server.Docking.cs:706`) performs them with the same search — one source of truth.

While fixing this, P754's own hole surfaced: it gated only the **parallel** rescue latch, so the default non-parallel
`ResolveRescueEvents` path never consulted the site at all (the callback was bypassed on the production default path unless
`ParallelSectorSim` was on). Both paths now call it (`Sector.cs:1137` serial, `Sector.cs:1211` parallel), as does the station-anchored pod
rescue (`Sector.cs:1309`, native's NULL rescuer, `shipIGC.cpp:862-867`).

### SIM-0209 — HitTreasureEvent carries the native flag gates

The refusal rule moved out of `Sector.ResolveTreasurePickups` (where P754 had it) and into `ServerHost.HitTreasureEvent`
(`ServerHost.cs:1296-1305`), which is where native has it: a ship already carrying a flag (`GetFlag() != NA`) or its own side's flag
(`oid == ship->GetSide()->GetObjectID()`) returns `false` (`FedSrv.CPP:13562-13568`), so the sector does not queue the pickup, the treasure
stays, and no `GAIN_FLAG`/destroy happens. Accepted pickups keep flowing to `Server.PerformTreasurePickup` (`Server.Docking.cs:882`), which
performs the rest of the native body (cash wallet `GiveSideMoney` + `MONEY_CHANGE`, part `ACQUIRE_TREASURE`, powerup apply, flag
`Ship.Flag` + `GAIN_FLAG`) and then `KillTreasureEvent`.

### SIM-0212 / SIM-0213 — ripcord

`ContinueRipcord`: P754 had the rule in a private `Sector.RipcordAnchorStillLegal` and the callback returning `true`. The rule now lives in
`src/Allegiance.Core/Sim/RipcordRules.cs:49-96` (`AnchorStillLegal`), the port of `FedSrvSiteBase::ContinueRipcord` (`:13379-13437`): flag
carrier (`:13385/:13405`), side/alliance mismatch with the `bAllowAlliedRip` + `AlliedSides` leg (`:13386/:13406`), an anchor that left its
cluster (`:13387/:13407`), and the `c_habmIsRipcordTarget` / `| c_habmIsLtRipcordTarget` hull mask (`:13390-13422`). `ServerHost.ContinueRipcord`
(`ServerHost.cs:1315-1316`) is the production binding; the `IIgcSite` default (`IIgcSite.cs:490`) calls the same helper so bare-domain
missions obey one rule, and `Sector.ResolveRipcordCountdowns` (`Sector.cs:937-956`) consumes only the boolean. The sector's false branch keeps
native's abort side effects (`droneRipMask` clear + `RIPCORD_ABORTED`, `:13425-13435`).

`UseRipcord`: P754 returned a constant `false` and deferred to a drain. Native always returns `true` and *is* the teleport
(`:13440-13504`). `ServerHost.UseRipcord` (`ServerHost.cs:1329-1333`) now invokes `RipcordUsed`, wired in `Server.Lifecycle.cs:785` to
`Server.PerformRipcordTeleport` (`Server.Docking.cs:525`, the port of that body: station `Launch` / target-energy cost + random-direction
placement / cluster move / `RIPCORD_ABORTED` on insufficient energy), and returns true, so the sector clears the ripcord on the spot
(`shipIGC.cpp:285-289`). The `DrainRipcordTeleports` path stays only for a site that answers `false` (native's zero-retry, `:288-289`).

Out of scope by the review's own list: SIM-0214 `FireMissile` (returns `void`, so "returns a constant" does not apply) is untouched.

## Scorecard

Statuses do not change in a fix round, so the scorecard is unchanged from merged master; recorded with
`python3 scripts/parity-scorecard.py` (full output: `docs/reports/P754B_assets/scorecard-after-p754b.txt`).

| Simulation area | Weighted | Complete / eligible |
| --- | --- | --- |
| callbacks | 78.46% (153/195) | 51/65 |
| constants | 98.52% (133/135) | 38/39 |
| periodic | 53.33% (24/45) | 7/14 |
| sounds | 83.45% (459/550) | 170/212 |
| updates | 83.95% (293/349) | 72/87 |
| warnings | 100.00% (10/10) | 2/2 |
| **TOTAL** | **83.49% (1072/1284)** | **340/419** |

## Rules, production code, and executable proof

| Row | Native source | Production code | Test |
| --- | --- | --- | --- |
| SIM-0182 | `Igc/modelIGC.cpp:55-61` | `src/Allegiance.Core/Model/Sector.cs:389-420`; `src/Allegiance.Server/Server.Combat.cs:2006`; `src/Allegiance.Server/Server.Lifecycle.cs:1956`; `src/Allegiance.Server/Server.Docking.cs:1026`; model end-of-life paths in `Projectile/Treasure/Probe/Missile/Mine/Chaff` | `P754CallbackTests.TerminateModelEvent_fires_on_termination_but_not_on_a_sector_move` |
| SIM-0205 | `FedSrv.CPP:12814-13031` | `src/Allegiance.Server/ServerHost.cs:1259-1266`; `src/Allegiance.Server/Server.Lifecycle.cs:784`; `src/Allegiance.Server/Server.Docking.cs:1186`; `src/Allegiance.Core/Model/Sector.cs:1416-1439` | `P754CallbackTests.LandOnCarrierEvent_performs_the_native_landing_body_and_returns_true`; `LiveRelaunchTests` (4 methods) |
| SIM-0206 | `FedSrv.CPP:13033-13076` | `src/Allegiance.Server/ServerHost.cs:1280-1282`; `src/Allegiance.Server/Server.Lifecycle.cs:786`; `src/Allegiance.Server/Server.Docking.cs:706`; `src/Allegiance.Core/Model/Sector.cs:1137,1211,1309` | `P754CallbackTests.RescueShipEvent_returns_the_native_station_destination_rule`; `LiveRescueTests` (5 methods); `StationRescueModelTests`; `ShipShipCollisionTests` |
| SIM-0209 | `FedSrv.CPP:13558-13576` | `src/Allegiance.Server/ServerHost.cs:1296-1305`; `src/Allegiance.Core/Model/Sector.cs:1529-1566`; `src/Allegiance.Server/Server.Docking.cs:882` | `P754CallbackTests.HitTreasureEvent_refuses_the_native_flag_cases_and_the_sector_obeys`; `LiveGainFlagTests` (8 methods); `P663TreasureParityTests` |
| SIM-0212 | `FedSrv.CPP:13379-13437` | `src/Allegiance.Core/Sim/RipcordRules.cs:49-96`; `src/Allegiance.Core/Sim/IIgcSite.cs:490`; `src/Allegiance.Core/Model/Sector.cs:937-956`; `src/Allegiance.Server/ServerHost.cs:1315-1316` | `P754CallbackTests.ContinueRipcord_returns_the_native_rule_and_drives_the_sector`; `P676SimRulesTests` ripcord theories (38 cases); `LiveRipcordTests` |
| SIM-0213 | `FedSrv.CPP:13440-13504` | `src/Allegiance.Server/ServerHost.cs:1329-1333`; `src/Allegiance.Server/Server.Lifecycle.cs:785`; `src/Allegiance.Server/Server.Docking.cs:525` | `P754CallbackTests.UseRipcord_performs_the_teleport_in_the_callback_and_consumes_the_attempt`; `LiveRipcordTests.Station_anchor_reaching_zero_relocates_to_launch_shape_and_clears` (+ the ship-anchor P113 cases) |

## Verification

Whole-solution build: `dotnet build AllegianceCore.slnx --nologo -m:1 -v:q -p:UseSharedCompilation=false -p:NuGetAudit=false` —
**0 warnings / 0 errors** (NuGet audit disabled: the sandbox cannot reach `api.nuget.org`; no product warning suppressed).

Linux x64 self-contained native AOT publish on the final tree:
`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -p:UseSharedCompilation=false -p:NuGetAudit=false -o .scratch/aot-final`
— **succeeded** (13.9 MB native `Allegiance.Server`), `grep -c 'IL2026\|IL3050'` over the publish log = **0**.

Focused runs — each class invoked **3× alone** on the final tree, every invocation passed (raw log:
`~/allegiance-evidence/P754B/focused-3x.txt`):

| Class | Cases | 3× alone |
| --- | ---: | --- |
| `P754CallbackTests` (8 new/rewritten methods) | 8 | 3/3 |
| `P676SimRulesTests` (ripcord legality theories) | 38 | 3/3 |
| `CarrierLandingTriggerTests` | 7 | 3/3 |
| `StationRescueModelTests` | 6 | 3/3 |
| `ShipShipCollisionTests` | 8 | 3/3 |
| `P663TreasureParityTests` | 17 | 3/3 |
| `LiveRipcordTests` | 13 | 3/3 |
| `LiveRelaunchTests` | 4 | 3/3 |
| `LiveRescueTests` | 5 | 3/3 |
| `LiveGainFlagTests` | 8 | 3/3 |
| `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` (bots/flight/sim are affected) | 1 | **5/5** (1 m 49-50 s each) |

Full `Allegiance.Core.Tests` run before the fast gate: **4985 passed, 1 failed** —
`Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events` (allocations 6584 ≠ 0), an egress-allocation
measurement unrelated to this change, taken while another agent's suite held the machine at 100 % CPU; it then passed **3/3 isolated**
(6/6 cases each) = the documented load flake (`docs/TEST_FLAKES.md`), not a regression. No assertion, deadline or trait was changed.

**Fast-tier run (ONE, on the final tree):** `NuGetAudit=false ./tools/run-tests.sh --tier fast --results-dir .scratch/fast-final` —
**PASS, 8971 tests, 268.5 s**, exit 0:

| Assembly | Passed | Failed | NotExecuted |
| --- | ---: | ---: | ---: |
| Allegiance.Lobby.Tests | 19 | 0 | 0 |
| Allegiance.ClientCore.MonoGame.Tests | 2572 | 0 | 3 |
| Allegiance.ClientCore.Tests | 1480 | 0 | 0 |
| Allegiance.Core.Tests | 4885 | 0 | 0 |
| fast/quarantine extras | 12 | 0 | 0 |

No timing offenders, no quarantine flakes, calibration factor 0.956 (fast limit 10.0 s). Summary:
`docs/reports/P754B_assets/fast-tier-summary.json`; raw TRX/logs: `~/allegiance-evidence/P754B/fast-final/`.

No build, test result, cache or temp artifact was placed in `/tmp`; scratch data lived in `.scratch/` inside this worktree (listed in the
worktree's `info/exclude`, never committed) and was deleted except the small summaries above.
