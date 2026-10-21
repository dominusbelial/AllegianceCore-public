# P774 — retire port-only score roster 209/219: the in-mission roster comes from PLAYERINFO + KILL_SHIP, as native

Branch `pi/p774`, base master after HUD-20 + P773 (worktree `allegiance-pi-p774`). Model: GLM-5.3 Flash.
No commits made; all changes left in the working tree.

## Step 0 findings

- P773 merged: `git grep -n "OwnShipKillBonus" -- src` is empty.
- HUD-20 is in the base and did **not** use its fallback: every mission peer gets one PLAYERINFO for every mission
  ship, enemies and drones included, seeded at each (peer, ship) EXPORT latch (`SeedMissionPlayerInfo`, cross-side sweep
  + same-side NPC paths in `Server.Broadcast.cs`; teammates keep the inline P63 send). The enemy-seed test exists:
  `Hud20PlayerInfoSeedTests.Enemy_and_enemy_drone_playerinfo_follows_their_first_cross_side_export_once`.
- HUD-20 added the shared builder `BuildPlayerInfo(Ship, short, string?, int kills, int deaths)` in
  `Server.Broadcast.cs`. P774 extended that one builder; it has **seven** call sites and every PLAYERINFO the server
  builds routes through it (builder: `Server.Broadcast.cs:1927`):
  1. serial P63 teammate send — `Server.Broadcast.cs:964`;
  2. parallel P63 teammate send — `Server.Broadcast.cs:1526`;
  3. parallel same-side NPC/drone seed (mirror of the serial seed; pair recorded in `r.MissionPlayerInfoSent`) —
     `Server.Broadcast.cs:1636`;
  4. serial same-side NPC/drone seed — `SeedMissionPlayerInfo` called from `Server.Broadcast.cs:1146`, building at
     `:1973`;
  5. serial cross-side seed (EXPORT sweep) — `SeedMissionPlayerInfo` called from `Server.Broadcast.cs:1886`, building
     at `:1973`;
  6. own-join send — `Server.Lobby.cs:3633`;
  7. join-roster snapshot (every other mission player's PLAYERINFO) — `Server.Lobby.cs:3936`.
- HUD-20 already encodes `dtidDrone` from the field (`FedSrv.CPP:2088-2091`), but the field **defaulted to 0**; P774
  changed the default to `NA` so any PLAYERINFO the port builds for a player is native-human.
- **MCP risk from HUD-20: handled.** `tools/allegiance-mcp`'s `IsDrone` (`BotManager.cs:1386`) was already updated by
  HUD-20 to `ship.Player is not { } info || info.DtidDrone != PlayerInfo.NA` — a ship whose PLAYERINFO has arrived is
  classified by `dtidDrone`, not by `Player is null`. Same-side ships (the only ships MCP's drone lists read) always
  have a PLAYERINFO (inline P63 send). P774 changes nothing here.

## Native rule, per field (cites under `~/Development/Allegiance/src/`)

| field | native in-mission source | P774 source |
|---|---|---|
| characterName | PLAYERINFO `CharacterName` (`FedSrv.CPP:2083`) | `PlayerInfo.CharacterName` |
| sideId | PLAYERINFO `iSide` (`:2099-2109`); JOIN_SIDE/QUIT_SIDE update it | `PlayerInfo.ISide` (client already updates it: `ClientWorld.cs` `Apply(JoinSide)`, `ClientWorld.Interface.cs` `Apply(QuitSide)`) |
| kills | PLAYERINFO `nKills` (`:2085`) + KILL_SHIP `AddKill` (`appmsg.cpp:2805-2808`) | seed + KILL_SHIP |
| deaths | PLAYERINFO `nDeaths` (`:2086`) + KILL_SHIP `AddDeath` (`appmsg.cpp:2766-2767`) | seed + KILL_SHIP |
| ejections | PLAYERINFO `nEjections` (`:2087`) + KILL_SHIP `AddEjection` (`appmsg.cpp:2770-2771`) | seed + KILL_SHIP (neither existed before; the port's server-side `PlayerScore.Ejections` existed but never reached a client in-mission) |
| rank | PLAYERINFO `rgPersistPlayerScores` NA entry (`FedSrv.CPP:952,2060-2071`; read back `clintlib.cpp:1019-1051`, `teampane.cpp:1866`) | one 12-byte `PersistPlayerScore` NA entry per PLAYERINFO; `rankID` stays 0 (native `//NYI`, `:2112`) |
| score, combatRating, rescues, baseKills, baseCaptures, artifacts, flags | **no native in-mission source** (score/base stats: GAME_OVER_PLAYERS only, `gameoverscreen.cpp:146-158`; combat rating never shown, `igc.h:5981`; rescues/artifacts/flags never reach a client, `igc.h:5999-6001`, `appmsg.cpp:309-340,3113-3150`) | read 0 until P774b (the UI change) |

There is no native message that streams a roster; native's `DEFINE_FEDMSG` table ends at CLUSTERINFO = 200
(`Messages.h:1039`). Native keeps one `PlayerInfo` per ship (`clintlib.h:343-361`); `IsHuman()` is `dtidDrone == NA`
(`clintlib.h:343`).

## What was removed, and what replaced it

Removed (fmid 209 `ScoreRosterUpdate` and fmid 219 `ScoreRosterDelta`):

- Wire codecs `src/Allegiance.Core/Wire/ScoreRosterUpdate.cs` / `ScoreRosterDelta.cs` (deleted, after moving the
  record).
- Server: the `broadcastScoreRoster` step, `ScoreRosterWorkDue`, `BroadcastScoreRoster`, `ScoreRosterState`,
  `_scoreRosterStates`, `ScoreRosterRefreshTicks`/`ScoreRosterFullRefreshSeconds`, the `Server.Lobby.cs` per-mission
  removal, both test-seam spots, and the `209 =>`/`219 =>` egress names (a stray 209/219 now labels `"fmid"`).
- Client: both `ConnectionSession` dispatch cases (a stray 209/219 now lands in `UnknownFmids`) and
  `ClientWorld._scoreRoster` as a wire mirror, `Apply(ScoreRosterUpdate)` and both `Apply(IFedMessage)` switch cases.
- Bot: `_scoreRosters`/`_scoreRoster`, `ScoreRosterBroadcasts`/`ResetScoreRosterBroadcasts`, the `Connect` reset and
  both dispatch cases. No replacement accessor — `PlayerInfos` and KILL_SHIP events already exist.
- `tools/receive-proof`: the roster precondition/observation replaced with the PLAYERINFO one (below).

Replacements:

1. **PLAYERINFO carries what native carries** (`Core/Wire/PlayerInfo.cs`): `PersistScores`
   (`readonly record struct PersistPlayerScore(float Score, float CombatRating, short Rank, short CivId)`, 12 bytes,
   `igc.h:5486-5494`), encode `ib=68 / cb=12n / cbmsg=68+12n`, decode accepts n in 0..20 (`cCivsMax`, `FedSrv.CPP:2058`)
   and still throws on anything malformed; `MessageSize = 68` stays the fixed width; `DtidDrone` defaults `NA`;
   `IsHuman` (`clintlib.h:343`); saturating `AddEjection()`; `Rank` = the NA entry's rank, else 0
   (`clintlib.cpp:1042-1051`). Server `BuildPlayerInfo` now fills `NEjections` (clamped like `NKills`,
   `FedSrv.CPP:2087`) and `PersistScores = [new(0,0,rank,NA)]` for a player / rank 0 for a drone
   (`:952`, `:2073-2077`, `igc.h:5449-5455`). **Documented simplification:** native sends an NA entry *plus one entry
   per civ* (`FedSrv.CPP:952-957`); the port sends only the NA entry because every native rank reader in the client
   uses NA (`teampane.cpp:308,1866`, `newgamescreen.cpp:1045`, `gameoverscreen.cpp:129`), and the port has no persisted
   career stats, so Score/CombatRating travel as 0.
2. **Client counts ejections**: `Apply(KillShip)` calls `e.Player?.AddEjection()` when `!m.BDeathCredit`
   (`appmsg.cpp:2770-2771`); death/kill lines unchanged.
3. **`ClientWorld.ScoreRoster` is derived**: every Apply that changes an input —
   `Apply(PlayerInfo)`, `Apply(KillShip)`, `Apply(JoinSide)`, `Apply(QuitSide)`, `Apply(QuitMission)`,
   `Apply(JoinedMission)` (the own-ship stub writer) and the ship-table clear in `ApplyLocalObservation` — only sets
   a dirty flag; the roster is rebuilt **once per received batch** in `MarkApplied()` (called by `ConnectionSession`
   after each batch and at the end of `ApplyLocalObservation`), with no LINQ (reused buffer + static comparer).
   `Reset()`/`BeginNextMission()` clear it to empty directly. (Review fixup: the first cut rebuilt on every Apply,
   ~N³ work and GBs of garbage per rotation across a bot process's ~214-400 worlds.) One row per ship with
   `Player is { IsHuman } && ISide >= 0`, ordered by ship id — the same population 209 carried. Two reads with no rebuild
   in between return the same instance, and an N-PLAYERINFO batch is one rebuild (`ScoreRosterRebuilds`; pinned by test). `ScoreRosterEntry` moved to
   `src/Allegiance.ClientCore/State/ScoreRosterEntry.cs` (`Allegiance.ClientCore.State`), same 13 positional fields,
   no `Size`/`Write`/`Read`.
4. **Funding recipients** (`InvestmentFundingInput.cs`): `s.Player is { IsHuman: true }` on the ship's own PlayerInfo
   (native `IsHuman()`, `clintlib.h:343`), no name match; `ResearchProduction` exclusion kept.

### Ordering check (Fix 6 — report only, no fix needed)

Combat runs in the `update` step of `SimPass`; KILL_SHIP is sent synchronously inside `BroadcastKillShip`
(`Server.Combat.cs:1705-1720`). Every PLAYERINFO build site runs in a later step of the same tick
(`broadcastShipStates` serial + parallel, `crossSideExportSweep`), and the parallel path flushes `r.Packets` in its
serial apply phase within the same `SimPass`. A PLAYERINFO is therefore always built *after* the tick's combat step
with the already-updated `PlayerScore`, and arrives after that kill's KILL_SHIP — no double count. Native has the same
property because one thread sends both, in order.

### UI behaviour changes between P774 and P774b

Scoreboard and pilot-info overlay keep drawing all 13 fields, but the seven fields with no native in-mission source
(score, combat rating, rescues, base kills, base captures, artifacts, flags) now read **0** for everyone (they used to
show the server's live P193 values). Kills, deaths, ejections, rank, names and sides are unchanged or better (ejections
and enemy rows are new). Team pane, commander side headers and the funding list behave as before. P774b (appendix)
drops the zero columns natively.

## Matrix / docs

- `docs/parity/fedmsg-wiring.csv`: deleted the `ScoreRosterDelta,219,…` row (there was no 209 row); `PLAYERINFO,10` and
  `KILL_SHIP,105` client-effect texts updated. One-off parse: every remaining row has exactly 8 fields.
- `docs/parity/parity-matrix.csv` row 495: replaced "live match stats" with
  `live match kills/deaths/rank from PLAYERINFO + KILL_SHIP (P774; clintlib.h:343-361; appmsg.cpp:2766-2771,2805-2808)`;
  stale cite `Game1.Rendering.cs:1655` → `:1852` (the comma-containing field is quoted). Every row parses with exactly
  6 fields. `git diff --stat docs/parity` shows only these two files.
- `docs/SCOREBOARD_P265.md`: superseded-by line added at the top. No `docs/reports/*` touched.

## Tests — new/rewritten, and their failures on the unfixed code

| test | result on unfixed code | result now |
|---|---|---|
| `Core.Tests/Wire/PlayerInfoWireTests.PlayerInfo_round_trips_one_persist_entry_ejections_and_human_dtid` (+ the new no-entry/default-NA test) | does not compile against the unfixed tree (no `PersistScores`; the unfixed encoder writes cb=0 and dtid 0, so the 80-byte/`0xFFFF`-at-58 expectations cannot hold) | pass |
| `PlayerInfo_RejectsWrongFmid_NonZeroScoreBlob_AndWrongLength` (rewritten: bad stride, wrong ib, trailing byte, 21 entries throw; cb=12 decodes) | unfixed decoder throws on *any* non-zero cb, so cb=12-decodes fails | pass |
| `ClientCore.Tests/P774DerivedScoreRosterTests` (5 tests: humans-only roster, kill/eject counters, side change/quit, reset/next-mission, same-instance; + 2 review tests below) | tests 1–3 fail at runtime (roster empty without 209; no AddEjection); reset/same-instance pass on both (stated per the brief) | 7/7 pass |
| `MonoGame.Tests/P728CommanderTests.Funding_recipients_are_PLAYERINFO_humans_not_drones` | fails at runtime (no roster ⇒ no recipient) | pass |
| `Core.Tests/P774Fmid209And219RetiredTests` (3 tests) | **3/3 fail** (verified): fmid 209/219 consts exist, `BroadcastScoreRoster`/`ScoreRosterWorkDue` exist, `ScoreRosterBroadcasts` exists | pass |
| `ClientCore.Tests/ScoreRosterLiveTests.Scoreboard_tracks_real_kills_from_PLAYERINFO_and_KILL_SHIP_without_fmids_209_219` (rewritten in place) | fails at runtime on the harness-has-rows assertion (P672 sends harness peers nothing); the final no-209/219 assertion also cannot pass pre-fix | pass |
| `Core.Tests/P774PlayerInfoSeedLiveTests.Late_joiner_PLAYERINFO_seeds_real_kills_deaths_ejections_and_rank` | does not compile on the unfixed tree (no `PersistScores`); unfixed seed has `NEjections=0` and no persist entry | pass |
| `ClientCore.Tests/FedMsgDispatchCoverageTests` | counts set to 14/113 (port-added 16→14, total 115→113; doc comment updated) — passes on both code states | pass |
| migrated seeding tests (`P761TeamPaneSortTests`, `TeamPaneControlsTests`, `P764UiSoundSeamTests`, `P728CommanderTests.FundingWorld`, `P761TeamLobbyControlsTests`) + `P642ConsoleZoomTests`/`PilotInfoLayoutTests`/`ScoreboardLayoutTests`/`TeamPaneLayoutTests` namespace fixes | **12 fail** at runtime on the unfixed code (verified): every test that expected roster rows from the PLAYERINFO seeds — 209-only facts changed to PLAYERINFO seeding; assertion texts unchanged | pass |

Deleted: `Core.Tests/ScoreRosterWireTests.cs`, `Core.Tests/ScoreRosterDeltaTests.cs`,
`ClientCore.Tests/ScoreRosterDeltaTests.cs`, `Core.Tests/P672ScoreRosterTrafficTests.cs`,
`Core.Tests/P684MergeTests.cs`, `Core.Tests/ScoreRosterLiveTests.cs` (replaced by `P774PlayerInfoSeedLiveTests.cs`).
`RetryCleanupTests:97` (`Assert.Empty(world.ScoreRoster)` after `Reset`) passes unchanged.

Final grep: `git grep -n "ScoreRosterUpdate\|ScoreRosterDelta\|ScoreRosterBroadcasts\|broadcastScoreRoster\|_scoreRosterStates" -- src tests tools`
returns **four hits, none of them live code**:

- `src/Allegiance.ClientCore/State/ScoreRosterEntry.cs:12` — the doc comment naming the retired 209/219 messages
  (P774b rewrites this file);
- `tests/Allegiance.Core.Tests/P774Fmid209And219RetiredTests.cs:8,47,49` — the guard test's own doc comment and the
  member names it asserts are absent (`ScoreRosterBroadcasts`, `ResetScoreRosterBroadcasts`).

`ScoreRosterEntry` and `ScoreRoster` remain (client-side, derived).

## Network traffic

Before (P672 design): 209 = `8 + 56n` B snapshot on join, on every stage change and every 30 sim seconds; 219 =
`12 + 58k` B delta at most every 40 ticks when a row changed; harness peers got neither. At 214 peers one snapshot is
`8 + 56×214 = 11,992 B` ≈ **400 B/s per human recipient** steady state (arithmetic, not measured here).

After P774: **209/219 = 0 bytes** — measured by frame capture in the rewritten live test
(`P774-TRAFFIC` line): 44,353 captured frames across the whole scenario (4 seats + rejoin + full game-over rotation),
**0 roster frames, 0 roster bytes**; 55 PLAYERINFO frames totalling 4,400 B, avg **80.0 B/frame** = the 68-byte fixed
region + exactly one 12-byte persist entry. The "before" PLAYERINFO figure for the same scenario is arithmetic, not
captured: 55 × 68 B = 3,740 B (+660 B, +17.6 %). The persist entry adds 12 B per (peer, ship) PLAYERINFO — arithmetic:
≈2.6 KB per joining peer at 214 ships, ≈550 KB once per full 214-peer reseat. KILL_SHIP (14 B) already went to every
mission peer; PLAYERINFO-per-peer is HUD-20's cost. Net: the steady per-human stream and its join/stage snapshots are
gone; a one-time 12 B per (peer, ship) pair is added.

`tools/receive-proof`: precondition replaced with "every peer holds a PLAYERINFO for every mission ship"
(`bots.PlayerInfos` distinct ShipIds ≥ count); observation now reports `playerInfosPerPeer`/`distinctPlayerInfoShips`.
Built and run at 96 peers (`~/allegiance-evidence/P774/receive-proof/observations.json`): the precondition **held**
(every peer held PLAYERINFOs for all 96 mission ships, including cross-side peers); the full 390-peer run was not
executed for time — at that scale the HUD-20 EXPORT gating means the precondition holds only once every pair's EXPORT
has fired, which the proof's join-burst pump provides for seated peers (no drop needed at 96; flagged if 390 is ever
re-run).

## Rotation burst

P774 grows each PLAYERINFO 68 → 80 B, so the per-match-rotation PLAYERINFO burst (every peer re-receives a PLAYERINFO
for every mission ship: N² pairs) grows by the same **~17.6 %**: the ~4.7 MB burst estimated at 214 peers becomes
~5.5 MB (for a strict 214 × 214 pair count: 3.11 MB → 3.66 MB).

Native has **no per-rotation burst**: PLAYERINFOs are sent at join time only (`fsmission.cpp:411` the joiner's own,
`fsmission.cpp:3958-3968` every mission ship to the joiner), and game over keeps the same `CFSMission` and restarts it
in place (`SetStage(STAGE_NOTSTARTED)`, `fsmission.cpp:3483-3490`), so no re-join happens. The burst is the port's own
cost, from the P439 fresh-mission rotation that re-joins every peer into a new mission. **The derived roster now
relies on that resend** to re-seed every world after `BeginNextMission()` clears it; a future task that moves the
rotation to native's restart-in-place must keep the PLAYERINFO objects (and so the roster) alive across the restart
instead.

## Gates (measured)

- Whole-solution build (`AllegianceCore.slnx`, includes `tools/allegiance-mcp` and `tools/receive-proof`): 0 errors,
  0 warnings.
- **AOT publish** `dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`:
  succeeded, no IL2026/IL3050.
- 3× alone (all 3/3 pass): `PlayerInfoWireTests` (13), `P774Fmid209And219RetiredTests` (3),
  `P774PlayerInfoSeedLiveTests` (1), `P774DerivedScoreRosterTests` (5), `ScoreRosterLiveTests` (1, ClientCore),
  `FedMsgDispatchCoverageTests` (3), `RetryCleanupTests` (2), `P728CommanderTests` (21), `P761TeamPaneSortTests` (17),
  `P761TeamLobbyControlsTests` (11), `TeamPaneControlsTests` (7), `TeamPaneLayoutTests` (11), `ScoreboardLayoutTests`
  (5), `PilotInfoLayoutTests` (7), `P642ConsoleZoomTests` (44), `P764UiSoundSeamTests` (24), `Hud12DataAndArtTests`
  (14), `KillBonusWireLiveTests` (1), `BroadcastSnapshotLoopbackTests` (4), `PipelineCostTests` (5),
  `Hud20PlayerInfoWireTests` (3), `Hud20PlayerInfoSeedTests` (5), `ClientWorldHud20Tests` (7),
  `ClientWorldKillBonusTests` (4). No flakes.
- Fast tier `tools/run-tests.sh --tier fast`: **PASS** — 9,808 tests, 0 failed (270.6 s).
- Showcase never-parks `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`: **5/5 pass**
  (1 m 42 s–1 m 50 s each), consistent with the base rate.

## Out of scope / left for P774b

- The seven source-less fields still render 0 in the scoreboard and pilot-info overlay; P774b drops those columns and
  shrinks `ScoreRosterEntry` to 6 fields.
- Native's team pane also lists drones (`teampane.cpp:500-512` `IsAllyOrHumanOrSpotted`) — the port's derived roster
  lists humans only (drones stay out, as the retired 209 did). Noted, not changed.
- Persisted career score/combat rating (no stats DB — the persist entry carries 0/0 by design).
- The 390-peer receive-proof run (96-peer run performed; see above).

## Claude review fixups (squashed into this commit)

1. `P774_BRIEF.md` (committed at the repo root by mistake) removed.
2. **Perf:** the derived roster was rebuilt with a LINQ filter+sort+array on every `Apply(PlayerInfo)`, KILL_SHIP,
   JOIN_SIDE and QUIT_SIDE — with ~214-400 `ClientWorld`s per bot process and N PLAYERINFOs per world at each match
   rotation, ~N³ work and GBs of garbage. Those paths now only set `_scoreRosterDirty`; `MarkApplied()` (end of each
   received batch, `ConnectionSession.cs` ~1655, and the end of `ApplyLocalObservation`) rebuilds once, LINQ-free
   (reused `List` buffer + static comparer). `ApplyLocalObservation`'s ship-table clear now marks dirty (a stale
   roster used to survive an observer frame); `Reset`/`BeginNextMission` set the roster empty directly. Still never
   rebuilt on read; the same instance is returned between rebuilds. New public diagnostic `ScoreRosterRebuilds`.
   Tests that read the roster right after `Apply` now call `MarkApplied()` (the receive thread's batch end).
   New tests: `A_PLAYERINFO_burst_rebuilds_once_per_batch_not_per_message` (fails if `Apply(PlayerInfo)` rebuilds
   eagerly) and `An_observer_frame_that_clears_the_ship_table_yields_a_fresh_roster` (fails without the dirty mark).
3. `P728CommanderTests.Funding_recipients_are_PLAYERINFO_humans_not_drones`: the drone now has ship id 5 (below every
   human) and the test walks the whole TAB cycle; it fails with the filter mutated to `s.Player is not null`.
4. This report: the final-grep hits, the seven builder call sites, the measured-vs-arithmetic traffic wording and the
   "Rotation burst" section.
5. `PlayerInfo.cs:23` malformed doc tag `</code>` → `</c>`.
6. `PlayerInfoWireTests`: `badStride` now isolates the `cb % 12` rule (cb = 13 with length/cbmsg consistent at 81 B);
   new `PlayerInfo_Decode_accepts_20_persist_entries_and_rejects_21` covers the decoder's `> 20` branch (each fails
   when its rule is removed from the decoder).

