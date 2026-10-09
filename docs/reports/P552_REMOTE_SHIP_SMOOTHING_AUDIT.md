# P552 — Remote-Ship Smoothing & Snap Correction: Native vs Port Audit

**Date:** 2026-10-09
**Scope:** READ-ONLY. No code changed. One clean build of `AllegianceCore.slnx` (0 warnings / 0 errors) to confirm the tree was untouched.
**Measured against:** local `Allegiance.Server` at 20 Hz (period 50 ms), map `m2xspiderw`, sides Alpha/Bravo (civ 1/3), live-settings (`testHarness`+`testUnlock`+`adminCheats`) on. MCP 10v10 arena (`spawn_scenario size=10 target_sector=Ganymede arena=true`) → 20 × "Adv Scout" combat bots, real combat occurred (25 kills, 6397 shots, TTK ≈ 4–5 s). Rendered by the headless MonoGame capture client (60 FPS render cadence).
**Sources:** native = `/home/domingo/Development/Allegiance/src/` (commit 052f5179); port = `AllegianceCore` master (commit 59c7ee21).

---

## 1. What native does

### 1.1 Receipt of ship updates — `clintlib/appmsg.cpp`

The client receives periodic `FM_S_HEAVY_SHIPS_UPDATE(176)` batches. The heavy-update handler walks each record and hands it to the ship object:

- `appmsg.cpp:814` — `pship->ProcessShipUpdate(pfmHeavy->timeReference, pfmHeavy->positionReference, hsu);` (non-parent, in-cluster ships; the `ProcessFractions` block the task references is at `appmsg.cpp:819`, preceded by the heavy loop `:798-817`).
- Turrets are updated via `ProcessShipUpdate(timeRef, atu)` at `appmsg.cpp:793`; active-turret gun fires set the weapon mask (`shipIGC.cpp:3314`).

### 1.2 The core smoothing routine — `CshipIGC::ProcessShipUpdate` / `WarpShip` / `CalculateShip` (`src/Igc/shipIGC.cpp`)

For a heavy update (`shipIGC.cpp:3104-3137`):

1. Convert the server sample time to client time and compute `deltaT = GetMyLastUpdate() - timeUpdate` (`:3110-3114`).
2. **Acceptance window:** if `|deltaT| < 1.5 s` **and** the position is legal (`LegalPosition`, `:3090-3095`), accept; else reject `c_susOutOfDate`/`c_susInvalid` (`:3115-3134`). Note: `deltaT` here is the gap between **successive server samples** (≈ one update period), because `GetMyLastUpdate()` is the previous update's sample time.
3. On accept it calls **`WarpShip(timeUpdate, deltaT, &position, &velocity, &orientation)`** (`:3128`).

`WarpShip` (`shipIGC.cpp:3290-3297`):

```
CalculateShip(timeUpdate, deltaT, pPosition, pVelocity, pOrientation);
SetPosition(*pPosition);
SetVelocity(*pVelocity);
SetOrientation(*pOrientation);
```

So **the fresh pose is first projected forward by `deltaT` (≈ one update period) with the real flight model, then written with a hard `SetPosition`** — a snap, but onto a dead-reckoned-forward spot.

`CalculateShip` (`shipIGC.cpp:3244-3288`):
- `deltaT <= 0` (future/past-packet): linear backup `*pPosition += *pVelocity * deltaT` + reverse roll/pitch/yaw by the turn rates (`:3249-3266`).
- `deltaT > 0`: chunked forward integration, "never advancing more than ¼ s per chunk": `n = 1 + (int)(deltaT / c_fTimeIncrement)` (`:3273`), each chunk runs `ExecuteShipMove` (thrust / drag / turn-rate integration) then `*pPosition += *pVelocity * dt` (`:3276-3286`).

`ExecuteShipMove` (`shipIGC.cpp:2841-2930+`) evolves velocity & orientation from control settings, thrust, mass, turn torque/rate and drag — i.e. the same physics the server simulates.

### 1.3 Between updates — the per-frame client sim (`WinTrek` / `CclusterIGC::Update`)

Each frame `WinTrek.cpp:8721` calls `trekClient.m_pCoreIGC->Update(now)`, which fans out to `CclusterIGC::Update(now)` (`clusterIGC.cpp:97`). For each parented ship it calls `s->ExecuteShipMove(now)` (`:225`) — which updates **velocity and orientation only**, not position (`shipIGC.cpp:2848-2849`) — and later `l->data()->Move()` (`:344`), which syncs the render transform to the object position (`modelIGC.h:309-318`: `m_pThingSite->SetPosition(m_pHitTest->GetPosition())`). `SetStopPosition`/`Move(void)` position advance (`Utility.h:849-882`) is only exercised during **collision processing** (`clusterIGC.cpp:604,618`), not per frame for ordinary flying ships.

**Native takeaway:** a remote ship's authoritative/rendered position is *held static between updates* and only changes when an accepted update arrives, where it is **hard-`SetPosition`'d** to a pose dead-reckoned forward by ≈ one update period. Native does **no per-frame interpolation and no blending** between received poses — it steps at the update cadence, with each step ≈ the incremental motion over one period, snapped onto the projected pose. There is no error-decay/convergence; a 10 m correction shows as a 10 m instant snap.

---

## 2. What the port (MonoGame) does

### 2.1 Receipt & storage — `State/ClientWorld.cs`

Heavy updates arrive as `HeavyShipsUpdate(176)` and are applied in `ClientWorld.Apply(HeavyShipsUpdate)` (`:1702-1759`). Each entry is stored verbatim into the entity's **`ShipEntity.Latest`** (`:1731-1739`, `ServerShipUpdate.FromValues(...)`), stamped with a local arrival clock `LatestArrivalTime = NowSeconds()` (`:1742`, monotonic `Environment.TickCount64/1000`), then the per-ship dead-reckoner is (re-)seeded (`ReseedRemoteReckoner(e)`, `:1743`). The same stamp+reseed happens at the other two pose-arrival sites (`:1431-1432` SetCluster, `:1664-1665` SingleShipUpdate).

### 2.2 The dead-reckoner — `Flight/ShipDeadReckoner.cs` (P448a/P448b)

`ShipDeadReckoner` is an explicit port of native `CalculateShip`/`WarpShip` (`ShipDeadReckoner.cs:10-18`):
- `Seed(...)` stores the authoritative position/velocity/orientation/turn-rates/controls + resolved hull stats, anchored at the **local arrival time** (`:138-166`).
- `PoseAt(now,...)` replays the real flight model forward from the seed, with the **identical chunk scheme** `n = 1 + (int)(ΔT / 0.25 s)` (`:237`) and `ShipKinematics.Step` (≡ native `ExecuteShipMove`) (`:239-246`); backward path and 1.5 s cap mirror native's branch / acceptance window (`:219-230`, `:64,208-209`).
- It is **pure forward extrapolation** — no blending between received poses (the old `RemoteShipPredictor` that *did* blend was removed; this re-runs physics instead, `:13-18`).

Render-time selection — `Game1.FlightInput.cs:1955-1963` `RemoteShipPose`:
- If the P448b kill switch `_remoteShipDeadReckoning` is **on** (default `true`, `Game1.Session.cs:89`; `Game1.cs:1323`) **and** the ship's reckoner is seeded, the pose is `TryGetRenderPose` → `PoseAt(now)` (`ClientWorld.cs:514-524`).
- **Otherwise it falls back to the raw `Latest` pose** (`FlightInput.cs:1962`: `new PredictedPose(up.Position, up.Orientation)`) — a hard 20 Hz step.

Seeding requires hull stats via `RemoteShipHullResolver` (`ClientWorld.cs:501-502`; body `Game1.Rendering.cs` `ResolveRemoteHullStats`, registered `Game1.cs:1213`). If the hull is not (yet) in the shop catalogue, or the catalogue/side GA are unavailable, the resolver returns **null** → `ReseedRemoteReckoner` returns early (`ClientWorld.cs:540-541`) → the ship renders **raw** (full per-update step, no smoothing).

The re-seed correction (the visible snap) is measured at every re-seed (`ClientWorld.cs:545-556`): `corr = |prevPoseAt(arrival) − newAuthoritative|`, accumulated into `_remoteCorrectionStats`, logged live by the client (`FlightInput.cs:1808-1814`).

### 2.3 Native-faithfulness gaps that matter

1. **Anchoring:** native dead-reckons from the *server sample time* and projects to the ship's last-update time; the port anchors on *local arrival time* and projects to render-`now`. Functionally equivalent in steady state (window ≈ one update period), but the port's window follows network-jittered arrival gaps, so it overshoots/undershoots slightly more on jittery links.
2. **Between-update motion:** native *holds* the rendered position (steps at 20 Hz); the port *extrapolates continuously every frame* — the port is **smoother than native between packets**.
3. **Correction application:** **both hard-snap.** Native `SetPosition` on every accepted update; port re-seeds (instant pose jump) on every 176. Neither blends/decays the residual error. This is the source of the remaining visible teleports.
4. **Raw fallback:** native always has hull stats (it runs the sim), so it never falls back to unstepped raw poses; the port can fall back to raw `Latest` when the hull/catalogue can't resolve or the kill switch is off.

---

## 3. Diff table

| Aspect | Native | Port (P448b, default) |
|---|---|---|
| Update receipt | `appmsg.cpp:814` → `ProcessShipUpdate` | `ClientWorld.cs:1743` `Apply(HeavyShipsUpdate)` |
| Authoritative pose stored | `CshipIGC` members | `ShipEntity.Latest` + `LatestArrivalTime` |
| Dead-reckon formula | `CalculateShip`, chunk `n=1+ΔT/0.25`, `ExecuteShipMove` | `ShipDeadReckoner.PoseAt`, chunk `n=1+ΔT/0.25`, `ShipKinematics.Step` (same) |
| Anchoring | server sample time → last-update time | local arrival time → render-now |
| Between updates | **holds** position; steps at update rate | **extrapolates** every frame (smoother) |
| Correction | `WarpShip` → **hard `SetPosition`** | **re-seed** → instant jump (same, hard) |
| Blend / error decay | none | none |
| Accept window | `\|ΔT\| < 1.5 s` (`shipIGC.cpp:3114`) | 1.5 s extrapolation cap (`ShipDeadReckoner.cs:64`) |
| Fallback | always has sim → never raw | raw `Latest` when hull unresolved / kill switch off |

Native and port implement **the same dead-reckoning-on-update + hard-snap** model. The port additionally glides between packets (nicer than native), and the only way it gets *worse* than native is the raw-`Latest` fallback.

---

## 4. Measurement

### 4.1 Setup
Local server, 20 Hz, arena 10v10 (20 "Adv Scout" combat bots) converging on the client's sector; real combat (25 kills, TTK ≈ 4–5 s). Server + MCP `get_perf_stats`; client `[P448b]` re-seed correction log.

### 4.2 Raw per-update step (server side — what a client with *no* dead-reckoning would snap by)
`get_perf_stats`, 200 real samples/bot, 20 bots, at 20 Hz:

| metric | mean | p95 | worst |
|---|---|---|---|
| **update interval** (s) | 0.0502 | 0.0503 | 0.0605 |
| **position jump** (m / update) | **3.77** | **8.77** | **17.77** |

The 20 Hz cadence is rock-solid (interval mean = period). Without smoothing, each ship would visibly hop ≈ 3.8 m mean, up to ~18 m worst, per 50 ms frame-boundary. This is the same class of number Domingo's earlier MCP-perf captured (2.07→4.46 mean; 4.65→11.96 worst) — the variation is fight/travel dynamics.

### 4.3 Visible snaps with dead-reckoning ON (client `[P448b]` re-seed corrections)
18,290 re-seed corrections captured over ≈ 38 s of active combat (≈ 24 ships × 20 Hz):

| quantity | value |
|---|---|
| per-window mean correction | avg **0.095 m**, median 0.093 m (range 0.044–0.144 m) |
| per-window **max** correction | median **1.17 m**, p90 **5.0 m**, overall **10.71 m** |
| windows with a ≥ 2 m snap | 7 of 42 (three hit ≈ 10.5 m) |

**Interpretation.** With P448b dead-reckoning on (the default), the typical re-seed correction is ~0.1 m — a **~40× reduction** from the raw 3.77 m step, and visually invisible. The "small teleporting" Domingo sees in 10v10/15v15 is the **per-window tail**: roughly every 2 s one ship shows a ≥ ~1 m correction, about 10% of windows contain a ≥ 5 m snap, and rare hard maneuvers produce ≈ 10.5 m spikes (a ship that changes thrust/heading harder than the stale controls in the last seed predicted). These are hard, instant pose jumps — there is no convergence — so even a 1–2 m correction can read as a micro-teleport at close range.

### 4.4 Native equivalence
Native would exhibit the **same or larger** per-update correction magnitude: it hard-snaps **every** update, and its step is the full incremental motion (≈ velocity × period, i.e. the 3.8 m mean / 17.8 m worst above), because it holds position between packets. The port's continuous extrapolation collapses that into the residual error only. **There is no evidence native is smoother; native provably steps harder.** Any residual port snap is ≤ the dead-reckoning error over one period, which native also incurs (and expresses as an even larger jump).

---

## 5. Recommendation (ranked)

Goal: remove the *visible* residual snaps with the smallest, safest change.

**Option 1 — bounded error-decay blend on re-seed (RECOMMENDED).**
When a re-seed correction exceeds a small threshold (e.g. > 0.5 m), converge the *render* pose from the previous extrapolated pose to the new seed over a short window (~50–100 ms, ≈ 1–2 update periods) instead of snapping in one frame. Implement as a small field in `ShipDeadReckoner` (keep an error vector + decay that `PoseAt` folds in) or in `RemoteShipPose`; zero wire/server changes; preserves all native physics fidelity. Estimate **~0.5–1 day**. It goes one notch *past* native (native snaps harder), which is precisely why it removes the teleport — native is not a smoothness target here.

**Option 2 — eliminate the raw-`Latest` fallback (hardening).**
Guarantee every visible remote hull resolves in `ResolveRemoteHullStats` (complete catalogue coverage + a hull-stat cache), and keep the kill switch off by default, so no ship ever renders at the raw 20 Hz step (3.8 m mean). This is the only path where the port is *worse* than native and would show severe teleporting. Estimate **~0.5 day**.

**Option 3 — match native exactly (no change).**
Evidence (§4.4) shows native hard-snaps every update with ≥ the port's correction magnitudes. Current P448b already outperforms native (typical snap ~0.1 m). Acceptable only if the residual rare 1–10 m maxima are tolerable; they are inherent dead-reckoning error under aggressive maneuvering that native exhibits *more* loudly. Estimate **~0**.

**Suggested sequencing:** land Option 1 (removes the perceived teleporting outright) and apply Option 2 as a guard so the smoothing path is never silently bypassed. Option 3 is the fallback if the team judges native-fidelity sacrosanct.

---

## Appendix — measurement artifacts
- `~/.hermes/cache/scratch/p552/run1.log` — 10v10 homogeneous travel mode (server-side `get_perf_stats`).
- `~/.hermes/cache/scratch/p552/run2.log` + `caprun/client.log` — arena 10v10 with the capture client (server perf + client `[P448b]` corrections).
- `~/.hermes/cache/scratch/p552/server.log` — server boot/activity.
- Full build of `AllegianceCore.slnx` (Release) — 0 warnings / 0 errors; no tracked files modified (repo clean; only this report added).
