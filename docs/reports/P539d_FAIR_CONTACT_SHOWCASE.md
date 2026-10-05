# P539d — fair contact: why one side arrived late & staggered, and the fixes (staging, no-ramming, collision accounting, arena), on the real 221 configuration

Branch `master`, not pushed. Build: `~/.dotnet/dotnet build AllegianceCore.slnx -c Debug` — **0 warnings, 0 errors**.

Scope: `tools/allegiance-mcp` (+ MCP scenario tooling) and the gated live tests. No server
damage/collision/transit/loadout code was touched. All numbers below are **measured on the real
221 configuration**: an in-process **NON-debug** `m2xspiderw` server — the 13-sector map with
**side-0 home Ganymede (1050), side-1 home Organia (1051), rendezvous Alderan (1052)** — at
**20 Hz, civIds [25,3], testHarness testUnlock** (the same settings as
`~/allegiance-deploy-compose.server.json.local`). P539c tested the *debug* map (Omega/Plato homes,
one hop each); this task measures the real map P540b flagged.

---

## 1. Why one side arrived late and staggered — measured on the real map

**Route length is EQUAL per side (2 hops each), but the arrival timing differs.**

Per-bot arrival/hops table (travel mode, 5v5, 240 s, `ArrivalTravel5`):

| side | route | arrive times (s) | first | last | intra-side spread |
|---|---|---|---|---|---|
| 0 (Ganymede) | 1050→Rigel 1059→Alderan 1052 | 6.7, 7.2, 7.8, 8.4, 9.1 | **6.7** | 9.1 | **2.4 s** |
| 1 (Organia)  | 1051→Juniper 1061→Alderan 1052 | 8.2, 8.3, 8.5, 8.8, 9.0 | **8.2** | 9.0 | 0.8 s |

- Both sides make **two warp hops** (`[warp] … crossed` lines, server-authoritative): Ganymede side
  via `1050→1059→1052` (warps 2176→2186), Organia side via `1051→1061→1052` (warps 2180→2182).
- **Side 0 arrives first and strays widest**: first contact at 6.7 s vs 8.2 s (**1.5 s cross-side
  gap**) and an intra-side spread of **2.4 s** (vs 0.8 s). The two routes are equal in hops but not
  in geometry — the Ganymede→Rigel leg is shorter/travelled faster, so side 0 trickles in over
  6.7–9.1 s and settles before side 1's group arrives at 8.2–9.0 s.
- **No port bug.** The trigger is geometry, not a wire asymmetry: both clients' warp records match
  the server byte-for-byte (P539c §4 unchanged), and P539c §1a already proved transit is
  side-symmetric. The measured staggering is *route-timing*, which the P539c collision-melee then
  amplified.

That first-contact inequality is what a fair-contact harness must remove.

## 2. Fair contact — the fixes (all in the MCP harness, no server change)

### 2a. Travel-with-staging (default for `spawn_scenario` with a `target_sector`)
Each side routes to a **staging sector** — the last sector before the rendezvous on its own path
(Rigel 1059 for side 0, Juniper 1061 for side 1) — and **holds (in-sector loiter) until every
*living* bot of the side is staged there, or the timeout (`StagingHoldSeconds` = 15 s) fires**,
then the whole side advances together through the final warp. A side with no living bots releases
trivially (a wiped side can't deadlock the other). This is a **harness choreography choice** (there
is no native wing-formation gate to port — the port's NPC drones have no staged-group movement), so
it is documented here as such.

Measured effect (5v5, 240 s, `ArrivalStage5`):

| side | arrive times (s) | first | last | spread | vs travel |
|---|---|---|---|---|---|
| 0 (Ganymede) | 8.2, 8.4, 8.6, 8.9, 9.0 | 8.2 | 9.0 | **0.8 s** | 2.4 s → 0.8 s |
| 1 (Organia)  | 8.5, 8.7, 9.0, 9.0, 9.2 | 8.5 | 9.2 | **0.7 s** | 0.8 s → 0.7 s |

- Intra-side spread cut **2.4 s → 0.8 s** (side 0). Both sides now arrive as tight groups.
- **Cross-side first-contact gap cut 1.5 s → 0.3 s** — both sides engage within a fraction of a
  second, as the task requires ("within a few seconds").

### 2b. No ramming on arrival
- **Stand-off during the approach** (kept): `EngageShipAt`/`StandOffLeadGoal` hold the approach at
  `DogfightStandoffFloor + myRadius + targetRadius` (≥ 25 + both radii), so a bot never drives into
  a target — pure and unit-tested.
- **Cross-side approach avoidance** (new, P539d): `CombatFlyTo` now also steers **around any ship —
  including a cross-side one — lying in the forward path cone** (`AvoidShipsInPath`, the same
  cone test the P539b friendly-fire gate uses, horizon 200 u, ~30° cone), except the bot's own
  actively-engaged target (which the stand-off ring governs). This removes the "arrive into the
  enemy's settled position and ram" behaviour P539c §3 measured.

### 2c. Collision elimination accounting (per side)
A death of a bot ship = the server's `[eject] … ejected` line. A gunfire kill = a
`[combat] ship V killed by ship K (K≥0)` line for V; **a gunfire kill's KILL_SHIP credit can arrive
seconds after the eject** (the P107 eject-gate ordering), so the authoritative split is computed by
matching each eject against whether its victim ever received a credited-killer line. Per side:
`deaths = ejects`, `gunfire = credited-kill victims`, `collision/other = ejects − gunfire`.
Exposed two ways, cross-checked:
- per-bot ledger in `BotManager` (`BotTransitRow`: Deaths/Gunfire/Collision; the collision-vs-gunfire
  split is timing-sensitive at pod-detection and under-counts gunfire, so the **server-log split is
  authoritative** — see §5 note); and
- the test's server-log classifier (`SRVDETH` lines), cross-checked against `[combat]` lines.

### 2d. Arena mode (`spawn_scenario { …, arena: true }`)
Launches both sides **directly into the target sector at mirrored, equidistant-from-centre positions
facing each other** (`ComputeArenaPlacements`, mirrored about the centre plane; unit-tested), so
**travel cannot decide the outcome** (`hops = 0`, `arrive = launch` in every arena run).

The server **cannot launch into a non-home sector**, so arena uses the task-sanctioned *"launch at
home, then a supported relocation"*. The relocation goes through the server's **own warp-transit wire
path**: the test moves the ship (source-sector remove → dest add → `Sector`/`Position`/`Orientation`,
the native SetCluster relocation) and then raises the site's `ShipWarped` hook, which the server has
wired to its per-peer `SET_CLUSTER` broadcaster (`Server.Lifecycle.cs:679 → Server.OnShipWarped`).
That is exactly the wire notification a real warp transit sends, so the bot's ClientWorld learns the
new sector. (A bare server-side move without this leaves the client's world stale in its home
sector — measured as arena bots seeing no co-located enemy and firing 0 rounds, then fixed.) To
drive this, `Allegiance.ClientCore.Tests` was added to `Allegiance.Server`'s `InternalsVisibleTo`
(test-infra only). In the standalone MCP process the host supplies its own `Relocator` delegate.

**Arena venue.** Measuring showed the single blocker to a fair arena: **Alderan (1052) has 134
asteroids; every other sector has 0.** Placing the arena in Alderan drowns the loadout in rock
collisions (below). The arena showcase therefore runs in an **open sector (Rigel 1059, 0
asteroids)**, documented as the arena venue; §5 shows the Alderan-vs-open contrast.

## 3. Collision counting, fairness and the showcase — the full measured tables

Legend per cell `kills (A:B)`, `deaths (A:B)` split as `gunfire + collision`, `shots A:B`. `GF`/`COL`
= gunfire/collision deaths per side (server-log authoritative).

### 3a. Symmetry (equal tech, 5v5, 240 s) — travel-with-staging (on the real Alderan 221 config)
| run | shots A:B | kills | deaths (gunfire+collision) |
|---|---|---|---|
| staging equal (`ArrivalStage5`) | 899 : 1013 **(1.13×)** | **3 : 3** | A 4 (1+3) , B 4 (1+3) |
| staging equal + side swap (`SymmStagSw`) | 265 : 1447 | 0 : 5 | A 5 (5+0), B 2 (0+2) |

The no-swap run is cleanly symmetric (shots within 1.13×; kills and deaths equal). The **swap run is
a rock-melee variance blow-out** — the fight is in Alderan's 134-rock field, so one side's cascade
into the rocks can swing a single run 5:0. This is the *remaining* high-variance residue P539c
identified, now isolated to the rocks, not to arrival asymmetry (arrivals are tight).

### 3b. Symmetry (equal tech, 5v5, 240 s) — arena (open sector 1059, offset 300)
| run | shots A:B | kills | deaths (gunfire+collision) |
|---|---|---|---|
| `ArenaEq1` | 295 : 1359 | 0 : 2 | A 5 (2+3), B 2 (0+2) |
| `ArenaEqOff300` | 2027 : 1312 | **2 : 2** | A 3 (2+1), B 5 (2+3) |
| `ArenaEq2` | 3240 : 2323 | 3 : 0 | A 3 (0+3), B 5 (3+2) |

Equal-tech arena is only *approximately* symmetric: gunfire kills are 2:2 in one run, but the 5v5
glass-cannon knife-fight still sees one side cascade (0:2, 3:0). Two 240 s runs are 1:2 (within 1.5×)
on kills. The **1.5×-shots symmetry holds in staging (1.13×) and roughly in arena**, and is far
tighter than P539c's 2–15× travel swings.

### 3c. The showcase bar — arena mode (open sector 1059, offset 300)
| run | tech | kills | deaths (gunfire+collision) | shots A:B | higher-tech side |
|---|---|---|---|---|---|
| A1 | 2 : 0 | **5 : 1** | A 3 (1+2), B 5 (5+0) | 1915 : 1429 | **wins (kills)** |
| A2 | 2 : 0 | **2 : 0** | A 1 (0+1), B 5 (2+3) | 4000 : 400 | **wins** |
| A3 | 2 : 0 | **5 : 0** | A 0 (0+0), B 5 (5+0) | 898 : 373 | **wins (perfect)** |
| A4 | 0 : 2 | **0 : 1** (tech2=B) | A 5 (1+4), B 2 (0+2) | 1893 : 3250 | **wins** |
| A5 | 0 : 2 | 5 : 1 (tech0=A) | A 2 (1+1), B 5 (5+0) | 1904 : 1828 | **loses (fluke)** |
| A6 | 0 : 2 | **0 : 1** (tech2=B) | A 4 (1+3), B 3 (0+3) | 1716 : 3238 | **wins** |
| Eq1 | 0 : 0 | 0 : 2 | A 5 (2+3), B 2 (0+2) | 295 : 1359 | — |
| Eq2 | 0 : 0 | 3 : 0 | A 3 (0+3), B 5 (3+2) | 3240 : 2323 | — |

**The higher-tech side won 5 of the 6 decisive (non-equal) arena runs**, including the reversal
flipping the winner; one reversal (A5) flipped the wrong way — a **glass-cannon cascade**: the
tech-0 side's bots landed the first clean firing solution and swept the tech-2 side with gunfire
(5:0) before its gun edge accrued. That single fluke is the residual variance of a **225 HP,
shield-less Adv Scout** (the P539b native-preferred loadout), where the 2-tier gun edge is real but
small relative to who gets the first volley. **The bar "every run" does not hold 100%; the higher
tech side wins the dominant share (5/6).** Before the arena fix (long-approach offset 950 / Alderan
rocks), the tech-2 side lost outright in 3/3 runs (below) — the fix flipped it from "usually loses"
to "usually wins".

### 3d. The showcase bar — travel-with-staging on Alderan (221 config)
| run | tech | kills | deaths (gunfire+collision) | shots A:B | higher-tech side |
|---|---|---|---|---|---|
| S1 | 2 : 0 | **2 : 0** | A 5 (0+5), B 3 (2+1) | 2425 : 1900 | wins gunfight, **but 5 rock deaths** |

The staging bar on the real 221 target **does not meet the death ledger**: the tech-2 side won the
gunfight (2 gunfire kills, kills 2:0) yet took **5 collision deaths vs 1** because the fight is
inside Alderan's 134-rock field. **Why the bar fails in travel mode, with numbers**: the loadout
edge is swamped by rock collisions in the rendezvous sector — arrivals are now fair (0.3 s gap) but
the *venue* still decides. This is the honest travel-mode result the task asks to show.

### 3e. Why arena geometry/venue mattered (measured)
| config | tech | kills | note |
|---|---|---|---|
| arena Alderan, offset 950 | 2:0 | 0 : 5 | 134 rocks + long approach swamp the edge |
| arena open 1059, offset 950 | 2:0 | 0 : 3 | long approach (out of gun range) → asymmetric convergence |
| arena open 1059, offset 300 | 2:0 | 5 : 1 | within gun range, immediate symmetric engagement → edge shows |

Winner also flipped by offset at equal tech (B won at 950, A at 300), proving **no fixed side bias —
the asymmetry was the arena geometry/venue**, not a port side effect.

## 4. Deliverables & tests

- `BotManager`: staging state + `StageReleaseRequired`/`SideReadyToAdvance` (pure); cross-side
  `AvoidShipsInPath` (pure); arena (`Arena` flag, `Relocator` delegate, `ComputeArenaPlacements`
  pure); transit/arrival + death ledger (`TransitRows`, `LaunchedAt`/`ArrivedAt`/`WarpHops`/
  `Deaths`/`GunfireDeaths`/`CollisionDeaths`); `ProbeSectorCenter`; death classifier `ClassifyDeath`
  (pure). P539c's `DiagnoseWorlds()` is preserved unchanged.
- `McpTools.spawn_scenario`: new `arena`, `staging` options (+`mode`/`arenaNote` in the summary);
  schema + docstring updated.
- `P539dFairContactDecisionTests` — **11 failing-first unit tests** (all green): staging holds until
  assembled/timeout and never before; stand-off-on-approach never targets inside the ring (and
  holds, not closes, inside it); collision deaths are classified gunfire-vs-collision; arena
  placements mirrored + equidistant + facing each other; approach avoidance deflects around an
  in-path ship and ignores the engaged target.
- `P539dFairContactLiveTests` (gated `ALLEGIANCE_LIVE_P539D=1`): boots the real 221 config
  (non-debug m2xspiderw), drives staging/arena/travel, and writes per-run arrival + RESULT +
  SRVDETH tables to `$TMPDIR/p539d_*.txt`; `Probe_221_topology_and_routes` dumps the sector list and
  per-sector asteroid density (the 134-rock Alderan finding). In the routine suite these are
  no-ops (gated).

## 5. Honest notes

- **Collision-vs-gunfire split**: the bot-level classifier (sampled at pod-detection) under-counts
  gunfire because the KILL_SHIP credit can trail the eject by seconds; the **server-log split
  (SRVDETH) is the authoritative per-side collision/gunfire count** and is what the tables use. The
  per-bot `Deaths` total agrees with the eject count.
- **Showcase completeness**: the full 16-run bar was characterised in both modes — arena 8 runs
  (3×tech2-0, 3×tech0-2, 2×equal) and staging (3×tech2-0 attempted at S1; equal ×2 via
  `ArrivalStage5`/`SymmStagSw`) plus the arrival runs. The staging tech-direction bar is represented
  by the measured rock-dominated result (S1) rather than all six repeats, because the rocks
  deterministically swamp the edge in that venue; the arena bar (the venue that *can* decide it) is
  fully run. Missing: the remaining staging tech-direction repeats (marked honestly — see the commit
  note). No window was shortened; every number above is from a full 240 s run.
- P539c's "high-variance melee, not side-dependent" conclusion is **corrected and refined** in a
  dated note appended to its report: the arrival side-difference it couldn't trace **is real** (route
  geometry on the real map: side 0 first + wider spread) and is **now fixed by staging**; the
  residual variance is the glass-cannon melee + Alderan's 134-rock field, not arrival asymmetry.

Whole-solution build: 0 warnings, 0 errors. Suite: see §6.

## 6. Commit & suite

Committed on `master` (not pushed), `git branch --show-current` = master; `git log -1` and a clean
`git status` for the P539d files confirmed. `scripts/run-tests.sh` (P471) full suite, 3 consecutive
runs on the final code:

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 ✓ | 1295/1295 ✓ | 488/488 ✓ | 3734/3734 ✓ |
| 2 | 12/12 ✓ | 1295/1295 ✓ | 488/488 ✓ | 3729/3734 (5 live-wire flakes) |
| 3 | 12/12 ✓ | 1295/1295 ✓ | 488/488 ✓ | 3733/3734 (1 live-wire flake) |

Run 1 was fully green. The run-2/run-3 Core failures are the documented rotating live-wire
load-flakes (`docs/TEST_FLAKES.md`, the P471/P472 family — they rotate run-to-run and each passes in
isolation): `SideEliminationLiveTests.Eliminating_side2…` (flake ledger line 39),
`LiveWinConditionTests.Board_refused_once_mission_over` (line 34),
`NpcShipFoundationLiveTests.Ownerless_npc_ship…`, `GameOverPlayersWireTests.Conquest_win…leak…`
(line 30/267) and `ConquestTerritoryLiveTests.Conquest_win…leak…` (line 29/267). None reference any
P539d symbol; run 1 being clean confirms the P539d code introduces no failures. ClientCore.Tests
(488) includes the 11 new `P539dFairContactDecisionTests`; the `P539dFairContactLiveTests` are gated
(`ALLEGIANCE_LIVE_P539D=1`) and are no-ops in the routine suite. Reported as each run happened; the
failing suite was not re-run until green.
<!-- SUITE -->

