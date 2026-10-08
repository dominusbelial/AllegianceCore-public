# P583 — Native player scoring (`PlayerScoreObject` + `CalculateScore`)

## Relationship to native

P583 ports native's `PlayerScoreObject` live-session counters and its complete
`CalculateScore` formula, and feeds every counter from the port's existing
events. The native references are:

- `Igc/common.cpp:2667-2695` — `PlayerScoreObject::CalculateScore` (the full
  formula: `kMax = m_dtPlayed/(15 min)`; six diminishing-returns class/base
  terms `(c·kMax)·Points/(c+kMax)`; linear terms; `min(hvt,5)·4`; `repair·5`;
  `×2 if win`; `× m_rankRatio`).
- `Igc/igc.h:5502-5966` — the `PlayerScoreObject` class (every counter's
  accessor/mutator), with the point-constant indices at `igc.h:77-90`
  (`c_fcidPointsWarp`=15 … `c_fcidPointsRescues`=27) and the two rating
  constants at `igc.h:92-93`.
- `Igc/missionigc.cpp:2704-2716` — the shipped PCore values
  (`c_pointsWarp`=2 … `c_pointsRescues`=5, `c_crAdd`=1.25, `c_crDivide`=100).
  **The port reads these from the loaded core's `FloatConstants` array, never
  hard-codes them.**

This is **Stage 2** of the player-scoring plan: Stage 1 (P215) added only
Kills/Deaths/CombatRating. P583 completes the object and the formula, keeps the
existing `AdjustCombatRating` (unchanged) and adds the game-over/roster
display.

## What changed

### 1. `PlayerScore` (src/Allegiance.Server/PlayerScore.cs) — full port
Added every native counter + its `Add*`/`Spot*`/`Set*` mutator:
`Ejections`, `Rescues`, `TechsRecovered`, `Artifacts`, `Flags`, `WarpsSpotted`,
`AsteroidsSpotted`, `HighValueTargetsSpotted`, `MinerKills`, `BuilderKills`,
`LayerKills`, `CarrierKills`, `PlayerKills` (floats, fractional partial
credits), `BaseKills`, `BaseCaptures` (floats), `Repair` (float), `TimePlayed`
(float seconds), `RankRatio`, `Win`; plus `KillShip(VictimClass, fraction)`
(`igc.h:5675-5706`), `KillBase(bool)` (`igc.h:5708-5717`),
`CaptureBase(bool)` (`igc.h:5718-5727`), `SetRepair` (`igc.h:5664-5667`),
`SetRankRatio` (`igc.h:5576-5579`), `SetTimePlayed`, `SetWin`, and
`CalculateScore(float[] fc)` (`common.cpp:2667-2695`). Constant indices
15-29 are exposed as `internal const int` and the caller passes the mission's
`FloatConstants` array in (the same testability seam `AdjustCombatRating`
established).

### 2. Feeds (each wired into an existing event, native-cited)
| Counter | Event | Native cite |
|---|---|---|
| `Rescues` | `Server.Docking.cs` `PerformRescue` | `FedSrv.CPP:13039` |
| class kills (`KillShip`) | `Server.Combat.cs` `BroadcastKillShip` | `FedSrv.CPP:11848` |
| `Ejections` | `BroadcastKillShip` `!bKilled` branch | `FedSrv.CPP:11919-11921` |
| `BaseKills` | `BroadcastStationDestroyed` (credited launcher) | `FedSrv.CPP:12252` |
| `BaseCaptures` | `BroadcastStationCapture` (capturing pilot) | `fsship.cpp:600` |
| `Flags`/`Artifacts` | `OnShipDocked` flag-secure block | `FedSrv.CPP:13127/13118` |
| `Repair` | synced from `Ship.Repair` in the game-over finalize | `FedSrv.CPP:3704-3709` |
| `TimePlayed`, `RankRatio`, `Win` | `FinalizeMissionScores` in `DeclareGameOver` | `fsmission.cpp:3178-3263` |

`FinalizeMissionScores` (Server.Lobby.cs) is the port of `ProcessGameOver`'s
per-player scoring: it computes each side's rank sum (`fsmission.cpp:1365`
`GetSideRankSum`, rank < 1 counted as 1), derives each side's ratio = opposing
sum / own sum when exactly two sides are present (`fsmission.cpp:3196-3207`),
syncs `Repair`, sets `TimePlayed` (elapsed since the peer seated — the
collapsed `Connect`/`Disconnect` accounting, `igc.h:5602-5613`, tracked via a
`ConnectSimTime` stamp on `LiveShip`), sets `Win` for the winning side, and
runs `CalculateScore` with the real core constants — then
`BuildGameOverPlayerEntries`/`BroadcastGameOverPlayers` carry the computed
values onto the end-game wire.

**Victim-class bucketing** (`ClassifyVictim`): native switches on the ship's
`pilotType` (`igc.h:5675-5706`); this port has no per-ship pilot-type
attribute, so it proxies from hull capability bits (the same basis P285 uses):
carrier → `Carrier`, miner → `Miner`, builder → `Builder`, else → `Player`.
Layer folds to Player — immaterial to the score (`c_pointsLayer`=10 =
`c_pointsPlayer`=10).

**Bots count like players.** Every feed credits via `LiveOf(ship)?.PlayerScore`,
which exists for any *live peer* — connected MCP/bench bots included. Ownerless
`SpawnNpcShip` NPCs have no score object (native carries one even on drones);
this is a documented gap.

### 3. Display ("show where native does")
- **End-game screen** — `GameOverPlayerEntry` (52 B) now carries score +
  Kills/Deaths/Ejections/Rescues/BaseKills/BaseCaptures/Artifacts/Flags. The
  client now actually consumes `GAME_OVER_PLAYERS(63)`
  (`ConnectionSession` → `ClientWorld.Apply` → `GameOverPlayersList`), and
  `GameOverScreenRenderer.Draw` renders a per-player scoreboard table under the
  result panel (native `gameoverscreen.cpp`'s per-player columns).
- **In-game roster** — `ScoreRosterEntry` (56 B) now carries the same counters;
  `BroadcastScoreRoster` fills them and `ScoreboardLayout`/
  `ScoreboardOverlayRenderer`/`PilotInfoLayout` display the extra columns live.
- Both wires are this port's own shapes (native has no in-mission live
  scoreboard wire; its client reads the local IGC score object directly) — a
  documented substitution, not a byte-compat claim, consistent with the P194
  precedent.

## Gaps (events that do NOT exist in the port yet — not invented)
- **Techs recovered** (`AddTechsRecovered`): native fires on docking a tech
  treasure when `ApplyDevelopmentTechs` succeeds (`FedSrv.CPP:13224-13226`);
  this port's `PerformTreasurePickup` has no dock-time tech application seam —
  part treasures become `ACQUIRE_TREASURE` mount offers only
  (`Server.Docking.cs:898-926`). Counter exists, unfed.
- **Warp / special-asteroid / high-value spotting** (`SpotWarp`/
  `SpotSpecialAsteroid`/`AddTargetSpot`): native's first-sighting scorer lives
  in the eye/visibility pass (`FedSrv.CPP:10956-10982,11101-11112`); this port
  has no spotting-score system (its `VisibilityMath`/`SideVisibilityState` feed
  ship-status, not scoring). Counters exist, unfed.
- **Partial-damage kill credits**: native's damage-bucket loop
  (`FedSrv.CPP:11826-11858`) credits every damage contributor proportionally;
  this port has one credited attacker (fraction 1.0). Documented simplification.
- **Assists**: native counts them (`m_cAssists`) but nothing in this port
  detects assists; counter present via `AddAssist` semantics, unfed.
- **NPC score objects**: ownerless `SpawnNpcShip` NPCs have no score object
  (native gives drones one); feeds credit live peers only.
- **Turret-gunner child credits** (`KillBase(false)`/`CaptureBase(false)` +0.5):
  no child/turret ships in this port.

## Exclusions
Persistent stats DB / rank progression across games and Steam achievements are
out of scope (per the task). Experience multipliers and commander-point
bookkeeping in `ProcessGameOver` (`fsmission.cpp:3297-3354+`) are likewise
beyond this slice (they feed persistence/command credit, not the per-player
score the roster/end screen shows).

## Tests
- `PlayerScoreCalculateScoreTests` (17) — pure: `CalculateScore` against
  hand-computed native values (linear terms; the `kMax` miner diminishing term
  100/11; the full-mix 5426/33; win ×2; ×rank ratio; `dtPlayed==0` early
  return) + one test per counter feeder (rescue → +1 → +5 pts, and so on).
- `LivePlayerScoringFeedsTests` (5) — live wiring: `PerformRescue` →
  `Rescues`+1; `BroadcastStationDestroyed` → `BaseKills`+1;
  `BroadcastStationCapture` → `BaseCaptures`+1; killing a mining hull →
  `MinerKills`+1; ejecting a peer → `Ejections`+1 (not a death). Must-fail
  proven for the rescue feed (disabled → test reds).
- `ScoreRosterWireTests`/`GameOverPlayersWireTests` updated for the widened
  records (size + offsets 44→56 / 36→52). `GameOverPlayersWireTests` live
  cases updated: score is now *computed* at game-over, so they seed real
  counters and assert a non-zero computed score rides the wire (the old
  raw-score writes are overwritten by `FinalizeMissionScores`).

Suite numbers — three consecutive `scripts/run-tests.sh` runs on the final code:

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1583/1584 | 561/561 | 3800/3803 |
| 2 | 12/12 | 1584/1584 | 560/561 | 3799/3803 |
| 3 | 12/12 | 1584/1584 | 560/561 | 3797/3803 |

Every failure is a pre-existing live-network load-flake documented in
`docs/TEST_FLAKES.md` (e.g. `ShipDeleteGoneLiveTests.DockOut…`,
`LiveWinConditionTests.Board_refused_once_mission_over`,
`LiveRelaunchTests.Carrier_without_launch_slots…`, `LiveTeamLobbyTests`,
`LiveChatSectorWireTests`) or a same-class intermittent live-wire flake
(`ReadyCheckLiveTests`, `P493EnemySpottingLiveTests`, `LiveTreasurePickupTests`,
`NpcShipFoundationLiveTests`, `CargoBayReloadTests`, `CloakVisibilityGateLiveTests`,
`LiveGainFlagTests`) — none references P583 scoring symbols, and each was
confirmed **green in isolation**. The P583 live feed test
`GameOverPlayersWireTests.Win_by_elimination…` also flapped under full-suite
socket load but passes 5/5 in isolation (same environmental flake class).
MonoGame was clean in runs 2 and 3 (its run-1 flake passed in isolation).

## Live proof (real window, `ALLEGIANCE_AUTOSHOT`, 2560×1440)

Ran `scripts/live-proof.sh` on the 221 map (`m2xspiderw.igc`) with MCP arena
combat bots and a real windowed MonoGame client, ending the game via a
temporary auto-declare hook (removed before commit) so the end screen could be
captured. Assets in `docs/reports/P583_assets/`:

- `endgame_scoreboard.png` — a **real presented backbuffer frame** from the
  client window showing the P583 game-over panel: "YOU ARE VICTORIOUS!",
  "WINNER: Alpha", reason, and the per-player scoreboard table with the
  counter columns `NAME SIDE SCORE K D EJ RS BK BC AR FL` and one row per
  connected peer (the client peer at 55 pts with K=2, RS=1, BK=1, AR=1; the
  rest are the MCP arena bots — *"bots count like players"*).
- `endgame_046.png` — a second frame moments earlier.
- `server.log` — the server side of the same run: `[P583-PROOF]` fired,
  `[gameover] … iSideWinner=0`, and the `[scoring]` finalize lines
  (`ship 8098 (side 0) final score 55.60084 kills=2 … rescues=1 … baseKills=1
  … artifacts=1 win=True ratio=0.6666667 played=26s`), then
  `GAME_OVER_PLAYERS(63)` with 10 peers.

Because a *genuine* mission-ending event (capture/elimination) is a
scenario-engineering problem on a live box (the same limitation P578
documented for the player-eject), the game-over here was reached by a temporary
server hook that seeded a couple of counters and declared the win — the 
*display* of the counters is what the real-window screenshot proves, while the
counter *feeding* from real events is independently proven by the 5 live
`LivePlayerScoringFeedsTests` (rescue → +1, base kill/capture → +1, miner-kill
→ +1, eject → +1) and the 17 pure `PlayerScoreCalculateScoreTests`.
