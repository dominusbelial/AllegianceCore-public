# P781 — ripcord lead-up as native: frozen-flight own/remote prediction, per-ship ripcord state, 3-s spark ramp at native density

**Branch:** `fix/p781` on master `bbf40def`. **Client-only** (no `Allegiance.Core`, `Allegiance.Server` or wire change).

**Provenance.** The task was started by a Pi delegate (GLM-5.3 Flash, WIP `6cd0b0e7` on base `c185229e`).
It was **stopped mid-way** while it looped on a broken screenshot harness: its shots showed the cockpit
with no ripcording ship in view, and its harness ticked the server about 10x faster than wall time (see
"Proof harness" below). Claude cherry-picked the WIP onto `bbf40def` (clean, no conflicts with
P777/P777b/P780), reviewed it against the brief and native, fixed what was wrong, finished what was
missing, rebuilt the harness, and took every measurement in this report.

## Native rule (paths under `~/Development/Allegiance/src/`)

1. **Frozen flight.** `CshipIGC::ExecuteShipMove`: the turn-rate ramp runs only `if (!m_pmodelRipcord)`
   (`Igc/shipIGC.cpp:2885-2910`). The orientation still integrates `m_turnRates` (`:2912-2917`). Engine
   thrust is skipped (`:2961-2962`), so the speed decays drag-only; an active afterburner is folded into drag
   (`:2939-2957`) and still pushes. The port's `ShipKinematics.Step(Ripcording: true)` already does this
   (SIM-0124).
2. **Every client predicts it.** `FM_S_RIPCORD_ACTIVATE` (`clintlib/appmsg.cpp:899-963`). For the own ship,
   `SetRipcordModel` + `ResetRipcordTimeLeft` run **only if the model changed** (`:940-944`). After that,
   `SetTimeUntilRipcord(GetRipcordTimeLeft())` runs every time (`:947`). For any other ship:
   `SetRipcordModel(pship)`, `ResetRipcordTimeLeft`, `SetTimeUntilRipcord` (`:950-962`).
   `FM_S_RIPCORD_ABORTED` clears the model and sets the timer to −1 (`:973-988`).
3. **Countdown.** The countdown is pinned at 0, and the model stays until the server moves the ship.
   `SetCluster` then drops it (`Igc/shipIGC.cpp:286-294`, `Igc/shipIGC.h:200-210`). The connected client's
   `UseRipcord` returns false (`WinTrek/trekigc.cpp:3961-4019`). The duration is
   `ripcordSpeed / c_gaRipcordTime` (`shipIGC.cpp:4665`).
4. **The client keeps sending SHIP_UPDATE.** `BaseClient::SendUpdate` (`clintlib/clintlib.cpp:1815-1866`)
   checks only `flyingF()`, the connection and the cadence. It has no ripcord gate.
5. **Sparks.** `ThingGeoImpl::AddTeleportParticles` (`effect/thinggeo.cpp:619-696`) runs every frame from
   `AddFireAndSmoke`, only when `m_bVisible && s_iShowSmoke > 0 && m_bVisibleShip` (`:503-528`).
   - `t` = the ripcord timer, or the aleph timer if that is sooner or the only one (`:621-623`).
   - Axes come from the frame velocity (`:632-647`).
   - `ramp = clamp(t/3,0,1)`; `count = (int)(floorf(3·s_iShowSmoke·(1−ramp)) + 0.5f)` (`:651-659`).
   - Planar pull toward a ring 1.5·R in front (`:666-685`); 0.5 s life (`:689`).
   - **Both timers keep decrementing by the frame dt with no clamp** (`:694-695`). So the ripcord spray
     **stops right after 0**, even though the frozen model is still set.
   - `s_iShowSmoke` ships as **3**: `SetSmoke(LoadPreference("SmokeEffects", 3))` (`WinTrek/WinTrek.cpp:3293`,
     `SetSmoke :5025-5032`). The static initialiser 1 (`thinggeo.cpp:1679`) is overridden at start-up.
6. **Aleph lead-up.** Every frame, for every ship in the view cluster (`WinTrek/WinTrek.cpp:7783-7802`):
   - The nearest warp is found with `FindTarget(c_ttWarp|c_ttNearest|c_ttAllSides)`. That is the minimum
     squared centre distance over the cluster's pickable models the ship can see (`Igc/common.cpp:511-575`).
   - `SetTimeUntilAleph(range/closing)` is used if the ship is closing, else −1.
7. **Own ship in the cockpit.** `pshipSource->SetVisibleF(!InternalCamera(m_cm) …)` (`WinTrek/WinTrek.cpp:7296-7300`).
   With the own ship invisible in the cockpit, `m_bVisible` is false, so it emits no sparks. External views
   do emit.

## Step 0 findings

- **Own ship before P781: frozen still.** In the base proof run, `[sim] freeze: ripcord` appears at activation.
  The sim is re-seeded from the server pose and frozen. Over t = 2.5 / 1.0 / 0.2 the log shows `ownYawDeg=0.0`,
  `speed=69.8` (constant), turn rates `(0,0,0)` and `sparks_live=0`. The three base frames are pixel-identical
  (`P781_assets/p781-B-own-ripcord-BASE.jpg`). SHIP_UPDATE stopped (`STOPPED sending … sim frozen
  (docked/dead/ripcord/warp)`).
- **The owner gets NO SET_CLUSTER on a ripcord teleport (any branch).** `PerformRipcordTeleport`
  (`Server.Docking.cs:525-645`) only calls `MoveShipToSector` + `UpdatePeerSectorSnapshot` on the station,
  ship and probe branches. The client learns the new sector from SHIP_STATUS. Native sends
  `FM_S_SET_CLUSTER` + `NewCookie()` on every cluster change, from `CFSPlayer::SetCluster`
  (`FedSrv/fsship.cpp:742-767`). So the `ClientWorld` comment "the server's teleport is silent on the wire"
  is still true. Consequences:
  - (a) The client must clear its own ripcord state on the own sector-change edge. P781 does this in `Game1`.
  - (b) An own SHIP_UPDATE that is in flight across the teleport tick is validated against the moved ship
    and draws **one post-transit SHIP_RESET**. Proof log: `[sim] SHIP_RESET #1` about 0.1 s after the
    landing, after the re-seed. It is a harmless snap to the pose the client just re-seeded to. Native avoids
    it with the cookie rotation. This is a server gap and goes to **P781b**.
- **The server accepts ripcording SHIP_UPDATEs.** `Ship.CalculateShipPose` builds its inputs with
  `Ripcording: _ripcordModel is not null` (`Ship.cs:5170-5196`). Live: the `[ctrlak]` counters went from
  `accepted=102` to `accepted=168` across the own countdown with `rejected=0 out-of-date=0`.
  **SHIP_RESETs during the countdown: 0** (asserted by the harness). So the Decision's primary path is used:
  the own ship keeps sending.
- **Remote pose error, before vs after.** These are the `[P448b] dead-reckon re-seed corrections` windows that
  cover scene A's 7 s countdown, from the same scene and seed.
  - Base: mean 0.092 / 0.042 / 0.085 / 0.140 m, max 0.419 m.
  - Branch: mean 0.070 / 0.037 / 0.087 / 0.147 m, max 0.403 m.
  - The two are **not distinguishable**. The proof enemy holds a constant stick whose steady-state rates equal
    its frozen rates, and with 20 Hz updates the error is bounded by one update interval anyway (as the brief
    predicted). The frozen-flight replay is pinned by `P781RemoteRipcordReckonTests`, not by this number.

## Port before → after

| | before (`bbf40def`) | after (`fix/p781`) |
|---|---|---|
| own pose during countdown | frozen at the server pose, no SHIP_UPDATE | `OwnShipSimulator.SetRipcording(true)`: pinned rates, drag-only, keeps sending (0 resets) |
| own state at t = 0 | `RipcordStatusLabel` cleared it at 0 | label gone at 0; state kept to the server move (own sector-change edge) or the 2 s safety expiry |
| own state after the teleport | n/a (already cleared at 0) | cleared on the own sector-change edge (native `SetCluster` → `SetRipcordModel(NULL)`) |
| re-target to the same anchor | restarted the countdown | keeps it (model unchanged, `appmsg.cpp:940-944`); a different anchor restarts it |
| remote prediction | last controls (`Ripcording: false`) | `ShipDeadReckoner.Ripcording` from `_remoteRipcords` via `TryGetRenderPose(..., ripcording)` |
| lead-up sparks | none | every drawn ripcording / aleph-closing ship (own only outside the cockpit), native ramp |
| spark timer past 0 | n/a | negative (native decrement), so the spray stops at 0 and the 9-spark transit burst stays |
| `ShowSmoke` | 1 (static initialiser) | 3 (shipped default, `WinTrek.cpp:3293`), so the transit burst is 9 sparks |
| spark pool | 512 slots (evicts at 50 bursts/frame) | 13 500 slots (peak measured 2 677) |
| spark gather / draw | `AddRange(Enumerate())` + `new List` + 2 new arrays per draw | `CopyTo`, reused list, reused batcher buffers (0 B) |

## Design

- **`RipcordLeadup`** (new, pure, `src/Allegiance.ClientCore/Flight/RipcordLeadup.cs`) holds the helpers
  below. It uses the HUD's TiDi rule (wall elapsed × time scale, the same expression
  `RipcordCountdownSound.TimeLeft` integrates).
  - `TimeLeft`, `OverrunSeconds` and `IsRipcording` (with the 2 s grace).
  - `TimeUntilTeleport`: the signed spark timer, −1 with no start or no duration.
  - `AlephEta`, `SelectTeleportTime` and `SparkCount`.
- **Own ship (`Game1.Prediction.cs`).** `world.RipcordActive` is no longer a freeze reason. On each frame:
  - On the own sector-change edge, it calls `world.ClearRipcord()`.
  - Then it calls `_ownSim.SetRipcording(world.RipcordActive)`.
  - There is no re-seed at activation. `SetHull` preserves the flag.
  - `RipcordStatusLabel` returns null at `left <= 0` and clears the state only at `left <= −2 s`.
- **Remote ships.** Each frame, `Game1.RemoteShipPose` computes `ripcording` from
  `TryGetRemoteRipcord + RipcordLeadup.IsRipcording`, then calls
  `ClientWorld.TryGetRenderPose(..., ripcording)`, which sets `ShipDeadReckoner.Ripcording` (applied to the
  reckoner's inputs on change). The duration is
  `RipcordSpeed / c_gaRipcordTime`. The hull's `RipcordSpeed` is cached per hull id; the GA is read live.
  `SingleShipUpdate.BIsRipcording` is **not** read (P781b).
- **Sparks.**
  - The gate-locked pose pass fills a preallocated `TeleportEmitter[64]` (`GatherTeleportEmitter`). It covers
    the own ship (outside the cockpit) and every remote ship the model pass draws.
  - `t = SelectTeleportTime(ripT, alephEta)`, where `ripT` is the signed `TimeUntilTeleport` and `alephEta`
    uses the nearest cached aleph centre of the view sector.
  - Frame velocity is the rendered-position delta over the frame dt, with the wire velocity on the first frame.
  - The render pass runs `EmitTeleportLeadup`: one native call per elapsed 1/60 s, at most 4 per frame.
  - Then `CopyTo` fills the reused list, the benchmark reducer runs (it now runs after the gather; before, it
    trimmed the list before it was refilled), and the renderer draws.
- **Batcher.** `ParticleBillboardBatcher.Draw` now fills grow-only member buffers with `FillBillboardArrays`
  and splits batches over 8191 quads (16-bit indices). Before, it allocated two arrays per call, about 110 B
  per particle per frame. This was the dominant allocation once 2 700 sparks were live, and it also helps
  every other effect that uses the batcher.

### `ClientWorld.cs` hunks (P775 concurrency note)

1. `:650` — a new private `_ownRipcordAnchor` (ot, oid), native's `GetRipcordModel()` stand-in.
2. `:1102` — `TryGetRenderPose(..., bool ripcording = false)` sets `r.Ripcording` (the planned change).
3. `:1689` and `:1705` — doc comments of `RipcordActive` / `RipcordActiveSinceLocalMs` (P781 semantics).
4. `:2663` — `Apply(RipcordActivate)` own branch: re-stamp the start only on a model change.
5. `:2719` — `ClearRipcord()` also clears `_ownRipcordAnchor`.

Hunks 1, 4 and 5 go beyond the "one parameter" plan. They are required by the brief's own test item 1
(the re-target rule, `appmsg.cpp:940-944`) and have no other home: the state lives in `ClientWorld`.

## Deviations from native

- **60 Hz emission clock.** Native emits once per rendered frame, so its density scales with fps. The port
  emits one native call per elapsed 1/60 s (catch-up capped at 4 per frame), so 144 Hz does not double the
  spray. Tested: same density at 144, 60 and 30 fps.
- **2 s safety expiry** (`duration + 2 s`). Native has none. It is only a backstop for a teleport that never
  lands, because the normal clear is the own sector-change edge.
- **Own sector-change clear.** It is in Game1, not in a SET_CLUSTER handler, because the port server sends no
  SET_CLUSTER on a ripcord teleport (P781b).
- **Radius stand-in.** The radius is `0.5 · HullType.Length` (native `m_radius` is the model's bounding
  radius). `ShipRadiusDefault = 50` is used when the hull is unknown.
- **Spark cut-off for icon-size ships.** Native drops a ship's sparks when its hull is reduced to an icon
  (`renderMask = c_ucRenderTrail`, `WinTrek.cpp:7933-7938`). The port emits for every ship that its model
  pass draws (`CanDrawInSector`), including far LOD sprites. This is a visual superset and not cut further.
- **Nearest aleph.** It ignores native's `CanSee` filter. Alephs in the view sector are static map data.
- **No LOD or spark-count cut.**

## Tests (fail-first on base `bbf40def`)

The Game1 tests are reflection-only, so they compile and run unchanged on the base. The ClientWorld tests run
on the base after removing the `RipcordLeadup` section, which is new API.

| test | base | branch |
|---|---|---|
| `P781Game1RipcordTests.Hud_label_drops_at_zero_but_the_ripcord_state_survives_one_second_past_it` | **FAIL** (state cleared at 0) | pass |
| `P781Game1RipcordTests.Own_ship_is_not_frozen_and_keeps_spinning_at_its_activation_rates_during_the_countdown` | **FAIL** ("frozen at the server pose") | pass |
| `P781Game1RipcordTests.Own_sector_change_ends_the_ripcord_state_like_native_SetCluster` | **FAIL** | pass |
| `P781Game1RipcordTests.Hud_safety_expiry_…`, `Hud_label_still_counts_down_before_zero` | pass (unchanged behaviour) | pass |
| `P781Game1RipcordTests.Lead_up_density_…(144/60/30)`, `A_long_hitch_…` | new API | pass |
| `P781RipcordStateTests.Retarget_same_anchor_keeps_the_running_countdown` | **FAIL** (restarted) | pass |
| `P781RipcordStateTests` own/remote set/clear (7 more) | pass (existing behaviour, now pinned) | pass |
| `P781RipcordStateTests` timeline (3), `P781OwnShipRipcordFlightTests` (6), `P781RemoteRipcordReckonTests` (4), `P781TeleportSparkTests` (14) | new API (does not compile on base) | pass |
| spark gather + billboard path, 50 bursts/frame (ad-hoc probe on base, not committed) | **67 905 B/frame, pool capped at 512** | 0 B (`Emit_update_copyto_steady_state_allocates_zero`, `Billboard_fill_matches_the_builder_and_allocates_nothing`) |

The brief's hand table was checked against float arithmetic (`SparkCount_at_the_capture_times_in_float_arithmetic`).
t = 5/3/1.5/0 gives 0/0/4/9 (smoke 3) and 0/0/1/3 (smoke 1), and t = 2.5 → 1 and t = 0.2 → 8 match. **At exactly
t = 1.0 the count is 5, not 6**: `9 × (1 − 1/3f) = 5.9999995f`, which floors to 5. Native float math does the same.
The count is 6 from t ≈ 0.99 down.

Existing fixture edits: `WarpTransitParticlesTests` burst count 3 → 9 (`ShowSmoke` 3). No test asserted
"frozen while ripcording".

## Proof (real `Server` + real `Game1` over TCP, 1920×1080)

`tools/ripcord-proof`. The harness was rebuilt from the WIP. Fixes:

- **Real-time server ticker.** Each `TickSim` advances 1/20 s. The WIP's free-running 5 ms loop ran the sim
  about 10x faster than wall time: the countdown finished before the client saw it, and every own SHIP_UPDATE
  was `out-of-date`.
- **The own ship is launched through `LaunchFromHangar`** (no `LaunchHull` auto-plan).
- **Each scene waits until the client really holds the subject ship.**
- **Cameras.** `ExternalTarget` frames the remote ship and `External` (fixed orientation) frames the own ship,
  so the hull spin is visible. The HUD is hidden in those shots so the target box does not cover the spray.
- **The own ship's spin is the "held stick".** The harness cannot inject input, so until the client decodes
  ACTIVATE it seeds the sim's turn rates every frame (0.45·max yaw, 0.6·max roll, 70 u/s). From the
  activation edge on, only the production rule keeps them.

Raw frames and logs are in `~/allegiance-evidence/P781/{run4,base-run3}` (+ `.log`). The committed crops
(centre 400×400 at native scale) are in `P781_assets/`, 232 KB in total.

| asset | what it shows |
|---|---|
| `p781-A-remote-ripcord.jpg` (t 2.5 / 1.0 / 0.2 / after) | enemy fighter (7 s ripcord hull) spinning, the hull visibly rotated between frames. t 2.5: one small cluster of sparks ahead. t 1.0: a cloud around the ship. t 0.2: dense full burst. After: the enemy is gone and the camera falls back to the own ship. Log: `sparks_live` 10 → 140 → 216 → 0, `remRip=True`. |
| `p781-A-remote-ripcord-BASE.jpg` | same frames on base: the ship spins (server updates) but there are **no sparks**. |
| `p781-B-own-ripcord.jpg` | own Adv Scout in a fixed external view. The hull orientation differs in every frame (spin) and the sparks grow from a few to a full cloud. After transit: in the new sector with the 9-spark transit glow. Log: turn rates pinned `(0.34,0.00,0.47)` across t 2.5/1.0/0.2, speed 70.3 → 20.1 → 15.7 → 13.7 (drag-only), `resets=0`. |
| `p781-B-own-ripcord-BASE.jpg` | base: three identical frames, frozen ship, no sparks. |
| `p781-C-aleph-leadup.jpg` (ETA 2.5 / 1.0 / 0.2) | a ship flying at the Ceto→Ixion aleph. A few sparks ahead, then a cloud, then a full spray with the aleph flare. `sparks_live` 11 → 141 → 220. |
| `p781-A3-full-frame.jpg` | the full 1920×1080 frame of scene A at t 0.2 (downscaled), for context. |

## Performance (`tools/ripcord-proof --perf`, `ALLEGIANCE_NOVSYNC=1`, 1920×1080, Draw-to-Draw frame time over 600 frames after warm-up)

The scene has 50 enemy rippers in the cockpit frustum. Their countdowns are staggered and endlessly re-armed:
abort at about 0.3 s, then re-request on the sim thread, so the ramps overlap and nobody leaves the view.

| run | p50 ms | p95 ms | p99 ms | peak live sparks | game-thread GC B/frame |
|---|---|---|---|---|---|
| base, 50 rippers (×3) | 1.18 / 1.27 / 1.22 | 2.28 / 2.28 / 2.30 | 3.34 / 3.22 / 3.23 | 0 | 390 895 / 387 424 / 385 970 |
| branch, 50 rippers (×3) | 1.63 / 1.62 / 1.61 | 2.69 / 2.56 / 2.43 | 3.61 / 3.00 / 2.89 | 2 677 / 2 677 / 2 668 | 387 919 / 387 991 / 386 251 |
| branch, 0 rippers | 0.65 | 2.66 | 3.03 | 0 | 130 743 |

- **p95 regression: +0.13 … +0.41 ms**, under the 1 ms target.
- The pool never evicts (peak 2 677 of 13 500).
- **The spark path allocates 0 B/frame** (unit tests above). Branch whole-frame GC is equal to base, so the
  added sparks cost nothing. Before the batcher fix, the first branch run measured 663 KB/frame; the
  difference was the batcher's per-draw arrays.
- The **~390 KB/frame on base with 50 remote ships is pre-existing** and outside P781. It is roughly 5 KB per
  remote ship per frame (for example `RemoteShipPose`'s `ori.Copy()`, label and LOD gathers). Follow-up.

## Gate

- `AllegianceCore.slnx` build: **0 warnings / 0 errors**.
- Targeted classes **3× (all green)**:
  - ClientCore.Tests: 146 (P781*, OwnShipSimulator, ShipDeadReckoner, ClientWorldRemoteDeadReckon,
    ClientWorldSoundEvent, ClientWorldShipUpdate, P648RelaunchVisibility).
  - MonoGame.Tests: 64 (P781*, WarpTransitParticles, WarpTransitFlash, P650RipcordCountdown, RipcordRequest,
    RipcordRequestLive, Hud12bReviewFix).
  - Core.Tests: 19 (ShipRipcordFreeze, ShipKinematicsGolden, LiveRipcord).
  - One earlier sweep had a single miss in the new billboard zero-alloc test, caused by a tiered-JIT re-JIT
    landing inside the measured window. Both zero-alloc tests now warm up past tier-up (200 calls, a 300 ms
    pause, 200 more). After that: 8/8 isolated runs and 3/3 sweeps green.
- `tools/run-tests.sh --tier fast`: **9 944 tests, PASS** (283.5 s), 0 failures.
- `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`: **5/5 pass** (about 1 m 51 s each).

## Follow-ups

- **P781b (server, after P775):**
  - `SingleShipUpdate.BIsRipcording = ship.IsRipcordActive` (`Server.Broadcast.cs:1022,1256,1401`; native
    `fsship.cpp:542,807`), and the client honours it (`appmsg.cpp:1154`: model only, no sparks).
  - **Send the owner `SET_CLUSTER` + new cookie on a ripcord teleport** (native `CFSPlayer::SetCluster`,
    `fsship.cpp:742-767`). This removes the one post-transit SHIP_RESET seen in the proof.
- `ShipEngineTrailParticles.ShowSmoke = 1` has the same smoke-default mistake (`thinggeo.cpp:714,773`).
- **Pre-existing crash:** `ShipRenderer.Draw` NRE at `ShipRenderer.cs:163` when the P607 procedural stand-in
  model is drawn (`mesh.ParentBone` null). It is hit by a side-0 dummy that first reaches the client without a
  resolvable hull. The proof uses side-1 dummies to avoid it.
- About 390 KB/frame of game-thread allocation with 50 remote ships in view (pre-existing, see Performance).
- The port sends ACTIVATE for NPCs too (native: players only, `FedSrv.CPP:13354`). This is a superset and
  was left alone.
