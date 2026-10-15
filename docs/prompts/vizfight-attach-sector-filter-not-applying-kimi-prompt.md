# Bug: vizfight attach-mode ship visibility never actually filters by focus sector

## Context

`tools/vizfight`'s `attach` mode is a real spectator client + real bot
choreography tool that watches a live, already-running `Allegiance.Server`
(not a private self-hosted one). It republishes real ship/kill state to a
web page (`tools/vizfight/wwwroot/index.html`) via a `SpectatorHub`
WebSocket broadcast. The web page has a "focus sector" dropdown meant to
let a viewer pick which of the map's 13 real sectors to look at, showing
only the ships physically in that sector and framing the camera there.

This session just extended attach mode so it can run **two real sides**
at once (a friendly bot fleet + a real opposing-side contingent that
launches from its own home base and flies real warps to rendezvous) —
run via a Docker container on the real game server's own box
(`docker run vizfight attach 127.0.0.1 6073 7799 15 15`, image built from
`tools/vizfight/Dockerfile`).

## Already-fixed, confirmed-working part

Before this bug, the "active sectors" **text** counts on the HUD were
completely wrong (either dumping the whole fleet's count onto whichever
sector happened to be focused, or showing 0 for every sector but one).
That's now fixed and **confirmed correct** via direct verification (live
console/network inspection): a new `sectorCensus` WebSocket message
(`{type:"sectorCensus", counts:{"<sectorId>": n, ...}}`), broadcast every
tick from `BroadcastAttach` (`tools/vizfight/Program.cs`), computed from
real ship positions:
- Friendly-side ships (the spectator's own `ClientWorld.Ships`, which
  includes every same-side ship's real, live `SectorId`).
- The opposing contingent's own ships, each read from **its own**
  `ConnectionSession.World.Ships[shipId].SectorId` (a same-side spectator
  can't see cross-side ships outside its own cluster, so each peer's own
  self-pose is the only truthful source — see `EnemyCluster()` in
  `Program.cs`, already used this same way for routing).

The frontend (`index.html`) consumes this via a `sectorCensus` handler
and a `countIn(id)` helper in `renderHUD()`, and it's been verified live
to show accurate numbers, e.g. `focus: Ganymede (16 ships) · active:
Organia (15), Alderan (0), ...` — matching the real 16 friendly (15 bots
+ spectator) in one sector and 15 real opposing peers in another.

**Also already added and confirmed correct**: each ship in the per-tick
`ships` array now carries a real `realSector` field (added in
`src/Allegiance.ClientCore/State/SpectatorFrame.cs`'s `BuildTickFrame`,
via a new `ExtraShip` record so the opposing contingent's own ships —
otherwise invisible to the spectator — get folded into the SAME frame
too, sourced the same way as the census). This field is deliberately
**not** named `sector` because the frontend's own explore-vs-combat mode
classifier is `exploreMode = m.ships.some(s => s.sector != null && s.sector
>= 0)` — adding real sector data to the classic `sector` field would
silently flip that classifier for every mode, including the private
self-hosted showcase/explore/roster/proof modes that must keep working
unchanged. `realSector` is confirmed present and correct on the wire —
verified by opening a second raw WebSocket connection in the browser
console and dumping the first real tick frame's `ships[0]`:
```
{"id":8223,"hull":"Adv Scout","side":0,"pos":[720,730,920],"fwd":[0,0,-1],"hp":1,"alive":true,"realSector":1050}
```

## The actual, unfixed bug

The 3D **rendering** still never respects the focus sector in combat/attach
mode — every ship, from both sides, is always visible in the scene
regardless of which sector is selected in the dropdown. Confirmed live,
repeatedly, both by direct visual screenshot (both blue "side 0" and red
"side 1" ship cones visible simultaneously at the exact same screen
positions whether focus is set to the friendly sector, the enemy sector,
or an empty third sector) and by the person testing it independently
(`"i see 16 blue vs 15 red on your chrome, every sector"`).

### What's already been tried in `index.html` (all still present, all
still insufficient)

1. `realSectorOf(st)` helper: `st.realSector ?? st.sector ?? null` — used
   everywhere a ship's real position-sector is needed instead of the
   classic explore-only `st.sector`.
2. `spawnShip(s)`: the initial add-to-scene decision now uses
   `realSectorOf(s)` when present (`rs != null ? rs === focusId : ...`)
   instead of always `true` in combat mode.
3. `applyFocus()` (runs on the dropdown's `change` event and on first-ever
   focus adoption): loops every tracked ship, recomputes
   `match = realSectorOf(st) === focusId` (falling back to the old
   explore-only logic when no real sector is known), and calls
   `scene.add`/`scene.remove` accordingly.
4. The per-tick `handle(m)` ship-update loop (`m.type === 'tick'`, the
   `else { updateShipVisual(s, e); ... }` branch for an already-seen
   ship): added a **second**, identical add/remove re-check using the
   ship's freshly-received `realSector`, reasoning that a ship's real
   sector can genuinely change over time (the whole point of this
   feature — the opposing contingent crosses warps into a new sector),
   unlike explore mode's ships whose sector never changed mid-session
   (the assumption the pre-existing code used to skip a per-tick
   re-check).

### What live debugging showed (do not re-derive this from scratch — use
it as a starting point)

Temporary `console.log` instrumentation (since removed — the deployed
code right now has NO debug logging left in it) showed:

- Inside `applyFocus()`, for a genuinely mismatched ship (real sector
  1050, focus 1051), the computed values were exactly correct:
  `{"rs":1050,"focusId":1051,"match":false,"inScene":true}` — and the
  function DID reach the `scene.remove(e.mesh)` line for it.
- A before/after instrumented run showed `applyFocus()` correctly
  dropping `scene.children` from 36 to 20 (`removed: 16`, i.e. exactly
  the 16 friendly ships removed when focusing on the 15-enemy sector,
  leaving the 5 non-ship scene objects + 15 real matching ships) —
  **immediately after the call**.
- But within a couple of seconds afterward, `scene.children` — checked
  via the SAME `scene.getObjectById(e.mesh.id) === e.mesh` truthiness
  test the code itself uses — showed **all 31 ships back in the scene**
  again, with NO further calls into `applyFocus()` (confirmed by adding
  a call-site stack trace to its own logging — it was only ever invoked
  from the dropdown's `change` listener, exactly once per selection).
- Meanwhile the **per-tick re-check path** (item 4 above) was logging
  real `REMOVE` events for real mismatched ships, **repeatedly, on
  every single tick, for the same ship ids over and over** (thousands of
  log lines in a few seconds) — i.e. it kept trying to remove the same
  already-supposedly-removed mesh again and again, which only makes
  sense if `scene.getObjectById(e.mesh.id) === e.mesh` was intermittently
  or consistently reporting `true` (mesh still "in scene") even right
  after a remove — OR if something else is re-adding the exact same
  mesh object back into the scene between ticks. NO corresponding `ADD`
  log lines were ever observed in that same window, which rules out the
  per-tick path's own `scene.add` branch as the source of the re-adds —
  something else is putting ships back in.

### Live-testing caveats the investigating session should account for

- The person testing this (and the investigating Claude session) both
  restarted the `vizfight-attach` Docker container (`docker rm -f
  vizfight-attach && docker run ...`) **many times** during this
  investigation, each time producing a **brand new** set of bot/ship ids
  from the game server (old ids never reused within one container's
  lifetime, but a full container restart starts the whole numbering
  over). The browser tab was NOT always fully reloaded between every
  container restart. **A real possibility not yet ruled out**: the
  frontend's `shipState`/`shipMeshes` Maps (module-scoped `Map`s, see
  `index.html`) may never be cleared on a WebSocket reconnect
  (`ws.onclose` → `scheduleReconnect()` → `connect()` — check whether
  `connect()` or anything in its neighborhood clears `shipState`/
  `shipMeshes`/`sectors`/`sectorCensus`/`shipMeshes`'s THREE.js meshes
  from the scene). If it doesn't, a reconnect to a fresh container
  could leave stale ship entries (and their still-in-scene meshes)
  permanently orphaned, contributing to ships that never get cleaned up
  — but this alone wouldn't explain a mesh being re-added to the scene
  after `scene.remove()` was just called on it within the SAME
  connection lifetime, which is the core mystery.
- Ghost-pruning exists already (`GHOST_TICKS`, the loop right after the
  per-tick ship loop in `handle(m)`) and calls `removeShip(e)` — which
  deletes the mesh from `shipMeshes` AND `shipState` entirely — for any
  ship that hasn't appeared in `GHOST_TICKS` consecutive ticks. If this
  is (incorrectly) triggering for ships that ARE still actually present
  in every tick's `m.ships` array, the next tick would then treat it as
  brand-new via `if (!shipMeshes.has(s.id)){ spawnShip(s); }` — which
  re-evaluates `match` against the CURRENT `focusId` in `spawnShip`
  itself, so in isolation this shouldn't cause a mismatched ship to
  reappear either — but it's worth ruling out as a contributing factor,
  especially interacting with anything above.

## Scope

1. Find the REAL root cause — don't guess, trace it with real
   instrumentation or careful reading, the way the investigation above
   already tried to (that investigation ran out of budget, not out of
   validity — its findings above are real and should be trusted, just
   incomplete).
2. Fix it so that switching the "focus sector" dropdown genuinely changes
   which ships are rendered, for BOTH the initial selection and any
   ship that later crosses in or out of the focused sector while the
   page stays on that same focus (the opposing contingent's whole
   rendezvous arc needs to visibly show it leaving its home sector and
   arriving in the friendly one, ship by ship, as it happens).
3. Keep every already-confirmed-correct part intact: the `sectorCensus`
   text counts, the `realSector` field's name and shape (must stay
   separate from `sector` — do not touch the explore/combat classifier),
   and every other mode (`showcase`/`explore`/`roster`/`proof`) must
   render exactly as before (they never send `realSector`, so
   `realSectorOf()` should fall through to their existing behavior
   unchanged for them — verify this explicitly, don't just assume it).
4. If the real root cause turns out to be the WebSocket-reconnect state
   leak described above, or the ghost-pruning interaction, or something
   else entirely (e.g. a THREE.js `Object3D.id` reuse/collision after
   disposal — `removeShip()` calls `.dispose()` on the mesh's geometry/
   material but does it ever get garbage-collected in a way that could
   let a LATER, unrelated mesh's auto-assigned `.id` collide with an
   `.id` this code still holds a stale reference to via `shipMeshes`?),
   trace and fix that specific thing — don't paper over it with a
   different, unrelated workaround.

## Explicitly out of scope

- Any change to `sectorCensus`'s computation or the server-side
  `ExtraShip`/`realSector` plumbing (`Program.cs`,
  `SpectatorFrame.cs`) unless you find a REAL bug in them specifically
  (their values were confirmed correct on the wire during this
  investigation — see the raw JSON dump above — so treat them as
  probably-fine, not as suspects, unless your own fresh evidence says
  otherwise).
- Any change to the game server itself, or to the MonoGame client.
- Any other vizfight mode's own behavior.

## Verification — real proof, not just "it builds"

- Whole-solution build: 0 warnings, 0 errors.
- **Live proof, required**: rebuild the `vizfight` Docker image
  (`docker build -f tools/vizfight/Dockerfile -t vizfight .` — run this
  from the repo root; see the Dockerfile's own header comment for the
  `docker run` invocation shape) and run it as
  `attach 127.0.0.1 6073 7799 15 15` against the real local dev server
  (`docker compose up -d --force-recreate server` first if one isn't
  already running — see `docker-compose.yml`/`docker-compose.override.yml`
  for the real dev-server invocation shape, artwork mount, etc.). Open
  the page, select each of two sectors that genuinely have different
  real ship populations (the friendly fleet's home sector vs. the
  opposing contingent's home sector, or empty ones), and confirm via a
  real screenshot (or the browser's own `document.title`/console state,
  not just a claim) that the rendered ships actually change to match
  each selection — not just the HUD text.
- Also verify the OTHER modes are unaffected: run `vizfight showcase
  <port>` (or `explore`/`roster`) briefly and confirm ship rendering in
  combat/explore mode still works exactly as before (nothing filtered
  out that shouldn't be).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
   `git log` currently ends at `83171a02` (P425); this bug's fix should
   land as a new, separate commit on top — do NOT amend or rebase
   anything before it. The working tree right now (uncommitted) already
   has the "already-fixed, confirmed-working part" described above
   (the `sectorCensus`/`realSector`/`ExtraShip` additions) — build on
   top of that uncommitted state, don't discard or redo it.
2. A short report (plain text in your final message is fine, a doc file
   is also fine) naming the actual root cause you found and exactly
   what fixed it.
