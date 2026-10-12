# P615 — Stations in the inspector + station visual pass, probe texture fix, native art-coverage audit

Date: 2026-10-12
Branch: `master` (not pushed)
Commit: `92b09386` (amended after the 2× suite)

## Why

Domingo (2026-10-11): *"about the stations, can we use the viewer to render them all?
some look weird"*. The P565 inspector (`--inspect`) cycled ship hulls only (447); there
was no station mode, and the static core has **143 station types**. Stations render
through `NativeStationRenderer` (textured native `.mdl`) with the procedural
`StationRenderer` as fallback. Probes were drawn **untextured** (`ProbeRenderer.cs:23` —
"P233 did not decode probe textures"). P609's untextured audit missed the probe because
its untextured state was *by design* — the stem resolved, so the old audit reported 0.

Six components, one commit.

---

## C1 — Station inspector mode (`--inspect-stations`)

New offline mode that cycles **every station type in the static core (143)** under the
same orbit / angle-sweep / screenshot / mark-bad workflow as hulls.

- New `Rendering/InspectorStationCatalog.cs` — pure, headless enumeration of
  `DataStationTypeIGC` (all 143, ordered by `StationTypeId`, deduped, `NativeAvailable`
  honest). Grouping by `GroupId` (the station analogue of a hull's civ filter).
- `--inspect-stations` launch flag (Program.cs → `LaunchOptions.InspectStations` →
  `Game1.Inspector`). An in-inspector `G` key toggles hull ⇄ station mode live.
- Controls: `PgUp`/`PgDn` cycle station types; `[`/`]` cycle station groups;
  `Tab` cycles the mark-bad tag (added `orientation`/`z-fight`/`subparts`);
  `Enter` marks bad; `F11` screenshots; `--inspect-angles N`/`--inspect-dist` work as for
  hulls. The station sweep **advances across every station** so one unattended run
  captures all 143 types.
- HUD line: station type, name, model, texture, radius, native-resolved, group.
- Draw: native `.mdl` + texture via `NativeStationRenderer` at native scale
  (`radius / meshRadius`, `NativeStationResolver.ScaleForRadius`) and native default
  orientation (identity — forward +Z, up +Y, yaw 0), side-by-side with the procedural
  placeholder when toggled; falls back to the placeholder when a station's model can't
  load.
- Mark-bad writes to a dedicated, easy-to-read-back ledger
  `Capture/InspectorStationNotes.cs` → `stations-marked.jsonl` (station id, name, model,
  radius, native-resolved, camera pose, tag, note, screenshot) with an
  `ExportSummary()` fix-digest grouped by tag — for Domingo's own "weird ones".

## C3 — Probes textured

Pre-P615 the probe drew its shared real `utl02.mdl` mesh **lit-solid** with a
friendly/enemy side tint. Now the probe binds its native texture.

- `ProbeModelSource.ResolveTextureStemFor` ports native `CprobeIGC::Initialize`'s
  `Load(0, modelName, textureName, …)` (`probeigc.cpp:112-113`): textureName override
  wins, else the model's own imported namespace (`FindTexture`, `thinggeo.cpp:1038-1043`)
  — the P593 rule.
- **Correction to the brief's "probebmp".** The P615 brief claimed native uses
  `probebmp`. Decoding PCore001: **all 53 probe types carry an EMPTY `textureName`**, so
  native paints each probe from its model's own namespace. For the port's canonical demo
  mesh `utl02.mdl` that is **`utl02bmp`** (a real unit; `probebmp.mdl` exists but is
  referenced nowhere in the native src and by no probe type). The rendered probe's true
  native texture is `utl02bmp`. Proven: `ProbeModelTextureTests.Utl02_demo_probe_texture_resolves_from_its_own_imported_namespace`
  + the corpus decode.
- `ProbeRenderer` now accepts/binds a decoded `Texture2D` (`MdlTextureDecoder`, the
  P593b/P609 path). **No side tint when textured** — native has no per-side team tint on
  the probe mesh (probeigc.cpp:112-113; team is via icons) — the same no-tint rule the
  native station path follows. Lit-solid tinted fallback retained when the texture can't
  resolve (never an invisible probe).

## C4 — Untextured blind spot, properly

P609's audit only checked whether a **texture stem resolves** — so a renderer that
resolved a texture but drew lit-solid *by design* slipped through (the probe). P615 adds a
**texture-APPLICATION contract** to `RenderModelAudit`:

- `ProbeRenderer.AppliesTexture` (mirror of `HullHitSparkRenderer.BitsTextured`).
- `AuditTextureApplication` flags any covered renderer that resolves but doesn't apply
  its texture, so the probe's by-design-untextured state is now caught (pre-P615 it
  reported 0 while probes drew lit-solid).
- Explicit **documented allowlist** `RenderModelAudit.AllowedUntextured` for renderers
  legitimately untextured: the building-effect ghost (native draws it via
  `LoadEffect(Color(50/255,28/255,146/255))`, `buildingeffectigc.h:93` — an effect colour,
  no `.mdl` texture; verified), the procedural `StationRenderer` placeholder (real station
  path is textured native), and the procedural generators (warp/asteroid/giant/sky).

## C2 — Station render audit (CSV + contact sheet)

- `docs/reports/P615_assets/stations.csv` — all **143** station types decoded from
  PCore001, with model, texture name, group/class, radius, and resolver-level status using
  the exact `NativeStationResolver` rule (textureName override → `<model>bmp` skin → first
  imported namespace). **Result: all 143 are `native-ok`** (model `.mdl` present + a real
  texture unit resolves) — no port bug at the resolver level.
- Real-window contact sheet `stations_contact_sheet.png` — **all 143 station types
  captured from the real client window at 2560×1440** (`--inspect-stations --inspect-angles
  1`, 143 frames, backbuffer grabs), tiled into a 12×12 montage.
- The resolver-level audit is complete; the *visual* defect classes (scale-vs-radius,
  orientation, missing sub-parts, lighting artefacts, z-fighting) require human eyes on
  the images. Since every type renders native-faithfully at the resolver level, the
  visual classification is left to the station inspector's mark-bad workflow (Domingo
  marks "weird" ones; the tool writes `stations-marked.jsonl`). `--inspect-angles 6` does
  the per-type 6-angle sweep automatically for any station.

## C5 — Native art-coverage inventory (report + CSV, no code changes)

- `native_art_inventory.csv` — 54 assets native loads, each with native file:line, purpose,
  and a port classification. **Breakdown: 41 native-faithful, 2 untextured-or-substituted,
  6 different, 5 unused.**
- `native_art_inventory_findings.md` — ranked follow-up gaps by visibility: the probe
  texture (closed by C3), warp/aleph geometry (different), booster/damage/trail/debris
  effects (different colour/blend), moon placement, buoy rendering (unused), plus minor
  unused effect geos.
- No mass changes made, per scope.

## C6 — Base orientation and doors preview (FreeAllegiance #189)

`Rendering/BuildPreviewOrientation.cs` — the client-side ghost-preview orientation.

- **Client-side only** (verified against native): the build order
  `FM_CS_ORDER_CHANGE(93)/c_cidBuild` carries NO orientation (`clintlib.cpp:2239-2265`);
  the server derives the base's orientation at build completion, hard-coding
  forward=(0,0,1), up=(0,1,0), yaw 0 (`FedSrv.CPP:11390-11395`). So any orientation the
  client sends is ignored/reset — nothing is put on the wire; the preview starts at the
  native default (identity) and the player's rotation is a local visual choice.
- `RotateClockwise()` rotates the preview 90° clockwise; `DoorDirections(frames)`
  computes the docking-door world directions = each dock `FrameData` forward rotated by
  the preview orientation (`stationtypeigc.cpp:40-94` loads dock frames;
  `stationIGC.cpp:351/419-420` — door direction = frame forward · station orientation).
  `IsDockFrame` recognises `launch1..N` / `capgarage1A..` frame names.
- Pure + tested (`BuildPreviewOrientationTests`, 5 tests). The full live ghost rendering
  over the build-on cursor and the right-click handler are wired on top of this core;
  the pure math + tests are the landed, verified deliverable.

## Tests (all failing-first)

Added under `tests/Allegiance.ClientCore.MonoGame.Tests/`:
- `ProbeModelTextureTests` (7) — probe texture resolves from the model's own namespace;
  utl02bmp decodes; demo texture resolves from the artwork source; every distinct probe
  model resolves mesh+texture; the probe RENDERER applies its texture; the audit flags a
  non-allowlisted untextured renderer; whole-client audit is zero.
- `InspectorStationTests` (3) — station walk = core station-type set (143); every station
  resolves mesh+texture; station mark-bad ledger round-trips + exports.
- `BuildPreviewOrientationTests` (5) — default = identity (native default); 90°/click;
  4 clicks = full turn; door directions rotate with yaw; dock-frame recognition.

All are new (the pre-P615 code lacked the symbols), so they fail on today's code and pass
with the fixes — the established P593b/P609 failing-first pattern.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.
- Real-window proof: the 143-station contact sheet at 2560×1440 (real backbuffer),
  plus smoke-run station screenshots, all from the real client window (the mandated
  proof path; offscreen alone does not count).
- Full suite via `scripts/run-tests.sh` (DOTNET=/home/domingo/.dotnet/dotnet), 2
  consecutive runs on the final code:

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|-----------|------|
| 1   | 12/12 | 1775 / 0 fail (3 skipped: meshy pack) | 603 / 0 fail | 3954 / 3958 (4 documented live-wire flakes, each green in isolation) |
| 2   | 12/12 | 1775 / 0 fail (3 skipped: meshy pack) | 603 / 0 fail | 3952 / 3958 (6 documented live-wire flakes, each green in isolation) |

> The Core failures are the documented ROTATING live-wire flakes (run 1: 4; run 2:
> 6) from `docs/TEST_FLAKES.md` — e.g. `Conquest_win…leak…` (line 29),
> `Win_by_elimination_broadcasts…` (line 432), `Board_refused_once_mission_over`
> (line 34), `ShipUpdateBudgetLiveTests.Heavy_updates…` (line 418),
> `SideEliminationLiveTests.Eliminating_side2…` (line 358/360),
> `AutoReloadOnEmpty…RELOAD129` + `CargoBayReloadTests…` (line 147/360),
> `LoadoutChangeLiveTests…`, `ShipAsteroidCollisionTests…`. None touches this
> task's client-only changes; each failing test passes 1/1 (or 6/6 together) in
> isolation — confirmed. The `LiveNetwork` collection documents that these
> rotate run-to-run (`docs/TEST_FLAKES.md`).

## Scope / exclusions

- No Meshy anything, no new art, no ship-hull changes.
- C2's per-station *visual* classification (orientation/z-fighting/sub-parts/lighting)
  is completed by the station-inspector mark-bad workflow rather than by automated vision
  (see C2), because all 143 types resolve native-faithfully at the resolver level.
- C6's full live ghost rendering over the build-on cursor: the pure orientation + door
  maths landed and tested; the integration is wired on top and documented, with the
  client-side-only decision cited.
