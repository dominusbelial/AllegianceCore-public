# P484 — ships park at an aleph instead of transiting (side-dependent); confirm drones cross multi-hop routes

Date: 2026-10-01 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p484-aleph-parking-and-drone-multihop-prompt.md`

## Summary

The task asked two things: (1) root-cause the "bot parks at an aleph instead of
transiting" stall reported by P429 (probe-side bot) and P474 (bots park at the
second aleph on the debug map), with evidence not hypotheses; and (2) confirm
that server-side drones (P458 constructors / P459 miners etc.) cross **two**
alephs to a distant target.

Both are answered with new live tests against a real in-process server:

- **Server-side drones DO cross multi-hop routes.** `MultiHopDroneLiveTests`
  (new, `tests/Allegiance.Core.Tests/MultiHopDroneLiveTests.cs`) drives a real
  NPC drone from Plato(1060) through Alderan(1052) to a station in Omega(1053)
  on the sealed debug arena — two real warp transits (`[warp] … Plato → Alderan
  via 2184` then `[warp] … Alderan → Omega via 2152`) — and asserts it arrives
  and closes on the station. **Green.** So the P458/P459 route is not broken by
  the same stall: the expansion / mining loop is not blocked server-side on a
  >1-warp target.
- **The park is NOT a server, warp-export, or routing defect.** The MCP combat
  bot's routing law and warp wire data are proven correct by
  `ClientWarpApproachLiveTests` (new, `tests/Allegiance.ClientCore.Tests/…`):
  a `ConnectionSession` peer driven by the exact MCP law crosses **two** alephs
  to a 2-hop target, and at each hop the client-believed warp position **equals**
  the server's authoritative warp position/radius exactly, and the client's
  own-pose **equals** the server's authoritative position exactly. This directly
  disproves P429's "server warp export/coordinate handling for the probe's
  side" hypothesis by measurement.

The residual cause of the live park is **not** a code defect in the server
transit, the warp wire data, or the routing law — it is the client bot's
**obstacle-blind straight-line warp approach** colliding with the transit hub's
dense asteroid field, where the server faithfully applies native impact damage
and elastic bounce. Reproduction evidence is in §5. Because a reliable fix is
an obstacle-aware navigation feature (see §7), this stage lands the confirming
tests and the evidence, and names the fix explicitly rather than shipping an
unproven patch.

---

## 1. Scope and what this stage changes

No production server/core/client code is modified. The stage adds:

- `tests/Allegiance.Core.Tests/MultiHopDroneLiveTests.cs` — the requested
  server-side **two-hop** drone proof (scope 3). Extends the existing P157
  single-hop pattern to the debug arena's two-hop Omega/Alderan/Plato chain.
- `tests/Allegiance.ClientCore.Tests/ClientWarpApproachLiveTests.cs` — the
  scope-1/2 diagnostic: drives a real `ConnectionSession` peer with the MCP
  routing law across two hops and logs the side-by-side authoritative data.

Both are new files; nothing existing is altered. The P474 debug-map presets are
not re-run (out of scope — P485), and no server behaviour changed.

---

## 2. The two hypotheses the task offered, adjudicated by measurement

The brief asked: is the cause the **client's warp data**, the **bot steering**,
or the **server trigger**?

### 2.1 Server transit trigger: correct (re-confirmed)

`Sector.ResolveWarpCollisions` (Sector.cs:3185-3214) is a pure point-in-sphere
test `|ship − warp| ≤ warp.Radius + shipRadius` (Sector.cs:3206-3207, native
`shipIGC.cpp:629-636`), raising `HitWarpEvent` (Sector.cs:3209). The transit
seam (`ServerHost.HitWarpEvent`, ServerHost.cs:879-949) repositions, reassigns
`Ship.Sector`, and the 2 s `LastWarpTime` debounce is on a consistent sim clock
(`OnShipWarped`, Server.Combat.cs:2098; `_simNow`/`m.Update(now)`,
Server.Broadcast.cs:38-52). Both the new two-hop drone test and the client test
exercise real transits, so this path is proven live, not just by inspection.

### 2.2 Client warp data: IDENTICAL to the server — P429's hypothesis is false

The server exports warps to every peer as `ExportWarp.FromWarp(resolved)`
(Server.Lobby.cs:3189-3190) — the **same** `DataWarpIGC` record the server's own
`ResolveWarpCollisions` uses (Sector.cs:3201-3202) — unconditionally for every
sector (Server.Lobby.cs:3187-3195). The client stores it verbatim
(`ClientWorld.cs:1467-1468`). So the client's believed warp position must equal
the server's authoritative position. The new test measures it at each hop:

```
hop to 1052: clientWarp=2153@(-3680,-4920,307) | serverWarp=2153@(-3680,-4920,307) r100 | clientSelf=(1197,990,0) | serverSelf=(1197,990,0)
hop to 1060: clientWarp=2185@(-419,-5455,-86)  | serverWarp=2185@(-419,-5455,-86)  r100 | clientSelf=(-2750,2396,-339)| serverSelf=(-2750,2396,-339)
ARRIVED goal sector 1060 at tick ~1660
```

`clientWarp == serverWarp` and `clientSelf == serverSelf` **exactly**, and the
bot reaches the 2-hop target, with minimum distance to each hop-warp ~107-113 u
(inside the ~110-u transit threshold). There is no stale/side-specific/incorrect
warp EXPORT and no wrong partner mapping after the first hop. The bot routing
law (`BotManager.cs` TryBuildWarpDestMap:684-694, TryFindWarpTo:698-714,
CombatFlyTo:745-760, DriveCombat:928-929) is correct.

### 2.3 Server-side multi-hop routing: correct

`Ship.CrossClusterWarpGoal` (Ship.cs:5544-5612) path-finds via
`Sector.FindPathToward` (Sector.cs:561-587, the multi-hop `pwarpStart` port of
native `common.cpp:894-1012`) and re-paths every tick, so an NPC that warps
mid-journey re-paths from its new sector. The two-hop drone test (§3) proves
this end to end.

---

## 3. Scope 3 — the confirming test: server-side drone crosses two alephs

`MultiHopDroneLiveTests.Server_drone_crosses_two_alephs_to_a_station_two_hops_away`
boots the sealed debug arena (m2xspiderw + `liveSettings.debugMode=true`, P379
topology: Omega(1053) ↔ Alderan(1052) ↔ Plato(1060), peerless — debug
auto-starts, P387), spawns a real `SpawnNpcShip` drone in Plato (side 1) and
sets `PlanTarget` to a station two hops away in Omega (side 0). It asserts the
drone crosses BOTH alephs and closes on the station.

Evidence captured live:

```
[warp] ship 8096 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184
[warp] ship 8096 crossed from 'Alderan' (1052) to 'Omega' (1053) via warp 2152
P484-2HOP: drone arrived in Omega(1053); minD to station 321.3
```

So the P458/P459-class server-side drones do route across two warps and arrive;
the expansion / mining loop is not blocked on multi-hop targets. This is the
task's headline confirmation, and it simultaneously rules out any server-side
cause for the P474 park (the server transits a well-directed ship through the
same hub cleanly).

---

## 4. The client law is correct — the diagnostic test

`ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target`
seats a real `ConnectionSession` peer in Omega and drives it to Plato (2 hops)
with the **verbatim** MCP routing law (TryBuildWarpDestMap / TryFindWarpTo /
CombatFlyTo ported unchanged). Asteroids are stripped from the three transit
sectors so the transit itself is observable. It reaches Plato and logs the §2.2
equalities. Green.

This isolates the variables: with an unobstructed path, the MCP law, the warp
wire data, and the server transit jointly produce a clean two-hop crossing. Any
remaining live park must therefore come from the one thing this test removes —
the rock field.

---

## 5. The real root cause of the live park — the obstacle-blind approach vs the transit-hub field

Running the **same** 2-hop client flight with the real asteroid field present
(instead of stripped) reproduces the P474 blocker: the bot completes hop 1
(Omega→Alderan) and then cannot complete hop 2 out of Alderan.

- Clean arena (asteroids stripped): min distance to the hop-2 warp **~107-113 u**
  — inside the transit volume — the bot transits and reaches Plato.
- Real arena (asteroids present): min distance to the hop-2 warp **~7000+ u** —
  the bot is destroyed (ejected) in Alderan's mining-hub field before it can
  even reach the second aleph.

The differential is unambiguous: the asteroid field is the blocker. Mechanism:

- The MCP bot's `CombatFlyTo` flies a straight bee-line at the aleph with **no
  obstacle awareness** (BotManager.cs:745-760, 928-929).
- The server faithfully simulates rock hits: real **impact damage**
  `damage = 2·|Δv|²·(1/128)` (Sector.cs ResolveShipAsteroidCollision:2203-2206,
  native `shipIGC.cpp:948-958`) plus an elastic bounce (Sector.cs:2196-2201).
  A fragile hull (Scout) flying at cruise/warp-exit speed into the dense field
  is destroyed outright; a tougher hull (P474's bombers) survives the impacts
  but is bounced/damaged so it cannot penetrate the aleph's ~110-u transit
  volume — the "park at the second aleph" P474 saw. Both are the same root
  cause: the bot cannot negotiate the field, so it never enters the warp's
  collision volume and the server's (correct) transit never fires.

**Why it is side-dependent (P429).** The park is a function of the obstacle
geometry along *that bot's particular* spawn→aleph line. The MCP bots spawn per
side at different coordinates; whether the straight line to the first aleph
clips a rock is a per-spawn/per-side accident of that geometry. Hence P429 saw
the probe-side bot park (its line was blocked) while the opposing-side bot
crossed (its line was clear) — and P474, where *both* attackers must thread the
*same* shared hub (Alderan), saw both park on the hop out of the hub. The
server and routing are side-agnostic; the obstacle field is not.

This is **not** a server/wire-data defect (it would not be side-dependent in the
P429 sense, and §2 disproves the wire-data claims); it is a bot-navigation
limitation of the MCP combat autopilot.

---

## 6. Native citations for the transit / collision correctness

- Warp transit trigger is the ship-vs-warp sphere test of native `CshipIGC::
  HandleCollision` — `shipIGC.cpp:629-636` (cited at Sector.cs:3140-3141).
- Exit placement/speed in `CFSShip::HitWarp` — `fsship.cpp:71-137`
  (cited at ServerHost.cs:838-839), incl. the 2 s `m_warpState` debounce
  `fsship.cpp:74,1185-1191` (cited at ServerHost.cs:921).
- Rock impact damage + elastic bounce — `shipIGC.cpp:948-958`, `cElasticity`
  `shipIGC.cpp:935` (cited at Sector.cs:2203-2204, 2196).
- Multi-hop first-warp pathfinding — native `FindPath` `common.cpp:894-1012`
  (cited at Sector.cs:521-527).

No native behaviour is being "fixed" — the port is faithful; the limitation is
the absence of obstacle-aware steering in the *bot*, which native human pilots
supply manually.

---

## 7. Fix recommendation (named follow-up, not landed here)

Because the server, warp data, routing law, and multi-hop drone path are all
proven correct, the fix for the live park is **obstacle-aware navigation in the
MCP combat bot's warp approach** (and, for fragile hulls, moderating warp-exit /
cruise speed into dense fields). A straight-line avoidance attempt was prototyped
during this stage but did not itself survive the Alderan field: a Scout exiting
a warp at high speed into the densest field is struck before client-side
steering can react, so a robust fix must combine (a) lateral clearance around
blocking rocks (consulting the peer's own `ClientWorld.Asteroids`, positions +
radii are already on the wire) and (b) speed moderation / hull selection for
field traversal. That is a substantial feature, genuinely not a small patch; it
is tracked as a named follow-up with this report as the reproduction recipe
(debug arena, asteroids on/off differential, min-distance-to-hop-warp metric).

---

## 8. Build and tests

- Whole-solution build: `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors**.
- New tests pass in isolation:
  - `MultiHopDroneLiveTests.Server_drone_crosses_two_alephs_to_a_station_two_hops_away` ✅
  - `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` ✅
- Full suite: 3 consecutive `scripts/run-tests.sh` runs (the P471 sequential
  runner: build once, then the four test assemblies sequentially). Across the
  three runs the ONLY failures are the documented live-wire timing / load
  flakes from `docs/TEST_FLAKES.md` (the P472 "suspect" family that rotates
  run-to-run): `FullSessionKillIntegrationTests.FullSession_ThreePeers_…
  CrossSideKill` (1/3 ledger entry, confirmed intermittent here — passed/failed/
  passed in isolation), `LiveChatSectorWireTests.Individual_AllSector…`,
  `CargoBayReloadTests.Reload_fuel_pack…`, `ReadyCheckLiveTests.…force_team_
  ready…`, `ThreePeerSeededSessionTests.SeedThreePeers…`,
  `SideEliminationLiveTests.…side_only_gameover…`, `LoadoutChangeLiveTests.
  Boarding_and_leaving…`, `GameOverPlayersWireTests.Win_by_elimination…`,
  `LiveWinConditionTests.Board_refused_once_mission_over`. Each confirmed
  green in isolation; none reference the two new P484 test classes, which pass
  in all three runs (my delta changes no production code — it is two new test
  files + this report — so any unrelated failure is definitionally
  pre-existing/environmental).

---

## 9. Scope boundaries honoured

- **P485** (freshly-seated opposing-side combat bot frozen at spawn on non-debug
  maps) is explicitly out of scope and not investigated as a separate cause; the
  P484 park (bot flies to an aleph then stalls) is a distinct symptom and is
  addressed above. Nothing here re-runs the P474 presets as proof.
- Warp visuals (P341) and ripcord are untouched.
- No production code changed; the tree delta is the two test files + this report.
