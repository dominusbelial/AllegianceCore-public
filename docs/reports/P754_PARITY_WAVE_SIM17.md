# P754 — simulation parity wave 17: remaining callbacks

Base: `master` at dispatch. Branch `pi/p754`. Changes remain in the working tree; no commits or pushes. Native citations are relative to `~/Development/Allegiance/src/`.

## Scope

Eight related **callbacks** rows, total weight **24**: SIM-0182, SIM-0183, SIM-0205, SIM-0206, SIM-0209, SIM-0212, SIM-0213, SIM-0214. These share the `IIgcSite` seam: several were default-only, and several were reached only through sector drain helpers without a concrete `ServerHost` override. One additional constants row (SIM-0014 `c_fcidRipcordTime`) was inspected; it remains **partial** because the port's `DataHullTypeIGC` already carries the scaled value produced by the native custom-hull loader, so there is no runtime loader to drive the slot-13 scaling without double-scaling the authored core. No interface/HUD, siege/finale, training, or HD texture rows were touched.

Skipped: all rows touched by the unmerged `pi/p751` branch (SIM-0199, SIM-0231–0233, SIM-0147, SIM-0176–0178, SIM-0180) per the coordination rule.

## Native rules and evidence

| Row | Weight | Before → after | Native source | Production code and test evidence |
| --- | ---: | --- | --- | --- |
| SIM-0182 | 3 | partial → implemented | `Igc/modelIGC.cpp:55-60` | `src/Allegiance.Core/Model/Sector.cs:395`; `src/Allegiance.Server/ServerHost.cs:1211-1214`; `P754CallbackTests.TerminateModelEvent_reaches_site_when_model_removed` |
| SIM-0183 | 3 | partial → implemented | `Igc/missionigc.cpp:3082` | `src/Allegiance.Server/Server.Lobby.cs:478`; `src/Allegiance.Server/ServerHost.cs:1211-1214`; `P754CallbackTests.TerminateMissionEvent_reaches_site_when_invoked` |
| SIM-0205 | 3 | partial → implemented | `Igc/shipIGC.cpp:1038-1048`; `FedSrv/FedSrv.CPP:12814` | `src/Allegiance.Core/Model/Sector.cs:1384`; `src/Allegiance.Server/ServerHost.cs:1219-1227`; `P754CallbackTests.LandOnCarrierEvent_reaches_site_with_concrete_ships` |
| SIM-0206 | 3 | partial → implemented | `Igc/shipIGC.cpp:1030-1042`; `FedSrv/FedSrv.CPP:13033` | `src/Allegiance.Core/Model/Sector.cs:1174`; `src/Allegiance.Core/Model/Sector.cs:1272`; `src/Allegiance.Server/ServerHost.cs:1236-1241`; `P754CallbackTests.RescueShipEvent_reaches_site_for_ship_ship_pair`; `P754CallbackTests.RescueShipEvent_reaches_site_for_station_rescue` |
| SIM-0209 | 3 | partial → implemented | `Igc/igc.h:4863`; `FedSrv/FedSrv.CPP:13589-13616` | `src/Allegiance.Core/Model/Sector.cs:1515`; `src/Allegiance.Server/ServerHost.cs:1250-1254`; `P754CallbackTests.HitTreasureEvent_reaches_site_with_concrete_args` |
| SIM-0212 | 3 | partial → implemented | `Igc/shipIGC.cpp:280-283` | `src/Allegiance.Core/Model/Sector.cs:820`; `src/Allegiance.Core/Model/Sector.cs:1372`; `src/Allegiance.Server/ServerHost.cs:1262-1266`; `P754CallbackTests.ContinueRipcord_and_UseRipcord_reach_site` |
| SIM-0213 | 3 | partial → implemented | `Igc/shipIGC.cpp:285-289` | `src/Allegiance.Core/Model/Sector.cs:820`; `src/Allegiance.Core/Model/Sector.cs:1372`; `src/Allegiance.Server/ServerHost.cs:1276-1280`; `P754CallbackTests.ContinueRipcord_and_UseRipcord_reach_site` |
| SIM-0214 | 3 | partial → implemented | `Igc/igc.h:4868`; `Igc/magazineigc.cpp:118` | `src/Allegiance.Core/Sim/Ship.cs:4091-4102`; `src/Allegiance.Server/ServerHost.cs:1288-1292`; `P754CallbackTests.FireMissile_reaches_site_when_missile_fired` |

**Implementation notes:**

- **TerminateModelEvent** is raised from `Sector.Remove` while the model still holds its mission/cluster/side links, matching `CmodelIGC::Terminate`.
- **TerminateMissionEvent** is raised from `Server.Lobby.RotateMissionAfterGameOver` before peers are migrated, matching `CmissionIGC::Terminate`.
- **LandOnCarrierEvent**, **RescueShipEvent**, and **HitTreasureEvent** are invoked by the existing sector detection passes (`ResolveCarrierLandings`, `ResolveRescueEvents`, `ResolveStationRescueEvents`, `ResolveTreasurePickups`). Each `ServerHost` override returns the safe default (`true` for carrier/rescue/treasure, `false` for queued ripcord use) and raises a typed hook, leaving the established drain/perform paths intact.
- **ContinueRipcord** / **UseRipcord** were already called by `Sector.ResolveRipcordCountdowns`; this wave adds concrete `ServerHost` overrides so the queued host has a real callback binding rather than relying on the interface default.
- **FireMissile** is now invoked from `Ship.TryFireMissile` after lock reset and before missile creation. The port has no mountable magazine model, so the `IMagazineIgc` argument is the `MagazineSentinel` marker.
- `Mission` now implements `IMissionIgc` and `Treasure` implements `ITreasureIgc` so the seam signatures can carry concrete instances; `IIgcSite.RescueShipEvent` accepts a nullable rescuer and `FireMissile` accepts a nullable target, matching native usage.

## Scorecard before → after

Both scorecards validate all **473** simulation rows. Partial earns zero credit; weights, native rules, and row identities are preserved.

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | **63.08% (123/195)** → **75.38% (147/195)** | **41/65** → **49/65** |
| constants | 98.52% (133/135) → 98.52% (133/135) | 38/39 → 38/39 |
| periodic | 51.06% (24/47) → 51.06% (24/47) | 7/15 → 7/15 |
| sounds | 83.45% (459/550) → 83.45% (459/550) | 170/212 → 170/212 |
| updates | 83.95% (293/349) → 83.95% (293/349) | 72/87 → 72/87 |
| warnings | 100.00% (10/10) → 100.00% (10/10) | 2/2 → 2/2 |
| **TOTAL** | **81.03% (1042/1286)** → **82.89% (1066/1286)** | **330/420** → **338/420** |

SIM-0014 remains **partial**: the native custom-hull loader at `FedSrv/FedSrv.CPP:1676` scales raw ship-type ripcord speed by `c_fcidRipcordTime`, but the port loads pre-scaled `DataHullTypeIGC` records from the static core. Applying the constant again would double-scale existing cores (verified by the P479/P676 ripcord countdown tests, which expect the authored core value). No runtime custom-hull generator exists to consume the constant faithfully, so the row stays open.

## Verification

Whole-solution build: `dotnet build AllegianceCore.slnx --nologo -m:1 -v:q -p:UseSharedCompilation=false -p:NuGetAudit=false` completed with **0 warnings / 0 errors**. NuGet audit was disabled because the sandbox cannot reach `https://api.nuget.org/v3/index.json`; no product warning was suppressed.

Linux x64 self-contained native AOT publish: `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -p:UseSharedCompilation=false -p:NuGetAudit=false -o .scratch/aot3` **succeeded with no IL2026/IL3050 warnings**.

**Focused verification:** `P754CallbackTests` (8 new methods / 8 cases) passed **3/3 isolated invocations** (24 case executions, zero failures/skips). Affected existing callback/sector tests were covered by the fast-tier run below.

**Fast-tier run:** `NuGetAudit=false ./tools/run-tests.sh --tier fast --results-dir .scratch/fast2`:

| Assembly | Passed | Failed | Skipped/NotExecuted |
| --- | ---: | ---: | ---: |
| Allegiance.Lobby.Tests | 19 | 0 | 0 |
| Allegiance.ClientCore.MonoGame.Tests | 2528 | 0 | 3 NotExecuted |
| Allegiance.ClientCore.Tests | 1479 | 1 | 0 |
| Allegiance.Core.Tests | 4867 | 0 | 0 |
| fast/quarantine extras | 12 | 0 | 0 |

The single ClientCore failure (`SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`) passed **3/3 isolated reruns**, satisfying the load-flake rule. No assertion, deadline, quarantine trait, or product behavior was changed to suppress it.

No build, test result, cache, or temporary artifact was placed in `/tmp`; all scratch data is under `.scratch/` inside the worktree, which is listed in `.git/info/exclude` and not committed. Raw fast-tier logs and AOT output remain in `.scratch/`; compact summaries are referenced above.

## Review fixes (P754b)

Claude's review of this wave found that six rows marked `implemented` only proved the *seam*: the `ServerHost` overrides
raised `Action` hooks nobody subscribed to and returned a constant, while native puts the real rule (and often the whole
action) inside the callback. The fix round re-routed each one through the port's existing production logic so the return
value comes from the real rule, and re-pinned it with a test. Full detail, native citations and verification:
`docs/reports/P754B_SIM17_REVIEW_FIXES.md`.

| Row | P754 state | P754b fix |
| --- | --- | --- |
| SIM-0182 `TerminateModelEvent` | raised from `Sector.Remove` (also the sector-**move** primitive) | `Sector.Terminate(model)` raises it and then evicts, `CmodelIGC::Terminate`'s order (`Igc/modelIGC.cpp:60-61`); moves raise nothing; every destruction funnel (ship death, logoff, treasure destroy, ordnance/probe/mine/chaff expiry, in-tick kills) uses it |
| SIM-0205 `LandOnCarrierEvent` | constant `true` + unsubscribed hook | the callback *is* the native landing: `ServerHost.LandOnCarrierEvent` → `Server.PerformRelaunch` (the port of `FedSrv.CPP:12814-13031`), returns native's `true`; the P117 drain step was removed |
| SIM-0206 `RescueShipEvent` | constant `true` + unsubscribed hook | returns the native rule `pstation ? DockWithStationEvent(...) : false` (`FedSrv.CPP:13073-13075`) via the Server's `FindFriendlyStation`; also fixed P754's own gap — only the parallel rescue latch consulted the site, so the default serial path bypassed it |
| SIM-0209 `HitTreasureEvent` | constant `true`; flag gates sat in the sector | the flag refusal gates (`FedSrv.CPP:13562-13568`) moved into `ServerHost.HitTreasureEvent`, which now returns `false` for a second/own-side flag |
| SIM-0212 `ContinueRipcord` | constant `true`; rule in a private sector helper | rule moved to `RipcordRules.AnchorStillLegal` (`FedSrv.CPP:13379-13437`), returned by `ServerHost.ContinueRipcord` (and the seam default); the sector consumes only the boolean and keeps native's abort side effects |
| SIM-0213 `UseRipcord` | constant `false`, deferred to a drain | `ServerHost.UseRipcord` performs `Server.PerformRipcordTeleport` (the port of `FedSrv.CPP:13440-13504`) and returns native's `true`, consuming the attempt inline |

SIM-0214 `FireMissile` was not in the review's list (it returns `void`) and is unchanged. This fix round changes no row status, weight or
identity, so the scorecard's *credit* is unchanged; the absolute percentages quoted above are the P754-branch snapshot — after `master`'s
P751 merge re-scored neighbouring rows, the same matrix reads 78.46 % callbacks / 83.49 % total (see the P754b report).
