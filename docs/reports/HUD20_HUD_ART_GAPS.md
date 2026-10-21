# HUD-20 — remaining HUD art gaps: cloak strength, sector "Currently viewing", observer count, target kill bonus

Base: `master` at `5f4c2f82` (worktree `pi/hud20`, uncommitted working tree).
Native cites: `~/Development/Allegiance/src/`; artwork: `~/Development/Artwork/`.

**Base caveat (checked first, as the brief asks).** This worktree's `master` does **not**
contain FONT-1, HUD-17 or HUD-18: those live only on the `pi/hud17` / `pi/hud18` WIP
branches (`git branch --contains`), and no FONT-1 commit exists in this repository. So
the brief's "the numbers will have moved" was accurate for HUD-16 (which *is* merged),
and all port cites below are the re-grepped numbers in this tree. For item 2 I used the
same font call sites (`_small` / `DrawMeasuredText`) as the neighbouring
"Current location:" text, which is what FONT-1 would restyle; no FONT-1 rework was
duplicated here. HUD-18's target-pane changes are also absent, so `TargetPaneModel` is
the pre-HUD-18 shape — the kill-bonus field was added in that shape.

**Rebased by the Claude review (branch `fix/hud20b`).** The work was cherry-picked onto
current `master`, which now has FONT-1, HUD-17 and HUD-18; the review fixups are listed in
"Claude review fixups" below; the cites in the sections the fixups touched (items 2 and 4,
the matrix rows) were re-grepped on the rebased tree, other first-round cites may be off by
the rebase. Where the caveat above and the fixups disagree, the fixups win.

Verification is at the end. Raw logs: `~/allegiance-evidence/HUD20/`.

## Item 1 — cloak strength in the inventory cloak row

Native rule (`Artwork/invcloak.mdl:10-23`):

```
StatsBar = StringPane(NumberString(GetCloaking(Me)) + "% / " + CloakTimeLeft + " sec")
```

`GetCloaking` is `100·(1 − pship->GetCloaking())` for the own or parent ship only
(`WinTrek/consoledata.cpp:480-490`); `CloakTimeLeft` is `"Inf."` for −1
(`invcloak.mdl:4-9`); `NumberString` is `(int)(v+0.5)` (`engine/modeler.cpp:1996-2009`).
The GH gauge/drained art is GlassHUD-only (`dialog.mdl:570-621,651-656,999-1022,4012`)
and stays out of scope.

Port (`HudInventoryPane.cs`):

- `HudInventoryPaneBuilder.Build` takes `float? cloakStrengthPercent = null`
  (`HudInventoryPane.cs:103`), carried in the builder `Ctx`.
- The cloak stats branch (`:370-395`) now returns
  `HudFormat.FormatNativeNumber(strength) + "% / " + Endurance(endurance) + " sec"`,
  and keeps today's endurance-only text when the HUD-12
  `OwnShipCloakUpdate(220)` stream has not delivered a value (no invented 0).
- `Game1.FlightParts.cs:81-98` passes
  `OwnShipCloakUpdate.StrengthPercent(world.MyCloaking)` (the HUD-12 live value).
- The existing stats drawing (classic `HudRenderer.cs:2542`, modern
  `ModernHudRenderer.cs:466`) already renders `StatsText` with the HUD-11 fit
  scale, so both skins show it. At 1920×1080 the string `50% / Inf. sec` (and the
  longer `50% / 12 sec`) fits the native 81-px stats slot without clipping —
  screenshots below; `FitFontScale` would shrink rather than clip if needed.

Tests: `HudInventoryPaneTests.Cloak_row_prints_native_strength_percentage_and_endurance`
(37.4 → `37% / 32 sec`) and `..._without_a_strength_update_keeps_the_endurance_only_text`.

## Item 2 — sector location: "Currently viewing:" in docked command view

Native (`Artwork/dialog.mdl:1429-1471,5420-5422`; `WinTrek.cpp:6831-6840`):

- `LookingAtImage` = yellow `promptFont` "Currently viewing:" plus
  `CurrentViewSectorNameImage` (`medVerdana`, `Color(1,0,0)`, centred 160 wide,
  translated `Point(0,-14)`), drawn at `SideTopRight(-180,-37)` only with
  `ofInStationCommand` (docked command view).
- `SectorName` is `trekClient.GetCluster()->GetName()` (`console.cpp:868-872`), and
  `GetCluster()` is the view cluster when set, otherwise the ship's cluster
  (`clintlib/clintlib.h:996`).

Port:

- `HudConsoleText.ViewedSectorName` (`HudConsoleText.cs:27`) plus a pure
  `ViewedSectorId(viewed, station)` (`:133`) and `LookingAt(name)` (`:139`,
  returns the label, name and native `Color(1,0,0)`).
- `Game1.BuildConsoleText` (`Game1.Rendering.cs:2966-2975,3011`) resolves
  `ViewedClusterId ?? station cluster` while docked — the same rule as
  `Game1.Commanding.cs` `RenderSector` (`ClientWorld.Commanding.cs:10`).
- Classic `HudRenderer.cs:783-810` and modern `ModernHudRenderer.cs:590-616` draw the
  block at the existing `SectorLabelTop` / `NameBelowLabel` anchor when
  `model.Visible && Context.Docked && Context.CommandView`. The in-flight
  (`ofInFlight`) "Current location:" branch is unchanged and still comes first.
- `HudElements` "Sector location" visibility now covers both states
  (`HudElements.cs:295-305`).

Hangar/loadout variant (`dialog.mdl:1449-1499,5420`): **not drawn; covered by the
hangar UI.** The real-client docked (non-command) screenshots show the hangar screen
replacing the flight HUD entirely — no top-right console block in either skin — so
there is no host layer for `LocationInHangarImage`. Left unchanged, as the brief allows.

Tests: `Hud20ArtGapsTests` — the viewed-sector fallback rule, the red label block, the
`HudElements` visibility/`Text` in flight vs docked command vs docked hangar, and the
shared `HudConsoleFormat.DockedLookingAt` gate (visible + docked + command view, never in
flight) that both skins and the HUD-11 anchor reservation call. The Classic font choice
(FONT-1 `promptFont` / `medVerdana`) needs a GraphicsDevice and is checked by the
screenshots, not a unit test.

## Item 3 — observer count row (and the observer turret-id bug)

Native:

- `ProcessObservers` counts the source ship's children with `GetTurretID() == NA` and
  inserts one `invobserver.mdl` row only when the count is > 0, **after** the fixed
  weapon rows and **before** the turret rows
  (`WinTrek/inventoryimage.cpp:141-169,320-332`; source ship `:290`).
- `invobserver.mdl:1-12`: white `"Observers: " + NumberString(n)` at `Point(23,5)`,
  no icon, no key.
- The child's mount comes from the carrier's loadout passenger list
  (`pshipChild->SetTurretID(ppd->turretID)`, `clintlib/appmsg.cpp:2059,2073`), and
  `turretID` is a `Mount` whose `NA` = −1 = 0xFF on the wire.

**Bug found and fixed (brief item 3a).** The server encoded
`new PassengerData(c.Id, (byte)(c.TurretId ?? 0))` (`Server.Combat.cs:499` before), so
every observer went out as **mount 0** — indistinguishable from a gunner. It now uses
`PassengerRecord` (`Server.Combat.cs:451-457`): `(byte)(child.TurretId ?? -1)` = 0xFF
for an observer. No existing test pinned 0.

Port:

- `ShipEntity.BoardTurretId` (`ShipEntity.cs:255-268`, −1 unknown, 0xFF observer) is
  stored by the LOADOUT_CHANGE passenger loop (`ClientWorld.cs:3871`) and cleared
  wherever `ParentId` is reset: `ClientWorld.cs:2576,2823,2839,4430,4449,4461`,
  `ClientWorld.Interface.cs:99`, `ClientWorld.FlightInterface.cs:70`,
  `ClientWorld.Commanding.cs:45`. A SHIP_STATUS with no parent also clears it
  (`ClientWorld.cs:2739-2743`).
- `Game1.FlightParts.cs:88-98` counts children of the source ship (own id, or
  `ParentId` when boarded) whose `BoardTurretId == 0xFF` and passes the count into the
  builder.
- `HudInventoryPaneBuilder` inserts a `HudInventoryRowKind.Observers` row with
  `"Observers: N"` (`HudInventoryPane.cs:127-133`); both skins draw it in white at the
  native key-column x with no icon (`HudRenderer.cs:2465-2479`,
  `ModernHudRenderer.cs:428-435`).
- `HudConsoleText.BoardState` is untouched.

Tests: a server test for the 0xFF gunner/observer encoding, a ClientWorld test for the
passenger turret id and its clearing on unboard, and builder tests for row order
("Observers: 2" between fixed weapons and turrets) and 0 observers → no row.

**Screenshot (c) not produced.** A second local ship was boarded as a real observer
(`PerformBoard` on a docked, turret-less Scout carrier → `turret NA`, server log confirms
`LOADOUT_CHANGE … 1 passenger`, and the client played the native boarded cue). The row
did not appear because the only spare ship was a synthetic NPC whose own
`SHIP_STATUS` (parent id unknown) cleared the client's parent link before the shot —
the client logged `parent=-1 boardTurret=-1`. A real observer is parented and excluded
from ship telemetry (`Sector.cs:604-630` filters `ParentShip is not null`), so this is a
limitation of the synthetic fixture, not of the row rule. The tests above are the
evidence, per the brief.

## Item 4 — target kill bonus (native PLAYERINFO + KILL_SHIP, **no new fedmsg**)

Native mechanism:

| Step | Native |
| --- | --- |
| `fExperience` seed | `SendPlayerInfo` writes `GetIGCShip()->GetExperience()` (`FedSrv/FedSrv.CPP:2084`); field at `Inc/Messages.h:109` |
| recipients | whole mission: no target player → `pfsMission->GetGroupMission()` (`FedSrv.CPP:2129-2134`); joiner receives one per ship (`FedSrv/fsmission.cpp:3958-3968`); new drones announced the same way (`:411`, `FedSrv.CPP:2269`) |
| `dtidDrone` | `NA` for a human, the drone type id for a drone (`FedSrv.CPP:2088-2091`) |
| client seed | `pship->SetExperience(pfmPlayerInfo->fExperience)` (`appmsg.cpp:2280`) |
| death | victim `SetExperience(0.0f)` on `bDeathCredit` (`appmsg.cpp:2769`) |
| kill | `pLauncherInfo->AddKill()` then `AddExperience()` only for cross-side, non-allied (`appmsg.cpp:2807-2811`; `+= 1.0f`, `shipIGC.h:1827-1830`) |
| readout | `TargetExperience = 100·(GetExperienceMultiplier()−1) = 50·e/(e+4)` (`consoledata.cpp:831-843`, `shipIGC.h:1812-1818`); black number + `targetexperiencebmp` at pane-local (280,110) / bitmap (+25,−21), ships only (`dialog.mdl:4192-4214,4232`) |

Port changes:

- **(a) Encoder.** `PlayerInfo.Encode` writes `FExperience` at offset 48 and the real
  `DtidDrone` at offset 58 (`Core/Wire/PlayerInfo.cs:197,203`); the decoder already
  read both. Layout unchanged (68 bytes).
- **(b)+(c) Server fill and recipients.** One native-shape builder
  (`Server.Broadcast.cs:1909-1937`, `BuildPlayerInfo`) now fills `FExperience =
  ship.KillBonus`, the ship's real `PilotType`, and `DtidDrone` (NA for a human, the
  drone type id for an NPC — 0 only when the port's `SpawnNpcShip(droneTypeId: null)`
  fallback has no type, because NA would falsely mean "human"). It is used by the two
  teammate sends (`Server.Broadcast.cs:965,1526`) and both lobby sends
  (`Server.Lobby.cs:3625,3938`). **Every seated peer gets one PLAYERINFO for every
  mission ship**, enemies and drones included, once per (recipient, ship), **right
  after that pair's EXPORT**: `SeedMissionPlayerInfo` (`Server.Broadcast.cs:1955`) is
  called at the moment the pair's export latch is first set — the cross-side export in
  `SweepCrossSideExports` (`:1884`) and the same-side NPC export in
  `BroadcastShipStates` (serial `:1146`, parallel `:1629-1636` via
  `BroadcastPeerBuild.MissionPlayerInfoSent`). Teammates keep the inline P63 send and
  are skipped (`_teammatePlayerInfoSent`), so no ship is seeded twice. There is no
  per-tick sweep (see the review fixups for the before/after cost). It never re-sends,
  not even on the P369 identity re-export or a hull-change relaunch (native never
  re-seeds experience; `FM_S_SET_EXPERIENCE` is a commented-out client handler,
  `appmsg.cpp:2918-2928`). The own ship keeps its join-burst send. The one-shot latch
  `_missionPlayerInfoSent` is cleared with the teammate latches (`Server.Lifecycle.cs`
  ReleasePeer, `Server.Lobby.cs` ClearPeerJoinLatches — which also covers the game-over
  rotation, every migrated peer is released there) and pruned when a drone dies
  (`Server.Combat.cs:1821`).
- **(d) Client seed.** `Apply(PlayerInfo)` sets `ShipEntity.Experience =
  info.FExperience` (`ClientWorld.cs:3735`).
- **(e) KILL_SHIP.** Death credit zeroes the victim's `Experience`
  (`ClientWorld.cs:4218`); kill credit with `TypeCredit == OT_ship`, an existing killer,
  a different side and not allied adds +1 (`:4261`), with a comment tying it to the
  server's `AddKillBonus` gate (`Server.Combat.cs:1686-1689`). Ejects keep the value.
- **(f) Target pane.** `TargetPaneModel.TargetExperience(e) = 50e/(e+4)`
  (`TargetPaneModel.cs:194`); `KillBonusPercent` is that value for `IgcObjectType.Ship`
  (0 when the seed has not landed) and `null` for every non-ship
  (`:183`), matching the native switch.
- **Draw.** `TargetPaneTextures` loads the real `TargetExperiencebmp.mdl` (capital T;
  Linux is case-sensitive) once (`TargetPaneTextures.cs:23-29,90`), and
  `packaging/artwork-manifest.txt` now lists it. Classic draws the bitmap with its
  lower-left at pane-local (305,89) and the black FONT-1 `medBoldVerdana` number centred
  in the 80-wide box (`HudRenderer.cs:2345-2363`, layout `TargetPaneLayout.cs:25-48`),
  marking `ElementDrawn("Target kill bonus")`. Modern draws `KB n%` at the same anchor
  (`ModernHudRenderer.cs:349-359`).
  *Anchor:* a native `StringImage` hangs **below** its origin — bounds
  `Rect(0,-ysize,w,0)` and each line drawn at `y -= size.Y` (`engine/image.cpp:336-388`)
  — so in the y-up pane space Point(280,110) is the number's **top**; the 13-px cell
  spans local 97..110, inside the 32×32 medal (89..121). With FONT-1's `medBoldVerdana`
  the port now uses exactly that anchor (`TargetPaneLayout.ExperienceNumberTopLeft`);
  the first round's substitute-font centring workaround is gone (review fixup 4).
  *Note (FONT-1c, 2026-10-21):* the "13-px cell" above is the NATIVE medBoldVerdana cell.
  FONT-1b later moved the DejaVu substitute from Size 8 to Size 9 (achieved em 12.0 px,
  ≈14 px line cell), which still fits inside the medal's local 89..121 span; this note
  records the change instead of rewriting the original measurement.

## Item 5 — the rig bot driver (required by item 4)

Native tells humans from drones by `dtidDrone` (`NA` = human, `FedSrv.CPP:2088-2091`).
The port now encodes/decodes it (above), and the rig no longer uses `Player is null`:

- `BotManager.IsDrone(ShipEntity)` (`tools/allegiance-mcp/BotManager.cs:1386`) =
  `Player is not { DtidDrone: NA }` (a not-yet-seeded ship still counts as a drone,
  preserving today's rig behaviour).
- The three old checks (`FindNewDrone`, `ListDrones`, `CommanderSeeing`) use it
  (`:1365,1498,1521`).
- The same stale discriminator existed in production and was fixed too:
  `LeaderBotCommandResolver` (`ClientCore/State/LeaderBotCommand.cs:40`) and the
  moderator `/boot` target check (`Rendering/ModerationModel.cs:59`) now use
  `DtidDrone == NA` for a human. Without this, the leader bot command and boot both
  silently disappeared once drones got a PLAYERINFO (caught by the live
  `LeaderSameSideBotClientLiveTests` and `P722InterfaceModelTests` fixtures, which were
  updated to seed the native `DtidDrone` on their human players).

Tests: `Hud20DroneDetectionTests` does a full PLAYERINFO encode → decode → `Apply` loop
for one human (`dtidDrone = NA`) and one drone (`40`) and asserts `IsDrone` classifies
each correctly; the updated live leader test proves a drone that now *has* a PLAYERINFO
is still offered the leader dock/launch command.

## Test fail-on-old-code proof

Production files were backed up, reverted to the old behaviour (keeping the new test
APIs so every test still compiled), the new tests run, then the production files
restored. Observed:

| Reverted production behaviour | Failing tests (old behaviour) |
| --- | --- |
| `PlayerInfo.Encode` writes `0f` / `0` for experience / dtidDrone | `Hud20PlayerInfoWireTests.Encode_writes_fExperience_at_offset_48_and_round_trips`, `..._round_trips_the_drone_type_id_for_a_drone` |
| no PLAYERINFO seed with the export (review rework: `SeedMissionPlayerInfo`) | `Hud20PlayerInfoSeedTests.Enemy_and_enemy_drone_playerinfo_follows_their_first_cross_side_export_once`, `Same_side_drone_playerinfo_follows_its_npc_export_once` (these replace the first round's sweep test; not re-run against reverted code in the review) |
| `PassengerRecord` `?? 0` | `Hud20ObserverIdTests.Observer_encodes_0xFF_and_a_gunner_its_mount` |
| no `e.Experience = info.FExperience` / no KILL_SHIP experience step | `ClientWorldHud20Tests.PlayerInfo_seeds_the_ship_experience`, `Eject_without_death_credit_keeps_the_victim_experience`, `Cross_side_non_allied_kill_credit_adds_one_to_the_killer`, `Same_side_kill_credit_adds_nothing`, `Allied_sides_earn_no_experience_and_non_credited_kills_add_nothing` |
| MCP `IsDrone` = `Player is null` | `Hud20DroneDetectionTests.Drone_and_human_both_with_playerinfo_are_classified_by_dtidDrone` |
| cloak strength ignored / no observers row | `HudInventoryPaneTests.Cloak_row_prints_native_strength_percentage_and_endurance`, `Observers_row_sits_between_fixed_weapons_and_turrets` |
| `ViewedSectorId` ignores the view cluster / wrong colour / null kill bonus | `Hud20ArtGapsTests.Viewed_sector_uses_the_view_cluster_then_the_station_cluster`, `Looking_at_is_the_yellow_label_and_the_red_viewed_sector_name`, `Target_pane_reports_the_kill_bonus_for_a_ship_and_null_for_a_station` |

Every test listed failed at **runtime** (not a compile error) against the reverted
behaviour; 5 Core, 6 ClientCore and 5 MonoGame failures respectively. Two tests are
intentionally not in that list. `KillShip_with_death_credit_zeroes_the_victim` seeds the
victim with **3** through PLAYERINFO, but with the seed reverted too the victim's
experience never leaves 0, so the "zeroed" assertion already holds. The other was a
source-text pin of the draw calls. The review replaced it with behaviour tests on the
shared seams (`Docked_looking_at_is_drawn_only_when_visible_docked_and_in_command_view`,
`Kill_bonus_number_sits_on_the_medal_at_the_native_anchor`,
`Target_experience_art_resolves_with_the_native_file_name`).

## Screenshots (real client, 1920×1080, both skins)

Driver: `tools/hud20-proof` — the real `Game1` over TCP against a real local server
(`m2xspiderw.igc`), real EXPORT/PLAYERINFO/LOADOUT_CHANGE traffic, real hangar Command
verb and `VIEW_CLUSTER` request, real launch, a 3-kill enemy dummy and a station target.
Each screenshot was opened with the read tool and is described here.

**Classic**

- `HUD20_assets/hud20-a-docked-hangar-classic.jpg` (skin-independent: the first round's
  Modern capture was byte-identical, so it was dropped) — the docked loadout screen fills the
  view (station loadout slots, hull stats, `Launch/Buy/Command/…` bar). The top-right
  corner is empty: the flight HUD console layer is replaced by the hangar UI, so the
  native hangar "Current location:" variant has no host layer (item 2 hangar decision).
- `HUD20_assets/hud20-a-inventory-cloak-classic.jpg` +
  `hud20-zoom-cloak-classic.jpg` — the F4 inventory pane on a `Lxy Stl Fighter` shows
  `8. Lt Cloak   50% / Inf. sec`: the native leading strength percentage, the endurance,
  and no clipping. The centre HUD separately shows `CLOAK 50%` (the port's HUD-12
  readout), which is unchanged.
- `HUD20_assets/hud20-b-currently-viewing-classic.jpg` — docked command view after a
  `VIEW_CLUSTER` request for sector 1051. Top-right: yellow **`Currently viewing:`** and
  **`Organia`** in red, at the same anchor as the in-flight location block (review
  re-capture: bold `promptFont` label, regular `medVerdana` name, the FONT-1 fonts). The starmap,
  command bar and minimap are visible; the station's own sector is `Ganymede`
  (`YOU: Ganymede`), proving the view cluster is shown, not the station cluster.
- `HUD20_assets/hud20-d-target-killbonus-classic.jpg` + `hud20-zoom-killbonus-classic.jpg`
  — target pane on enemy `Ship 5476: Lxy Scout` (review re-capture): the native medal
  art with a black bold **`21`** on it at the native anchor (text top at local 110). The client's enemy entity logged `experience=3`, so
  `50·3/7 = 21.4 → 21` came through the new PLAYERINFO seed.
- `HUD20_assets/hud20-d-target-station-classic.jpg` — target pane on `Garrison:
  Garrison`: no medal and no number (native hides non-ship readouts).

**Modern**

- Docked hangar: no separate Modern capture. The first round's `hud20-a-docked-hangar-modern.jpg`
  was byte-identical to the Classic one (the hangar screen renderer is skin-independent),
  so it was dropped.
- `hud20-a-inventory-cloak-modern.jpg` + `hud20-zoom-cloak-modern.jpg` — the HUD-2 style
  inventory panel shows `8. Lt Cloak   50% / Inf. sec` with the same no-clip fit.
- `hud20-b-currently-viewing-modern.jpg` — yellow `Currently viewing:` / red `Organia`
  in the same docked command view.
- `hud20-d-target-killbonus-modern.jpg` — `Ship 5476: Lxy Scout` with **`KB 21%`** under
  the name, at the native experience anchor.
- `hud20-d-target-station-modern.jpg` — `Garrison: Garrison`, no KB readout.

## Verification

- Whole solution build (no-incremental): **0 warnings / 0 errors**.
- AOT publish (`Allegiance.Server`, linux-x64, self-contained, `PublishAot`): **succeeds,
  no IL2026/IL3050** (`~/allegiance-evidence/HUD20/aot-publish-2.log`).
- New/affected tests 3× alone: Core `Hud20*` 5/5 ×3; ClientCore `Hud20*` 9/9 ×3;
  MonoGame `Hud20* + HudInventoryPaneTests` 29/29 ×3; `LeaderSameSideBotClientLiveTests`
  1/1 ×3; `LeaderBotCommandTests` 11/11 ×3; `P722InterfaceModelTests` 7/7 ×3.
- Fast tier (`tools/run-tests.sh --tier fast`): **PASS**, 9661 tests
  (`~/allegiance-evidence/HUD20/fast-tier-summary.json`). Three earlier attempts each
  had a single unrelated failure that passes 3/3 alone: the first a stale discriminator
  this task fixes (Leader/P722, fixed), then one allocation-measurement flake
  (`Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact`,
  1488 B once under load, 0 B 3/3 alone — `docs/TEST_FLAKES.md`).
- Showcase never-parks 5× alone: **5/5 pass**. Last run's metric: 30 bots, 1096 samples,
  `longest speed<1 with target = 1.91 s` (threshold 5 s; `IdleWithTarget.CurrentCount =
  0`) — the base rate (a pass) is unchanged.

## Matrix

Edited as text (LF, 6 columns, no csv.writer): "Cloak strength and drained state"
(`partial`, rewritten note, stale `HudRenderer.cs:990` cite removed), "Equipment cloak
endurance" (`done`), "Sector location" (`done`), "Observer and turret board state"
(`partial`, observer count drawn + wire id fixed, loadout-only `BoardTextImage` /
`InTurretPaneImage` still absent), "Target kill bonus" (`done`, native mechanism
replacing the "not on the wire" claim). No other row touched.

## Unfinished / decisions

- Live observer-row screenshot (item 3c) not produced; see the finding above. The row,
  the wire id and the count rule are pinned by tests.
- The hangar "Current location:" variant is left undrawn because the docked hangar
  screen replaces the flight HUD (screenshots in both skins).
- The target-pane number now uses the native anchor (top at pane-local 110) and FONT-1
  `medBoldVerdana`; the first round's centring workaround is gone.
- `DtidDrone` for a port NPC with no drone type (the `SpawnNpcShip(droneTypeId: null)`
  fallback) is encoded as 0 (stock/unknown drone), never NA, so the client and rig still
  classify it as a drone. Native always has a real type id.
- FONT-1 is now in the base: the Classic docked "Currently viewing:" block uses the same
  FONT-1 fonts as the in-flight "Current location:" block (review fixup 3).

## Claude review fixups (branch `fix/hud20b`)

1. **Teammates got PLAYERINFO twice (MAJOR).** The first round's sweep checked
   `_teammateExportSent` but never `_teammatePlayerInfoSent`, so a teammate pair was
   seeded by both the inline P63 send and the sweep. It only went unnoticed because
   `ShipStatusServerTests.Teammate_ship_playerinfo_is_broadcast_once_after_export_before_status`
   never started its mission. Fixed by the rework in 2: teammates never reach the
   seed, and `SeedMissionPlayerInfo` also returns early when `_teammatePlayerInfoSent`
   holds the pair. That test now sets `Stage = Started`. A new test,
   `Hud20PlayerInfoSeedTests.Two_same_side_peers_in_a_started_mission_get_exactly_one_playerinfo_each`
   (serial and parallel, 40 ticks of the real `crossSideExports` + `shipStates` steps, plus
   an enemy peer), checks exactly one PLAYERINFO per pair.
2. **Per-tick sweep cost (MAJOR perf).** The sweep is gone. The seed is sent at the
   moment each EXPORT latch is first set (cross-side, same-side NPC serial and parallel;
   teammates keep the inline send). The killer's kills/deaths are looked up at most once
   per ship per sweep, and only when that ship has a new pair. Release micro-benchmark,
   400 peers × 400 ships (200 per side, one sector), steady state with every pair
   already exported, 200 ticks, 3 runs:

   | | before (sweep) | after |
   | --- | --- | --- |
   | `missionPlayerInfoSweep` step per tick | **4.24–4.28 ms, 80 B/tick** | step removed: **0 ms, 0 B** |
   | `SweepCrossSideExports` per tick (unchanged work) | 1.57–1.58 ms, 11,560 B (seed disabled) | 1.61–1.67 ms, 11,560 B (no new allocation) |
   | first mass-join sweep (80,000 new pairs) | 190–231 ms (EXPORT only) | 249–266 ms (EXPORT + 80,000 PLAYERINFO) |

   The steady-state cost is now O(new pairs). The ~0.05 ms gap in the cross-side row is
   within run-to-run noise: the only per-tick addition is one nullable local per ship.
   The extra one-off join cost is the PLAYERINFO encodes themselves, which the old sweep
   also paid. `_missionPlayerInfoSent` is pruned when a drone dies
   (`Server.Combat.cs:1821`; ship ids wrap in `AllocateShipId`). It is pruned per peer on
   departure and on the game-over rotation by the existing `ReleasePeer` /
   `ClearPeerJoinLatches`.
3. **Docked "Currently viewing:" fonts.** Classic now uses the FONT-1 `promptFont` label and
   `medVerdana` name, the same fonts as the in-flight block, so the "Sector location"
   `done` holds. Both skins and the HUD-11 `ReserveConsoleAnchors` share one gate,
   `HudConsoleFormat.DockedLookingAt(visible, flightVisible, docked, commandView, name)`.
   That gate gives Modern the `model.Visible` check and gives the block an anchor
   reservation like the in-flight one.
4. **Kill-bonus placement.** The number uses the native anchor and FONT-1
   `medBoldVerdana`; see the "Anchor" note under item 4. FONT-1 sizes are unchanged.
   *Note (FONT-1c, 2026-10-21):* "unchanged" was true at this merge, but FONT-1b then moved
   `medBoldVerdana` from Size 8 to Size 9 (em 12.0, ≈14 px cell) for the measured +16 px
   width fit; it is still inside the 89..121 medal span. The target-pane anchor and number
   box were not changed by FONT-1b/FONT-1c.
5. **Source-text tests.** These were replaced with behaviour tests on pure seams:
   `DockedLookingAt`, `TargetPaneLayout.ExperienceArtBottomLeft`/`ExperienceNumberTopLeft`,
   and an Artwork-gated decode of `TargetExperiencebmp.mdl` (32×32). Fonts and the actual
   draw calls need a GraphicsDevice, so the re-captured screenshots cover them instead.
6. **Report.** Fixed the `KillShip_with_death_credit_zeroes_the_victim` description (it
   seeds 3). Dropped the byte-identical Modern hangar screenshot. Corrected the matrix seed
   cite (`ClientWorld.cs:3735`). Re-captured the kill-bonus and currently-viewing
   screenshots in both skins with `tools/hud20-proof`.
7. **Formatting.** Split the two joined lines in `HudConsoleText.cs` and
   `TargetPaneLayout.cs`.

Also found on the rebase: the Classic observer row drew with the mono `_small` font,
which FONT-1's `Font1NativeFontTests` forbids. It now uses the FONT-1 row font
(`smallFont`, the StringPane default, at the pane scale), like every other inventory row.

Unchanged by design: the `dtidDrone` encoding (`FedSrv.CPP:2088-2091`), the
allegiance-mcp `IsDrone` change, and the `fExperience` wire layout.
