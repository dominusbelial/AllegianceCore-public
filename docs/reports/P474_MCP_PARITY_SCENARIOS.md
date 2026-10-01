# P474 — allegiance-mcp scenario presets for live-verifying the parity features

Date: 2026-10-01 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p474-mcp-parity-scenario-presets-prompt.md`

## Summary

Added a `run_scenario` MCP tool to `tools/allegiance-mcp` with two live-verification
presets that exercise the parity signature mechanics unit tests can't make you *feel*:

- **`bomber_run`** — side A spawns an escorted bomber group whose bomber hull
  carries a real anti-base weapon **resolved from the core** (never hard-coded),
  flies to side B's garrison and attacks the station; side B gets a defender
  contingent. Reports station shield/hull over time, the P451 base-under-attack
  alert(s) side B's bots received, whether the station died, and — when it was
  side B's last restart station — the P455 events (`SIDE_INACTIVE`, the
  "is no more." chat, `GAME_OVER`).
- **`capture_attempt`** — a side-A boarding-capable hull (`c_habmBoard`) tries to
  dock with side B's station twice: first at full shields (report shows the P450
  refusal), then after an escort knocks the shields below the downed-shield
  constant (report shows the capture + side change as the bots observed it).

**Parity commits checked:** P450 (`3c9c588c`), P451 server (`9d9d4e4d`) + client
(`a4106a4c`) + writeup (`0ead5341`), P455 (`ff34d9cd`), P455b (`ba41a3f8`), P473
(`7134562a`) all exist on `master`. No preset was skipped.

---

## 1. Tool-shape decision: a new `run_scenario` tool (not a `scenario` arg)

The scope asked to "add a `scenario` argument, or a new `run_scenario` tool
(choose one and justify it)". I chose a **dedicated `run_scenario` tool**, for
four reasons:

1. **Different return contract.** `spawn_bot`/`spawn_scenario` return a short
   spawn confirmation; the presets return a large **structured observed-outcome
   report** (timestamped wire events), so they are a different tool shape —
   exactly why the similar self-contained `run_hull_matrix` is already its own
   tool rather than a flag.
2. **Own lifecycle.** A preset spawns a cohort, runs a multi-phase choreography
   for a real observation window, and **cleans up its own bots** when done (or on
   the timeout) — a unit of work, not an augment to the spawn-and-forget tools.
3. **Its own args.** `preset`/`size`/`target_sector`/`timeout_seconds` don't
   belong on the already-crowded `spawn_bot`/`spawn_scenario` schemas.
4. **Consistency.** The house pattern for "one call runs a whole scenario" is
   already a dedicated tool (`run_hull_matrix`); `run_scenario` follows it.

The design rule is honoured unchanged: **MCP sets intent; the choreography
(`BotManager.Scenarios.cs`) flies the bots at tick rate** through their own
`ConnectionSession` peers — the model never steers per tick.

### Files

- `tools/allegiance-mcp/ScenarioPlanner.cs` — pure, server-free resolution of the
  bomber/anti-base-weapon group and the `c_habmBoard` boarding hull from the real
  `PCore001.igc` corpus (no hard-coded ids).
- `tools/allegiance-mcp/BotManager.Scenarios.cs` — the two choreography presets,
  the live observer/event pipeline (station fractions, chats, side-active,
  game-over), and cleanup. `BotManager` became `partial` to host it.
- `tools/allegiance-mcp/BotManager.cs` — `ActiveBot.InScenario` flag; the generic
  Loop skips scenario-owned bots so its controller never races them.
- `tools/allegiance-mcp/McpTools.cs` — `run_scenario` tool definition + dispatch.
- `tests/Allegiance.ClientCore.Tests/BotSpawnerResolutionTests.cs` — 4 new unit
  tests for `ScenarioPlanner` against the real corpus.
- `tools/allegiance-mcp/Program.cs` — tool-list comment updated.

Both presets take optional `size` and `target_sector` with sensible defaults
(the default target sector is the side-B garrison's **own home sector**, resolved
live from the defender's world — see §4 finding), clean up their bots when done
or on the timeout, and `list_active_bots` reflects them while running (they are
real `ActiveBot`s, just gated out of the generic Loop while scenario-owned).

---

## 2. Anti-base weapon / boarding-hull resolution (pure + unit-tested)

`ScenarioPlanner.PlanBomberRun()` resolves, deterministically from the core:

- the **anti-base weapon** = the real weapon whose projectile has the greatest
  power (max per-shot damage) that actually mounts on the bomber hull via the
  real `CanMountPart` capability mask — so it is a genuine anti-station gun,
  never a hard-coded id;
- the **bomber hull** = the preferred bomb-carrying hull that hull-weapon pair
  fits (bomber-named hulls first, else highest-armour), guaranteeing the weapon
  genuinely mounts (so the server's P334 hull-swap `CanMountPart` gate won't drop
  it — the P432 lesson, reused);
- the **escort hull** = the same real fighter the spawn tools default to.

`ScenarioPlanner.PlanCaptureAttempt()` resolves the **boarding hull** = the
lowest-id real hull carrying `c_habmBoard` (the only hull a hostile ship can
capture with), plus the default fighter escort.

Four unit tests (`ScenarioPlanTests`) prove the resolution against the real
corpus: the anti-base weapon is a real weapon that mounts on the bomber hull and
is the strongest gun that hull can carry; the boarding hull really carries
`HabmBoard`; both escorts are the real default fighter.

---

## 3. Live verification — what ran, and the server-side navigation blocker

Live runs were against standalone `Allegiance.Server` instances (real
`PCore001.igc`, `m2xspiderw.igc` / `XXV_simple.igc`, teams 2) with P473's
`liveSettings.testHarness` grant ON (`prefix 'mcp-', money=1000000,
ignoreTechGate=True`), driven by the built `allegiance-mcp` over newline-
delimited MCP JSON-RPC (the `verify_stdio.py` pattern).

### What the scenarios demonstrably did live (evidence)

- `tools/list` exposes `run_scenario` alongside the other 7 tools.
- The scenario spawns its bots **through the real P473-granted spawn path** — the
  server log shows each `mcp-b…` bot `GRANTED wallet $1000000 at seat` and buying
  its hull (e.g. `swapping base hull … -> 13 ('Adv Scout')`).
- The correct enemy garrison is **discovered live from the defender's own world**
  and selected deterministically (the first run grabbed a wrong neutral
  "Garrison"; the fix in §4 lands the right one):
  `station { id: 24465, name: 'Omega Debug Garrison', sector: 1053, defenderSide: 0 }`.
- The live observer pipeline streams the **real station_state series from
  STATIONS_UPDATE(57)** for the whole window — e.g. 130 timestamped samples of
  `{ shield, hull, side }` (see §5 output excerpt).
- Real **BFS warp routing** is computed against the bot's live world graph, and
  the first warp hop completes:
  `[p433-diag] … cluster 1060 goal 1053 path[3] next=1052 graph=True warps=36 destMap=True warpFound=True`,
  then the bot appears in cluster 1052 (Alderan) mid-transit.

### The blocker (real server issue — reported, not fixed)

Neither preset could *complete* live, because the attackers could not finish the
warp transit to the enemy garrison on any server instance on this box:

- **Non-debug instances** (`m2xspiderw`, `XXV_simple`, `debugMode=false`): a
  freshly-seated opposing-side combat bot **freezes in place** — its live
  position never changes from spawn across a 36 s+ window, so it never even
  starts moving toward the warp. This is the documented P429 "combat bots freeze
  in place" server reality.
- **Debug-mode instance** (`debugMode=true`, which re-homes the map to
  Omega(1053)/Plato(1060) via the sealed Alderan(1052) hub): bots **move** and
  complete the **first** warp hop (Plato→Alderan confirmed), but **park at the
  aleph** on the second hop (Alderan→Omega) — `p433-diag` keeps reporting
  `cluster 1052 goal 1053 … warpFound=True` while the bot stays in 1052 for the
  rest of the window. Reaching the enemy garrison on this map is always 2 warp
  hops (there is no side-0 station in the 1-hop hub Alderan — verified), so the
  attacker never arrived at the station.

Net live result across the configurations tried (all evidence, no fix applied):
`attackerReachedStation: false`, station shield stayed `1.0`, so the P451 damage
path, the P450 refusal/capture, and the P455 elimination could not be exercised
this session. This is the **same class** as the documented P429 warp-export /
mobility follow-ups; it is a server-side navigation limitation, not a tool defect.

> **Named follow-up (server-side, out of scope here — not fixed):** make bot warp
> transit reliable for the second (departure) hop — or give the MCP scenario
> layer a supported way to co-locate/relocate its actors — so `run_scenario`'s
> flight presets can reach a hostile station and complete live. Track alongside
> the existing P429 mobility notes. (See §6 for the P474b scope note.)

---

## 4. Small real fixes surfaced during the live work (in-scope tool fixes)

While instrumenting the scenarios, the live runs exposed two tool-side bugs that
were fixed here (not server bugs):

1. **Wrong-station selection.** `FindTargetStation` originally picked the first
   side-B station in dictionary order when `target_sector` was omitted — it
   grabbed a neutral `Garrison` in Alderan instead of the defender's home
   garrison, and the attackers then couldn't route to it. Fixed: the implicit
   target sector is now the **defender's own home sector** (a freshly-seated
   defender sits in its home), so the scenario reliably targets the enemy's
   home garrison.
2. **Routing mismatch once mis-targeted.** The stale wrong-sector goal produced a
   `next` hop with no real warp (`warpFound=False`), stranding the attackers —
   a consequence of the mis-targeting, resolved by the same fix.

Neither touches server behaviour — both are corrected in `BotManager.Scenarios.cs`.

---

## 5. Structured observed-outcome report shape (with live excerpt)

The tool returns a timestamped, event-driven JSON report — real wire observations,
not a narrative. Excerpt from the definitive capture_attempt live run (debug
server, correct garrison target):

```json
{
  "preset": "capture_attempt",
  "sideA": 1, "sideB": 0,
  "plan": { "boardingHull": {"id":26,"name":"Hvy Troop Transport"},
            "escortHull":   {"id":13,"name":"Adv Scout"} },
  "station": {"id":24465,"name":"Omega Debug Garrison","sector":1053},
  "outcome": { "refusalConfirmed":false, "attackerReachedStation":false,
               "shieldsDownAt":-1, "captured":false, "stationSideStart":0,
               "stationSideEnd":0, "downedShieldConstant":0.1 },
  "events": [
    {"t":0.17,"kind":"station_selected","detail":{...}},
    {"t":0.46,"kind":"group_spawned","detail":{"boardingHull":"Hvy Troop Transport","escortHull":"Adv Scout","defenders":1}},
    {"t":1.0,"kind":"station_state","detail":{"shield":1,"hull":1,"side":0}},
    ... (130 samples, 1 Hz, shield/hull/side from STATIONS_UPDATE(57)) ...
    {"t":130.0,"kind":"scenario_end","detail":{"captured":false,"attackerReachedStation":false,...}}
  ]
}
```

`bomber_run` additionally reports `p451_base_under_attack` chat events (with the
station type's real voiceOver sound id), `station_destroyed`, `p455_side_inactive`,
`p455_eliminated_chat` and `p455_game_over` events, plus outcome flags
`stationDied`, `attackerReachedStation`, `killTime`, `firstUnderAttackAlertAt`,
`p451AlertCount`, `p455Events`. Both presets clean up their own bots in a `finally`
(so cleanup runs even on an exception / timeout).

---

## 6. Out of scope — P474b (named follow-up)

Per the brief, minefield and elimination-only presets are a **named follow-up,
P474b**, and are not included here. Minefields are the obvious next parity check
(the native `OT_mine` deploy/detonation arc, complementary to P433/P435's
equippable half); an elimination-only preset is a slimmed `bomber_run` that skips
the escorted-fight in favour of just driving the station's hull to 0 to isolate
the P455 `SIDE_INACTIVE` / "is no more." / `GAME_OVER` sequence.

Also out of scope (honoured): no new server features (the transit limitation in
§3 is reported as a named follow-up, not fixed here), and no vizfight changes or
retirement.

---

## 7. Build and tests

- Whole-solution build: `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors**.
- `ScenarioPlanTests` + `BotSpawnerResolutionTests`: 20/20 passed (real corpus).
- Full suite: 3 consecutive `scripts/run-tests.sh` runs (the P471 sanctioned
  full-suite runner: build once, four test assemblies sequentially). Per-run
  failures (none reference P474 symbols — P474 adds only the MCP tool +
  ClientCore pure-unit tests, none of which the failing live-network tests touch):

  | run | failures |
  |---|---|
  | 1 | `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`, `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_…`, `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`, `BotFlightTests.Abrupt_disconnect_during_broadcast_…` |
  | 2 | `DockOut_WatcherLatchesGone_UndockReturnsAndClears`, `ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_is_served_the_next_…` |
  | 3 | `DockOut_WatcherLatchesGone_UndockReturnsAndClears`, `P366LeaderBotCommandLiveTests.Leader_commands_same_side_bot_to_return_to_base_…`, `LiveRipcordTests.Re_target_activates_to_requester_only`, `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`, `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_…`, `CargoBayReloadTests.Reload_malformed_entry_refuses_rest_…` |

  The one failure present in **all three** runs, `DockOut_WatcherLatchesGone`, is
  the ledger's documented **real bug (P472)** (`docs/TEST_FLAKES.md`, fails-in-
  isolation Yes, 3/3 pre-existing) — untouched by P474. The rest are the
  documented live-wire **"suspect (P472)" / "load-flake"** family (live-socket
  timing under full-suite load), each appearing in a single run with no stable
  victim — the textbook load-flake signature. All are pre-existing on `master`;
  P474 changes no server or core logic that any of these tests exercises.
