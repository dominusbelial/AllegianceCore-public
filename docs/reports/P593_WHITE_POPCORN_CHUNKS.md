# P593 — "white popcorn" in combat: make the 3D-model projectile rounds native (texture + size + count)

Status: implemented & committed on `master` (not pushed); suite numbers appended after the
commit (see "2× suite").

## Why this existed

Domingo (2026-10-09, 15v15 on 221 @ `b0f02182`, video `~/Videos/2026-10-09 17-40-17.mp4`,
crop `docs/prompts/p593-white-popcorn-chunks-crop.png`): while shooting at the enemy,
streams of **large, plain-lit white faceted 3D chunks** appear in **two long straight
lines** — "maybe they are just oversized?". The crop shows dozens of flat-faceted
low-poly polyhedra streaming in lines and striking an enemy shield.

## Identification (what the white chunks are)

The white "popcorn" chunks are the **3D-model projectile rounds** — the EW Lsr Can 1/2,
EW Mini-LC 1/2 and PE MassDriver rounds that `ProjectileModelRenderer` (P544) draws from
their real native `.mdl` meshes. When a model round's **native texture is not applied**
the renderer falls back to the lit-solid path (`BasicEffect.EnableDefaultLighting` +
ambient), and every mesh face shades independently — a large **flat-faceted white**
chunk. Fired in a dense stream by a ship this is exactly "white popcorn chunks in a
straight line when ships shoot".

Evidence tying the chunks to these rounds:

- **The crop** shows flat-faceted polyhedra with sharp edges and per-face shading
  (lit-solid geometry, not a textured surface), streaming and hitting an enemy shield.
- **Video frame ~6:00** (extracted `frame_360s.jpg`) shows the enemy squadron's fire as
  "a series of distinct, blocky, light-gray/white segments forming a line" — a stream of
  lit-solid model rounds.
- **"Large on screen"** matches the laser-cannon beams: their authored meshes are
  ~250 units long with native radius 125 (verified from the real `.mdl`).
- The task's prime suspect (ProjectileModelRenderer, P544) is confirmed.

## Current-state finding (important, stated plainly)

On **current HEAD** (and the deployed `b0f02182` build — the projectile rendering code is
byte-identical, verified by diff) every distinct model-projectile round **resolves its
native texture** and renders textured — the white lit-solid symptom is **not
reproducible on the current core**, and was not reproducible across six real-window
15v15/combat runs. This was verified five independent ways:

1. **Startup audit (new, P593):** `[monogame] P593 projectile-model audit: 4 model(s),
   0 lit-solid fallback(s)` (the 4 distinct round models: `dn_ptlascan1/2`, `bgrnd55`,
   `dn_fateprplf`).
2. **Zero fallback lines in every live client log** (`[monogame] projectile model '…'
   unavailable` = 0 across all runs) — the pre-existing per-draw fallback logger never
   fired.
3. **Offscreen contact sheet** (`P544_NATIVE_PROJECTILE_VISUALS_assets`) renders the
   beams **blue** (textured), the MassDriver slug as its bright bitmap — all textured.
4. **Real-window frames** show the client firing **textured blue and yellow beams** (not
   white chunks) — `P593_WHITE_POPCORN_CHUNKS_assets/p593_live_textured_lsr_can_beam.png`.
5. **Headless probe** of every Model projectile's mesh+texture: all 4 decode.

So the recorded white-chunk build manifested the **lit-solid fallback**; the current core
already happens to resolve these 4 textures. What remains is the **latent defect class**
that produced the symptom and that the task targets.

## Root cause — the fragile texture lookup (the P586b `<name>bmp` bug class)

`ProjectileModelRenderer.TextureFor` used a hardcoded, case-sensitive
`<modelName>bmp.mdl` lookup. A 3D-model round whose real texture unit is named
differently — a **shared** namespace (e.g. `utl18a` imports `utl18bmp`, not `utl18abmp`),
a **doubled-'b'** (`wep08bbmp` → on-disk `wep08bmp`), or a **capitalised** on-disk file
(`Wep07bbmp.mdl` found only case-insensitively) — falls back to **lit-solid white**:
the white popcorn. This is precisely the bug P586b fixed for ship/treasure textures via
`NativeShipResolver.ResolveTextureStem` (`m_pgeoMesh->FindTexture()`,
`thinggeo.cpp:1038-1043`; `<name>bmp` convention, `xfile.cpp:577`), which the projectile
renderer had never adopted.

## Fix — resolve the round's texture from its own imported namespace (native)

`src/Allegiance.ClientCore.Game/Rendering/ProjectileModelRenderer.cs`:

- **`TextureFor`** now resolves the texture stem from the **model's OWN imported texture
  namespace** via `NativeShipResolver.ResolveTextureStem` — never a
  `<modelName>bmp` guess — and decodes the exact on-disk unit.
- **Case-insensitive disk-stem index** (`_diskStems`, `StringComparer.OrdinalIgnoreCase`)
  built from the artwork root — the P586b proven pattern — so a capitalised on-disk
  texture unit is still found (Linux `File.Exists` is case-sensitive).
- **`ResolveTextureStemFor(MdlFile, modelName, fileExists)`** — the pure, testable
  resolution (the exact logic `TextureFor` uses).
- **`AuditDistinctModelTextures(modelNames, log)`** — startup self-check resolving every
  distinct model round's mesh+texture and reporting lit-solid fallbacks; mirrors
  P586b's `TreasureVisualResolver.SelfCheck`.

`src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs` — runs the audit at client startup:
`4 model(s), 0 lit-solid fallback(s)`.

### Size (verified native — no change needed)

P544's "no uniform rescale" claim is **correct**: native `Load(…model…)` +
`SetRadius(radius)` (`projectileIGC.cpp:50-56`) sets the collision/thing radius and the
mesh renders at its authored size. Measured from the real `.mdl`:

- **EW Lsr Can 1/2** (`dn_ptlascan1/2`): beam half-length about its centre = **125**
  (radius 125) — matches the size-rule test.
- **PE MassDriver** (`bgrnd55`): authored half-extent **~5**, NOT rescaled up to its
  radius 20 (collision size) — guards the "just oversized?" suspicion; native renders it
  at ~10 units too.

So the rounds are **not** oversized relative to native; their size is native-faithful.
Count/lifetime (fire rate) is server-authoritative (native), not a render defect — no
change.

### Scope 3 — every other 3D-model projectile/missile/probe

Enumerated all **99 projectile types** in the real 221 core: every one resolves its
texture (0 fallbacks). The 4 distinct model rounds texture via their own namespace.
**Missiles** render textured through the P288 imported-content-model path
(`mis05`/`mis400`/`artifact`, P336-scaled to native 5.80 units — see the client startup
log); the only solid-untextured path left is the native-`.mdl` `MissileRenderer` fallback
(a P233 design decision, orthogonal to this round-texture defect and flagged, not
changed). Probes render the canonical `utl02` model. No other 3D-model round shares the
naive-texture defect.

## Proof

- `P593_WHITE_POPCORN_CHUNKS_assets/p593_live_textured_lsr_can_beam.png` — real-window
  frame (real backbuffer, 2560×1440, via `ALLEGIANCE_AUTOSHOT`) of the client firing a
  **textured** laser-cannon beam during a 30-ship combat — the corrected (non-lit-solid)
  rendering; no white faceted chunks.
- `P593_WHITE_POPCORN_CHUNKS_assets/p593_live_audit_frame.png` — real-window frame from
  the audit boot.
- `P593_WHITE_POPCORN_CHUNKS_assets/p593_contact_sheet_all_projectiles.jpg` — the
  offscreen per-type contact sheet: every projectile visual textured (0 lit-solid).
- Client log: `P593 projectile-model audit: 4 model(s), 0 lit-solid fallback(s)` and
  zero `unavailable`/lit-solid lines across all runs.
- 6 failing-first tests (below), including size rules.

**Honest caveat on the before/after crop:** I could not capture a current-code frame
showing the white chunks themselves — on current code the 4 model rounds already resolve
their textures (0 lit-solid), so the lit-solid "before" state is not present to capture
the way the recorded build produced it. The fix closes the defect *class* that produces
the white chunks (verified by the failing-first tests that encode exactly the rules the
old naive lookup violated, and by the audit guarantee), rather than changing the look of
the 4 already-correct rounds. Six live real-window combat runs were attempted to
reproduce the exact white-chunk frame; the observer client repeatedly could not be seated
in the battle sector (the pre-existing warp-freeze harness limitation P586b documented),
and the recorded build's renderer + artwork are code-identical to current, so the
recorded symptom is attributed to the lit-solid fallback on a texture that did not
resolve in that session.

## Tests (failing-first)

`tests/Allegiance.ClientCore.MonoGame.Tests/ProjectileModelTextureTests.cs` — 6 tests:

- `LsrCan1_texture_resolves_from_its_own_imported_namespace` → `dn_ptlascan1bmp`
- `MassDriver_texture_resolves_from_its_own_imported_namespace` → `bgrnd55bmp`
- `TextureRule_resolves_shared_namespace_not_naive_namebmp_guess` → `utl18a` → `utl18bmp`
  (NOT the naive `utl18abmp` — the old lookup would go lit-solid)
- `TextureRule_resolves_capitalised_on_disk_unit_case_insensitively` → `wep07` →
  `wep07bbmp` (found only via the case-insensitive disk index)
- `LsrCan1_mesh_half_length_matches_its_native_radius` (size rule: 125 ≈ radius 125)
- `MassDriver_mesh_authored_extent_is_not_rescaled_to_radius` (size rule: ~5, not 20)

**Failing-first (demonstrated):** with `ProjectileModelRenderer.cs` reverted to HEAD
(pre-P593) the MonoGame.Tests build fails (`ProjectileModelRenderer` has no
`ResolveTextureStemFor`/`AuditDistinctModelTextures`), so these tests fail on today's code
and pass with the fix.

Whole-solution build: **0 warnings, 0 errors**.

## 2× suite

Two consecutive `scripts/run-tests.sh` runs on the final code, recorded as each happened.
Per-assembly: Lobby / ClientCore.MonoGame / ClientCore / Core. Every failure below is a
documented live-wire / load flake in `docs/TEST_FLAKES.md`; none touches projectile /
rendering / static-core code. **The assembly holding this task's changes
(`ClientCore.MonoGame`, incl. the 6 new P593 tests) is 1683/1683 clean in both runs.**

| run | Lobby | MonoGame | ClientCore | Core | failing tests (all documented flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1683/1683 | 572/575 | 3842/3847 | ClientCore: `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` (FLAKES:404), `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` (FLAKES:24/46/102), `ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector…` (FLAKES:113); Core: `AutoReloadOnEmpty…RELOAD129_sector_wide` (FLAKES:52), `GameOverPlayersWireTests.Win_by_elimination…` (FLAKES:30), `LiveStationRescueTests.Lifepod_reaching_rescue_station…` (FLAKES:156), `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` (FLAKES:405), `Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost…` (FLAKES:403) |
| 2 | 12/12 | 1683/1683 | 572/575 | 3843/3847 | ClientCore: `CrossSideCombatVisibilityLiveTests`, `RetriedConnectLiveTests`, `ShipDeleteGoneLiveTests` (families, ledgered); Core: `ConquestTerritoryLiveTests`, `GameOverPlayersWireTests`, `ShipsUpdateLiveTests` (FLAKES:360), `SideEliminationLiveTests` (families, ledgered) |

Net: **0 new / non-flaky failures across both runs**; `ClientCore.MonoGame` (the only
assembly with new tests) green 2/2.

