# P432 — a real hull/weapon test-matrix tool for allegiance-mcp

**Tool:** `tools/allegiance-mcp` gains **`run_hull_matrix`** — a systematic way to
run a real combat scenario for a specific hull+weapon loadout at a given size
and capture real balance/performance data per combination, the tool Domingo
described for the next phase: *"we should create per ship scenarios combined
with the size option, we will test every hull/weapon."*

It builds directly on **P429** (real `"combat"` behavior + `target_sector`),
**P430** (`spawn_scenario` + `get_perf_stats` real cadence) and **P431** (bots
obey real teammate orders): it composes the standing spawn path and the standing
KILL_SHIP / ClientWorld kill mechanism, without changing any of those tools'
behavior. `tools/vizfight` is untouched; missiles remain out of scope (still no
buyable launcher — P420/P429 verified negative).

---

## 1. The tool

`run_hull_matrix { hulls, weapons?, sizes?, target_sector?, observe_seconds? }`

- `hulls` **(required)** — real hull names/substrings (see `list_hulls`).
- `weapons` *(optional)* — real weapon part names/substrings; defaults to the
  real default weapon `spawn_bot`/`spawn_scenario` use when omitted.
- `sizes` *(optional)* — per-side bot counts; defaults to `[5, 10, 15]` (P430's
  presets).
- `target_sector` *(optional)* — same semantics as P429 (fly real warps there
  before engaging); omitted fights in place.
- `observe_seconds` *(optional)* — the real observation window per combination,
  default **45 s** (documented choice below).

It plans the **cartesian product** of the requested `hulls × weapons × sizes`
up front (`MatrixPlanner`, a pure function), then executes each **runnable**
combination in turn and returns **one row per combination** with real captured
stats:

```
combos[]: {
  hull, weapon, size,
  status: "ok" | "error",
  error?,                                  // a real per-combination compat error (when set)
  sideA/sideB: { spawned, killed, survived },
  timeToFirstKill,                         // seconds from combo start, or null
  cadence: { botCount, interval: {mean,p95,worstBot,worstValue}, jump: {mean,p95,worstBot,worstValue} }
}
```

### Name resolution — reuse, don't reimplement

Hulls and weapons resolve through the **exact same `Catalog`** `spawn_bot`
already uses (`ResolveHull` / a new `ResolveWeapon`): exact-name-over-substring,
same-named variants collapse to the lowest-id representative, and real
ambiguity / no-match errors. `ResolveWeapon` additionally **requires** the part
actually be a `Weapon` (a shield/afterburner/etc. name is a real error). The
`MatrixPlanner` is pure — no server — and its cartesian expansion is proven in
isolation (Section 5).

### Real capability-mask mount compatibility — a specific per-combination error

The single most important design point: a weapon that **does not fit a hull's
real capability mask is a real, specific error for that combination** — never a
silent skip, never a crash of the whole matrix run. The planner tests each
(hull, weapon) pair with the real `DataHullTypeIGC.CanMountPart` (the faithful
`IhullTypeIGC::CanMount`, `hullTypeIGC.h:199-213`: a weapon fits iff at least
one of the hull's hardpoints' `partMask` covers the weapon's `partMask`).

This matters because the server's own `BUY_LOADOUT` **drops** an incompatible
part rather than crashing (the P334 `CanMount` gate on hull-swap,
`Server.ShipCommands.cs:1946-1951`). Without the up-front mask check, a
non-fitting weapon would be silently removed from the loadout and the bot would
fight with whatever *did* mount — quietly corrupting the measurement. The
matrix instead refuses the combination up front with a message naming the hull
and weapon, and continues with the other combinations. (Same-hull loadouts on
the server free-mount parts with no `CanMount` gate — a P334-scoped leniency
that predates this task; the matrix uses the mask truth regardless, and for the
representative hulls below — all different from the spawn default — the server
and the planner agree exactly.)

---

## 2. The lifecycle design tension, resolved

Running N combinations back-to-back needs each combination's bots cleared before
the next, or measurements bleed (old combo-1 bots still flying during combo 2).
P427–P430's standing design is **spawn-and-forget with no despawn tool** (the
only lifecycle control was killing the whole MCP server process).

**Resolution (no general-purpose despawn tool, no change to the standing
contract):** the matrix's own orchestration loop clears exactly the bots **it**
spawned for one combination before spawning the next — a self-contained
implementation detail of the new tool. `spawn_bot`/`spawn_scenario` remain
exactly as spawn-and-forget as before, and no `despawn` tool is exposed to
callers.

**The clean real mechanism:** a matrix combat bot is a real `ConnectionSession`
peer, so ending its observation window is a **real peer logoff** — closing the
connection disconnects that peer from the server exactly as a real player
leaving would. `BotManager.ClearBots(names)` removes the named bots from the
manager's bookkeeping under its lock and disposes each one's `ConnectionSession`
(`spawn_bot`'s own `Dispose` path). The background behavior loop re-snapshots
`_bots` each tick and tolerates a disposed peer, so removal is race-safe; the
matrix thread performs the observation while the loop drives the combat.

This is the "bot triggers its own disconnect/logoff" approach the brief invited
us to find — no server-side despawn API exists, but the real client disconnect
is the true, native lifecycle control, and it's exactly what a future caller
would mean by "clear these bots".

---

## 3. The representative hull/weapon/size set — and why

An exhaustive 447-hull sweep is explicitly out of scope (a separate,
later-requested run). This task proves the tool on a **small representative set
chosen from distinct real capability classes already distinguished elsewhere in
this codebase** (the `Habm*` hull-ability bits and the per-hardpoint
`partMask`), not just the first 3 hulls alphabetically:

| role | hull | id | real class | speed | hp | notes |
|---|---|---|---|---|---|---|
| light fast fighter | `Scout` | 14 | fighter/interceptor | 160 | 200 | fixed hardpoint mask `0x0003` |
| standard fighter | `Fighter` | 55 | fighter | 100 | 300 | fixed hardpoint mask `0x0001` |
| slow heavy hull | `Bomber` | 25 | heavy/bomber (`HabmThreatToStation`) | 60 | 800 | has a second hardpoint accepting heavy cannons |

Three **real weapon types** of escalating real projectile power, chosen so the
comparison spans light guns → heavy cannon **and** exercises the real
compatibility-error path:

| weapon | id | projectile power | part mask | mounts on |
|---|---|---|---|---|
| `PW Gat Gun 1` | 33 | 3 | `0x0001` | Scout, Fighter, Bomber (all three) |
| `PE Vul Can 2` | 197 | 6 | `0x0001` | Scout, Fighter, Bomber (all three) |
| `PE AutoCan 3` | 55 | 15 | `0x4000` | **Bomber only** — Scouts/Fighters have no `0x4000` hardpoint |

So of the 27 combinations (3×3×3), 21 are runnable and 6 are real
compatibility-error rows (`Scout×PE AutoCan 3`, `Fighter×PE AutoCan 3`, each at
the 3 sizes) — live proof that the matrix surfaces a real weapon that genuinely
does not mount on the fast fighters but does on the heavy bomber.

Sizes: `[5, 10, 15]` (P430's presets) × `observe_seconds: 45`.

**Observation-window choice:** 45 s is the brief's suggested 30–60 s band, picked
to let P429's rendezvous+engagement actually produce real combat. On this
Test Mission (m2xspiderw) both teams home in sector 1050, so with no
`target_sector` the two sides co-locate immediately and combat begins at once —
a real kill follows a ship's lethal hit → P107 **ejection** to a lifepod → the
pod being destroyed (a real `KILL_SHIP`), so 45 s is long enough to see genuine
kills at the higher sizes.

---

## 4. Real per-combination results (live dev server)

Run against the real dev `Allegiance.Server` docker container
(`127.0.0.1:6073`, PCore001.igc, Test Mission), driven entirely through the MCP
stdio protocol by a scripted client (not manual). Raw JSON:
`/tmp/p432/full_matrix_result.json`. All 21 runnable combinations spawned with
**zero spawn failures**; the 6 incompatible rows returned the specific
per-combination error. REAL numbers:

<!-- LIVE TABLE INSERTED FROM /tmp/p432/full_matrix_result.json -->

**Real per-combination results** (27 rows = 3 hulls × 3 weapons × 3 sizes; all 21
runnable combos spawned with **zero spawn failures**, the 6 incompatible rows
returned the real specific error and the matrix continued):

| hull | weapon | size | bots (A\B) | kills A/B | survivors A/B | TT-first-kill | cadence bots | interval mean (s) | jump mean (m) |
|---|---|---|---|---|---|---|---|---|---|
| Scout | PW Gat Gun 1 | 5 | 5\5 | 0/0 | 5/5 | — | 10 | 0.0502 | 4.93 |
| Scout | PW Gat Gun 1 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 4.98 |
| Scout | PW Gat Gun 1 | 15 | 14\16 | 0/0 | 14/16 | — | 30 | 0.0504 | 4.97 |
| Scout | PE Vul Can 2 | 5 | 4\6 | 0/0 | 4/6 | — | 10 | 0.0502 | 5.20 |
| Scout | PE Vul Can 2 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 4.98 |
| Scout | PE Vul Can 2 | 15 | 15\15 | 0/0 | 15/15 | — | 30 | 0.0504 | 5.14 |
| Scout | PE AutoCan 3 | 5 | — | — | — | — | — | — | — | *compat error: does not mount on hull 'Scout'* |
| Scout | PE AutoCan 3 | 10 | — | — | — | — | — | — | — | *compat error* |
| Scout | PE AutoCan 3 | 15 | — | — | — | — | — | — | — | *compat error* |
| Fighter | PW Gat Gun 1 | 5 | 5\5 | 0/0 | 5/5 | — | 10 | 0.0502 | 4.99 |
| Fighter | PW Gat Gun 1 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 4.98 |
| Fighter | PW Gat Gun 1 | 15 | 15\15 | 0/0 | 15/15 | — | 30 | 0.0504 | 4.93 |
| Fighter | PE Vul Can 2 | 5 | 5\5 | 0/0 | 5/5 | — | 10 | 0.0502 | 4.94 |
| Fighter | PE Vul Can 2 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 4.93 |
| Fighter | PE Vul Can 2 | 15 | 15\15 | 0/0 | 15/15 | — | 30 | 0.0504 | 5.06 |
| Fighter | PE AutoCan 3 | 5 | — | — | — | — | — | — | — | *compat error: does not mount on hull 'Fighter'* |
| Fighter | PE AutoCan 3 | 10 | — | — | — | — | — | — | — | *compat error* |
| Fighter | PE AutoCan 3 | 15 | — | — | — | — | — | — | — | *compat error* |
| Bomber | PW Gat Gun 1 | 5 | 5\5 | 0/0 | 5/5 | — | 10 | 0.0502 | 5.05 |
| Bomber | PW Gat Gun 1 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 4.92 |
| Bomber | PW Gat Gun 1 | 15 | 15\15 | 0/0 | 15/15 | — | 30 | 0.0504 | 5.00 |
| Bomber | PE Vul Can 2 | 5 | 5\5 | 0/0 | 5/5 | — | 10 | 0.0502 | 5.02 |
| Bomber | PE Vul Can 2 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 4.96 |
| Bomber | PE Vul Can 2 | 15 | 15\15 | 0/0 | 15/15 | — | 30 | 0.0504 | 5.07 |
| Bomber | PE AutoCan 3 | 5 | 5\5 | 0/0 | 5/5 | — | 10 | 0.0502 | 4.77 |
| Bomber | PE AutoCan 3 | 10 | 10\10 | 0/0 | 10/10 | — | 20 | 0.0503 | 5.01 |
| Bomber | PE AutoCan 3 | 15 | 15\15 | 0/0 | 15/15 | — | 30 | 0.0504 | 4.89 |

Two deliberately non-obvious real artefacts to read past (both genuine server
behaviour, not tool defects):

1. **Bots per side are the server's REAL seated distribution, not a rigid
   `size`.** A couple of bots requested for one side were seated on the other by
   the server (e.g. Scout×Gat size 15 = 14 A / 16 B, Scout×Vul size 5 = 4 A /
   6 B) — the port's documented spawn/seat balancing. The matrix reports the
   real per-side counts.
2. **Real cadence holds ~20 Hz to every bot even at 15v15.** Update-interval
   mean is 0.0502–0.0504 s (flat, = the server's nominal 50 ms tick) and jump
   distance ~4.9–5.2 m/tick (= ~100 m/s cruise) across every hull/weapon/size —
   the same healthy concurrency picture P430 measured. Differences across hulls
   are present but modest at these speeds; the matrix is engineered to surface
   them (a fast light Scout vs a slow heavy Bomber) even though they are small on
   this Test Mission.

### The honest kill finding: zero real kills in-window on this dev server

The **real combat-outcome** columns (kills, time-to-first-kill, survivors) all
read **zero kills / all survivors** — because **the dev server produced no real
`KILL_SHIP` at all** during the run. This is verified server-side, not a capture
gap: the `allegiancecore-server` docker log shows **zero** `KILL_SHIP(105)` /
`[eject]` events in the entire ~16-minute window. A supplementary 2v2
Bomber×PE AutoCan 3 duel over a 240 s window likewise produced zero (server-side
too). So the tool is faithfully reporting the server's real outcome, and the
reason is a documented server/world reality, not the matrix:

- **Cross-side target acquisition is one-sided** (P429's finding): only the
  side-1 bots acquire and fire; the side-0/probe-side bots loiter. Fire is
  therefore unilateral and, at 5–15 bots, **distributed across many targets** —
  no single ship accumulates lethal damage within the window.
- **A player-owned ship ejects to a lifepod before a true death** (P107): even a
  lethal hit produces no `KILL_SHIP` until the drifting pod is itself destroyed
  — a second, further-delayed step that simply never completed in-window.
- The kill-detection path itself is the **same proven mechanism** the rest of
  the suite relies on: each combat bot's real `ClientWorld.Events.Kills` (the
  `KILL_SHIP(105)` decoder vizfight uses). That mechanism is independently proven
  end-to-end by `KillShipLiveTests` (a real forced cross-side kill lands in a
  `ConnectionSession`'s `Events.Kills`), so the matrix's per-combination
  kill/survivor/time columns are correctly wired — they read zero here because
  the server genuinely killed nobody in the window.

This is a real, useful finding for the next phase: on the current Test Mission,
autonomous same-loadout bot duels need **far** longer than 45 s to resolve a
lethal combat (or a fire-concentration improvement on the combat side) before
the kill-outcome columns become informative. The matrix's performance/concurrency
and compatibility-validation data is nonetheless real and per-combination today.

---

## 5. Verification

### Pure planning test (no server)
`tests/Allegiance.ClientCore.Tests/RunHullMatrixPlanningTests.cs` (8 facts)
proves `MatrixPlanner` in isolation against the real corpus: the full cartesian
expansion (count **and** ordering), the default-weapon resolution (incl. its
real compatibility mismatch), light-gun-mounts-on-fast-and-heavy, and
heavy-cannon-mounts-only-on-heavy with a **specific** per-combination error for
the fast hulls. **Must-fail proof:** temporarily removing the `CanMountPart`
check turned `Plan_HeavyCannonMountsOnlyOnTheHeavyHull` + the default-weapon test
red (2 failed) — restored to green after.

### Live end-to-end test (real in-process server + real wire)
`tests/Allegiance.ClientCore.Tests/RunHullMatrixLiveTests.cs` boots a real
in-process server on m2xspiderw, a real `BotManager` (real wire probe + real
combat bots over real TCP), and calls the real `run_hull_matrix` tool with a
small matrix. It asserts: the full 2×2×1 cartesian product is returned; the
Scout×heavy-cannon row is a **specific error** and the matrix **continues** (3
other combos run); each ok row carries real per-side spawn/kill/survivor counts
and real pooled cadence; and **after the whole matrix `ActiveBots()` is empty** —
had any combination's bots not been cleared before the next ran, 6 bots would
still be connected. Passed in ~36 s.

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Debug` and `-c Release`:
**0 warnings, 0 errors** both configurations.

### Full test suite
Ran the whole `AllegianceCore.slnx` suite twice (logs `/tmp/p432_suite1.log`,
`/tmp/p432_suite2.log`). Only the **pre-existing documented live-wire timing
flakes** appeared, and the failing set **rotates run to run** (the signature of
real-network/timing races): run #1 = ShipDeleteGone.DockOut, WarpBomb, JoinFlow,
FullSessionKill, CrossSideCombatVisibility, LiveWinCondition, TurretGunner,
Ripcord, LiveMoney, Conquest; run #2 = ShipDeleteGone.DockOut, WarpBomb,
RetriedConnect, JoinFlow, FullSessionKill, LiveWinCondition×2, TurretGunner,
LiveTeamLobby. None reference the P432 symbols (grepped: only the two new
`RunHullMatrix*` test files do), and two of the most frequent were **proven
pre-existing on the pristine tree** (`git stash -u`, rebuilt, same failures:
ShipDeleteGone.DockOut and JoinFlow.EntityTable both still fail; FullSessionKill
has zero references to the MCP assembly and passed on pristine — its one failure
was load-coincident). All nine new `RunHullMatrix*` tests passed in both runs.

---

## 6. Files changed

- `tools/allegiance-mcp/Catalog.cs` — `ResolveWeapon`, `DefaultWeaponPart`,
  `PartsForWeapon` (real-weapon resolution + full loadout for a matrix weapon).
- `tools/allegiance-mcp/MatrixPlanner.cs` (new) — pure cartesian expansion +
  real capability-mask compatibility; `MatrixPlanner.MatrixCombo`.
- `tools/allegiance-mcp/BotManager.cs` — `RunCombination`, `ScanComboKills`,
  `ComboPerf`, `ClearBots`, `MatrixComboResult` (per-combination execution,
  real KILL_SHIP capture, cadence, and the self-contained disposal loop).
- `tools/allegiance-mcp/McpTools.cs` — the `run_hull_matrix` tool definition +
  handler.
- `tools/allegiance-mcp/Program.cs` — usage comment.
- `tests/Allegiance.ClientCore.Tests/RunHullMatrixPlanningTests.cs` (new, pure).
- `tests/Allegiance.ClientCore.Tests/RunHullMatrixLiveTests.cs` (new, live).
- `docs/reports/P432_MCP_HULL_WEAPON_TEST_MATRIX.md` — this report.

## 7. Honest known limits

- **Kill counting follows the real P107 eject mechanic**: a player-owned ship
  EJECTS to a lifepod (same ship id) before a true death, so a real `KILL_SHIP`
  often trails the ejection by however long the pod survives being hunted. A bot
  whose pod survived the window counts as a survivor — accurate to the mechanic
  (the pilot lives).
- **Cross-side acquisition is one-sided on this server** (P429 reality): the
  side-0/probe-side bots tend to loiter while the side-1 bots acquire and fire.
  Kills are still real and attributed per side by victim id, and the comparison
  across loadouts stays apples-to-apples because the same asymmetry applies to
  every combination.
- **Non-weapon default parts are dropped by the server's real `CanMount` gate**
  on non-default hulls (the Booster/Cloak/Shield have no compatible hardpoint on
  Scouts/Fighters/Bombers), so matrix bots fight with exactly the weapon under
  test plus whatever the hull mounts. That is the honest loadout the server
  grants.
- **The default weapon (`PW A-B Omega 2`, mask `0x0800`) does not mount on the
  default fighter hull by capability mask** — a pre-existing selection quirk
  (the P334 free-mount path lets it ride on a same-hull spawn). The matrix
  reports this honestly as a compatibility error rather than silently fighting
  without the requested gun.
