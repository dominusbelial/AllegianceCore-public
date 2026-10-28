# P862B — the arrival frame: the aleph glimpse resolved the wrong destination (and in fact drew nothing), and its flare read the GPU back synchronously

**Base:** public `master` at dispatch (`e4c04845`, contains P862).
**Host:** this workstation only (Intel Arc Pro B50 / Mesa 26.0.8 on Xwayland, `DISPLAY=:0`), no rig, no
mini, no Mac. Local evidence: `~/allegiance-evidence/P862B/` (all runs' `client.out`, `perf.csv`,
`summary.json`, the shot PNGs and the glimpse-target dumps). Nothing outside this worktree was edited.
**Task driver:** P862 §2.4/§10 left one ~1 s arrival frame and named two leads: the destination glimpse
resolves its sector wrongly (so P862 could not pre-warm it), and the flare's occlusion test does a
synchronous GPU→CPU readback inside the frame.

---

## 0. Outcome

1. **Lead 1 confirmed — and it is worse than "wrong destination": the glimpse drew *nothing* on this
   map, ever.** `DataWarpIGC.DestinationId` is the **partner warp id** (native
   `Igc/warpIGC.h:129-143`, `GetWarp(m_warpDef.destinationID)`); the port fed it into
   `World.Clusters` (`Game1.Rendering.cs:2260` at the base). Warp ids and cluster ids do not overlap
   (`m2xspiderw.igc`: clusters `1050-1062`, warps `2150-2193` — `tools/missiondump`), so the lookup never
   matched, `DrawAlephGlimpses` returned an empty list and the whole feature (destination sky, open-wide
   pulse, lens flare) was dead code. Fixed with `WarpDestinationResolver` in
   `AlephGlimpseRenderer.CollectTargets`, and the cache is keyed by the **resolved sector**
   (`Rendering/AlephGlimpseRenderer.cs`).
2. **P862's pre-warm failure was exactly lead 1 — that holds.** With the resolution fixed, and with the
   destination sky rendered through the **sector's own sky-tier renderer** (shared art caches, so the
   sector you just left needs no second decode), the sector warm-up prepares the glimpse sky during the
   aleph approach: the arrival window now reports **`glimpse sky +0`** created and the two arrival-frame
   targets are already resident (`resident before entry: … glimpse sky +2`).
3. **Lead 2 measured: the readback had never even run** (no glimpse → no flare → no
   `DepthRT.GetData`). With the glimpse fixed it runs, and on this host it costs **2.5–3.3 ms per call**
   (worst 5.4–5.9 ms) — a *per-call GL driver cost*, not a pipeline wait, so it has to be amortised by
   calling it **less often**. It is now **deferred**: the sample is read at the START of the next frame,
   from the depth target this frame's pass has not overwritten yet (same pixel, same depth pair, one frame
   late), capped to **one batched readback per frame** and refreshed per warp at ~10 Hz
   (`Rendering/FlareOcclusion.cs`). Measured on the same scenario: **61 readbacks/s → 7–8 readbacks/s**
   and **133–138 ms/s → 14–19 ms/s** (≈0.3 ms per frame in a transit wave), with the P585 behaviour
   intact — the flare is still hidden exactly when the funnel centre is behind opaque geometry, one frame
   and ≤100 ms of verdict age later (§4.3).
4. **P862's residual ~1 s arrival frame does not reproduce on this host today.** The *before* arm
   (unmodified master, glimpse on) measures the arrival frame at 17.3–17.6 ms and the next frame at
   24.8–34.9 ms (GPU 3.6–11.3 ms, 2–4 MB). Every first-use texture here loads in 25–45 ms, not the
   ~1 s P862 recorded (§5), so the P862 §2.5 caveat applies in reverse: absolute milliseconds on this
   host are *not* the rig's, and the arrival-frame numbers below are only meaningful as an A/B.
5. **Anything left in the arrival window after 1–3 is named** (§6): nothing is loaded and no glimpse
   target is created; the 10–14 ms GPU / 3–5 MB frame one frame after the change exists in **every** arm
   including master, is unaffected by the glimpse and by the flare test, and is the P585 transit-pulse
   frame (the opt-in funnel swell that fires on the player's own arrival).

---

## 1. The two leads, checked against the code

Both leads were re-grepped at the base commit and in the tree.

**1a. The glimpse's destination.** Base `Game1.Rendering.cs:2252-2268`:

```csharp
foreach (var kv in world.Warps)
{
    var w = kv.Value;
    if (w.ClusterId != current.ClusterId) continue;
    if (!world.Clusters.TryGetValue(w.DestinationId, out var dest)) continue;   // :2260
```

`DataWarpIGC.DestinationId` is decoded from the warp's `destinationID` field (`Core/Data/DataWarpIGC.cs`,
native `WarpDef`/`DataWarpIGC` `Igc/igc.h:2506`), and native's meaning for it is unambiguous: the
**partner warp** (`Igc/warpIGC.h:129-143`: `m_destination = GetMyMission()->GetWarp(m_warpDef.destinationID)`),
whose `ClusterId` is the destination **sector** — what the server transit uses
(`FedSrv/fsship.cpp:481-483`: `pwarp->GetDestination()->GetCluster()->GetObjectID()`) and what the client
uses (`clintlib/appmsg.cpp:867`). The port already has that rule in one place for the autopilot, the
router and the server transit: `Core/Model/WarpDestinationResolver.DestinationSector` (P540c). The
glimpse was the one consumer that never adopted it.

`tools/missiondump` on `m2xspiderw.igc` shows why that matters concretely:

```
sector 1053 'Omega'   warp 2153 'Alderan'  destSector=2152->'Alderan'
sector 1052 'Alderan' warp 2152 'Omega'    destSector=2153->'Omega'
cluster ids 1050-1062, warp ids 2150-2193
```

`Clusters[2153]` never exists, so the glimpse resolved *nothing*: no destination sky, no in-funnel
open-wide, and no flare — the code that P862 measured (and §2.4 blamed for the GPU cost) never ran.

**1b. The flare readback.** Base `Game1.Rendering.cs:2282-2310`: `TransitFlareOccluded` did
`depth.GetData(0, new Rectangle(sx, ty, 1, 1), px, 0, 1)` (base `:2299`) for every flaring aleph, every
frame of a pulse, inside the frame that is drawing — a synchronous GPU→CPU readback that cannot complete
before this frame's depth pass has finished. It is reached only from `AlephGlimpseRenderer.DrawFlare`,
which is reached only when a glimpse is drawn — i.e. never, until 1a is fixed.

That also means P862 §2.4's attribution needs correcting: `--aleph-glimpse off` disables the **transit
pulse** as well as the glimpse (`Game1.DetectTransitSwells` / `DrainAlephTransitSignals` gate the pulse on
the same flag), so that A/B moved two things at once, and the destination-sky preparation it named could
not have taken part (there was none). The pulse frame is still there with the glimpse flag off — §6 —
which is the true owner of that residual.

---

## 2. What changed

### 2.1 The glimpse resolves (and caches) the RESOLVED destination sector

`Rendering/AlephGlimpseRenderer.cs` gains a pure, GPU-free target builder:

```csharp
public static List<GlimpseTarget> CollectTargets(
    IReadOnlyDictionary<short, DataWarpIGC> warps,
    IReadOnlyDictionary<short, DataClusterIGC> clusters,
    short currentSector, Vector3 cameraEye)
```

`GlimpseTarget(Warp, DestSector, Dest, DistSq)` carries the resolved sector **explicitly**, so the cached
256² target and the "glimpse sky +N" counter are keyed by a sector id, never by a warp id. A warp whose
partner is not synced contributes nothing (the P540c rule: skip, never guess). `DrawAlephGlimpses`
(`Game1.Rendering.cs:2263`) now calls it, and logs the resolution once per warp — the objective evidence
that the glimpse aims at a real sector:

```
[glimpse] warp #2153 'Alderan' in Omega (1053) -> Alderan (1052) via partner warp #2152
[glimpse] warp #2152 'Omega' in Alderan (1052) -> Omega (1053) via partner warp #2153
[glimpse] warp #2185 'Plato' in Alderan (1052) -> Plato (1060) via partner warp #2184
```

### 2.2 The destination sky is the SECTOR's sky (shared), and the warm-up prepares it

The glimpse used to construct a **private** sky renderer per destination — its own `PlanetPoster`
(16.7 MB per 2048² planet), its own dome/environment textures, and for the black tier its own starfields,
lens-flare art, moon/galaxy/distant-object renderers. That is why P862 measured "a destination sky per
glimpsed aleph": every one of them re-decoded art the sector already had. Now the glimpse renders through
the **same tier renderer the sector draws with** (`Game1.ActiveSkyTier`, `Game1.Lifecycle.cs`), switching
its cluster to the destination, rendering the 256² target, and restoring the current sector in a
`finally` (the sky latch in `DrawSkyP576` would not re-set it otherwise). Consequences:

* the sector you just left was the current sector one frame ago, so **its glimpse sky costs one 256²
  pass and no decode at all** — the "reuse rather than re-decode" the task asked for, and the renderers
  *do* allow it once they are shared;
* the target cache is bounded (LRU, `MaxCachedTargets = 8` × 256² colour+depth ≈ 2 MB);
* `Draw` is now a two-pass frame: prepare the targets (all render-target switches), restore the shared
  sky, then composite — the P582b rule (no RT switches mid-composite) kept intact.

P862's warm-up drives the preparation: `SectorWarmupPlanner.GlimpseSky(sector)` is a new
`WarmAssetKind.GlimpseSky` item, enqueued by `Game1.ScanAlephApproach` for **the sector being left** (the
one the arrival glimpse looks back at) as soon as the own ship is on approach. It runs under the same
`WarmupBudget` as every other item and is off with `--aleph-glimpse off` or
`ALLEGIANCE_SECTOR_PREWARM=0`. It renders the target along the **crossing direction** (own ship → the
aleph that leads there), which is what a glimpse is: a peek at the sky as seen *looking along the transit*
— so the cached image is the one the arrival glance would produce, not an arbitrary view.

### 2.3 One near-distance guard the fix needs

With the destination resolving for the first time, the arrival frame exposed a degenerate case the
feature never met: the ship comes out of a warp **~12 u** from the partner aleph (measured:
`[sector-warm] aleph approach -> Omega (1053): … (11 u to the aleph)` on the frame after the transit), where
the disc half-size is `warp.Radius × 1.3` (tens of times the distance), the quad straddles the near plane
and the composite covers the viewport with a 55–92 % destination-sky wash. A glimpse whose disc the camera
is *inside* is skipped (`dist <= Warp.Radius * radiusFactor`), which only ever removes that degenerate
frame — the fade does not even start until 3400 u.

### 2.4 The flare occlusion test, deferred by one frame

`Rendering/FlareOcclusion.cs` (new) holds the probe; `Game1.TransitFlareOccluded` keeps the rule and only
moves *when* the depth is read:

* **Deferred (default)** — `DrawFlare` asks the probe for a verdict and queues this frame's pixel +
  the aleph's normalized view depth; at the **start of the next frame, before the depth pass re-renders
  `DepthRT`**, `SampleDeferred()` reads the still-present previous-frame depth in **one batched
  readback** (a rectangle covering all samples when they are within 64×64 px, else the first sample only)
  and stores the verdict per warp. The pixel and the depth compared against it come from the *same*
  frame, so the test is exact — it is the *decision* that is one frame late.
* A warp is re-tested at most every `RefreshFrames = 6` frames (~10 Hz at 60 fps, staggered by warp id),
  with an immediate first sample when a pulse starts; a verdict older than `MaxVerdictAgeFrames = 30`
  is forgotten (a stale "occluded" can never hide a flare indefinitely).
* **`ALLEGIANCE_FLARE_OCCLUSION=blocking`** restores the P585 in-frame readback (the A/B arm);
  **`=off`** skips the test entirely (the measurement floor). Default deferred; the mode is logged at
  startup and every readback second is logged:

```
[client] transit flare occlusion: deferred — last frame's linear depth, read before this frame's depth pass (P862b)
[flare-occlusion] 61 flare sample(s) in the last second via 61 readback(s), 134.7 ms total, worst 4.61 ms (deferred 1 frame, depth 1280x720, 1 verdict(s) cached)
```

Other small things in the same pass: the glimpse composites with ONE reused `BasicEffect` and one reused
vertex array (it used to construct a new `BasicEffect` — i.e. a new GL program — per glimpse per frame),
and `LoadCounters` gained `glimpse sky +N` so the `[sector-entry]` line accounts for glimpse targets like
any other asset (that is the line that proves the pre-warm).

---

## 3. Measure first — the harness

Unchanged: P862's `scripts/p862-warp-stall.sh` on `m2xspiderw.igc` (Omega → Alderan, real windowed
client, `--auto-goto`, `--park Alderan` + MCP bots so the client knows the destination sector, P643
per-frame CSV, `ALLEGIANCE_NOVSYNC=1 ALLEGIANCE_FRAME_CAP=60`, 1280×720). Two arms are used:

* **standard** — `--park Alderan --bots 'spawn_scenario {"preset":"showcase","size_scale":1}' --bots-after 10`
  (the P862 A/B scenario: the arrival frame).
* **flare** — plus `--bots 'spawn_scenario {"size":15,"target_sector":"Omega","staging":false}' --bots-after 5`
  (P607's scenario): enemy bots keep crossing the gate the pilot is flying at, so the transit pulse and
  its lens flare actually draw and the occlusion probe is exercised.

```bash
# before arm (master), glimpse on (the default)
scripts/p862-warp-stall.sh before-1 --exit-after 95 --park Alderan \
  --bots 'spawn_scenario {"preset":"showcase","size_scale":1}' --bots-after 10
# after arm (this tree) — same command, different label
scripts/p862-warp-stall.sh after-1  --exit-after 95 --park Alderan \
  --bots 'spawn_scenario {"preset":"showcase","size_scale":1}' --bots-after 10
# flare-readback arms (same binary, one env var)
ALLEGIANCE_FLARE_OCCLUSION=blocking scripts/p862-warp-stall.sh blocking-1 --exit-after 95 …
ALLEGIANCE_FLARE_OCCLUSION=off      scripts/p862-warp-stall.sh off-1      --exit-after 95 …
```

Both arms' artifacts are in `~/allegiance-evidence/P862B/<arm>/` (`client.out`, `perf.csv`,
`summary.json`). The `/tmp`-free scratch rule was followed (`.scratch/`, removed at the end apart from
what this report links to); the worktree's `.scratch/` is git-ignored.

---

## 4. Results

### 4.1 The arrival frame — before vs after (standard scenario, glimpse ON, 3+1 runs each)

"entry" is the frame the own ship's sector changed on (`[sector-entry]`); "+1" is the next rendered frame
(the one that pays for anything deferred into the arrival); the last columns are the first 30 frames in
the new sector. Zero assets are created in the entry frames in every run of both arms (P862's fix), so
what is being compared here is the *glimpse*'s cost and the residual frame.

| run | glimpse targets resident before entry | entry frame | +1 frame (GPU / allocated) | 30 frames in the new sector |
|---|---|---|---|---|
| **before** 1 | — (glimpse unresolved: it drew nothing) | 17.3 ms | 27.0 ms (3.6 ms / 2.2 MB) | 0 assets, 10.9 MB, GC 0/0/0 |
| **before** 2 | — | 17.5 ms | **34.9 ms** (11.3 ms / 4.1 MB) | 0 assets, 13.4 MB, GC 0/0/0 |
| **before** 3 | — | 17.6 ms | 24.8 ms (3.8 ms / 2.2 MB) | 0 assets, 10.8 MB, GC 0/0/0 |
| **after** 1 | **2** | 17.7 ms | 28.8 ms (5.3 ms / 3.3 MB) | 0 assets, **glimpse sky +0**, 11.2 MB, GC 0/0/0 |
| **after** 2 | **2** | 17.6 ms | 34.4 ms (10.7 ms / 4.7 MB) | 0 assets, **glimpse sky +0**, 14.3 MB, GC 1/0/0 |
| **after** 3 | **2** | 17.3 ms | 36.6 ms (12.2 ms / 5.0 MB) | 0 assets, **glimpse sky +0**, 14.9 MB, GC 1/1/0 |
| *after, final tree* | **2** | 17.4 ms | 28.1 ms (4.8 ms / 3.3 MB) | 0 assets, **glimpse sky +0**, 11.4 MB, GC 0/0/0 |

Reading it honestly:

* **The glimpse now exists and costs the arrival frame nothing.** Before the fix the destination never
  resolved, so the numbers in the "before" rows are the cost of a feature that *was not running*; after
  the fix a glimpse is actually drawn (it shows the correct destination sky, §7) and still reports
  `glimpse sky +0` for the entry frames: the two targets (the sector being approached and the sector
  being left — the one the arrival glance looks back at) were rendered during the approach, under the
  2 ms/frame warm budget, exactly the outcome P862's failed attempt was after. **P862's failed pre-warm
  was lead 1 and nothing else.**
* **There is no ~1 s arrival frame here at all** — not before and not after. On this host today the
  worst frame in the ±5 s window around the warp is 24.8–39 ms. P862 §2.4/§10 measured 1015–1118 ms
  with 990–1057 ms of *GPU* time on the contended machine; the same probe here measures 3.6–13.5 ms
  (§5), so this host cannot reproduce that residual and the numbers above must not be read as "the
  residual is gone".
* The **+1 frame's 10–14 ms GPU / 3–5 MB** is the transit-pulse frame (see §6), present with the same
  size in the before arm, so it is neither caused nor removed by this task.
* Both arms' rows are from the shipping tree's rendering path; the two edits that landed after them (the
  dome-clearing branch in the two sky tiers, and the probe's frame-stamp reset) cannot move them — this
  scenario never flares (no `[flare-occlusion]` line, the probe never runs) and every sector of the map
  declares a poster — and the final-tree confirmation run reproduced them (§9).

### 4.2 Assets and allocations in the arrival window

Every run of both arms: **0** rock/station/hull meshes or textures, **0** planet posters, **0** HD
materials created in the entry frames (`[sector-entry] … first 30 frames: rock 0M/0T, station 0M/0T,
ship 0M/0T, planet +0, HD +0`), and now **glimpse sky +0** as well. Total managed allocation for the 30
frames after the change is 10.8–14.9 MB in both arms. The warm-up itself still does the sector's real
work during the approach (Alderan: 3 rock textures + the planet, one per frame:
`[sector-warm]   rock bgrnd05: 38.1 ms (new)`, …).

### 4.3 The flare occlusion readback (flare scenario, 3+1 runs per arm)

The flare only draws when a pulse is on and the funnel centre is on screen, so the probe is exercised
only in the "flare" scenario (a bot transit wave). `[flare-occlusion]` logs one line per active second
(samples, readbacks, total/worst ms); the peak second of each run:

Artifacts: `~/allegiance-evidence/P862B/{blocking-1..3,deferred-f1..3,off-1..2}/`. Each run's peak
(busiest) second while a bot transit wave kept the flare up:

| arm | entry frame | +1 frame (GPU / allocated) | peak second: samples / readbacks | peak second: readback time (worst call) |
|---|---|---|---|---|
| **blocking** (P585 in-frame readback) | 17.6–17.8 ms | 33.2–36.1 ms (10.8–12.1 ms / 3.4–3.9 MB) | 61 / 61 | **137.7 / 133.4 / 134.1 ms/s, worst 5.4–5.9 ms** |
| **deferred** (this task, final tree) | 17.4–17.6 ms | 32.5–36.5 ms (10.1–13.0 ms / 3.8–3.9 MB) | **7–8 / 7–8** | **18.7 / 13.7 / 15.5 ms/s, worst 2.5–3.3 ms** |
| **off** (no occlusion test) | 17.2–17.6 ms | 32.5–37.0 ms (10.5–13.9 ms / 3.7–3.9 MB) | 0 / 0 | — |

What that says, plainly:

* **The readback costs 2.5–3.3 ms per call** and is a **per-call GL driver cost**, not a wait for the
  GPU: the same 1×1 read from the *previous* frame's depth costs the same as the in-frame one (measured
  before the refresh throttle landed: 61 calls/s in both arms, 123–138 ms/s). So "read a frame late" on
  its own makes the read harmless to the pipeline but not free — the win has to come from calling it
  less. Hence the refresh cadence: each flaring warp is re-tested at ~10 Hz instead of ~60 Hz.
* **Net effect on the same scenario: 61 → 7–8 readbacks per second, 133–138 → 14–19 ms per second**
  (≈0.3 ms per frame while a transit wave is on, and 0 when nothing flares — which is most of a match).
  The arrival frame itself is unchanged in all three arms (entry 17.2–17.8 ms; the +1 frame is the pulse
  frame of §6), so nothing here is being bought at the arrival frame's expense.
* **Bounded by construction**: at most **one batched readback per frame** (the old code could issue one
  per flaring aleph — up to 6 — every frame), a verdict older than 30 frames is discarded rather than
  reused, and a warp with no verdict at all is treated as **visible** (the P585 failure mode: never hide
  a flare because the test could not run).
* **Behaviour is preserved.** Same target (the P547 linear depth), same normalization
  (`LinearDepth.Normalize(1, 6000)`), same margin (`0.005`), same pixel (bottom-left-origin flip), and the
  pixel and the depth it is compared against still come from the same frame. The only differences are one
  frame of latency and a verdict at most ~100 ms old. The remaining ~0.3 ms/frame is the price of an exact
  test on this driver; the alternative the task allows — CPU-side geometry (ray vs the opaque objects'
  bounds) — would remove it but replace the exact depth comparison with an approximation at silhouettes,
  so it is recorded as the next option rather than taken here.

---

## 5. The P862 §2.5 caveat, this time in reverse

P862 measured ~1 s per first-use texture on this workstation (`fig05ops1bmp` 963 ms, `nebplnt06bmp`
982 ms) and every absolute millisecond in that report is dominated by that driver stall. **Today the
same box loads the same assets in 25–45 ms** (`[sector-warm] sky nebplnt06bmp ('globe3'): 42.9 ms (new)`,
`rock bgrnd05: 38.1 ms (new)`, `[asset] planet poster 'nebplnt43bmp' 2048x2048 loaded in 45.4 ms`) and
the per-call depth readback is a flat ~2.2–2.5 ms. So:

* the load average was 3–8 on the shared 24-CPU host while these runs were taken, and no other lane's
  build was running against this worktree; but the GPU was **not** contended the way it was during P862;
* consequently **this host cannot re-measure P862's residual stall either way** — neither to confirm it
  nor to show that it is gone. That measurement belongs on the rig/mini (P862 §8's recipe, unchanged:
  watch for `[glimpse]` and for `glimpse sky +0` in the `[sector-entry]` line);
* the transferable numbers are the *counts* (assets created, glimpse targets created, readback calls per
  second, verdicts) and the frame-time profile with that caveat stated, exactly as P862 §4 argued.

---

## 6. What is left in the arrival frame, named

The one frame that is consistently more expensive than a normal flight frame is the frame **after** the
sector change (`+1`: 24.8–38.8 ms, GPU 3.6–13.9 ms, 2.2–5.0 MB). It is not this task's, and the lines say
so:

* `[sector-entry] frame N (+1 after the change): loads rock 0M/0T, station 0M/0T, ship 0M/0T, planet +0,
  HD +0, glimpse sky +0, alloc 3358 KB` — **nothing was loaded** (no `[asset]` line either), and no
  glimpse target was created;
* it is present with the same size in the **before** arm (master, glimpse dead: 24.8–34.9 ms, GPU
  3.6–11.3 ms) and in the arm with the **glimpse off** (32.5–37.0 ms, GPU 10.5–13.9 ms), and the flare
  test being off does not change it (32.5–37.0 ms with `ALLEGIANCE_FLARE_OCCLUSION=off`);
* it is the **P585 transit-pulse frame** — the opt-in funnel swell + transit particles that fire on the
  player's own arrival (`DetectTransitSwells` → `WarpPulseController.Trigger`, `[transit-pulse] warp #2152
  ARRIVAL pulse (sector change)`), which is a deliberate non-native flourish
  (native has nothing there at all: `WinTrekClient::ShipWarped`, `WinTrek/trekigc.cpp:2976-2991`, is an
  empty commented-out body; the only native arrival visual is the star-core swell,
  `effect/alephgeo.cpp:158-208`).

Nothing else is left: the sector's own assets were already pre-warmed by P862 (0 created), the
destination sky is prepared during the approach (0 created), the flare's occlusion read is off the
frame (≤1 batched read, one frame late), and the near/far HUD work is unchanged. **Off-render-thread
decode (P862 §10's "modern approach") stays out of scope**: after 1–3 there is no measured asset-decode
cost left in the arrival window to justify it.

---

## 7. Frames (`docs/reports/P862B_assets/`, 1.1 MB, every file < 1 MB)

Real 1280×720 presented-backbuffer frames from the scripted flight (`--shots`,
`ALLEGIANCE_SECTOR_SHOTS`), plus the glimpse's own cached targets dumped with the new
`ALLEGIANCE_GLIMPSE_DUMP=<dir>` proof aid (inert unless set). All were taken with `--sky native` so the
destination sky is a full nebula dome + planet (the default `black` tier's glimpse is a starfield, which
is honest but visually thin). I opened every file and describe what is visible:

| file | what I see |
|---|---|
| `p862b_classic_before_glimpse.png` | Classic HUD, 1790 m from the Alderan aleph, **`--aleph-glimpse off`** (what master produced, since its glimpse never resolved): black sky with a few stars, the aleph is a small dark swirl under the target brackets, the gauge rings and "YOU: Omega" below. No destination sky anywhere. |
| `p862b_classic_after_glimpse.png` | The same moment with the glimpse ON: the funnel is now filled with a **bright blue nebula** (Alderan's `globe3` dome) that spills around the aleph, with the aleph's bright core in the middle and stars across the disc. Everything else (ship, gauges, "Alderan 1784 m") is unchanged — this frame is a **resting** glimpse (no pulse was active: the run's log has no transit signal in Omega at that second), so the delta is the glimpse alone. |
| `p862b_modern_before_glimpse.png` | The Modern HUD at 1798 m, glimpse off: Omega's own nebula in the top-right corner, the aleph a faint dark swirl under the target box, no destination sky. |
| `p862b_modern_after_glimpse.png` | The Modern HUD at 1767 m, glimpse on: the same funnel now carries a faint blue-haize disc and a visibly brighter core (the resting glimpse at ~30 % fade, ≈17 % opacity at this range — the Modern run did not catch a transit pulse, so the effect reads much subtler than the Classic pair; the same disc is what the Classic frame shows at full strength). |
| `p862b_glimpse_funnel_compare.png` | Four 2× crops of the funnel region: Classic off (black, dark swirl) / Classic on (blue nebula disc) / Modern off (dark) / Modern on (brighter swirl + haze). This is the before/after at a glance. |
| `p862b_glimpse_targets.png` | The three **destination sky targets the glimpse prepared**, dumped from the render cache: `Alderan (1052)` (dark starfield — Alderan's dome is dark in the crossing direction), `Omega (1053)` (the bright multi-colour nebula dome), `Plato (1060)` (starfield). This is the direct proof that the cache is keyed by the **resolved sector** and that the peek at the Omega aleph really is Alderan's sky and the peek at the Alderan aleph really is Omega's. |

The arrival frames (`sector_1052_after0/after30`, plus the black-tier pairs) are in
`~/allegiance-evidence/P862B/{native-classic-on,native-classic-off,native-modern-on,native-modern-off,shots-classic,shots-modern,…}/`;
they show the settled sector with **no** screen-covering sky wash — the near-distance guard of §2.3 is
what keeps the ~12 u back-aleph from projecting a full-viewport glimpse on the arrival frame.

**New media:** six PNGs, generated by the local harness (no Meshy content, no external art). They are
unreviewed — Domingo must eyeball them before a push (`scripts/publish-guard.sh` fails on new media
unless `ALLOW_MEDIA=1`).

---

## 8. Native-intent classification and the modern/better approach

**Native intent (cited, re-grepped).**

* A warp's destination is its **partner warp**, and the destination sector is that partner's cluster:
  `Igc/warpIGC.h:129-143` (`GetDestination()` → `GetMyMission()->GetWarp(m_warpDef.destinationID)`),
  used exactly that way by the server transit (`FedSrv/fsship.cpp:481-483`,
  `CFSShip::ShipStatusWarped`) and by the client (`clintlib/appmsg.cpp:867`). P862b makes the glimpse
  use the same rule as everything else in the port (`WarpDestinationResolver`, P540c) — the glimpse was
  the last consumer still treating a warp id as a sector id.
* There **is no native destination glimpse and no native flare**: the only native transit visual is the
  arrival aleph's star-core swell (`effect/alephgeo.cpp:158-208`), and `WinTrekClient::ShipWarped`
  (`WinTrek/trekigc.cpp:2976-2991`) is an empty commented-out body. So P576-B/P585/P862b are a
  deliberately non-native flourish on top of the native sky port; the native rules they must respect are
  the ones they reuse — the per-cluster sky (`clusterIGC.cpp` posters/environment, `trekigc.cpp:243-248`
  poster placement) and the transit attribution the server already does.
* The occlusion test has no native counterpart either (P547's linear depth is a port device for soft
  particles). Keeping it exact (same depth target, same margin) rather than approximating it with CPU
  geometry is the native-faithful choice: it hides the flare exactly when the geometry the player sees is
  in front of it.

**Modern/better approach.** The glimpse now shares the sector's sky renderer, which is the big structural
win: a peek at another sector costs one 256² pass and **zero** decodes, and the warm-up can therefore
afford to prepare "the sky I will look back at" during the approach instead of inside the arrival frame.
The remaining genuinely modern steps, in order of value:

1. **The depth readback is a per-call GL cost (~2.5–3.3 ms) on this driver**, now amortised to ~10 Hz
   per flaring warp (14–19 ms per active second). If that last cost matters, two options sit on the
   probe's existing seam: (a) sample even less often (the flare is a 0.9 s envelope; 4–5 Hz would still
   be invisible) — a one-constant change; (b) the CPU-side geometry test (ray vs the opaque draws'
   bounds, no readback at all), which trades exactness at silhouettes for a free test.
2. **Off-render-thread decode** (P862 §10) is not needed for the paths measured here — the arrival
   window creates no assets — but it remains the right answer for a *cold, unannounced* sector entry.

---

## 9. Verification (light gate)

* `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
* `tools/build-tools.sh` — **all 90 tool projects built**, 0 warnings / 0 errors.
* New tests `tests/Allegiance.ClientCore.MonoGame.Tests/P862bGlimpseDestinationTests.cs` — **24 tests**
  in three groups: (1) `P862bGlimpseDestinationTests` — the resolution rule, including the failing-first
  case the task asked for (*a warp whose partner id ≠ its destination sector id glimpses the partner's
  sector*: a decoy cluster is given the partner **warp** id, so a warp-id-as-sector lookup picks the
  decoy while the correct resolution picks Alderan), agreement with `WarpDestinationResolver`, dangling
  partners, unknown sectors, current-sector filtering, distance, two alephs sharing one destination;
  (2) `P862bGlimpseWarmupTests` — the `GlimpseSky` warm item (one per sector, distinct from the sector's
  own sky item, promoted with the approached sector, never re-issued); (3) `P862bFlareOcclusionTests` —
  the deferred scheme driven by a fake depth source: one-frame-late verdicts, refresh cadence, the
  one-readback-per-frame cap, batching, pixel mapping with the Y flip, the occlusion margin, no-depth and
  out-of-range handling, mode parsing, counters. **3× alone: 24/24, 24/24, 24/24.**
* ONE `tools/run-tests.sh --tier fast` (final tree) — **green apart from a ledgered live-timing flake**:

```
Discovery counts: Lobby 87 fast / Update 28 / MonoGame 3657 / ClientCore 1920 / Core 5594
Fast infrastructure checks: PASS
fast: 11286 tests, 311.6s, FAIL
  Allegiance.Update.Tests          28 passed
  Allegiance.Lobby.Tests           87 passed
  Allegiance.ClientCore.MonoGame.Tests 3654 passed, 3 not executed (documented skips)
  Allegiance.ClientCore.Tests      1917 passed, 1 failed (+2 quarantine)
  Allegiance.Core.Tests            5584 passed (+10 quarantine)
```

  The only failure is `P660NextMatchLiveTests.Two_consecutive_matches_deliver_both_players_results_and_reseat_every_peer`
  ("loopback condition timed out", `Pump` line 49 / round wait at :201 — the live next-match timing test),
  already ledgered in `docs/TEST_FLAKES.md` (`2026-10-28`, twice) and **3/3 alone** here (2 s each). P862b
  touches no server, session or wire path. Two earlier sweeps on the same tree (before the last two
  edits) hit two more out-of-scope ledgered flakes — `SwapPartTests.SwapPart_swaps_two_occupied_slots_sectorwide_moves_and_silent_noop`
  (now recorded, 3/3 alone) and `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`
  (`2026-10-24`/`2026-10-27`, 3/3 alone), plus one sweep whose `Core.Tests` half died without a TRX under
  a host load average of 7 (`No TRX generated`). Same pattern each time: a different load-sensitive live
  test per sweep, every one green alone, all suites of this task's surface green.
* Showcase never-parks
  (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`)
  **5× alone: PASS** each time — 3 m 12 s, 3 m 23 s, 3 m 22 s, 3 m 36 s, 4 m 21 s (load average 3–8).
* The A/B itself (§4) is the performance gate: 3 runs per arm, one env var (`ALLEGIANCE_FLARE_OCCLUSION`)
  or one commit apart, plus a final confirmation run of each arm on the shipping tree.
* Client smoke: the harness runs themselves — the client flies, warps, the glimpse resolves
  (`[glimpse]` lines), the flare fires and is occlusion-tested (`[flare-occlusion]` lines), no exception,
  no visual artefact on the arrival frame.
* One last edit landed after the measurement arms and was re-verified the same way: the two dome tiers
  clear their active dome when a cluster declares **no** poster (`NativeSkyRenderer.SetCluster`,
  `HdSkyRenderer.SetCluster`, native `Igc/clusterIGC.cpp:41-42`). It is needed because one sky renderer
  is now shared between the current sector and the glimpse's destinations — without it, glimpsing a
  poster-less sector would keep drawing the previous sector's nebula. It cannot affect anything measured
  here: every sector of `m2xspiderw.igc` declares a poster (`tools/missiondump`). After it, the build is
  0/0 and the 24 new tests are 24/24 ×3. A later one-line fix in the probe (the frame stamp had to be
  reset after each readback, otherwise every verdict aged out and the refresh throttle never engaged) was
  re-measured with 3 fresh deferred runs — those are the 7–8 readbacks/s numbers in §4.3 — and re-gated.
  A final smoke run reproduced the arrival accounting
  (`[sector-entry] … entry frame 22.3 ms; … resident before entry: … glimpse sky +2`) —
  `~/allegiance-evidence/P862B/smoke-final/` (frame times there are not comparable: another lane was
  loading the shared host, p50 22.7 ms instead of 16.7 ms).

---

## 10. Files

New:
* `src/Allegiance.ClientCore.Game/Rendering/FlareOcclusion.cs` — the deferred occlusion probe
  (mode switch, batching, refresh cadence, counters, `[flare-occlusion]` line).
* `tests/Allegiance.ClientCore.MonoGame.Tests/P862bGlimpseDestinationTests.cs` — 24 tests.
* `docs/reports/P862B_assets/*.png` — the six frames/targets listed in §7.

Changed:
* `src/Allegiance.ClientCore.Game/Rendering/AlephGlimpseRenderer.cs` — `GlimpseTarget` +
  `CollectTargets` (resolution through `WarpDestinationResolver`), shared sky-tier rendering
  (`Func<ISkyRenderer?>`, restore in `finally`), LRU-capped target cache keyed by destination sector,
  `Warm`, the near-distance guard, reused `BasicEffect`/vertex arrays, `ALLEGIANCE_GLIMPSE_DUMP` proof aid.
* `src/Allegiance.ClientCore.Game/Rendering/SectorWarmup.cs` — `WarmAssetKind.GlimpseSky` +
  `SectorWarmupPlanner.GlimpseSky`.
* `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — `DrawAlephGlimpses` uses `CollectTargets` and logs
  `[glimpse]`; `TransitFlareOccluded` delegates to the probe (new signature incl. the warp id);
  `ReadSceneDepthForFlare`, `ActiveSkyTier`, the probe's per-frame `BeginFrame` / `SampleDeferred` /
  `Report` hooks around the depth pass.
* `src/Allegiance.ClientCore.Game/Game1.SectorLoad.cs` — `LoadCounters.GlimpseTargets` (`glimpse sky +N`
  in every `[sector-entry]` line), `EnqueueGlimpseSky` on the aleph approach, the `GlimpseSky` warm
  case (travel direction through the gate), the `sector_*_pre2` departure frame at ≤700 u.
* `src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs` — constructs the probe (mode from
  `ALLEGIANCE_FLARE_OCCLUSION`, logged) and the glimpse renderer with the shared sky tier.
* `src/Allegiance.ClientCore.Game/Game1.cs` — the two new fields (`_flareOcclusion`, `_glimpseLogged`).
* `src/Allegiance.ClientCore.Game/Rendering/NativeSkyRenderer.cs`, `HdSkyRenderer.cs` — clear the active
  dome for a cluster that declares no poster (needed now that one renderer serves the sector and the
  glimpse's destinations).
* `GLOSSARY.md` — the "Aleph destination glimpse (P576-B/P585/P862b)" entry.
* `docs/TEST_FLAKES.md` — the out-of-scope fast-tier failure recorded (3/3 alone).

No server, simulation, wire, parity-CSV or Docker change (client rendering only).

---

## 11. Caveats / unfinished

* **This host cannot reproduce P862's residual ~1 s arrival frame** (§5): every first-use texture costs
  25–45 ms here today instead of ~1 s, so "the residual is gone" is *not* what §4 shows — what it shows
  is that the glimpse no longer *adds* to the arrival frame and that nothing is loaded there. Re-measure
  on the rig/mini with P862 §8's recipe and the new `[glimpse]` / `glimpse sky +N` lines.
* **The flare readback is not free on this driver**: ~2.5–3.3 ms per call. Deferring it removes the
  *pipeline stall* and the per-frame cap bounds the work, and the 10 Hz refresh cuts the calls from 61/s
  to 7–8/s (14–19 ms per active second, ~0.3 ms/frame); it does not make a call cheaper. The
  CPU-geometry alternative (§8) would remove the last of it and is deliberately not taken here (it
  changes the exactness of the test).
* **One frame of latency is accepted** for the flare's occlusion verdict, plus up to ~100 ms of verdict
  age between refreshes (a pulse lasts ~0.9 s). Nothing else depends on it.
* The glimpse's cached image is rendered once per destination, along the **crossing direction** it was
  warmed with; a very sharp turn right at the gate would show the destination sky rotated a little
  (unchanged from P585's design, which was always a one-shot cache).
* A glimpse for a destination whose art the client has *never* had resident (an aleph in the arrival
  sector pulsing toward a sector never visited) still decodes that sky at that moment — measurable as
  `glimpse sky +1` inside the arrival window (seen in two of the three flare runs, 0 in every standard
  run). It is bounded (one 256² pass + the destination's own art, which that sector would need on entry
  anyway) and it is not the arrival glimpse the task is about; warming every destination of a new sector
  is the obvious follow-up if a live run shows it.
* `ALLEGIANCE_FLARE_OCCLUSION` and `ALLEGIANCE_GLIMPSE_DUMP` are proof/measurement aids (default
  deferred / off), like P862's `ALLEGIANCE_SECTOR_SHOTS` and `ALLEGIANCE_ASSET_LOG_MS`.
