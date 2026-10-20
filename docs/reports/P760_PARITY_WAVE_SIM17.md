# P760 — Simulation parity wave 17: site callbacks (server + client)

**Branch:** `pi/p760` based on `master` (`e1c528b2` or later).  
**Scope:** route the 12 `callbacks` rows listed in `P760_BRIEF.md` through the
native `IIgcSite` callbacks with their native gates, instead of the scattered
direct paths that existed in the port.

## What changed

### Server site (`FedSrvSiteBase`)

| Row | Native rule | Port file:line | Status | Test |
|-----|-------------|----------------|--------|------|
| SIM-0150 | `CFSShip::SetShipUpdate` warp re-entry handshake (`fsship.cpp:1185-1191`, `fsship.h:188-209`) | `src/Allegiance.Core/Sim/Ship.cs:604`, `src/Allegiance.Server/Server.Dispatch.cs:2302`, `src/Allegiance.Core/Model/Sector.cs:3629` | **partial → partial with handshake implemented** | `P760ServerCallbacksTests.HitWarpEvent_enforces_mass_limit_and_warp_handshake` |
| SIM-0173 | `ForwardChatMessage` (`FedSrv.CPP:13652-13693`) | `src/Allegiance.Server/ServerHost.cs:572`, `src/Allegiance.Server/Server.Callbacks.cs:12` | **partial → implemented** | `P760ServerCallbacksTests.SendChat_routes_group_and_command_targets` |
| SIM-0203 | `DockWithStationEvent` (`FedSrv.CPP:13087-13140`) | `src/Allegiance.Server/ServerHost.cs:852`, `src/Allegiance.Server/Server.Docking.cs:1828` | **implemented (expanded)** | `P760ServerCallbacksTests.DockWithStationEvent_runs_ChangeStation` |
| SIM-0208 | `CFSShip::HitWarp` (`fsship.cpp:71-137`) | `src/Allegiance.Server/ServerHost.cs:1280`, `src/Allegiance.Core/Model/Sector.cs:3629` | **partial with mass-limit + handshake** | `P760ServerCallbacksTests.HitWarpEvent_enforces_mass_limit_and_warp_handshake` |
| SIM-0219 | `ChangeStation` (`FedSrv.CPP:13179-13311`) | `src/Allegiance.Server/ServerHost.cs:1445` | **partial → implemented** | `P760ServerCallbacksTests.ChangeStation_repairs_launches_and_resets_first_dock` |
| SIM-0220 | `ChangeCluster` (`FedSrv.CPP:13513-13528`) | `src/Allegiance.Server/ServerHost.cs:1428`, `src/Allegiance.Server/Server.Combat.cs:2393` | **partial → implemented** | `P760ServerCallbacksTests.ChangeCluster_notifies_on_sector_exit` |
| SIM-0221 | `CommandChangedEvent` (`FedSrv.CPP:13699-13732`) | `src/Allegiance.Server/ServerHost.cs:1517`, `src/Allegiance.Server/Server.Dispatch.cs:1435` | **partial → implemented** | `P760ServerCallbacksTests.CommandChangedEvent_forwards_NPC_order_change` |
| SIM-0226 | `LoadoutChangeEvent` (`FedSrv.CPP:13530-13545`) | `src/Allegiance.Server/ServerHost.cs:590` | **implemented (AoE target re-send added)** | covered by existing loadout tests; no new test added |

### Client site (`WinTrekClient`)

| Row | Native rule | Port file:line | Status | Test |
|-----|-------------|----------------|--------|------|
| SIM-0197 | `TargetKilled` (`trekigc.cpp:2934-2974`) | `src/Allegiance.ClientCore/State/ClientWorld.cs:4100`, `src/Allegiance.ClientCore/State/ClientWorld.Callbacks.cs:36`, `src/Allegiance.ClientCore.Game/Game1.Targeting.cs:852` | **partial → implemented** (P760b: queue + native-fidelity retarget; see Review fixes) | `P760ClientCallbacksTests.KillShip_enqueues_TargetKilledModel_with_victim_type_and_id` |
| SIM-0176/0177/0178/0180 | `PlaySoundEffect`, `PlayNotificationSound`, `PlayVisualEffect` (`trekigc.cpp:4955-5003`) | (P760b: the wave's gated methods/events deleted as dead code) | **partial** | existing producers still route through direct cue planners (see Unfinished) |
| SIM-0195 | `PostNotificationText` (`trekigc.cpp:2994-3018`) | `src/Allegiance.ClientCore/State/ClientWorld.Callbacks.cs` (ship-id overload; P760b deleted the dead `IModelIgc?` overload) | **partial** | local-ship gate kept; equipment/reload producers not routed |
| SIM-0172 | `ReceiveChat` (`trekigc.cpp:4467-4910`, `clintlib.cpp:2160`) | `src/Allegiance.ClientCore/State/ClientWorld.cs (Apply ChatMessage/ChatBuoy)` | **partial** | not touched; gaps remain |

### Data model / helper additions

- `DataWarpIGC.MassLimit` runtime field (default `-1`, unlimited) so aleph mass
  limits can be enforced.
- Native-style `WarpState` enum and `Ship.PlayerWarpState`, `TimeNextWarp`, and
  `WarpReady` property.
- `Ship.IsUsingAreaOfEffectWeapon` helper for the AoE command-re-send rules.
- `IIgcSite.SendChat`, `ChangeStation`, and related signatures made nullable to
  match native semantics where `NULL` station/target is valid.

## Verification

- **Whole-solution build:** `dotnet build AllegianceCore.slnx` → 0 warnings, 0 errors.
- **AOT publish:** `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot` → success, no `IL2026`/`IL3050` warnings.

## Review fixes (P760b)

Fix round on this wave from Claude's review (branch `pi/p760b`, rebased over
`master` — the merge kept master's P758 `TreasureMountSource` wiring and the
P754 `SIM-0209` row, and this wave's `SIM-0208` row).

| # | Fix | Where | Test |
|---|-----|-------|------|
| 1 | **Crash (regression on master):** `UndockShipState` called `_host.ChangeStation(...)`, which NREs in a Server that was never started (`_host` is null) — `P753SimUpdateTests.Docked_npc_launch_forced_by_goto_not_mine` failed 3/3. Routed through the mission site (`ship.Mission?.Site.ChangeStation(...)`), like the `CommandChangedEvent` calls. | `src/Allegiance.Server/Server.Docking.cs:1936` | `Docked_npc_launch_forced_by_goto_not_mine` 3/3 |
| 2 | **NPCs keep their loadout:** `UpgradeNpcShipAtStation` is now the native `UpgradeShip` body (`FedSrv.CPP:11554-11639`) — a hull upgrade nukes the loadout, swaps the hull and buys the new hull's preferred parts (`GetPreferredPartTypes` → `CanBuy` (`stationIGC.h:383-391`) → `GetSuccessor` → every free mount, weapons into all `MaxWeapons` hardpoints). The upgrade also runs when the dock gates refuse the dock (native calls it after the gate if/else, `FedSrv.CPP:13233-13240`). | `src/Allegiance.Server/ServerHost.cs:1715,1762,1607` | `ChangeStation_npc_hull_upgrade_buys_the_preferred_loadout`; `ChangeStation_npc_upgrade_runs_when_the_dock_gates_refuse` |
| 3 | **SIM-0220:** native routes every `SetCluster` through `ChangeCluster` (`shipIGC.h:254`, `FedSrv.CPP:13513-13528`). The port's `SetCluster` analogue — `Server.MoveShipToSector` (ripcord teleport, teleport, rescue, lifepod respawn) — now routes through the site too, announcing `SDR_LEFTSECTOR`. Dock-bound relocations pass `announceExit:false`: native nulls the cluster first there (`FedSrv.CPP:12045 SetCluster(NULL)` → `ChangeCluster(old, NULL)` announces nothing). | `src/Allegiance.Server/Server.Docking.cs:1349` | `MoveShipToSector_announces_the_sector_exit_like_a_ripcord_move` |
| 4 | **SIM-0197 threading + fidelity:** `Apply(KillShip)` now only ENQUEUES a `TargetKilledModel`; `Game1.Update` drains it (`UpdateTargetKilled`) and runs the retarget on the game thread (the old seam mutated `Game1._autoTarget`, called `CommitTargetForPlayer` and read the own pose on the receive thread). Fidelity to `trekigc.cpp:2934-2974`: victim compared against the c_cmdCurrent slot (`ClientWorld.MyCurrentCommand`, HUD-8) for any model type, not `_autoTarget`; source ship (carrier when boarded) gated on being in a cluster; similar-target mask (`WinTrek.cpp:267-284`) with rescue → miner/builder → any on the enemy-ship mask; the dead model never re-picked; `SetTarget` commit; autopilot re-engages when the plan target was the killed model. | `src/Allegiance.ClientCore/State/ClientWorld.Callbacks.cs:36`; `src/Allegiance.ClientCore.Game/Game1.Targeting.cs:852` | `P760ClientCallbacksTests` (queue seam); handler covered by the drain path |
| 5 | **SIM-0221 fire-on-change:** new `Ship.SetCommand` (port of `shipIGC.h:721-790` for the c_cmdCurrent/c_cmdAccepted slots) fires the site's `CommandChangedEvent` ONLY when the cid or target changed (`shipIGC.h:784-786`); the ORDER_CHANGE handler sets slots through it instead of firing on every inbound order; the `LoadoutChangeEvent` AoE re-send uses the ship's REAL `c_cmdCurrent` cid (new `Ship.CurrentCommandId`, `GetCommandID(c_cmdCurrent)`, `FedSrv.CPP:13541`), not an invented `CidAttack`/`None`. Residual: this port's NPC AI writes the underlying slots directly and stays silent (native fires from every `SetCommand` caller) — recorded in the matrix. | `src/Allegiance.Core/Sim/Ship.cs:1542`; `src/Allegiance.Server/Server.ShipCommands.cs`; `src/Allegiance.Server/ServerHost.cs:593` | `SetCommand_fires_CommandChangedEvent_only_on_change`; `LoadoutChangeEvent_aoe_resend_uses_the_real_current_cid` |
| 6 | **Dead client methods deleted:** `PlaySoundEffect` (×2), `PlayNotificationSound`, `PlayVisualEffect`, `PostNotificationText(IModelIgc)` and `TargetKilled(IModelIgc)` gated on the Core `Ship` type while client entities are `ShipEntity`, nothing subscribed to their events and no producer called them. Deleted (their rows stay `partial`); the ship-id `PostNotificationText` overload used by P709 stays. | `src/Allegiance.ClientCore/State/ClientWorld.Callbacks.cs` | — (rows stay partial) |
| 7 | **CHAT_SHIP no echo:** the recipient set (extracted as `Server.ShipChatRecipients`) forwards to the source ship only when the sender is a gunner (`FedSrv.CPP:3488-3493`) and to each child ship except the sender (`:3496-3505`). | `src/Allegiance.Server/Server.Callbacks.cs` | `ShipChatRecipients_never_include_the_sender` |
| 8 | **Warp handshake, one clock:** `TimeNextWarp` is armed and compared in MISSION time (native `g.timeNow`, `fsship.cpp:1188`) — the same clock `LastWarpTime` is stamped with and `Ship.OnUpdate` compares against — instead of mixing the server receive clock (`SimNow`). The row stays `partial`: the 2 s `NoUpdate→Ready` fallback in `Ship.OnUpdate` exists so a player with no real peer-update stream (tests, scripted/direct server-side ship manipulation) is not permanently stuck in `NoUpdate` and unable to ever warp. | `src/Allegiance.Server/Server.Dispatch.cs:2300`; `src/Allegiance.Core/Sim/Ship.cs:8064` | existing warp handshake tests |
| 9 | **Evidence moved** out of `.scratch/` to `~/allegiance-evidence/P760/` (outside the repo); the link below points there and `.scratch/` is deleted. | — | — |

### P760b verification

- Whole-solution build: 0 warnings, 0 errors.
- AOT publish (`-p:PublishAot=true`, linux-x64): success, no `IL2026`/`IL3050`.
- New tests 3× alone: `P760ServerCallbacksTests` (13) 3/3, `P760ClientCallbacksTests` (3) 3/3, `Docked_npc_launch_forced_by_goto_not_mine` 3/3.
- Affected tests 3× alone: `OrderChange{Current,Accepted,ReturnToBase}LiveTests` + `GhostShipSectorMembershipTests` + `P754CallbackTests` + `P753SimUpdateTests` (29) 3/3; `CrossSide_sector_stays_correct_across_warp_return` + `SameSide_WarperLeavesSector_WatcherLatchesGone_OwnPeerNotHidden` 3/3.
- Fast tier (`tools/run-tests.sh --tier fast`): 9262 tests, PASS, 273.9 s.
- Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`, Live-tier): 5/5 (~1 m 50 s each).

### P760b scorecard

```text
$ python3 scripts/parity-scorecard.py
callbacks | 86.15% (168/195) | 86.15% (56/65)
TOTAL     | 88.24% (1133/1284) | 85.92% (360/419)
```

(Relative to the pre-merge wave numbers at the top: the merge with `master`
brought the P754/P758 implemented rows; P760b's own row edits keep
SIM-0197/0219/0220/0221 `implemented` with the fix-round evidence, SIM-0150
and SIM-0208 `partial` with the single-clock note, and the deleted dead
client-method rows `partial`.)

## Wave verification (as run on `pi/p760`, before the fix round)

- **Whole-solution build:** `dotnet build AllegianceCore.slnx` → 0 warnings, 0 errors.
- **AOT publish:** `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot` → success, no `IL2026`/`IL3050` warnings.
- **New tests 3× alone:**
  - `P760ServerCallbacksTests` (7 tests) — 3/3 passed.
  - `P760ClientCallbacksTests` (3 tests) — 3/3 passed.
- **Affected live/warp tests 3× alone (docking/warp/order changes):**
  - `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`
  - `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target`
  - `ClientWarpApproachSlowHullLiveTests.Slow_board_hull_26_transits_when_the_warp_approach_flies_through_the_aleph`
  - `ClientWarpApproachDodgeLiveTests.Client_bot_with_shared_dodge_transits_two_hops_through_real_alderan_field`
  - `ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector_WatcherLatchesGone_OwnPeerNotHidden`
  - all 3/3 passed.
- **Fast tier:** `tools/run-tests.sh --tier fast` → 9024 tests passed in 272.6 s.
- **Showcase never-parks:** `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` — 5/5 passed (~1 m 50 s each).

## Scorecard

```text
$ python3 scripts/parity-scorecard.py
sim-rules-matrix.csv: validated 473 rows
callbacks | 73.85% (144/195) | 73.85% (48/65)
TOTAL     | 82.79% (1063/1284) | 80.43% (337/419)
```

The `callbacks` area gained credit from the rows moved from `partial` to
`implemented`. A precise "before" snapshot was not captured, but the change is
visible in the CSV diff: SIM-0173, SIM-0197, SIM-0219, SIM-0220, SIM-0221 are
now `implemented`; SIM-0208 and SIM-0150 remain `partial` with expanded
implementation notes.

## Unfinished / honest gaps

- **Client sound/visual callbacks (SIM-0176/0177/0178/0180):** the existing
  client cue planners and notice producers are not routed through site
  callbacks. The port's sound/visual architecture uses separate seams (e.g.
  `ClientSoundEventKind`, `SoundManager`, HUD feedback lines) that do not
  naturally feed an `IModelIgc`-based site callback. P760b additionally
  DELETED the wave's gated consumer methods + events: they gated on the Core
  `Ship` type (client entities are `ShipEntity`), nothing subscribed and no
  producer called them — dead code, not a callback. Rows stay `partial`.
- **PostNotificationText producers (SIM-0195):** the native local-ship text
  consumer is implemented for the ship-id form, but the native producers
  (`afterburnerIGC.cpp:120`, `cloakigc.cpp:76`,
  `dispenserigc.cpp:99`, `magazineigc.cpp:97`, `shieldIGC.cpp:83`,
  `shipIGC.h:612/618/642`) are not routed through it. P760b deleted the wave's
  dead `IModelIgc?` overload (Core-`Ship` gate, no callers). Row stays `partial`.
- **ReceiveChat (SIM-0172):** not addressed in this wave; remains `partial`.
- **Allied rearm/relaunch in `ChangeStation`:** the `#ALLY` branch in
  `FedSrv.CPP:13242+` is not implemented (noted as out-of-scope-adjacent).
- **Warp wobble/entry-speed fine-tuning:** zero wobble simplification and the
  existing deterministic exit-speed formula remain; SIM-0208 stays `partial`.

## Evidence

Raw test logs and the AOT publish artifacts live OUTSIDE the repo (evidence
size rule) under `~/allegiance-evidence/P760/`:

- `p760b-new-tests-3x.txt` — new tests 3× alone (P760ServerCallbacksTests,
  P760ClientCallbacksTests, Docked_npc_launch_forced_by_goto_not_mine).
- `p760b-affected-tests-3x.txt` — affected ORDER_CHANGE / sector-move /
  callback tests 3× alone.
- `p760b-warp-departure-tests-3x.txt` — warp-return + sector-departure live
  tests 3× alone.
- `p760b-fast-tier-summary.json` — the fast-tier run summary.
- `p760b-showcase-never-parks-5x.txt` — the never-parks showcase 5×.

Nothing is kept in `.scratch/` (deleted at the end of the task, per the
scratch-space rule).
