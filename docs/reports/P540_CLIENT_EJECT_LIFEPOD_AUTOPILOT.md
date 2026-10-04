# P540 — client handles EJECT(77): become the lifepod and autopilot to the nearest rescue point

Branch: `master`, commit `a4c8adad` (amended after the 3× full-suite run). Not pushed.
Build: `/home/domingo/.dotnet/dotnet build` — **0 warnings, 0 errors.**

---

## 1. What was missing

The server already broadcasts a real `FM_S_EJECT(77)` when a player's ship is
beaten (P107, `Eject.ForSurvivor`, `Server.Combat.cs:1913-2020` — the port of
`FedSrv.CPP:11944-12006`). The MonoGame client **never dispatched or handled
it**: there was no `Eject` case in
`src/Allegiance.ClientCore/Session/ConnectionSession.cs`, and nothing in
`Allegiance.ClientCore.Game`. So when the player was beaten, the client kept
rendering the old ship and never became the drifting Lifepod locally, and never
autopiloted home.

Native does all of it in `clintlib/appmsg.cpp:170-242` (`case FM_S_EJECT`):

```
:183-187  if boarded -> SetParentShip(NULL) + OnBoardShip(pShip, NULL)
:190-201  else "died" -> terminate all parts, SetAmmo(0), SetFuel(0)
:203-213  clear missiles in the cluster targeting the ship
:216      SetBaseHullType(side->GetCivilization()->GetLifepod())
:220      SetCurrentTurnRate(c_axisRoll, pi*2)
:222-227  SetPosition / SetVelocity / Orientation(forward) from the message
:230-237  if pShip == m_ship: SetCookie, then
          FindTarget(pShip, c_ttStation|Friendly|AnyCluster|Probe, cmdTarget,
                     pcluster, &position, NULL,
                     c_sabmRescue|c_sabmRescueAny|c_sabmLand);
          SetCommand(goto), SetAutoPilot(true), bInitTrekJoyStick=true,
          PlayNotificationSound(salAutopilotEngageSound)
```

## 2. The port

### 2.1 Dispatch + world apply (ClientCore)

`ConnectionSession.cs` now routes `Eject.Fmid` to `_world.Apply(Eject.Decode(m))`
(next to `ShipReset`). `ClientWorld.Apply(Eject)` (with the same file's
`BuildEjectUpdate` helper) is faithful to `:183-227`:

- boarded → `ParentId = -1` (unboard); else the parts tail is cleared (`Loadout
  = null` — ammo/fuel are structural no-ops, this client keeps no pools, noted).
- missiles targeting the ship are cleared — a structural no-op, the client's
  `MissileEntity` tracks no target back-reference (same omission already noted
  server-side, `Server.Combat.cs:1939`).
- hull swaps to the side's lifepod via an injected
  `ClientWorld.LifepodHullResolver` (`short sideId -> short?`), which Game1 wires
  to `ResolveLifepodHull` (`Game1.Eject.cs`) once the shop catalogue loads:
  `SideId -> CivId` (SIDE_ROSTER) -> `StaticCore.Civilizations[civ].Lifepod`,
  falling back to the first `c_habmLifepod` hull. The world's `Apply(Eject)`
  writes the pose into `ShipEntity.Latest` as a canonical `ServerShipUpdate`
  built from the message position/velocity/forward, with the native `pi*2` roll
  as `TurnRates[2]` — so the renderer and the own-ship sim see the pod
  immediately (including the tumble).
- the pod is alive and present: `IsDead`/`IsGone` are cleared (the server never
  sends KILL_SHIP/SHIP_DELETE for an eject).
- own ship (`ShipId == MyShipId`): `Cookie = e.Cookie` and `HasPendingEject` is
  latched (single consumer).

### 2.2 Own-ship reaction + autopilot home (ClientCore.Game, `Game1.Eject.cs`)

`UpdateOwnShipSimulation` (the P508 own-ship host) consumes `HasPendingEject`
(the new step 4.5 in `Game1.Prediction.cs`):

- `HandleOwnShipEject` re-seeds the local sim to the pod pose
  (`SeedSimFrom(Latest)` — snaps position/velocity/forward + the roll spin), logs
  the "[eject] own ship … became lifepod hull …" line, then runs the production
  target pick `ChooseEjectRescueTarget` against the real world.
- `ChooseEjectRescueTarget` is the pure, static mirror of native's
  `FindTarget(...c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttProbe,
  ..., c_sabmRescue | c_sabmRescueAny | c_sabmLand)`: nearest FRIENDLY station
  (`DataStationIGC.SabmCapabilities & (Rescue|RescueAny|Land) != 0`) or rescue
  probe (`EabmCapabilities & (Rescue|RescueAny) != 0`), across every known
  sector, measured from the eject position. (The current-command-target
  tie-break native passes has no equivalent — this client has no command-console
  concept, verified in P417's writeup.)
- `EngageAutopilotToward` (factored out of the P212/P524 G-key path so the G key
  and the eject share one engage) sets the selection, the engaged flag, the
  mouse baseline, the stationary-target snapshot, plays `PlayAutopilotEngage`,
  logs "[monogame] … autopilot ON → <name> (<Kind>#<id>)", and refreshes the
  title. Manual input disengages it as usual.
- `TargetKind` gained a `Probe` member, wired into `ResolveTargetPosition` so
  the autopilot can actually fly to a rescue probe; the skip/dodge mapping keeps
  probes out of the obstacle set (probes aren't dodge obstacles).

## 3. Tests

Failing-first unit tests:

- `tests/Allegiance.ClientCore.Tests/ClientWorldEjectTests.cs` (4): decoding +
  applying EJECT swaps the hull to the side's lifepod, clears parts
  (loadout), takes position/velocity/orientation/roll-spin from the message,
  adopts the cookie + latches `HasPendingEject` for the own ship; a remote ship
  ejects without the latch; a boarded ship unboards instead of clearing parts.
- `tests/Allegiance.ClientCore.MonoGame.Tests/EjectRescueTargetTests.cs` (4):
  `ChooseEjectRescueTarget` returns the nearest friendly rescue-capable station
  across clusters, honours RescueAny/Land capability bits, ignores enemy and
  non-rescue stations (null when none usable), and can pick a friendly rescue
  probe.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P540EjectLiveProofTests.cs`
  (1, `[Collection("LiveNetwork")]`): live headless proof.

### 3.1 Live headless proof (`P540EjectLiveProofTests`)

A real in-process server (XXV_simple) seats a real `ConnectionSession` (the
ejecting player, own ship, side 0) and a real cross-side shooter in the same
sector, with a friendly rescue-capable station injected into the sector (the
model's `Mission.PlaceStation` seam). A lethal cross-side hit drives the P107
eject; the victim session's real `ClientWorld` applies the real `EJECT(77)`. The
production pick runs against the victim's real world + the real client
`ShopCatalog`, and the ejected pod is landed at the rescue station via the
server's dock/rescue path. Client log lines:

```
[eject] player ship 8098 ejected -> Lifepod hull 33 at Vector3 { X = 0, Y = 0, Z = 0 }, vel (...), fwd (...); EJECT(77) -> 2 peer(s) in sector 1052; ...
[eject] client applied EJECT(77): own ship 8098 -> lifepod hull 33 at Vector3 { X = 0, Y = 0, Z = 0 } [cookie 0x00]
[eject] autopilot target chosen: P540_RESQ (Station#9001) in sector Borasisi (1052)
[rescue] ejected pod 8098 docked (landed) at station 9001 'P540_RESQ' via the server's station dock/rescue path
LIVE-EJECT-PROOF: victim 8098 (side 0) ejected by cross-side 8099 -> lifepod 33; target P540_RESQ (#9001) in Borasisi (1052); pod docked at station 9001
```

(`cookie 0x00` because the direct-connect harness sends `Eject.ForSurvivor` with
a zero cookie — the server's documented choice for direct-connect bots; the unit
tests pin cookie adoption with a real non-zero cookie.)

## 4. Full suite — 3 consecutive runs on the final code

Run via `scripts/run-tests.sh` (P471 sequential runner). Counts are Passed /
Total per assembly (Flakes column cites `docs/TEST_FLAKES.md`; all are the
repo's documented live-wire load-flakes, none touching P540 code — the five
P540 tests pass in every run):

| run | Lobby | MonoGame | ClientCore | Core | flakes (pre-existing live-wire load-flakes) |
|-----|-------|----------|-----------|------|---------------------------------------------|
| 1 | 12/12 | 1233/1233 | 454/455 | 3716/3719 | ClientCore `ThreePeerSeededSessionTests…DeterministicVisibility` (L38); Core `GameOverPlayersWireTests.Conquest_win…leak…` (L30/267), `CargoBayReloadTests.Reload_fuel_pack…` (L54), `StationsUpdateLiveTests.Damaged_station_fraction…` (live-wire timing; family L426) |
| 2 | 12/12 | 1233/1233 | 454/455 | 3718/3719 | ClientCore `FullSessionKillIntegration…RealCrossSideKill` (L33); Core `LiveWinConditionTests.Board_refused_once_mission_over` (L34) |
| 3 | 12/12 | 1233/1233 | 453/455 | 3715/3719 | ClientCore `FullSessionKillIntegration…RealCrossSideKill` (L33), `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` (family L155); Core `LiveStationRescueTests.Lifepod_reaching_rescue_station_is_docked…` (L156), `LiveCombatWireTests.Moving_target_streams…falling_hull…` (L406), `ConquestTerritoryLiveTests.Conquest_win…leak…` (L29/267), `CargoBayReloadTests.Reload_transfers_ammo…` (L53-54 family) |

Every failure is a documented network-timing flake that appears intermittently
under full-suite load in this repo (the ledger records the same tests flaking at
these rates even on unrelated runs, and several are flagged "load-flake"/"suspect
(P472)"). None involves P540 code: all are server/live-wire timing tests in the
pre-existing suites. All five P540 tests (4 unit + 1 live proof) passed in all
three runs.


---
