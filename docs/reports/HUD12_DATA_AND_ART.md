# HUD-12 — HUD rows blocked on data or native art

Base: master at dispatch `be1d36fd` (SC-7 fix, after HUD-10 `73296846`).
Worktree/branch: `pi/hud12`. Model: DeepSeek V4.1 Flash. No commits or pushes;
all changes are in the working tree as instructed.

Native citations use the checked-out tree at `~/Development/Allegiance/src/`
and the artwork at `~/Development/Artwork/`. `dialog.mdl` points are its own
lower-left/+Y-up coordinates; the port converts them in the layout classes.

## Track coordination

`git branch --list "pi/*" --no-merged master` lists the older per-task branches,
but `git diff master <branch> -- docs/parity/parity-matrix.csv` shows none of
them adds a newer claim on these eleven rows — their CSV lines are the older
HUD-3/4/5/6 snapshots that master already supersedes. `pi/hud11` (layout
cleanup, WIP) does touch shared HUD files (`HudRenderer.cs`, `HudModel.cs`,
`HudConsoleText.cs`, `ModernHudRenderer.cs`, `HudElements.cs`) but carries no
parity-row edits; this wave's additions are separate append points in those
files and will need a normal merge when HUD-11 lands. No rows were skipped.

## Rows

### 1. Radar carried flag marker — partial

- **Native:** `WinTrek/radarimage.cpp:219-230` loads `hasflagbmp`
  (`WinTrek/artwork.h:75`); `:643-645` sets `c_maskFlag` when
  `pmodel->GetFlag() != NA` and is not the `SIDE_TEAMLOBBY` artifact sentinel;
  `:343-345` draws it with `DrawExpandedBlip(..., m_psurfaceFlag, colorOther)`;
  `:255-277` is the quadrant-expansion technique (delta = radius/√2, floored at
  half the bitmap's horizontal midpoint).
- **Port:** `StatusTextures` decodes the real `hasflagbmp.mdl`;
  `RadarBracketRenderer.DrawExpandedBlip` reproduces the four-quadrant
  placement (`ExpandedBlipDelta`/`ExpandedBlipQuadrant`); the radar snapshot
  fills `CarriedFlag` through `RadarOverlayRules.CarriedFlag`
  (`Game1.Rendering.cs:3433`). Fallback pennant remains only when the art is
  missing.
- **Test:** `Hud12DataAndArtTests.Expanded_blip_quadrants_match_native_geometry`,
  `...Carried_flag_and_artifact_masks_use_the_native_GetFlag_sentinels`.
- **HUD-12b:** the row is **partial**, not done. `DrawExpandedBlip` is fed the
  port's fixed overlay bracket radius; native derives the quadrant delta from
  each contact's projected `GetScreenRadius()` (`radarimage.cpp:255-277,343-345`),
  which this projected-snapshot renderer does not carry. Same limitation as
  the other bracket rows on master.
- **Screenshot:** `radar-native-markers.webp` — ships 8097/8098 carry the two
  native quadrant glyph sets instead of the old procedural pennants (at 1080p
  the two gold glyph sets are still hard to tell apart; see HUD-12b below).

### 2. Radar carried artifact marker — partial

- **Native:** same constructor (`radarimage.cpp:225-226`), mask at `:643-645`
  (`GetFlag() == SIDE_TEAMLOBBY (-2)`), draw at `:346-347` with
  `m_psurfaceArtifact`.
- **Port:** `hasartifactbmp.mdl` decoded and drawn by the same native
  quadrant path; `RadarOverlayRules.CarriedArtifact` is the mask used by the
  snapshot (`Game1.Rendering.cs:3434`).
- **Test:** the two tests above; the artifact sentinel is asserted explicitly.
- **HUD-12b:** **partial** for the same fixed-bracket-radius reason as row 1
  (native `radarimage.cpp:255-277,346-347` sizes the quadrants from the contact's
  screen radius).
- **Screenshot:** `radar-native-markers.webp` — 8098 shows the artifact quadrant
  art (different glyph shape from 8097's flag art).

### 3. Radar command subjects — partial

- **Native:** `radarimage.cpp:570-574` assigns `psubjects` only when
  `!bRangeLabels` (command camera) from
  `GetWindow()->GetConsoleImage()->GetSubjects()`; `:651` sets `c_maskSubject`
  for a ship in that list; `:341-342` draws `subjectbmp`.
  `GetSubjects` returns the console's chat-subject list, non-null only while a
  chat is being composed (`WinTrek/console.cpp:2327-2334`), filled by
  `SetRecipient` (`console.cpp:269-455`).
- **Port:** `RadarOverlayRules.SubjectsApply`/`IsSubject` implement the native
  per-target rules (Team/Allies → own side, Everyone → all, Wing → matching wing,
  FriendlySector → own side in the sector, Individual/Group not modelled); the
  snapshot fills `Subject` for every contact while the port composer is active
  in command view (`Game1.Rendering.cs:3332,3442-3445`); the real
  `subjectbmp.mdl` is drawn by `DrawExpandedBlip`.
- **Remaining difference:** the port composer has no individual/group recipient
  selection (Tab cycles Wing/FriendlySector/Team/Everyone only), so those two
  native lists contribute no subjects; the bracket radius is the port's fixed
  overlay radius rather than native `pts->GetScreenRadius()`.
- **Test:** `Hud12DataAndArtTests.Command_subjects_follow_the_console_chat_recipient_rules`.
- **Screenshot:** `radar-subjects.webp` — `[TEAM]` composing and all three
  friendly ships wrapped by the large native subject quadrant frames; the same
  frame without composing (`radar-native-markers.webp`) has no subject frames.

### 4. Radar ripcord marker — done

- **Native:** `radarimage.cpp:999-1005` sets `c_maskRip` when
  `(pship->GetStateM() & droneRipMaskIGC) != 0`, the ship is on the local side,
  and `GetPilotType() < c_ptPlayer`; `:348-350` draws `ripiconbmp` in
  `colorIcon` (not the dimmed `colorOther`). `droneRipMaskIGC = 1024·coastButtonIGC
  = 0x1000` (`Igc/igc.h:821`).
- **Port:** StateM already reaches the client in the 47-byte
  `ServerShipUpdate` (stateM at offset 2), so
  `RadarOverlayRules.DroneRipcord` uses the real wire mask + side +
  `PilotTypeOf` (no PLAYERINFO = server-spawned NPC, pilot type below player);
  the snapshot fills `Ripcord` (`Game1.Rendering.cs:3449-3450`) and the real
  `ripiconbmp.mdl` (PNG-backed) is drawn full-brightness.
- **Test:** `...Drone_ripcord_needs_the_state_bit_same_side_and_a_non_player_pilot`.
- **Screenshot:** `radar-native-markers.webp` — 8099 carries the native rip
  quadrants with its `29s` label.

### 5. Radar friendly ripcord seconds — done

- **Native:** `radarimage.cpp:1015-1030` — a different same-side ship with
  `fRipcordActive()` gets `(int)GetRipcordTimeLeft() + 1`, capped 99999. Remote
  clients seed that countdown on the sector-wide `RIPCORD_ACTIVATE(185)` with
  `ResetRipcordTimeLeft()` (`clintlib/appmsg.cpp:953-970`), whose value is the
  hull `ripcordSpeed / c_gaRipcordTime` (`Igc/shipIGC.cpp:4665`).
- **Port:** `ClientWorld.Apply(RipcordActivate)` now records every remote
  ship's activation local time (`ClientWorld.cs:2640-2652`), cleared on abort,
  death, delete and mission reset. The radar computes the seconds with the
  ship's own hull `RipcordSpeed` and side `c_gaRipcordTime`
  (`Game1.Rendering.cs:3455-3460`, `RipcordDurationSecondsFor`), matching the
  native remote handler. `RadarLabelCache` already renders the `Ns` suffix.
- **Test:** `...Friendly_ripcord_seconds_are_int_left_plus_one_and_cap_at_99999`
  pins the seconds rule. HUD-12b adds the missing data-path test
  `Hud12bReviewFixTests.Remote_ripcord_seconds_start_on_activate_and_clear_on_abort_kill_and_delete`
  (`RIPCORD_ACTIVATE` seeds the start time; `RIPCORD_ABORTED`, `KILL_SHIP` and
  `SHIP_DELETE` clear it), so the row's `done` is now backed end to end.
- **Screenshot:** `radar-native-markers.webp` / `radar-subjects.webp` — 8099
  shows `29s` (then `28s` as it counts down), from the real `RIPCORD_ACTIVATE`
  the proof raises on the local server.

### 6. Radar threat damage flash — done

- **Native:** `radarimage.cpp:660-671` — a model's
  `GetDamageBuckets()` list (non-empty) sets `c_maskThreat`; `db->flash(now)`
  adds `c_maskFlash`; `:682-684` turns a flashing contact red; `:356-357`
  draws `lastfiredbmp`. The bucket window/flash is `igc.h:4926-5057`
  (8 × 5 s slots, 250 ms bright/dim cycle; bucket total < 0.5 is dropped).
  In native only the local ship owns a `DamageTrack` (`trekigc.cpp:2505`), so
  these buckets are "models that recently damaged me".
- **Port:** The port's `VICTIM_DAMAGED(217)` unicast already feeds
  `HostileDamageTracker` (P623); HUD-12 added `Threat` (total ≥ 0.5) and
  `Flash` (the same 250 ms anchor update as `DamageBucket::ApplyDamage`) and
  the radar snapshot fills `Threat`/`ThreatFlash` (`Game1.Rendering.cs:3446-3447`).
  The real `lastfiredbmp.mdl` is drawn and the contact/label colour goes red.
- **Test:** `...Damage_bucket_threat_lives_for_the_40s_window_and_flashes_250ms`,
  `...Damage_bucket_flash_cycles_while_damage_keeps_arriving`.
- **Screenshot:** `radar-threat-flash-2.webp` — enemy 8100's label, range and
  contact are red, the native last-fired quadrants are around it, and the HUD
  damage wash is lit after a real `VICTIM_DAMAGED` attribution.

### 7. Lives remaining — partial

- **Native:** `WinTrek/console.cpp:812-817`:
  `iLives − ppi->MissionDeaths()`, badge shown only while the result is < 10;
  `dialog.mdl:5056` (`livesbmp`) with the centred `<n> lives` text at
  `dialog.mdl:5070-5080`, anchored `SideTopLeft(45,-97)` (`dialog.mdl:5442`).
  `MissionParams::iLives` is `igc.h:1185`; unlimited default `0x7fff`
  (`igc.h:1007,1291`).
- **Port:** `MISSIONDEF(38)` now exposes the existing MissionParams slot at
  absolute offset 208 (`MissionDef.ILives`, `MissionDef.cs:68,113,169`), and
  `Mission.Lives` carries the native default. `PLAYERINFO(10)` carries real
  mission deaths (`PlayerInfo.NDeaths`, now filled from `PlayerScore` at
  `Server.Broadcast.cs`/`Server.Lobby.cs`, native `FedSrv.CPP:2085-2086`), and
  `ClientWorld.Apply(KillShip)` applies `AddDeath` exactly where native does
  (`appmsg.cpp:2766-2773`). `BuildConsoleText` computes
  `iLives − deaths` (`Game1.Rendering.cs:2959`) and `HudRenderer` draws the
  real `livesbmp` badge with the native gate `lives < 10`.
- **Remaining difference:** the port has no lives-limited elimination policy —
  a player always respawns — so production missions keep the native unlimited
  sentinel and the badge stays hidden. A mission owner can set `Mission.Lives`
  finite (the proof does: 5) and the presentation is native, but nothing
  consumes the value for elimination yet.
- **Test:** `...Lives_are_iLives_minus_deaths_and_badges_follow_the_native_gates`,
  `MissionDef_Carries_MissionParams_ILives_At_Offset_208`,
  `KillShip_credits_the_victims_death_and_the_launchers_kill`.
- **Screenshot:** `lives-kills-badges.webp` — `5 lives` with the real
  `livesbmp` art.

### 8. Mission kills — done

- **Native:** `console.cpp:813,818-821` — `ppi->MissionKills()` (native
  `PlayerInfo::MissionKills() = m_pship->GetKills()`, `clintlib.h:346-347`),
  badge only when `IsDeathMatchGame()` (`igc.h:1521-1523`);
  `dialog.mdl:5107` (`hudKillsbmp`) and anchor `SideTopLeft(45,-177)`
  (`dialog.mdl:5445`). PLAYERINFO seeds the counters (`FedSrv.CPP:2085-2086`,
  `appmsg.cpp:2254-2256`), then KILL_SHIP keeps them current
  (`appmsg.cpp:2766-2773,2807-2810`).
- **Port:** PLAYERINFO now writes its existing nKills/nDeaths fields (server
  fills them from the live `PlayerScore`), `ClientWorld.Apply(KillShip)` calls
  the native-equivalent `PlayerInfo.AddKill/AddDeath`, `BuildConsoleText`
  reads the own ship's counts and the deathmatch bit from the real
  `MissionBriefingInfo`, and `HudRenderer` draws the real `hudKillsbmp` with
  `<n> kills` only in a deathmatch.
- **Test:** `...PlayerInfo_carries_mission_kill_and_death_counters_through_the_wire`,
  `...KillShip_credits_the_victims_death_and_the_launchers_kill`,
  `...Lives_are_iLives_minus_deaths_and_badges_follow_the_native_gates`.
- **Screenshot:** `lives-kills-badges.webp` — `4 kills` with the native badge
  art and the real kill-feed lines for the four credited KILL_SHIPs.

### 9. Cloak strength and drained state — partial

- **Native:** `WinTrek/consoledata.cpp:476-489` — own/source ship only,
  `100·(1 − pship->GetCloaking())`; the continuous value comes from the
  `CcloakIGC` ramp (`Igc/cloakigc.cpp:85-122`): target `MaxCloaking`, energy
  throttle `r²`, up at `OnRate`, down at `OffRate`, then
  `SetCloaking(1 − m_cloaking)`. The glass HUD gauge is
  `dialog.mdl:570-597` (`gh_cloak1.png`/`gh_cloak2.png`, energy-scaled), with
  the drained marker at energy ≤ 0.2 (`dialog.mdl:651-656`).
- **Port:** the server now streams the sim's exact `GetCloaking()` on the
  owner-only `OwnShipCloakUpdate(220)` (new wire message, 8 bytes/tick,
  `Server.Broadcast.cs:423,3137`); `ClientWorld.MyCloaking` feeds
  `HudFrame.CloakStrengthPercent` and `HudOwnStatus.CloakStrengthPercent`.
  `HudRenderer`/`ModernHudRenderer` draw `CLOAK <n>%` plus the existing
  drained marker.
- **Remaining difference:** the native CortUI glass gauge art and its
  energy×strength fill are not drawn; the port shows the same native strength
  number as text at the native status anchor and keeps the separate
  energy≤0.2 drained marker.
- **Test:** `...Own_ship_cloak_update_roundtrips_and_derives_native_values`,
  `...Cloak_drained_marker_needs_an_active_ramp_and_low_energy`.
- **Screenshot:** `cloak-strength.webp` — `CLOAK 99%`, `SIG: 0%`, native
  `concloakbmp` icon.

### 10. Cloak active — done

- **Native:** `consoledata.cpp:563-575` — `IsCloaked = GetCloaking() < 1.0`;
  `dialog.mdl:2537-2547` shows `concloakbmp` for that value. Because
  `GetCloaking()` only returns to 1 when the decloak ramp finishes, the icon
  stays lit after the `cloakActiveIGC` bit drops.
- **Port:** `HudModel.BuildStatus` now uses the continuous
  `CloakStrengthPercent > 0` when the owner-only stream has delivered it
  (`HudModel.cs:302-318`), falling back to the old state bit only before the
  first message; `Game1.Rendering.cs` sets `HudFrame.Cloaked` from
  `OwnShipCloakUpdate.IsCloaked`. The real `concloakbmp` icon and anchor are
  unchanged.
- **Test:** `...Cloak_icon_persists_through_the_decloak_ramp_until_cloaking_reaches_one`.
  HUD-12b adds the data path (`OwnShipCloakUpdate_applies_the_native_visible_fraction_to_the_client_world`),
  the server owner-only test (`Hud12bServerCloakTests.Each_owner_receives_only_its_own_cloak_ramp_on_message_220`)
  and the turret-parent rule (`Own_ship_cloak_uses_the_parent_ships_ramp_for_a_turret_gunner`,
  native `GetSourceShip()` at `consoledata.h:144-146` and the own-or-parent gate
  at `consoledata.cpp:480-489,563-575`); the server now sends
  `ship.ParentShip?.Cloaking ?? ship.Cloaking`.
- **Screenshot:** `cloak-decloak-tail-4.webp` — the state bit is already off
  (server `Cloaking=0.781` in the proof log), yet the native icon is lit and
  the port reads `CLOAK 22%`.

### 11. Command view ship summary — partial

- **Native:** `WinTrek/console.cpp:778-800` feeds the console model data every
  frame: `m_pmodelDataMe = GetShip()->GetSourceShip()`,
  `m_pmodelDataMyStation`, `m_pnumberMoney`, `m_pnumberAutopilotOn`,
  `m_pnumberRadarMode`. `dialog.mdl:4997-5027` is `cortCommandModeInfo`:
  ship type, shields/hull, ship/pod displays (energy/ammo/fuel or oxygen),
  thrust/speed/signature/kill bonus and the target group; it is shown under
  `ofInFlightCommand` at `SideLeft(-200,120)→(36,120)` (`dialog.mdl:5412`).
- **Port:** `HudRenderer.DrawCommandShipSummary` draws the summary at that
  anchor in both skins: source-ship hull type, shields/hull/energy/ammo/fuel/
  thrust `softgaugebmp` bars, speed, SIG, kill bonus, a money/autopilot/
  radar-mode line, and the selected target. The rows are laid out by the pane
  itself (fixed anchors, not the shared text-avoidance layout).
- **Remaining difference:** **HUD-12b** — the money/AP/RADAR line is a port
  invention: native feeds those console readouts (`m_pnumberMoney`,
  `m_pnumberAutopilotOn`, `m_pnumberRadarMode`, `console.cpp:778-800`) into
  their own HUD slots, not into `cortCommandModeInfo`, so the row stays
  `partial`. Native `cortTargetGroup`'s target speed/distance rows and the exact
  medVerdana typography/softgauge scaling are also approximated; the
  base/hangar variant `cortCommandModeInfoBase` (`ofInHangar | ...`) is outside
  this wave.
- **Test:** layout/gauge behaviour is exercised by the real renderer proof; the
  data-path helpers (`ShipTypeName`, money/AP/radar values) are fed by
  `Game1.Rendering.cs:2816-2824`.
- **Screenshot:** `command-ship-summary.webp` — `Ship: Adv Scout`, shields
  100%, hull 94%, energy/ammo/fuel/thrust bars, speed 179 mps, SIG 50%,
  kill bonus 25%, `1350 cr AP off RADAR Default`.

## Wire/state additions (minimum needed)

1. **`OWN_SHIP_CLOAK_UPDATE` (220)** — owner-only, per-tick, 8 bytes:
   `float cloaking` (native `GetCloaking()` visible fraction). Native's client
   simulated this value locally (`cloakigc.cpp:85-122`); this renderer-only
   client cannot. Coalesce key 13; sent beside the existing owner-only
   KillBonus/Mining/Lead streams.
2. **`MISSIONDEF(38).iLives`** — the wire slot already existed; HUD-12 exposes
   it at absolute offset 208 (`MissionParams` offset 204, after
   `fStartingMoney`), and the server fills it from `Mission.Lives` (native
   unlimited sentinel by default).
3. **`PLAYERINFO(10)` nKills/nDeaths** — the fields already existed but were
   encoded as zero. The server now writes the live `PlayerScore` counts, and
   the client keeps them current from `KILL_SHIP`, exactly native.
4. **Remote ripcord countdown** — client state only, built from the existing
   sector-wide `RIPCORD_ACTIVATE(185)`; no wire change.

## Verification (HUD-12 original run)

The results below are the original wave; HUD-12b re-verified the whole gate in
"Review fixes (HUD-12b)" below.

- **Whole-solution Release build:** 0 warnings / 0 errors
  (`dotnet build AllegianceCore.slnx -c Release`).
- **AOT publish** (Server/Core changed):
  `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release
  -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot` — exit 0,
  no IL2026/IL3050. Log: `~/allegiance-evidence/HUD12/aot.log`.
- **New tests 3× alone:** `Hud12DataAndArtTests` **14/14 on each of 3 runs**.
- **Affected families 3× alone:** `Hud12DataAndArtTests` + `HudModelTests` +
  `Hud6bOwnStatusTests` + `Hud10PanelsTests` + `P623TargetingPackTests`
  **117/117 on each of 3 runs**.
- **Wire/scorecard tests 3× alone:** `MissionDef*`, `PlayerInfo*` and
  `P674ParityScorecardTests` **29/29 on each of 3 runs**.
- **One fast tier** (`TMPDIR` inside the worktree):
  **9,354 tests, 271.7 s, 5 failures — all
  `Allegiance.Core.Tests.PlayerBundlePathsTests`**, the known local.env
  baseline (5 of its 9 cases; HUD-5 documented the same 5/9 on master's own
  worktree). No other test failed. Log:
  `~/allegiance-evidence/HUD12/fast-final.log` and `fast-summary.json`.
- **Showcase never-parks 5×** (server broadcast changed):
  `Local_showcase_never_parks_with_target_for_five_seconds` **passed 5/5**
  (1 m 48 s each).
- **Scorecard:** `hud` **8/108 → 15/108** complete rows (`7.41% → 13.89%`);
  seven rows moved to `done`, four remain `partial` for the reasons above.
  `python3 scripts/parity-scorecard.py` validates both CSVs.
- No rig / 221 / 200v200 runs were launched.

## Review fixes (HUD-12b)

Base: `pi/hud12b`, created from `pi/hud12` (`0b9b3543`), first merged with
master `339db913` (merge commit `16a07c22`; HUD-11's `HudSnapshotBuffers` /
`SnapshotList` kept as the reuse pattern). Fixes are the review list from the
dispatch; each one below cites the native rule and the test that pins it.

### 1. Per-frame allocations in the HUD draw path

- **`HostileDamageTracker.Prune`** (`Game1.Targeting.cs:1512`) built
  `slots.Keys.Where(...).ToList()` on every `Threat()` call — once per radar
  ship per frame (`Game1.Rendering.cs` radar loop). It now clears and reuses one
  `_staleEpochs` list (grown once), so the per-contact prune is allocation-free.
  The 40 s window / 250 ms flash rules are unchanged
  (`Igc/igc.h:4926-5057`).
- **Value-keyed string cache** (`Rendering/HudStringCache.cs`) replaces the
  per-frame interpolations: `CLOAK n%`, `CLOAK n% DRAINED`, `n lives`,
  `n kills`, every command-gauge percentage, the command-summary ship/speed/SIG/
  kill-bonus/money-AP-radar/target rows and the turret type suffix
  (`Game1.Rendering.cs:2823`). The cache is keyed by the integer value (and by
  source string for arbitrary names), capped, and mirrors HUD-5b's
  `RadarLabelModel.RangeText` pattern.
- **Test:** `Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact`
  (0 B over 10,000 calls), `...HUD_formatted_readouts_allocate_nothing_in_steady_state`
  (0 B over 10,000 frames) and `...Headless_HUD_frame_allocation_does_not_scale_with_contact_count`
  (896 B/frame at 4 contacts and 896 B/frame at 400 contacts). Reverting `Prune`
  to the old LINQ body makes the last test fail with 1,760 B vs 87,296 B/frame,
  so the test genuinely pins the fix. It is a headless model/formatting path;
  the GPU draw itself still needs the real renderer proof.

### 2. The cloak stream ignores turret gunners

- Native `ModelData::GetCloaking()` uses `GetShip()`, which is
  `GetSourceShip()` (`WinTrek/consoledata.h:144-146`), and its visibility gate
  is `pmodel == GetShip() || pmodel == GetShip()->GetParentShip()`
  (`WinTrek/consoledata.cpp:480-489`); the same gate is in `IsCloaked`
  (`consoledata.cpp:563-575`). A gunner's own ship is the child, so the carrier's
  ramp is the one the HUD shows.
- `Server.Broadcast.cs:3158` now has the native rule
  `ship.ParentShip?.Cloaking ?? ship.Cloaking`, and both `PackCloak` branches
  (buffer-reuse and the normal `OwnShipCloakUpdate` path) use it.
- **Test:** `Hud12bServerCloakTests.Own_ship_cloak_uses_the_parent_ships_ramp_for_a_turret_gunner`.

### 3. Cloak-active row data path

- `Hud12bReviewFixTests.OwnShipCloakUpdate_applies_the_native_visible_fraction_to_the_client_world`
  feeds `OwnShipCloakUpdate(220)` into `ClientWorld` and checks `MyCloaking`,
  `StrengthPercent` and `IsCloaked`.
- `Hud12bServerCloakTests.Each_owner_receives_only_its_own_cloak_ramp_on_message_220`
  is a live loopback proof with a real in-process server and two real
  `ConnectionSession` owners: only A cloaks, A's client sees the ramp fall while
  B keeps its own fully-visible value; then the two swap. No owner ever receives
  the other's fraction.

### 4. Friendly ripcord seconds data path

- `Hud12bReviewFixTests.Remote_ripcord_seconds_start_on_activate_and_clear_on_abort_kill_and_delete`:
  `RIPCORD_ACTIVATE(185)` records the activation's local start time;
  `RIPCORD_ABORTED`, `KILL_SHIP` and `SHIP_DELETE` each clear it. `ShipDelete`
  now clears the countdown before its "ship not in the table" early return, and
  `KillShip` clears it for an eject as well as a credited death (native removes
  the model on `SHIP_DELETE`, `clintlib/appmsg.cpp:455-495`).

### 5. "CLOAK DRAINED" regression

- Both renderers gated the whole cloak text row on `CloakStrengthPercent is { }`,
  so before the first 220 the native drained label disappeared. The row now goes
  through `HudStringCache.CloakStatusText(drained, percent)`
  (`HudStringCache.cs:73`), used by `HudRenderer.cs:1084` and
  `ModernHudRenderer.cs:150`: with a known strength it shows `CLOAK n%` /
  `CLOAK n% DRAINED`; with an unknown strength it falls back to the legacy
  `CLOAK DRAINED` when the native energy ≤ .2 gate is true
  (`Artwork/dialog.mdl:651-656`), and draws nothing otherwise.
- **Test:** `Hud12bReviewFixTests.Cloak_drained_still_shows_before_the_first_cloak_strength_message`.

### 6. `_flashAnchor` leak

- `Prune` now removes the attacker's `_flashAnchor` entry when its last damage
  slot leaves the window (the native bucket delete also removes
  `m_timeLastDamage`, `Igc/common.cpp:2572-2589`, `Igc/igc.h:5050-5057`).
- **Test:** `Hud12bReviewFixTests.Damage_bucket_flash_anchor_is_dropped_when_the_bucket_leaves_the_window`
  (`FlashAnchorCount` is an internal diagnostic seam).

### 7. Matrix notes

- `docs/parity/parity-matrix.csv` now marks **Radar carried flag marker** and
  **Radar carried artifact marker** `partial` with the fixed-overlay-radius
  note (native sizes `DrawExpandedBlip` from the contact's projected
  `GetScreenRadius()`, `WinTrek/radarimage.cpp:255-277,343-345`).
- The **Command view ship summary** note now says the money/AP/RADAR line is a
  port invention; native draws those readouts in their own slots
  (`WinTrek/console.cpp:778-800`). The row stays `partial`.
- **Cloak active** and **Radar friendly ripcord seconds** stay `done`, now with
  the data-path/owner-only tests above.
- `python3 scripts/parity-scorecard.py` validates both CSVs. The `hud` area is
  **13/108 complete (12.04%)**, down from HUD-12's 15/108 because the two radar
  marker rows were correctly corrected to `partial` (no row was marked `n/a`).

### 8. HUD-11 leftover — self target / TK_TargetCommand send `c_cidNone`

- Native `SetTarget` only resolves the special `c_cidDefault`: `if (cid ==
  c_cidDefault) cid = pmodel ? GetDefaultOrder(pmodel) : c_cidNone;`
  (`WinTrek/WinTrek.cpp:8827-8833`), and it sends the model with `OT_invalid/NA`
  only when `pmodel` is null (`:8846-8854`). `TK_TargetSelf` passes `c_cidNone`
  (`:10440-10443`) and `TK_TargetCommand` copies the accepted slot's id,
  including `c_cidNone` (`:10408-10412`).
- `AutopilotTarget` gained `UseDefault` (default true for real T/click/cycle
  picks). `SelectSelfTarget` and `CommandSelection` set it false, so
  `CurrentCommandSetFor` returns `c_cidNone` **with the model**
  (`Game1.Orders.cs:171`). `SendCurrentCommandToServer` delegates to the new
  static `SendCurrentCommand` seam (`Game1.Orders.cs:221`), which preserves the
  model payload for `c_cidNone`.
- **Tests:** `Self_target_sends_c_cidNone_with_the_own_ship_like_native`,
  `TK_TargetCommand_preserves_the_accepted_slots_c_cidNone`, and the wiring test
  `SendCurrentCommand_wiring_records_the_exact_native_payload` (own ship → a
  recording sink, plus the default-pick and null-target cases).

### 9. Per-row `FitFontScale` delegates

- `InventoryPaneLayout` has a `SpriteFont`/scale overload
  (`FitFontScale(text, maxWidth, font, scale)`), backed by the shared pure
  `FitScale(width, maxWidth)` rule. `HudRenderer` and `ModernHudRenderer` now
  call it directly instead of allocating `t => _small.MeasureString(t).X` per
  inventory row per frame.
- **Test:** `Hud12bReviewFixTests.Inventory_stats_fit_uses_the_shared_width_rule`
  plus the source guard `HUD_renderers_do_not_allocate_per_row_or_per_character`
  (the guard fails on the old lambdas).

### 10. Per-character chat truncation

- `Rendering/ChatLineTruncationCache.cs` caches the clipped line per source
  string / width / font scale; `ModernHudRenderer` uses it instead of the
  `while (rowText.Length > 0 && Measure(rowText + "...")) rowText = rowText[..^1]`
  loop. The clip is the same longest-prefix-with-`...` result.
- **Test:** `Hud12bReviewFixTests.Chat_line_truncation_matches_the_native_clip_and_is_cached_per_source_and_width`
  (0 B over 10,000 cached frames) and the source guard above.

### Merge integration fix (HUD-11 × HUD-12)

Merging HUD-11's measured-label layout with HUD-12's command-view summary made
`ModernHudTests.Both_skins_render_Hud1_scenarios...` fail in the real hud2-proof
overlay: the command radar label `radar:Enemy fighter...` was placed over the
command summary's fixed `80%` gauge. This reproduced on the merge base before any
HUD-12b edit (verified by stashing this work and rebuilding). The summary now
reserves its native `SideLeft(-200,120)→(36,120)` block in both skins
(`HudRenderer.ReserveCommandSummary`, called from both `PrepareLayout`s), so
measured labels yield to it; the hud2-proof render test passes again. This is a
layout reservation only — no row claim changed.

### HUD-12b verification

- **Whole-solution Release build:** 0 warnings / 0 errors
  (`dotnet build AllegianceCore.slnx -c Release`).
- **AOT publish** (Server/Core changed): exit 0, **no IL2026/IL3050**
  (`~/allegiance-evidence/HUD12b/aot.log`).
- **New/affected tests 3× alone:**
  `Hud11*|Hud12*|Hud8b*` in `Allegiance.ClientCore.MonoGame.Tests`
  **47/47 on each of 3 runs**, and in `Allegiance.ClientCore.Tests`
  **2/2 on each of 3 runs** (the live owner-only 220 test is ~1 s).
- **One fast tier:** **9,409 tests, 274.2 s, 5 failures — all
  `Allegiance.Core.Tests.PlayerBundlePathsTests`** (the same 5-of-9 local.env
  baseline documented in the HUD-12 report). No HUD, renderer or other test
  failed. Summary: `docs/reports/HUD12_assets/hud12b-fast-summary.json`; raw
  log `~/allegiance-evidence/HUD12b/fast.log`. A repeat after the final
  `c_cidDefault` tweak (272.4 s) hit the same 5 baseline failures plus one
  **documented load flake**,
  `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`
  (`docs/TEST_FLAKES.md:1196`); it passed **3/3 alone**, and no HUD test failed
  in either run. Summary/log: `hud12b-fast-rerun-summary.json`,
  `~/allegiance-evidence/HUD12b/fast-rerun.log`.
- **Scorecard:** `hud` **13/108** complete; both CSVs validate.
- **Screenshots:** no new capture was needed for these data/format fixes.
  `docs/reports/HUD12_assets/radar-native-markers.webp` was re-opened and read:
  it still shows the command view with `5 lives` / `0 kills`, ships 8097/8098
  carrying gold quadrant glyph sets around them and 8099 with the rip quadrants
  and `Ship 8099 29s`. At 1920×1080 the 8097 flag and 8098 artifact glyph sets
  remain barely distinguishable to the eye — they read as similar small gold
  marks — so this report makes no stronger art claim than HUD-12 did.

## Real-client screenshots

Seven real `Allegiance.ClientCore.MonoGame` 1920×1080 windows against a real
local `Allegiance.Server` on `m2scatter.igc` (2 sides), captured through the
production autoshot backbuffer path by `tools/hud12-proof`. The proof drives
the real Game1 over TCP; for server-side state it uses the production methods
(`SpawnNpcShip`, `BroadcastGainFlag`, the real sector-wide
`RIPCORD_ACTIVATE(185)` through `SendPeer`, `BroadcastVictimDamaged`,
`Ship.ReceiveDamage`, and the client's real cloak-toggle latch) rather than hand-writing client state. Raw PNGs and the
full run log are outside the repo in `~/allegiance-evidence/HUD12/`
(`raw/`, `hud12-run.log`); hashes/provenance are in
[`HUD12_assets/screenshots.json`](HUD12_assets/screenshots.json). Assets total
**600 KB** (≤ 2 MB).

| Scenario | Asset |
| --- | --- |
| Native flag/artifact/ripcord radar art (command view, not composing) | [`radar-native-markers.webp`](HUD12_assets/radar-native-markers.webp) |
| Native subject art while composing `[TEAM]` in command view | [`radar-subjects.webp`](HUD12_assets/radar-subjects.webp) |
| Threat flash after a real `VICTIM_DAMAGED` | [`radar-threat-flash-2.webp`](HUD12_assets/radar-threat-flash-2.webp) |
| Lives + kills badges | [`lives-kills-badges.webp`](HUD12_assets/lives-kills-badges.webp) |
| Cloak strength | [`cloak-strength.webp`](HUD12_assets/cloak-strength.webp) |
| Decloak tail (bit off, icon still lit) | [`cloak-decloak-tail-4.webp`](HUD12_assets/cloak-decloak-tail-4.webp) |
| Command-view ship summary | [`command-ship-summary.webp`](HUD12_assets/command-ship-summary.webp) |

What is actually visible in each frame (opened and described, not just
pixel-counted):

- **radar-native-markers:** command view with the summary pane on the left,
  `5 lives` / `0 kills`. Ships 8097 and 8098 carry the two different native
  gold quadrant glyph sets (flag vs artifact); 8099 carries the rip quadrants
  and the label `Ship 8099 29s`. The composing chat is NOT open, so the large
  subject frames are absent.
- **radar-subjects:** the same scene with the `[TEAM]` composer open; all three
  friendly ships are now enclosed by the large native `subjectbmp` quadrant
  frames, while 8099 still shows `28s`.
- **radar-threat-flash-2:** cockpit, full-screen red damage wash; enemy
  `Ship 8100` has red label, red range text, red contact square, the red native
  last-fired quadrants and the nearest-enemy diamond. `5 lives` / `0 kills` at
  this earlier frame.
- **lives-kills-badges:** `5 lives` (real `livesbmp` art) and `4 kills` (real
  `hudKillsbmp` art) at the native top-left anchors, with the server's
  `you killed ship 8103/8104` feedback lines still on screen.
- **cloak-strength:** `CLOAK 99%` at the native status anchor with the real
  `concloakbmp` icon and `SIG: 0%`; `5 lives` / `4 kills` remain visible.
- **cloak-decloak-tail-4:** the proof log records server `Cloaking=0.781` and
  the client `MyCloaking=0.781` after the state bit dropped; the frame shows
  `CLOAK 22%` with the native cloak icon still lit.
- **command-ship-summary:** the left-side native command group with
  `Ship: Adv Scout`, shields 100%, hull 94%, energy/ammo/fuel/thrust bars,
  `Speed: 179 mps`, `SIG: 50%`, `Kill bonus: 25%` and
  `1350 cr AP off RADAR Default`.

## Unfinished / remaining gaps

- **Lives** (row 7): presentation and both counters are done, but the port has
  no lives-limited elimination rule, so only a mission that sets
  `Mission.Lives` finite will ever show the badge; production keeps native's
  unlimited default.
- **Cloak strength** (row 9): the native CortUI glass gauge art and its
  energy×strength fill are not drawn; the port shows the native number as text
  plus the energy drained marker.
- **Command summary** (row 11): the money/AP/RADAR line is a port invention
  (native draws those in their own slots, `console.cpp:778-800`); target
  speed/distance rows, the hangar/base variant, and exact native typography/
  gauge scaling remain approximate.
- **Subjects** (row 3): individual/group recipient lists are not modelled by
  the port composer.
- **HUD draw-path allocations** (fix 1): the allocations named in the review
  (per-contact `Prune`, the cloak/lives/kills/command-summary/gauge strings, the
  turret suffix) are gone and pinned at 0 B by the new tests. A steady
  `HudModel.Create` frame still allocates a constant ~896 B (the gauge and
  warning lists plus their `AsReadOnly` wrappers), and a few unlisted readouts
  (time-dilation, weapon-list, roll and the modern `RADAR mode` line) still
  format per frame; HUD-11's buffers cover contacts/inventory/radar labels only.
  These were outside the HUD-12b fix list, so the new allocation test proves the
  frame cost is flat (896 B at 4 and at 400 contacts), not that the whole HUD is
  allocation-free.
- Evidence limitation: the native radar bracket radius is a per-contact
  projected screen radius; this projected-snapshot renderer uses a fixed
  overlay radius for the native quadrant placement (same as the existing
  overlay brackets). HUD-12b therefore corrected the carried-flag and
  carried-artifact rows to `partial` (rows 1 and 2 above) instead of claiming
  the quadrant geometry is exact end to end.

## Merge review of HUD-12b (Claude, 2026-10-20)

- **Fixed: one allocation left on the chat truncation path.** `ChatLineTruncationCache.Fit(…, SpriteFont, float)`
  declared a capturing lambda, and C# allocates the closure at method entry, so even a cache hit allocated. The
  lambda now lives in a slow-path method used only on a miss.
- **Added source guards** (`Hud12bSourceGuardTests`): both renderers draw the cloak readout through
  `HudStringCache.CloakStatusText`; `DrawCommandShipSummary` / `DrawCommandGauge` build no strings; self-target and
  TK_TargetCommand pass `UseDefault: false`.
- **Matrix:** radar threat damage flash and radar ripcord marker → `partial`. Both draw with the fixed
  `bracketRadius = 15f` (`RadarBracketRenderer.cs:416`), not native `GetScreenRadius()`, like the flag/artifact
  markers.
- **Still weak:** the headless allocation test (`BytesPerHeadlessHudFrame`) passes no telemetry, so the own-status
  and cloak model path isn't exercised. The command-summary label collision fix (`ReserveCommandSummary`) has no
  re-shoot.
