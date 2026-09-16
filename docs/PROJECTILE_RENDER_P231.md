# P231 — Render weapon projectiles in the MonoGame client

Date: 2026-09-16. Stage P231 of the staged AllegianceCore port, landed on `master`
directly (not pushed — Domingo pushes).

P221 already wired **real weapon fire** — the server creates real `Projectile`
objects and broadcasts `PROJECTILE_INSTANCE(12)` (`Server.BroadcastProjectileInstance`,
`Server.cs:9521`) — but until this stage the MonoGame client had **zero**
client-side handling of that broadcast: shots were simulated and broadcast but
invisible to every player. This stage makes them render as colored streak/bolt
geometry.

---

## 1. What the client already had, and the architectural question

P221 verified live that the server fires real projectiles and broadcasts them
(the `[projectile] ship N fired -> PROJ_INST(12) type 4 pos ... vel ... -> 1
same-side peer(s)` log), and the `ProjectileInstance` codec (fmid 12,
`src/Allegiance.Core/Wire/ProjectileInstance.cs`) already carries everything a
renderer needs geometrically: `Position`, `Velocity`, `Forward`, `Lifespan`,
`ProjectileTypeId`. What was missing was any code in `Allegiance.ClientCore` /
`Allegiance.ClientCore.MonoGame` that consumes the message.

### The static-core / per-type-colour question (checked, per the brief)

The brief asked to re-confirm whether the MonoGame client has any static-core
access. It does **not**, and that is unchanged since P230's finding:

- `ClientCore` has a single ProjectReference to `Allegiance.Core` (the **wire
  codecs** only). `MissionInfo` (`State/MissionInfo.cs`) carries only the static
  core **filename** and **version** — no `DataProjectileTypeIGC` instances.
- A repo-wide grep for `StaticCore`/`MissionMap`/core-loading in the client
  projects returns nothing.
- Therefore the client cannot look up `DataProjectileTypeIGC.Color/Width/
  Directional` for a projectile type id. Making the per-type visual data
  reachable would require dragging the whole static-core loader (`IgcReader` +
  every `Data/*` decoder + `StaticCore.Load`) into the client and locating a
  core file at runtime — a real prerequisite that this brief explicitly allows
  deferring ("a single reasonable default colour with a note in your summary"),
  and disproportionate for a POC renderer. The wire `ProjectileInstance` itself
  cannot be widened (it is a byte-faithful port of a real native struct, P88).

**Consequence:** the client renders every projectile as a **single default amber
tracer colour**, sized from the real per-instance velocity (which *is* on the
wire). Real per-type `Color`/`Width`/`Directional` remain unreachable client-side
this stage. The renderer takes the tint as a parameter so real per-type colours
can be plugged in later without touching the mesh.

---

## 2. Implementation summary

### `ClientWorld` projectile tracking (ClientCore, pure, no MonoGame)

- `ProjectileEntity` (`State/ProjectileEntity.cs`): a transient entity capturing
  `ProjectileTypeId`, `Position`, `Forward`, `Velocity`, `Lifespan` and a local
  monotonic `ReceivedLocalMs` (the receipt instant via
  `Environment.TickCount64`). The wire `TimeCreate` is a server-tick index in an
  unknown epoch, so the port anchors **both** expiry and motion on the local
  receipt instant — the honest client-side equivalent of native's
  creation-time tracking (a same-host client receives ≈ creation).
- `ClientWorld.Apply(ProjectileInstance)` appends one entity per broadcast —
  a growing **list**, not an id-keyed dict, faithful to native's fire-and-forget
  projectiles (no persistent id on the wire). `ClientWorld.Projectiles` exposes
  it as `IReadOnlyList`.
- `ClientWorld.PruneExpiredProjectiles(nowLocalMs)` removes entries whose
  lifetime (`ReceivedLocalMs + Lifespan*1000`) has elapsed. `ClientWorld` is
  pure (no clock), so the caller supplies the time and calls it per frame under
  the same `ConnectionSession.Gate` lock that guards the other world reads.
- `ProjectileEntity.PositionAt(nowLocalMs)` = creation `Position` advanced along
  `Velocity` by elapsed seconds (clamped to never travel before creation) — the
  brief's "simple extrapolation, no full prediction system".

Wired in via a new `case ProjectileInstance.Fmid` in `ConnectionSession`'s
receive switch (decode → `_world.Apply`).

### `ProjectileRenderer` (MonoGame)

`Rendering/ProjectileRenderer.cs` follows the P218/P227 "generated, not loaded"
discipline — a single shared **unit box** `VertexBuffer`/`IndexBuffer` built in
the constructor, no content-pipeline asset. Per-instance `Draw(world, tint,
view, proj)` sets `BasicEffect.DiffuseColor = tint` (vertex colours baked white,
so real per-type colours slot in later) and draws with `CullMode.None`.

A **thin box** (not a flat quad) is used deliberately: a flat decal quad read
edge-on degenerates to an invisible line, whereas a thin elongated box always has
projected area — it reads as a bolt/streak from every angle. Native's
`Directional` flag distinguishes stretched decals from flat circles; it isn't on
the wire and isn't reachable from static core, so every projectile renders as a
**directional** streak (the only style needing no extra data).

### `Game1.Draw` hook

A projectile draw loop beside the P218/P227 asteroid/warp loops. Because a
projectile carries no sector on the wire (and the client has no static core to
map one), the "sector filter" analog is a **distance-based visibility test**
against the local ship (`ProjectileVisibilityRadiusSqr` ≈ the camera far plane)
— the closest the wire supports to "is this in my sector", preventing a
same-side teammate's far-sector shot from rendering. Gather + prune both ride
the `Gate` lock; draws happen after release (the established pattern).

Per bolt: position = `PositionAt(now)` (velocity extrapolation — fast shots
don't snap between broadcasts), orientation = `Orientation.Set(normalized
Velocity)`, falling back to `Forward` for a near-static bolt; **length ∝ speed**
(faster shots read as longer streaks, clamped), a **fixed default width** (no
per-type `Width` on the wire); tint = the single default amber tracer.

Defaults (`ProjectileWidth=8`, `BoltLengthPerUnitSeconds=0.12`, `Min/MaxBoltLength`
32..480, `ProjectileTint` amber) were tuned from the live measurement below
(600 u/s muzzle velocity, 1.1 s lifespan → 33..72-unit streaks); the captures
used enlarged values purely to make the tracer stream unambiguous for the proof.

---

## 3. Live proof (real stack, real weapon fire)

Reused P221's live-fire recipe: a temp `teams:1` server config (port 6233) +
temp overrides that (a) spawn a fight-capable hull (has energy/ammo/fixed
hardpoint so `MountCombatWeapon` arms it) instead of the stock unarmed Adv Miner,
and (b) auto-hold Fire + re-undock after the real Started and force the chase
camera. Offscreen `RenderTarget2D` capture (the P207/P218/P228 technique)
wrote 8 frames. **All temp overrides reverted; `Server.cs` diff is empty.**

Real server wire log (proof the broadcaster fires real projectiles to the client):

```
[projectile] ship 8096 fired -> PROJ_INST(12) type 4 pos Vector3 { X = 720, Y = 730, Z = 920 }
            vel Vector3 { X = 0, Y = 0, Z = -600 } fwd (0,0,-1) life 1.1 -> 1 same-side peer(s)
[ctrl] 1 ship 8096 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x8000   ← AllWeapons fire
```

Vision-checked captures (`.verify_p231/proj_frame*.png`): the chase frames show a
bright amber **projectile/tracer stream discharging forward from the ship's nose**
("muzzle flash and projectiles/tracers being discharged from the ship's forward
cannons... directly in front of the ship's nose", with the P221 red `FIRING` tag),
and the amber region reshapes across frames (a narrow column when the ship held
course; a wide receding cone when it pitched up) — the bolt stream is visibly in
motion, new bolts spawning at the muzzle and receding. Cross-frame amber-pixel
analysis confirmed the dynamic stream tracks the weapon's fire state.

---

## 4. Build & test status

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- `Allegiance.Core.Tests`: **3050 total** — baseline unchanged (this stage adds
  no Core tests). Full suite 2×: both runs 3049 passed / 1 failed, the sole
  failure each time the documented pre-existing
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  machine-state flake (fails persistently in isolation and on the pristine tree;
  zero refs to any P231 symbol — not chased, per the port's established policy).
- `Allegiance.ClientCore.Tests`: **75 total, 0 failed** (+7
  `ClientWorldProjectileTests` — the pure tracking/expiry/extrapolation unit
  tests). Full suite 2×.
- `Allegiance.ClientCore.MonoGame.Tests`: **159 total, 0 failed** (unchanged —
  the renderer is pure MonoGame drawing; the testable domain logic lives in
  ClientCore). Full suite 2×.

Must-fail check: neutralizing the `case ProjectileInstance` in
`ClientWorld.Apply` reds
`ClientWorldProjectileTests.ProjectileInstance_AppendsEntity_WithCapturedFields`
(`Assert.Single ... collection was empty`), then restored green.

---

## Scope boundary

- Missile rendering — a separate, bigger task (needs the real `.mdl` parser);
  untouched.
- Weapon-fire sound / projectile in-flight ambient (P232) — not added.
- Real texture-mapped decals (loading `.mdl`-wrapped textures) — out of scope; a
  solid-colour procedural bolt is the P218/P227 precedent's analogue.
- Per-type `Color`/`Width`/`Directional` — not reachable client-side this stage
  (no static core; wire carries only `ProjectileTypeId`); single default amber
  tracer used and documented (§1). Renderer tint is parameterized for a later
  hook-in.
- Native reference source (`~/Development/Allegiance`) untouched (read-only).
