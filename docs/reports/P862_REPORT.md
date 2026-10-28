# P862 — the sector-warp stall: a warp into Alderan loaded the sector inside one frame

**Base:** public `master` at dispatch (`a85dad5b`).
**Host:** this workstation only (Intel Arc Pro B50 / Mesa 26.x on Xwayland, `DISPLAY=:0`), no rig, no
mini, no Mac. The rig's own 200v200 showcase run is Claude's/Domingo's to repeat (recipe in §8).
**Task driver:** Domingo's live flight video (rig 221, showcase map `m2xspiderw.igc`, 200v200 bots):
the frame rate dropped to ~18 fps for a moment at t≈74 s, the instant his ship warped from **Rigel**
into **Alderan** — the sector with the 134-rock asteroid field.

---

## 0. Outcome

**Cause (measured, not inferred).** A warp does not stream a sector in — it makes the client build that
sector's *entire* render state inside the ONE frame that first draws it. Entering Alderan from Omega on
this map loads three native rock models **and three 2048² native rock textures** (8.4 MB RGB565 `.mdl` →
16.7 MB RGBA decode → 16.7 MB GPU surface each) plus the planet poster, all in a single frame:

```
[sector-entry] Omega (1053) -> Alderan (1052) frame 3728: entry frame 17.9 ms; warming OFF (A/B baseline)
[asset] frame 3728: rock texture 'bgrnd05bmp' 2048x2048 loaded in 981 ms
[asset] frame 3728: rock texture 'bgrnd03bmp' 2048x2048 loaded in 999 ms
[asset] frame 3728: planet poster 'nebplnt06bmp' 2048x2048 loaded in 982 ms
frames -2..+5: 3727:16.7ms/0.2gpu/0MB  3728:1107.9ms/1050.6gpu/244MB
               3729:1001.9ms/995.0gpu/4MB  3730:6.6ms/0.1gpu/3MB  3731:11.7ms/0.1gpu/0MB
```

Two frames of ~1 s, **244 MB allocated in one of them**, 4 GCs (gen0/1/2 = 4/4/3) — then the sector
renders at 16.7 ms. In the live recording this is the 18 fps dip: native loads a sector's art up front, at
mission load (its models load in their own constructors, §2.3), and this port had drifted to loading it
lazily inside the transit frame, where the port's HD asset sizes turn native's benign step into a stall.

**Fix.** The client now resolves a sector's per-type assets *ahead of time* under a per-frame budget:
the own sector as soon as the mission data is known (while still docked), and the sector behind an aleph
as soon as the own ship is on approach — through the SAME loaders the draw path uses, so every warmed
asset is a permanent cache hit for the rest of the match. Along the way three measured inefficiencies
went: one warp decoded the planet **three times** (once per sky tier), the aleph destination glimpse
built the skies of up to six destinations in the arrival frame, and every native texture decode staged a
16.7 MB `Color[]` (≈33 MB of LOH garbage per 2048² texture).

**Measured result (this harness, 3 runs each side):** the entry frames that load the sector go from
**3 rock textures + the planet poster (+0–2 hull textures), 257–265 MB allocated and 1–2 one-second
frames** to **0 new assets, 17–22 MB allocated and no asset-driven stall**. §4 has the table; the one
remaining ~1 s frame in the "after" runs is the aleph destination glimpse's own sky preparation (GPU
1000 ms, ~4 MB), which is present in BOTH arms and is classified in §2.4 — it is not a regression and not
a sector-asset load.

---

## 1. Measure first — the harness

Two small additions to the existing proof paths (no new harness):

* `scripts/p862-warp-stall.sh <label> [--no-prewarm] [--park <sector>] [--bots "<tool> <json>"] …` —
  boots the local server + windowed client through `scripts/live-proof.sh` (P572) on
  `m2xspiderw.igc`, has the client `--auto-ready` / `--auto-launch` / `--auto-goto Alderan` and fly the
  real aleph (P572 auto-proof), and records the P643 per-frame CSV
  (`ALLEGIANCE_CLIENT_PERF=1 ALLEGIANCE_CLIENT_PERF_CSV=…`). It writes its own server config with
  `showMap`/`showHomeSector` (native `bShowMap`, `Igc/igc.h:1123`; a scripted autopilot cannot route to
  an aleph it has not discovered — see `GLOSSARY.md` §4).
  `--no-prewarm` exports `ALLEGIANCE_SECTOR_PREWARM=0`, the A/B switch: the pre-warm is off and every
  asset loads lazily on first draw, exactly as before this task. **Same binary, one env var** — the two
  arms cannot differ by anything else.
* `--park <sector>` (`--auto-park-bots`, a P862 proof aid) relocates the side's MCP bots into the target
  sector with the existing privileged `relocate` chat cheat. That is what makes the harness reproduce
  the LIVE situation: native discovery is side-wide, so friendly ships sitting in Alderan are what make
  the server export Alderan's rocks/stations to this pilot *before* he gets there (in the 200v200
  showcase hundreds of friendly bots were already fighting there). Without it a single-pilot run cannot
  know a destination sector's contents until it arrives, and no client-side pre-warm could cover that.
* Client diagnostics (`Game1.SectorLoad.cs`): on an own-ship sector change the client logs
  `[sector-entry]` — the entry frame's wall time, what the client already knew about the sector, and,
  over the next 30 rendered frames, exactly which assets the entry frames created plus the managed
  allocation and GC cost. Slow individual loads log `[asset] frame N: <kind> '<stem>' WxH loaded in
  X ms` (`Rendering/AssetLoadLog.cs`, threshold `ALLEGIANCE_ASSET_LOG_MS`, default 40 ms).

`ALLEGIANCE_NOVSYNC=1` + `ALLEGIANCE_FRAME_CAP=60` (a new companion to the P579 switch) are set by the
harness: this workstation's Xwayland throttles an occluded window's frame callbacks to ~1 Hz (measured
`present 1000.6 ms`), and an uncapped vsync-less loop runs at thousands of fps (which the server answers
with `SHIP_RESET`s). The cap gives a realistic 60 fps frame whose cost is the real cost.

```bash
# baseline (assets load lazily, pre-P862 behaviour), then the fix
scripts/p862-warp-stall.sh base --no-prewarm --exit-after 95 --park Alderan \
  --bots 'spawn_scenario {"preset":"showcase","size_scale":1}' --bots-after 10 \
  --client-extra '--aleph-glimpse off'
scripts/p862-warp-stall.sh fix  --exit-after 95             --park Alderan \
  --bots 'spawn_scenario {"preset":"showcase","size_scale":1}' --bots-after 10 \
  --client-extra '--aleph-glimpse off'
# → prints the per-change profile; artifacts: <out>/client.out, perf.csv, summary.json
```

---

## 2. The cause, with evidence

### 2.1 What happens on the transit frame

Everything the port draws is resolved **lazily, per model type, on first draw**:
`NativeAsteroidRenderer.GeometryFor/TextureFor` (native `.mdl` parse + native texture decode + upload),
`NativeStationRenderer.MeshFor/TextureFor`, `NativeShipRenderer.MeshFor/TextureFor`, `PlanetPoster`
(2048² planet `.mdl`), and the HD material sets on top of them. Nothing about a sector change creates
work a first sighting would not — but a warp makes a whole sector's worth of first sightings coincide:

* Omega (the debug-topology side-0 home sector on this map) holds **no asteroids**; Alderan holds
  **134 rocks in three models** (`bgrnd05` ×65, `bgrnd03` ×60, `bgrnd56` ×9 — the map's own
  `DataAsteroidIGC.modelName`), so from Omega every rock model is new.
* Each rock model's texture is a 2048² RGB565 `.mdl` (8,388,724 bytes). `MdlTextureDecoder.Decode`
  produces a 16.7 MB RGBA buffer, the client then staged a 16.7 MB `Color[]` (a 4.2 M-iteration loop),
  and `Texture2D.SetData` uploads a further 16.7 MB surface.
* The per-frame telemetry (`ALLEGIANCE_CLIENT_PERF_CSV`) shows the whole burst in one frame: **244 MB
  allocated**, 4 GCs, 1108 ms of frame time.
* The `[asset]` line names the assets and their cost, and a 256² *ship* texture measures the same
  ~980 ms on this box (see §2.5): on this host a first-use texture creation stalls ~1 s regardless of
  size (a driver/GL behaviour of this workstation, not the decode arithmetic), so the *number* of
  textures created in the entry frame — 4 in the baseline — is what the fix has to reduce.

### 2.2 Three duplicate/deferred loads the port did not need

1. **The planet was decoded three times per warp.** `DrawSkyP576` told *all three* sky tiers about every
   sector change, and each tier owns its own `PlanetPoster` (and its own dome/environment cache). One
   warp decoded the same `nebplntNNbmp.mdl` (2048², ~16.7 MB GPU surface) three times over, and the
   inactive tiers' nebula art on top, inside the transit frame.
2. **The aleph destination glimpse prepared invisible skies.** `AlephGlimpseRenderer` renders a
   destination sector's sky into a 256² target per aleph — up to `MaxGlimpses = 6` — and it ran that
   preparation *before* checking the approach fade. Beyond `FadeStartDistance` (3400 u, P585) the
   glimpse is drawn fully transparent, so a freshly entered sector prepared up to six destination skies
   (each a 2048² planet + the `p589obj` PNG library) behind invisible overlays, in the arrival frame.
   Measured: with `--aleph-glimpse off` the arrival frame's GPU time drops from 995–1050 ms to 19 ms.
3. **Every native texture decode allocated a redundant 16.7 MB staging array** (§2.1).

### 2.3 The native intent (classification)

This is **not** a case of native being faster at the same thing — native does the work *earlier*:

* Every asteroid loads its model + texture **in its constructor**:
  `HRESULT rc = Load(0, m_asteroidDef.modelName, m_asteroidDef.textureName, …)`
  (`Igc/asteroidigc.cpp:32-37`), same for stations (`Igc/stationIGC.cpp:240-244`), and every hull type
  is **preloaded** when the core is created: `pigc->Preload(m_data->modelName, …)`
  (`Igc/hullTypeIGC.cpp:47-49`). `CmodelIGC::Load` → `ThingSiteImpl::LoadModel`
  (`Igc/modelIGC.cpp:141-166`) → `GetModeler()->LoadImage(textureName, false)` + `LoadMDL`
  (`WinTrek/trekigc.cpp:1816-1858`), i.e. **geometry + texture decoded and uploaded at mission load**,
  and `WinTrekClient::Preload` (`WinTrek/trekigc.cpp:4912-4936`) exists purely to warm the modeler's
  namespaces up front.
* The own-ship **cluster change** itself (`WinTrekClient::ChangeCluster`, `WinTrek/trekigc.cpp:2746-2790`)
  only activates the cluster, clears the old cluster's sounds and swaps the environment geo
  (`Igc/clusterIGC.cpp:43` → `SetEnvironmentGeo`) — no model/texture loading at all.
* Native's assets are also 2000-era: 512²–1024², no HD pack, so the up-front load is cheap there and the
  port's lazily-loaded HD assets turn native's benign shape into a visible stall.

So the **native rule is "load the art up front, never in the frame that first needs it"**, and the port
had drifted to lazy-on-first-draw. P862 restores the native shape (pre-resolve ahead of need) and adds
the bounded per-frame budget a modern asset set needs.

### 2.4 What is left after the fix (secondary finding, measured)

With the sector's own assets pre-warmed the entry frames create nothing, yet one ~1 s frame is still
there at arrival. Two measurements split it:

1. **The aleph destination glimpse.** With the glimpse enabled the arrival frame's GPU time is
   995–1050 ms; adding `--aleph-glimpse off` drops the same frame's GPU time to **19 ms**. The glimpse
   prepares a destination sector's sky (a 2048² planet decode + the `p589obj` PNG library + a per-
   destination sky renderer) for alephs it will draw — including, on arrival, the sector being left.
   P862 removed the wasteful half (it prepared skies for alephs it would draw fully transparent, §3.2);
   the remaining single destination sky is a follow-up: the glimpse resolves a destination by feeding the
   raw partner-warp id into a cluster lookup (the P540c mismatch `WarpDestinationResolver` already fixes
   for the autopilot), so a pre-warm cannot reliably target "the sky the arrival flare will ask for" —
   my attempt warmed a 1292 ms sky the flare did not use and the arrival still decoded its own, so it was
   reverted.
2. **A host-level first-use GPU stall (~990–1000 ms of GPU time, ~3–5 MB allocated, nothing loaded).**
   It is still present with the glimpse off. A first-use program-link hypothesis was tested and falsified:
   applying the asteroid pass once during the warm-up (`NativeAsteroidRenderer.WarmPipeline`, a temporary
   change) left the arrival frame at 1021.7 ms / GPU 997.3 ms versus 1015.3 / 990.8 without it — no
   change — so the change was reverted rather than shipped. On this host *any* first-use GPU resource
   costs about a second (§2.5), so this is very likely the same driver behaviour and not something an
   asset pre-warm can address; it must be re-measured on the rig (§8), where the `[asset]` and
   `[sector-entry]` lines will name anything that is actually loaded.

Both arms of the A/B contain that frame, so it is neither caused nor hidden by the sector-asset fix. The
final confirmation runs on the shipping binary reproduced the fixed arm exactly: `final-fix` entry frames
**0 assets / 15.2 MB**, warp-frame p99 **17.6 ms**, worst 1015.3 ms (GPU 990.8 ms, 3.1 MB), and
`final-fix3` (after the bounded autopilot hold) **0 assets / 15 MB, p99 18.0 ms**, worst 1017.4 ms
(GPU 994.6 ms, 3.4 MB), and a last end-to-end check on the exact shipping harness (after the proof-aid
clean-up) **0 assets / 16.7 MB, 0 GCs, p99 24.3 ms**, worst 1018.5 ms (GPU 994.2 ms, 3.6 MB) — the same
single residual frame every time, and the ship warps.

### 2.5 Harness caveat: this host's GL stall

A 256×256 ship texture (`fig05ops1bmp`) measured **963 ms**, `bgrnd56bmp` (2048²) **34–45 ms** in the
next frame, `nebplnt43bmp` **31 ms** while `nebplnt06bmp` (same size) **982 ms**. The absolute
milliseconds are therefore dominated by a per-creation driver stall on this workstation (shared with
other lanes' GPU work), not by decode cost. What is stable and comparable across arms is *how many*
textures/first-uses land in the entry frames and *how many bytes* are allocated there — those are the
numbers §4 reports, plus the frame-time profile with the caveat stated.

---

## 3. What changed (client rendering only)

### 3.1 Sector asset warm-up (`Rendering/SectorWarmup.cs`, `Game1.SectorLoad.cs`)

* `SectorWarmupQueue` — the pure scheduler: one item per `(kind, stem)` across the whole match (two
  sectors sharing a rock model load it once), `Promote(sectorId)` for an imminent warp, `Complete()`
  after which an asset is never handed out again (**a second entry into a sector does no new loads**),
  and counters for created / already-resident / unavailable.
* `SectorWarmupPlanner` — which assets a sector needs: its rocks (`DataAsteroidIGC.modelName` +
  texture), its station **types** (`DataStationTypeIGC.modelName`, resolved through the shop catalogue
  the draw path uses), the hull models of the ships the client knows are in it, and its sky.
* `WarmupBudget` — "one heavy item per frame": a single 2048² decode cannot be split, so the budget
  bounds how many items a frame starts (2 ms/frame flying, 12 ms while docked — those frames carry no
  flight) and never wedges when one item overshoots it.
* `Game1.SectorLoad.cs` — feeds it: the own sector as soon as `world.WorldReady` (switching the session's
  now-unused docked/loading frames to work), the sector behind an aleph once the own ship is within
  **7000 u** (`WarpDestinationResolver.DestinationSector`, promoted to the front), re-seeded on every
  200 ms scan while approaching so ships that arrive late are still warmed. `ALLEGIANCE_SECTOR_PREWARM=0`
  disables it (the A/B baseline).
* Warm entry points on the renderers (`Warm(...)` on `NativeAsteroidRenderer`, `NativeStationRenderer`,
  `NativeShipRenderer` incl. its LOD levels, `PlanetPoster.Warm`, `NativeSkyRenderer.Warm`,
  `HdSkyRenderer.Warm`) call exactly the loaders `Draw` calls and report created/already-resident.

Cost/memory: only the sectors that matter are warmed (own + approached), so the GPU memory added is the
sector's own per-type set — Alderan here: **3 rock textures + 1 planet ≈ 4 × 16.7 MB ≈ 67 MB** (plus the
hull textures that sector's ships need, which every one of those ships would have loaded anyway). Nothing
is duplicated: the caches are the ones the renderer already owns, and the existing HD-material LRU budget
(512 MiB) still governs HD sets.

### 3.2 One sky tier per sector change (`Game1.Rendering.cs` `DrawSkyP576`)

Only the ACTIVE tier is told about a cluster change (per-tier `short` latches keep a tier switch working,
though `--sky` is fixed at launch), so one warp decodes the planet once instead of three times, and never
decodes the two tiers that are not drawn.

### 3.3 The glimpse no longer prepares a sky it draws invisibly (`AlephGlimpseRenderer`)

The fade/opacity is computed BEFORE `EnsureRendered`, and a glimpse with a zero resting fade and no
transit pulse is skipped entirely (`continue`) instead of being prepared and then drawn fully
transparent. Visually identical: at ≥ 3400 u the glimpse contributed nothing. It still prepares a
destination sky the moment an aleph is close enough to show one, or when a transit pulse fires.

### 3.4 Native textures upload their decoded bytes (`NativeAsteroidRenderer`, `NativeShipRenderer`,
`NativeStationTexture`, `PlanetPoster`, `NativeSkyRenderer`)

`SurfaceFormat.Color` IS RGBA8 and the decoder already writes alpha 255 (or the premultiplied RGBA for
the planet/dome), so the `Color[]` staging array + per-pixel loop was pure overhead: `SetData(decoded.Rgba)`
uploads the same bytes and removes ~16.7 MB of LOH garbage and a 4.2 M-iteration loop **per 2048²
texture** — which matters most exactly when several land in one frame.

### 3.5 Autopilot: "cannot steer this frame" is not "target lost" (`Flight/AutopilotHold.cs`)

The harness needs a real aleph transit, and the client could not fly one: a `--auto-goto` issued on the
undock frame resolved before the own pose/warp graph existed, the caller treated that single frame as
`TargetLost` and dropped the order (`autopilot ON … autopilot OFF (TargetLost)`, same second), so the ship
never warped. `AutopilotHold.ShouldDisengage(engaged, steered, targetKnown, nowMs, holdSince)` now holds the order while
the target still exists, and disengages when it is really gone **or** when the order has been unsteerable
for longer than `HoldLimitMs` (2 s) — so the one-frame transient is covered without leaving a stuck order
engaged for the rest of the match. Native's own behaviour is the model (an autopilot with no pose simply
has nothing to steer). This is the only behaviour change in the flight code; it is client-only and it is
what makes an unattended live proof of a warp possible at all.

### 3.6 Harness aids, off unless asked

`ALLEGIANCE_FRAME_CAP=<fps>` (a companion to the P579 `ALLEGIANCE_NOVSYNC`), `ALLEGIANCE_ASSET_LOG_MS`
(the `[asset]` slow-load line), `ALLEGIANCE_SECTOR_SHOTS=<dir>` (frames on both sides of the transit),
`ALLEGIANCE_AUTOPILOT_DEBUG=1` (one line per second explaining why an engaged autopilot cannot steer),
`--auto-park-bots` (proof-only relocate of the side's harness bots). All are inert in normal play.

---

## 4. After the fix — the same curve

Same binary, same map, same scripted flight (Omega → Alderan), same parked friendly bots; only
`ALLEGIANCE_SECTOR_PREWARM` differs. 3 runs per arm, `--aleph-glimpse off` (to isolate the sector-asset
effect from the glimpse finding in §2.4). "Entry frames" = the 30 rendered frames starting at the sector
change; "5 s" = 300 frames before/after it.

| run | assets created in the entry frames | allocated in the entry frames | GC (0/1/2) | worst frame in the 5 s **after** the warp | frames ≥ 100 ms after |
|---|---|---|---|---|---|
| **before** 1 | rock 3M/3T, planet +1, ship 2T | **264.3 MB** | 5/5/4 | **1115.8 ms** (GPU 1057 ms) | 2 |
| **before** 2 | rock 3M/3T, planet +1 | **257.5 MB** | 2/2/1 | **1111.8 ms** (GPU 1055 ms) | 2 |
| **before** 3 | rock 3M/3T, planet +1, ship 2T | **265.3 MB** | 2/2/1 | **1107.6 ms** (GPU 1049 ms) | 2 |
| **after** 1 | **none** | **20.0 MB** | 2/2/1 | 1024.9 ms (GPU 1000 ms, 4.8 MB alloc) | 1 |
| **after** 2 | **none** | **22.0 MB** | 1/1/0 | 1018.6 ms (GPU 1000 ms, 3.8 MB alloc) | 2 |
| **after** 3 | **none** | **17.7 MB** | 1/1/0 | 1023.7 ms (GPU 1000 ms, 3.5 MB alloc) | 1 |
| *after, final binary* | **none** | **15.2 MB** | 1/0/0 | 1015.3 ms (GPU 990.8 ms, 3.1 MB) | 1 |

Reading it honestly:

* **The sector's asset load is gone.** Entry-frame allocations drop from **257–265 MB to 17–22 MB**
  (~12×) and the entry frames create **no** new meshes/textures/planets, in 3/3 runs. That is the defect
  §2.1 measured, and the same three rock textures that used to be decoded inside the transit frame are
  now decoded during the aleph approach (`[sector-warm] rock bgrnd05 … 983 ms`, `… bgrnd03 … 999 ms`,
  one per frame under the 2 ms budget).
* **The remaining slow frame is not the sector's assets.** The fixed runs' entry frames create nothing
  and allocate ~3.5–4.8 MB (baseline: 244 MB), yet one frame still measures ~1000 ms of *GPU* time. Part
  of that is the aleph destination glimpse's own sky preparation: with the glimpse enabled the
  arrival frame's GPU time is ~995–1050 ms and turning the glimpse off drops it to 19 ms (§2.4). With the
  glimpse off (this A/B) the frame's GPU time is still ~990–1000 ms with nothing loaded and ~3 MB
  allocated — the host's per-first-use GPU stall (§2.5), which no asset accounting can attribute and which
  I could not eliminate (a program-link warm-up was measured to have no effect and reverted, §2.4).
  Either way it is present in BOTH arms, so it is neither caused nor fixed by this change; it is the
  follow-up in §10 and must be re-measured on the rig (§8).
* **The cost moved earlier, deliberately.** The "5 s before" window in the fixed runs contains the
  approach's warm frames (worst ~1002 ms here, one texture per frame). On this host EVERY first-use
  texture costs ~1 s (§2.5); on the rig each 2048² texture is a fraction of a frame's budget, so the
  2 ms/frame warm-up absorbs it over the approach. What matters for the reported symptom is that the
  warp itself no longer pays it.
* Everything else is unchanged: `p50` is 16.6–16.7 ms before and after, and the sector renders at
  0.1–0.2 ms of GPU time once entered (134 rocks in 3 models, all cached).

With the **private HD pack** on (`--hd`; the rig's default — `HdMaterialRenderer` now also warms the HD
material set for each warmed model, at a mid-distance mip, inside the same LRU budget):
baseline entry frame **1134.9 ms / GPU 1073.7 ms / 255.4 MB** (3 rock textures + planet + the HD sets),
fixed **1012.5 ms / GPU 985.3 ms / 9.9 MB** with **20 HD materials already resident before the entry**
(their first uploads measure 0.2–14.6 ms and 0.01–1.33 MiB each on this pack). The HD side was never the
dominant cost here — the pack's atlases are small — but it is no longer deferred into the transit frame
either, and the later mip promotions as the camera closes on a rock stay in the existing quality path.

For the record, the **glimpse-on** arm (the port's default configuration the live recording ran): baseline
`ab-base-1` = entry frame **1116.1 ms, GPU 1058.6 ms, 243.3 MB**; fixed `shots-modern` = entry frames load
nothing, **~12 MB**, with the same single glimpse frame. The asset delta is identical; the glimpse frame
is the constant.

---

## 5. Verification (light gate)

* `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
* `tools/build-tools.sh` — **all 90 tool projects built**, every one 0 warnings / 0 errors.
* New tests `tests/Allegiance.ClientCore.MonoGame.Tests/P862SectorWarmupTests.cs` — **20 tests** (the
  queue/planner/budget properties the fix rests on, plus the autopilot hold rule incl. its 2 s limit),
  **3× alone: 20/20, 20/20, 20/20**.
* ONE `tools/run-tests.sh --tier fast` (after the final build and the 3×-alone new tests):

```
Discovery counts: Lobby 87 fast / Update 28 / MonoGame 3633 / ClientCore 1915 / Core 5565
Fast infrastructure checks: PASS
fast: 11228 tests, 310.4s, PASS
  Allegiance.Update.Tests          28 passed
  Allegiance.Lobby.Tests           87 passed
  Allegiance.ClientCore.MonoGame.Tests 3630 passed, 3 not executed (documented skips)
  Allegiance.ClientCore.Tests      1913 passed (+2 quarantine)
  Allegiance.Core.Tests            5555 passed (+10 quarantine)
PASS: TestResults/tiers-20261028T062111.353989Z/summary.json
```
The fast tier was run three times in total (an earlier pass on the pre-final tree, 11 227 tests /
311.4 s; a second green pass, 309.6 s; and this one) and also once more after the last HD-material warm
edit, which reported **11228 tests / 310.9 s with two failures outside this task's surface** —
`Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` (6672 B over 10 000 frames,
the documented P775/P787a allocation-sampling flake) and
`P844MissionRulesCertifyTests.Allied_survivors_win_when_third_side_is_eliminated` (another lane's P844
work). **Both pass 3/3 alone** and are ledgered in `docs/TEST_FLAKES.md` (2026-10-28); P862 touches no
HUD-formatting/allocation or mission-rules code (client rendering only). The tree that produced the
numbers above and these edits differs only by the HD-material warm, which the HD A/B in §4 measures.

* Showcase never-parks
  (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`)
  **5× alone: PASS** each time — 3 m 23 s, 3 m 19 s, 3 m 59 s, 3 m 15 s, 3 m 25 s (load average 3–8 on
  the shared host; the other lane was running its own copy of this test during part of the window).
* The A/B itself (§4) is the performance gate: 3 runs per arm, one env var apart.
* Client-side smoke: the harness's own runs — the client flies, warps, and the
  `[sector-entry]` accounting reports the entry frames; no exception, no visual change (§6).

---

## 6. Frames (`docs/reports/P862_assets/`, 1.7 MB, every file < 1 MB)

Real 1280×720 backbuffer frames from the scripted flight, captured by
`ALLEGIANCE_SECTOR_SHOTS` on the frame in the departure sector 1.7 km from the aleph and on the first
frame after the transit (plus the settled field for Classic). Both HUD styles are shown because the
frames carry the HUD (`--hud-settings`). I opened every file and describe what is visible:

| file | what I see |
|---|---|
| `p862_classic_before_warp.png` | Omega, 1.7 km from the Alderan aleph: an empty starfield with the aleph dead ahead (dark swirl, small yellow core), the Classic HUD's gauge rings (thrust/fuel/armor/shield/ammo/energy), target pane "Alderan: unknown", minimap Omega/Bug Nebula/Tellus, "YOU: Omega", "Current location: Omega", and the relocate chat lines the harness sent. |
| `p862_classic_after_warp.png` | Alderan, first frame after the warp: the asteroid field is already there and **textured** — dozens of grey-brown speckled rocks near and far — with the arrival aleph's white funnel glowing below the ship, friendly bot contacts (`mcp-b0xx`) labelled with ranges, "Ship 8097 was killed", minimap now Alderan/Rigel/Gaia and "YOU: Alderan". |
| `p862_classic_after_warp_0_5s.png` | Alderan, half a second later: the same field from a slightly different position (1083 m to the nearest contact), rocks unchanged and textured, the aleph funnel now behind/below, throttle arc lit. |
| `p862_modern_before_warp.png` | The same moment as the first frame in the Modern HUD: Omega, aleph ahead at 1.66 km, AUTOPILOT engaged, Modern's large circular gauges (AMMO/ENERGY/ARMOR/SHIELD/THRUST/FUEL all 100 %), big speed readout, "HUD Tactical / RADAR Default". |
| `p862_modern_after_warp.png` | The same arrival frame in the Modern HUD: the same textured asteroid field (identical rocks), gauges recomposed around the ship, speed 204, the bot contacts and kill/eject messages. |

**Nothing about the look changed** — the after-frame rocks are the same three native models and the same
2048² native textures whether they were decoded at the approach (fixed) or in this frame (baseline); the
baseline capture of the same frame (`~/allegiance-evidence/P862/shots-baseline/shots/sector_1052_after0.png`)
shows the identical rock appearance and lighting.

---

## 7. Native-intent classification and the modern/better approach

**Native intent.** Load the art up front; never in the frame that first needs it (§2.3):
`Igc/hullTypeIGC.cpp:47-49` (`Preload` from the hull type's constructor),
`Igc/asteroidigc.cpp:32-37` / `Igc/stationIGC.cpp:240-244` (`Load(...)` in the model constructor),
`Igc/modelIGC.cpp:141-166` → `WinTrek/trekigc.cpp:1816-1858` (model + texture decode),
`WinTrek/trekigc.cpp:4912-4936` (`WinTrekClient::Preload`), and the cluster change itself
(`WinTrek/trekigc.cpp:2746-2790`) only swaps the environment geo (`Igc/clusterIGC.cpp:43`).
P862 restores that intent (resolve ahead of need) **and** adds what native never needed: a bounded
per-frame time budget, because the port's HD assets are 4–16× the bytes native shipped and because the
client is a single-threaded render loop with a 16.7 ms contract.

**Modern/better approach.** The remaining, genuinely modern step is to do the *decode* off the render
thread: the file read + RGB565→RGBA conversion are pure CPU and could run on a worker, leaving only the
`SetData` upload on the render thread (a smaller, bounded cost). That is the natural follow-up to this
task and would let even a cold, unannounced sector entry cost one upload per frame instead of one full
decode; it was not done here because it changes the loaders' threading contract in five renderers and the
budgeted pre-warm already meets the target on the measured path.

---

## 8. Recipe — confirm it in a live flight on the mini client

1. Server: the showcase map with the 200v200 fleet (`spawn_scenario {"preset":"showcase","size_scale":13}`
   → 195 bots/side, `target_sector: Alderan`), i.e. the rig configuration the video came from. Bots from
   your own side must already be in Alderan — that is what makes the client know the field before it
   warps (`showMap`/`showHomeSector` are NOT needed for this on the rig, the scouts do it).
2. Client (mini, windowed, the same binary): fly home → Rigel → Alderan by hand (or `--auto-goto Alderan`
   once an aleph is discovered).
3. Watch the client log for:
   * `[sector-warm] aleph approach -> Alderan (1052): N asset(s) queued, N promoted to the front (… u to the aleph)`
     — this must appear **before** the warp;
   * `[sector-warm] Alderan (1052): N item(s) pre-warmed, …` — with the rock textures listed and a few
     hundred ms each;
   * `[sector-entry] Rigel (1059) -> Alderan (1052) frame N: entry frame X ms; warming on; client knows
     R rock(s), S ship(s) here` followed by `… first 30 frames: rock 0M/0T, station 0M/0T, ship 0M/0T,
     planet +0, HD +0 … allocated <20 MB`.
   If `rock 3M/3T` and `allocated ~240 MB` appear there instead, the pre-warm did not run (check
   `ALLEGIANCE_SECTOR_PREWARM` is unset) or the client did not know the sector on approach (then the
   pre-warm cannot help — see §2.4).
4. For numbers, run with `ALLEGIANCE_CLIENT_PERF=1 ALLEGIANCE_CLIENT_PERF_CSV=<file>` and compare the
   frames around the `[sector-entry]` frame id (or on the mini run `scripts/p862-warp-stall.sh` with
   `--no-prewarm` once and without it once).

---

## 9. Files

New:
* `src/Allegiance.ClientCore.Game/Rendering/SectorWarmup.cs` — queue, planner, budget (pure).
* `src/Allegiance.ClientCore.Game/Game1.SectorLoad.cs` — seeding, aleph approach, sector-entry accounting,
  proof shots.
* `src/Allegiance.ClientCore.Game/Rendering/AssetLoadLog.cs` — the `[asset]` slow-load line.
* `src/Allegiance.ClientCore.Game/Flight/AutopilotHold.cs` — the hold-vs-lose rule.
* `scripts/p862-warp-stall.sh`, `scripts/p862-warp-summary.py` — the harness + its summary.
* `tests/Allegiance.ClientCore.MonoGame.Tests/P862SectorWarmupTests.cs` — 20 tests (queue/planner/budget +
  the autopilot hold rule).
* `docs/reports/P862_assets/*.png` — the warp frames (both HUD styles).

Changed:
* `src/Allegiance.ClientCore.Game/Rendering/HdMaterialRenderer.cs` — `Warm` (resolve a material's HD set
  ahead of the draw), `WarmProjectedPixels`.
* `src/Allegiance.ClientCore.Game/Rendering/NativeAsteroidRenderer.cs` — `Warm`, load counters, byte upload.
* `src/Allegiance.ClientCore.Game/Rendering/NativeStationRenderer.cs` — `Warm`, load counters.
* `src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs` — `Warm` (incl. LOD levels), counters,
  byte upload.
* `src/Allegiance.ClientCore.Game/Rendering/NativeStationTexture.cs`, `PlanetPoster.cs`,
  `NativeSkyRenderer.cs`, `HdSkyRenderer.cs` — byte uploads, `Warm`/`WarmPlanet`, counters.
* `src/Allegiance.ClientCore.Game/Rendering/AlephGlimpseRenderer.cs` — skip an invisible glimpse's sky.
* `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — one sky tier per sector change.
* `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — call the sector load update; hold the autopilot
  on a transient frame; `AutoPilotTargetStillKnown`.
* `src/Allegiance.ClientCore.Game/Game1.AutoProof.cs`, `LiveProof/AutoProofPlan.cs` — `--auto-park-bots`.
* `src/Allegiance.ClientCore.Game/Game1.cs`, `Game1.Lifecycle.cs` — `ALLEGIANCE_FRAME_CAP`, the per-tier
  sky latches, the autopilot hold flag, the `[asset]` frame stamp.
* `GLOSSARY.md` — the "Sector asset warm-up (P862)" entry.
* `docs/TEST_FLAKES.md` — the two out-of-scope fast-tier failures recorded (both 3/3 alone).

No server, simulation, wire, parity-CSV or Docker change. New media: the five PNGs above — unreviewed
generated-by-run screenshots (no Meshy content), which Domingo must eyeball before the push
(`scripts/publish-guard.sh` fails on new media unless `ALLOW_MEDIA=1`).

---

## 10. Caveats / unfinished

* **The remaining arrival-frame stall** (§2.4) is present in both arms here and is not an asset load: it
  is the aleph destination glimpse's own sky preparation (GPU ~1000 ms with ~4 MB allocated; its GPU part
  disappears with `--aleph-glimpse off`), plus, with the glimpse off, one ~1 s first-use GPU frame that I
  could not attribute to any load (nothing is created and ~3 MB is allocated; a first-use program-link
  hypothesis was tested and falsified). Follow-ups: (a) fix the glimpse's destination resolution
  (`WarpDestinationResolver`, the P540c shape — today it feeds the partner WARP id into a cluster lookup)
  and then pre-warm the destination the arrival flare will ask for; my blind attempt cost 1292 ms and
  still missed, so it was reverted; (b) re-measure the residual on the rig — on this host every first-use
  GPU resource costs ~1 s (a 256² texture measured 963 ms), so its absolute size here says nothing about
  the rig.
* **This host's GL stalls ~1 s per first-use texture** (§2.5), so absolute milliseconds here are not the
  rig's; the counts and allocations are the transferable result. The rig numbers are Claude's to take (§8).
* The harness runs with `--aleph-glimpse off` for the A/B (to isolate the sector-asset effect from §2.4);
  the glimpse-on numbers are quoted there. `--park Alderan` + MCP bots are required for the client to know
  the destination sector, as in the live 200v200 case.
* The HD material warm asks for a mid-distance mip (a 512 px footprint): a rock the camera flies right up
  to still promotes its set once (measured 1.33 → 5.33 MiB, ~3 ms on this pack) — cheap, and it is the
  same promote path the renderer already had.
* The pre-warm only helps when the client knows the sector on approach (side-wide discovery) or when the
  sector is its own. A blind teleport into an undiscovered sector still loads on arrival — no client-side
  fix can avoid that without the map data, and the budgeted warm-up at least spreads whatever it can.
* Not measured on a slow GPU: the warm-up's 2 ms/frame budget assumes a 60 fps flight frame; on a slower
  client the same budget simply spreads the work over more frames.
