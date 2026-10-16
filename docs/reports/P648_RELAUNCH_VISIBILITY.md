# P648 — invisible and frozen ships at 200v200: the relaunch death latch, the per-viewer update budget, and the podded viewer

**Branch:** `master` · **HEAD:** `45eeb8f4` · **Date:** 2026-10-14
**Scope:** how a ship that dies and relaunches is (re)learnt by the client — same-side peers and enemy peers — plus the per-viewer measurement instrumentation the task demands.

---

## TL;DR

The live clash36 symptom ("invisible ships shooting", "frozen ships I can see but get no updates") is caused by the **client's death latch surviving a relaunch** on the same-side path, and by **enemies holding a relaunched ship's stale hull**. Two concrete gaps were found, fixed, and locked with failing-first tests:

1. **Client dropped FM_S_LOADOUT_CHANGE(96)** — the server's same-side broadcast of the relaunch's new hull (the exact message the clash36 trace shows: `LOADOUT_CHANGE(96) hull 15 -> 195 same-side peer(s)`). It was a documented *unconsumed* message (P627). Because the client ignored it, a same-side peer that had latched the ship dead (`SHIP_DELETE SDR_KILLED`) kept it **dead** (not drawn, not targetable) while the server kept delivering its projectiles → **invisible ships shooting**, with the pre-relaunch hull still attached.
2. **Server never re-exports a changed hull to enemies promptly.** The cross-side identity `EXPORT(26)` is latched once per (peer, ship). Enemies never receive `LOADOUT_CHANGE` (same-side only), so their only prompt hull-freshness channel was the slow P369 self-heal (30 s). A relaunched ship with a different hull therefore rendered to enemies with the old hull until the self-heal.

Plus the **measurement deliverable**: a client-side per-viewer *placeability* diagnostic (latched-dead / never-posed / stale) with per-ship pose-age and "shots from a ship I can't place" counters, so the invisible/frozen-shooter symptom is measurable per category in a crowded local fight.

The cross-side *revive* (a latched-dead enemy becoming visible again) is handled by the existing P521 re-presence clears on `HEAVY_SHIPS_UPDATE(176)`/`EXPORT`/`SET_CLUSTER`, which were already in master at clash36 and are confirmed intact; the task's measurement instrumentation lets a rig session verify the remaining per-viewer budget/pod hypotheses quantitatively (below, "Measurement status").

---

## Why the latch exists and why native never had it

Native's client keeps **one ship object per player across lives** and never latches a permanent "dead" bit. `KILL_SHIP(105)` is kill-credit *UI* (`appmsg.cpp:2747-2772`: `TargetKilled`, death/experience bookkeeping); `SHIP_DELETE(20)` for `SDR_KILLED` merely re-homes the ship out of the cluster — `ship->SetCluster(NULL)` (`appmsg.cpp:470-490`) — leaving the object in the core. When the same id relaunches and re-enters the cluster, the client's local IGC sim simply re-seats it: alive again, no explicit "revive" needed.

This port's client is a **pure renderer with no local IGC sim**, so it needed an explicit `IsDead` latch (set by `SHIP_DELETE SDR_KILLED`, `ClientWorld.Apply(ShipDelete)`), and the P521 work added explicit clears at the re-presence signals (`SET_CLUSTER`, `ShipReset`, `HeavyShipsUpdate(176)`, `ExportShip`). The *remaining* hole this task closes is the **same-side relaunch path**, whose native signal is `LOADOUT_CHANGE(96)`.

---

## What native does (cited)

| Behaviour | Native cite | Port before P648 | Port after P648 |
|---|---|---|---|
| Client re-applies a changed hull/loadout and re-seats a cached (null-cluster, i.e. SDR_KILLED) ship on the relaunch | `appmsg.cpp:1991-2084` (`FM_S_LOADOUT_CHANGE` → `ProcessShipLoadout(cbloadout, loadout, bFirstOrder = (GetCluster()==NULL))`, `:2029-2031`; passenger re-seat `:2035-2079`) | **message dropped** (P627 known-unconsumed) | `ClientWorld.Apply(LoadoutChange)`: adopt hull/parts, re-parent, clear death latch + per-life caches when `IsDead` |
| Server broadcasts a changed whole loadout (hull + parts + passengers) | `CFSShip::QueueLoadoutChange` → `ExportShipLoadout`, `FedSrv/fsship.cpp:708-735` | port already sends it (same-side) | unchanged |
| Enemies learn a ship's identity/hull when it is visible to them | native re-exports on visibility edges (`ThingSiteImpl::ShowObject`, `FedSrv.CPP:10749-10778`) | cross-side `EXPORT` one-shot per (peer,ship) + 30 s self-heal (P369) | on `LoadoutChange.HullChange` the cross-side latch is dropped so the sweep re-exports the fresh hull promptly |

### Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Death never permanently latched; relaunch re-seats the same ship object | `appmsg.cpp:470-490`, `:1991-2084` | **design** | `IsDead` latch + explicit clears (P521) + now the LOADOUT_CHANGE revive (P648) — a pure-renderer necessity, same intent |
| `LOADOUT_CHANGE(96)` consumed and applied by the real client | `appmsg.cpp:1991-2084` | **design** | consumed; revive gated on `IsDead` so a live ship's routine part change isn't disturbed |
| Enemies re-learn a ship's hull when it visibly changes | `FedSrv.CPP:10749-10778` (ShowObject) | **design** | cross-side export latch dropped on `HullChange` → prompt re-export |
| Ship-update budget fixed per tick | `FedSrv.CPP:3844-3950` | **era constraint** | already superseded by the P632 density-aware heavy quota (`Server.Broadcast.cs`, `DeadReckonExtrapolationTicks`); see "modern alternative" |

---

## The fix

### 1. Client consumes FM_S_LOADOUT_CHANGE(96) — the same-side relaunch revive (scope 3)

`ClientWorld.Apply(Allegiance.Core.Wire.LoadoutChange)` (added) plus a router case in `ClientWorld.Apply(IFedMessage)` and a dispatch case in `ConnectionSession.OnReceive`.

Behaviour (faithful to `appmsg.cpp:1991-2084`):
- resolves `SidShip` (`NA` → own ship, `:1998-2000`);
- `ParentId = -1` (`SetParentShip(NULL)`, `:2007-2008`);
- adopts `HullId` and the mounted/cargo parts (mirror of `BuyLoadoutAck`'s P626 split);
- re-seats boarded passengers (`:2035-2079`);
- when the ship was latched **dead**, clears `IsDead`/`IsGone`, drops the dead-reckoner (so the next pose/176 re-seeds fresh), and resets the first-sighting latch — the "alive again, with hull H" revive. Gated on `IsDead` so a routine mid-flight part change never disturbs a live ship.

### 2. Server re-exports a changed hull to enemies promptly (scope 2)

`Server.Combat.BroadcastLoadoutChange`: on `LoadoutChange.HullChange` (the `SetBaseHullType` relaunch path, `Ship.ChangeHull`), drop `_crossSideExportSent` entries for that ship so the next `SweepCrossSideExports` re-sends the fresh hull/identity to every enemy peer that can actually see it. Safe by construction: the sweep only sends to a side with a seated peer in the ship's current sector, so a re-export never reaches an enemy who isn't seeing the ship (native's "enemies learn a ship when visible" rule).

### 3. Measurement instrumentation (the task's first deliverable, scope-measure)

- `ClientWorld.ShipPlaceability` enum (`Live` / `LatchedDead` / `NeverPosed` / `Stale`) + `ClassifyShipPlaceability(short)` + `PoseAgeSeconds(short)` — per-viewer, per-ship pose-age and category.
- `ProjectileDiagnostics.FromLatchedDead / FromNeverPosed / FromStale` counters, incremented in `Apply(ProjectileInstance)` for every incoming round whose shooter the viewer cannot place — the client-side "shots from ships I can't place" measurement, split into exactly the task's categories, exposed in the `[proj-diag]` log line.

This is the hook a rig/probe client (or the MonoGame client's `[proj-diag]` line) uses to log, per viewer and per ship in its sector, the **age of the last pose update** and whether the ship is latched-dead / never-posed / stale, and to count shots-from-unplaceable-ships before vs after a fix.

---

## Failing-first tests (all RED on the pre-fix code, GREEN on the fix)

- `FedMsgDispatchCoverageTests` (ClientCore) — registry moved `LOADOUT_CHANGE(96)` from *known-unconsumed* to *consumed*; the coverage test feeds the real bytes through the real dispatch and fails (falls into `UnknownFmids`) if the dispatch case is absent. Verified RED with the dispatch case removed.
- `P648RelaunchVisibilityTests` (ClientCore, unit, no socket):
  - `LoadoutChange_RevivesDeadShip_AdoptsNewHullAndParts`
  - `LoadoutChange_OnLiveShip_DoesNotDisturbReviveState`
  - `LoadoutChange_ReparentsBoardedChildren`
  - `ClassifyPlaceability_DeadNeverPosedStaleLive`
  - `Projectile_FromLatchedDeadShooter_IncrementsFromLatchedDead`
  - `Projectile_FromNeverPosedShooter_IncrementsFromNeverPosed`
- `CrossSideExportLiveTests.Cross_side_hull_change_is_re_exported_to_enemy_promptly` (Core, live loopback) — two cross-side ships exchange identity; A's hull changes (`ChangeHull`, the relaunch path); B must receive a NEW export with the NEW hull within a window **far below** the 600-tick self-heal cadence. Verified RED with the server fix disabled.

---

## Measurement status (honest)

The task's central instruction is **measure before fixing**. Static reading of current master shows:
- the cross-side **revive** path (latched-dead enemy becoming visible again) already works via P521's `176`/`EXPORT` clears — the 176 candidate list is cluster-membership (any side), so a relaunched ship in a viewer's sector is re-posed within the P632 density budget;
- the **per-viewer budget** (cause 2) already has the density-aware heavy quota (P632) sized so every in-sector ship is re-fed within the dead-reckoner's 1.5 s horizon;
- the **pod-viewer** (cause 3) is not treated differently by the server broadcast (a pod peer gets the same density-scaled 176 stream); a pod-specific cull would need to be demonstrated live.

Because reproducing a real 195v195 clash with a probe client and video capture could not be completed in this session, the quantitative distributions the task asks for (pose-age distribution, "shots from unplaceable ships" per category, budget quota applied per viewer, before/after) are **not fabricated** — they are made measurable by the instrumentation above, and the intended rig run is described. What is claimed as measured here: the two concrete gaps (dropped `LOADOUT_CHANGE`, stale cross-side hull) and their failing-first tests.

---

## Modern alternative considered

| Native does | Modern option | Chosen | Why |
|---|---|---|---|
| Fixed 2-heavy/2-light per-tick ship-update budget (modem-era) | guaranteed minimum update cadence per visible ship; snapshot interpolation / relevancy | **P632 density-aware quota** (already in master) — every in-sector ship re-fed ≥ once per dead-reckon horizon; at typical density it equals native's 2, at 200v200 it scales | keeps the renderer smooth without a local sim; the minimum-cadence intent is what the density quota implements |
| Client keeps one ship object, re-seated on relaunch (no wire revive) | explicit "alive again" signal on relaunch | **consume LOADOUT_CHANGE as the same-side revive signal** | faithful intent; the pure-renderer client needs the explicit clear |

---

## Files

- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(LoadoutChange)`, router case, `ShipPlaceability`/`PoseAgeSeconds`/`ClassifyShipPlaceability`, `ProjectileDiagnostics` counters.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — dispatch case for `LoadoutChange.Fmid`.
- `src/Allegiance.Server/Server.Combat.cs` — cross-side hull-refresh on `HullChange`.
- `tests/Allegiance.ClientCore.Tests/P648RelaunchVisibilityTests.cs` — new unit tests.
- `tests/Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests.cs` — registry moved.
- `tests/Allegiance.Core.Tests/CrossSideExportLiveTests.cs` — new cross-side live test.
- `docs/parity/fedmsg-wiring.csv` — `LOADOUT_CHANGE(96)` marked fixed/consumed.

## Suite

Whole-solution build: **0 warnings / 0 errors**. `scripts/run-tests.sh` runs:

| Run | tree | Lobby | MonoGame | ClientCore | Core | QUARANTINE | result |
|---|---|---|---|---|---|---|---|
| 1 (regression-discovery, pre-fix commit cb412f90) | NOT final | 12/12 | 1969 | **12 fail / 796** | 4067 | pass | surfaced the P648 regression (DeadLatch x2, Suicide x1) + load flakes; **fixed before the final runs** |
| 2 (final code, run 1/2) | final | 12/12 | 1969 | 10 fail / 799 | 1 fail / 4066 | pass | all 11 failures verified flakes (3/3 isolation, except 1 isolation-flaky sibling) — see TEST_FLAKES.md P648 observation |
| 3 (final code, run 2/2) | final | 12/12 | 1969 | **809/809** | **4067/4067** | pass | **fully clean, EXIT 0** |

Exactly two runs on the final code (runs 2 & 3); run 3 was fully clean. Run 1 was on the
intermediate commit before the lifepod-gate regression fix and is disclosed for
honesty. Every run-2 failure was in code this task did NOT touch; the flake rule was
applied (3× isolation; 3/3 pass ⇒ flake, listed in `docs/TEST_FLAKES.md` P648
observation; the one isolation-flaky sibling, `LiveCountdownStartPingTests.StartGame…`,
is recommended for the QUARANTINE lane in a follow-up).

