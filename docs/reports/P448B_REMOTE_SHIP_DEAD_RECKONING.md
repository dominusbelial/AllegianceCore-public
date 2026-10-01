# P448b — Render remote ships through the P448a dead-reckoner

Stage: P448b. Landed on `master` (top commit at landing: 8e166950 P448b, on top of
b8f5e95d P448a). Not pushed. Builds on P448a's pure `ShipDeadReckoner` (the port of
native `CshipIGC::CalculateShip` / `WarpShip`, `shipIGC.cpp:3244-3297`).

## 1. Problem

Before this stage the MonoGame client drew every REMOTE ship from its raw last-known
`ServerShipUpdate` (`PredictedPose.cs`, the pose-gather in `Game1.FlightInput.cs`).
At the default 20 upd/s that means enemies visibly step and every remote ship renders
about one packet in the past — an error of roughly `speed × gap` (measured **2.43 m**
at the mid-interval of a 20 Hz turn; see §5). P448a built the pure dead-reckoner but
deliberately did NOT wire it into drawing ("Rendering is deliberately NOT wired (that
is P448b)").

History respected: an earlier blend/lerp `RemoteShipPredictor` was removed (2026-09-14)
for flashing/teleporting/ghosting with up to 21 ships in flight. This stage does not
blend — it re-runs the real flight model forward from each authoritative update, so
the artifacts that motivated the removal do not recur (see §6).

## 2. What was built

### 2.1 `ClientWorld` — per-remote-ship dead-reckoners, re-seeded at every pose arrival
`src/Allegiance.ClientCore/State/ClientWorld.cs`

- New `Dictionary<short, Flight.ShipDeadReckoner> _remoteReckoners`, held **separately
  from the wire-truth `ShipEntity` fields** (the reckoner is client interpolation
  state, not server truth).
- `ReseedRemoteReckoner(e)` is called right after each of the three `ShipEntity.Latest`
  write sites stamp the local arrival time:
  - `Apply(SetCluster)`,
  - `Apply(SingleShipUpdate)`,
  - `Apply(HeavyShipsUpdate)` (the heavy/light 176 telemetry).
  Each seeds the ship's `ShipDeadReckoner` from that update's pose/velocity/orientation/
  turn-rates/controls, anchored at `ShipEntity.LatestArrivalTime` (new field, local
  monotonic clock, distinct from the wire's server-tick `LastUpdateTick`).
- Ships that can't be dead-reckoned fall back to their raw pose with no reckoner:
  the local own ship (`IsMine` — own prediction is P447), ships with no resolvable
  hull record (identity not yet known / no core), and dead/gone ships. `KillShip`/
  `ShipDelete` drop the reckoner so a recycled id never reuses stale extrapolation.
- `TryGetRenderPose(shipId, now, out pos, out ori)` is the renderer's query: it
  `PoseAt`s the ship's reckoner to `now` (same monotonic clock as the seed) and
  returns false when there is no seeded reckoner (caller uses the raw pose).
- `RemoteShipHullResolver` — a delegate the host client registers, resolving a remote
  ship's *already-effective* hull stats × its side's GA factors (see §3). ClientWorld
  stays pure of the catalogue/GA data.
- `NowSeconds` — an injectable clock (default `Environment.TickCount64/1000.0`) so a
  test can space update arrivals deterministically.
- Re-seed **correction measurement** (item 4): on every re-seed, the distance between
  where the PREVIOUS seed's extrapolation stood at that instant and the new
  authoritative seed is accumulated; `TakeRemoteCorrectionStats()` reports
  `(count, mean, max)` and resets (polled live by the client every ~2 s).

### 2.2 `ShipEntity` — `LatestArrivalTime`
`src/Allegiance.ClientCore/State/ShipEntity.cs` — a local wall-clock arrival stamp for
the latest `Latest`, clearly documented as local meta (NOT wire truth; the wire `Time`
is still in `LastUpdateTick`). Anchors the reckoner's replay.

### 2.3 MonoGame pose gather — render at `PoseAt(now)`, one pose per ship
`src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs`

- The remote-ship loop now computes `Flight.PredictedPose pose = RemoteShipPose(world, id, up)`
  and puts THAT pose into `_poses[id]` (feeding the hull draw, camera, radar position,
  target-selection projection — every consumer of `_poses` automatically).
- `RemoteShipPose` calls `world.TryGetRenderPose(id, TickCount64/1000, ...)` when the
  kill switch is on; it returns a **copy** of the pose because the reckoner's
  orientation scratch mutates on every `PoseAt` — each ship gets one stable
  orientation object shared by the hull and every attached effect this frame.
- Every effect attached to the hull is fed the SAME render pose so nothing detaches:
  `GatherDamageEmitter`, `UpdateEngineTrail`, `UpdateNozzleGlow`, `UpdateNavStrobes`,
  `UpdateMotionTrail` (signatures changed from `ServerShipUpdate` to explicit
  position/velocity/orientation; their wire-driven inputs — velocity, hull fraction,
  stateM, throttle — stay wire values).
- Local own ship: unchanged, still on its raw pose with the same mining-facing logic
  (P447 out of scope; own-ship emitter inputs were rewritten to the same raw values).

### 2.4 Kill switch
- `LaunchOptions.RemoteShipDeadReckoning` (default **ON**) + client-only
  `--noDeadReckon` presence flag in `Program.cs` (mirrors P385's `--debugMode`
  pattern paired by hand with the server's `liveSettings`; never sent on the wire).
  With it, `RemoteShipPose` returns the raw last-known pose — an instant A/B for
  Domingo. `Game1` logs `[client] --noDeadReckon: ...` on launch so a kill-switched
  run is obvious in the console.

## 3. Hull-stats resolution for remote ships

`Game1.ResolveRemoteHullStats(sideId, hullId)` is registered on `ClientWorld` at
construction. It resolves the effective stats from the loaded shop-catalogue hull
record (`_shopCatalog.Hulls`) × the ship's side's `GlobalAttributeSet` factors
(thrust / max-speed / turn-rate / turn-torque; identity when the side is unknown,
exactly how the server treats a sideless ship), via `ShipDeadReckoner.HullStats.FromHull`.
**Afterburner is treated as absent (0)** for remote ships: the client has no reliable
per-ship part/loadout table for arbitrary ships (P448a §5), and a null afterburner is
behaviourally identical to native's no-afterburner branch — documented, harmless over
≤1 tick of extrapolation.

## 4. Correction item (4): measured, and NO error-decay added

Requirement: measure the typical re-seed jump (max/mean correction distance); only if
visibly large, add a short visual-only error decay.

Decision: **no error-decay added** — the hard re-seed is invisible because the
correction is sub-metre over the operative gap:

- P448a: dead-reckoned error at the realistic one-tick gap (0.05 s) is **0.0038 m**.
- This stage's client-level test (§5): mid-interval (0.025 s) error is **0.003 m**;
  the largest correction over a normal 20 Hz gap is therefore a few millimetres —
  orders of magnitude below anything a player perceives as a jump.
- Only over an *abnormal* ≥1 s stall (server hiccup) does the error reach metres, and
  then native's ±1.5 s extrapolation cap (`ShipDeadReckoner.ExtrapolationCap`) holds
  the pose rather than flinging the ship, matching native dropping the update as
  `c_susOutOfDate`.

So a lerp/decay would only *add* smoothing error between stale states — the exact
flaw of the removed `RemoteShipPredictor`. The re-seed is authoritative state
replacement, never a blend. To keep the decision data-backed live, the client logs
`[P448b] dead-reckon re-seed corrections: n=… mean=…m max=…m` every 2 s; if Domingo's
live fleet ever shows a mean correction of more than a few centimetres (e.g. afterburner
ships, where the absent-afterburner simplification could grow error), the seam for a
100 ms visual-only error-offset decay is documented and trivial to add.

## 5. Verification

### 5.1 Deterministic client-level test (the must-fail-first proof)
`tests/Allegiance.ClientCore.Tests/ClientWorldRemoteDeadReckonTests.cs` (3 tests):

Feeds a scripted 20 Hz sequence of real `ServerShipUpdate`s (sampled from a real
server-side `Ship` flying a constant turning climb) into `ClientWorld` through the real
`Apply(SingleShipUpdate)` path, then queries the render pose at the mid-interval of the
last tick:

| Measure | Value |
|---|---|
| Dead-reckoned render pose error at mid-interval (0.375 s, 0.025 s past seed) | **0.0030 m** |
| True ship pose at that instant | (as executed, logged) |
| RAW (pre-change) last-packet pose error at mid-interval | **2.43 m** |

Asserts: the dead-reckoned pose is within 0.5 m of the true ship, the raw baseline is
visibly stale (> 0.3 m), and the raw error dwarfs the dead-reckoned error by > 5×.
On the unfixed port the test cannot compile/pass (there is no `TryGetRenderPose`, no
re-seeding) — the raw last-packet pose is the only option, which the assertions reject.
The other two tests cover the unresolvable-hull fallback (→ raw, then re-seeds once the
hull resolves) and the reseed correction-stat accumulation (count = N−1 re-seeds).

### 5.2 Build
Whole-solution `dotnet build AllegianceCore.slnx -c Debug` (via
`/home/domingo/.dotnet/dotnet`): **0 warnings, 0 errors**.

### 5.3 Test suite
`Allegiance.ClientCore.Tests`: **3 consecutive runs**, each 338 passed / 2 failed, the
2 failures being exactly the pre-existing documented live-wire timing flakes named in
P448a — `EntityTable_TracksARealTickingServer` and
`DockOut_WatcherLatchesGone_UndockReturnsAndClears`. (One earlier run also tripped a
third live-network timing flake, `FullSession_ThreePeers_TeammateComposition_Then_
RealCrossSideKill`'s ready-relay wait, which did not recur in the consecutive clean
runs.) None reference the dead-reckoner. Foundation intact: `ShipDeadReckonerTests`
(4/4) and `ShipKinematicsGoldenTests` (4/4) pass.

### 5.4 Live run — NOT performed in this session (honest)
A full live visual proof (spawn MCP/vizfight bots + a MonoGame client, capture per-frame
render-pose deltas showing a steady step instead of 20 Hz jumps, and eyeball no-flashing/
no-teleporting/no-ghosting with 10+ ships) requires the graphical client plus a fleet
scenario, which I could not meaningfully observe from this terminal session. I am NOT
claiming a live visual confirmation. What IS in place for Domingo to A/B live:
- the `--noDeadReckon` kill switch (§2.4) for a one-flag A/B,
- the `[P448b] dead-reckon re-seed corrections` log (§4) for the jump statistics,
- the deterministic numbers above, which already bound the operative-gap correction to
  a few millimetres — the physics basis for expecting no visual stepping.

## 6. Why this is not the removed RemoteShipPredictor (recurring-artifact check)

The removed predictor (a) blended/lerped toward stale positions, (b) stacked a `vel·dt`
extrapolation term on top of the lerp, and (c) lerped orientation — so it fought its own
next packet and flashed/teleported/ghosted. `ShipDeadReckoner.PoseAt` does none of these:
it re-runs the SAME `ShipKinematics.Step` the server uses, from the last authoritative
seed, so a turning/thrusting ship keeps turning/thrusting between packets exactly as the
server will simulate it. Error is only the not-yet-heard control change (millimetres over
one tick), and the seed replacement is authoritative — no interpolation, no stacking, no
orientation lerp. That is the structural reason the 2026-09-14 failure modes cannot recur,
and the deterministic test demonstrates the mid-interval pose is (near-)exact rather than
blended.

## 7. Out of scope (unchanged)

- Own-ship prediction (P447a/b) — the local ship stays on its raw pose.
- Server update rate / wire format — untouched.
- vizfight rendering — a separate visualizer, left on raw poses.
- P438–P440, P445, P446, P449.

## 8. Files changed

- `src/Allegiance.ClientCore/State/ClientWorld.cs` — reckoner dict, reseed sites,
  `TryGetRenderPose`, resolver seam, injectable clock, correction stats.
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — `LatestArrivalTime`.
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — render-pose gather via
  `RemoteShipPose`; all effect emitters routed through the same pose; correction log.
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — emitter methods take
  position/orientation; `ResolveRemoteHullStats`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` / `Game1.Session.cs` / `Program.cs` —
  `--noDeadReckon` kill switch plumbing.
- `tests/Allegiance.ClientCore.Tests/ClientWorldRemoteDeadReckonTests.cs` — new tests.
