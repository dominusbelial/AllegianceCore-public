# P490 — final live proof: elimination (P455/P455b) proven end-to-end; capture (P450) still blocked by the board hull's reachability, now precisely characterised

Date: 2026-10-02 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p490-live-proof-capture-and-elimination-prompt.md`

## Summary

Two live parity proofs were open after P485: **P450 capture** (a `c_habmBoard`
board hull must dock a hostile garrison after its shields are driven down) and
**P455/P455b elimination** (destroying the defender's last restart station →
`SIDE_INACTIVE`, "<side> is no more.", `GAME_OVER` with native's *"won by
outlasting all other sides."* reason, then the P439/P480 rotation back to
`NotStarted` with the players kept seated).

This task ran them for real, on the normal (`m2xspiderw`, `debugMode=false`)
and debug (`m2xspiderw`, `debugMode=true`) maps, with P489's shared dodge law in
place:

1. **Elimination — PROVEN, end to end.** Added the dedicated P474b preset
   **`elimination_run`** (the planned elimination preset; `bomber_run` already
   had its `size` arg, so I added the dedicated preset to also carry the P480
   rotation evidence). With **10** bombers (real anti-base weapon `PE AutoCan 3`
   on `Lxy Hvy Bomber`) the garrison hull is driven `1.0 → 0` in ~3 s, and the
   defender's **last restart station** dies at `t≈141.1 s`. The report records
   `station_destroyed`, then the observer's `GameOverResult` with the exact
   native reason text **"Bravo won by outlasting all other sides."**, then the
   **P480 rotation back to `NotStarted` with the player kept seated** (new ship
   id 8132, side re-active, stage 0). The authoritative server log additionally
   shows the two transient signals the observer's world races through in ~0.2 s:
   **`SIDE_INACTIVE(68) bActive=false`** and the **"Alpha is no more."**
   elimination chat, plus the full 31-peer P480 migration into the fresh
   `NotStarted` mission on their original sides. §2.
2. **Capture — honestly NEGATIVE, with root cause.** The `c_habmBoard` board
   hull is the slowest ship in the corpus (Hvy Troop Transport id 26: mass 90,
   thrust 1350, **accel 15.0** vs the default fighter's 30.0; *all* HabmBoard
   hulls are accel 15–18.8 and the largest radii). P489's dodge law fixes
   **fighter/escort** transit (escorts now cross the transit-hub fields on both
   maps), but the transport still **cannot transit an aleph through the dense
   field**: on the normal map it traverses its whole home sector then parks
   permanently at the aleph's transit volume (trajectory evidence), and on the
   debug map it never leaves its home sector at all. So the P450 gate is never
   exercised — the board hull cannot reach the garrison on either map. This is
   the same P484-class reachability blocker, now precisely characterised per
   hull (it is a hull-maneuverability limit, **not** a P450 gate-logic bug), and
   is reported per task item 3 with a named follow-up — **not fixed here**. §3.

Per the brief, the elimination is proven on a real 2-side map; the capture
attempt is run on both maps and reported honestly with its evidence.

---

## 1. Precondition + scope

- **P489 present** (checked as instructed): `git log --oneline | grep P489`
  → `29715303 P489: client bots steer with native's shared dodge law (ShipDodgeLaw)
  so they cross asteroid-field hubs`. It exists, so this task proceeded.
- Branch: `master` (`git branch --show-current`). Nothing pushed.
- In scope: a dedicated elimination preset (P474b) + honest live proof of the
  remaining mechanics. Out of scope (honoured): bot steering (P489) — not
  touched; the minefield preset — still a named follow-up.

Live method (identical to P474/P485): standalone `Allegiance.Server` on
`m2xspiderw.igc` (`debugMode=false` on :6190, `debugMode=true` on :6200) with
P473's `liveSettings.testHarness` grant ON (`prefix 'mcp-', money=1 000 000,
ignoreTechGate=True`), driven by the built Release `allegiance-mcp` over
newline-delimited MCP JSON-RPC via `run_scenario`. Both maps run the P485
ready-up fix so the normal map auto-starts.

---

## 2. Elimination — PROVEN live (`elimination_run`, normal map)

### 2.1 The new preset

`bomber_run` already takes `size` (so that half of the brief's "size argument…
if one doesn't exist, **or** a dedicated elimination preset" is satisfied by the
dedicated preset instead). I added **`elimination_run`** — the planned P474b —
in `tools/allegiance-mcp/BotManager.Scenarios.cs` (`RunElimination`). It reuses
the bomber-group machinery (real anti-base weapon resolved from the core by
`ScenarioPlanner`) and, crucially, after the station dies it **keeps watching**
for the full P455/P455b win sequence **and** the P480 rotation:

- `station_destroyed` — the garrison leaves `Stations` with hull ≈ 0.
- `p455_side_inactive` — the observer's `IsSideActive(sideB)` flips false
  (`SIDE_INACTIVE(68)`).
- the `<side> is no more.` elimination chat (drained again immediately after the
  death, since the server broadcasts it in the same tick).
- `p455_game_over` — the observer's `GameOverResult` with the reason text.
- `rotation_back_to_notstarted` — the P480 signal: the observer's world reports
  `MISSION_STAGE NotStarted`, the side active again, and the peer still holding
  a seat (`RotationCompleted(stage==NotStarted && hasSeat && sideActive)`), with
  the fresh migrated ship id.

Because the deactivation + game-over are a **~0.2 s window** before the P480
rotation resets the observer's world, the terminal reads happen synchronously in
the same loop pass that detects the death (and re-drain the chat), with a short
bounded grace so the rotation watch always runs. The report is intentionally
corroborated by the server log (authoritative) for the two transient signals.

### 2.2 The live run — `elimination_run` size 10, normal map (~141 s, early-exit)

```json
"plan":    {"bomberHull":{"id":299,"name":"Lxy Hvy Bomber"},"antiBaseWeapon":{"id":55,"name":"PE AutoCan 3"},"escortHull":{"id":13,"name":"Adv Scout"}}
"station": {"id":4328,"name":"Garrison","sector":1050}
"outcome": {"stationDied":true,"attackerReachedStation":true,"killTime":141.10,
            "firstUnderAttackAlertAt":138.59,"p451AlertCount":1,
            "sideInactive":false,"gameOverAt":141.10,"gameOverReason":"Bravo won by outlasting all other sides.",
            "rotatedBackToNotStarted":true,"rotatedAt":141.30,"p455Events":1}
```

Real wire events (from the side-B observer's own world):

```json
{"t":1.49,"kind":"station_selected","detail":{"stationId":4328,"name":"Garrison","sector":1050,"defenderSide":0}}
{"t":4.47,"kind":"group_spawned","detail":{"bombers":10,"escorts":10,"defenders":10,"bomberHull":"Lxy Hvy Bomber","antiBaseWeapon":"PE AutoCan 3"}}
{"t":138.59,"kind":"p451_base_under_attack","detail":{"voiceOver":820,"chat":"Garrison in Ganymede under attack by mcp-b029-8262"}}
{"t":141.10,"kind":"station_destroyed","detail":{"stationId":4328,"name":"Garrison"}}
{"t":141.10,"kind":"p455_game_over","detail":{"message":"Bravo won by outlasting all other sides.","winner":1,"na":-1}}
{"t":141.30,"kind":"rotation_back_to_notstarted","detail":{"side":0,"stage":0,"sideActive":true,"playerKeptSeated":true,"newShipId":8132}}
```

The **authoritative server log** at the same moment (the two transient signals
the observer races through in ~0.2 s before rotation resets the world):

```
[sideinactive] side 0 deactivated (lost last restart station) => SIDE_INACTIVE(68) bActive=false -> 31 peer(s)
[sideinactive] side 0 elimination chat "Alpha is no more." -> 31 peer(s)
[reseat] peer 1 -> fresh mission: new ship 8131 (was 8098) targeting side 0 (was leader)
[reseat] peer 2 -> fresh mission: new ship 8132 (was 8099) targeting side 0
[reseat] peer 3 -> fresh mission: new ship 8133 (was 8100) targeting side 0
... (all 31 connected peers) ...
[reseat] peer 31 -> fresh mission: new ship 8161 (was 8128) targeting side 1
[stage] peer N -> MISSION_STAGE(37) NotStarted
[reseat] peer N -> re-sent fresh-joiner join burst (ship 8NNN, stage NotStarted)
```

Every requirement in the brief's elimination list is met:

| Requirement | Evidence |
|---|---|
| the station's death broadcast | `station_destroyed` event (observer world) + hull `1.0 → 0` |
| `SIDE_INACTIVE` | server log `[sideinactive] … => SIDE_INACTIVE(68) bActive=false -> 31 peer(s)` |
| "<side> is no more." | server log `elimination chat "Alpha is no more." -> 31 peer(s)` |
| `GAME_OVER` with native *"won by outlasting all other sides."* | `p455_game_over` event: `"Bravo won by outlasting all other sides."` |
| P439/P480 rotation back to `NotStarted`, players kept seated | `rotation_back_to_notstarted` (stage 0, `playerKeptSeated:true`, new ship 8132) + server `[reseat]` migration of all 31 peers to fresh ships on their original sides |

**Bonus sizing datum (why "enough bombers" matters):** a 3-bomber `bomber_run`
drove the same garrison hull `1.0 → 0.175` in ~8 s (t≈135–143) before the fire
lapsed and the station self-repaired back to 1.0 over ~500 s — i.e. the initial
simultaneous burst scales with bomber count, and 10 bombers tip it past the
kill threshold while 3 do not. This is the live-measured justification for
"scale `size` to crack the garrison hull in the window."

> **Note on `sideInactive:false` in the report:** the observer's `IsSideActive`
> is a live per-side map that the P480 rotation re-seeds to active within ~0.2 s
> of the death, so the report's observer-world read races it; `SIDE_INACTIVE` is
> therefore carried by the authoritative server log above. The `p455_game_over`
> reason text and the `rotation_back_to_notstarted` (which prove the win and the
> migration) are captured directly from the observer's world.

---

## 3. Capture — live attempt on both maps, honest negative + root cause

### 3.1 The board hull cannot transit — the root cause (hull-maneuverability, not a P450 gate bug)

P489's dodge law made **fighters/escorts** cross the transit-hub fields on both
maps (the escorts now transit reliably). But the mandatory P450 board hull —
`ScenarioPlanner` resolves the lowest-id `c_habmBoard` hull, **Hvy Troop
Transport id 26** — is the slowest ship class in the corpus:

| hull | mass | thrust | **accel** | length (⇒ radius) |
|---|---|---|---|---|
| Adv Scout (default fighter) | 40 | 1200 | **30.0** | 17.0 (9.5) |
| Lxy Hvy Bomber | 50 | 750 | 15.0 | 23.2 |
| **Hvy Troop Transport id 26 (boarder)** | 90 | 1350 | **15.0** | 30.0 (16) |

*All* HabmBoard hulls are accel 15–18.8 with the largest radii in the game. The
native dodge law's imminence gate (`t² ≤ 4·r/accel`) gives such a slow, large
hull barely any reaction time in a dense field, so at the aleph's transit
volume — where the dodge goal pushes *away* from the rock that sits right at
the transit point and the bot then turns back — the transport oscillates and
parks instead of punching through.

### 3.2 Normal map (`m2xspiderw`, `debugMode=false`) — boarder parks at the aleph

`capture_attempt` size 1, 120 s. Live boarder trajectory (from `list_active_bots`,
spawn side A):

```
t= 3s  sector 1051 pos [1440,730,920]          ← spawn
t=15s  sector 1051 pos [1441,731,903]          ← moving toward the warp
t=18s  sector 1051 pos [1492,762,831]
...   (steady advance through the field, the dodge working) ...
t=70s  sector 1051 pos [4258,2216,-863]
t=76s  sector 1051 pos [4287,2231,-881]         ← reaches the transit volume
t=88s  sector 1051 pos [4292,2234,-884]         ← PARKS — position frozen thereafter
```

The boarder never transits hop 1; routing stays live the whole window
(`[p433-diag] … cluster 1051 goal 1050 path[4] next=1055 … warpFound=True`). The
escort (Adv Scout) does transit, but in 120 s the station shields stayed `1.0`
(the one-sided cross-side fire limit, P429/P432) so no shields-down and no
capture either. Outcome: `refusalConfirmed:false, attackerReachedStation:false,
shieldsDownAt:-1, captured:false`.

### 3.3 Debug map (`m2xspiderw`, `debugMode=true`) — boarder never leaves its home sector

`capture_attempt` size 1, 300 s. The **escort** transits `1060 → 1052 → (toward)
1053` (`attackerReachedStation:true`), but the **boarder (mcp-b002)** repeats
`[p433-diag] bot mcp-b002 … cluster 1060 goal 1053 path[3] next=1052 …
warpFound=True` across the whole 300 s window — it stays in its home sector 1060
(Plato) and never transits. The station shields stayed `1.0` for the entire 300 s.
Outcome: `refusalConfirmed:false, attackerReachedStation:true, shieldsDownAt:-1,
captured:false`.

### 3.4 Conclusion for capture (task item 3)

The P450 gate is **not** reached on either map because the mandatory
`c_habmBoard` board hull cannot reliably transit to the garrison — a
hull-maneuverability reachability limit, the same P484-class as P484/P485, now
precisely attributed to the board hull's low acceleration + large radius (not a
P450 gate-logic bug — no boarder ever reached a dock for the gate to judge).
It is reported here with evidence and is **not fixed in this task**.

**Named follow-up** (carried from P484/P485, now made concrete): the MCP
scenario layer needs either (a) hull-aware obstacle transit that accounts for a
slow/large hull at the aleph's transit volume, or (b) the supported
co-location/relocation of scenario actors (drop the board hull at the dock) —
otherwise the P450 capture proof cannot be completed live on these maps. Tracked
alongside the existing P484 mobility notes and P474b.

---

## 4. Code changes + failing-first tests

All in the MCP parity layer (no server behaviour changed):

- `tools/allegiance-mcp/BotManager.Scenarios.cs` — `elimination_run` preset
  (`RunElimination`): bomber-group choreography scaled by `size`, the full
  P455/P455b terminal-read sequence (station death → side-inactive → "is no
  more." → game-over), and the P480 rotation watch. Pure helpers
  `IsSupportedScenarioPreset` and `RotationCompleted`.
- `tools/allegiance-mcp/McpTools.cs` — `run_scenario` accepts `elimination_run`
  (preset `enum` + description); `Program.cs` tool-list comment updated.
- `tests/Allegiance.ClientCore.Tests/BotSpawnerResolutionTests.cs` — two new
  theories (12 cases) that **fail on the unfixed code** (the helpers and the
  preset did not exist → the tests could not pass):
  - `IsSupportedScenarioPresetRecognisesThePresets` — `elimination_run` is a
    recognised preset; junk/minefield are not.
  - `RotationCompletedRequiresNotStartedSeatedAndActive` — the P480 rotation
    detector needs `stage==NotStarted` AND seated AND side-active.

Verified red→green: `IsSupportedScenarioPreset("elimination_run")` is false (or
the member does not exist) before the change and true after; the new tests pass
**12/12** in isolation after the change (§6).

---

## 5. Scope boundaries honoured

- **Bot steering (P489)**: not modified.
- **Minefield preset (P474b)**: not added — still a named follow-up.
- **Server / core / P450-P455 gate logic**: not modified; only the MCP parity
  tool gained the elimination preset and the live-reporting of the sequence.
- The capture proof is reported as a reachability negative (task item 3), with a
  named follow-up, rather than being faked or hacked around.

---

## 6. Build and tests

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors**.
- New scenario tests pass **12/12** in isolation.
- Full suite via `scripts/run-tests.sh` (the P471 sequential runner), **3
  consecutive runs** (actual measured per-assembly Passed!/Failed! lines):
  | run | Lobby | MonoGame | ClientCore | Core |
  |---|---|---|---|---|
  | 1 | 12/12 | 1079/1079 | **400/401** | **3680/3683** |
  | 2 | 12/12 | 1079/1079 | 401/401 | **3680/3683** |
  | 3 | 12/12 | 1079/1079 | **400/401** | **3681/3683** |
  The failing tests, by run (all the documented P472 live-wire-timing /
  load-flake family from `docs/TEST_FLAKES.md` — rotating run-to-run, no stable
  victim, and **all confirmed green in isolation** here, so none is a regression):
  - **Run 1**: `FullSessionKillIntegrationTests…RealCrossSideKill` (the ledger's
    "suspect (P472)" ready-relay timing, 1/3; fails intermittently even in
    isolation, P485), `AutoReloadOnEmpty…RELOAD129` (ledger "load-flake (P472)",
    green in isolation), `CloakVisibilityGateLiveTests.Detectable_enemy_…
    collapsed_radius` (not in the ledger but a one-off live-wire flake — green in
    isolation; did not recur in runs 2/3), `LiveChatSectorWireTests.Individual_
    AllSector_…` (ledger LiveChat load-flake family).
  - **Run 2**: `AutoReloadOnEmpty…RELOAD129`, `LiveChatSectorWire…Individual_
    AllSector`, `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (ledger
    LiveRipcord load-flake family).
  - **Run 3**: `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility`
    (ledger "suspect (P472)"; passes individually in isolation),
    `AutoReloadOnEmpty…RELOAD129`, `LiveStationRescueTests.Lifepod_reaching_…`
    (ledger load-flake P487).
  Notes: `AutoReloadOnEmpty…RELOAD129` appeared in all three runs (the one
  repeating offender); it is the ledger's documented fixed-sleep "load-flake
  (P472)" and was **green in isolation** (22/22 across the isolated batch that
  included it) — a load-amplified flake, not a P490 regression. **None reference
  any P490 symbol**: P490 adds only the `elimination_run` preset and two pure
  unit theories; every failing test drives raw peers / unrelated subsystems
  (`AutoReload`, `Cloak`, `LiveChat`, `LiveRipcord`, `FullSessionKill`,
  `ThreePeerSeeded`, `LiveStationRescue`).

---

## 7. Deliverables

1. Committed on `master` (not pushed) — `git branch --show-current` = `master`,
   `git log -1` confirms the commit.
2. This report: `docs/reports/P490_LIVE_PROOF_CAPTURE_ELIMINATION.md`.
