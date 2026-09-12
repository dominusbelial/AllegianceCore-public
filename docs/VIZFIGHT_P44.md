# P44 — 3D renderer for the live-combat spectator feed

Task: turn P43's JSON stream (`ws://127.0.0.1:7717/ws`, one `tick` frame per
`Server.Tick()` pass at 20/s, plus a `kill` frame per real death) into a
watchable Three.js scene. Ships move, a kill lands, in 3D, matching the game's
real space. Single static HTML file — no build tooling, no bundler.

Branch: `master` (top commit `e9a4e78` P43 at start; this adds P44 on top).
Date: 2026-09-01.

---

## Files changed (scope)

```
tools/vizfight/wwwroot/index.html   (modified — real Three.js renderer replaces P43's placeholder)
tools/vizfight/Program.cs           (modified — +1 line: UseDefaultFiles so "/" serves the page)
VIZFIGHT_P44.md                     (this file)
```

Nothing else touched. No build step introduced. `bin/`/`obj/` are gitignored as
before. The helper driver scripts used for browser verification were removed
after use (evidence preserved under `/tmp/p44-evidence/` — see Verification).

---

## What the renderer does (and how)

Single page, `index.html`, one `<script type="module">` block. Three.js loaded
via **importmap**, pinned — not `latest`:

```html
<script type="importmap">
{ "imports": {
    "three":         "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js",
    "three/addons/": "https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/" } }
</script>
<script type="module">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
…
```
`three@0.170.0` == release r170 (Sep 2024, recent stable). jsDelivr is used so
versions are unambiguous (npm-semver `@0.170.0` pins exactly that tag; a bare
`latest` is never used). Both URLs verified live (HTTP 200) before pinning.

- **Axis mapping — direct, no remap.** Confirmed from the ported
  `Orientation` (`src/Allegiance.Core/Sim/Orientation.cs:60` `GetForward()`
  returns `-row2`, identity ⇒ `(0,0,-1)`) that server space is
  X-lateral / Y-up / Z-depth with forward = −Z, and three.js is the same
  (cameras look down −Z, world Y is up). So `pos:[x,y,z]` →
  `new THREE.Vector3(x,y,z)` and `fwd` maps 1:1. Stated explicitly: **no
  projection, no axis-drop — server coords are used verbatim as three.js
  world coords.**
- **Mesh/orientation:** one `THREE.ConeGeometry` (radius 18, length 62) per
  ship id, spawned on first sight. A cone's native nose is +Y, so orientation
  is `mesh.quaternion.setFromUnitVectors(UP, fwd.normalized())` — clean, no
  `lookAt` needed. Colored by `side`: side 0 = cyan `0x00c8ff`, side 1 = red
  `0xff5050`, plus a modest emissive so side colors read clearly under
  lighting.
- **Despawn policy (documented):** a mesh is removed only after its death
  animation completes (~1.4 s, driven by the `kill` frame). If a ship turns
  `alive:false` with no `kill` frame, or disappears from a tick entirely, it's
  flashed white and removed after a 5 s grace so the death anim normally wins.
  Explicit despawn does not exist in the protocol, so this is the late-
  `alive:false` path. Implemented via `dying` flag: positive → real death anim;
  negative → disappearance countdown.
- **Kill treatment:** on `kill`, the victim's cone pops +0.35×, flashes
  emissive white, then scales down + fades over ~1.4 s and is disposed (also
  frees material/geometry). Mirrored in the DOM `#kills` log.
- **Health:** a camera-facing `THREE.Sprite` health bar above each ship whose
  width ∝ `hp`, plus a secondary color bleed toward dark red as `hp` drops.
- **Starfield + lighting:** 480 point-sprites on a distant sphere shell
  (~4200–5100 units) for spatial anchor; an `AmbientLight`+`DirectionalLight`
  pair for form; a floor grid at y=0 and a small axes helper for orientation.
- **Camera:** `OrbitControls` (drag orbit / wheel zoom / right-drag pan).
  Fixed start: `position (648,610,860)`, `target (648,0,-90)` — derived from
  livecombat's deterministic geometry (`TargetZ=-180`, `LaneSpacing=432`,
  `Pairs=4` → lanes span x∈[0,1296], z∈[-180,0]). Keeps the fight framed at
  t=0. No auto-follow (nice-to-have, not required).
- **Reconnect:** `setTimeout(reconnect, 2500)` on `close`; stats show connect
  count so reconnects are visible. No backoff/backfill (simple is enough).
- A small `window.__positions` hook exposes live mesh positions purely for
  scripted verification (not used by the render path).

Backend change: added `app.UseDefaultFiles()` to `tools/vizfight/Program.cs`
because P43 never mapped `/` → `wwwroot/index.html` returned 404 while
`/index.html` served 200 — the page P43 advertised at `http://127.0.0.1:7717/`
would not have loaded. The single `Program.cs` line makes the advertised URL
work. No other backend logic touched.

---

## Run it

```
dotnet run --project tools/vizfight -c Release
```
then open `http://127.0.0.1:7717/` in a browser. Requires the stock artwork
tree at `/home/domingo/Development/Artwork` (same as P43/livecombat).

---

## Honest verification statement

**This session drove a real browser (headless Chromium via CDP) and confirmed
end-to-end behavior numerically.** The one thing I could not do is *visually*
eyeball a screenshot: the auxiliary vision model (`vision_analyze`) returned
404 (model `gemma4:26b` not found on the Ollama backend), so no image could be
interpreted by a model. Instead I verified with the page's live DOM/state and
pixel statistics — see below — and deliberately left that gap explicit.

Confirmation obtained from an actual headless Chrome running the page against
a live `vizfight` backend (four separate clean runs):

1. **WS connects; 8 ships spawn at the exact livecombat geometry.** First poll
   showed `ships: 8`, `conn: 1`, and per-ship world positions matching the
   deterministic lanes:
   ```
   side0 8096@(0,0,-11)   side1 8097@(0,0,-180)
   side0 8098@(432,0,-11) side1 8099@(432,0,-180)
   side0 8100@(864,0,-11) side1 8101@(864,0,-180)
   side0 8102@(1296,0,-11) side1 8103@(1296,0,-180)
   ```
   (y=0 everywhere; x at `lane*432`; targets at z=−180.) This confirms the
   1:1 axis map puts every ship where the game puts it.
2. **Ships visibly move tick-over-tick.** Sampling positions over a run, the
   four shooters translate monotonically down −Z at real time:
   `-11 → -197.5 → -527.8 → -946.2 → -1417.5 → -1920.8 → …`, while the four
   targets hold z=−180 until they die. Smooth continuous motion, not teleport.
3. **Kills land and the death treatment runs.** Four `kill` frames, all four
   targets, same sim-time (`@ t=3.6`/`3.8` depending on run). `ships` fell
   8 → 4 across the run as the killed targets' death-anim completed and their
   meshes were removed, and the `#kills` panel populated with all four lines.
4. **No console errors, no network failures** across every clean run
   (`CONSOLE/EXCEPTIONS: (none)`, `NETWORK FAILURES: (none)`).
5. **Pixels confirm the cones actually rasterize.** Post-tuning, the center
   region of the t=0 screenshot held ~2270 cyan and ~1562 red pixels (the 
   untouched legend/HUD corners aside), proving colored cone silhouettes are
   on screen — not just simulated state. Initial framing (camera ~1400 units
   out, radius-9 cones) had produced almost no detectable ship pixels, so I
   tuned camera+cone size for a legible first ~5 s of the fight, re-ran, and
   got the thousands-of-pixels result.

Capture cadence sanity: sim clock vs wall clock tracked ~1:1 (sim 3.5 → 83
over ~78 s wall), confirming the backend's real pacing is intact and the
renderer keeps up.

Bottom line: ships spawn at correct places, move, a kill is visibly handled,
health/fade/logic all execute without a single JS error — but I did not have a
working vision model to look at the final beauty shot. A human eye / Claude
Code with browser access should confirm the aesthetics (framing, readability
of cone nose heading, health-bar visibility) as the last human-preference check.

---

## Evidence (preserved)

Raw browser-capture screenshots and driver outputs from the verification runs
were moved to `/tmp/p44-evidence/` (shots + run transcripts) and the
throwaway driver scripts deleted from the repo, so the working tree carries
only the two intended changes.

---

## Related

- P43 `VIZFIGHT_P43.md` defines the wire schema (`tick`/`kill`) this page
  consumes — unchanged, P44 matches it exactly.
- Backend untouched except the one `UseDefaultFiles()` line; all sim/product
  code in `src/` is byte-for-byte as P43 left it.
