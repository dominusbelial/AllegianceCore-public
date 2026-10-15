# P402 — debug-mode test arena: replace the hand-injected Omega/Plato/Alderan topology with generated `Brawl`

## Why this task exists

P379 built debug mode's test arena by hand: three real, hardcoded
m2xspiderw sectors (Omega/Plato/Alderan), two synthetic injected
stations, and manual warp removal to seal it off
(`ApplyDebugTestTopology`, `Server.Lifecycle.cs:274-344`). P397-P401
(read all five reports, `P397_MAPGEN_FOUNDATION.md` through
`P401_MAPGEN_BRAWL.md`, in the repo root, before starting) ported
native's own procedural map generator, and `Brawl` (P401, landed just
before this task) is native's own version of almost exactly what P379
hand-built: one shared sector, one real side-owned home station per
side (using each side's actual `InitialStationTypeId` from core data,
not a hardcoded station type constant), a few asteroids, and — since
there's only one cluster — zero warps to manage. This task swaps debug
mode over to use it, deleting the hand-built topology's station-
injection and warp-removal machinery while keeping everything debug
mode needs for unattended autonomous testing (hull-mix ship spawning,
docked initial miners, mission auto-start).

**This was a live design discussion with Domingo, not a request out of
nowhere** — confirm the reasoning below still holds by reading the
actual current code yourself before implementing; don't take this
brief's claims about line numbers or behavior on faith where you can
verify directly.

## Confirmed current architecture (traced this session, verify yourself)

- `InstantiateGames` (`Server.Lifecycle.cs:678-747`) already supports
  generating a game's map instead of loading a file, purely via
  per-game config: `if (g.MapGen is { } mg) { ... MissionMapGenerator
  .Generate(...) ... }` (`:707-715`), completely independent of
  `liveSettings.debugMode`. Right after `Mission.Assemble` (`:737`), if
  `debugMode` is on, `ApplyDebugTestTopology(mission)` runs
  unconditionally (`:746-747`) — **regardless of whether the mission's
  map came from `g.MapGen` or a real file**.
- `ApplyDebugTestTopology` (`:274-344`) currently assumes the real
  m2xspiderw sectors exist and bails gracefully if not
  (`:284-290`, already-existing guard for exactly this kind of
  "debug mode on an unexpected map" case — e.g. P378's endurance test on
  `XXV_simple`). **If a debug-mode game were pointed at `g.MapGen =
  Brawl` today with no other change, this guard would fire and skip the
  entire block — meaning the hull-mix `ShipSpawner` (`:321-341`) would
  never get wired**, silently falling back to whatever the default
  spawner does. This is the concrete bug this task must not reintroduce.
- `HomeSectorForSide` (`Server.cs:556-573`) — the **generic**, always-on
  station resolver used by real (non-debug) games: iterates every
  sector in the mission and returns the first one containing a runtime
  or static station owned by the given side. **Confirmed this already
  works correctly for a Brawl-generated map with no changes**: Brawl's
  one shared cluster holds every side's own station, so
  `HomeSectorForSide(mission, sideId)` correctly returns that same
  shared sector for every side — there's no second candidate sector to
  confuse it, unlike real-map debug mode (see next point).
- **Why `ResolveSideHomeStation` (`:503-523`) and
  `FindDebugGarrisonStation` (`:222-228`) hardcode the two synthetic
  station ids instead of just using `HomeSectorForSide` already** — on
  the *real* m2xspiderw map, debug mode's Omega/Plato stations are
  injected into a map that **still contains the real, unreachable
  Ganymede/Organia home garrisons** (P379 only removes warps, it
  doesn't delete those far sectors). `HomeSectorForSide`'s generic
  iteration would find a side's real Ganymede/Organia station as a
  candidate too, and — depending on `mission.Sectors` iteration order —
  could return the wrong (unreachable) one instead of the reachable
  synthetic garrison. The hardcoded-id check exists purely to force the
  correct disambiguation in that specific ambiguous case. **This
  ambiguity does not exist for a `Brawl`-generated mission** — the
  entire mission *is* the generated map, there is no leftover real
  Ganymede/Organia anywhere in it — so `HomeSectorForSide`'s ordinary
  generic answer is already correct and sufficient there. Confirm this
  reasoning against the actual `MissionMapGenerator`/`Mission.Assemble`
  output before relying on it (e.g. that a generated mission truly has
  no other side-owned stations anywhere it could mismatch on).

## Scope

1. **Thread "did this mission's map come from MapGen" into
   `ApplyDebugTestTopology`** — the caller (`InstantiateGames`) already
   knows this at the call site (`g.MapGen is { } mg`, `:708`); pass it
   through explicitly (e.g. an added parameter) rather than re-deriving
   it from the mission's shape.
2. **Add a generated-arena branch to `ApplyDebugTestTopology`**: when
   the mission's map came from `g.MapGen` (any generator-produced map —
   don't hardcode this branch to only trigger for `MapGenType.Brawl`
   specifically unless you find a real reason a home-base+choke-point
   generated topology like `SingleRing`/`Star` wouldn't also work
   correctly here; if you do find such a reason, document it plainly),
   skip the station-injection (`:303-304`) and warp-removal
   (`:313-315`) entirely — the generator already produced the right
   stations and topology — and wire the `ShipSpawner` closure generically:
   every side already has exactly one real generated station somewhere
   in `mission.Sectors`; resolve each side's home sector the same way
   `HomeSectorForSide` already does (reuse it directly rather than
   re-implementing), spawn ships there, keep the existing hull-mix
   alternation logic (`spawnCount % 2` for side, `(spawnCount/2) % 2`
   for hull) unchanged. **Keep the existing real-map (m2xspiderw)
   branch exactly as it is today** — this is an added path, not a
   replacement of working, already-live-tested behavior; the existing
   `:284-290` guard should now mean "not m2xspiderw AND not a generated
   map" before it skips entirely.
3. **Simplify `ResolveSideHomeStation`** (`:503-523`): its debug-aware
   branch should only take the hardcoded-synthetic-id path when the
   mission is the real-map case (the one with the genuine Ganymede/
   Organia ambiguity risk described above); for a MapGen-sourced debug
   mission, fall through to the plain `HomeSectorForSide` + first-side-
   owned-station resolution — the same generic path real (non-debug)
   games already use, since it is already correct there. Thread the
   same "did this come from MapGen" signal you added in step 1 to
   decide which branch applies (don't re-derive it by sniffing sector
   names/ids).
4. **Do the same for `FindDebugGarrisonStation`** (`:222-228`) and its
   two callers (P390's `ResolveSideHomeSector` join-path,
   `Server.Lobby.cs:1968-1978`; P393's `FindFriendlyStation` death/
   respawn/rescue/briefing path, `Server.Combat.cs:1446-1451`) — same
   principle: the hardcoded-id preference only matters when the real
   map's leftover Ganymede/Organia creates ambiguity; for a generated
   debug mission, the plain generic resolution is already right.
5. **Config**: no new config schema needed — `g.MapGen` already exists
   per-game (`ServerConfig.cs:38-40`). Add a commented-out example in
   `docker/compose.server.debugmode.json` (or wherever P379's example
   config lives — check `docker/README.md`) showing a debug game
   configured with `"mapGen": { "type": "Brawl", "teams": 2, "civIds":
   [...] }` instead of pointing at `m2xspiderw.igc`, so Domingo has a
   ready-to-use example.
6. Confirm P382's `SpawnInitialMiners` (already debug-aware via
   `ResolveSideHomeStation`) and P381's `RunNpcAutoLaunch` /
   P387's mission-auto-start-at-boot still produce the "unattended
   autonomous economy" Domingo asked to keep — each side's miner should
   still spawn docked at its real (now generically-resolved) home
   station and auto-launch into mining with no human player connected,
   exactly as today, just against the generated arena instead of the
   hand-built one.

## Explicitly out of scope

- Deleting the real-map (m2xspiderw) debug-topology path — keep it
  working exactly as-is; this task adds an alternative, it does not
  remove the existing one.
- Any change to `MissionMapGenerator`/the 15 topologies themselves —
  P397-P401, already landed, untouched.
- Any change to hull-mix logic itself (Scout+Fighter alternation) —
  reuse verbatim.
- P368-P401 — unrelated, don't touch beyond the specific methods named
  above.

## Verification — real proof, not just build/test

- **Live proof, required**: boot a debug-mode game configured with
  `MapGen = Brawl` (2 teams), connect at least 2 real MonoGame clients
  on different sides, and confirm: each spawns in the one generated
  sector; each side's home station is the correct one (dock/buy works,
  full economy loop); hull mix is still correct (Scout/Fighter
  alternation); no player ever needs to leave the sector to reach the
  other side (Brawl's whole point).
- **Live/observed proof, required — the "economy without real users"
  requirement**: boot the same config with zero players connected and
  confirm the docked initial miners still auto-launch (P381) and
  actually mine (P382/existing mining loop) unattended, exactly as
  today's real-map debug mode does — this is the specific behavior
  Domingo asked to preserve.
- **Regression, required**: boot debug mode against the real
  m2xspiderw map (no `MapGen` set) and confirm it is byte-for-byte
  unaffected — same Omega/Plato/Alderan topology, same synthetic
  station ids, same warp removal, same live behavior as before this
  task. Also confirm a normal (debugMode off) boot is completely
  unaffected.
- A test proving `ResolveSideHomeStation`/`FindDebugGarrisonStation`
  correctly pick the synthetic station (not a real one) in the real-map
  ambiguous case, AND correctly resolve via the generic path in the
  generated-map case.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The change, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P402_DEBUG_MODE_MAPGEN_ARENA.md` in the repo root: what
   changed in each of the methods named above, the config example
   added, and the verification performed (including the two live/
   observed proofs above).
