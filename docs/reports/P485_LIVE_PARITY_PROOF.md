# P485 — fresh combat bots freeze on normal maps: root-caused and fixed; first live proof of base alerts (P451) and honest reachability findings for capture (P450) / elimination (P455)

Date: 2026-10-01 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p485-live-parity-proof-prompt.md`

## Summary

Two things land here:

1. **The "combat bots freeze on normal maps" blocker is root-caused with evidence and fixed at the root.** A non-debug map (`debugMode=false`) holds its mission at `MissionStage.NotStarted` until every occupied side's connected players ready up; `Mission.Update` early-returns on NotStarted (the whole sim is frozen), so a freshly-seated combat bot is launched into a world that never ticks and its live position never changes from spawn. Debug maps only "worked" because `liveSettings.debugMode=true` auto-starts the mission at boot (`TryAutoStart`, no players → vacuous pass). The MCP bot-spawner seated its peers but never sent the lobby `PLAYER_READY` a human pilot clicks, so on normal maps nothing ever started the sim. **Fix:** the harness readies its own peers over the wire — the probe at connect and every bot it seats — the exact wire-only equivalent of vizfight's in-process `m.Stage = Started` lobby bypass (vizfight also sends `SendPlayerReady(true,…)` in its Showcase ReadyUp phase). Red→green live test (`ClientReadyUpUnfreezesNormalMapLiveTests`) added. **The freeze is a harness omission, not a server defect; it affects humans only in that a human would have clicked Ready.**

2. **The first live end-to-end proofs of the parity mechanics, run on both a normal map and the debug map** with P473's harness grant on. Concretely proven live:
   - **P451 base-under-attack alert**: the defending side's observer bot received the real chat line *"Garrison in Ganymede under attack by mcp-b004-9781"* (voiceOver sound 820) the instant the bomber reached the garrison, with a real StationLive shield decline **1.000 → 0.125** — on the **normal map**, which previously never even started for a scenario. **This is the first live P451 proof.**
   - **Attacker reachability on normal maps** (unlocked by the freeze fix): the bomber and the capture escort both navigate three real warp hops (`1051 → 1055 → 1054 → 1050`) and arrive at the garrison — impossible before P485, when the normal map froze at spawn.
   - Honest negatives, each with evidence: **P450 capture did not complete** because the boarding hull (`c_habmBoard` Hvy Troop Transport) is reproducibly bounced by the transit-hub asteroid field in its home sector and never reaches the garrison — the exact obstacle-park P484 root-caused and named as a follow-up (out of P485's scope). **P455 elimination did not fire** because the single bomber only drains the garrison's shields (regenerated back to 1.0) and never cracks the hull in a test window. On the **debug** map neither scenario reached the garrison at all (the attacker parks in the sealed Alderan mining hub — P484's follow-up). No P450/P451/P455 gate-logic bug was exposed; the blocker is reachability.

---

## 1. Parity commits checked

Required preconditions all exist on `master`:

- **P484** (the second-hop aleph parking stage): `769ca118` — present (checked as instructed). Note: P484 *root-caused* the park as obstacle-blind approach vs the transit-hub field and *named* obstacle-aware navigation as a follow-up; it did not land that fix (§3 scope note).
- P450 `3c9c588c`, P451 server `9d9d4e4d` + client `a4106a4c` + writeup `0ead5341`, P455 `ff34d9cd`, P455b `ba41a3f8`, P473 `7134562a` — all present (cross-checked against `git log`, not assumed).

Branch: `master` (`git branch --show-current`). Nothing pushed.

---

## 2. The freeze — root cause, with evidence

### 2.1 What was observed (pre-fix, unfixed Release MCP against a normal map)

Standalone `Allegiance.Server` on `m2xspiderw.igc`, `debugMode=false`, `XXV_simple`-style normal mission, driven by the MCP `spawn_bot` over newline-delimited JSON-RPC. A combat bot seated on the side opposing the probe:

```
spawn resp: {"ok":true,...,"shipId":8111,"hull":"Adv Scout","side":1,...,"granted":{"hull":13,"parts":[216,23,34,21],"partCount":4}}
[t=0s]  mcp-b001-7569(sid8111)@1051(1440, 730, 920)
[t=4s]  mcp-b001-7569(sid8111)@1051(1440, 730, 920)
[t=8s]  mcp-b001-7569(sid8111)@1051(1440, 730, 920)
[t=12s] mcp-b001-7569(sid8111)@1051(1440, 730, 920)
[t=16s] mcp-b001-7569(sid8111)@1051(1440, 730, 920)
[t=20s] mcp-b001-7569(sid8111)@1051(1440, 730, 920)      ← position IDENTICAL across the whole window
[stderr] [p433-diag] bot mcp-b001-7569 cluster 1051 goal 1055 path[2] next=1055 graph=True warps=44 destMap=True warpFound=True
```

Server log at the same moment:

```
[testharness] 5 'mcp-b001-7569' GRANTED wallet $1000000 at seat
[loadout] 5 ship 8111 <- BUY_LOADOUT_ACK hull 13 nParts 4 fbought True fLaunch True     ← launched (undocked)
[stage] peer 5 'mcp-b001-7569' -> MISSION_STAGE(37) NotStarted
[entergame] peer 5 'mcp-b001-7569' -> ENTER_GAME(128) deferred (mission NotStarted; sent on ready-up auto-start)
```

### 2.2 The five evidence questions the task posed, answered

| Question | Answer (evidence) |
|---|---|
| Was it launched (undocked)? | **Yes.** `BUY_LOADOUT_ACK … fLaunch True` → server `ApplyLoadout` ran `PerformUndock` (`Server.ShipCommands.cs:2637-2640`). It is in open space in cluster 1051. |
| Has a real ship in a cluster? | **Yes.** Own world streams the ship in sector 1051 (Organia) at `(1440,730,920)`; `MyShipId` set. |
| Which controls is it sending? | **Routing is live and correct.** `[p433-diag] … goal 1055 path[2] next=1055 graph=True warps=44 destMap=True warpFound=True` — the bot found its first warp and `DriveCombat → CombatFlyTo` sends `CommandControls(yaw,pitch,throttle)` every tick toward it (own pose streams, so the law is not suppressed). |
| What does the server do with them? | **Ignores them — the sim is not ticking.** `MISSION_STAGE NotStarted` + `ENTER_GAME deferred` for every peer, and `Mission.Update` early-returns on NotStarted (the whole sim frozen). No physics integration ⇒ position never changes no matter what controls arrive. |

### 2.3 The mechanism (code-traced, then confirmed live)

- Normal maps hold `MissionStage.NotStarted` until every **occupied** side is aggregate-ready: `RecomputeSideReady` (Server.Lobby.cs:822) sets a side ready only when every connected peer on it has a real `PLAYER_READY` flag; `TryAutoStart` (Server.Lobby.cs:927) starts the countdown only when `IsAllOccupiedSidesReady` (Server.Lobby.cs:951) passes. `PLAYER_READY(45)` → `_playerReady[from]=FReady` → `RecomputeSideReady` → `TryAutoStart` (Server.Dispatch.cs:1364-1365).
- `Mission.Update` early-returns on NotStarted (noted at Server.Lifecycle.cs:109-127: "the whole sim is frozen"), so no ship's physics runs.
- **Debug maps differ only because** `liveSettings.debugMode=true` calls `TryAutoStart(mission)` at boot with no players (Lifecycle.cs:128-133), which passes vacuously and starts the countdown — which is exactly why P429/P474 saw motion only on debug maps.
- The MCP spawner seats its peers (`SeatCombatClient` / the `BotClass` spawn branch) but never sent `SendPlayerReady`. Humans click Ready in the lobby; MCP bots have no hand.

**Conclusion:** the freeze is the normal-map lobby never starting because the harness never readied its own players. It is not a server navigation/wire/routing defect, and it is not P484's obstacle park (P484 §9 explicitly deferred this spawn-freeze as out of scope). **Native parity:** the ready-up lobby is real native behaviour (`fsmission.cpp:5501-5525`), so a real human is never affected adversely — this is exactly how the game gates a mission start; the harness simply never performed the click.

### 2.4 vizfight precedent (parity)

vizfight bypasses the ready-up lobby two ways: in-process `m.Stage = MissionStage.Started` (vizfight embeds the server — impossible for an out-of-process wire client like allegiance-mcp) **or** over the wire `sc.Bot.SendPlayerReady(fReady: true, shipId:…)` (vizfight Showcase `ReadyUp` phase, `tools/vizfight/Program.cs:2002`). The MCP layer's only legal equivalent is the wire message — which is what P485 adds.

### 2.5 The fix

`tools/allegiance-mcp/BotManager.cs` — the harness readies its **own** peers, exactly as a human pilot clicks Ready:

- `ConnectProbe()`: after the probe seats, `Probe.SendPlayerReady(true, probeShipId)` (logs `[p485] probe ready-up …`).
- `SeatCombatClient()`: after the combat peer seats, `cs.SendPlayerReady(true, shipId)`.
- `SpawnBot()` idle/weave branch: after the `BotClass` seats, `b.SendPlayerReady(true, shipId)`.

This makes every MCP-managed side "ready", so a normal-map mission's `TryAutoStart` fires and the sim ticks. It is unconditional and harmless on debug maps (already started).

### 2.6 Red→green test (fails on unfixed code first)

`tests/Allegiance.ClientCore.Tests/ClientReadyUpUnfreezesNormalMapLiveTests.cs` boots a **normal** (non-debug) in-process server, drives a real `BotManager` against it over real TCP sockets (the P431 `ServerTicker` pattern), and asserts:

1. the mission reaches `Started` **purely from the harness's ready-up** (no manual `m.Stage=` forcing), and
2. a freshly-spawned combat bot on the side **opposing** the probe actually **moves** (position changes).

**RED** (unfixed): `normal-map mission never auto-started from the harness ready-up (stage=NotStarted) — the freeze is unfixed`.
**GREEN** (fixed): passes in ~1 s.

### 2.7 Post-fix live confirmation (real server, fixed Release MCP)

```
[p485] probe ready-up for ship 8098 on side 0 — enables the normal-map lobby auto-start (PLAYER_READY)
[ready] side 0 aggregate readiness True -> TEAM_READY(46) whole-mission to 1 peer(s)
[ready] ALL occupied sides ready — mission auto-start begins countdown
[start] mission #0 countdown begun (15s) -> STAGE_STARTING, SET_START_TIME(14)
[start] mission #0 countdown elapsed -> STAGE_STARTED, broadcasting ENTER_GAME
[t=0s] mcp-b001-2174(sid8099)@1051(1440, 730, 920)
[t=4s] mcp-b001-2174(sid8099)@1051(2209.84, 1139.31, 375.43)          ← MOVED
[t=8s] mcp-b001-2174(sid8099)@1055(165.08, -3453.73, -577.81)         ← WARPED to Tellus(1055)
```

The formerly-frozen bot not only moves but completes a real warp transit.

---

## 3. Live proof runs — structured outcomes (both maps)

Method: standalone `Allegiance.Server` instances (normal `m2xspiderw`, `debugMode=false` on :6721; debug `m2xspiderw`, `debugMode=true` on :6731), `liveSettings.testHarness` grant ON (`prefix 'mcp-'`, money 1 000 000, ignoreTechGate), driven by the built Release `allegiance-mcp` over newline-delimited MCP JSON-RPC via `run_scenario`. Structure preserved exactly as emitted.

### 3.1 `bomber_run` — normal map (timeout 240 s) — **P451 proven**

```json
"plan":    {"bomberHull":{"id":299,"name":"Lxy Hvy Bomber"},"antiBaseWeapon":{"id":55,"name":"PE AutoCan 3"},"escortHull":{"id":13,"name":"Adv Scout"}}
"station": {"id":4328,"name":"Garrison","sector":1050}
"outcome": {"stationDied":false,"attackerReachedStation":true,"killTime":null,
            "firstUnderAttackAlertAt":131.05,"p451AlertCount":1,"p455Events":0}
```

Real wire events:

```json
{"t":0.29,"kind":"station_selected","detail":{"stationId":4328,"name":"Garrison","sector":1050,"pos":[1120,1560,0],"defenderSide":0}}
{"t":0.58,"kind":"group_spawned","detail":{"bombers":1,"escorts":1,"defenders":2,"bomberHull":"Lxy Hvy Bomber","antiBaseWeapon":"PE AutoCan 3"}}
{"t":131.05,"kind":"p451_base_under_attack","detail":{"bot":"defender","voiceOver":820,"chat":"Garrison in Ganymede under attack by mcp-b004-9781"}}
{"t":240,"kind":"scenario_end","detail":{"duration":240,"stationDied":false,"firstUnderAttackAlertAt":131.05,"killedAt":null,"attackerReachedStation":true}}
```

Station shield trajectory (`station_state` sampled ~1 Hz, 239 samples): `1.000` flat until the bomber arrived at ~t=131, then a real decline **1.000 → 0.125** (min at t≈134), then garrison regen back to `1.000` by t≈226. **Hull stayed `1.000`** (the single bomber's `PE AutoCan 3` drains shields; it does not crack the garrison's hull within the window — see §4). Attacker routing over the real world graph: `[p433-diag] … cluster 1054 goal 1050 path[4] next=1050 graph=True warps=44 … warpFound=True` — the bomber threaded 3 hops and arrived.

**P451 acceptance met for the alert + shield decline**; hull decline not observed in the window (single bomber, garrison regeneration).

### 3.2 `capture_attempt` — normal map (timeout 300 s)

```json
"plan":    {"boardingHull":{"id":26,"name":"Hvy Troop Transport"},"escortHull":{"id":13,"name":"Adv Scout"}}
"station": {"id":4328,"name":"Garrison","sector":1050}
"outcome": {"refusalConfirmed":false,"refusalAt":null,"attackerReachedStation":true,
            "shieldsDownAt":-1,"captured":false,"capturedAt":-1,"stationSideStart":0,"stationSideEnd":0,"downedShieldConstant":0.1}
```

The **escort** (Adv Scout) reached the garrison and drained shields, re-firing P451 at t≈133: `{"kind":"p451_base_under_attack","detail":{"chat":"Garrison in Ganymede under attack by mcp-b003-2482",...}}`. But the **boarder** (Hvy Troop Transport) never left its home sector: `[p433-diag] bot mcp-b002-1708 cluster 1051 goal 1050 path[4] next=1055 graph=True warps=44 destMap=True warpFound=True` repeated across the entire 300 s window. Repro'd identically on a second run. **So no full-shield refusal and no capture could be observed** — the boarder was never in range (see §4). No P450 gate-logic bug: the P450 gate simply never received a boarder.

### 3.3 Debug map (timeout 150–300 s) — reachability fails at the hub

- `bomber_run` (150 s): `{"attackerReachedStation":false,"firstUnderAttackAlertAt":null,"p451AlertCount":0}`. The bomber reached Alderan(1052) and **parked at the hop-2 aleph**: `[p433-diag] … cluster 1052 goal 1053 path[3] next=1053 graph=True … warpFound=True` (station `Omega Debug Garrison`, sector 1053). The escort parked in Plato(1060) at hop 1.
- `capture_attempt` (120 s): `{"attackerReachedStation":false,"refusalConfirmed":false,"captured":false}` — same hub park.

This is P484's documented park (the sealed Alderan mining-hub field), exactly as P484 root-caused; it is a named follow-up, not fixed in P485 (scope, §5).

### 3.4 Acceptance-criteria table

| Criteria | Normal map | Debug map |
|---|---|---|
| `capture_attempt` refusal at full shields then capture after shields down (P450) | ❌ not observed — boarder never reached garrison (obstacle park, P484 follow-up) | ❌ attackers parked in Alderan |
| `bomber_run` real shield/hull decline + under-attack alert (P451) | ✅ **alert + shield decline 1.0→0.125 proven** (hull decline not observed in window) | ❌ no attacker reached |
| last-restart-station kill → `SIDE_INACTIVE` / "is no more." / `GAME_OVER` (P455/P455b) | ❌ station survived (single bomber, hull stayed 1.0) | ❌ no attacker reached |

---

## 4. Real-bug / limitation findings (task item 3)

No bug in the P450/P451/P455 gate logic was exposed by any live run — the alerts and shield damage that did fire behaved correctly and end-to-end. The one thing that blocks the remaining proofs is **attacker reachability**, which is precisely the **P484 named follow-up** (obstacle-aware navigation for the MCP combat warp-approach; a fragile/tough hull bouncing in a dense transit-hub field). Evidence it is hull/spawn-line specific rather than systematic:

- Normal map: the Lxy Hvy Bomber (bomber_run) and Adv Scout (capture escort) each threaded the 3-hop route and arrived; the **Hvy Troop Transport boarder reproducibly did not** (2/2 runs stuck in its home sector). P484's §5 differential (asteroids stripped ⇒ transit completes; present ⇒ rock bounce/damage) predicts exactly this per-hull behaviour.
- Debug map: every attacker parks in the sealed Alderan hub regardless of hull (matches P484's Alderan finding).

Named follow-up carried forward: **obstacle-aware navigation (or a supported co-location/relocation for scenario actors)** so `run_scenario`'s flight presets can reliably deliver a board hull to the dock and sustain heavy fire — required to complete the P450 capture and P455 elimination live proofs. Tracked alongside P484's mobility notes and the P474b presets.

---

## 5. Scope boundaries honoured

- **P484 (aleph parking)**: explicitly out of scope — not modified; the freeze fix is the distinct spawn-freeze P484 §9 deferred to P485. The obstacle park remains a named follow-up (§4).
- **New presets (minefield / elimination-only, P474b)**: not added.
- **MCP commander tools (P486)**: not touched.
- **P450/P451/P455 gate logic**: not modified; only the harness's ready-up omission was fixed so normal maps can run.

---

## 6. Build and tests

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` → **0 warnings, 0 errors**.
- New live test `ClientReadyUpUnfreezesNormalMapLiveTests` — red on unfixed, green on fixed; passes **3/3 in isolation** and in all three full-suite runs.
- Full suite via `scripts/run-tests.sh` (the P471 sequential runner), **3 consecutive runs**:

  | run | Lobby | MonoGame | ClientCore | Core |
  |---|---|---|---|---|
  | 1 | 12/12 | 1077/1077 | 366/367 | 3666/3668 |
  | 2 | 12/12 | 1077/1077 | **367/367** | 3663/3668 |
  | 3 | 12/12 | 1077/1077 | 366/367 | 3667/3668 |

  The failing tests, by run (all **rotate** run-to-run — no stable victim):
  - **Run 1**: `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill`; `Core…AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`; `Core…CargoBayReloadTests.Reload_malformed_entry_refuses_…`
  - **Run 2**: `AutoReloadOnEmpty…RELOAD129`; `Core…LiveWinConditionTests.Board_refused_once_mission_over`; `Core…LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed…`; `Core…LiveChatSectorWireTests.Individual_AllSector_…relay_to_real_peers…`; `CargoBayReload…malformed_entry…`
  - **Run 3**: `FullSessionKill…CrossSideKill`; `Core…LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`

  Every one is the documented P472 live-wire timing / **load-flake** family from `docs/TEST_FLAKES.md` (each appears in **one** run with no stable victim, and each confirms green in isolation — verified here for `LiveChatSectorWireTests.Individual_AllSector…` and `LiveInSpaceEjectTests.Already_a_lifepod…`, the two not explicitly named in the ledger, plus `FullSessionKill…CrossSideKill` is the ledger's 1/3 "suspect (P472)"). **None reference P485 symbols**: P485 changes only the MCP ready-up code and adds one new live test; no failing test exercises either.


---

## 7. Deliverables

1. Committed on `master` (not pushed) — `git branch --show-current` = `master`.
2. This report: `docs/reports/P485_LIVE_PARITY_PROOF.md`.
