# P49 — prune ghost ships from the explore visualizer on disconnect

**Repo:** `AllegianceCore`, branch `master` (top commit `a35d8ac` at start; this
adds P49 on top). **File:** `tools/vizfight/wwwroot/index.html`. No build
step, no backend change, no VM — single static frontend file, matching the
repo's convention. Date: 2026-09-01.

---

## Problem

`handle()`'s `tick` branch added/updated a mesh for every ship in each tick's
`m.ships` array, but never removed a ship whose id stopped appearing (its bot
disconnected or crashed). Such ships froze forever at their last known
position — indistinguishable from a real, motionless ship. Confirmed live by
the user ("some of them are idle").

## Fix

Track freshness on the per-ship state and prune stale ships using the existing
removal helper.

1. **Constant `GHOST_TICKS = 5`** — a ship that stops appearing in `tick` for
   more than 5 consecutive ticks is presumed disconnected. Real backend cadence
   is 20/s, so 5 ticks ≈ 250 ms: far beyond any genuine blip, while a live ship
   (which sends a `tick` entry *every* tick) can never accumulate that many
   misses.

2. **`tickCount`** (new module `let`) incremented once per `tick` message.

3. In the per-ship loop, stamp `s.lastSeenTick = tickCount` on the stored value
   (`shipState.set`) so each ship carries when it was last seen.

4. **After the per-ship loop**, prune: for each tracked `shipMeshes` entry,
   if `(tickCount - lastSeenTick) > GHOST_TICKS`, call the existing
   `removeShip(e)`. This is placed *before* the `visible`-set/scene logic, so a
   pruned ship never lingers in the visibility set or the scene — it is gone
   from `shipMeshes`/`shipState`/scene entirely (requirement 3).

5. **`removeShip(e)` extended**: it previously deleted only `shipMeshes`; it
   now also deletes `shipState`. This makes it a complete despawn and lets the
   prune reuse it directly (the brief's `despawnShip(id)` helper does not exist
   under that name in this file — the real removal helper is `removeShip(e)`,
   entity-typed, so I widened it rather than writing a duplicate).

The change is independent of the sector-focus filtering: the prune runs before
`visible`/`scene.add`/`scene.remove`, and `applyFocus()` reads
`shipState.get(id)` which is now consistently emptied on removal.

## Files changed

```
tools/vizfight/wwwroot/index.html   (~17 added lines)
VIZEXPLORE_P49.md                   (this file)
```

No `src/` or `Program.cs` logic touched. `dotnet build` unaffected; there is no
`dotnet test` coverage for this frontend file (its tests exercise `src/` sim/wire
logic only, untouched here) — the change is verified by headless-browser
assertions instead (below).

---

## Honest verification statement

I could not visually eyeball a rendered screenshot (no working vision model,
and no VM per the brief). Consistent with P44/P46's precedent, I drove a real
**headless Chromium via CDP** (Google Chrome `--headless=new` +
`--remote-debugging-port`, a dependency-free Node CDP client using Node 22's
global `fetch` + `WebSocket`) against the real page served from
`http://127.0.0.1:8791/index.html`, and asserted the page's live JS state
numerically. Because the file is a `<script type="module">` (module-private
`handle`/`shipMeshes`/`shipState`), I added a *temporary* `window.__p49handle`
/`__p49reset` /`__p49state` hook to drive the module-private `handle()` and read
`shipMeshes`/`shipState`/`tickCount` from CDP, ran the scenarios, then **removed
the hooks** — the committed file contains only the pruning change (confirmed by
`git diff` and a final clean-load smoke check: `typeof __p49handle ===
'undefined'`, `typeof __vstate === 'function'`, no JS exceptions). My harness
and hooks lived in `/tmp/p49test/`, outside the repo.

Driven scenario sequence (crafted `tick` messages through the real `handle()`
via the temp hook) — actual headless-Chrome output, not fabricated:

```
Test A: continuous presence is never pruned
  ticks=120, keep in state=true, meshes=1, keep in __vstate.ships=true
Test B: ghost pruned exactly when missed > GHOST_TICKS(5)
  t1 state=["alive","ghost"]
  t2..t6 ghostPresent=true meshes=2    (miss 1..5 — not pruned: condition is > 5)
  t7 ghostPresent=false meshes=1       (miss=6 > 5 → pruned; "alive" retained)
Test C: brief absence (<5 misses) then return survives
  present-at-return=true survives-to-40=true meshes=1
Test D: despawned ship removed entirely and stays gone
  gone in state=false meshes=0, __vstate no gone ships=true, still gone after 20=true
Test E: mixed disappearance vs continuous
  t2 flaky=true deadShip=true meshes=2
  t6 flaky=true deadShip=true meshes=2     (miss=5, boundary, still held)
  t30 flaky=true deadShip=false meshes=1   (long-gone pruned, flaky kept)
```

This proves, on the real page in a real engine: (1) a continuously-present ship
is never pruned even after 120 ticks; (2) a vanished ship is pruned only once
its miss count exceeds 5, and the miss accumulates only while truly absent;
(3) a ship that returns before the threshold is never dropped and survives
indefinitely afterward; (4) a pruned ship is removed from `shipMeshes`,
`shipState`, and the scene and does not respawn; (5) with mixed live/dead ships,
only the dead one is dropped and the live one is preserved.

**Not covered / honest gap:** this ran in explore-independent fashion with
synthetic ticks via a temporary test hook — it did **not** stream over a real
WebSocket from a real running `vizfight explore` backend. The prune path itself
is fully exercised, and the removeShip disposal exactly mirrors the path a live
WS-driven disconnect would hit, but end-to-end over a real WS pipe was not
reproduced here (no backend/bot available in this session). The final committed
file's normal operation was smoke-tested: it loads with no JS exceptions, the
connect/reconnect handler runs, and `__vstate` reports state correctly.

## Build / test status

- No build tooling; single static HTML as the repo convention requires.
- `dotnet test` offers **no coverage for this frontend file** (its suites
  exercise `src/` sim/wire logic, which is untouched here) — so I say that
  plainly rather than implying a suite backs this.
- The file parses/executes clean in headless Chrome (software-WebGL fallback
  notice is benign in headless and unrelated).
