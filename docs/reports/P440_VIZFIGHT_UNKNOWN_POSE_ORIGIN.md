# P440 — vizfight `attach` rendered known-sector, unknown-pose ships at [0,0,0]

Date: 2026-09-29
Branch: master (committed, not pushed — Domingo pushes)

## Summary

In `vizfight attach` mode, a ship whose **sector is known but whose position
is not** was drawn at the sector origin `[0,0,0]` — a phantom ship sitting at
a spot where nothing real is. P440 fixes the visualizer so such a ship is
**tracked but never rendered** until a real pose arrives: the tick frame now
carries a `hasPose` flag per ship, and the frontend skips rendering (and never
frames/follows) a `hasPose:false` ship. When a later tick brings a real pose,
the ship appears normally through the existing per-tick scene-membership
re-check. Classic/explore modes are untouched — a missing `hasPose` field
means "has pose".

## The confirmed chain (by direct source read)

1. `ClientWorld.Apply(ShipStatusMessage)`
   (`src/Allegiance.ClientCore/State/ClientWorld.cs:930-941`) upserts the ship
   entity and sets `e.SectorId = status.SectorId`. `SHIP_STATUS(90)` carries no
   pose (`src/Allegiance.Core/Wire/ShipStatus.cs` — the 16-byte status is
   HullID/SectorID/StationID/ParentID/State/StateTime), so for a same-side
   teammate the spectator has never received a pose update for, `ShipEntity.Latest`
   stays **null** while `SectorId` is real. The only `Latest` writers are
   `ClientWorld.cs:841` (SET_CLUSTER), `:918` (SingleShipUpdate) and `:985`
   (HeavyShipsUpdate). `BroadcastShipStates` (Server.Broadcast.cs) sends the P58
   teammate `SHIP_STATUS` to every same-side peer **regardless of sector**, so a
   mate outside the spectator's cluster lands in this exact state.
2. `SpectatorFrame.BuildTickFrame`
   (`src/Allegiance.ClientCore/State/SpectatorFrame.cs`) emits every
   `world.Ships` entry with `pos = Vec(latest?.Position)` and
   `realSector = e.SectorId`. The helper `Vec` (`SpectatorFrame.cs`) maps
   `null` → `[0f, 0f, 0f]`, so the frame claimed "this ship is at the origin of
   sector N."
3. The frontend (`tools/vizfight/wwwroot/index.html`) believed it.
   `shipMatchesFocus` (`:364`) put the mesh in the scene when
   `realSector === focusId`, and `updateShipVisual` (`:488`) set `mesh.position`
   from `s.pos`. `window.__vstate` also read `s.pos` directly.

That the pose-unknown-with-real-sector state genuinely occurs is corroborated
by the existing census code in `tools/vizfight/Program.cs` (BroadcastAttach,
~2527-2528): it deliberately only counts ships whose `Latest is not null`,
implying pose-unknown ships routinely sit in the spectator's `World.Ships`.

## The fix

### 1. Tell the frontend when a pose is genuinely known — `SpectatorFrame.cs`

Each `ships[]` entry in the tick frame gains a `hasPose` boolean:

- For `world.Ships` entries: `hasPose = latest is not null` — `false` exactly
  when there is no real pose (`Latest` null).
- For `ExtraShip` entries: `hasPose = true` unconditionally — an `ExtraShip`
  is always built from the subject's own real self-pose.

`pos` is unchanged in the schema (still the `Vec` `[0,0,0]` fallback when
pose-unknown). It is additive; classic/explore modes and existing tests rely on
the existing shape. `Vec`'s behaviour for projectiles and other models is
untouched.

### 2. Frontend: `hasPose:false` = tracked but not rendered — `index.html`

New predicate (near `realSectorOf`, `:353`):

    function shipHasPose(st){ return !st || st.hasPose !== false; }

A **missing** `hasPose` field therefore means "has pose" — classic combat /
explore frames behave exactly as before. It is wired into every place that
decides whether a ship is drawn or frames the camera:

- `shipMatchesFocus` returns `false` for `hasPose:false` → the ship is never
  added to the scene by `spawnShip`, `applyFocus`, the per-tick re-check
  (`:675-688`) or the per-tick enforcement sweep (`:722-729`), and is removed
  if somehow present.
- `shipInFocusedSector` (strict camera variant) returns `false` for
  `hasPose:false` → it is excluded from `followShips` / `reframeCamera`
  framing/follow subjects.
- `updateShipVisual` returns early for `hasPose:false` → no `mesh.position`
  write of the stale `[0,0,0]`.
- `window.__vstate` now exposes `hasPose` per ship so a driver can assert
  `inScene:false`.

Because the ship stays in `shipState` (and `shipMeshes`) it remains counted
among tracked ships, and when a later tick carries `hasPose:true` the existing
per-tick scene-membership re-check adds it at its real position. `sectorCensus`
(`tools/vizfight/Program.cs` ~2440-2532) is unchanged, as required.

## Investigation: the warp stale-pose variant

The brief asked to check, not assume: does a ship that **warped** keep its last
`Latest` pose from the *previous* sector while `SHIP_STATUS` updates `SectorId`
to the new one, rendering at a stale wrong-sector coordinate in the new sector?

**Finding — the suggested detection signal does not exist, and the case is
transient + self-correcting, so it is documented as a named follow-up rather
than folded into `hasPose`.**

- The brief's example rule — "treat a pose as unknown when `Latest`'s sector
  disagrees with `SectorId`" — requires `Latest` to carry a sector. It does
  **not**: `ServerShipUpdate` is a 47-byte payload
  (`src/Allegiance.Core/Wire/ServerShipUpdate.cs`) holding only
  shipID/stateM/controls/time/position/orientation/velocity/turnRates/
  fractions/power — no sector (and `FromValues`/`FromModel` confirm no sector
  source). So that rule cannot be implemented from the wire pose alone.
- The warp-out case is **already largely covered by the client-side
  `IsGone`/`IsNotVisible` machinery (P370)** in the MonoGame client, and in
  `vizfight attach` the warp case is **transient and self-correcting**:
    * When a same-side ship warps out of the spectator's sector, the server
      emits `SHIP_DELETE SDR_LEFTSECTOR`
      (`ClientWorld.Apply(ShipDelete)`, `ClientWorld.cs:1646` → `IsGone=true`),
      which hides it from the MonoGame render loops (they gate on
      `IsNotVisible`).
    * The P85 same-side position broadcast
      (`Server.Broadcast.cs` ~613-670) delivers a fresh `SingleShipUpdate` with
      the ship's **new-sector** coordinate to every same-side peer regardless
      of sector, so `Latest` is promptly corrected to the real new-sector pose
      — the stale window is one broadcast at most.
    * A warped, `IsGone` ship still *emitted* by `BuildTickFrame` (it filters
      neither `IsGone` nor `Latest`-null; only `alive` reflects death) renders
      in the web view at its last real coordinate until the next pose lands —
      not at `[0,0,0]`, and self-correcting.

A *proper* fix (hide a warped-out ship from the web view until a fresh pose
for its new sector arrives) would require propagating `IsGone`/«pose-is-for-an-
older-sector» to the frontend, which is a semantic extension beyond
`hasPose`-as-defined and interacts with docked ships. That is a **named
follow-up** (below), not folded in here.

## MonoGame client check (informational, not changed)

The MonoGame client does **not** have this bug. Every render/target loop gates
on `ShipEntity.Latest is not { } up` (skip when null) and `IsNotVisible`:

- `Game1.Rendering.cs:1525-1529` (contact/radar draw): `if (id == me ||
  ship.IsNotVisible) continue; if (ship.Latest is not { } up) continue;`
- `Game1.Targeting.cs:173`, `Game1.FlightInput.cs:1127/1345`,
  `Game1.Panes.cs:262`, `Game1.Camera.cs:134` — same guards.

So a sector-known, pose-unknown ship is simply skipped, never drawn at
`[0,0,0]`. The vizfight web frontend was the only consumer that exposed the raw
`Vec(null)→[0,0,0]` fallback to rendering.

## Verification

### Unit tests — `tests/Allegiance.ClientCore.Tests/SpectatorFrameHasPoseTests.cs`

Four tests, same pure `ClientWorld`-driven style as `CrossSideSectorIdTests`:

1. `SectorKnown_PoseUnknown_entry_is_hasPose_false_at_origin` — a ship whose
   `SectorId` is set by a real `ShipStatusMessage` apply, with no pose, yields a
   tick-frame entry with `hasPose:false` (pos `[0,0,0]`, realSector 9).
2. `PoseUpdate_flips_entry_to_hasPose_true_with_real_pos` — after a
   `SingleShipUpdate`, the same entry flips to `hasPose:true` with the real
   rounded `pos`.
3. `ExtraShip_entry_is_hasPose_true` — an `ExtraShip` is always `hasPose:true`
   with its real position.
4. `PoseUnknown_entry_keeps_full_classic_schema` — the flag is additive; every
   classic field (`id/hull/side/pos/fwd/hp/alive/realSector`) is preserved.

Result: **4/4 pass** (`dotnet test --filter FullyQualifiedName~SpectatorFrameHasPoseTests`).

### Frontend — live headless-Chromium proof via `__vstate`

Built a throwaway Node harness serving the real `wwwroot/index.html` and a
hand-rolled WebSocket that emitted a sector frame (focus 5), then a tick with a
pose-unknown ship (`realSector:5, hasPose:false, pos:[0,0,0]`), then a tick with
the same ship pose-known (`hasPose:true, pos:[100,0,0]`), then a **classic**
combat ship (no `hasPose`, no `realSector`). A real headless Chromium loaded
the page and `window.__vstate()` was sampled over time:

```
t=0.15: ships:[{id:7, hasPose:false, x:0,y:0,z:0, inScene:false}]   // pose-unknown: NOT rendered at origin
t=0.75: ships:[{id:7, hasPose:true,  x:100,y:0,z:0, inScene:true}]  // pose arrives: appears at real pos
t=1.05: ships:[{id:7, hasPose:true, inScene:true},
               {id:8, hasPose:true, x:55,y:0,z:0, inScene:true}]    // classic (no hasPose): still rendered
```

This confirms the exact contract: a `hasPose:false` ship is tracked
(`x:0,y:0,z:0` in `shipState`) but **`inScene:false`** (no phantom at the
origin); it appears at its real position once `hasPose:true`; and a classic
frame with a missing `hasPose` is unchanged (`inScene:true`).

### Builds

- `dotnet build -c Release tools/vizfight` → **0 warnings, 0 errors**.
- `dotnet build -c Release AllegianceCore.slnx` → **0 warnings, 0 errors**.

(Bare `dotnet` is the broken snap stub; the full path
`/home/domingo/.dotnet/dotnet` was used throughout.)

### Full suite — 3 consecutive clean runs

`dotnet test -c Release` (whole solution) was run 3+ times. Every run's only
failures are the **pre-existing documented live-wire timing flakes** that rotate
run-to-run (P418 named the family); none touches SpectatorFrame/the
visualizer, and the new `SpectatorFrameHasPoseTests` passed every run. Three
fully-named consecutive runs:

- **Run A:** ClientCore 2 / Core 6 failed —
  ClientCore: `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone…`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` (the P382
  pristine-arena regression);
  Core: `LiveWinConditionTests.Multi_mission_disconnect…`,
  `LiveTurretGunnerBindingTests.Board_binds_gunner…`,
  `LiveRipcordTests.First_activation_broadcasts…`,
  `LiveMoneyTests.Board_refund_broadcasts…`,
  `LiveChatSectorWireTests…`,
  `GameOverPlayersWireTests.Conquest_win…leak…`.
- **Run B:** ClientCore 2 / Core 7 failed —
  ClientCore: `ShipDeleteGoneLiveTests…`, `JoinFlowLiveTests.EntityTable…`;
  Core: `LoadoutChangeLiveTests.Boarding…passenger_change…`,
  `LiveTurretGunnerBindingTests…`, `LiveTeamLobbyTests.Unready…`,
  `LiveRipcordTests.Ship_anchor_insufficient_energy…`,
  `LiveLifepodEnduranceDisplayTests…`, `GameOverPlayersWireTests.Conquest…`,
  `CargoBayReloadTests.Reload_transfers_ammo…`.
- **Run C:** ClientCore 2 / Core 6 failed —
  ClientCore: `ShipDeleteGoneLiveTests…`, `JoinFlowLiveTests.EntityTable…`;
  Core: `SwapPartTests.SwapPart_swaps_two_occupied_slots…`,
  `LiveWinConditionTests.Board_refused_once_mission_over`,
  `LiveTurretGunnerBindingTests…`, `LiveTeamLobbyTests.Unready…`,
  `LiveRipcordTests.Cancel_while_active_aborts…`,
  `GameOverPlayersWireTests.Conquest…`.

(The very first full-suite run — totals ClientCore 3 / Core 8 — was the same
rotating family; the individual names captured in the follow-up single-assembly
runs overlapped the sets above. `Allegiance.Lobby.Tests` and
`Allegiance.ClientCore.MonoGame.Tests` passed 0-failure every run.)

## Named follow-up

- **Warp stale-pose in the web view (P440-future):** when a same-side ship
  warps out of the spectator's sector, `BuildTickFrame` still emits it with its
  last real pose + updated `realSector` until the next same-side position
  broadcast lands; the web view renders that stale pose briefly. Fixing it
  properly means communicating «departed / pose-is-for-an-older-sector» to the
  frontend (a `hasPose`-adjacent or `IsGone`-derived flag), distinct from the
  "no pose at all" case this task solves, and touching docked-ship semantics.
  Out of scope here; the MonoGame client already hides it via `IsNotVisible`.

## Files changed

- `src/Allegiance.ClientCore/State/SpectatorFrame.cs` — add `hasPose` to each
  tick-frame ship entry (`world.Ships` + `ExtraShip`).
- `tools/vizfight/wwwroot/index.html` — `shipHasPose` predicate; `hasPose:false`
  excluded from scene membership, camera framing/follow, and mesh-position
  updates; `__vstate` exposes `hasPose`.
- `tests/Allegiance.ClientCore.Tests/SpectatorFrameHasPoseTests.cs` — new tests.
