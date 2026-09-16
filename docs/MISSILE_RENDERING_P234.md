# P234 — Track and render live missiles in the MonoGame client

Landing stage: real client-side tracking of in-flight missiles plus rendering of a
real parsed `.mdl` missile model, in the MonoGame client of the AllegianceCore C#
port. Landed on `master` (not pushed — Domingo pushes), no side branch.

---

## 1. The broadcast design decision (what the client needs and why)

**Question the task posed first:** is there any client→server / server→client
missile-position broadcast today beyond the one-shot creation?

**Verified state at start of this stage:**

- Creation **already existed** (P90): `ServerHost.MissileCreated = BroadcastFireMissile`
  (Server.cs:439) sends a real `FM_CS_FIRE_MISSILE(70)` carrying one
  `MissileLaunchData{position, forward, velocity, id}` plus type/cluster, to the
  launcher's same-side peers.
- Destruction **already existed** (P90): `MissileDestroyed` → `FM_S_MISSILE_DESTROYED(83)`.
- **No ongoing position broadcast existed.** Native Allegiance has *no* per-tick
  missile-position message either (its only missile messages are the creation 70,
  `FM_S_MISSILE_DESTROYED` 83 and `FM_S_MISSILE_SPOOFED` 110) — because native's
  client ran its **own local IGC simulation** of each missile (including P179-style
  homing/turning), so creation-plus-destroy sufficed.

**The port's constraint:** the MonoGame client is a *pure renderer of
server-authoritative state* with **no static core** — it cannot simulate the
missile's homing/turning locally (the same architecture finding P230 recorded for
missile lock). Yet P179/P181 make missiles genuinely home — turning and
accelerating every sim tick — so a straight-line extrapolation from the launch
velocity (the P231 projectile approach) would visibly diverge over a missile's
multi-second flight.

**Decision (option B — a new per-tick broadcast, mirroring P230's precedent):**
add a this-port-only `FM_S_MISSILE_UPDATE(205)`, sent once per sim tick from the
same loop cadence as `BroadcastMissileLock`/`BroadcastShipsUpdate`, carrying each
in-flight missile's current `{id, type, cluster, position, forward}`. FMID **205**
sits above native's real maximum (200), exactly like P230's 204, so it can never
collide with a faithfully-ported native message. This is the port's honest
equivalent of native's client-side simulation: the server streams the
authoritative state and the renderer draws it.

Audience: **same-side only**, batched per side like `BroadcastProbesUpdate` — a
peer sees missiles launched by ships on its own side (cross-side visibility is
not attempted, matching the creation broadcast). It is deliberately a hair
broader than creation's export-sent micro-gate: a peer that missed the one-shot
FIRE_MISSILE can hydrate a missile straight from the per-tick update (ClientWorld
upserts on update), so the two audiences stay coherent even across a missed
creation.

Wire layout (no native `DEFINE_FEDMSG`; our own, natural 4-byte alignment):
```
  MissileStateRecord (32 B): short missileId @0, short typeId @2, short clusterId @4,
                            pad @6, Vector position @8, Vector forward @20
  MissileUpdate (205): header(4) + ib/cb var descriptor(4) + MissileStateRecord[n]
```

`ClientWorld` then models missiles as an **id-keyed** `MissileEntity` table (missiles
carry a real id, unlike id-less projectiles): hydrated by 70, kept current by 205,
removed by 83, plus a 30 s stale-horizon pruner as a missed-destroy guard (the
client has no static core to know `Lifespan`).

---

## 2. Winding / culling — the empirical finding

The task required **not assuming** the P233 note that the DirectX winding might
need a MonoGame flip. Verified empirically with an offscreen `RenderTarget2D`
render of the real `mis05.mdl` at a large broadside view:

- Raw file indices under MonoGame's default `CullCounterClockwiseFace` →
  **20 interior-hole pixels (0.486%)** in the concave regions (fin roots / nozzle)
  where back-face culling wrongly removes near faces. Mostly-convex bodies fill
  their silhouette under either winding, so the defect shows up precisely in
  concavities — exactly why an eyeballed "looks fine" is insufficient.
- Reversing the winding (index flip) under the same cull → **0 interior holes**.

**Fix applied:** keep the raw file indices and switch the renderer's rasterizer to
`CullMode.CullClockwiseFace` — MonoGame's own mechanism for a DirectX-authored
mesh. Committed renderer (CW cull, raw indices): **0 interior holes**.

(The fix is *not* the same toggle the Python/Blender mesh path needed — that path
had its own handedness special cases; the committed `.mdl` path uses
`CullClockwiseFace` and nothing else.)

## 3. Orientation — the (second) empirical finding

Loading the mesh was not enough; the model had to point the right way. Verified
from the geometry that `mis05.mdl`'s **pointed nose is at model +Z** (the +Z end
converges to an apex at (0,0,5.62); the -Z end is a flat, wider engine/nozzle
base where the `thrust`/`trail`/`rocket` frames attach). This port's ship models,
by contrast, are nose-at-**-Z** — which is why `Orientation.Set(forward)` +
`PoseConverter.ToXnaRotation` orients ships correctly (local +Z maps to
*backward* = -forward).

Because the `.mdl` nose is +Z, `o.Set(missile.Forward)` rendered the nose pointing
**backwards** along the flight path. **Fix:** the missile draw loop builds the
orientation with `o.Set(-missile.Forward)`, so model +Z (nose) maps to the missile's
real heading. Confirmed in the render: with the fix, the pointed nose points
along the actual `+X` flight direction.

## 4. Live-proof screenshot observation

A throwaway harness (outside the repo, `/tmp`) booted the **real stack**: an
in-process `Allegiance.Server` using P226's recipe (spawner mounts a
`MaxLock==0` missile launcher + places an enemy `CommandTarget` so the missile
homes), connected a **real `ConnectionSession`**, and fired a real missile over the
real `SendFireMissile` wire. The missile's live pose came straight from the real
client world (ClientWorld.Missiles, driven by the real 205 stream). It rendered
with the **committed** `MissileRenderer` and Game1's exact world-matrix math
(`PoseConverter.ToXnaRotation` + `o.Set(-forward)` + `translate(pos − cameraEye)`),
captured offscreen to `.verify_p234/mis_committed.png`.

Observed: a solid, opaque, correctly-oriented missile — pointed nose, cylindrical
fuselage, tail fins — flying along its real `+X` heading, with **0 interior holes**
(CW cull). The missile's per-tick motion over the 205 stream was also proven by
the `MissileTrackingLiveTests` movement assertion (frozen at the one-shot pose
without the 205 broadcast — see §5).

Evidence renders kept (untracked) in `.verify_p234/`:
- `mis_straight.png` — raw indices + default CCW cull: 20 interior-hole px (the defect).
- `mis_flipped.png` — flipped indices + CCW cull: 0 holes (alternative proof).
- `mis_committed.png` — **committed** renderer (CW cull + corrected orientation): 0 holes.

## 5. What changed

- `src/Allegiance.Core/Wire/MissileUpdate.cs` *(new)* — this-port-only 205 codec
  + `MissileStateRecord` (32 B fixed records; ib/cb var-array like FIRE_MISSILE).
- `src/Allegiance.Server/Server.cs` — `BroadcastMissilesUpdate()` per-side,
  per-tick in the sim loop (beside `BroadcastMissileLock`).
- `src/Allegiance.ClientCore/State/MissileEntity.cs` *(new)* + `ClientWorld` —
  id-keyed `Missiles` table; Apply for 70 (hydrate), 205 (upsert), 83 (remove);
  `PruneExpiredMissiles` stale guard.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — receive cases 70/205/83.
- `src/Allegiance.ClientCore.MonoGame/Rendering/MissileRenderer.cs` *(new)* — builds
  one combined VB/IB from the parsed `.mdl` meshes; `CullClockwiseFace`; lit solid.
- `src/Allegiance.ClientCore.MonoGame/Rendering/MissileModelSource.cs` *(new)* —
  artwork path + the P234 single-model scope (`mis05` = PCore001 type 34).
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — missile draw loop (sector-filtered
  by `ClusterId`, `o.Set(-forward)`, per-instance world), renderer construction + dispose.

**Tests (all must-fail-if-removed verified):**
- `MissileUpdateWireTests` (Core) — 5: 205 codec round-trip, empty records, fmid>200,
  wrong-fmid rejection, 32 B record size.
- `ClientWorldMissileTests` (ClientCore) — 7: hydrate from 70, upsert/move from 205,
  hydrate-unseen-id from 205, remove from 83, forward normalization, empty-salvo no-op,
  stale prune.
- `MissileTrackingLiveTests` (ClientCore) — 1 live loopback: a real fired missile
  appears in the real client's world, **moves** across the per-tick 205 stream, then
  disappears on destruction. **Must-fail proven:** disabling `BroadcastMissilesUpdate()`
  makes the movement assertion fail (missile stuck at the one-shot spawn pose) — the
  discriminating proof that the 205 stream, not the 70 creation, is what enables
  client-side in-flight tracking.

## 6. Build / test status

- Whole-solution build: **0 warnings, 0 errors**.
- `Allegiance.ClientCore.Tests`: **83 passed, 0 failed** (baseline 75 → +8).
- `Allegiance.ClientCore.MonoGame.Tests`: **170 passed, 0 failed** (unchanged).
- `Allegiance.Core.Tests`: full run (see final run); only the pre-existing
  `LiveTurretGunnerBindingTests` machine-state flake is expected to interrupt.

Scope respected: no smoke trail (P235), no missile audio (P236), no texture
(not decoded by P233) — solid lit render; no changes to native reference (read-only)
or to P233's parser (only consumed).

## 7. Future work (out of scope, noted)

- Per-type model selection: the renderer loads ONE real mesh (`mis05`); mapping the
  wire typeId → `ModelName` needs a static-core-fed map (future task).
- Smoke-trail particles (P235) and missile audio (P236) are separate stages.
