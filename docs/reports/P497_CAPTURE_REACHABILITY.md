# P497 — a shields-down board hull can't reach the dock point: the blocker was the bot's own dodge, not the server; the live P450 capture proof now passes

Date: 2026-10-02 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p497-capture-reachability-prompt.md`

## Summary

P496 (§4) reported the live blocker as a *server-side dock-geometry reachability
limit*: the capture trigger requires a ship within **`DockRadius` = 200 u** of the
station centre (`Sector.cs:1501`), while the station's collision body held the
boarder at ~500–585 u — so, it reasoned, no open-flight ship could ever reach the
buried dock point. **That hypothesis was wrong, and this task disproves it with
evidence and fixes the real cause.**

Instrumenting a live `capture_attempt` revealed the true mechanism:

1. The **server** reachability path is correct and needs **no change**. A
   different-side `c_habmBoard` hull with the station's shields below
   `DownedShieldFraction` *is* excluded from the ship↔station bounce
   (`Sector.cs IsDockOrBoardCandidate`, the port's anticipation described in the
   brief), so it penetrates the hull; the new failing-first **open-flight loopback
   test** flies such a hull ballistically from *outside* the hull, through it, to
   the 200 u dock point, and **captures**. Named cause 1 (exclusion/ordering) is
   thereby **ruled out with a failing-test-shaped proof**.
2. The live blocker was the **boarder's own P489 dodge law** (cause 2). The bot's
   `TryDodgeGoal` treats the huge garrison (radius ≈ 423.5 u on the normal map) as
   a collision obstacle and perpetually deflects the boarder *around* it — measured
   live: the dodge fired **1299 times**, producing dodge goals ~520 u off-path, so
   the transport hovered at ~430 u and could never converge on the buried 200 u
   dock point. Native boards by flying *into* the bay (`InGarage`, shipIGC.cpp:853);
   it does not evade the base it is capturing.
3. **The fix** (bot law, cause 2): a `c_habmBoard` hull does not dodge stations
   (`BotManager.ObstaclesIn` drops stations from a board hull's obstacle set;
   asteroid/ship avoidance unchanged). With it, the boarder sails straight through
   the hull gap to the dock point and captures.

Finally the live P450 proof closes: on the normal map the scenario now observes
**`refusalConfirmed: true` → shields below 0.1 → `captured: true`** with the
station's side flipping 0→1. (Two further discoveries surfaced and were fixed in
the *observation* layer: the side-B defender-observer goes blind the moment a
captured station flips to side A — P429 cross-side invisibility — so the scenario
now reads the flip from the side-A **boarder**, and the abandon-on-vanished guard
no longer demands shields-down, since a successful capture *restores* the shield.)

The debug map (`debugMode=true`) captures **`shieldsDownAt: -1`** — the escorts
never drop the Omega garrison's shields below 0.1 there (shield stays 1.0 all
300 s), a pre-existing escort-knock/durability phenomenon on that map
(station-ramming damage tuning is explicitly out of scope), *not* a reachability
defect: the boarder reaches the garrison and the refusal confirms. Honest partial
result; reachability is proven by the normal map and by the open-flight loopback.

---

## 1. Diagnosis with evidence — the blocker is the bot's dodge, not the server

### 1.1 The open-flight loopback test rules out the server (cause 1)

The existing P450 capture test (`StationCaptureLiveTests.Hostile_boardable_ship_…`)
*teleports* the capturer to a spot already inside the dock radius
(`AtBase`, dist 0). The brief asked for the variant that **flies in from outside
the hull**. Added as Scenario 4 of `ShipStationCollisionTests`:

`Hostile_shield_down_board_hull_flying_in_from_outside_the_hull_captures`

- Plants a synthetic station with **radius 300** (> `DockRadius` 200, so the dock
  point is genuinely buried inside the hull), drains its shield to 0 via the real
  `Station.ReceiveDamage` path (stationIGC.cpp:172), and coasts a real `c_habmBoard`
  hull **ballistically** (`CoastButton` — no autopilot, no bot law, no dodge) from
  **clearly outside the hull** straight at the centre.

Result (measured):

```
P497-LOOPBACK: board hull side 1 coasted from 334.1 to 140.9 u
                (dock point ≤ 200, hull surface 311.4); station side 1
```

The ship physically traversed the hull (334 → 140 u), reached the dock point, and
**captured** (station re-sided to side 1). This is the failing-first loopback: it
**passes on the committed code** because the server exclusion already works — the
brief's candidate cause 1 is disproven with evidence. The live blocker therefore
had to be the bot.

### 1.2 Live instrumentation names the bot dodge (cause 2)

Temporary per-tick diagnostics in `Sector.ResolveShipStationCollisions` /
`ResolveDockCollisions` (removed before commit; the server file is unchanged) and
in `BotManager.TryDodgeGoal` measured, on the live normal map:

- With shields down the collision pass correctly logged **`excl=True`** (the board
  exclusion fired) and the boarder **penetrated** the hull surface (438.5 u →
  ~430 u) — it was *not* being bounced by the station.
- **No asteroid or ship-ship bounce involved the boarder** (0 events in both
  passes), so neither of those pinned it.
- The P489 dodge fired for the boarder **1299 times**, emitting dodge goals ~520 u
  off-path (e.g. `dodgeGoal dist=520`), pulling it tangentially around the garrison;
  its centre-distance hovered at ~430 u and it never converged to 200.

So the boarder was deflected by **its own dodge law** away from the very station it
was meant to board. Native's board path is `InGarage` (shipIGC.cpp:853) — the ship
flies *into* the bay; it does not evade the base. The port's MCP bot inherited the
generic dodge that treats every station as an obstacle, which is wrong for a
boarding hull.

### 1.3 The server-side capture is confirmed (independent of the observer)

Adding a server-side log at the capture gate (removed before commit) confirmed the
mechanism end-to-end on the fixed run:

```
[P497-CAPTURE] boarder 8100 captures station 4328 at dist 198.6 (DockRadius 200), rtShield 0, armor 0.928
```

The station re-sided server-side with **no station destruction**. Yet the scenario
still reported `captured:false` — which led to the *observation*-layer fixes in
§2.2.

---

## 2. The fix

### 2.1 Bot law: a board-capable hull does not dodge stations (`BotManager`)

`TryDodgeGoal` now computes `dodgeStations = !hull.HasCapability(HabmBoard)` and
passes it to `ObstaclesIn`, which, when false, **omits every station** from the
boarder's obstacle set. Asteroid and ship avoidance are unchanged. Semantics:
a `c_habmBoard` hull's purpose is to fly into a base's hull to its buried dock
point and capture; dodging that base defeats the boarding intent (and P489's dodge
applies to the *approach/dodge* of threats, which a base being boarded is not).
The only board-capable use in the harness is the capture boarder, so no other
scenario's behaviour changes.

### 2.2 Observation layer: the scenario must read the flip from the boarder

Two defects in `capture_attempt`'s observation were uncovered once capture actually
started happening, and fixed in `BotManager.Scenarios.cs`:

1. **Observer blindness on success.** The scenario read the station's side via the
   **side-B defender** observer. The instant the station is captured it becomes
   side A — **cross-side-invisible** to that observer (P429) — yet the stale record
   stays "present", so the defender's `side` never changes. `captured` therefore
   stayed false even though the server captured. Fixed: the scenario also reads the
   station from the **side-A boarder**, for whom the station transitions from
   absent/side −1 to `side == sideA` exactly on capture (`boarderSeesOwn`), and
   accepts that as the capture signal alongside the defender's flip and the
   boarder's dock. `capture_succeeded.observedVia` records which fired.
2. **Abandon-on-vanished guard.** The loop previously abandoned on
   `!present && shield < downed`; but a successful capture **restores** the
   station's shield (post-capture heroic fudge → 0.7), so that guard would
   "press the eject" exactly when the capture we want to observe succeeds. Fixed:
   the guard now only checks the boarder is not at the dock footprint.

These are pure observation/reporting fixes — they do not alter the P450 gate or
the server capture path.

---

## 3. The final live P450 proof

Method: in-process `Allegiance.Server` on `m2xspiderw`, P473 test-harness grant,
driven by a real `BotManager` running production `RunScenario("capture_attempt",
size: 3, …)` — the same driver as P496. Opt-in `ALLEGIANCE_LIVE_CAPTURE_PROOF=1`.

### 3.1 Normal map (`m2xspiderw`, `debugMode=false`)

```json
"outcome": {"refusalConfirmed": true, "refusalAt": 33.815, "attackerReachedStation": true,
            "shieldsDownAt": 41.430, "captured": true, "capturedAt": 42.131,
            "stationSideStart": 0, "stationSideEnd": 1, "downedShieldConstant": 0.1}
```

Key real wire events (side-B defender observer; the capture is observed via the
side-A boarder, see §2.2):

```json
{"t":33.8,"kind":"capture_refused_full_shield","detail":{"stationSide":0,"shield":1,"boarderDistanceToCentre":439}}
{"t":41.4,"kind":"shields_down","detail":{"shield":0,"hull":0.913,"downedConstant":0.1}}
{"t":42.1,"kind":"capture_succeeded","detail":{"oldSide":0,"newSide":1,"stationId":4328,"shield":0,"observedVia":"boarder_sees_own"}}
{"t":42.1,"kind":"scenario_end","detail":{"captured":true,...}}
```

✓ **`refusalConfirmed: true`** at full shields — the P450 shield-up gate rejects the
   boarder (boarder's closest approach 439 u recorded while shields up).
✓ **shields below the downed constant** (`shieldsDownAt: 41.4`, shield 0) — the
   escorts drain `ShieldFraction` below 0.1.
✓ **`captured: true` at 42.1 s** — the boarder (now dodging no stations) flies
   through the hull to the dock point; the server captures (dist ~198 < 200) and
   the station's observed **side flips 0 → 1**.

That **closes the parity pass's last live-proof gap** (P490/P496's unfinished
item), now with the mandatory `c_habmBoard` transport physically reaching the
buried dock point.

### 3.2 Debug map (`m2xspiderw`, `debugMode=true`, 300 s)

```json
"outcome": {"refusalConfirmed": true, "refusalAt": 28.651, "attackerReachedStation": true,
            "shieldsDownAt": -1, "captured": false, "capturedAt": -1,
            "stationSideStart": 0, "stationSideEnd": 0, "downedShieldConstant": 0.1}
```

The boarder reaches the Omega garrison (refusal confirms at 589 u, `attackerReachedStation:
true`), but the escorts **never drop the garrison's shields** — `station_state` shows
`shield = 1.0` for the entire 300 s window (`shieldsDownAt: -1`), so no capture is
possible on that map. This is a shield-**knock/durability** phenomenon on the debug
map's garrison (escort survival / station shielding on that topology), **not** a
capture-reachability defect: reachability itself is proven by the normal map and by
the open-flight loopback test. Station-ramming damage tuning is explicitly out of
scope; named as the debug-map follow-up.

---

## 4. Scope boundaries honoured

- **Server flight physics / collision / P450 gate / DockRadius approximation:**
  untouched — the server file has **zero net change**; the loopback test proves the
  existing board exclusion already makes capture reachable in open flight. The
  documented `DockRadius` approximation is retained (porting native `InGarage`
  land-slot geometry remains a named follow-up, not needed for reachability).
- **Friendly docking** (P53/P381/P482): untouched and green (§5).
- **P489 dodge mechanics / P496 warp approach:** the dodge *law itself* is
  unchanged; only the board hull's **obstacle set** stops listing stations (the
  minimal change needed for boarding intent). Non-board bots keep dodging stations
  exactly as before.
- **Station-ramming damage tuning:** out of scope; the debug map's shield non-drop
  is reported, not tuned.

## 5. Build and tests

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors**.
- New open-flight loopback test: **1/1 pass**
  (`ShipStationCollisionTests.Hostile_shield_down_board_hull_flying_in_from_outside_the_hull_captures`).
- Targeted docking / capture / rescue / ramming suites (`ShipStationCollisionTests`,
  `DockDockingTests`, `StationCaptureLiveTests`, `StationCaptureTests`,
  `StationRescueModelTests`): **23/23 pass** — friendly docking, the P126/P450
  capture tests, and the P466 ram/lifepod/exclusion scenarios all stay green.
- Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (measured
  per-assembly Passed!/Failed! lines; Core total is 3699 = 3698 + the new loopback
  test):

  | run | Lobby | MonoGame | ClientCore | Core |
  |---|---|---|---|---|
  | A | 12/12 | 1105/1105 | **403/407** | **3695/3699** |
  | B | 12/12 | 1105/1105 | **407/407** | **3694/3699** |
  | C | 12/12 | 1105/1105 | **407/407** | **3697/3699** |

  Every failing test is a documented `docs/TEST_FLAKES.md` live-wire / load-flake
  family member that rotates run-to-run; none references any P497 symbol, and none
  is in the docking/capture/rescue/ramming code this task touches — **and every
  one of them passed when re-run in isolation** (7/7 for the run-B set, 9/9 for
  the run-C set). ClientCore — the assembly exercising the MCP bots — is **fully
  green in runs B and C (407/407)**. The failing classes and their ledger entries:
  run A — `ClientCore…ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_
  WatcherLatchesGone` (ledger:51, P472), `RetriedConnectLiveTests.RepeatedConnects_
  LeaveNoAccumulatedSocketsOrThreads` (ledger:155), `ReadyCheckLiveTests.Only_the_
  mission_owner…` (live-wire), `FullSessionKillIntegrationTests.FullSession_
  ThreePeers_…CrossSideKill` (ledger:33, P472), `Core…AutoReloadOnEmptyTests.
  Auto_reload_relays_RELOAD129_sector_wide` (ledger:52, P472), `LiveCountdownStartPingTests.
  Multi_mission_countdown_scopes_per_mission` (ledger:37, P472), `ConquestTerritoryLiveTests.
  Conquest_win_in_one_mission_does_not_leak_to_another` (ledger:29), `CargoBayReloadTests.
  SwapPart_wire_out_of_range_negative_mount…` (CargoBayReload family, P472); run B —
  `ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget…` (ledger:158),
  `LiveStationRescueTests.Lifepod_reaching_rescue_station…` (ledger:156),
  `ConquestTerritoryLiveTests.Conquest_win_in_one_mission…` (ledger:29),
  `CloakVisibilityGateLiveTests.Detectable_enemy_still_receives…` (live-wire),
  `CargoBayReloadTests.SwapPart_wire_out_of_range…` (CargoBayReload family); run C —
  `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`
  (ledger:40, P472), `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_
  leak_game_over_players_to_another` (ledger:30, previously-fixed residual).
- The two heavy (300 s) opt-in live proof drivers (`P496CaptureProofLiveTests`) are
  gated on `ALLEGIANCE_LIVE_CAPTURE_PROOF=1` as in P496, so the routine suite stays
  fast; the JSON in §3 is their output on the committed code.

## 6. Deliverables

1. Committed on `master`, not pushed — `git branch --show-current` = `master`,
   `git log -1` confirms the commit.
2. This report: `docs/reports/P497_CAPTURE_REACHABILITY.md`.

## Named follow-ups

- **Debug map shield-knock** (§3.2): the escorts never drop the Omega garrison's
  shields below 0.1 on `debugMode=true`; a durability/escort-survival matter,
  orthogonal to reachability.
- **Port native `InGarage` land-slot dock geometry** (replacing the documented
  `DockRadius` approximation): still not needed for reachability, but the faithful
  long-term replacement (shipIGC.cpp:843,853 land slots) if dock footprints are
  ever refined.
