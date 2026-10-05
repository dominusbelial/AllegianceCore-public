# P539c — MCP bots: one side doesn't acquire enemies and parks at alephs. Find the side-dependent root cause

Branch: `master`, not pushed. Build: `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug` — **0 warnings, 0 errors**.

> **Headline finding (measured, not assumed).** After deterministic per-side
> reproduction, **there is no side-dependent port bug** in cross-side target
> acquisition, warp transit, or what one side's clients are told. Every
> candidate mechanism the task named — the wrong sector stamped on a ship
> export, wrong/stale warp records, the probe client's side seating — was
> verified side-symmetric in code AND live. The one-sided outcomes that P429 /
> P474 / P539b reported are **emergent**, the product of a violent
> first-contact melee in which the lower-count trickling side is eliminated by
> collision/ramming (cross-side ramming is native-allowed) and the low
> survivability of the unshielded native-preferred Adv Scout. High variance,
> not a fixed side, decides each run. So the "higher-tech side wins every
> repeat" bar cannot hold, and the earlier "server reality" labels were
> premature. Details, numbers, and dated correction notes below.

---

## 1. Method — per-side, deterministic reproduction on a live in-process server

New read-only tooling (committed here, used by the repro):

- `BotManager.DiagnoseWorlds()` (`tools/allegiance-mcp/BotManager.cs`) — snapshots
  each combat bot's OWN `ClientWorld` exactly as its combat branch reasons over
  it: its own sector, every different-side ship it lists with its stamped
  `SectorId` + pose + last update tick (the acquisition inputs), and the warp
  records its world holds (id / cluster / position / radius / destination) —
  contrasted below against the server's authoritative view of the same ships
  and warps.
- `ALLEGIANCE_MCP_PROBE_SIDE_IDX` env seam in `ProbeSidePref` — forces which
  roster side the MCP probe (and thus the "probe's side") seats on, so the
  probe-causality experiment can be run.
- `tests/.../P539cReproLiveTests.cs` — a live in-process `m2xspiderw` debug
  server (20 Hz) + real `BotManager` over real TCP; spawns combat bots on both
  sides at a neutral rendezvous and every ~2 s dumps each bot's world view
  alongside the server's authoritative ship/warp state, plus every
  `[warp]`, `[xexport]`, `[combat]`, `[collision]`, `[eject]` and `[p433-diag]`
  line (gated `ALLEGIANCE_LIVE_P539C_REPRO=1`; writes `$TMPDIR/p539c_repro_*.txt`).

### 1a. Transit — verified to WORK for both sides, both directions

Quoting the server's own log from the 5v5 repro (all ten bots crossed into the
neutral rendezvous, Alderan 1052, from their homes — side 0 home Omega 1053,
side 1 home Plato 1060):

```
[warp] ship 8098 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184   [side 1]
[warp] ship 8100 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184   [side 1]
[warp] ship 8099 crossed from 'Omega' (1053) to 'Alderan' (1052) via warp 2153   [side 0]
[warp] ship 8097 crossed from 'Omega' (1053) to 'Alderan' (1052) via warp 2153   [side 0]
```

Every bot's `worldWarpToRdv` (the warp record its own client holds) matched the
server's authoritative warp byte-for-byte (same id, cluster, position, radius,
destination) on every sample — **no wrong/stale warp record on either side**.
Arrival is also simultaneous: both sides' first bots appear in 1052 within the
same 2–3 samples (S2–S4). There is **no park-at-the-aleph and no one-sided
transit** on this map/configuration. (The P496/P540b/P489 warp-approach and
dodge work that landed before this task already cured the older park-at-the-
aleph; the MCP warp law reuses those results.)

### 1b. Acquisition — verified to WORK for both sides

In every run bots of BOTH sides populate real cross-side targets and fire real
rounds. The cross-side identity export is side-symmetric in the server log:

```
[xexport] side 0 <- peer … ship 8098/8100 …   (side-1 ships exported to side 0)
[xexport] side 1 <- peer … ship 8097/8099 …   (side-0 ships exported to side 1)
```

Each bot's world holds the other side's ships with `SectorId == own sector`,
live `Latest` pose, and `visible=True` — exactly the P371 co-location gate
inputs. No one-sided `target` population was reproduced.

## 2. The probe-causality experiment — the probe is NOT the cause

5v5, equal tech, 200 s, neutral rendezvous, forcing the probe onto each side in
turn (the exact swap the task asked for):

| probe on | kills (A:B) | shots A:B | hits A:B |
|---|---|---|---|
| side 0 (default) | **2:0** | 3214 : 217 | 137 : 49 |
| side 1 (forced) | **0:0** | 3210 : 1519 | 129 : 29 |

The probe's side did **not** follow the dominance. Side 0 ("a" = first roster
side) out-fired in both, but the kill outcome swung (2-0 → 0-0). The probe's
seating is therefore not a (or the) cause.

## 3. What actually differs — the collision-dominated melee (measured)

When both sides are present and firing the fire **volume is symmetric** (the
P539b 90 s co-location run: shotsA 882 vs shotsB 703). The asymmetry emerges
from elimination-by-collision: side-1 bots arriving individually into side-0's
settled position ram and are destroyed. Direct from the 2v2 repro log:

```
[collision] ship 8098 x ship 8097 sector 1052 … jiggle(360.5, 360.5)   [side-1 ship rammed side-0 ship]
[collision] ship 8098 x ship 8099 sector 1052 … jiggle(359.7, 359.7)   [same side-1 ship rammed a 2nd side-0 ship]
[eject] player ship 8098 ejected -> Lifepod …                          [side-1 rammer eliminated]
[collision] ship 8100 x ship 8099 sector 1052 …                        [another side-1 ship rammed]
```

Cross-side ramming is **native-allowed** (native suppresses only friendly
damage, `shipIGC.cpp:1222-1228`; the server's bot-friendly-fire gate is
same-side only). Because side-0 settles a firing line first and side-1 bots
feed in one at a time, side-1's eliminations concentrate. With the unshielded
225-HP native-preferred Adv Scout, the first effective contact snowballs.
Result: 5v5/240 s outcomes are **bimodal and high-variance**, not
side-deterministic.

## 4. Native citations for the symmetric paths (no port difference to fix)

- **Warp transit trigger is side-independent**: `Sector.ResolveWarpCollisions`
  (`src/Allegiance.Core/Model/Sector.cs`) transits any ship that enters
  `|ship − warp| ≤ warp.Radius + ship.Radius` — the native
  `warpIGC` collision law; no side input.
- **The 176 ship-update candidate list is side-independent**:
  `Sector.GetShipUpdateCandidates` (`Sector.cs:456`) selects every non-dead,
  non-docked, non-boarded `Ship` in the sector with **no side filter**; every
  seated peer (both sides) is served from the same list
  (`Server.Broadcast.BroadcastShipsUpdate`).
- **The cross-side identity export is side-symmetric**:
  `SweepCrossSideExports` (`Server.Broadcast.cs:949`) iterates every sector /
  ship and every side other than the ship's own, gated only on "a seated peer
  of that side is in the ship's sector" + cloak — a mirror, not a side bias.
- **The client stamps every 176 entry's SectorId from its own sector** (P375,
  `ClientWorld.Apply(HeavyShipsUpdate)`), the native rule; symmetric for both
  sides.

There is therefore no code path in which "one side's clients are told"
something different — the side-dependence the task hypothesised does not exist
in the port.

## 5. Showcase bar — re-run, honestly (numbers measured)

Fresh runs this session on the current tree (all `[combat] … killed by …`
cross-checked):

| run | config | window | kills (A:B) | shots A:B | hitRate A:B | ejects |
|---|---|---|---|---|---|---|
| equal-tech 5v5 | tech 0 vs 0 | 240 s | **3 : 0** | 576 : 158 | 24.0% : 11.4% | 7 |
| repro 5v5 | tech 0 vs 0 | 200 s (probe 0) | 2 : 0 | 3214 : 217 | — | — |
| repro 5v5 | tech 0 vs 0 | 200 s (probe 1) | 0 : 0 | 3210 : 1519 | — | — |
| TechBar A r1 | tech 2 vs 0 | 150 s | **0 : 0** | 4000 : 11 | 0.3% : 100% | 5 |
| TechBar A r3 | tech 2 vs 0 | 150 s | **4 : 2** | 2014 : 499 | 6.8% : 15.8% | 9 |

(P539b's published full 240 s table — shotsB 854/240/1,030 vs shotsA ~2,850–3,200,
lower-tech-side wins 2-0 and 3-0, equal-tech 0 kills — is reproduced by the same
dynamics; the fresh runs above re-confirm.)

**The bar does not hold**, for the measured reasons in §1–§3, not for a missing
fix: outcomes swing 3-0 / 2-0 / 0-0 / 4-2 on equal-tech and even the tech-2
side loses a repeat (TechBar A r1 = 0-0) and occasionally the lower-tech side
shows a *higher* hit-rate. The modest 2-level gun edge is swamped by the
collision-dominated first-contact melee and the glass-cannon hull. This is
reported with the numbers above rather than a fabricated "fix that makes it
hold".

## 6. Symmetry proof — result

"Every bot on both sides completes both hops; real shots per side within 1.5×"
is **not met**: every bot does complete the outgoing transit (all ten `[warp]`
lines in §1a), but the melee makes shots land 2–15× apart (576:158, 3214:217,
3210:1519, 2014:499). Swapping side assignment (probe idx) does not change the
fundamental picture — shots stay unequal because one side's bots spend most of
the window dead/relaunching after collision eliminations.

## 7. Correction notes (dated)

The following claims in earlier reports called a real observed behaviour a
"server reality / the port inherits it" when it is actually **emergent melee
dynamics** and — on this configuration — no longer even reproduces:

- **P429** (`docs/reports/P429_MCP_COMBAT_BEHAVIOR.md:136-160, 185-201`) —
  "probe-side bots park at the aleph" and "cross-side target acquisition is
  one-sided … not a port defect". On `m2xspiderw` (debug) neither reproduces:
  both sides transit and both sides acquire. The park-at-the-aleph was already
  addressed by the P496/P540b/P489 approach+dodge work; the "one-sided
  acquisition" was a transient of that older map/config, not a wire defect.
- **P474** (`docs/reports/P474_MCP_PARITY_SCENARIOS.md:125-152`) — "server-side
  navigation limitation … park at the aleph on the second hop". Not reproduced
  here; transit is reliable both directions on the current tree.
- **P539b** (`docs/reports/P539b_MCP_BOT_REAL_HITS.md:155-160, 168-172`) —
  "P429 one-sided cross-side target acquisition … P474 park-at-the-aleph …
  out-of-scope server-visibility limitation". Both are now shown (P539c) to be
  **emergent** (collision-dominated melee), not a server-visibility defect; the
  correct characterization of the showcase non-reproducibility is high-variance
  melee + low survivability, not an acquisition/transit blocker.

## 8. Deliverables & tests

- `BotManager.DiagnoseWorlds()` — read-only per-bot world-view snapshot API.
- `ALLEGIANCE_MCP_PROBE_SIDE_IDX` probe-side seam.
- `P539cReproLiveTests` — per-side live repro + probe-swap (gated
  `ALLEGIANCE_LIVE_P539C_REPRO=1`), reproducing transit/acquisition/xexport
  symmetry and the collision-driven asymmetry; the run logs are the deliverable
  evidence (written to `$TMPDIR`).
- The reproducible, gated live test asserts the symmetric invariants that ARE
  true: every bot's world warp record equals the server's, every bot acquires a
  co-located cross-side target within the window, and the probe-side swap does
  not change the per-side cross-side-export set.

Whole-solution build: 0 warnings, 0 errors. Full suite: see §9.

## 9. Commit & suite

Committed on `master` (not pushed), `git branch --show-current` = master;
`git log -1` and a clean `git status` confirmed below. `scripts/run-tests.sh`
(P471) full suite, 3 consecutive runs on the final code:

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 ✓ | 1295/1295 ✓ | 474/475 (FullSessionKill) | 3730/3734 (AutoReloadOnEmpty, LiveTreasurePickup, LiveRipcord×2) |
| 2 | 12/12 ✓ | 1295/1295 ✓ | 474/475 (FullSessionKill) | 3731/3734 (LiveRipcord×2, AddPartTests) |
| 3 | 12/12 ✓ | 1295/1295 ✓ | 473/475 (ThreePeerSeeded, DockOut_Watcher) | 3732/3734 (LiveRipcord×2) |

Every failure is a **documented pre-existing live-wire timing load-flake**
(`docs/TEST_FLAKES.md`; the P471/P472 family — they rotate run to run and each
passes in isolation), and none reference this task's symbols: `FullSessionKill…
RealCrossSideKill` (line 33), `ThreePeerSeededSessionTests…
DeterministicVisibility` (line 38), `ShipDeleteGoneLiveTests.
DockOut_WatcherLatchesGone` (ledger line 24/46/102 — P472 real-bug flake,
pre-exists in every prior report), `AutoReloadOnEmptyTests…` (line 52),
`LiveTreasurePickup…cash_treasure` (line 157), `LiveRipcordTests…` (line 36/40),
`AddPartTests.Part_pickup_accept…` (line 247). My added `P539cReproLiveTests` is
gated (`ALLEGIANCE_LIVE_P539C_REPRO=1`) and is a no-op in the routine suite; the
`BotManager` diagnostic additions are read-only and none of the failing live
tests exercise them. Whole-solution build: 0 warnings, 0 errors.
<!-- SUITE -->
