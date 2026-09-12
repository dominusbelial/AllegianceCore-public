# P46 — single-sector-focused renderer for real WOPR AI exploration

**Repo:** `AllegianceCore` (branch `master`), `tools/vizfight`. This task is the
frontend half of P45's `explore` backend: it retargets the *same* Three.js
rendering primitives P44 built for `livecombat` at real `ScoutExploreMap` AI
roaming the actual map, subject to the one governing constraint the brief set:
**render one focused sector at a time** (sectors are separate coordinate
bubbles, not one continuous universe — so a single 3D scene spanning the whole
map is impossible and was explicitly decided against).

Branch: `master`, top commit was `3727dba` P45 at start; this adds P46 on top.
Date: 2026-09-01.

---

## Files changed (scope)

```
tools/vizfight/wwwroot/index.html   (rewritten — single-file sector renderer, P44 primitives reused)
VIZEXPLORE_P46.md                    (this file)
```

Nothing else touched. No build step introduced. `bin/`/`obj/` gitignored as
before. Driver/loopback scripts used for verification lived in `/tmp/` and were
removed after use (evidence in the Verification section).

---

## Decision: one file, dual-mode

`explore` and `livecombat` serve the *same* `wwwroot/index.html` from the same
Kestrel (`Program.cs:581-582 UseDefaultFiles/UseStaticFiles`). So this single
static file must satisfy **both** backends without a build step. It does by
auto-detecting mode on the first `sector` message:

- **Combat mode** (default backend, no `sector` messages ever): ships render
  unfiltered, exactly as P44 — no regression. The `kill` path is retained but
  dormant: a weapon-less Scout can never fire, so no explore `kill` frame can
  arrive.
- **Explore mode** (the first `sector` message flips it): all P46 sector-focus
  behavior below.

Because the shared file means one code path, I kept P44's ship cone, health
bar, orientation-from-`fwd`, reconnect-on-close, ambient+directional lighting,
and starfield byte-for-byte, and layered sector focus on top.

---

## What the renderer does (mapping to the brief's scope)

1. **Sector focus (scope item 1).** Every `sector` message is recorded into a
   JS `Map` (`sectors`). `focusId` defaults to the sector the **first ship you
   ever see** is in, then stays fixed until the user changes it via a `<select>`
   populated from every seen sector (labeled `id · name`, e.g. `1051 ·
   Arrokoth`). `rebuildSelector()` refreshes the dropdown on each new sector
   message without disturbing the current selection. **(file:line in
   `wwwroot/index.html`: `sectors`/`focusId` at `index.html:263-269`,
   `rebuildSelector` `index.html:~282`, focus-default at `index.html:379`.)**

2. **Filter ships by sector (item 2).** Every ship's latest state is tracked
   in `shipState` regardless of focus; its mesh lives in `shipMeshes`, spawned
   on first sight and updated every tick. A ship is **in the scene iff its
   current `sector === focusId`** (`index.html:390-399`). Ships elsewhere are
   still position/state-updated but `scene.remove`d — so switching focus shows
   already-tracked ships immediately, correctly positioned, no re-sync.
   Verified: when the loopback Scout sat in sector 1051, all its peers rendered;
   the geometry swap is the same code path the dropdown drives.

3. **Static geometry (item 3).** On each focus change, `drawFocusGeometry()`
   clears `geomGroup` and redraws from the cached `sector` message (no re-request
   from the server): **warps** as thin emissive-cyan torus rings at their real
   `pos`, sized to their real `radius` (`index.html: torus scales x/y=R,
   z=thin, laid in the XZ plane`) — showing the warp's actual P41/P42 trigger
   volume, so a viewer sees *why* a crossing happens there; **stations** as
   orange boxes at their `pos` (`index.html:~305-330`). Redraw reads only the
   cache; no re-fetch. Also drawn for the focused sector when a trailing `sector`
   message arrives after the first focus is set (`index.html:422`).

4. **Camera (item 4).** On every focus change (first included), `reframeCamera()`
   computes a bounding box over the focused sector's warps + stations + any
   currently-visible ships, centers on it, and places the camera at
   `distance = boundingSphereRadius * 2.4` with an elevated offset
   (`index.html:~340-360`). `OrbitControls` remains for manual
   orbit/zoom/pan on top of that start frame. P44's fixed lane-geometry camera
   does not apply (extents vary per sector) and is not used in explore mode.

5. **Reused unchanged from P44 (item 5):** cone geometry/colors/orientation-from-
   `fwd`, health bar, reconnect-on-close, ambient/directional light, starfield.
   Kill-flash/fade is present but **dormant** — structurally unreachable in
   explore; kept for combat-mode fidelity.

6. **HUD (item 6):** `sectorInfo` line shows `focus: <name> (n ships)` plus a
   compact `active: NAME (n), NAME (n)` list of the other sectors with ships, so
   a viewer sees activity elsewhere worth switching focus to
   (`index.html:349-365`).

A `window.__vstate()` diagnostics hook (P44's `__positions` precedent) exposes
the live viewer state (focus, sectors, per-ship sector + world pos + in-scene
flag, geometry-child count) for headless assertion. Not used by the render path.

---

## Schema consumed (exactly P45's)

Verified live verbatim from `ws://127.0.0.1:7917/ws` while a loopback Scout was
present:

```json
{"type":"sector","id":1051,"name":"Arrokoth","warps":[{"id":1051,"pos":[320,-5155.5557,1613.2656],"radius":100,"destSector":1057},{"id":1053,"pos":[3733.3333,3911.111,606.72705],"radius":100,"destSector":1052}],"stations":[]}
{"type":"tick","simTime":963.9,"ships":[{"id":8102,"hull":"Lxy Scout","side":0,"sector":1051,"pos":[6384,731,921],"fwd":[0,0,-1],"hp":1,"alive":true}, ...]}
```

- `tick.ships[]` each carry `sector` (the live `Ship.Sector.Id`, per-tick-correct
  via P41) — this is the field the renderer filters on.
- `sector` one-time geometry carries `id`, `name`, `warps[]` (`id/pos/radius/
  destSector`) and `stations[]` (`id/pos`). No `kill` message in explore.
- **No deviation** from the schema needed; `VIZEXPLORE_P45.md` (§"WebSocket JSON
  schema") and the wire agree on every field this renderer consumes.

---

## Honest verification statement

I could not *visually* eyeball the rendered scene (no working vision model /
screen-eyeballing available), and I did **not** have a real `Wopr.exe` bot. I
drove a **real headless Chromium via CDP** (Google Chrome `--headless=new`,
`--remote-debugging-port`, Node CDP) against a **real running `vizfight
explore` backend** and asserted the page's live DOM/JS state numerically. That
matches P44's own precedent for the same gap (P44 relied on headless Chrome +
numerics, not a vision model). Full-blown `ScoutExploreMap` autonomy / a real
two-sector swap is explicitly deferred to Claude Code's live pass with real
Wopr.exe bots, exactly as the brief stipulates.

**What I proved (loopback, real frames — not fabricated):**

1. **Pipeline stands up.** A loopback managed `Bot` (the same
   `src/Allegiance.Bot/Bot.cs` harness P45 used) joined the *running* explore
   binary and was handed a Scout in sector **1051 Arrokoth** at `(912,731,921)`
   (`[explore] peer 1 joined: ship 8096 ...`). The explore WS stream then carried
   a `sector` message for 1051 and per-tick `tick` frames with that ship.

2. **Frontend reaches the same state (headless Chrome, live page).**
   ```
   exploreMode:true, focusId:1051, sectors:[{1051,Arrokoth}]
   ships:[{id:..., sector:1051, x:912, y:731, z:921, inScene:true}, ...] (all in focused sector)
   geometryChildren:2          (Arrokoth has 2 warps, 0 stations — exactly matches the sector msg)
   DOM: stats "ws: open · ships:N · sectors:1 · focus:1051"
        info "focus: Arrokoth (N ships)"
        dropdown [("", "(choose sector)"),("1051","1051 · Arrokoth")]
        hud "connected (1). explore mode — watching real ScoutExploreMap..."
   ```
   Each ship renders at its exact wire position (`x/y/z` == the `tick.pos`),
   `inScene:true` in the focused sector, `geometryChildren:2` proves the two
   warp rings drew, the dropdown lists the sector, the HUD shows the sector
   name + ship count, and mode flips to explore correctly.

3. **Robust to many ships.** With several loopback Scouts (some abandoned
   mid-drift by killed probes — the backend keeps ticking abandoned ships, so
   they kept flying), the renderer tracked all of them and rendered all in the
   focused sector 1051 with correct live positions. No JS errors observed.

**What I could NOT do (gap, being honest):** force a **real warp crossing** with
the loopback to obtain a *second* sector and thereby demonstrate a live dropdown
swap between two sectors. I built a steering autopilot (yaw/pitch control into
the running backend) and iterated on its control law; it converges onto the warp
direction briefly but then the Scout — full-throttle with limited turn torque —
overshoots into a wide orbit rather than entering the warp's 100-radius volume
within the attempt budget, and the backend deliberately has no scripted-spawn
hook in explore (its spawner always places in sector 0). The frontend *swap*
code path is nonetheless straightforward by construction: `selEl 'change' →
applyFocus()` rebuilds geometry from the cached `sector` messages, reshuffles
the ship meshes by `sector === focusId`, and reframes the camera — all three are
exercised for the focused sector in the single-sector evidence above (the only
difference with a second sector is the cache already holds it and the dropdown
lists it). I am stating plainly that the live two-sector swap, and real
ScoutExploreMap exploration, are **not** proven by this session and remain for
Claude Code's real-bot pass.

---

## Build / test status

```
dotnet build tools/vizfight/vizfight.csproj → 0 warnings, 0 errors
dotnet test  tests/Allegiance.Core.Tests/…  → 3 consecutive runs, 2232/2232
```

Only `tools/vizfight/wwwroot/index.html` changed. No `src/` or `Program.cs`
product logic was touched, so the 2232 tests (which exercise `src/` sim/wire
logic) are unaffected — they still run green as the baseline before/after.

---

## Commit decision

**Commit directly.** The renderer builds, the full suite is green
(2232/2232 ×3), and headless-Chrome verification confirms the explore page
renders real ships in the focused sector at correct positions with its geometry
drawn and dropdown/HUD populated. The residual gaps (real-bot autonomy, a live
two-sector warp swap) are explicitly the Claude Code follow-up's job, not a
blocker on this commit — they don't leave the deliverable in a broken state.
Nothing is left genuinely unresolved in the code itself. Committing as P46.
