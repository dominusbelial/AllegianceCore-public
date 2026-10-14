# P433 — real missiles: launcher resolution + firing, in both allegiance-mcp and vizfight

## Why this task exists

Domingo, after confirming tonight's combat never used missiles: *"I
think we need to [extend] Catalog's weapon resolution in allegiance-mcp,
but also make it work for vizfight's fleet, we need to keep working in
having full parity with the mcp."* Two real, previously-confirmed facts
make this genuinely achievable now (not a re-investigation):

- `PCore001.igc` has **123 real missile launcher parts** and **56 real
  missile types** in `core.Launchers` (a `StaticCore.Launchers`
  collection, `Data/DataLauncherTypeIGC.cs`) — SEPARATE from the 117
  `core.Parts` every prior loadout builder only ever read from. This
  was the actual, narrow gap (confirmed by an earlier research fork
  this session): missile data and the missile-lock mechanic were never
  broken, nothing ever equipped a launcher.
- `ConnectionSession.SendFireMissile()` (`ConnectionSession.cs:492`)
  and `Allegiance.Bot.Bot.SendFireMissile()` (`Bot.cs:1458`) already
  exist and are unit-tested (P420) for correct wire encoding — never
  exercised live because no bot has ever carried a launcher.
- The missile-lock mechanic (`Ship.UpdateMissileLock`,
  `UpdateMountedMissileLock`, the real `MissileLockUpdate` wire message,
  and now P428's real flower-sprite HUD indicator) is fully wired and
  faithful to native — confirmed this session.

This task closes the loop: real launcher resolution → real loadouts
that include a launcher → real missile fire once a lock is achieved —
in BOTH `tools/allegiance-mcp` and `tools/vizfight`, so the two stay at
comparable capability (the standing parity goal, see
[[allegiance-vizfight-mcp-architecture-split]] if that memory note is
available to you; if not, the short version is: vizfight's existing bot
code is being kept intentionally until allegiance-mcp can do everything
it does, and this task is part of closing that gap — not a reason to
start removing vizfight code).

## Scope

### 1. Real launcher resolution (`tools/allegiance-mcp/Catalog.cs`)

Add real resolution against `core.Launchers` (keyed by `PartId` —
confirm the exact key/lookup shape by reading `StaticCore.Launchers`
and `DataLauncherTypeIGC` yourself, don't assume): a `ResolveLauncher`
(or extend `ResolveWeapon` to also accept launcher names — your call,
document the choice) using the same exact-over-substring policy every
other resolver in this file already uses. A query that doesn't resolve
to a real launcher is a real, specific error, matching `ResolveWeapon`'s
own existing error shape (`Catalog.cs:190-197`).

### 2. Real loadouts that include a launcher, in BOTH tools

- `tools/allegiance-mcp/Catalog.cs`'s `PartsForWeapon` (and wherever
  `spawn_bot`/`spawn_scenario`/`run_hull_matrix` build a loadout) gains
  an optional real launcher, checked against the hull's real capability
  mask the exact same way P432's `MatrixPlanner` already checks weapon
  compatibility (`DataHullTypeIGC.CanMountPart`,
  `hullTypeIGC.h:199-213`) — reuse that check, don't reimplement it.
  Consider a new optional `spawn_bot`/`spawn_scenario`/`run_hull_matrix`
  argument (e.g. `launcher`) so a caller can name one explicitly, plus
  a sensible default-launcher choice (mirroring `DefaultWeaponPart`'s
  existing pattern) when omitted but requested.
- `tools/vizfight/Program.cs`'s `FullInventoryParts`
  (`Program.cs:2660`) gains the equivalent: a real compatible launcher
  added to the loadout alongside the existing weapon/afterburner/
  cloak/shield, using the SAME resolution + compatibility logic (share
  the underlying resolver code with allegiance-mcp if that's clean to
  do without a large refactor; if not practical without a big
  restructuring, a faithful parallel implementation is acceptable — but
  don't let the two silently diverge in behavior, document whichever
  choice you make and why).
- A hull/loadout combination where no real launcher fits must be a
  real, honest outcome (falls back to gun-only, logged/reported
  plainly) — never a silent crash and never a fabricated launcher.

### 3. Real missile firing in combat behavior, in BOTH tools

Once a bot's real lock (`MissileLockState`/the equivalent lock-fraction
data each side already tracks) reaches full lock on its current
engagement target, call the real `SendFireMissile()` (reuse the
existing method, already proven correct — don't touch its wire
encoding). Wire this into:
- `tools/allegiance-mcp/BotManager.cs`'s `EngageCombat` (the P429
  engagement loop).
- `tools/vizfight/Program.cs`'s `EngageEnemy` (the P420 engagement
  loop this session's whole combat choreography descends from).

A real missile has real ammo/cooldown constraints server-side —
respect whatever real gate already exists (don't spam
`SendFireMissile()` every tick once locked; find and reuse the real
per-launcher cooldown/ammo state the server already enforces, the same
way the existing gun-fire code already paces its own AllWeapons beats).

## Explicitly out of scope

- Any change to the missile-lock math itself (`Ship.UpdateMissileLock`)
  or the wire message shape — already correct, don't touch.
- Any change to P428's HUD sprite rendering — already correct.
- Any new `spawn_bot`/`run_hull_matrix` behavior beyond adding
  launcher support to the existing `"combat"` path.
- Reducing or removing any of vizfight's existing bot-driving code —
  this task ADDS missile capability to it, it does not start the
  eventual "vizfight becomes pure visualizer" migration.

## Verification — real proof, not just build/test

- **Live proof, required, in BOTH tools separately**: against a real
  running dev server, confirm a real bot (one spawned via
  `allegiance-mcp`'s combat behavior, and separately one from
  vizfight's own attach fleet) actually builds a real lock to 1.0 and
  fires a real missile — a real `SendFireMissile()` wire call
  observed, and ideally a real missile object/kill resulting. Screenshot
  or describe both.
- A test proving launcher resolution + hull-compatibility-gated loadout
  building in isolation (mirroring P432's `MatrixPlanner` test style).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. Real launcher resolution + loadout + firing, in both
   `tools/allegiance-mcp` and `tools/vizfight`, committed on `master`
   (not pushed — Domingo pushes).
2. Report doc `docs/reports/P433_REAL_MISSILES_MCP_AND_VIZFIGHT_PARITY.md`:
   the exact resolution/compatibility logic added, whether the two
   tools share code or run faithful parallel implementations (and why),
   and the live firing verification performed in both.
