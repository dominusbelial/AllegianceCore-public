# P406 — warp-transit exit clearance scaled by WarpVisualScale (leftover of the reverted P238 trigger)

## Exact change

`src/Allegiance.Server/ServerHost.cs` — `ServerHost.HitWarpEvent`, step 3 (the
exit reposition):

```csharp
// before (P238 leftover)
float exitClearance = Sector.WarpVisualScale * rw.Warp.Radius + shipRadius + 5f;

// after (P406 — native-faithful)
float exitClearance = shipRadius + 5f;
```

The `Sector.WarpVisualScale * rw.Warp.Radius` term is dropped entirely. The exit
is now `destPos + up·rand(2,5) + right·rand(2,5) − (shipRadius + 5)·backward`,
byte-for-byte the native formula (`~/Development/Allegiance/src/FedSrv/fsship.cpp:129-132`:
`m_pShip->SetPosition(pwarpDest->GetPosition() + (GetUp()·random(2,5)) +
(GetRight()·random(2,5)) − (GetRadius()+5)·backward)`).

## Corrected comment (was stale — justified a scale whose reason is gone)

The previous comment at `ServerHost.cs:834-846` explained the scaled clearance as
necessary to land the exit OUTSIDE the destination warp's "(P238-widened)
collision sphere" and to stop the observed ship-8096 Arrokoth↔Haumea ping-pong.
That rationale is obsolete:

- **P244** already reverted the real transit trigger from the 9.5×-scaled
  threshold back to the raw collision radius, `warp.Radius + shipRadius`
  (`Sector.cs:2729`, `threshold = warp.Warp.Radius + shipRadius;`).
- **P247** shrank the render funnel from 9.5× to `Sector.WarpVisualScale = 2.5f`
  (`Sector.cs:1403`) — the visual no longer implies a 10× destination sphere.
- The re-trigger ping-pong is prevented by the 2-second
  `LastWarpTime` / `WarpCooldownSeconds` debounce (`Sector.cs:2716`,
  `WarpCooldownSeconds = 2.0` at `Sector.cs:1340`), which collapses native's
  `warpNoUpdate → warpWaiting → warpReady` state machine (`fsship.cpp:74,1185-1191`).
  Native itself exits only `GetRadius()+5` — i.e. **inside** the destination
  warp's own raw collision radius — and relies on its equivalent `m_warpState`
  gate, not on exit distance, to avoid re-triggering.

The rewritten comment states all of this and cites `fsship.cpp:129-132`,
`Sector.cs:2729`, `Sector.cs:2716`, and `Sector.cs:1340`.

## Out of scope (unchanged)

- `Sector.WarpVisualScale` itself (2.5f) and its use for the visual funnel
  rendering — untouched; only its use in the exit-clearance was wrong.
- The `LastWarpTime` / `WarpCooldownSeconds` debounce — untouched.
- P368–P405 — untouched.

## Live verification (real proof)

Added `ShipExitPosition_IsAtShipRadiusPlusFive_FromDestinationWarp` to
`tests/Allegiance.Core.Tests/WarpTransitServerTests.cs` (mirrors the P246 cruise
harness). A real in-process `Server` boots the `XXV_simple.igc` debug topology, a
real `Bot` joins, its ship is placed just inside a source warp's raw collision
volume with a realistic cruise entry speed, the native trigger fires on the first
tick, the host performs the transit, and the server-side position is captured on
that exact tick (transit is the last pass of the tick —
`Sector.Update`: models → combat → warp — so there is no coasting drift).

Measured (fix applied):

```
P406-LIVE: exit dist=24.1 vs native clearance 23.7
           (lateral jitter lands it in [23.7,24.8];
            the old P238-scaled exit would have been ~274)
P406-LIVE: flew straight 60 ticks (3.0s) in sector 1057
           — no re-trigger ping-pong back to 1051
```

- **Exit distance hugs the aleph:** dist = 24.1 units, versus native clearance
  `shipRadius+5 = 23.7` (dist = √(clearance² + up² + right²), up/right ∈ [2,5],
  so the theoretical band is [23.7, 24.8]). The old scaled exit would have been
  ~274 units — about 250 units farther out, exactly the reported defect.
- **No re-trigger ping-pong:** the ship (now emerging *inside* the destination
  warp's raw ~100-unit collision sphere) flew straight for 60 ticks (3.0 s,
  past the 2 s debounce) and never re-entered the source sector — the debounce
  gate, not exit distance, is what holds it.

### Discriminating "must fail if removed" proof

Temporarily restored the old line
`float exitClearance = Sector.WarpVisualScale * rw.Warp.Radius + shipRadius + 5f;`
and re-ran the test in isolation — it went **red**:

```
P406-LIVE: exit dist=273.8 vs native clearance 23.7 ...
Assert.InRange() Failure: Value not in range   ← FAIL
```

Then restored the fix (green again). This proves the test genuinely discriminates
the removed scale factor.

### Test suite

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors.**
- `Allegiance.Core.Tests` run 3× (3484 total, incl. the new test). Every run:
  only the documented rotating live-wire timing flakes and the known persistent
  `LiveTurretGunnerBindingTests` machine-state flake. Each flagged test was
  verified to pass in isolation and/or on the pristine P405 tree (stashed) —
  none reference the new symbol, none touch warp-exit code.
- `Allegiance.ClientCore.MonoGame.Tests`: 771/771 pass.
- `Allegiance.Lobby.Tests`: 12/12 pass.
- `Allegiance.ClientCore.Tests`: 2 live tests
  (`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`) fail **consistently on
  the pristine P405 tree in isolation** too — pre-existing machine-state failures,
  unrelated to warp-exit clearance (proven not a regression).

Suite count for `Allegiance.Core.Tests` moved 3483 → 3484 with the +1 P406 test.
