# P632 — "port ship-update mode bots no longer kill each other": NOT a regression — port broadcast is healthy, the 0-kills are bot-driver transit at scale

## TL;DR

The hypothesised port-mode regression **does not exist**. On the 221 map
(`m2xspiderw`, Alderan rendezvous, staging OFF per the repro spec) **port mode
kills at every commit in the range `9194ddec..e1175e1b`** — and at the project
head (`a325977f`):

| Build | Model | size | staging | Kills (150 s) | Reached | MaxTargeted | MaxVisEnemies |
|---|---|---|---|---|---|---|---|
| `9194ddec` (P615, "good") | port | 30v30 | off | **33** | 60/60 | 53 | 30 |
| `e1175e1b` (P622, "bad") | port | 30v30 | off | **45** | 60/60 | 56 | 30 |
| `e1175e1b` | native | 30v30 | off | **40** | 56/60 | 39 | 30 |
| `a325977f` (HEAD) | port | 30v30 | off | **49** | 60/60 | 59 | 31 |

Port mode sees enemies (max visible enemy count ≈ the true enemy population),
acquires targets (≈ the whole fleet), fires, and kills **identically at both the
"good" and "bad" endpoints**. There is **no first bad commit** to bisect: the
port broadcast behaviour is flat across the whole range.

The real "0 kills" at 195v195 (221 clash23, P622's local proof) reproduces
locally at **both** endpoints and is **dominated by the MCP bot-driver's
transit/arrival collapse at ~390 local bots**, not by the port broadcast:

| Run | Arrivals into Alderan | Kills |
|---|---|---|
| scale-13 (390 bots) port, `9194ddec` | 341 | 0 |
| scale-13 port, HEAD (pre-fix) | 221 | 0 |
| 150v150 (300 bots) port, HEAD, staging ON | 101/300 | 0 (but bots fire, 53% hit) |
| 150v150 (300 bots) port, HEAD, staging OFF | 98/300 | 2 (65% hit) |
| 100v100 (200 bots) port, HEAD | 200/200 | **78** |

Below ~250 total bots every bot routes through the alephs and reaches Alderan,
and kills flow. Above that, on this workstation, only ~⅓ of a side makes it to
the rendezvous — so co-located combat strength never reaches a level where
destruction accumulates. This is a bot-driver aleph-routing capability limit
(the P610/P539d transit machinery), **independent of ship-update mode**, and it
is identical at `9194ddec` and `e1175e1b` (no regression).

While investigating, one genuine latent defect surfaced in the **port-mode
server broadcast** and is fixed here: **enemy telemetry starvation at density**.
See [The fix](#the-fix).

---

## Method — a cheap, deterministic repro

`tests/Allegiance.ClientCore.Tests/P632PortModeZeroKillsLiveTests.cs` (new,
env-gated `ALLEGIANCE_LIVE_P632=1`): an in-process `m2xspiderw` server (NON-debug
by default = the 221/P622 faithful map), a real `BotManager` over real TCP, and a
**fair, symmetric face-off** — `N` same-loadout `Adv Scout` combat fighters per
side, both sent to the neutral rendezvous Alderan (1052), staging OFF (the repro
spec), for a measured window. It reports, every 2 s: `botsReached` (mandatory
reach check), kills (cross-checked against the server's authoritative
`[combat] ship X killed by ship Y` lines), shots / damaging hits / hit-rate,
`maxTargeted` (bots holding a non-null engagement target), and per-bot
`maxVisibleEnemies` (cross-side ships in each bot's `ClientWorld` — the direct
test of port-mode cross-side visibility). Writes a trace to `$TMPDIR/p632_*.txt`
and prints `RESULT_P632`. Parametrised by env for size / seconds / broadcast
model / staging / debug, so the SAME harness ran at both bisect endpoints (a
temp worktree per commit) and in native mode.

The exact endpoints were run in dedicated temp worktrees
(`/tmp/p632-9194ddec`, `/tmp/p632-e1175e1b`) with the harness copied in; the
harness's API surface (`SpawnBot`, `GetCombatSummary`, `ActiveBots`,
`DiagnoseWorlds`, `Catalog.DefaultFighterHull/NativePreferredLoadout`) was
verified present at both endpoints.

## What we measured

### 1. Port mode kills at 30v30 — at both endpoints and at HEAD (the "say so plainly" case)

All numbers are real measured outputs (trace files under `/tmp`; the 
`[combat] … killed by …` lines are quoted in each trace).

- `9194ddec` port: **33 kills**, 60/60 reached, 59.2 % hit rate.
- `e1175e1b` port: **45 kills**, 60/60 reached, 70.7 % hit rate.
- `a325977f` (HEAD) port: **49 kills**, 60/60 reached, 60.9 % hit rate.
- `e1175e1b` native: **40 kills**, 56/60 reached, 67.6 % hit rate.

Port mode is not merely *able* to kill — at 30v30 it kills **comparably to
native**. Cross-side visibility works in port mode (each bot's world held the
real enemy population; `maxTargeted` ≈ fleet size; `GetCombatSummary` recorded
15k–19k real rounds with ~60–70 % landing). The P375 `CrossSideCombatVisibilityLiveTests`
also already proved, on current code, that a cross-side peer's entity reaches a
watcher's world with `SectorId == own`, a live pose and `IsNotVisible == false`.

### 2. Scale sweep (port, HEAD): the 0-kill is a high-density arrival problem

| size (bots) | staging | window | reached | kills | maxTargeted | maxVisEnemies |
|---|---|---|---|---|---|---|
| 30v30 (60) | off | 150 s | 60/60 | 49 | 59 | 31 |
| 60v60 (120) | on | 180 s | 120/120 | 77 | 119 | 60 |
| 100v100 (200) | on | 200 s | 200/200 | 78 | 180 | 99 |
| 150v150 (300) | on | 120 s | **101/300** | **0** | 98 | 53 |
| 150v150 (300) | off | 120 s | **98/300** | **2** | 94 | 51 |

Up to 200 total bots, every bot reaches Alderan and kills flow. At 300 bots only
~⅓ arrives and (almost) nothing dies — even though the bots that ARE present
fire and land their shots (53–65 % hit rate). The bottleneck is **getting 150+
ships of a side through the aleph choke-points to the rendezvous**, not the
server's per-tick delivery. Note the `maxVisibleEnemies` at 300 bots is ~53
(≈ the number that actually co-located), so the bots can only fight what reached
them.

### 3. The 195v195 standalone baseline is flat across the range (no regression)

Using the P619c-A/B harness pattern (`spawn_scenario showcase size_scale 13`,
m2xspiderw, Alderan, staging ON, port) against the Release server + MCP:

| commit | arrivals Alderan | kills | ticks/5 s (before / in-combat) | p95 (before / in-combat) |
|---|---|---|---|---|
| `9194ddec` | 341 | 0 | ~100 / ~100 | 24 / 37.7 ms |
| `a325977f` pre-fix | 221 | 0 | ~100 / ~100 | 14.9 / 18 ms |
| `a325977f` post-fix | 174 | 0 | ~100 / ~100 | 15.1 / 17.9 ms |

The server **holds 20 Hz (100 ticks/5 s) at 391 peers before contact and in
"combat"** — it is not stalling — but the fleets do not reach a fight on this
box at 390 bots. Critically, `9194ddec` ("good", which the morning 221 runs
attributed kills to) gives **0 kills locally too**. So the 0-kill observation is
*not* something introduced in the range; it is a local bot-driver arrival
capability limit.

### 4. Why 221 clash23 (and P622's local proof) showed 0 kills

The evidence above says the port broadcast is healthy. The 221 clash23 and P622
0-kill runs share three properties that match the measured local mechanism and
the task's own listed confounds:

- **Staging / routing imbalance** — clash23 had "only 109 arrivals in Alderan vs
  206 into Rigel (staging route)" and P622's proof staged the fleets. Our local
  runs reproduce exactly this: at high scale the majority of a side parks en
  route and never co-locates (101/300, 98/300 with staging on/off). When the
  two sides do not co-locate in strength, kills are ~0.
- **Join-burst dropouts** — clash23 dropped ~19 bots on `SendTimeout` during the
  ~400 Mbps join burst, leaving sides uneven. (Local standalone runs recorded no
  such dropouts; on a shared host this skews the sides further.)
- **P622's design** — its local 60v60 proof used a lone low-capacity target with
  staggered arrival; P622's anti-swarm selector holds the rest at the staging
  point, so the window was largely *hold*, not fight. That is a measurement
  design, not a broadcast defect.

Net: the 0-kills are a **bot-fleet arrival/engagement-coverage** effect, not a
**port-mode server broadcast** effect.

## The fix

While ruling out the broadcast, a genuine latent port-mode defect surfaced, and
it is fixed here with a failing-first regression test.

**Defect.** In `Server.Broadcast.cs`, `BroadcastShipsUpdatePeer` serves remote
ship poses through the deviation-ranked HEAVY budget (`cHeavyPerInterval = 2`
plus `cLightPerInterval = 2` folded into heavy in port mode = **4 full poses per
peer per tick**). In port mode, *teammate* positions additionally flow through
the per-tick `posBatch`, but a **cross-side enemy's pose reaches a bot ONLY
through this HEAVY budget** (there is no enemy posBatch). P604b made the HEAVY
quota density-aware for **native mode only** (`Server.Broadcast.cs:2746`,
`_nativeShipUpdates ? … : cHeavyPerInterval`), explicitly skipping port on the
belief that "port sends a per-tick posBatch anyway" — which is only true for
teammates. With the fixed 4 serves/tick, ~195 co-located enemies are each re-fed
only every ~49 ticks, far beyond the pure-renderer client's dead-reckoner
1.5 s capture horizon (`DeadReckonExtrapolationTicks = 30`, `Server.cs:714`) —
enemy poses freeze and fighting degrades.

**Fix.** Extend the P604b density-aware quota to port mode: the heavy quota is
now `max(cHeavyPerInterval, ⌈others / DeadReckonExtrapolationTicks⌉ + targetSlot)`
in **both** modes (`Server.Broadcast.cs:2745-2751`), so every remote ship —
teammate or enemy — is re-fed within the dead-reckoner horizon at any density.
At typical density (others ≤ 60) this equals the old 2, so wire bytes are
unchanged; only high density scales. The native path is byte-identical to before
(it already used this formula). Perf is unaffected: the post-fix 390-peer
baseline still holds 20 Hz.

Honesty note: this fixes the *latent* density starvation (the closest server-side
thing to the named symptom), but it does **not** by itself make the *local*
195v195 driver run kill — the binding constraint there is bot-driver aleph
routing at 390 local bots (see above), which is the P610/P539d/P631 transit
machinery, out of scope for P632. On a host where a 195-ship side actually
reaches the rendezvous, the density fix now guarantees enemy poses stay fresh so
the bots can fight.

### Regression test (failing-first)

`tests/Allegiance.Core.Tests/NativeShipUpdateModelTests.cs` →
`Port_mode_heavy_budget_scales_with_density_for_enemy_dead_reckoner_horizon`
(mirrors P604b's native density test, but with `TestSetShipUpdateModel(false)`
— port mode — and 195 cross-side NPCs):

- **RED on today's code**: port mode served only **4** heavy/tick at 195
  enemies → `4 < ⌈195/30⌉ = 7` → asserted failure ("got 4").
- **GREEN after the fix**: port mode serves ≥ 7 heavy/tick within the horizon.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Serve a deviation-ranked subset of cluster ships each tick | `SendShipUpdates`, FedSrv.CPP:4150-4165 | design (budget) | kept — the HEAVY/LIGHT budget is the shared skeleton for both modes |
| Client interpolates between sparse updates in its own local sim | `ProcessShipUpdate` ±1.5 s window, shipIGC.cpp:3113-3115 | design (era: client-side IGC sim) | **reclassified** — this port's client is a pure renderer with NO local sim, so poses are bridged by the dead-reckoner only; hence it genuinely "needs more" than native's fixed 2 heavy/tick (the P604b finding, now extended to port mode) |
| Teammate positions flow per tick | (port extension, no native analogue) | design | port mode's `posBatch` — teammates only; this is why enemies were accidentally starved |

**Check-native note:** the P604b/P632 density raise is a *correctness* change
for a pure-renderer port, not a porting of native's numbers. Native's fixed
`cHeavyPerInterval=2` only works because the real client runs a local IGC sim;
our client does not. The task's own "Native-faithful INTENT, not native-faithful
defects" rule classifies this as era-constraint compensation (P604b already did
it for native; P632 completes it for port). Classified as **era constraint** —
keep the intent, size for today.

## Modern alternative considered

| Aspect | Native | Modern option | Chosen | Why |
|---|---|---|---|---|
| Remote-ship pose delivery | fixed heavy/light budget + client local sim | snapshot interpolation / interest management with per-entity relevance + delta compression | density-aware quota (this fix) | The quota preserves the native budget shape (bounded per-peer bytes) and is a one-line change with a clean failing-first test. Full relevancy/interest management (per-recipient per-entity subscription) is a larger architecture (P631 territory) and out of scope; noted as the eventual right answer for the pure-renderer client. |

## Mandatory live check (forward)

The kill check is now part of the port-mode live gate:
`P632PortModeZeroKillsLiveTests.Assert_port_bots_kill_in_window` — a 30v30 fair
face-off asserting **bots reach the target AND fight (kills > 0 within N s)**.
Gated behind `ALLEGIANCE_LIVE_P632_ASSERT=1` so the pure-measurement probe never
aborts a run. Update the footer: "bots reach the target **and fight** (kills >
0 within 150 s at 30v30)".

## Verification

- **Repro numbers** for good/bad/fixed: table above (30v30 kills at both
  endpoints and HEAD; native kills; scale sweep; 195v195 flat across endpoints).
- **Bisect**: no first bad commit exists — port-mode combat is flat across
  `9194ddec..e1175e1b` (both endpoints kill at low density, both 0 at 390 local
  bots). Documented instead of a bisect log.
- **Regression test**: failing-first (RED "got 4" pre-fix, GREEN post-fix).
- **Whole-solution build**: 0 warnings, 0 errors.
- **Suite — 2 consecutive runs via `scripts/run-tests.sh` on the final code:**

  | Assembly | Run A | Run B |
  |---|---|---|
  | Lobby.Tests | 12/12 | 12/12 |
  | ClientCore.MonoGame.Tests | 1776/1779 (3 skipped) | 1776/1779 (3 skipped) |
  | ClientCore.Tests | 625/625 | 624/625 |
  | Core.Tests | 4002/4004 | 4002/4004 |

  **Zero P632 failures in either run** (the new P632 port-density test in
  Core.Tests and the P632 harness in ClientCore.Tests both pass; the live harness
  is env-gated so it exercises the gating path in the suite). All failures are
  the documented rotating live-wire load-flakes in `docs/TEST_FLAKES.md`
  (follow-up #4): Run A → `LiveCountdownStartPingTests.Multi_mission_countdown_…
  ` (line 37) and `GameOverPlayersWireTests.Win_by_elimination_…` (GameOverPlayers
  family); Run B → `ThreePeerSeededSessionTests.SeedThreePeers_…
  DeterministicVisibility` (line 38), `LiveChatSectorWireTests.Individual_…`
  (line 216), and `GameOverPlayersWireTests.Win_by_elimination_…`. Each was
  verified to pass **in isolation** on the final code (proving load-flake, not a
  P632 regression); the P632 change is byte-identical on sectors with ≤ 60 remote
  ships (`scaledHeavy = max(2, ⌈≤60/30⌉) = 2`), so it cannot affect these small
  live-wire tests. Reported as each run happened.
- **200v200 baseline locally**: server holds 100 ticks/5 s (20 Hz) at 391 peers
  before contact and in combat (p95 15–18 ms, max ~44 ms) — no stall; kills flow
  up to 200 bots locally (78 kills @ 100v100); the 390-bot local 0-kill is the
  bot-driver aleph-routing/arrival limit documented above, reproduced identically
  at `9194ddec` (i.e. not a range regression).

## Deliverables

- `src/Allegiance.Server/Server.Broadcast.cs` — port-mode density-aware heavy quota (P632 fix).
- `tests/Allegiance.Core.Tests/NativeShipUpdateModelTests.cs` — the failing-first port-density regression test.
- `tests/Allegiance.ClientCore.Tests/P632PortModeZeroKillsLiveTests.cs` (new) — the 30v30 repro + mandatory kill-check harness.
- `docs/reports/P632_PORT_MODE_ZERO_KILLS.md` (this report).

Committed on `master` (not pushed). See `git log -1`.
