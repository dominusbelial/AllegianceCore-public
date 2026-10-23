# P799 — native lives limit: ghosts, side elimination, and the HUD that shows them

Base: `master` at dispatch, `a1e6420a` (re-checked `git log -1` before starting; every
line number below re-grepped against it). Branch/worktree `pi/p799`. No commit or push;
all changes are in the working tree. Native reference: `~/Development/Allegiance/src/`.

## Why

`docs/parity/parity-matrix.csv` rows **Lives remaining** (hud, weight 3) and **Death
banner** (hud, weight 3) were `partial`: the port never simulated the lives limit —
production missions always sent the unlimited sentinel `0x7fff` (`Mission.Lives`
defaulted to `MissionDef.UnlimitedLives` and nothing ever set it), and the death banner
was gated on the port's transient `Dead` flag instead of native `IsGhost`.

## Native rules (verified against `~/Development/Allegiance/src/`)

| Rule | Native cite |
|---|---|
| `IsGhost()` = `m_nDeaths > GetMyMission()->GetMissionParams()->iLives` | `Igc/shipIGC.h:2465-2469` |
| `iLives` "Player must die more than this number of times to exit" | `Igc/igc.h:1185`; sentinel `c_cUnlimitedLives = 0x7fff` `igc.h:1007`, `MissionParams::Reset` default `igc.h:1291` |
| `fLimitedLives = misparms.iLives != 0x7fff` (lobby flag) | `FedSrv/fsmission.cpp:4086` |
| Ghosts cannot buy (`FM_C_BUY_LOADOUT` head) | `FedSrv/FedSrv.CPP:6153-6157` |
| Ghosts cannot promote (`FM_C_PROMOTE` head) | `FedSrv.CPP:6354-6357` |
| Ghosts cannot board/unboard (`FM_C_BOARD_SHIP` head) | `FedSrv.CPP:6387-6389` |
| Ghost launch refused: `(stationID == NA) && !IsGhost()`; a ghost's `NA` undock falls into the teleport branch where `GetStation(NA)` is invalid → `TELEPORT_ACK` `stationID=NA`, ship stays docked | `FedSrv.CPP:7048, 7063-7077` |
| Ghost-aware player counts (`bCountGhosts`) | `fsmission.cpp:1335-1361` (GetSidePlayerSum), `:1361-1427` (GetSideRankSum), `:1425-1432` (HasPlayers); lobby join `fslobby.cpp:140-148` |
| Death → `AddDeath()` → lifepod restart; then `if (pship->IsGhost() && !HasPlayers(pside, false)) DeactivateSide(pside)` | `FedSrv.CPP:12025-12032, 12034-12074 (restart), 12068-12072 (elimination)` |
| Station loss: `VacateStation` ejects non-ghosts, does NOT eject ghosts — it re-docks every ghost of the side at another `c_sabmRestart` base ("if they don't have one, don't worry: side will be eliminated") | `fsmission.cpp:4783-4850` (eject loop `:4796-4802`, ghost relocation `:4805-4849`) |
| Client: ghost docked view is command view, not hangar | `WinTrek/trekigc.cpp:2832-2857`, `WinTrek.cpp:2325-2340` (spawn), `WinTrek.cpp:6560-6570` |
| Client: back button never returns a ghost to the hangar | `WinTrek/console.cpp:1126-1131` |
| Client: `m_pnumberIsGhost` drives `DeathBannerPane = SwitchPane(IsGhost, deathbannerbmp)` | `WinTrek.cpp:3653`; `Artwork/dialog.mdl:5438-5439`, banner art `dialog.mdl:1998-2003` |
| Client: lives badge = `iLives − ppi->MissionDeaths()`, badge on while `< 10` | `console.cpp:812-821`; `MissionDeaths() = m_pship->GetDeaths()`, `clintlib/clintlib.h:346-352` |
| Client deaths arrive via `PLAYERINFO` `nDeaths` + `KILL_SHIP` `bDeathCredit` (`PlayerInfo::AddDeath`) | `clintlib/appmsg.cpp:2766-2773` |

The client learns `iLives` from `MISSIONDEF(38)`'s `MissionParams.iLives` and its own
deaths from `PLAYERINFO(10)`/`KILL_SHIP(105)` — the port already carried both values
(HUD-12 wired the badge plumbing in P713/P76x); what was missing was the server
simulation that makes them ever differ from the sentinel.

## Port before → after

### Server config / wire

- `GameConfig.Lives` (short, default `0x7fff` = `MissionDef.UnlimitedLives`, i.e. every
  existing config is byte-for-byte the old unlimited game) — `ServerConfig.cs`.
  Validated `>= 0` in `AssembleGameMission`, which now sets `mission.Lives = g.Lives`
  (`Server.Lifecycle.cs`). `Server.Lobby.cs` already copied `_missions[0].Lives` into
  both `MISSIONDEF(38)` sends, so the real value reaches the wire with no further edit.
- Skirmish: `SkirmishOptions.Lives` (default sentinel; choices unlimited/5/3/1),
  `SkirmishScreenModel` row 8 "Lives: …" (Start/Back shifted to rows 9/10),
  `SkirmishPrefs` round-trip through `player.json`, `CreateConfig` → `GameConfig.Lives`.

### Server simulation (`Allegiance.Server`)

- `IsGhost(LiveShip)` = owning peer's `PlayerScore.Deaths > mission.Lives`
  (`shipIGC.h:2465-2469`; the port's death counter is `CFSPlayer::AddDeath`'s
  `m_cDeaths`, already incremented at `Server.Combat.cs`'s death branch,
  `FedSrv.CPP:12032`). `SideHasNonGhostPlayers(mission, side)` = native
  `HasPlayers(pside, false)` over `_peerShips` (`fsmission.cpp:1335-1361`).
- Gates (all no-ops while `Lives` is the sentinel):
  - `ApplyLoadout`: ghost failure → negative ack `FBoughtEverything=false FLaunch=false`,
    ship unchanged (`FedSrv.CPP:6156`).
  - `PerformUndock`: ghost → `TELEPORT_ACK stationID=NA`, no state change, stays docked
    (`FedSrv.CPP:7048,7067-7073`). Covers both the `FM_C_DOCKED(NA)` path and
    BUY_LOADOUT's `fLaunch=true` (which is refused one gate earlier).
  - `HandleBoardShip` / `HandlePromote`: ghost requests dropped (`FedSrv.CPP:6388,6356`).
- Death → elimination (`Server.Combat.cs`, lifepod respawn block): after the ghost's
  pod docks, `if (IsGhost && !SideHasNonGhostPlayers) DeactivateSide(mission, side)`
  (`FedSrv.CPP:12068-12072`). The existing P455 `DeactivateSide` flow then runs the
  native game-over path: SIDE_INACTIVE(68) + "is no more" chat + the
  win-by-elimination check (last side standing wins / side-only game over).
- Station loss (`BroadcastStationDestroyed`, the `VacateStation` port): docked GHOSTS
  are no longer ejected — they are re-docked at another restart-capable base
  (`FindNearestRestartStation`), acknowledged with `TELEPORT_ACK(newStationId)` (port
  adaptation, stated in-code: native `VacateStation` sends no message, the docked
  SHIP_STATUS carries the new station). No alternate base → the ghost keeps its
  destroyed-station dock and the existing `SideLostRestartStation → DeactivateSide`
  machinery collects it (`fsmission.cpp:4789`).

### Client

- `ClientWorld.OwnIsGhost` = `TryGetOwnMissionStats deaths > MissionLives` — the exact
  native comparison over exactly the native wire values (MISSIONDEF iLives +
  PLAYERINFO/KILL_SHIP deaths).
- Death banner: `HudContext.Ghost` (new, set from `world.OwnIsGhost` in
  `Game1.Rendering.DrawHud`) → `HudModel.DeathVisible = Visible && Context.Ghost`
  (was `Dead && !Docked`). `HudElements` "Death banner" value = the
  `m_pnumberIsGhost` stamp. With unlimited lives nobody is ever a ghost, so the
  banner never shows — the native production behaviour.
- Ghost view mode: `Game1.Panes.HandleDockedScreen` — a docked ghost never enters the
  hangar; the camera goes to command view (native `SetViewMode(IsGhost ? vmCommand :
  vmHangar)`, `trekigc.cpp:2832-2857`), with the P571 dock-time autopilot teardown.
  This is also the console gating of `console.cpp:1126-1131` (a ghost can never reach
  the hangar view, so the back-button rule holds by construction).
- Lives badge: unchanged code path (`HudConsoleFormat.RemainingLives` + `LivesVisible`,
  HUD-12), now fed real values end to end.

### Bot guard (minimal, as the brief allows; listed per scope)

- `tools/allegiance-mcp/BotManager.Lifecycle.cs` `RelaunchDockedPod`: a bot whose
  player is a ghost stops requesting launches (sets `RelaunchExhausted` +
  `GrantWarning "… is a ghost (N deaths > lives M) — out of lives, cannot launch"`)
  instead of burning the 3-attempt retry budget on a refusal that can never succeed.
- `SkirmishSmoke`'s benchmark pilot: same guard before its periodic
  `SendBuyLoadout(fLaunch:true)` retry.
- No bot strategy change beyond these two guards.

## Tests

New (`tests/Allegiance.ClientCore.Tests/P799LivesLimitTests.cs` — 12; `.../P799LivesLimitLiveTests.cs`
— 4 live loopback; `tests/Allegiance.ClientCore.MonoGame.Tests/P799LivesLimitHudTests.cs` — 3):

| Test | Gate (fails on master because…) |
|---|---|
| `Game_config_lives_defaults_to_the_native_unlimited_sentinel` | `GameConfig.Lives` did not exist on master |
| `Skirmish_lives_option_flows_into_the_game_config` | same |
| `Skirmish_lives_row_cycles_unlimited_then_finite_budgets` | the Lives row did not exist (master: 10 rows, Start=8) |
| `Missiondef_codec_round_trips_a_finite_lives_value` | passes on master for the default; the finite-budget half is the new wire contract |
| `Own_ghost_rule_is_strictly_deaths_over_lives` (5 cases) | `OwnIsGhost` did not exist; master's MissionLives is pinned to the sentinel so finite budgets never ghost |
| `Unlimited_lives_never_ghosts`, `No_playerinfo_yet_means_not_a_ghost` | regression guards (pass on master) |
| `Death_banner_gates_on_ghost_even_when_docked`, `Dead_alone_no_longer_shows_the_death_banner`, `Death_banner_element_value_is_the_native_isghost_number` | **fail on master behaviour**: the banner was `Dead && !Docked` (a plain death showed the banner; a ghost docked in command view did not) |
| `Lives_badge_appears_only_for_a_finite_budget` | regression guard (passes on master) |
| LIVE `Limited_lives_death_makes_a_ghost_and_refuses_buy_and_launch` | **fails on master**: no lives limit ⇒ no ghost ⇒ the ghost's BUY_LOADOUT launches and `FM_C_DOCKED(NA)` undocks |
| LIVE `Unlimited_lives_death_is_never_a_ghost` | regression control (passes on master) |
| LIVE `Station_loss_relocates_a_docked_ghost_to_another_restart_base` | **fails on master**: no ghosts ⇒ the docked ship is simply undocked instead of re-docked at the alternate restart base |
| LIVE `Ghost_only_side_is_eliminated_and_last_side_wins` | **fails on master**: no ghosts ⇒ no `DeactivateSide` ⇒ no game over |

(Three tests use the new P799 config surface and therefore do not compile against
master; the behavioural fail-first claims are the four rows marked "fails on master".
The updated existing tests: `HudModelTests.Pod_uses_oxygen…` (banner gate is now
IsGhost) and `SP6GameSpeedTests` row indices 8→9/10.)

## Verification

- Whole-solution build: **0 warnings / 0 errors** (`dotnet build AllegianceCore.slnx`).
- `tools/build-tools.sh`: all 71 tool projects built, 0/0.
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained
  -p:PublishAot=true`): clean, no IL2026/IL3050.
- New tests 3× alone (live set run 5×; 13 pure + 4 live in ClientCore.Tests, 3 HUD in
  MonoGame.Tests): every run all-green.
- One Fast-tier run (`tools/run-tests.sh --tier fast`): 10,069 tests, 2 failures, both resolved:
  - `HudModelTests.Pod_uses_oxygen…` — asserted the pre-P799 Dead-gated banner; updated
    to the native IsGhost gate, then 54/54 alone.
  - `SessionLossLiveTests.EstablishedSession_ServerKilled…` — load flake (ran
    concurrently with the P799 skirmish live run); passes alone. Not ledgered (single
    occurrence under artificial load).
- Showcase never-parks 5× (`P700ShowcaseLiveTests`): 5/5 pass (default unlimited
  unchanged; bots/flight/sim touched only behind a finite `Lives`).

### Loopback skirmish-sized live run with lives = 1

Scratch harness `.scratch/P799SkirmishRun` (deleted after the run; raw logs →
`~/allegiance-evidence/P799/`) hosted the REAL skirmish (embedded server + the real bot
driver workers, 5v5, `SkirmishOptions{Lives=1, Difficulty=Hard}`). Two runs (35 min and
70 min wall, ~4200 s sim): **no natural side elimination occurred** — the ghost
pipeline is demonstrably working inside it, but the last pilot of one side (ship 8107)
never accumulated its second death:

| Evidence (70-min run, `~/allegiance-evidence/P799/skirmish-lives1-hard-70min.log`) | Value |
|---|---|
| Pilots ghosted (2 deaths > iLives 1, `shipIGC.h:2465-2469`) | 5 (ships 8100/8102/8103/8105/8109) |
| Successful launch ACKs by a ghost after its ghosting death | **0** for all 5 — every ghost stayed docked at its base for the rest of the match; no infinite buy/launch retry (the bot-driver guard + server gates held) |
| Successful launch ACKs by the 1×-dead (non-ghost, 8104) pilot | 2 — the one allowed relaunch still works |
| Side elimination | never fired (side 1's 5th pilot never died a 2nd time; most lethal hits eject — `bEjectPods` is always on in this port, `Server.Combat.cs:1636-1637`, out of P799 scope) |

The deterministic elimination proof is the LIVE
`Ghost_only_side_is_eliminated_and_last_side_wins` test (real server, real TCP clients,
lives budget 0, ends by elimination with the last side standing declared the winner
through the native game-over path).

## Parity rows

Both rows updated to `done` in `docs/parity/parity-matrix.csv` (Lives remaining, Death
banner) with the new port cites and exact gap notes.

## Unfinished / notes

- The 5v5 bot skirmish runs with `Lives=1` (35 min and 70 min wall) did not reach
  natural side elimination: bot lethality is low and most lethal hits eject
  (`bEjectPods` is always on in this port — `Server.Combat.cs:1636-1637`, out of P799
  scope to change), so the last non-ghost pilot of one side never accumulated its
  second death. The run still demonstrates the ghost pipeline end to end with real
  bots: 5 ghosted pilots, zero launch attempts (and zero successful ones) after
  ghosting, non-ghost relaunch unaffected (evidence table above). The elimination
  gate itself is proven live by `Ghost_only_side_is_eliminated_and_last_side_wins`.
- Native's ghost-aware lobby counts (`fslobby.cpp:140-148`, PLAYER_JOINED skips ghosts)
  and `GetSideRankSum` (`fsmission.cpp:1361-1427`) are covered by the shared
  `SideHasNonGhostPlayers` helper where the port has equivalents (game-over win check);
  the port's lobby layer has no per-ghost reporting surface to gate (out of the brief's
  scope items).
- Evidence: raw skirmish run logs + test TRX → `~/allegiance-evidence/P799/`.
