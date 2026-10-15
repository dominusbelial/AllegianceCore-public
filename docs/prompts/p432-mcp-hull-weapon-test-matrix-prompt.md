# P432 — a real hull/weapon test-matrix tool for allegiance-mcp

## Why this task exists

Domingo, planning the next phase after the P429-P431 combat/scenario/
order-acceptance chain lands: *"once we finish with all the performance
setup, we should create per ship scenarios combined with the size
option, we will test every hull/weapon."* A systematic way to run a
real combat scenario for a specific hull+weapon loadout at a given size,
and capture real balance/performance data per combination — not just
per bot-count (P430), and not limited to whatever default loadout
`spawn_scenario` currently uses.

This task assumes **P429** (real `"combat"` behavior + `target_sector`),
**P430** (`spawn_scenario` + `get_perf_stats`), and **P431** (bots obey
real teammate orders) have already landed — build on all three; if any
has not landed when this is picked up, stop and say so.

## Real scope constraint — read before implementing

`list_hulls` returns **447 real hulls**. An exhaustive all-hulls ×
all-weapons × all-sizes run is not a reasonable live-verification target
for one task (it would take a very long time and isn't what's being
asked for right now). The TOOL itself must be general (accept any real
hull/weapon names, any list of sizes), but this task's own live
verification should run a **small, representative set**
(e.g. 3 real distinct hull classes × 2-3 real weapon types × the 3 size
presets [5, 10, 15]) — document exactly which ones and why they're
representative (e.g. picking hulls from different real capability
classes already distinguished elsewhere in this codebase, not just the
first 3 alphabetically).

## A real design tension to resolve, don't silently pick one side

Running N distinct hull/weapon combinations back-to-back needs each
combination's bots cleared before the next one starts, or measurements
bleed together (old bots from combo 1 still flying during combo 2). But
P427-P430's standing design is explicitly **spawn-and-forget with no
despawn tool** (the only lifecycle control is killing the whole MCP
server process). Resolve this WITHOUT breaking that standing contract
for `spawn_bot`/`spawn_scenario` callers:

- The new matrix tool (below) may manage the disposal of ONLY the bots
  IT spawned for one combination, internally, before spawning the next
  combination — this is a self-contained implementation detail of the
  new tool's own orchestration loop, not a general-purpose `despawn`
  tool exposed to callers. `spawn_bot`/`spawn_scenario` themselves stay
  exactly as spawn-and-forget as they are today.
- If you find a cleaner real approach during implementation (e.g. the
  server already exposes a real disconnect/logoff a bot can trigger on
  itself), use that instead — but the constraint above (no new
  general-purpose despawn tool for existing callers) still holds.
  Document whichever approach you take and why.

## Scope

1. A new tool, `run_hull_matrix` (or a name you find clearer — document
   the choice), args: `hulls` (list of real hull names/substrings,
   required), `weapons` (list of real weapon part names, optional —
   default to whatever `spawn_bot`'s existing default loadout resolves
   to when omitted), `sizes` (list of per-side counts, default
   `[5, 10, 15]` matching P430's presets), `target_sector` (optional,
   same semantics as P429's). Resolve hull/weapon names via the exact
   same tolerant resolution `Catalog.cs`/`BotSpawnerResolutionTests`
   already established (exact-over-substring, real ambiguity/no-match
   errors) — reuse, don't reimplement.
2. For each (hull, weapon, size) combination in the cartesian product:
   spawn a real `size`-per-side `"combat"` scenario with that exact
   hull+weapon loadout (respecting real hull/weapon mount compatibility
   — a weapon that doesn't fit the hull's real capability mask must be
   a real, specific error for that combination, not a silent skip or a
   crash of the whole matrix run), let it run for a real, fixed
   observation window (pick a reasonable default, e.g. 30-60 real
   seconds — long enough for P429's rendezvous+engagement to actually
   produce real combat, document the choice), capture:
   - P430's real per-bot update-cadence stats for that combination.
   - Real combat-outcome stats: kill count per side (a real bot, being
     a real connected peer, already receives real `KILL_SHIP`
     broadcasts via its own `ClientWorld.Events.Kills` — the same
     mechanism vizfight already decodes; reuse that, don't invent a new
     kill-detection path), time-to-first-kill, and survivor count per
     side at the window's end.
   - then clear that combination's own bots (per the design-tension
     resolution above) before moving to the next combination.
3. Return a real, structured summary: one row per combination with its
   real captured stats — not just a final aggregate, so a caller can
   see how a fast light fighter compares to a slow heavy hull at the
   same size, etc.

## Explicitly out of scope

- Running the full 447-hull catalog, or every real weapon — this task
  proves the tool works correctly on a real representative sample; a
  separate, later, explicitly-requested run is the place for an
  exhaustive sweep.
- Any change to P429/P430/P431's own tools or behavior — this is a new
  orchestration layer on top, not a modification of what it composes.
- Any change to `tools/vizfight`.
- Missiles — still out of scope until the separate, already-identified
  loadout-resolution gap (pulling a real `Magazine` launcher from
  `core.Launchers`) is addressed in its own task.

## Verification — real proof, not just build/test

- **Live proof, required, this IS the point of the task**: run the
  real representative matrix (documented hull/weapon/size set) against
  a real dev server and report the real per-combination results table
  in the deliverable doc — real numbers, not placeholders.
- A test proving the cartesian-product expansion and hull/weapon
  resolution logic in isolation (pure, no server needed).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. `run_hull_matrix` + its internal per-combination lifecycle
   management, committed on `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P432_MCP_HULL_WEAPON_TEST_MATRIX.md`: the
   representative hull/weapon/size set chosen and why, the real
   per-combination results table, and which despawn/cleanup approach
   was used internally and why.
