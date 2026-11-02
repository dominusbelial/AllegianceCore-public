# P915 — own small-hull smoke, black hull panels, afterburner gating

The changes are left in the working tree. No commits or pushes were made.
Production changes are confined to the game client; server, simulation, wire,
artwork and camera settings are unchanged.

## Result and cause

The attached black shape reproduced as **the pod mesh sampling the wrong rows
of its texture atlas**, rather than an absent ambient light. This was established
with separate real Game1 window captures, not inferred solely from the recording:

1. The shape survives disabling soft particles. The ChaseOccluder only
   multiplies puff alpha (`Rendering/EnhancedSmokeSizing.cs`, `AlphaFactor`);
   neither pass draws the attached hull polygon.
2. Replacing the hull texture with white, with hull particles suppressed, makes
   those same faces bright white/blue under the existing lighting. Mesh normals
   are unit length. The existing P800 ship ambient floor is already
   `(0.50, 0.52, 0.56)`; raising ambient would still multiply black atlas samples
   by the new light value.
3. Keeping the geometry, normals and lighting, and sampling texture rows in file
   order, restores the authored panels/vents/lamps. This also reproduced on the
   Bios `utl100` pod during investigation. The brown, blocky `utl203` pod is the
   closest model/atlas match to the recording and is used in the saved final
   comparisons. The video alone does not supply its numerical hull ID.

The source chain is:

- `src/Allegiance.Core/Data/MdlTextureDecoder.cs:106-135`: default vertical row
  reversal. Its justification cites native **image/decal** drawing.
- `src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs:155-162`: uploads
  that reversed texture; the mesh merge preserves the file's original UVs.
- Native `engine/geometry.cpp:311-314` draws the mesh directly;
  `engine/device3d.cpp:2245-2246` copies its U/V unchanged;
  `engine/modeler.cpp:723-733` supplies the original rows and pitch. The V
  inversion in `engine/context.cpp:659-660` belongs to `DrawImage3D`'s generated
  image quad, not these raw mesh UVs.

The correction is deliberately scoped to **own ship, external view, real hull
length below 6 units**. `NativeShipResolver.WithReversedTextureV` compensates
for the already reversed texture using `v = 1-v`; it leaves positions, normals,
indices and material scopes intact. `NativeShipRenderer` caches the resulting
geometry variant and disposes it with the original. Native, classic HD and PBR
material paths use the same corrected UVs. The original depth geometry has the
same positions. Normal fighters, remote hulls, stations, preview panes, lighting
constants and shaders retain their current rendering.

The fighter comparison caught an integration mistake during development: the
own ship is not populated in the generic remote-radius dictionary, so that
lookup's fallback would have included a fighter. The final binding uses
`_myRenderRadius`, the real own hull radius, at
`Game1.Rendering.cs:747-748`; the final fighter comparisons verify the scope.
A complete PCore001 size audit confirms this UV scope includes only the twelve
lifepod/observer entries, and excludes every non-pod hull, including the
10–11-unit scouts/interceptors/stealth fighters. The real-core regression test
asserts that exclusion across the full hull table. The smoke-size rule still
uses its independent 12/24-unit limits. The broader mesh/texture origin
convention remains deferred rather than
silently changing every model's appearance in this task.

## Smoke rule and native gates

`Rendering/ShipEffectRules.cs:13-17` applies:

```text
own external-view size scale = min(1, real hull length / 12, eye-to-hull distance / 24)
other ships / cockpit / unknown dimensions = 1
```

`Game1.Rendering.cs:1527-1528` uses the emitted hull's real radius and the actual
frame camera eye. `DamageDebrisParticles.Emit` scales the native random
1.5–2.5-unit effect size before both sizing and the ejection/start variance
calculations. Lifetime, random draw count, inherited ship velocity, vent count,
thresholds and pool ordering are unchanged. Applying the scale at emission also
covers both Native and Enhanced smoke paths; it does not resize remote smoke.

A length-4 pod at the proof's chase distance receives scale **1/3**. The
length-13 fighter at its normal approximately 24.7-unit chase distance and a
length-200 capital at approximately 380 units both receive **1**. This rule has
no pod ID or capability special case.

Verified native behavior: `effect/thinggeo.cpp:701,739,745-751,1358-1395` provides
the damage-frame gate, fixed particle size, ejection/lifetime and vent counts;
`WinTrek/WinTrek.cpp:6612-6621` hides own damage in the cockpit. The port's
`DamageDebrisTracker` and cockpit emitter gate remain intact. As before, the
actual vent count uses native float truncation; at 45% armor it is five.

## Afterburner, nozzle glow, jiggle and autopilot

Native `WinTrek/WinTrek.cpp:7999-8006` reads the mounted afterburner and forces
thrust power to zero when it is absent. `effect/thinggeo.cpp:760` gates the
engine trail on positive power. The own ship now uses its granted mounted
loadout through `OwnShipLoadoutHasAfterburner`; a lifepod capability guard also
rejects a stale pre-ejection loadout. Removing the part immediately clears the
client's residual power ramp, so ejection cannot keep creating trail particles.
Already emitted particles keep their ordinary lifetime.

Remote loadouts are not available in the client state: `ShipEntity.Loadout` is
populated by the owner-only `BUY_LOADOUT_ACK`. Remote lifepod hulls now emit no
engine trail and cannot force nozzle glow with the boost bit. Other remote
hulls retain the existing button-based approximation. A non-pod remote ship
that has no mounted afterburner can therefore still falsely emit a trail;
this remains **partial**, explicitly tracked in
`docs/parity/p915-client-effects.csv`. Even for mounted parts, the existing
button/ramp approximation does not reproduce native GetPower's fuel/mounting
response exactly. No new wire data was added.

`UpdateNozzleGlow` had the same missing part gate and is fixed with the same
availability boolean: ordinary throttle glow remains, while the boost override
requires the part (`effect/thinggeo.cpp:442-455`). Camera jiggle already gates on
mounted-part presence (`Game1.Rendering.cs:234-247`, native
`WinTrek/WinTrek.cpp:374-399`); it benefits from the stale-lifepod guard but its
shake law is unchanged.

Pod autopilot does **not** set the afterburner button: accepted autopilot
controls replace the outgoing mask at `Game1.FlightInput.cs:2441`; that mask is
reset at `:3199` and its only nonzero assignment at `:3513` calls
`DockApproach.MovementButtons` (six translational RCS bits, no afterburner,
`src/Allegiance.Core/Sim/DockApproach.cs:373-394`). Holding Shift/booster manually
can set the bit. The proof intentionally holds it to exercise the visual gate.
The HUD's button-driven AFTERBURNER label is unchanged; it can still say ACTIVE
in that deliberate no-part fixture. The training HUD is outside this task.

## Real-window evidence

All listed screenshots were opened and visually inspected. They are actual
1280×720 Game1 backbuffer captures, losslessly stored as WebP, with Enhanced
VFX, **Effects Medium, HD textures on, experimental PBR off**, except the
explicit PBR check. The real installed schema-3 HD pack was loaded and the
`utl203bmp`/fighter materials uploaded, as recorded in the raw logs.

The harness performs a real loopback logon/launch, pauses server ticking, then
feeds a stable client telemetry fixture through the production gather/draw
code: 45% hull, idle throttle, afterburner button held, no mounted afterburner on
the pod and a mounted afterburner on the fighter. This is a rendering proof,
not a claim that a server naturally produced those telemetry values. Both
versions use the same fixtures and framing. Smoke/engine particle positions
and incidental background objects are not pixel-identical between processes.

| Scene | Before | After | Observed |
|---|---|---|---|
| Pod, Modern | [before](P915_assets/before-pod-modern.webp) | [after](P915_assets/after-pod-modern.webp) | Large smoke halo and false yellow booster glare become a small plume with readable hull details and no booster trail. Armor remains 45%. |
| Pod, Classic | [before](P915_assets/before-pod-classic.webp) | [after](P915_assets/after-pod-classic.webp) | Same presentation change beneath the Classic artwork HUD. |
| Fighter, Modern | [before](P915_assets/before-fighter-modern.webp) | [after](P915_assets/after-fighter-modern.webp) | Same hull texture/lighting and wing silhouette; real mounted-booster trail remains. |
| Fighter, Classic | [before](P915_assets/before-fighter-classic.webp) | [after](P915_assets/after-fighter-classic.webp) | Same normal-hull result with Classic gauges. |

Diagnostics: [soft particles off](P915_assets/diagnostic-no-soft-pod-modern.webp)
keeps the dark attached panels; [white hull](P915_assets/diagnostic-white-pod-modern.webp)
lights the complete same silhouette; [file-order sampling](P915_assets/diagnostic-unflip-pod-modern.webp)
restores authored texture detail without changing lighting. The
[PBR check](P915_assets/after-pbr-pod-modern.webp) also keeps the corrected hull
and no false engine trail; its different PBR shading is expected.

The proof uses the existing player zoom override at distance 13 for the pod.
An additional existing issue was found: the unzoomed length-4 pod's chase eye
is about 7.6 units away, inside the fixed 10-unit near plane
(`Rendering/ChaseCamera.cs:213-246`, `Game1.Lifecycle.cs:254`), which can clip
away its hull. This task does not change chase distance or near-plane policy.
The same fixed zoom is used before and after, outside that clipping issue.
No cause was established for black shapes inside fireballs; explosions, the
yellow beam, other ships' smoke and training presentation were not changed.

Reproduction: `tools/p915-smoke-proof/README.md`. Isolated copies of the build
outputs prevent builds from overwriting DLLs loaded by a running window. New
media remains subject to the required human review before a later push.

## Validation

- `dotnet build AllegianceCore.slnx`: final build **0 warnings, 0 errors**.
- `tools/build-tools.sh`: all **109** tools built, **0 warnings/errors**.
- **35 new cases passed × 3 alone**. Including the affected damage-particle/
  tracker, jiggle and native resolver tests: **95 passed × 3**, no skips/failures. The corpus test also
  checks that no ordinary PCore001 hull enters the UV correction.
- Exactly **one** Fast sweep: **12,950 executions**, **12,935 passed / 12 failed /
  3 skipped**, **542.831 s**; all **nine infrastructure checks passed**. All 35
  P915 cases passed. Six executions exceeded the ten-second limit.
- Every failed/slow method or theory family was isolated three times. Results
  and known failure signatures are recorded in `docs/TEST_FLAKES.md`; the Fast
  gate remains **FAIL**, not represented as clean. The final radius binding and
  size-scope audit were checked with rebuilt solution/tools and focused runs;
  the whole sweep was not repeated, as the brief requires one run.
- Glossary links, registered parity scorecards, P915 sidecar LF/CSV shape and
  `git diff --check` passed. There is no server/Core production change requiring
  AOT, no training behavior change, and no new service/port/environment setting.

The compact [Fast summary](P915_assets/fast-summary.json),
[screenshot hashes](P915_assets/screenshots.json),
[small-hull census](P915_assets/small-hulls.json) and
[task validation summary](P915_assets/validation.json) accompany the report.

Raw local evidence is retained under `.scratch/p915/evidence/` because this sandbox
cannot write the lane's usual external evidence directory. Temporary build and
window copies are removed after validation; retained files are listed in the
validation summary. The checked-in screenshots/summary stay within the 2 MB budget.

## Native-intent footer

**Design:** visible armor damage produces persistent vents; cockpit damage is
hidden; only an actual mounted afterburner supplies booster thrust power.
Those gameplay gates stay native.

**Era constraint:** native uses fixed world-size, short-lived smoke and a cheap
bidirectional light rig. Enhanced translucent smoke plus a camera scaled down
to a four-unit hull makes the same fixed size much more obtrusive. The new size
rule is a client visual policy, not a new damage rule.

**Bug:** the port generalized a decal/image row inversion to mesh textures and
confused a held boost input with equipment presence. Texture sampling and part
gates address those defects; extra ambient light would mask the diagnosis and
would not recover black atlas samples.

**Modern better approach:** make texture-origin conventions explicit at the
material binding, validate all native mesh/HD atlas pairs against native
renders before a global migration, and use a projected smoke-size budget for
close own-ship views. Accurate remote afterburner power needs reliable equipment
knowledge; the current client cannot infer absence from a button bit alone.
