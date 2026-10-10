# P593b — white "shard" clouds in combat: every missile type now renders its own native texture

Status: implemented & committed on `master` (not pushed); suite numbers appended after the
commit (see "2× suite").

## Summary

Domingo (2026-10-10, live 15v15, Iron Coalition (25) vs Ga'Taraan (3), server `6391b186`,
client `754ec51c`, in **both** `--fx volumetric` and `--fx displaced`) again saw streams of
white faceted 3D solids — the same "white popcorn" defect class P593 closed for projectile
rounds. P593's startup audit covered only the **4** distinct 3D-model **projectile** rounds;
this task found and fixed the object type that escaped it: **missiles**.

**Identification: the white shards are missiles rendered lit-solid.** Of the **56 missile
types** in `PCore001.igc`, the **45** whose `ModelName` is not one of the three P287-imported
content assets (`mis05`/`mis400`/`artifact`) all fell to the port's single shared
`MissileRenderer` (P233), which **never decodes textures** — it drew every one as the same
`mis05.mdl` mesh, lit-solid, tinted the fixed light-grey `MissileDiffuseColor (0.55, 0.55,
0.58)`. At the framerates and distances of combat that is exactly "white faceted shards":
many small grey-white faceted solids, far more than the number of ships. The mission civs'
fighter missiles (MRM Seeker `mis06`, MRM Quickfire `mis08`, LRM Hunter `mis07`, …) are all
lit-solid types. This matches P593's own scope-3 note, which **explicitly flagged
`MissileRenderer` as the remaining solid-untextured path** ("a P233 design decision …
flagged, not changed").

Because `MissileRenderer` is lit-solid *by design* (it is not a "fallback"), it logs
nothing — which is why the client log printed no `rendering lit-solid` fallback lines.

## Native cause (cited)

Native loads every missile with its **own real mesh and texture**:

- `CmissileIGC::Initialize` → `Load(0, dataMissileType->modelName, dataMissileType->textureName,
  dataMissileType->iconName, c_mtDamagable|…)` for model missiles (`src/Igc/missileigc.cpp:49-51`),
  and `LoadDecal(…)` only for missiles whose `modelName` is not alpha (`missileigc.cpp:60-68`).
- The texture is the model's own imported bitmap (`m_pgeoMesh->FindTexture()`,
  `src/client/thinggeo.cpp:1038-1043`; `<name>bmp` convention, `src/client/xfile.cpp:577`).

The port's `MissileRenderer` (P233) predates texture decoding and rendered the single shared
`mis05` mesh lit-solid — so **every** non-content missile rendered untextured. That is the
root cause, and it is the P593 defect class (a 3D model whose native texture is not applied →
lit-solid white) applied to missiles.

## The fix — `NativeMissileRenderer` (the P593 way)

New `src/Allegiance.ClientCore.Game/Rendering/NativeMissileRenderer.cs` draws every missile
with its OWN real parsed native `.mdl` mesh **and** its native texture, exactly like native
`missileigc.cpp:49-51`:

- **Per-type model:** the missile type's real `ModelName` (`MissileModelSource.ResolveModelName`)
  is parsed at runtime (the P544/P593 runtime-native-`.mdl` convention) and cached.
- **Native texture:** resolved via `NativeShipResolver.ResolveTextureStem` with the type's real
  `textureName` override (native passes it explicitly) then the model's own imported namespace —
  the exact P593/P586b rule (`thinggeo.cpp:1038-1043`/`xfile.cpp:577`), never a `<name>bmp` guess.
  Case-insensitive on-disk index (the P586b lesson) so a capitalised unit still resolves.
- **Orientation:** every missile `.mdl` is authored nose-forward along model +Z (measured across
  all 20 distinct missile models) — the one exception, `mis404` (Kinetic Torpedo), is nose-along-Y
  and gets a per-model prerotation so it too flies nose-first.
- **Winding:** double-sided (CullMode.None, the `ProjectileModelRenderer` convention) — the
  various missile `.mdl`s wind inconsistently and a small model rendering both faces guarantees
  no interior hole for any winding.
- **Lighting:** sector-lit (P530) like the rest of the scene.
- Falls back to the legacy renderer only if the `.mdl` truly cannot be parsed (never an
  invisible missile).

`Game1.Rendering.cs` missile dispatch now routes every non-content missile through
`NativeMissileRenderer` (per-type model + texture); the legacy `MissileRenderer` remains only as
the last-resort safety net and for the inspector.

## Widened audit (Scope 4)

New `src/…/Rendering/RenderModelAudit.cs` resolves mesh + native texture for **every 3D model
any renderer can draw** — projectiles, missiles and probes across the whole core, plus the
mine/chaff sprites — logging each lit-solid fallback by model name. Wired into client startup
alongside P586b's treasure `SelfCheck`. On the real 221 core:

```
[monogame] P593  projectile-model audit:   4 model(s), 0 lit-solid fallback(s)
[monogame] P593b missile-model audit:     20 model(s), 0 lit-solid fallback(s)
[monogame] P593b render-model audit: 0-lit-solid guarantee -> 0 model fallback(s), 0 treasure fallback(s)
```

(20 distinct missile model names resolve; every one gets a real native texture. 0 lit-solid
fallbacks over the whole core.)

## Tests (failing-first)

`tests/Allegiance.ClientCore.MonoGame.Tests/MissileModelTextureTests.cs` — 5 tests:

- `Mis06_seeker_texture_resolves_from_its_own_imported_namespace` → `mis06bmp` (MRM Seeker)
- `Mis08_quickfire_texture_resolves_from_its_own_imported_namespace` → `mis08bmp` (the P593b repro family)
- `Mis404_kinetic_torpedo_texture_resolves_and_is_y_forward` → `mis404bmp` + nose-axis check
- `Every_distinct_missile_model_resolves_mesh_and_native_texture` → 0 fallbacks across all 56 types
- `Widened_render_model_audit_over_whole_core_is_zero` → `RenderModelAudit.AuditCore == 0`

**Failing-first (demonstrated):** with the new `NativeMissileRenderer`/`RenderModelAudit`
absent (today's HEAD `1146fe92`), the test project fails to build (`NativeMissileRenderer does
not exist …`); with the fix all 5 pass.

## Real-window proof (Iron Coalition vs Ga'Taraan)

Local server on this workstation (`m2xspiderw.igc`, `civIds [25, 3]`), lit-solid missile
launcher ("MRM Quickfire 1" → `mis08` / the observer's real Seeker `mis06`), real 2560×1440
backbuffer frames via `ALLEGIANCE_AUTOSHOT`/`--autoshots`. Assets in
`P593b_WHITE_SHARDS_assets/`.

- **Before** (`p593b_before_v6_t039_white_shards.png`, zoom `p593b_before_zoom_lowerleft.png`):
  a close-range missile exchange around the observer shows the classic **white faceted
  solids** — the lit-solid missiles (the vision read: "small, white/light-grey, faceted 3D
  shapes… four distinct shapes" trailing the ship).
- **After** (fixed build): real-window frames from the same fight
  (`p593b_after_v6_*`, `p593b_after_v9_*`, `p593b_after_v10_t049_close.png`,
  `p593b_after_v11_t022_injected_fire.png`, `p593b_after_v12_t030_cockpit.png` — the last two
  caught the observer actually launching a sustained volley via injected fire input).

**Plain statement of what I observed in my own frames:** in none of the after frames is there
any white faceted-shard cluster — the bright-white lit-solid signature is gone (in the before
frames it was unmistakable; in every after frame the objects in the scene are the ship,
stations/planets, the aleph funnel, and the combat itself, with no bright-white faceted
chunks). I could not, however, exhibit a single missile at large readable size in a still: an
Allegiance missile mesh is only ~5 units and combat occurs at 260–1000+ units, so a missile
occupies only a few pixels and — now that it is textured (painted, lower-contrast) rather than
bright-white — it does not stand out from stars in a still. That low-salience is itself the
fix's effect: the bright-white fallback that made shards conspicuous is gone.

The **objective, authoritative** confirmation that every missile now renders textured is the
real client's startup audit on the fixed build (printed every run, reproduced verbatim):

```
[monogame] P593  projectile-model audit:   4 model(s), 0 lit-solid fallback(s)
[monogame] P593b missile-model audit:     20 model(s), 0 lit-solid fallback(s)
[monogame] P593b render-model audit: 0-lit-solid guarantee -> 0 model fallback(s), 0 treasure fallback(s)
```

plus **zero** `missile model '…' unavailable` / `… lit-solid fallback` lines in any after run
(the pre-fix code path rendered 45/56 missile types lit-solid by design and logged nothing; the
new path logs a fallback if a model cannot be parsed or textured, and none ever fires on the
real core). Combined with the 5 failing-first tests (below), the white-shard missile class is
closed at the root.

## Files changed

- `src/Allegiance.ClientCore.Game/Rendering/NativeMissileRenderer.cs` (new)
- `src/Allegiance.ClientCore.Game/Rendering/RenderModelAudit.cs` (new)
- `src/Allegiance.ClientCore.Game/Rendering/MissileModelSource.cs` (ResolveModelName/ResolveTextureName)
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` (missile dispatch)
- `src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs` (construct + audits)
- `src/Allegiance.ClientCore.Game/Game1.cs` (field + dispose)
- `tests/Allegiance.ClientCore.MonoGame.Tests/MissileModelTextureTests.cs` (new)
- `scripts/repro_p593b*.sh` (reproduction/verification harness incl. the injected-fire v11/v12)

Whole-solution build: **0 warnings, 0 errors**.

## 2× suite

Two consecutive `scripts/run-tests.sh` runs on the final code (run A = run 1, run B = run 2),
each recorded as it happened. Per-assembly: Lobby / ClientCore.MonoGame / ClientCore / Core.
The assembly holding this task's changes (**ClientCore.MonoGame**, incl. the 5 new P593b tests)
is **1713/1713 clean in both runs**. Every failure below is a documented live-wire / load flake
in `docs/TEST_FLAKES.md`; none touches rendering / missile / projectile / static-core code.

| run | Lobby | MonoGame | ClientCore | Core | failing tests (all documented flakes) |
|---|---|---|---|---|---|
| 1 (A) | 12/12 | 1713/1713 | 578/579 | 3868/3871 | ClientCore: `ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector_WatcherLatchesGone_OwnPeerNotHidden` (FLAKES:114); Core: `GameOverPlayersWireTests.Win_by_elimination…` + `Conquest_win_in_one_mission…` (FLAKES:30), `CargoBayReloadTests.SwapPart_out_of_range_negative_mount…` (FLAKES:53/238) |
| 2 (B) | 12/12 | 1713/1713 | 579/579 | 3867/3871 | Core: `AddPartTests.Part_pickup_decline…` + `Second_part_touch_while_offer_pending…` (FLAKES:247/331), `GameOverPlayersWireTests.Win_by_elimination…` (FLAKES:30), `LiveWinConditionTests.Board_refused_once_mission_over` (FLAKES:34/440) |

Net: **0 new / non-flaky failures across both runs**; the assembly with this task's tests is
green 2/2.
