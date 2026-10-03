# P518 — native own-ship model breaks after the first dock: client SHIP_UPDATEs stop being processed → can't dock/undock, ship "flies through" stations

**Stage:** P518 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Symptom (Domingo's live playtest, 2026-10-03)

After P507+P508, on server 221 the first flight worked (`[ctrlak]` `accepted` climbed
9 → 76 → 142 → 209 → 275 → 342 → 404) but from the **first dock onward it froze** at
`accepted=404 rejected=1 out-of-date=0 invalid=0` through a Lt Interceptor purchase +
undock, a Scout purchase + undock, and 10+ minutes of flight. The server processed
**none** of the client's updates after the first dock — so its copy of the ship stayed
parked and could never dock again, while the client's local sim flew freely through the
station meshes ("flies through stations").

## Root cause (verified in source, reproduced live)

The P507 per-player cookie is delivered to the client in every **SET_CLUSTER(7)** and
renewed on each **SHIP_RESET(183)**; the client echoes it in every native
`FM_C_SHIP_UPDATE(24)`, and the server drops (without counting) any update whose cookie
mismatches (`FedSrv.CPP:4854`). Native's **single** SET_CLUSTER-sending site is
`CFSPlayer::SetCluster`, which **always** stamps `pfmSetCluster->cookie = NewCookie()`
(`fsship.cpp:767`) — the join, warp *and* undock/launch paths all go through it.

The port had three SET_CLUSTER sites:

| site | file | cookie |
|---|---|---|
| join burst | `Server.Lobby.CompleteJoinAndLaunch` | `NextCookie(EnsurePeerUpdate(from))` ✓ |
| warp | `Server.Combat.OnShipWarped` | `NextCookie(EnsurePeerUpdate(from))` ✓ |
| **undock** | `Server.Docking.PerformUndock` | **`Cookie = 0`** ✗ |

`PerformUndock` sent `Cookie = 0` (an old "same simplification" comment) while the
server's `PeerShipUpdate.Cookie` stayed at the join value (1). So on the first undock the
undock SET_CLUSTER told the client its cookie was **0**, the client's
`ClientWorld.Apply(SetCluster)` set `ShipEntity.Cookie = 0` (ClientWorld.cs:1207) and
the P508 send path echoed it (`_ownShipCookie = mine.Cookie` every frame), while the
server still expected **1**. Every subsequent SHIP_UPDATE was dropped at the dispatch
cookie gate — `accepted` frozen, ship parked. This is candidate #2 in the brief;
candidates #1 (client never resumes — the client does resume once the periodic
SHIP_STATUS clears `StationId`) and #3 (server "not in cluster" gate — the ship is in a
cluster when undocked) were investigated and are **not** the cause.

**Fix:** `Server.Docking.PerformUndock` now sends
`Cookie = NextCookie(EnsurePeerUpdate(from))` — matching join and warp, and native's
`SetCluster`-always-`NewCookie()` invariant.

## Scope items

### 1. Instrument before fixing (silent drops are now counted)
- Added `PeerShipUpdate.Dropped{NoShip, PreGame, Boarded, NotInCluster, Cookie}` counters
  (Server.cs) wired into every silent-drop branch of the SHIP_UPDATE dispatch
  (Server.Dispatch.cs) — including a new native-faithful "not in a cluster"
  (`GetCluster()`, `FedSrv.CPP:4857`) gate/counter — and surfaced in the ~10 s
  `[ctrlak]` summary as `dropped[cookie=… noship=… pregame=… boarded=… notincluster=…]`
  (Server.Broadcast.cs). A drop with every counter still 0 (the old "nothing counted"
  blindness) can no longer occur.
- Client: `MaybeSendOwnShipUpdate` (Game1.Prediction.cs) now logs `[sim] STOPPED/RESUMED
  sending native SHIP_UPDATE: <reason>` on every send-eligibility transition (armed /
  hull / frozen / server-clock), so a silent "client stopped sending" is never invisible
  again.

### 2 & 4. Reproduce live (failing first) + live proof — `NativeOwnShipDockLiveTests`
A real `ConnectionSession` (the production client session) drives the real
`OwnShipSimulator` + `OwnShipServerClock` + `SendShipPose` over a real TCP wire to a
real in-process P507/P508 server, through the full brief sequence: **fly → DOCK #1 →
buy a DIFFERENT hull (while docked) → UNDOCK → fly back → DOCK #2 → UNDOCK → WARP →
keep flying**. The server accepts each ≤5 m pose within its 20 m window, so its copy of
the ship genuinely flies to the garrison / aleph; the dock latches inside
`Sector.DockRadius` and the warp transits inside `warp.Warp.Radius + shipRadius`.

**Failing-first** (measured with the fix temporarily reverted to `Cookie = 0`): after
undock #1 the cookie diverged and the server accepted **no** poses — the server ship
never flew back into the garrison, and the test failed with
`ship never docked #2 (after 3000 ticks) — accepted froze / silent cookie drops?`
(2 m 27 s). Green after the fix.

**Green measurement** (real wire, real server):

    [P518-LIVE] DOCK #1 reached in 11 ticks; accepted-so-far=10 rejected=0
    [P518-LIVE] hull swap while docked: 1 ('Adv Miner') -> 245 ('Observer')
    [P518-LIVE] DOCK #2 reached; accepted 10 -> 61; dropped[cookie]=0
    [P518-LIVE] warped 1053 -> 1052 in 1034 ticks; re-seeded
    [P518-LIVE] FINAL: accepted=1123 (dock1 10 -> dock2 61) rejected=0
                dropped[cookie=1 noship=0 pregame=0 boarded=0 notincluster=0]

- **`accepted` keeps climbing after every dock/undock**: 10 (dock #1) → 61 (dock #2) →
  1123 (final, after hull swap + warp). The ship docked on the server **both** times.
- **~0 rejections**: `rejected=0` across the whole sequence.
- **No silent cookie drops after the first dock**: `dropped[cookie]=0` measured at dock
  #2 (the exact P518 window). The single final `dropped[cookie]=1` is the **faithful
  warp-boundary transient**: the client's last pre-warp pose (sent with the old cookie
  in the same tick-window as the transit) is dropped against the warp-rotated cookie —
  native behaves identically, because the client stops sending on the warp freeze edge.
  It is not the P518 divergence (which is persistent and post-dock).
- Sequence included the hull swap (`Adv Miner → Observer`) and a real warp transit
  (`1053 → 1052`).

## Out of scope (confirmed not touched)
- The P507 acceptance rules (no change; step 2 did not prove them wrong — the cookie was
  the only divergence).
- The turret clock (P517).

## Verification

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`
  → **0 warnings, 0 errors**.
- New discriminating live test **fails first** (fix reverted → red) and **passes** with
  the fix.
- Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs. Grand total per run:
  **5347** = Lobby 12 + MonoGame 1208 + ClientCore 419 + Core 3708.

  | run | Lobby | MonoGame | ClientCore | Core | failures |
  |---|---|---|---|---|---|
  | 1 | 12/12 | 1208/1208 | 418/419 | 3705/3708 | CC `FullSessionKillIntegration…CrossSideKill`; Core `NpcShipFoundationLiveTests…Ownerless_npc…`, `LiveRelaunchTests…Carrier_without_launch_slots…`, `LiveChatSectorWireTests…` |
  | 2 | 12/12 | 1208/1208 | 418/419 | 3707/3708 | CC `FullSessionKillIntegration…CrossSideKill`; Core `LiveGainFlagTests…docking_at_own_side_station…` |
  | 3 | 12/12 | 1208/1208 | 417/419 | 3707/3708 | CC `FullSessionKillIntegration…CrossSideKill`, `CrossSideCombatVisibilityLiveTests…warp_return`; Core `LiveTreasurePickupTests…cash_treasure…` |

  Every failure is a **pre-existing rotating live-wire load-flake** in an assembly/file
  that does **not** reference any P518 symbol. All are cited in `docs/TEST_FLAKES.md`
  (`FullSessionKillIntegration…CrossSideKill` :33/:148/:204, `LiveRelaunchTests` :40,
  `LiveTreasurePickupTests` :157, `LiveChatSectorWireTests` :216,
  `LiveGainFlagTests…flag-secure` :246); `NpcShipFoundationLiveTests` and
  `CrossSideCombatVisibilityLiveTests` are the same rotating class P508 already recorded
  (P508 run-2 log lists `NpcShipFoundationLiveTests…`; the ClientCore
  `CrossSideCombatVisibility…` rotater is in P508's table). **Each confirmed green in
  isolation**: `FullSessionKillIntegration` + `CrossSideCombatVisibility` (4/4, ClientCore)
  and `NpcShipFoundation` + `LiveGainFlagTests` + `LiveRelaunchTests` +
  `LiveTreasurePickupTests` + `LiveChatSectorWireTests` (17/17, Core) — 21/21 in isolation.
  `LiveGainFlagTests` is dock-adjacent and passing in isolation confirms the P518 undock
  change is not implicated.
  **No P518-area failure**: the new `NativeOwnShipDockLiveTests` passed in all 3 full-suite
  runs (and in isolation), as did every existing own-ship/P507/P508 test.

## Files changed
- `src/Allegiance.Server/Server.Docking.cs` — **the fix**: undock SET_CLUSTER now
  `NextCookie(EnsurePeerUpdate(from))`.
- `src/Allegiance.Server/Server.cs` — `PeerShipUpdate` dropped-by-reason counters.
- `src/Allegiance.Server/Server.Dispatch.cs` — wire the counters + new "not in a
  cluster" gate/counter into the SHIP_UPDATE dispatch.
- `src/Allegiance.Server/Server.Broadcast.cs` — `[ctrlak]` summary includes `dropped[…]`.
- `src/Allegiance.ClientCore.Game/Game1.Prediction.cs` / `Game1.cs` — log
  stop/resume-sending edges with reason.
- `tests/Allegiance.ClientCore.Tests/NativeOwnShipDockLiveTests.cs` — **new** live
  failing-first test (+1).
- `docs/reports/P518_OWN_SHIP_AFTER_DOCK.md` — this report.

## Commit
`master` only, **not pushed** (branch verified with `git branch --show-current` →
`master`). `git log -1` confirms the `P518:`-prefixed commit is HEAD.
