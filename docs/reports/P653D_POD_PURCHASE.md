# P653D — docked pod purchase regression

Base: supplied `integ3` working tree at `6c3e54adcd8508c3f085e13e695b5875f08a4356`,
branch `codex/p653d`. No commits or pushes.
Native citations below are relative to `~/Development/Allegiance/src/`.

## Root cause and reproduction

**The failing condition is `ship.Dead`, after a pod's true death/restart.**
Lethal `Ship.ReceiveDamage` sets fraction to zero before the kill callback.
`BroadcastKillShip`'s true-death branch replaces the hull with the side's pod and
sets its station, but previously left fraction zero. P653 H-06 then refused every
new hull. The in-space ejection branch already restored fraction; this explains
why simple ejection/return paths and initial purchases could still work.

Reproduced over real TCP with PCore001, Iron Coalition civilization 25, **pod 444**,
and a real friendly restart station. Destroy the fighter, then destroy its ejected
pod; the production restart docks the replacement pod. Send the real hangar
`ShopModel.TryLaunch` / `ConnectionSession.SendBuyLoadout` purchase. With the fix
removed, the ordinary ejected-pod docking case passes and the true-death case fails:

```text
P653D pre-buy killPod=True hull=444 dead=True parent= station=1051
purchase preconditions: dead_fraction (hull=444, fraction=0) — REFUSED
```

The diagnostic evaluates every purchase predicate separately. No parent, station,
side, part-count or negative-amount failure accompanied this refusal. The MCP
production `BotManager` path also reproduced three refusals/retries and remained
in its pod before the fix (initial reproduction used civilization 1, pod 33).
Logs: `/tmp/p653d-444-baseline.log`, `/tmp/p653d-repro2.log`.
This is a local reproduction of the reported failure signature; the clash43
rig's original 1,041 refusals were not individually reclassified here.

Why the old coverage missed it: the original H-06 pod test only called
`ChangeHull`, leaving initial health intact. The MCP proof called `KillShipEvent`
directly rather than dealing lethal damage, then explicitly invoked a docking
callback whose restock heals the ship. Neither exercised a zero-health restart.

## Native rules and fix

- `FedSrv/FedSrv.CPP:6153-6161`: require a mission, no flying cluster, a docked
  station, non-ghost player and nonnegative side. The port keeps a sector reference
  while docked, so `StationId` represents the native dock/cluster distinction.
- `Igc/shipIGC.h:2465-2469`: **IsGhost is deaths exceeding the mission's lives
  allowance**, not fraction zero. The port has no equivalent ghost/lives state;
  `Dead` is an additional defensive integrity check, not a translation of IsGhost.
- `FedSrv/FedSrv.CPP:6163-6172`: native also handles allied-base rearm with children
  and requires the purchasing ship to have no parent. No pod-hull prohibition exists.
- `FedSrv/FedSrv.CPP:6176-6180`, `FedSrv/fsship.h:27-32`: validate loadout framing
  and cap it at twenty parts. `Igc/shipIGC.h:992-1004,1026-1038` requires the
  station to sell a different hull and validates/mounts parts with successors.
- `FedSrv/FedSrv.CPP:11770-11785`: with eject pods enabled, lethal damage to
  an unboarded lifepod takes true death; a normal player hull instead ejects.
- `FedSrv/FedSrv.CPP:11941-11942,12045-12065`: ejection/restart assigns the pod
  using `SetBaseHullType`. **`Igc/shipIGC.cpp:1435-1436` resets fraction to 1**, even
  on a repeated assignment of the same pod hull; this function has no same-hull
  early return (`:1352-1359`).

`Server.Combat.cs` now restores fraction immediately after the true-death pod
assignment, matching the existing ejection restoration. `ChangeHull` keeps its
existing hull-only API; its documentation and the ejection comment now describe
the actual native reset and the callers' responsibility. This fixes the lifecycle
invariant for all downstream gates instead of accepting damaged lifecycle state.

`ApplyLoadout` preserves every prior protection and now reports all failed
predicates by name: `not_docked`, `dead_fraction`, `boarded_parent`, `invalid_side`,
`unresolved_station`, `unresolved_side`, `too_many_parts`, `negative_part_amount`.
Hull/fraction context accompanies the refusal. Refusal ACKs remain unchanged.

H-06 dock/side/parent/station resolution, H-07 mount compatibility/uniqueness,
H-08 bounded nonnegative quantities, native loadout size, station tech/successors,
player-hull eligibility, wallet/overflow and paid-credit protections all remain.
Native's shop excludes negative hull groups (`WinTrek/load.cpp:1603-1606`), and
its purchase checks funds (`FedSrv/FedSrv.CPP:6193-6199`); integer-overflow and
refund-provenance guards are retained port integrity protections.
Negative quantities are defensive input validation, not a native negative-amount
sentinel accepted by these clients. Native quantity ceilings remain relevant:
`Igc/magazineigc.h:107-113`, `Igc/packigc.h:102-123`.

## Coverage and other-gate audit

| Path | Result / native basis |
| --- | --- |
| Human hangar | New `P653DPodPurchaseLiveTests`: real TCP, initial launch, lethal fighter damage, either pod placement at home with actual docking pass or lethal pod damage with production restart; select a real hull and default gun through ShopModel; assert ACK `FBoughtEverything=true`, `FLaunch=true`, hull granted, server/client undocked, alive/unboarded. Uses the same model/sender as `Game1.Panes.cs:286-288`; no graphical window is needed. Controlled free/sellable shop records isolate the lifecycle from research/funds. |
| MCP lifecycle | Strengthened `Mcp_debug_bot_buys_relaunches_and_fights`: actual lethal damage; one returning ejected pod and **three simultaneously connected bots**, each undergoing lethal pod death, home restart, the manager's own purchase/relaunch and subsequent projectile combat. Assert each client's successful purchase/launch ACK and alive/unboarded ship. Harness bypasses tech only; uses production debug spawn and real transport. |
| H-06 hostile states | Existing flying/dead/boarded/missing-station refusals retained; added lobby side, unresolved side and missing station-type cases. Each checks unchanged inventory/wallet and a legitimate purchase/launch after repair of the fixture. |
| Parts purchase | Shares `ApplyLoadout`, so the same stale `Dead` caused refusal before part validation. Restart restoration fixes it; hangar/MCP purchases include actual weapon parts. H-07/H-08 retain negative, duplicate, incompatible and over-capacity protections. Native parts: `Igc/shipIGC.h:1026-1044`. |
| SWAP_PART / reload / drop | H-22 requires flying, unboarded inventory operations; `Dead` is a port integrity guard. After valid restart/purchase, no stale `Dead` remains. New human proof unmounts into cargo and mounts back over TCP after launch. Existing H-09/H-22 and swap regressions cover positive/hostile operations. Native cluster/parent/ranges: `FedSrv/FedSrv.CPP:6297-6313,6459-6483,6505-6516`. |
| PROMOTE | `HandlePromote` checks Started and searches only the requester's child list; it adds no `Dead` check. `Ship.Promote` clears the promoted child's parent and transfers hull/fraction, then reparents the former commander. No additional P653 post-restart refusal found. Native: `FedSrv/FedSrv.CPP:6353-6377`, `Igc/shipIGC.cpp:3493-3574`. |
| Launch | BUY_LOADOUT launch occurs only after successful apply, which restores health; direct DOCKED/undock uses station repair/refuel. No extra P653 `Dead` gate there. Native dock/launch gates: `FedSrv/FedSrv.CPP:7031-7053`; launch repair: `:13189-13191`, `Igc/stationIGC.cpp:449-489`. |
| Boarding / teleport / rescue | Boarding and teleport also check `Dead`; a zero-health restart incorrectly blocked these too, and the same restoration removes that cause. Root-pilot ejection/restart leaves ParentShip null in both new proofs. Station rescue preserves the already-live ejected pod. Native boarding: `FedSrv/FedSrv.CPP:6384-6410`; teleport station/parent/restart: `:7040-7077`; station rescue: `:13033-13037`. |

Separate pre-existing parity gaps observed during the audit: direct DOCKED dispatch
still lacks some native stage/side/parent checks (compare `FedSrv/FedSrv.CPP:7033-7045`);
the kill/ejection code still documents/simplifies boarded-child death rather than
implementing native's parent detachment/child walk (`:11679`, `:11941`, `:12048`).
These are not evidence that the purchase parent guard should be relaxed. This
change covers the reported root-pilot pod lifecycle, not carrier passenger death
or a new finite-lives/ghost subsystem.

## Verification

`dotnet build AllegianceCore.slnx --nologo` passed with zero warnings/errors.
Each affected method ran alone in its own test process three consecutive
times (theory rows included):

| Focused assembly | Methods | Cases per repetition | Result |
| --- | ---: | ---: | --- |
| Core | 52 | 62 | 3/3; 186 passes |
| ClientCore MCP / death latch | 3 | 4 | 3/3; 12 passes |
| MonoGame hangar lifecycle | 1 | 2 | 3/3; 6 passes |

**204 final focused case executions passed across 56 methods; no failures or skips.** Core selection:
P653EconomyLoadoutIntegrityTests, LivePlayerDeathRespawnTests,
LiveInSpaceEjectPodAliveTests, LiveLifepodOxygenTests, LiveP393DebugRespawnTests,
LiveShipDeathFinalizeTests, SwapPartTests, LivePromoteTests,
LiveTeleportDockTests and LiveRelaunchTests.

Complete suites ran sequentially, unfiltered, with `dotnet test <project>
--no-build --logger trx`:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4305 | 0 | 0 |
| ClientCore final (showcase enabled) | 935 | 0 | 0 |
| MonoGame | 2127 | 0 | 3 existing optional Meshy-pack checks |

The first ClientCore pass had **932 passed / 2 failed / 1 opt-in skip**. Both
failures were `DeadLatchRespawnLiveTests` methods:
`Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock` and
`Remote_client_sees_victim_again_after_death_and_respawn`. Each failed **3/3
alone** because their shared helper returned `ship.Dead` *after synchronous
restart*. That expectation preserved this regression. The helper now verifies
lifepod input, `DamageResult.Killed`, full health and a docked replacement pod.
Both passed **3/3 alone** after correction. All existing client death-latch,
frozen-simulation, remote visibility and accepted-flight-counter assertions remain.
This was a deterministic test-premise correction, not a load-flake classification.

The separate opt-in **15v15 MCP showcase passed** (1/1, no skip, 21 seconds).
The final unfiltered ClientCore pass enables `ALLEGIANCE_LIVE_P660_SHOWCASE=1`
to include that proof in the assembly run too; **935/935 passed**. Final suites
therefore total **7,367 passed, zero failed, three existing optional-pack skips**.
No load-flake exemptions were needed. Core and MonoGame passed on their
first complete runs; the subsequent build/test correction changes only the
ClientCore test helper, not product behavior.

Evidence: `/tmp/p653d-build-final.log`, `/tmp/p653d-focused.json`,
`/tmp/p653d-focused-{Core,ClientCore,MonoGame}-<method>-{1,2,3}.log`, and
`/tmp/p653d-results/*.trx`. Additional evidence:
`/tmp/p653d-build-corrected-tests.log`, `/tmp/p653d-deadlatch-progress.log`,
`/tmp/p653d-{stale,corrected}-helper-<method>-{1,2,3}.log`,
`/tmp/p653d-full-Core.log`, `/tmp/p653d-full-MonoGame.log`,
`/tmp/p653d-full-ClientCore-final.log`, `/tmp/p653d-showcase.log`.
All tests run locally with real sockets enabled. Rig/221/200v200 work is reserved
for Claude as instructed. No tests were disabled or assertions weakened.
