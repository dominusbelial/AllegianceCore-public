# P871 — client-side native behaviour from the n/a audit

Lane: **Pi**, worktree `allegiance-pi-p871` on the dispatch base (Claude's local
master with P865/P864b/P863b/P864d/P868 merged). **Nothing committed or pushed.**
No rig/mini/Mac contact. Native reference read-only at
`~/Development/Allegiance/src/`; every cited line was re-grepped this session.

## 1. What landed

| Row | Result | Port |
|---|---|---|
| **SIM-0179** Force feedback | **implemented** | `Audio/ForceFeedback.cs` (producer gates, native impact direction, expiry/focus/disconnect/settings), `Audio/MonoGameForceFeedback.cs` (`GamePad.SetVibration`), `Game1.ForceFeedback.cs`, `IntroPreferences.EnableForceFeedback` (default **on**), `OptionsMenuModel.ToggleForceFeedback` |
| **SIM-0223** Preload | **implemented** | `Rendering/AssetPreload.cs` (`PreloadRules` + `AssetPreloader`), `Game1.Preload.cs`; startup pass over hull/station/probe/missile/mine types |
| **SIM-0227** LoadRadarIcon | **certified both styles** | `RadarIconNameCache`/`RadarIconTextures` + the shared `RadarBracketRenderer` icon pass; Classic and Modern both use it |
| **SIM-0314** numeric `StartSound` | **implemented** | generated `Core/Audio/NativeSoundCatalog.Generated.cs`, `Audio/NativeSoundDispatch.cs`, `SoundManager.StartSound` |
| **SIM-0346** `PlaySoundIf` | **implemented** | `PlaySoundIfSlot` + `SoundManager.PlaySoundIf` |
| **SIM-0371** gunner interior loop | **implemented** | `GunnerInteriorSound` rule, `SoundManager.SetGunnerInteriorLoop`, wired in `Game1.FlightInput` (pilot loop suppressed while gunning) |
| **SIM-0395** carrier-repair notification | **certified** (already live) | `ClientWorld.FlightInterface.Apply(RelaunchShip)` → voice id **1399**, which the new native table confirms as `salRepairedAtCarrierSound`; existing `P740FlightInterfaceTests` covers own-only + camera override |
| **SIM-0407/0408/0409** sound gates | **implemented/certified** | `SoundRouting` (`AllowsEffect`, `AllowsEffectWithOffset`, `OffsetSource`/`LocalOffset`, `AllowsNotification`) |
| **SIM-0412** MDL button sound flush | **implemented** | `SoundIdEventSink` + `SoundManager.StartSoundId/FlushAudio`; every Options/menu activation now fires and flushes its click |
| **fedmsg::113 MISSIONPARAMS** | **implemented** | `Mission.AddSide` (native `InitSide` seeding), `Server.MissionParams.InitSide` + live growth, `Server.Lobby.BroadcastSideRoster`, client team cap now native 6 |
| **hud::Game list screen boundary** | **implemented (merged)** | explicitly merged with the already-certified server-browser row (intro Find Server scan + directory browser) |
| **lobby::Create game wizard** | **implemented** | `Lobby/Games/GameCreationStore.cs`, `GameEndpoints.cs`, `ClientCore.Game/Games/CreateGameWizard.cs`, `LobbyGameClient.cs`, `Game1.CreateGame.cs` (Find Server → **N**) |
| **lobby::Lobby-directory chat** | **implemented** | `Lobby/Chat/DirectoryChat.cs`, `ChatEndpoints.cs`, `ClientCore.Game/Chat/DirectoryChatLog.cs`, `LobbyChatClient.cs`, `Game1.DirectoryChat.cs` (`/lobby` send), rendered in the shared chat log |
| **autoupdate::crcFileList** | **implemented** | `Core/FileListCrc.cs`, `ClientCore/Session/ClientFileList.cs`, `Server.Dispatch.CheckFileList`, `ServerConfig.RequireFileList/ExpectedFileListCrc` |
| **commander::Voice chat** | **decision resolved** | native **prerecorded quickcomms** certified (quickchat.mdl catalog + `NativeVoiceCatalog` sonic chats); microphone VoIP explicitly not a parity requirement |
| **fedmsg::107 ICQ_CHAT_ACK** | **retired** | "retired external service — Domingo 2026-10-28": the native ICQ acknowledgement targets a discontinued external messenger and is deliberately not ported |

Native citations, per-row actions and test names now live in
`docs/parity/sim-rules-matrix.csv`, `docs/parity/parity-matrix.csv` and
`docs/parity/p868-na-audit.csv` (all three updated in place, LF endings).

### 1.1 Native rules worth calling out

- **Numeric `SoundID` space.** `Igc/igc.h:459-464` declares
  `enum { lastDynamicSound = 1000, #include "sounds.h" }`, so every `DEFSOUND`
  name starts at **1001**; `soundList` numeric keys are literals and later
  entries overwrite earlier ones (`TLookup::Parse`, `engine/value.h:686-722`).
  The generated table reproduces exactly that (1045 templates, `Count = 1707` in
  P871 — **wrong, see the P871b section**: the generator kept a dead
  `/* NYI no turrets */` block and every id after `Igc/sounds.h:443-459` came out
  **+13**; `collisionSound = 1040` was right, `salRepairedAtCarrierSound` is
  **1386**, not 1399, and the corrected table has `Count = 1694`). **The §1.1
  claim that the table "confirms 1399" was wrong** and is corrected in §6.
- **Force feedback.** The seam gate is `model == NULL || model == GetShip()`
  (`Igc/igc.h:4833`, `trekigc.cpp:4985-4990`); fire credits the gunner's ship
  (`weaponIGC.cpp:174`), collisions the ship party
  (`shipIGC.cpp:835,1005,1190-1194`), and credited damage uses
  `GetShip()->GetSourceShip()` with
  `direction = degrees(atan2(backward·Δ, right·Δ)) + 90`
  (`trekigc.cpp:4296-4302`). The client has no `p1/p2`, so Δ is reconstructed
  from the wire hit normal (`normalize(position1−position2)`,
  `projectileIGC.cpp:224-225`) — the same vector, sign inverted once.
- **Preload.** Colour-key hint OFF and mipmap generation ON only inside the call,
  then both restored; texture name + `"bmp"`; a null model never suppresses the
  texture (`trekigc.cpp:4912-4937`), and only an alphabetic texture field names a
  surface (`hullTypeIGC.cpp:49-51`).
- **Gunner interior loop.** Resolved from the SOURCE hull's mounted-turret
  hardpoint and maintained iff `bIsGunner`, independent of the pilot loop and of
  the camera (`trekigc.cpp:1096-1112,1300-1314`).
- **Side growth.** `SetMissionParams` growth calls `InitSide`
  (`FedSrv/fsmission.cpp:1846-1852`) and `UpdateSides` creates the side from
  `rgCivID`, the native `sideNames[]` array and the per-slot palette
  (`Igc/missionigc.cpp:3749-3779`).
- **Directory chat / create game / file list.** Native relay
  (`clintlib/appmsg.cpp:2640-2665`), `CREATE_MISSION_REQ`/ACK/NACK
  (`lobby/messageslc.h:54-66`, `lobby/client.cpp:390-465`) and
  `LOGONREQ.crcFileList` compared against the release
  (`clintlib/clintlib.cpp:1418`, `lobby/client.cpp:272-281`).

### 1.2 Stated deviations

1. Native's `StartSound` numeric bound is `soundId > GetCount()` — one past the
   last index, a retail-build out-of-bounds read. The port gates `>= Count`; all
   in-range behaviour is identical, the crash case is not reproduced.
2. Native's DirectInput `PlayFFEffect` switch has no `break` after the bounce
   case, so a bounce also starts the explode motor when file effects are
   disabled. That is a device-backend bug; the MonoGame adapter maps one effect
   to one rumble profile. `effectExplode` has no live producer in this checkout,
   so it is never emitted.
3. Haptics could not be exercised on hardware here (no gamepad on this host);
   the device adapter, producer gates, direction maths and stop paths are unit
   tested, and the live client logs the adapter's availability.
4. DirectInput polar direction has no exact MonoGame analogue; the adapter pans
   two motors by the native direction (documented as a device adaptation).

## 2. Verification

- **Whole-solution build**: **0 warnings / 0 errors**
  (`dotnet build AllegianceCore.slnx`).
- **Tools**: `tools/build-tools.sh` — **all 92 tool projects** built.
- **AOT publish** (`src/Allegiance.Server`, linux-x64, `PublishAot=true`):
  succeeds, **no IL2026/IL3050**.
- **`docker compose config --quiet`**: passes (no new service/port/volume/env).
- **`tools/check-glossary-links.py`**: OK (286 paths; GLOSSARY updated with the
  new audio/force-feedback/preload/create-game/chat/file-list systems).
- **New tests — 50 cases, each class 3× alone, zero failures**:
  `P871NativeSoundRowsTests`, `P871ForceFeedbackTests`, `P871PreloadTests`,
  `P871WizardChatAndFileListTests`, `P871RadarIconStyleTests` (32 cases,
  MonoGame tests); `P871FileListLogonTests`, `P871MissionParamsGrowthTests`
  (4 live cases, ClientCore tests); `P871ChatAndGamesTests` (5, Lobby tests);
  `P871FileListCrcTests` (9, Core tests). Two existing expectations were updated
  for the native behaviour restored here: `P806OptionsMenuTests` (Game submenu
  now seven rows with Force Feedback) and `P853bHostEditUiTests` (team cap is
  native six, growth is live).
- **ONE Fast sweep** (`tiers-20261028T230922`): **11,512 tests, 480.4 s**,
  infrastructure **PASS**; failures were the six below and are addressed:
  | Method | Outcome | Classification |
  |---|---|---|
  | `P806OptionsMenuTests.Game_submenu_…` | fixed | P871 stale expectation (7 rows) |
  | `P784ComposeTests.Chat_history_marks_the_side_leaders_lines_bold` | fixed | P871 null directory-chat log in a headless instance |
  | `P757ChatPaneTests.P806_chat_pane_pipeline_uses_the_two_native_filters` | fixed | same |
  | `P757ChatPaneTests.P806_filter_chats_to_all_drops_everyone_lines_in_game` | fixed | same |
  | `P853bHostEditUiTests.Settings_form_steps_native_values_…` | fixed | P871 growth removed the assembled-count cap |
  | `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_Is_TheFlaggedRadarTargetHighlight` | 2 pass / 1 fail alone | ledgered environment flake (P864d/P868: 0/3 and 1/3); no radar placement code touched |
  The quarantine retry that passed is the known `AddPartTests.Part_pickup_decline…`
  fixture assumption. Recorded in `docs/TEST_FLAKES.md`.
- **Showcase never-parks 5×** — `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`:
  **pass, pass, pass, pass, pass** (3m09s, 3m35s, 3m11s, 3m47s, 3m48s;
  ten complete local matches, zero skips).
- **Parity scorecard** (`scripts/parity-scorecard.py`), before → after:
  | Matrix | Before | After |
  |---|---|---|
  | General (`parity-matrix.csv`) | 98.69% (1861.832/1886.5), 651/660 | **99.38% (1874.832/1886.5), 656/660** |
  | Simulation (`sim-rules-matrix.csv`) | 89.31% (1905/2133), 683/803 | **89.97% (1919/2133), 694/803** |
  `tools/p868-na-audit.py --native-root` validates all 199 audit rows:
  **A=76, B=118, C=5; B implemented = 27** (was 11).
- **`git diff --check`** clean; no `/home/<user>` paths or secrets in
  `src tests scripts tools docker .github`; `docs/parity/*.csv` have no CRLF.
- **Real-window screenshots** (`scripts/live-proof.sh`, 2560×1440 presented
  backbuffer, look-at with the read tool):
  [`P871_assets/hud-classic-2560x1440.png`](P871_assets/hud-classic-2560x1440.png),
  [`P871_assets/hud-modern-2560x1440.png`](P871_assets/hud-modern-2560x1440.png),
  [`P871_assets/hud-classic-radar-zoom.png`](P871_assets/hud-classic-radar-zoom.png).
  What is actually visible: the Classic cockpit frame (round teal gauges, the
  minimap panel bottom-right with `Plato`/`Ganymede`/`Rigel` nodes and the
  `[TAB] EXPAND` hint, `RADAR Default` LOD label) and the Modern frame (hexagonal
  gauge rings, same minimap and labels). The client log for both runs reports
  `hud-16 radar icons: 117 surfaces, 0 blank, 0 missing`, i.e. every core
  contact surface decoded. **Limitation, stated:** the marching fleets never
  entered the tactical radar's own range during the captured windows, so no
  contact ICON is visible in these frames (an earlier `mode:"arena"` scenario is
  no longer supported — P795 removed it, and `mode:"march"` fleets stayed out of
  range). The known/absent icon resolution and both-style wiring are therefore
  certified by `Hud16RadarIconTests` + `P871RadarIconStyleTests`, not by these
  two pixels.
  The two looked-at frames are archived under `docs/reports/P871_assets/`; the
  raw command logs stay in `.scratch/p871/` (git-excluded).

## 3. Native-intent classification

- **A/B/C:** the audit's classes are unchanged. Every row above is **B**
  (executable/observable native behaviour) except the two **C** product
  decisions, which are now **resolved**: voice chat = native prerecorded
  quickcomms (certified), ICQ = retired external service (Domingo 2026-10-28).
  No row was closed with "the port chose differently" or "same observable
  outcome".
- **n/a:** no new n/a claims. The retired ICQ integration is an explicit product
  retirement, not a platform exclusion; nothing here is DirectPlay/AGC
  telemetry.

## 4. Modern / better-approach note

The native seams stay where they belong and the edges are adapted:

- **Haptics:** MonoGame has no DirectInput effect objects, so the adapter maps
  the three native effects to two-motor vibration profiles and keeps native's
  producer guards, direction maths and enable preference (`EnableFeedback=1`)
  in a device-free controller. A future controller API (impulse triggers, HD
  rumble) drops in behind `IForceFeedbackDevice` without touching the rules.
- **Sounds:** the `soundList` vector is a generated, immutable table rather than
  a C++ lookup built at boot, so numeric-ID validation is O(1), thread-safe and
  testable; the port keeps native's "invalid id is silence" contract while
  avoiding the original's out-of-bounds bound and ZAssert-only safety.
- **Preload:** warming the real loader output (model bytes + decoded surface,
  with mipmaps) is the portable equivalent of pre-resolving modeller
  namespaces; the colour-key/mipmap hint is an explicit scoped flag, not global
  mutable engine state, so nested/failed preloads cannot leak a mode.
- **Mission growth:** `Mission.AddSide` appends to the one live side list the
  sectors already hold, so a growth is visible everywhere without re-wiring; the
  server re-publishes `SIDE_ROSTER` (the port's replace-on-arrival roster)
  instead of native's DirectPlay group membership.
- **Lobby surface:** the native lobby's DirectPlay session/`CREATE_MISSION_REQ`
  is an HTTP resource (`/api/games`) with the caller's lobby identity, an
  owner-only edit path reusing the authoritative `MissionParamsMsg.Invalid`
  rule, and a token-authenticated server claim. Directory chat is an
  authenticated, sequenced relay (append-only, bounded, per-account mute) rather
  than a broadcast fan-out — same observable semantics, no session state.
- **File-set identity:** a CRC-32 over the installed file list keeps the native
  wire field and rejection reason, while the actual update delivery remains the
  signed HTTPS manifest feed (P810); the server gate is opt-in so an
  unconfigured host behaves exactly like pre-P871.

## 5. Known limits left visible

- **Haptics hardware**: no gamepad on this host, so the physical rumble was not
  felt. The adapter, its availability check, the producer gates, direction maths
  and the stop-on-expiry/focus/disconnect paths are unit-tested and wired.
- **Radar-icon screenshot**: the live frames show the HUD and the minimap but no
  tactical-radar contact icon (the `mode:"march"` fleets stayed out of radar
  range inside the capture window; `mode:"arena"` was removed by P795). Icon
  resolution/wiring is certified by tests in both styles, as stated above.
- **Create-game request consumption**: the lobby owns the catalog, the
  authenticated request, owner-only parameter editing, cancel, and the
  token-authenticated claim a game server can call (all tested). This checkout's
  game server still builds its worlds from `ServerConfig.Games` at boot and does
  not poll `GET /api/games` / claim requests at runtime, so submitting the wizard
  today records and advertises the request but does not instantiate a new world
  in an already-running server process. That runtime provisioning step is the
  remaining integration; it is called out in the row's action so nobody reads the
  wizard as spawning worlds by itself.
  **CLOSED by P871b** (§6): the game server now polls, claims and provisions the
  mission, and the client joins that server on the ACK.

---

# 6. P871b — the P871 review fixes (2026-10-28)

Lane: **Pi**, worktree `allegiance-pi-p871b` (a git worktree of AllegianceCore at
master + `git cherry-pick --no-commit cab6e98e`, P871's WIP commit). **Nothing
committed or pushed**; all changes are in the working tree. No rig/mini/Mac
contact, no `git stash`, no synthetic CPU load. Native reference read-only at
`~/Development/Allegiance/src/`; every cited line re-grepped this session.
Scratch under `.scratch/p871b/` (git-excluded, screenshots there, **no new media
in the repo**).

## 6.1 The eight review items

### (1) Sound IDs were off by 13 — fixed, and the whole port re-checked

`scripts/generate-sound-catalog.py` stripped only `//` comments, so the dead
`/* NYI no turrets … */` block at `Igc/sounds.h:443-459` (13 `DEFSOUND` names,
one of which even contains a `//` inside the block) stayed in the enum and every
generated id after it was **+13**.

- **The stripper is now shared and preprocessor-correct**:
  `scripts/native_cpp_comments.py` (`strip_cpp_comments`) is a single-pass
  scanner (block comments first, then line comments, string/char literals left
  intact). Both generators use it — `generate-voice-catalog.py` had the identical
  bug, and its `Sounds` table is what the carrier-repair path resolves through.
- **Verified against the real preprocessor**, not by eye: `gcc -E` of
  `#define DEFSOUND(n) __SND(n)` + `#include "Igc/sounds.h"` yields **693 names,
  ids 1001..1693** (`turretLimit` survives; the 13 `turret*` names inside the
  block do not). The regenerated tables match that map exactly:
  `NativeSoundCatalog.Count = 1694` (was 1707), `IdsByName = 693`,
  `NativeVoiceCatalog.Sounds` re-derived from the same map.
- **Corrected values** (all `gcc -E`-checked): `noTurretsSound = 1366`
  (last name before the block), `droneMiningSound = 1368` (last live name before
  it), `salCommenceScanSound = 1369` (first after it), `salRepairedAtCarrierSound
  = 1386` (**was 1399**), `salNoAmmoSound = 1396` (**was 1409**),
  `salChaffDepleted/MissilesDepleted/DispenserEmpty = 1404/1405/1406`
  (**were 1417-1419**), `tm_0_blankSound = 1693` (last name). `collisionSound =
  1040` was correct, as were every id ≤ 1368 (all the drone/defend chatter cues).
- **Every hard-coded ID in the port re-checked** (grep for 4-digit sound
  constants + `NativeVoiceCatalog`/`NativeSoundCatalog` lookups):
  `ClientWorld.FlightInterface.cs:91-93` (SIM-0395, 1399 → resolves
  `NativeSoundCatalog.IdOf("salRepairedAtCarrierSound")` = 1386),
  `Core/Sim/Ship.cs` `SalNoAmmoSound` (1409 → 1396 — the *server* was sending
  `salMIASound`'s id, a real behavioural bug, not just a comment),
  `Audio/SoundManager.cs` depleted-VO comments (1417-1419 → 1404-1406). All other
  constants (`DroneChatterCue`, `Server.Combat` 1323-1343, `VoAffirmative` 1178,
  `VoNegative` 1185, `NotificationCue` 1035/1036, `WeaponReadyCue` 1023) are below
  the block and were re-verified against the `gcc -E` map.
- **Tests**: `P871NativeSoundRowsTests` now pins ids on **both sides** of the
  block and asserts the 13 dead names do not exist
  (`The_dead_nyi_turret_block_does_not_shift_the_ids_after_it`);
  `P732VoiceCatalogTests` re-derives the native name list with the same
  preprocessor-correct strip (it had encoded the bug); `P740FlightInterfaceTests`
  and `P767WingmanAmmoTests` assert the corrected 1386/1396.
- **The §1.1 claim is retracted** (see the strike-through note in §1.1 above and
  the row texts): the table did *not* "confirm 1399"; it produced it.

### (2) Create-game wizard: the server now provisions the mission (end to end)

P871 shipped the lobby half (catalog, authenticated `CREATE_MISSION_REQ`,
owner-only parameter editing, cancel, token-authenticated claim) and recorded the
request, but **no server consumed it**. P871b implements native's remaining half
(`lobby/client.cpp:390-465`):

- **The server claims the relay.** `Server/LobbyHeartbeat.cs` polls
  `GET /api/games` on the heartbeat the server already runs, filters
  `State == Queued && ServerId == <my listing id>`, claims the first one with its
  **own registration token**, and hands it to the sim thread
  (`TryTakeClaimedGame`). It only claims while `CanHostPlayerGame()` says an idle
  mission slot exists — native's lobby never sends `CREATE_MISSION_REQ` to a
  server that is mid-game.
- **The sim thread builds the world.** `Server/Server.Provisioning.cs`
  (`DrainClaimedGames`, wired into `SimPass` as the `claimedGames` step) calls
  `PlayerGameProvisioning.TryCreate` to map the wizard's native `MissionParams`
  onto a `GameConfig` — winning conditions, team count + `rgCivID` (unknown civ
  ids fall back to random, like `InitSide`, `fsmission.cpp:33-62`), per-team
  caps, lives, options (developments/paths/defections/allied rip+viz/friendly
  fire/show-map/home-sector/squad), countdowns, treasure rates — then
  `AssembleGameMission(..., playerCreated: true)` (whose showcase goal rotation
  is skipped so the player's own goals stand) and inserts the mission at **slot
  0** with its aligned `_config.Games` entry. Slot 0 is the join target every
  join path uses, so the created mission is where a joiner lands; the configured
  worlds keep running behind it. Refusals are logged, never thrown.
- **The client is sent there.** `Game1.CreateGame.cs` keeps the chosen server
  from the ACK and joins it (`JoinFromIntro`), so the wizard ends in the new
  mission instead of a "Game requested" line — native ends the create flow by
  connecting to the server that created it.
- **The parameters cross HTTP as the native struct.** The games DTOs now carry
  `paramsWire` = base64 of `MissionParamsMsg.Encode()` (the 256-byte
  `FM_CS_MISSIONPARAMS`); see item (3).
- **Tests**: `P871bCreateGameProvisioningLiveTests` (Allegiance.Core.Tests) —
  real in-process lobby over real HTTP: register → signed-in create → the server
  heartbeats, claims, provisions; asserts the mission's team count (3), winning
  condition (7 kills), map/sectors/warps, `PlayerCreated`, the join target, and
  that the request is consumed (a later tick creates nothing).
  `P871bCreateGameClientTests` pins the client's `paramsWire` body and the
  join-on-ACK. `P871ChatAndGamesTests` (lobby side, incl. owner param edits and
  the claim) stays green.

### (3) `IncludeFields = true` — gone, and the other endpoints pinned

P871 set `IncludeFields = true` on the lobby's **global** serializer so the games
endpoints could bind `MissionParamsMsg`'s public fields. P871b removes the flag
entirely: the parameters now ride `paramsWire` (base64 of the native packed
struct), so the lobby is back on the framework's default contract and the games
endpoints carry one string instead of ~60 JSON members for a 252-byte struct.

`P871bJsonContractTests` snapshots the exact bytes of one response per endpoint
family — `GET /api/identity/me`, `/api/career/{id}`, `/api/squads`,
`/api/events/`, `/api/servers` — and pins the games shape (camelCase property
names, no `StrGameName`/`bits` field leakage, and the blob round-trips through the
codec). **Measured before/after**: with the flag flipped back on, those five
endpoint families' bytes are *identical* in this checkout (no response DTO had
public fields), so the P871 flag's only observable effect was on the games
endpoints themselves; the snapshots now keep it that way for good.

### (4) Directory chat — cite, session gate, bearer, mute UI, `MaxVisible`

- **Cite corrected; this is a PORT EXTENSION.** Native has **no** lobby/directory
  chat: every chat target is in-mission (`Igc/igc.h:2731-2734`) and the lobby only
  ingests `FM_S_LOG_CHAT_MESSAGE(216)` to upload chat logs for moderation
  (`lobby/messagesls.h:152`, `lobby/server.cpp:341-372`). P871's cite
  `clintlib/appmsg.cpp:2640-2665` is `FM_CS_CHATMESSAGE`, the in-mission relay.
  The row is now `n/a` with that reason (see open questions); the semantics the
  port *does* borrow are native's: own-echo suppression and per-player mute
  (`trekigc.cpp:4492` + `PlayerInfo::SetMute`, `clintlib/clintlib.h:282,326`), the
  `!mute`/`!unmute` shell command (`clintlib/clintlib.cpp:2281-2296`) and the
  context-menu mute key ('M', `WinTrek/WinTrek.cpp:3731-3734,4310`).
- **No chat on the intro/lobby screens** — fixed: the relay is now polled from the
  top-level frame `Update` (every state: intro, splash, browser, connecting,
  side-select, ready-check, flight) instead of only from `UpdateFlying`, and the
  `_session`-null early return is gone (the log is independent of the world).
  `UpdateDirectoryChat` also throttles *before* touching `player.json`, and reads
  the **configured** lobby (`--lobby`) instead of only the remembered one.
- **`SendAsync` sent no bearer token** — fixed: `LobbyChatClient` takes the
  identity bearer and authorizes read/post/mute/unmute.
- **`MuteAsync` had no UI** — fixed twice over, both in the shared model so both
  HUD styles get it: the `!mute <callsign>` / `!unmute <callsign>` shell line now
  also mutes a *directory* sender through `MuteAsync` (native's own command path),
  and the Options → **Chat/Mute/Filter** page (native's `idmMuteFilterOptions`)
  gained a `Mute/Unmute <newest directory sender>` row (`idmContextMutePlayer`,
  WinTrek.cpp:4310). The local log drops the sender's lines immediately
  (`trekigc.cpp:4492`).
- **Directory lines cut by `MaxVisible`** — fixed: the pane merges game and
  directory lines and trims the **oldest** overflow, so a directory line is never
  dropped because the game chat was already full.
- **Tests**: `P871bDirectoryChatTests` (session-free poll from the intro state,
  bearer on every mutating call, mute drops lines, the options row, and
  directory lines surviving a full game-chat window).

### (5) Rows

- `commander::Voice chat` → **`done`**: certified native **prerecorded
  quickcomms** (Domingo 2026-10-28 ruling), tests cited
  (`P732VoiceCatalogTests`, `QuickChatMdlParserTests`,
  `QuickChatOrderResolverTests`, `LiveQuickChatToDroneCommandTests`,
  `P770ChatKeySoundTests`, `P871NativeSoundRowsTests`). Microphone VoIP is
  explicitly not a parity requirement.
- `fedmsg::107 ICQ_CHAT_ACK` → **`n/a`**, reason "retired by Domingo 2026-10-28".
- The two Steam rows (`SIM-0148`, `SIM-0472`) were already `n/a` with the same
  retirement reason; re-checked.
- `lobby::Lobby-directory relay feedback` → **`n/a`** (port extension, above).
- `lobby::Create game` stays **`implemented`**, now with the runtime provisioning.
- `SIM-0146` reachability `test-only` → **`production`** (see item 8).

### (6) `cockpitMode: … && !isGunner` — audited and split correctly

P871's rewrite dropped **every** cockpit-mode behaviour for a turret gunner.
Native splits the two gates (`WinTrek/trekigc.cpp:1310-1314`):
`PlaySoundIf(m_pGunnerInteriorSound, …, bIsGunner)` /
`PlaySoundIf(m_pPilotInteriorSound, pht->GetInteriorSound(), …, !bIsGunner)`,
while the **engine/main-thruster interior bed carries no gunner gate** and the
*audibility* of the interior mix is the camera's job (`SetInternalVolume(-100)`
off cockpit view, `trekigc.cpp:1360-1372`). So:

- `Audio/CockpitAudio.cs`-style record `CockpitAudio(CockpitView, IsGunner)` now
  owns the split: `EngineAudible = CockpitView` (a gunner keeps the engine mix)
  and `PilotInterior = CockpitView && !IsGunner` (the pilot's hull interior bed
  belongs to the pilot; the gunner's own loop replaces it).
- `SoundManager.Update(..., pilotInterior:)` gates only the hull interior bed;
  `Game1.FlightInput` passes `cockpitAudio.EngineAudible` / `.PilotInterior`.
- The other cockpit-keyed loops were audited: the turn-thruster interior/exterior
  pair and `UpdateShipExterior` are camera-keyed only, which matches native (they
  are not gunner-gated), so they were left alone.
- **Test**: `P871NativeSoundRowsTests.A_turret_gunner_keeps_the_cockpit_mix_but_not_the_pilot_interior_bed`.

### (7) Preload resets mipmap generation to `false`

`AssetPreload.cs` restored the previous mipmap value on scope exit; native
hard-resets it (`trekigc.cpp:4935-4936`: `SetColorKeyHint(bOldColorKeyValue);
SetEnableMipMapGeneration(false);`) while the colour-key hint *is* restored.
`PreloadRules.Restore` now matches exactly that; the nested-scope expectation in
`P871PreloadTests` was updated (a nested scope's close resets mipmaps to false).

### (8) SIM-0146 wiring — `PlayerCreated` now has a producer

`Mission.PlayerCreated` existed (P872) but nothing set it, so
`Server.Periodic.CheckForNoShows` could never fire.

- **Set** by the provisioning path only: `AssembleGameMission(..., playerCreated:
  true)` from `Server.Provisioning.ProvisionPlayerGame` (i.e. every wizard /
  `CREATE_MISSION_REQ` mission). **Not** set for configured, admin, showcase or
  skirmish worlds (they call the assembler with the default `false`), and a
  player-created world keeps the flag across its own game-over rotation.
- The reap is now also **index-safe**: `_missions` and `_config.Games` are
  index-aligned (rotation rebuilds from `_config.Games[idx]`, `MissionId` is
  `idx + 1`), so the reaped mission's config entry leaves with it along with its
  `_missionConfigs`/`_matchups`/`_missionParams`/`_mapGenMissions` state — without
  that, a reap would silently shift every later mission's config.
- **Tests**: `P871bCreateGameProvisioningLiveTests` asserts the provisioned
  mission is `PlayerCreated`, the configured one is not, the empty
  player-created mission is reaped after 10 s of sim time and the configured
  world behind it survives with `_config.Games` still length 1;
  `P872SessionReapTests` (reap/persist/occupied) stays green.

### New media (P871b)

**None.** No image, audio or video file was added by P871b; every proof frame
stays in `.scratch/p871b/` (git-excluded). The only media in the working tree are
P871's own three PNGs (`docs/reports/P871_assets/hud-classic-2560x1440.png`,
`hud-modern-2560x1440.png`, `hud-classic-radar-zoom.png`), which arrived with
`git cherry-pick --no-commit cab6e98e` and still need Domingo's approval before a
push.

## 6.2 Verification

- **Whole-solution build**: **0 warnings / 0 errors** (`dotnet build
  AllegianceCore.slnx`).
- **Tools**: `tools/build-tools.sh` — all **92** tool projects built.
- **AOT publish** (`src/Allegiance.Server`, linux-x64, `PublishAot=true`):
  succeeds, **no IL2026/IL3050**.
- **New/changed tests 3× alone** (each iteration = the whole set once):
  `P871NativeSoundRowsTests`, `P871PreloadTests`, `P871bDirectoryChatTests`,
  `P871bCreateGameClientTests`, `P871WizardChatAndFileListTests`,
  `P806OptionsMenuTests`, `P732VoiceCatalogTests`, `P757ChatPaneTests`,
  `P784ComposeTests` — **95 passed ×3**;
  `P871bCreateGameProvisioningLiveTests`, `P872SessionReapTests`,
  `LobbyHeartbeatTests` — **11 ×3**; `P871ChatAndGamesTests`,
  `P871bJsonContractTests` — **7 ×3**; `P740FlightInterfaceTests` — **16 ×3**.
- **Fast tier** (`tools/run-tests.sh --tier fast`): run 1
  (`tiers-20261029T022443`): 11,557 tests in 485.7 s, infrastructure **PASS**;
  failures: `P767WingmanAmmoTests.GateOn_WingmanRunsOut…` (**mine** — it asserted
  the old 1409; fixed to native's 1396 and re-run 3×, green),
  `NpcWingmanDefendLiveTests.Defender_disengages…` (**pre-existing at base**,
  ledgered in `docs/TEST_FLAKES.md` §P872: deterministic 3/3 alone, reproduced
  with P872's edits reverted; still deterministic 3/3 alone here and unrelated to
  this lane), and three load flakes that pass 3/3 alone
  (`LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores`,
  `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget…`,
  `ConsoleLogNonBlockingTests.Verbose_gate_silences…`) — the sweep runs the
  projects in parallel on a shared host.
- **Fast tier run 2** (`tiers-20261029T023527`, after the P767 fix): 11,556
  tests in 486 s, infrastructure **PASS**, **no failure from this lane**. Four
  non-green results, all pre-existing/load, each re-checked alone:
  `NpcWingmanDefendLiveTests.Defender_disengages…` (pre-existing base failure,
  ledgered), `ThreePeerSeededSessionTests.SeedThreePeers…DeterministicVisibility`
  (ledgered logon-drain-order load flake; 2/3 alone here),
  and the calibrated **10 s timing guard** tripped by
  `WarpBombTests.Live_whole_mission_warp_bomb…` (36.6 s) and
  `MinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag…` (35.2 s) —
  both take **2 s** for the pair, 3/3, when run alone (host load average 4.0-6.2
  while another lane's suite held the runner lock). No timing limit, skip or
  fixture was changed.
- **Showcase never-parks 5×** — `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`:
  **pass, pass, pass, pass, pass** (3m36s, 3m32s, 4m07s, 3m32s, 2m46s; ten
  complete local matches, zero skips).
- **Parity scorecard** (`scripts/parity-scorecard.py`), before → after:
  | Matrix | Before | After |
  |---|---|---|
  | General (`parity-matrix.csv`) | 99.38% (1874.832/1886.5), 656/660 | **99.80% (1876.832/1880.5), 656/658** |
  | Simulation (`sim-rules-matrix.csv`) | 91.37% (1947/2131), 705/801 | **91.41% (1948/2131), 706/801** |
  (The general denominator shrinks by 6 because the ICQ row (weight 4) and the
  directory-chat row (weight 2) are now `n/a`; the voice-chat row gained its
  weight-4 credit.)
- **`tools/check-glossary-links.py`**: OK (293 paths; GLOSSARY updated with the
  shared comment stripper, the provisioning path and the directory-chat scope
  correction).
- **`tools/p868-na-audit.py --native-root`**: **199 rows; A=78 B=119 C=2;
  B implemented = 35, partial = 84**; citation bounds/text checked in 42 files.
  Two guard rules were widened to allow the decisions this task records, and both
  still require the decision to be written down: a **C** row may now close as
  `n/a` (retired: ICQ) or a complete status (certified: voice chat) instead of
  staying `partial` forever, and a **B** row may close as `n/a` when the audit
  itself re-scopes it out of parity with a `retain n/a:` reason (the
  directory-chat cite correction).
- **`git diff --check`** clean; `docs/parity/*.csv` LF-only (0 CR); no
  `/home/<user>` paths or secrets in `src tests scripts tools docker .github`.
- **Real-window proof** (`scripts/live-proof.sh` + a real `Allegiance.Lobby` on
  `127.0.0.1:5099` with a private identity store, screenshots in
  `.scratch/p871b/shots/`, **not** committed — **no new media in the repo**; the
  three PNGs P871 landed under `docs/reports/P871_assets/` are still P871's and
  still await Domingo's OK):
  - **Wizard, both styles**: `.scratch/p871b/shots/wizard-classic.png`,
    `wizard-modern.png` — a real
    2560×1440 presented backbuffer of the intro's **Create Game** screen with the
    **live lobby catalog**: `> SERVER: P871b Proof Host`, `CORE: PCore001.igc`,
    `MAP: m2xspiderw.igc`, the `GAME NAME` row and the `CREATE GAME` row, plus the
    key hints. (The intro screens are style-independent — the two PNGs are
    byte-identical — so the pair is shown for completeness; the *chat* pair below
    is where the styles differ.)
  - **Directory chat, Classic**: `.scratch/p871b/shots/directory-chat-classic.png`
    (also kept as `chat-classic/proof_t024.0_sOmega_ccockpit.png`) — the Classic
    cockpit HUD with its chat pane showing two real relayed lines
    (`ProofChatter: Wingman: anyone up for a bomber run on …`,
    `ProofChatter: Sal: Enemy fighters inbound to our home s…`), seeded into the
    **real lobby's** directory store and posted through the **real
    `/api/chat`** endpoint with a signed-in session.
  - **Directory chat, Modern**: `.scratch/p871b/shots/directory-chat-modern.png`
    (also `chat-modern/proof_t024.0_sOmega_ccockpit.png`) — the same two lines in
    the Modern HUD's chat pane.
  - What is actually visible was checked with the read tool (not by pixel
    counting): the wizard's five rows and its catalog values; the two chat lines
    at the top-centre pane in both styles.

## 6.3 Open questions / what is left

1. **Directory chat is a port extension, not a parity behaviour** (native has
   none — `Igc/igc.h:2731-2734`). The row is `n/a` with that reason; if Domingo
   wants it treated as a product feature with its own matrix, it needs a
   non-native "extension" area rather than `lobby::`.
2. **A player-created mission becomes the server's join target (slot 0).** A
   native server hosts one mission; this port hosts several, so the wizard's world
   is inserted in front of the configured ones (they keep ticking and keep their
   config entries). If the operator wants a dedicated "one configured world per
   process" policy instead, the provisioner's insert is the one place to change.
3. **The claim path needs a lobby heartbeat.** A server started without
   `Lobby` config never polls `/api/games`, so the wizard's request stays queued
   (exactly like native with no lobby). No new config key was added.
4. **Real-window proof of a *full* provisioned game** (client creates → server
   provisions → client lands in it) was not captured as a windowed run: the
   wizard's CREATE row needs an authenticated session, which the automated client
   cannot obtain (the identity flow is a passkey/device approval). The chain is
   covered end to end in `P871bCreateGameProvisioningLiveTests` + the client's
   join-on-ACK test; the windowed frames prove the wizard UI and the directory
   chat.
5. **`NpcWingmanDefendLiveTests.Defender_disengages…`** remains red at base
   (pre-existing, ledgered); it is the only non-green test in the fast tier.

## 6.4 Native-intent classification (P871b)

- Every P871b item is **B** (executable/observable native behaviour) except the
  two **C** product decisions already resolved in P871 (voice chat = native
  prerecorded quickcomms → `done`; ICQ/Steam = retired → `n/a`).
- **No new n/a claim** was made to avoid work: the directory-chat `n/a` records
  that *native has no such behaviour* (with the source evidence), not that the
  port declined to implement it — the port ships the extension anyway.
- No row was closed with "the port chose differently" or "same observable
  outcome".

## 6.5 Modern / better-approach note (P871b)

- **The native struct crosses the wire as itself.** Rather than teaching a JSON
  serializer to emit C-style fields, the mission parameters travel as base64 of
  the packed 256-byte `MissionParams` the fedmsg codec already owns — AOT-safe, no
  reflection, and it removes a global serializer flag that silently changed every
  other endpoint's contract.
- **One comment stripper, shared.** Both native-table generators now import
  `scripts/native_cpp_comments.py`, so the "preprocessor or bust" rule lives in
  one tested place instead of two subtly different regexes.
- **Provisioning is a pure mapping plus one mutation point.** `GameConfig`
  synthesis (`PlayerGameProvisioning`) is a static, unit-testable function of
  (claim, params, server core); the only side effects are "insert the mission at
  slot 0" and "mark it player-created", which keeps the sim-thread contract
  unchanged (one mission per tick, no cross-thread state).
- **The claim poll rides the heartbeat that already exists** (same HttpClient,
  same token, same failure policy: log and retry next tick) — no new service, no
  new port, no new credential, `docker-compose.yml` untouched.
- **Cockpit audio is a two-flag model now.** Splitting `CockpitView` from
  `IsGunner` makes the native rules explicit and testable without a device, and it
  removed a whole class of "which loop does this key on?" bugs the P871 rewrite
  introduced.
