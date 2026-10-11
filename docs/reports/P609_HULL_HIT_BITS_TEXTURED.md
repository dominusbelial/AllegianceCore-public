# P609 — Texture the hull-hit debris bits with native `utl10bmp`; close the untextured-model audit blind spot

## Summary

The P545 hull-hit debris bits — the native `ThingGeo::AddHullHit` two-part effect
(`effect/thinggeo.cpp:1324-1340`) — were ported drawing the `utl10` debris bits
with `TextureEnabled = false`, so the real 3D chunks (which scale to `m_radius/2`
of the *hit object* — large on asteroid hits) rendered as scene-lit **solid white**
blocks. Domingo's "large white chunks flying out of somewhere" on the 195v195
battle was exactly these untextured bits.

Native `utl10.mdl` is a `TextureGeo` importing `utl10bmp` (its texture namespace,
`xfile.cpp:577`). The bits are now drawn **textured with that native `utl10bmp`**
via the same `MdlTextureDecoder` pipeline as `NativeMissileRenderer` /
`NativeAsteroidRenderer`, keeping the native scene-lit look. The real-window proof
shows the fragments going from bright untextured white chunks to dark textured
grey/brown `utl10bmp` fragments.

## 1. Texture the bits

`Rendering/HullHitSparkRenderer.cs`:

- `LoadBitTexture` decodes `utl10bmp.mdl` (a binary RGB565 `.mdl`, verified 32×32)
  with `MdlTextureDecoder.Decode` and uploads it premultiplied for `BasicEffect` —
  the same pipeline the other native renderers use.
- The `_bitEffect` now has `TextureEnabled = (_bitTexture != null)` and binds the
  texture; the UVs come from the already-parsed `utl10.mdl` mesh texcoords.
- Lighting stays scene-lit (`EnableDefaultLighting`, `AmbientLightColor`), blend is
  unchanged (opaque lit geometry — native draws the bit mesh through the default
  material path, not a decal). If the texture is genuinely unavailable it is
  logged once and the bits keep the old lit-solid fallback (never invisible).
- A single shared headless rule — `HullHitSparkRenderer.BitsTextured(Func<string,bool>)`
  — is the source of truth for BOTH the renderer's `TextureEnabled` decision and
  the startup audit, so the deliberate-untextured blind spot cannot silently reopen.

## 2. Native numbers (verified, unchanged)

Re-read native and confirmed every P545 number already matches (no change needed):

| Quantity | Native | Port |
|---|---|---|
| bit size | `size = m_radius / 2` (`thinggeo.cpp:1328`) | `radius * 0.5f` (`HullHitSparkParticles.Emit`) |
| speed | `speed = 0.4f * size` (`thinggeo.cpp:1329`) | `SpeedFactor=0.4f` |
| spread | `spread = 0.25`, per-axis `normal + random(−0.25,0.25)` (`thinggeo.cpp:1330,1337-1340`) | `Spread=0.25f` |
| bits/hit | `for (index < 2)` (`thinggeo.cpp:1334`) | `BitsPerHit=2` |
| life | `g_bitsLifeTime = 2` (`efgeo.cpp:764`) | `LifetimeSeconds=2f` |

`m_radius` is the **hit object's** radius: `ThingGeo` sets it from the mesh radius
then the real world radius via `SetScale` (`thinggeo.cpp:1051-1053,1193`). The port
passes the same radius/2 for every struck object — ship `0.5·Length`
(`Model/Sector.cs:1806`), asteroid `asteroid.Radius` (`:2024`), station
`station.Radius` (`:1935`) — all flowing unchanged to the bits' `radius/2` over the
wire (`ProjectileHullHit.Radius`, `ServerHost.cs:256` "the struck target's collision
radius"). Already unit-tested by `HullHitSparkParticlesTests` (size, speed, spread,
2 bits, 2 s life, straight-line integration).

## 3. Close the audit blind spot

`Rendering/RenderModelAudit.cs` previously audited mesh+texture RESOLUTION for
projectiles/missiles/probes/mine/chaff — but the bits were untextured *by design*
(it never "fell back"), so P593b's lit-solid audit missed them. New
`RenderModelAudit.AuditBits` covers the hull-hit bit (`utl10` → `utl10bmp`) via the
**same** `HullHitSparkRenderer.BitsTextured` rule the renderer uses, plus a check
that the `utl10.mdl` mesh parses. The startup audit (`Game1.Lifecycle.cs:680`) and
the test suite now assert 0 untextured native models across the whole client.

## Tests

`tests/.../MissileModelTextureTests.cs` (new):

- `Hull_hit_bit_resolves_native_texture_and_renders_textured` — `utl10.mdl`'s
  imported namespace resolves to `utl10bmp` AND `BitsTextured` is true (the renderer
  applies it, not lit-solid).
- `Whole_client_render_model_audit_including_hull_hit_bit_is_zero` —
  `RenderModelAudit.AuditCore` (now including the bit) returns 0.

**Failing-first (demonstrated):** with the bits forced untextured
(`BitsTextured → false`), both new tests FAIL — the whole-client audit reports a
`P609 hull-hit bit 'utl10': LIT-SOLID` fallback and the renderer does not apply the
texture. With the fix they pass. `MissileModelTextureTests` passes 7/7.

## Suite (2 consecutive runs on the final code, P471 `run-tests.sh` assembly-by-assembly)

Whole-solution build (Release): **0 warnings, 0 errors** (both the solution build
and with the proof harness change).

| Assembly | Run 1 | Run 2 |
|---|---|---|
| Lobby | 12/12 | 12/12 |
| ClientCore.MonoGame | 1735/1738 (3 skipped — Meshy pack) | 1735/1738 (3 skipped) |
| ClientCore | 593/593 | 593/593 |
| Core | 3915/3918 | 3915/3918 |

Core failures are all documented live-wire/timing load-flakes (see
`docs/TEST_FLAKES.md`): run 1 — `LiveRelaunchTests.Carrier_without_launch_slots…`
(line 432), `LiveChatSectorWireTests.Individual_…` (line 216),
`GameOverPlayersWireTests.Win_by_elimination…` (line 432); run 2 —
`AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129…` (line 52, load-flake P472),
`GameOverPlayersWireTests.Win_by_elimination…` (line 432), and
`P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield_reaches_same_side_seated_peer`
— a live-wire minefield-export timing test that passes 2/2 in isolation (measured),
so it is a transient full-suite load flake, not a regression (the change is a
client renderer texture path; no server/wire logic touched). All failures are the
known live-network timing class the suite was reduced to 2 runs for (per Domingo,
2026-10-09). No failure is rendering-related.

## Real-window proof (before vs after)

Extended `tools/hullhit-proof` with a `--real-window [--window WxH]` mode that
renders the deterministic close-up (real in-process fight, real
`ProjectileHullHit(213)` wire hits at the real target) into the **presented
2560×1440 backbuffer** during the normal Draw/present cycle and captures it via
`GraphicsDevice.GetBackBufferData` (the exact path the client's F11 /
`ALLEGIANCE_AUTOSHOT` screenshot uses, `Game1.AutoProof.cs:425-445`) — a genuine
window frame, not an offscreen render target. Wire proof: **8 real
PROJECTILE_HULL_HIT(213)** events at the real target (worst impact offset 172.5 u
vs radius 165 u, all sector 1050, all target) — harness `PROOF 1 … PASS`.

Same deterministic scene, identical framing (camera 49.5 u from the impact, the
`utl10` chunk scaled to `radius/2` = 82.5 u — the size native makes on a big target):

- **BEFORE (pre-P609, untextured):** the two debris fragments are **bright
  light-grey/white** — measured average RGB **(171, 165, 151)**, 148 near-white
  pixels — the solid-white-chunk look. [`realwindow_BEFORE_untextured_whitechunk.png`]
- **AFTER (P609, `utl10bmp`):** the two fragments are **dark grey/brown with
  visible texture** — measured average RGB **(66, 64, 48)**, 4 near-white pixels —
  matching the native `utl10bmp` texture (vision: two angular fragments, grey/beige/
  brown mottled surface, light/dark shading, no solid white block).
  [`realwindow_AFTER_textured_utl10bmp.png`]

State plainly: **no solid-white chunks remain** — the real-window debris fragments
are now dark textured `utl10bmp` (the BEFORE fragment average is ~2.6× brighter and
whiter than the AFTER).

Proof PNGs: `docs/reports/P609_assets/realwindow_BEFORE_untextured_whitechunk.png`,
`realwindow_AFTER_textured_utl10bmp.png`, `realwindow_AFTER_textured_birth.png`,
`closeup_after_textured_drift.png`.

Asteroid vs ship: both use the identical bits renderer/texture, differing only in
`radius/2` size (asteroid hits are bigger — exactly the "large chunks" Domingo
flagged). The real-window harness produces deterministic projectile hits on a real
SHIP target (the reliable real-hit source); asteroid hits exercise the same code
path with `asteroid.Radius` (`Sector.cs:2024`), so the same textured-bits result
holds. A separate deterministic real-window asteroid-target capture is the one
piece not produced (the scenario tooling does not aim projectiles at asteroids).

## Files

- `src/Allegiance.ClientCore.Game/Rendering/HullHitSparkRenderer.cs` — bits textured
  with `utl10bmp`; `BitModelStem`/`BitTextureStem`/`BitsTextured` contract.
- `src/Allegiance.ClientCore.Game/Rendering/RenderModelAudit.cs` — `AuditBits` covers
  the hull-hit bit, closing the deliberate-untextured blind spot.
- `tests/.../MissileModelTextureTests.cs` — 2 failing-first P609 tests.
- `tools/hullhit-proof/Program.cs` — `--real-window` backbuffer-capture proof mode.
- `docs/reports/P609_assets/*.png` — real-window before/after frames.

Committed on `master` (not pushed).
