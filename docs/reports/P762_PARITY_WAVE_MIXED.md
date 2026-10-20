# P762 — mixed parity wave: server tick, ready rules, drones, moderation, sector-map icons

Base: `master` at dispatch (`e1c528b2`). Changes left uncommitted in the working tree.
Branch lane: `pi/p762`.

## Result

All nine rows are now `done`/`implemented`. Five simulation rows moved out of
`partial` and four interface rows moved out of `partial`/`missing`.

> **P762b (review fix round) changed several of the claims below.** The
> authoritative corrections are in [Review fixes (P762b)](#review-fixes-p762b)
> at the end of this report; the affected row bullets here were edited to match
> the final code.

### Scorecard (CSV row counts; weighted = sum of the `weight` column)

| Matrix | Before | After | Δ |
|---|---|---|---|
| `sim-rules-matrix.csv` implemented | 332 rows / 1048.0 wt | **337 / 1065.0** | +5 rows / +17.0 |
| `sim-rules-matrix.csv` partial | 59 / 179.0 | 54 / 162.0 | −5 / −17.0 |
| `parity-matrix.csv` done | 462 / 1397.74 | **466 / 1404.07** | +4 / +6.33 |
| `parity-matrix.csv` partial | 148 / 393.83 | 145 / 388.83 | −3 / −5.00 |
| `parity-matrix.csv` missing | 21 / 25.44 | 20 / 24.10 | −1 / −1.34 |

Only the nine P762 rows were edited.

## Rows

### 1. SIM-0064 — mine attrition (`Igc/clusterIGC.cpp:255,322-345`)
**Status: implemented (ordering corrected in P762b).**
- Native applies accumulated mine damage inside the `activeF && bStarted` block
  **after collision resolution and before every model's `Move`**, walking the
  ship list **back to front** because children follow parents
  (`clusterIGC.cpp:255,325-335`).
- Port: `Sector.Update` calls `ResolveMineDetonations` **after every collision
  pass** (`src/Allegiance.Core/Model/Sector.cs:938`); the method gates on the new
  `Sector.Active` and `MissionStage.Started`, accumulates the worst hit, then
  applies in reverse snapshot order (`Sector.cs:3463`). The port's per-ship
  `Move` runs inside `OnUpdate` (before the collision passes), so only the
  collision→mine half of native's order is reproduced; P762's "no move on the
  death tick" claim is withdrawn — see the P762b section.
- Tests: `P762SectorTickTests.Mine_damage_kills_and_finalises_the_ship_in_a_started_tick`,
  `P762SectorTickTests.Inactive_sector_skips_mine_damage_and_the_visibility_cadence`,
  `P762SectorTickTests.Mine_damage_walks_ships_back_to_front_so_children_take_their_hit_before_parents_die`.
  Because the mine pass now runs after this port's Move, the `MineCombatTests`
  formula tests were corrected to capture the post-Move velocity the pass sees
  (14/14, 3× alone).

### 2. SIM-0065 — visibility and cluster callback every tenth pass (`Igc/clusterIGC.cpp:348-357`, `clusterIGC.h:22`)
**Status: implemented.**
- Port: visibility now runs only on an **active started** pass whose counter is
  `% c_nPassesPerUpdate == 0` (`Sector.cs:1031`; `Sector.Active` is native's
  `activeF`, P762b), and the sector exposes a plain
  bool `ClusterUpdateDue` (`Sector.cs:190`) — no per-pass allocation. `Mission.Update`
  raises `ClusterUpdateEvent` only for a due sector (`src/Allegiance.Core/Model/Mission.cs:748`),
  so the callback is once per cluster per tenth active started pass, not once per chunk.
- Starting-stage chunks no longer advance the phase.
- Tests: `P762SectorTickTests.Visibility_and_cluster_callback_run_once_per_ten_active_started_passes`
  pins the boolean cadence (due on passes 1 and 11 of 20) and exactly 2 callbacks per cluster.
  `MissionUpdateTests` and `P747ConstructionTests` were updated from the old per-chunk
  callback expectation to the native cadence.

### 3. SIM-0142 — `FAllReady`, `bAutoStart`, restart readiness (`FedSrv/fsmission.cpp:4375-4444`, `FedSrv/FedSrv.CPP:8178-8226`)
**Status: implemented (FAllReady corrected in P762b).**
- Port: `Server.Lobby.cs:1193` is the native `FAllReady` **balance check only**:
  min players < `nMinPlayersPerTeam`; max > `nMaxPlayersPerTeam`;
  `min + iMaxImbalance < max`; rank spread > `GetRankThreshold()` in
  `iMaxImbalance == 0x7ffe` mode; inactive empty teams skipped under
  `bAllowEmptyTeams`. Native's "every side ready" loop
  (`fsmission.cpp:4435-4444`) is unreachable dead code after `return true`
  (`:4432`), so it is not part of FAllReady (P762b).
  The port's ready-up gate is the separate P180 policy in `TryAutoStart`
  (`Server.Lobby.cs:1129`, `IsAllOccupiedSidesReady`).
- `GetRankThreshold` follows the native integer formula
  (`fsmission.cpp:1380-1408`) at `Server.Lobby.cs:1248` and divides by the whole
  mission ship list (including docked ships), P762b.
- `bAutoStart` / `fStartCountdown` are config (`ServerConfig.cs:117,127`) and gate
  the ready-up countdown (`TryAutoStart`, `Server.Lobby.cs:1129`). The non-arena
  `AutoRestart` path now uses the same `FAllReady` after force-ready
  (`FedSrv.CPP:8185-8220`). P660 `AutoNextMatch` is untouched.
- P762b tests: `P762LobbyModerationTests.FAllReady_is_the_native_balance_check_only_and_ignores_side_ready_flags`
  and `...GetRankThreshold_divides_by_the_whole_mission_ship_list_including_docked_ships`.
- Arena note: when `AllowEmptyTeams` is set the port keeps its established
  occupied-side rule as a fallback, matching native's arena AutoRestart path
  that bypasses `FAllReady` entirely. Non-arena missions use the full policy.
- Tests (`P762LobbyModerationTests`): min/max counts, player-count imbalance,
  rank spread on/off, inactive-empty skip, every-active-side-ready, and
  `AutoStart` off/on (asserts `Starting` and the configured deadline).

### 4. SIM-0471 — target hull/shield fractions (`FedSrv/FedSrv.CPP:4076-4124`)
**Status: implemented.**
- Port: a shared resolver (`src/Allegiance.Server/Server.Broadcast.cs:2928`) is used by
  both the legacy and interest-tier per-peer paths (:2601, :2644). It defaults both
  bytes to 255 and then:
  ship → `GetSourceShip` root, skipping self, hull plus shield only when a shield
  part is mounted; station → hull + shield; probe/asteroid/missile → hull only;
  any target outside the player's cluster → 255/255.
- `TargetInCluster` now recognises the port's static-body `EquipmentTarget`
  wrapper; `Ship.HasMountedShield` supplies the native mounted-shield gate.
- Test: `P762TargetFractionLiveTests.Target_fraction_payload_covers_every_native_target_type_and_out_of_cluster`
  runs over a real loopback server for ship, child/source collapse, station,
  asteroid, probe, missile and out-of-cluster. The existing P139 lock test was
  corrected to expect 255 shield for a shieldless target.

### 5. SIM-0145 — chat budget charge/debt (`FedSrv/fsship.h:24-25,449-468`)
**Status: implemented.**
- Port: `TryChargeChatBudget` (`src/Allegiance.Server/Server.Dispatch.cs:2159`) now matches
  `DecrementChatBudget` exactly: paid send allowed iff budget ≥ cost, then
  subtract cost and floor at −cost; unpaid send (only `CHAT_GROUP_NOECHO`, per
  `FedSrv.CPP:3372`) allowed iff budget ≥ 0 and leaves the budget unchanged.
- `Transport.ChatBudgetProfile` selects `"dedicated"` (P654 1000/20/+5, default)
  or `"native"` (100/20/+1) (`ServerConfig.cs:705`, `Server.Lifecycle.cs:1512`).
  Explicit values/env still override.
- Tests: `P762ChatBudgetTests.Default_and_native_profiles_are_selectable` and
  `...Paid_sends_charge_and_floor_at_negative_cost_while_unpaid_sends_only_test_nonnegative`.

### 6. Interface — commander / Drones: mine layers / least-targeted asteroid plan (`Igc/shipIGC.h:2417-2434`)
**Status: done.**
- Port: `FindLeastTargetedAsteroidAnyCluster` (`src/Allegiance.Core/Sim/Ship.cs:2273`) keeps the
  same-side contender count and now applies native's cautious-cluster filter when
  expanding across warps. P762b deleted P762's private `Ship.IsFriendlyCluster`
  stand-in and uses the master P753 `Sector.IsFriendlyCluster(side)`
  (`src/Allegiance.Core/Model/Sector.cs:746`) — territory **and** the weighted
  seen-ship count (`common.cpp:388-431`, called at `common.cpp:693`).
- Tests: `P762DroneRuleTests.Layer_default_plan_picks_the_least_targeted_asteroid`
  (discriminating busy/free rocks) and
  `...Layer_cowardly_search_skips_unfriendly_clusters` (enemy-held rock with zero
  contenders is skipped for a friendly rock with a contender).

### 7. Interface — commander / Drones: constructors / drilling and building controls (`Igc/shipIGC.cpp:875-895,2262-2300`)
**Status: done.**
- Port: `Ship.ApplyBuilderDrillingControls` (`src/Allegiance.Core/Sim/Ship.cs:7564`, called from
  `OnUpdate` before the move) holds yaw 0 / pitch 0 / roll 1 / throttle −0.8 while
  building, and roll 1 / throttle 0 while drilling on the rock's rotation axis
  (`offset² < 50`, `distance² < (r_ship + r_rock + 25)²`). Off-track it leaves the
  autopilot approach in place, the port's form of native clearing the drilling mask
  and re-approaching.
- Asteroid collision while drilling and NPC-autopiloted now creates the building
  effect once and raises drilling hull-hit debris (`Sector.cs:2662-2680`) through a new
  `IIgcSite.HullHitEvent` (`IIgcSite.cs:278`) that the server host broadcasts on the
  existing `PROJECTILE_HULL_HIT(213)` visual channel with source −1 (`ServerHost.cs:686`,
  `Server.Combat.cs:79`).
- Tests: `P762DroneRuleTests.Building_builder_holds_native_roll_and_negative_throttle`,
  `...Drilling_builder_holds_only_when_the_rock_axis_alignment_is_on_track`, and
  `...Drilling_collision_creates_one_building_effect_and_hull_hit_debris`.

### 8. Interface — commander / Sector threat / Map threat marker (`WinTrek/sectormap.cpp:1056-1099`)
**Status: done.**
- Port: `StarmapRenderer` now decodes and draws the native artwork through the shared
  `.mdl` pipeline (`MdlTexture2D`): `sectorwarningbmp`, `sectorcapitalbmp`,
  `sectorbomberbmp`, `sectorminerbmp`, `sectorcombatbmp` on the flash phase;
  `sectorhighlightbmp` for the **chat cluster** (`GetChatCluster()`, the port's
  `ViewedClusterId ?? own.SectorId`) and `sectorselbmp` for the selected
  cluster, with the chat cluster taking precedence (`StarmapRenderer.cs:174`;
  pure rule in `StarmapThreatPresentation.OverlayFor`). P762b also restored
  P374's flashing cyan `sectorEmpty` outline for the commander's
  `HIGHLIGHT_CLUSTER` set on the off-flash phase
  (`StarmapThreatPresentation.FlashingOutline`, `sectormap.cpp:1043-1046`).
  The tier → stem mapping stays pure in
  `StarmapThreatPresentation.AssetStem` (`StarmapThreatPresentation.cs:94`).
- Tests: `P762StarmapArtworkTests` pins the native cascade/stem mapping and the
  presence of all seven artwork files; `P762bStarmapOverlayTests` pins the
  chat-cluster bitmap precedence and the flashing-outline phase.
- Screenshots: threat tiers (renderer harness)
  [p762_starmap_threats_1280x800.png](P762_assets/p762_starmap_threats_1280x800.png);
  **real client sector map** for P762b
  [p762b-sector-map-overlays-720p.png](P762B_assets/p762b-sector-map-overlays-720p.png)
  (and the on-flash-phase contrast shot in the same folder), produced by
  `dotnet run --project tools/p762b-sector-map-proof` drives the real `Game1`
  against a real loopback server. See the P762b section for what is visible.

### 9. Interface — lobby / native moderator ban and defection boot (`FedSrv/FedSrv.CPP:6749-6818`)
**Status: done.**
- Port: `Server.Moderation.cs` now:
  - grants holders of a configured moderator **bearer secret** the right to
    remove any player with a reason + message (the port of the Steam
    moderator/developer group identity). P762b security fix: only the secret
    presented in `LOGONREQ.CDKey` is compared (fixed-time); self-asserted
    character names and Steam IDs are never credentials — see
    `ServerConfig.ModeratorAccounts` and the P762b section;
  - otherwise permits only self-quit (`QSR_Quit`) or a side leader booting a
    teammate (`QSR_LeaderBooted`), with the `TrapHackBoot` reason match;
  - refuses a leader boot when `bLockTeamSettings` is set (new `GameConfig` flag).
- Client: master's full `QuitSideReason` enum (`AdminBooted = 2`, P758) and
  `ClientWorld.OwnBootMessage` return the native “You have been booted by a
  server administrator!” text (`trekigc.cpp:5288-5290`); the redundant P762
  `DepartureNotice`/`P762DepartureNoticeTests` were deleted in P762b.
- Tests (`P762LobbyModerationTests`): self quit, non-leader boot rejected,
  cross-side boot rejected, wrong reason rejected, leader teammate boot accepted,
  locked-team boot refused, non-moderator admin reason rejected, configured
  moderator accepted, and a real loopback moderator ban (`FltBot` →
  `QUIT_MISSION(124)`) that removes the target and delivers the admin reason + message.
  The client wording is pinned by the existing P758 tests after the merge.
- Scope note: the port's client never sends `FM_CS_QUIT_SIDE` (side changes go
  through `DELPOSITIONREQ`/`QUIT_MISSION`), which is the same mechanism native's
  context “ban player” uses (`QUIT_MISSION` + `QSR_AdminBooted`).

## Verification (P762 original — affected gates re-run in P762b below)

| Gate | Result |
|---|---|
| Whole-solution build | **0 warnings / 0 errors** |
| AOT publish (`linux-x64`, self-contained, PublishAot) | **success**, 0 `IL2026`/`IL3050`; log `~/allegiance-evidence/P762/aot-publish.log` |
| New P762 Core tests, 3× alone | 23/23 passed each run (sector tick, FAllReady/moderation, drones, target fractions, chat budget) |
| New P762 MonoGame tests, 3× alone | artwork 2/2 and departure notice 1/1 passed each run |
| Fast tier (final) | **PASS** — 9040 tests, 270.7s; log `~/allegiance-evidence/P762/fast-tier.log` |
| Showcase never-parks, 5× | **5/5 passed** (~1m50s each) |
| Screenshot | `docs/reports/P762_assets/p762_starmap_threats_1280x800.png` |

One earlier fast-tier run failed only `SessionLossLiveTests.EstablishedSession_ServerKilled…`
(“SessionLost event must fire on the transport thread”), an unrelated
load-sensitive test. It passed 3/3 alone and the next full fast-tier run passed.

Raw logs/TRX are kept outside the repo at `~/allegiance-evidence/P762/`; the only
committed evidence is the 60 KB screenshot under `docs/reports/P762_assets/`.

## Files changed

Core/sim: `Sector.cs`, `Mission.cs`, `Ship.cs`, `EquipmentTarget.cs`, `IIgcSite.cs`,
`RecordingIgcSite.cs`, `QuitSideReason.cs`.
Server: `Server.Lobby.cs`, `Server.Moderation.cs`, `Server.Broadcast.cs`,
`Server.Dispatch.cs`, `Server.Lifecycle.cs`, `Server.Combat.cs`, `ServerHost.cs`,
`ServerConfig.cs`.
Client: `StarmapRenderer.cs`, `StarmapThreatPresentation.cs`,
`Game1.FlightInput.cs` (merge: master's OwnBootMessage), `Game1.Lifecycle.cs`;
proof tool `tools/minimap-proof` (P762b adds `tools/p762b-sector-map-proof`).
Tests: new `P762*` files plus corrected native expectations in `MineCombatTests`,
`ShipsUpdateLiveTests`, `MissionUpdateTests`, `P747ConstructionTests`; Bot gained
`SendQuitMission` for the loopback moderation proof.
CSVs: only the nine P762 rows.

## Review fixes (P762b)

Base: `pi/p762b`, merged up from master (`50830ab5`) first. Both merge conflicts
took master's side: the full P758 `QuitSideReason` enum and
`ClientWorld.OwnBootMessage`; P762's `DepartureNotice.cs` and
`P762DepartureNoticeTests` were deleted. No commit/push (working tree only).

### 1. Map threat marker — chat cluster vs commander highlight (row `done`)
- **Wrong in P762:** `sectorhighlightbmp` was drawn for
  `ClientWorld.HighlightedClusters` (the commander's `HIGHLIGHT_CLUSTER`), and
  P374's flashing cyan `sectorEmpty` outline for those clusters had been
  removed. Those are two different native concepts.
- **Now:** `StarmapRenderer` draws `sectorhighlightbmp` only for the **chat
  cluster** (`trekClient.GetChatCluster()`, `sectormap.cpp:1095`), which the
  port computes as `ViewedClusterId ?? own.SectorId` in `Game1.Rendering.cs`
  (the same rule P752 used for the sector-info hover), falling back to
  `sectorselbmp` for the selected cluster. The pure, tested rule is
  `StarmapThreatPresentation.OverlayFor`/`OverlayAssetStem`
  (`src/.../StarmapThreatPresentation.cs:46,54`). The commander's
  `HIGHLIGHT_CLUSTER` set again drives P374's flashing cyan outline on the
  **off-flash phase only** (`GetHighlight() && !m_bFlashFrame`,
  `sectormap.cpp:1043-1046`) via `FlashingOutline` (`:70`), restored in both the
  full pane and the compact map.
- **Tests:** `P762bStarmapOverlayTests.Chat_cluster_gets_the_highlight_bitmap_and_selection_is_the_fallback`
  and `...Commander_highlight_flashes_cyan_only_on_the_off_flash_phase`.
- **Real-client screenshot (not the minimap harness):**
  [p762b-sector-map-overlays-720p.png](P762B_assets/p762b-sector-map-overlays-720p.png)
  (off-flash phase) plus
  [p762b-sector-map-onflash-720p.png](P762B_assets/p762b-sector-map-onflash-720p.png)
  (on-flash contrast). Reproduced with
  `dotnet run --project tools/p762b-sector-map-proof -- docs/reports/P762B_assets`,
  which boots a real ephemeral loopback server and drives the real `Game1`
  DesktopGL client over TCP; the chat cluster is set through the real
  `FM_C_VIEW_CLUSTER` request/reply and the highlight through the real
  `FM_CS_HIGHLIGHT_CLUSTER` relay.
  I opened both captures and checked them: in the off-phase shot the top-row
  **Quaoar** dot carries the distinct blue `sectorhighlightbmp` overlay (the
  chat cluster), **Borasis** has the double cyan flashing outline (commander
  highlight), and **Deucalion** carries the gold `sectorselbmp` selection
  overlay; the on-phase shot shows Borasis with only its normal cluster ring
  (the cyan outline is gone). The selected-cluster info pane reads
  “Deucalion Sector”, matching the selected id sent by the proof.

### 2. SIM-0142 FAllReady and GetRankThreshold
- **Wrong in P762:** FAllReady ended with a “every side ready” loop. In native
  that loop is **dead code**: the balance block's `return true`
  (`fsmission.cpp:4432`) precedes it (`:4435-4444`).
- **Now:** `FAllReady` (`Server.Lobby.cs:1193`) is the native balance check
  only (min/max, `min + iMaxImbalance < max`, rank spread in `0x7ffe` mode,
  inactive-empty skip). The port's readiness requirement was moved out into the
  pre-existing P180 `TryAutoStart` gate (`IsAllOccupiedSidesReady`,
  `Server.Lobby.cs:1129`), which is a deliberate, documented port policy, not
  native FAllReady. Arena-style missions (`AllowEmptyTeams`) keep the
  established occupied-side fallback around the balance check, mirroring
  native's arena AutoRestart path that bypasses FAllReady
  (`FedSrv.CPP:8185`); this restores the P762 arena/debug auto-start behaviour
  without putting the dead ready loop back into FAllReady. The AutoRestart
  warning/cancel paths (`AdvanceAutoRestarts`) also use the native
  balance-only FAllReady now.
- **GetRankThreshold:** now divides by the whole mission ship list
  (`fsmission.cpp:1405`) — peers + NPCs, flying **and docked** — not a
  flying-ship-only accessor (`Server.Lobby.cs:1248`).
- **Tests:** `P762LobbyModerationTests.FAllReady_is_the_native_balance_check_only_and_ignores_side_ready_flags`
  (balanced but all-unready mission is FAllReady-true; imbalance still blocks)
  and `...GetRankThreshold_divides_by_the_whole_mission_ship_list_including_docked_ships`
  (3 ships, ranks 100/0/0 → threshold 108; docking two does not change it).
- **Docs/rows:** SIM-0142 row description, implementation refs, test list and
  note updated; the P762 row bullets above corrected.

### 3. Mine-layer cowardly search uses native Sector.IsFriendlyCluster
- Deleted P762's private `Ship.IsFriendlyCluster` (it rejected any cluster with
  any non-allied station, even unseen). `FindLeastTargetedAsteroidAnyCluster`
  now calls master's P753 `Sector.IsFriendlyCluster(sideId)`
  (`src/Allegiance.Core/Model/Sector.cs:746`, native `common.cpp:388-431`,
  reached from `common.cpp:693`), which is territory **and** the weighted
  seen-ship balance. The drone test marks the enemy station as seen so the
  enemy cluster is hostile by the native rule; it still skips the uncontested
  enemy rock and chooses the friendly one.

### 4. Moderator identity is authenticated (security)
- `IsModerator` previously matched `ModeratorAccounts` against the
  self-asserted logon character name, CD key **and** Steam ID. This port has no
  ASGS/Steam authentication (the Steam ticket is not validated), so any client
  could claim a configured name and ban players.
- **Now:** each `ModeratorAccounts` entry is a **bearer secret** matched
  fixed-time only against `LOGONREQ.CDKey`, the same authenticated-bearer model
  P655 already uses for admin/test-harness grants (`Server.Moderation.cs:88`).
  Character names and Steam IDs are never consulted. The `///` doc on
  `ServerConfig.ModeratorAccounts` states this and tells operators to issue
  distinct random secrets out of band (a plain name is not a credential).
- **Test:** `P762LobbyModerationTests.Spoofed_character_name_and_steam_id_do_not_grant_moderator_rights`
  claims both a configured name and a configured Steam ID with a different
  CD key and is refused; presenting the configured secret succeeds.

### 5. SIM-0064 ordering, activeF gate, minor drilling radius
- The mine-attrition pass now runs **after all collision passes** (was before
  the models loop). Native order is collisions → mines → Move; this port's Move
  happens earlier (inside `Ship.OnUpdate`), so only the collision→mines half is
  native — stated explicitly in `ResolveMineDetonations`' remarks and the
  SIM-0064 row, and P762's “killed ship does not move that tick” test/claim is
  replaced.
- Added `Sector.Active` = native `activeF` (default true for mission clusters,
  `missionigc.cpp:4053`; only wire export lies false) and gated both the mine
  pass and the every-10th visibility/callback pass on `activeF && started`
  (`clusterIGC.cpp:255`). Test:
  `P762SectorTickTests.Inactive_sector_skips_mine_damage_and_the_visibility_cadence`.
- Drilling-debris offset now uses the new `Ship.Radius` (native `GetRadius()`,
  `shipIGC.cpp:1419,888-890`) instead of spelling out `0.5 * HullType.Length`
  (`Sector.cs:2662`).

### P762b verification

| Gate | Result |
|---|---|
| Whole-solution build | **0 warnings / 0 errors** |
| AOT publish (`linux-x64`, self-contained, PublishAot) | **success**, no IL2026/IL3050; log `~/allegiance-evidence/P762b/aot-publish.log` |
| New/affected Core tests (`P762LobbyModerationTests`, `P762SectorTickTests`, `P762DroneRuleTests`), 3× alone | **23/23 passed each run**; `~/allegiance-evidence/P762b/core-tests-3x-final.log` |
| Affected mine-formula tests (`MineCombatTests`), 3× alone | **14/14 passed each run**; same log |
| New MonoGame overlay/artwork tests, 3× alone | **4/4 passed each run**; `~/allegiance-evidence/P762b/monogame-tests-3x.log` |
| Fast tier (final run) | 9276 tests, 274.6 s. **One failure**: the documented load flake `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` (logon-drain order 0/1/0) — passed **3/3 alone**; every other fast test passed. Log `~/allegiance-evidence/P762b/fast-tier-final.log`, TRX under `TestResults/tiers-20261020T202016.518646Z/`. |
| Showcase never-parks, 5× | **5/5 passed** (~1m50s each); `~/allegiance-evidence/P762b/showcase-5x.log` |
| Real-client sector-map screenshot | `docs/reports/P762B_assets/` (two 720p PNGs, ~100 KB each), opened and described above |

An earlier fast-tier run (before the arena fallback was restored) failed 27
auto-start-dependent tests plus one slow test; that was a real regression from
this round, was fixed (see FAllReady above), and the affected tests plus the
final tier run then passed. Raw logs/TRX are outside the repo at
`~/allegiance-evidence/P762b/`; the only committed new evidence is the two
small PNGs under `docs/reports/P762B_assets/`.
