# P463b — Shield-hit flare (the shield bubble flash)

Part 2 of the three-way hit-feedback split. Consumes the P463 wire signal
(`AbsorbedByShield` on `PROJECTILE_HULL_HIT(213)`) and draws the native
"shield bubble lights up where you hit it" cue on the struck ship's shield.

---

## 1. Prerequisite

`git log --oneline | grep P463` → landed at `40dfdbbe` ("P463: hit sounds
(my/other x shield/hull) and hit feedback on shield-absorbed hits") on
`master`. The P463 signal exists: `Wire/ProjectileHullHit.cs` carries a
one-byte `AbsorbedByShield` flag (`Dr == ShieldDamage`), the server raises it
for fully shield-absorbed strikes, and the client's `HullHitEntity` surfaces it
in both the `HullHits` renderer stream and the `HitSoundEvents` audio stream.
Branch is `master`; work committed there (not pushed) per the workflow.

---

## 2. Native flare details (documented with file:line)

**Call site.** `CshieldIGC::ApplyDamage` fires the flare on every
shield-absorbed strike (shield above the downed threshold, non-`c_dmgidNoFlare`
damage type), passing the hit delta-momentum as the direction and the ship's
hit-test ellipse equation:
`src/Igc/shieldIGC.cpp:171`:
```cpp
m_ship->GetThingSite()->AddFlare(timeCollision, deltaP, 0, m_ship->GetHitTest()->GetEllipseEquation());
```

**Client wrapper.** `TrekIGC::ThingSiteImpl::AddFlare`
(`src/WinTrek/trekigc.cpp:1624-1640`) gates on `GetFlareCount() < 4`
(`:1627`), builds a `TextureGeo` of the shield-flare artwork (`m_pimageFlare`,
id 0 = the `lens30` geo), negates the incoming direction, and calls the
client-side `ThingGeo::AddFlare`.

**Artwork.** The flare is a lens sprite. `artwork.h:208-209`:
`AWF_SHIELD_FLARE = "lens"`, `AWF_SHIELD_FLARE_TEXTURE = "fx09bmp"`. Loaded in
`trekigc.cpp:2512-2522`: the surface `fx09bmp.mdl` (checked against
`~/Development/Artwork/fx09bmp.mdl`) is wrapped in an `AnimatedImage`, and the
geo namespace "lens" supplies `lens30.x`/`lens60.x`/`lens90.x` (id 0 = `lens30`,
the one used here). Additively blended → the classic bright lens flash.

**Placement + size (the ellipsoid).** `ThingGeo::AddFlare`
(`src/effect/thinggeo.cpp:1243-1301`):
- `vecLocal = m_orientation.TimesInverse(-vecDirection)` (`:1256`) — the
  (negated) hit direction in the ship's local frame.
- With an ellipse equation, each local component is divided by the hit-test
  ellipsoid's per-axis extent (`:1255-1258`), so the direction maps from the
  centre out to the impact point on the ellipsoid; the flare geo is oriented
  along it (`orient.Set(vector)`, `:1271`) and scaled by `ellipse / m_radius`
  (`orient.Scale`, `:1272`) so it sits ON the shield at the hit.
- The geo is wrapped in `Matrix mat(orient, Zero, m_radiusMesh)` (`:1289`), so
  the rendered size scales with the ship's mesh-bounding radius
  (`m_radiusMesh`, `:1053`) — i.e. roughly the ship's radius.

**Lifetime + fade.** The flare is a `TimedGeo` over **0.75 s**
(`thinggeo.cpp:1304`) with **`BlendModeAdd`** (`:1298`): it flashes on at birth
then fades to nothing over 0.75 s. Admission is capped at **four live flares
per ship** (`GetFlareCount() < 4`, `trekigc.cpp:1627`); each expired flare
decrements the counter (`thinggeo.cpp:1230-1237`).

**Native vs port substitution (documented).** The port has no static per-axis
hull hit-test extents to build the true 3-axis ellipsoid, so the shield is
approximated as a **sphere of the ship's real render scale** —
`0.5 × HullType.Length` via P333's `_hullLengthById` (the same measure the whole
renderer uses) — and the flare is placed at `centre + unitDir · radius` on that
sphere toward the hit (the wire's `Normal` = `normalize(position1 − position2)`
is the outward hit direction). The per-ship cap of 4, the 0.75 s lifetime and
the additive blend are reproduced faithfully.

---

## 3. Implementation

Following the `HullHitSparkParticles` / `HullHitSparkRenderer` split (P364):

**`Rendering/ShieldFlareParticles.cs`** (pure, MonoGame-free) — a pool of live
flares, one per absorbed hit:
- `Emit(target, shipCenter, hitDirection, shieldRadius)` places the flare on the
  shield toward the hit (`centre + unitDir · radius`, size = `SizeFactor ×
  radius`, `SizeFactor = 0.5`), honours the native per-ship cap of
  `MaxFlaresPerShip = 4` (evicting that target's oldest), and the fixed pool
  capacity.
- `Update(dt)` ages/expires over `LifetimeSeconds = 0.75`.
- `Enumerate()` yields `ShieldFlareView(Center, Direction, Size, RemainingFrac)`
  oldest-first for the renderer.
- Zero/hit-direction fallback and `ShipRadiusDefault` mirror the spark pool.

**`Rendering/ShieldFlareRenderer.cs`** — draws each flare as a camera-facing,
soft-edged additive quad (procedural sprite, no asset), light ice-blue-white
(the port's colour choice, distinct from the warm-white hull spark) fading over
the native 0.75 s life; single `DrawUserIndexedPrimitives` under one additive
blend, restoring depth/blend/raster states (P339 B2) exactly like
`HullHitSparkRenderer`.

**Client wiring.**
- `Game1.cs` — fields `_shieldFlareParticles`, `_shieldFlareRenderer`,
  `_drawShieldFlares`; renderer disposed with the rest.
- `Game1.Lifecycle.cs` — constructs `_shieldFlareRenderer` beside the spark
  renderer.
- `Game1.Rendering.cs` — a P463b block right after the P364 spark pass drains
  the **same** `_pendingHullHits` (already cluster-gated to the local ship's own
  sector), but emits a flare only for `AbsorbedByShield` (a hull strike spawns
  the spark, never the flare — native `AddHullHit` is hull-or-worse only,
  `projectileIGC.cpp:224`). For each absorbed hit it resolves the struck ship's
  real centre + render scale under the `Gate` (`world.Ships[TargetId].Latest`
  + `0.5 × HullLength(HullId)`, falling back to the wire radius/impact point),
  then ages + draws. This covers **our own ship** (TargetId == MyShipId is a
  normal `world.Ships` member, so the flare shows in chase view) and **every
  other ship**.

Out of scope honoured: no server/wire change (the P463 signal already carries
everything the flare needs: the absorbed flag, the outward hit normal, and the
struck target); no sounds (P463) / collision sounds (P463c) / damage flash; the
P364 hull spark is untouched.

---

## 4. Verification

### 4.1 Pure unit tests — fail first, then green

Written against the intended API before the type existed
(`tests/Allegiance.ClientCore.MonoGame.Tests/ShieldFlareParticlesTests.cs`).
The recorded failing run (RED):

```
ShieldFlareParticlesTests.cs(30,24): error CS0246: The type or namespace name
'ShieldFlareParticles' could not be found (are you missing a using directive?)
```
(A stack of CS0246 for every test — the type genuinely didn't exist.)

After implementing `ShieldFlareParticles`, GREEN — 9/9:
```
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9
```
covering placement on the shield toward the hit, non-unit/zero-direction
normalisation, size scaling with render scale, the native 0.75 s lifetime and
linear fade, the per-ship cap of 4 (oldest evicted), multi-target pooling and
reset.

### 4.2 Live evidence — `tools/shieldflare-proof` (real server, real wire, real renderer)

A harness booting a REAL in-process server hosting a REAL fight (the same
cheap, fully-shield-absorbing round P463's hitsound-proof used), connecting
REAL bots + a REAL `ConnectionSession` observer. It harvests the observer's REAL
absorbed `PROJECTILE_HULL_HIT(213)` events, proves the wire signal
programmatically, then renders the REAL `ShieldFlareParticles` +
`ShieldFlareRenderer` offscreen at the REAL hit data with a REAL graphics device,
framing the struck ship in chase view, and checks the placement math.

**MODE other** (an armed same-side shooter hits a shielded enemy target — the
observer watches another ship get hit):
```
MODE other: drained 8 ABSORBED + 0 hull hits (absorbed all correct) guard=12000
PROOF 2 (placement): 4 flares; all on the shield toward their hit at native size — PASS
wrote .verify_p463b/shieldflare_MODE_other_real_birth_age0.png
wrote .verify_p463b/shieldflare_MODE_other_real_fade_age039.png
wrote .verify_p463b/shieldflare_MODE_other_DIRECTION_FAN_synthetic.png
```

**MODE my** (an enemy shooter hits the OBSERVER'S OWN ship — its own shield
absorbs the rounds, so `TargetId == MyShipId`; the flare on our own ship in
chase view):
```
MODE my: viewer MyShipId=8099, shielded victim ship 8099 'Assault Carrier Drone' radius 165
MODE my: drained 8 ABSORBED + 0 hull hits (absorbed all correct) guard=12000
PROOF 2 (placement): 4 flares; all on the shield toward their hit at native size — PASS
wrote .verify_p463b/shieldflare_MODE_my_real_birth_age0.png
wrote .verify_p463b/shieldflare_MODE_my_real_fade_age039.png
wrote .verify_p463b/shieldflare_MODE_my_DIRECTION_FAN_synthetic.png
```

Vision-checking the PNGs: the `real_birth` frames show a bright ice-blue lens
(white-hot core, blue halo) sitting on the front of a wireframe shield bubble at
the hit point; the `DIRECTION_FAN` frame (labelled synthetic, eight distinct hit
directions around the ship) shows ~7 distinct glows spread around the sphere —
confirming the flare lands on the shield toward ITS OWN hit direction. The
per-ship cap is also demonstrated live: 8 absorbed hits on one ship collapse to
the newest 4 flares, exactly as native caps at 4 per ship.

### 4.3 Build & test

- `/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors** (full solution,
  incl. the new `tools/shieldflare-proof` project; added to `AllegianceCore.slnx`
  and the Server's `InternalsVisibleTo` list).
- `ShieldFlareParticlesTests` → 9/9 in isolation.
- Full `dotnet test` ×3 (three consecutive runs of the whole solution):

| Suite                          | Run 1        | Run 2        | Run 3        |
|--------------------------------|--------------|--------------|--------------|
| Lobby.Tests                    | 12/12 ✅     | 12/12 ✅     | 12/12 ✅     |
| **ClientCore.MonoGame.Tests**  | **960/960 ✅**| **960/960 ✅**| **960/960 ✅**|
| ClientCore.Tests               | 331✅/2❌     | 329✅/4❌     | 331✅/2❌     |
| Core.Tests                     | 3557✅/13❌   | 3561✅/9❌    | 3562✅/8❌    |

The MonoGame suite — which contains the 9 new ShieldFlare tests — is **clean
960/960 in all three runs**. The only failures are the repo's **documented
pre-existing live-wire timing flakes** in the `Core`/`ClientCore` suites
(`ConquestTerritoryLiveTests.*`, `GameOverPlayersWireTests.*`,
`LiveTurretGunnerBindingTests.*`, `LiveMoneyTests.*`, `LiveWinConditionTests.*`,
`JoinFlowLiveTests.*`, `ShipDeleteGoneLiveTests.*`, `AsteroidMinedLiveTests.*`
and friends): their failure counts rotate run to run (13→9→8 and 2→4→2) with no
deterministic regression, and **none references any P463b symbol** — the same
set P463/P462 logged. They pass green in isolation.

---

## 5. Out of scope (unchanged)

- Sounds (P463), collision/rock sounds (P463c), damage flash (P463).
- Any server/wire change — none needed; the P463 signal carries the absorbed
  flag, the outward hit normal, and the target radius/id.
- The P364 hull spark — untouched.
