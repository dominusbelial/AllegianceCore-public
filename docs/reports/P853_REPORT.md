# P853 — live host editing: FM_CS_MISSIONPARAMS, FM_CS_SET_TEAM_INFO, SetSideName (server + client session/state; UI is P853b)

Lane: Pi (glm, 2026-10-28). Base: public master `a85dad5b`. Rules file: `~/.claude/jobs/82c1509b/tmp/pi-lane-rules.md`.
No commit/push — all changes left in the worktree.

## What shipped

### 1. Codecs (`src/Allegiance.Core/Wire/`)

- **`MissionParamsMsg.cs` (new)** — `FM_CS_MISSIONPARAMS(113)`: header(4) + the whole native `MissionParams`
  struct = **256 bytes**, wire-exact. The struct layout was reconstructed from `Igc/igc.h:1109-1251` and
  cross-checked against the P29 MSVC wire probe placements the `MissionDef` codec already carries
  (`szIGCStaticFile` +65, `verIGCcore` +120, `timeStart` +156, `rgCivID` +168, `iLives` +204, `nTeams` +218 — all
  confirmed on the wire by P29). The 36 `bool:1` flags sit at params+108 (5 bytes, MSVC LSB-first); the
  `bAllowAlliedViz`=6 / `bLockGameOpen`=18 / `bAllowFriendlyFire`=28 indices agree with the established P761/P856
  mapping. **Correction of a recorded finding:** `NotSupportedFedMsgs` claimed "MissionParams has NO declaration
  anywhere in this native checkout" — that is wrong; the struct declares at `Igc/igc.h:1109`. The codec also ports
  `MissionParams::Invalid(bool)` (`igc.h:1339-1506`) including the client leg (max-players 1..100, winning
  conditions, the KGJV #62 admin-flag hack check) and the common leg (asteroid/treasure counts and rates, string
  NUL checks, teams 2..6, rank/count windows, conquest/invulnerable-stations and prosperity/developments
  conflicts, territory-map restrictions), ending in the only two non-trivial map-maker rules:
  DiamondRing needs >2 teams (`mapmakerigc.cpp:1930-1937`), EastWest exactly 2 (`:2416-2427`); every other map
  maker's `IsValid` is `NULL` (`mapmakerigc.h:195-198,265-268,285-288,310-313,335-338,360-363,387-390,496-499,
  545-548`) and Brawl/PinWheel/LargeSplit checks are commented out in native
  (`mapmakerigc.cpp:1695-1704,1810-1818,2243-2251`).
- **`MissionDef.cs`** — the advertisement now carries the params tail the C→S echo guards compare against
  (`iMinRank` +220, `iMaxRank` +222, `nMinPlayersPerTeam` +246, `nMaxPlayersPerTeam` +247,
  `nTotalMaxPlayersPerGame` +250); encode/decode round-trip, wire width unchanged (864).
- **`SetTeamInfo.cs`** — unchanged; already wire-exact in both directions (id 15).
- **`Side.cs`** — `Name` is now settable only through the new `SetName` (the `IsideIGC::SetName` port).
- **`Model/Mission.cs`** — `AddInvitation` performs native's squad-game side rename
  (`fsside.cpp:78` → `SetSideName(szSubjectName)`) at the model level: SIM-0491's remaining branch.

### 2. Server (`src/Allegiance.Server/`)

- **`Server.MissionParams.cs` (new)** —
  - `ParamsOf` / `_missionParams`: the per-mission stand-in for native `m_misdef.misparms`, seeded from the game
    config + assembled mission, mutated only by an accepted MISSIONPARAMS (pruned on rotation).
  - `HandleMissionParams` (`FedSrv.CPP:7518-7597`, every guard in native order): owner-only
    (`IsMissionOwnerPlayer`, the port's `GetOwner()` — FedSrv.CPP:7529-7531); `Invalid(true)` TrapHackBoot
    (:7534); the `bObjectModelCreated` server override (:7536-7537); the standalone `bClubGame == false` trap
    (:7540-7541); the two player-immutable equality traps — `bLockGameOpen` (:7545) and
    `nTotalMaxPlayersPerGame` (:7547); the locked-game team-size rule (:7550-7557); the `STAGE_NOTSTARTED` gate
    with native's silent-ignore (:7560, 7593-7595); then `SetMissionParams`, the whole-mission
    `MISSIONDEF(38)` re-advertisement (:7577) and the lobby refresh (:7579 — the port's `_liveGameParams`
    rebuilds from the mission every loop tick, Server.Broadcast.cs:201).
  - `SetMissionParams` (`fsmission.cpp:1796-1895`): IGC-static fields preserved (:1803-1806), club flag forced
    (:1809-1817), nTeams shrink boots each destroyed side's players (`QSR_SideDestroyed`, :1821-1835) and marks
    the side inactive (`SIDE_INACTIVE(68)`), squad-game flip maintained/cleared (:1885-1893 via
    `MaintainSquadLeadership`/`ClearSideSquad`), goals/lives/flags applied to the Mission (the lobby
    advertisement + briefing read these), alliance revalidation (:1861), ready recheck (:1864-1869,
    `RecomputeSideReady`), rank-window boots (`QSR_RankLimits`, :1871-1890).
  - `HandleSetTeamInfo` (`FedSrv.CPP:7430-7467`): NOTSTARTED + `!bLockTeamSettings` gate (:7433-7436, silent);
    the four TrapHackBoots — sideID ≥ 0 (:7439), sideID < c_cSidesMax (:7440), SideName NUL-terminated inside
    c_cbName (:7441), sender member of the named squad (:7442; `GetIsMemberOfSquad(NA)` is true,
    `fsship.cpp:1359-1363`); the leadership race check (:7445-7448); then the squad-game re-point
    (`GetCanLeadSquad || side squad` trap :7455 → `ApplySideSquad`) or the non-squad rename (`squadID == NA` →
    `SetSideName`, :7461).
  - `SetSideName` (`fsmission.cpp:1564-1600`): THE one rename primitive — truncate to c_cbName−1 = 24,
    `Side.SetName`, broadcast `SET_TEAM_INFO(15)` with the side's current squad id and the new real name to the
    whole mission. Used by the handler, the invitation load and future admin paths.
- **`Server.Dispatch.cs`** — two new branches (after CHANGE_ALLIANCE): `SetTeamInfo.Fmid` and
  `MissionParamsMsg.Fmid`, per-message exception barriers like every sibling.
- **`InboundPacket.cs`** — AOT-safe decode entries for both fmids.
- **`Server.Lifecycle.cs`** — after `LoadInvitations`, a squad game's renamed (invited-squad) sides reach every
  joining peer through `SendSideRoster`'s `SET_TEAM_INFO` seed (no relay at assembly itself — the mission is not
  yet registered and no peer is seated; see the gate-run note below); `Server.Broadcast.cs` — a `missionParamsPrune` loop step drops rotated missions'
  snapshots; **`Server.BroadcastTestSeams.cs`** — `TestProcessDatagram` (raw dispatch entry) and `TestParamsOf`.

### 3. Client session/state (no new screens — UI is P853b)

- **`ConnectionSession.cs`** — `SendSetTeamInfo(sideId, squadId, sideName)` and
  `SendMissionParams(MissionParamsMsg)` (the P802 `SendChangeTeamCiv` pattern).
- **`ClientWorld.cs`** — the S→C rename echo: a `SET_TEAM_INFO` with `squadID == NA` and a non-empty
  `SideName` now records the side's live name (`_sideNameBySideId`, `SideNameOverride(sideId)`), exactly what
  native's client does with `appmsg.cpp:2541-2550` (the P820c "we do not rename" divergence is retired; the
  squad-name slot is unchanged). Cleared on reconnect/mission reset. Mission-params echoes already apply via the
  existing `MISSIONDEF(38)` → `MissionInfo` path (`Apply(MissionDef)`), so the re-advertised goals/lives/rank
  window reach the existing panes with no new code.
- **`TeamPaneLayout.cs` / `Game1.Rendering.cs`** — the existing team pane's side header now shows the live side
  name when the server renamed it (falls back to `SIDE n`). One shared layout path — the pane is the same in
  Classic and Modern (no style-specific composition on this line).

### 4. Parity rows (`docs/parity/sim-rules-matrix.csv`)

SIM-0491, SIM-0508, SIM-0513, SIM-0625, SIM-0628 → `implemented`, with port cites, evidence tests and notes.
`python3 scripts/parity-scorecard.py` validates (production 639/671 eligible implemented, +2; test-only 6/6, +3).

## Native-intent classification

- SIM-0628 / SIM-0625 / SIM-0508: **intended-feature** — real, load-bearing host-editing paths in native
  (`FedSrv.CPP:7518-7597`, `:7430-7467`; `fsmission.cpp:1564-1600`) that the port had stubbed behind the
  unsupported-fmid refusal. Now ported with full guard parity.
- SIM-0513: **intended-feature, partially bound** — scalar settings, shrink, squad flip, ready recheck, alliance
  revalidation and rank boots are full parity; two documented divergences below.
- SIM-0491 close-out: **intended-feature** — the `fsside.cpp:78` rename branch.

## Deliberate divergences (both recorded in the CSV notes)

1. **nTeams growth is clamped** to the assembled side count (logged, not booted). Native grows the mission with
   `InitSide` (`fsmission.cpp:1846-1852`); this port's `Mission.Sides` is fixed at assembly
   (`Mission.cs:37-38`), so a live grow would need re-assembly. Shrink is full parity (boot + SIDE_INACTIVE +
   MISSIONDEF count).
2. **Rank boots spare nobody**: native exempts `CanCheat()` / `UTL::PrivilegedUser` players
   (`fsmission.cpp:1885-1886`); the port has no per-peer cheat flag. Practically moot: the default window
   (-1..1000) boots nobody, and the seeded advertisement carries those defaults.

## Modern / better-approach note

The native design lets the client send the whole 252-byte `MissionParams` blob and the server TrapHackBoot-prints
any mismatch with its own def — an echo-trust scheme the port must keep for wire parity, but a port-native API
would send only the *mutable* field set (a diff, not the struct) and would model `bLockGameOpen` /
`nTotalMaxPlayersPerGame` in `GameConfig` so the advertisement and the guards read one source of truth (today the
port advertises the native `Reset` defaults for those two). If P853b adds the game-settings form, prefer
server-resolved validation errors (the `Invalid` string) relayed back to the client over re-implementing
`Invalid(bool)` client-side again.

## Verification (light gate)

- Whole-solution build `dotnet build AllegianceCore.slnx`: **0 errors / 0 warnings**.
- `tools/build-tools.sh`: **all 90 tool projects built**.
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained
  -p:PublishAot=true`): completed with **0 IL2026 / IL3050** (explicit AOT-safe `InboundPacket` dispatch; no
  reflection serialization added).
- New tests (`P853MissionParamsWireTests` — 12, `P853HostEditServerTests` — 8): **20/20, run 3× alone: pass,
  pass, pass** (~0.3 s). Red-run proof: with the two new dispatch branches disabled (fmid diverted to the
  unknown-fmid refusal), the 8 server-effect tests fail exactly as native intent requires (no MISSIONDEF
  relay, no rename, no squad re-point, no shrink boot); the refusal guards pass vacuously against master because
  master refuses these fmids wholesale — the fix narrows acceptance to exactly native's guard set, and the
  effect tests pin each guard's acceptance path. Codec/model tests did not compile against master (the types did
  not exist) — the P854 convention for wholly new members.
- `python3 scripts/parity-scorecard.py`: validates.
## Gate run

First `tools/run-tests.sh --tier fast` run caught two of my own breakages (this is what the gate is for):

1. `P854PrivateGameInvitationServerGateTests.PerSideGate_squad_mode_matches_identity_squads` — NRE: my
   AssembleGameMission relay called `GameSettings(mission)` before the mission is registered in `_missions`.
   Fixed by dropping the at-assembly relay entirely: no peer is seated at assembly, and every joining peer is
   seeded with the renamed side by `SendSideRoster`'s `SET_TEAM_INFO` relay (which carries the renamed
   `SideName` with `squadID = NA`). The SIM-0491 CSV note was amended to match.
2. `MissionDefJoinedMissionWireTests.MissionDef_PlacesKeyFieldsAtCompilerConfirmedOffsets_AndZerosTheRest` — the
   historical "zeros the rest" byte-proof now whitelists the P853 params-tail cells (iMinRank@224 = -1,
   iMaxRank@226 = 1000, nMin/nMaxPlayersPerTeam@250-251 = 1/100, nTotalMaxPlayersPerGame@254 = 0x7FFF — the
   native `Reset` defaults, igc.h:1301-1302,1318-1320), which are deliberately no longer zeros.

Both affected tests re-run 3× alone after the fix (green: 35/35 three runs).

Final gate results (after the fix):
- `tools/run-tests.sh --tier fast`: **PASS — 11228 tests** (fast/main ClientCore 1913 passed, Core 5575 passed;
  quarantine 12 passed; `TestResults/tiers-20261028T042709.496470Z/summary.json`).
- Showcase never-parks `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_
  relaunch` **5×: pass, pass, pass, pass, pass** (3m12s–4m05s each; no parking).
- Whole-solution build re-checked after the fix: **0 errors / 0 warnings**; `parity-scorecard.py` validates.

## P853b handoff — the native UI this slice deliberately did not build

- **Team-name edit box**: native `TeamSettingsDialogPopup` (`WinTrek/teamscreen.cpp:856-1104`) — the
  `teamNameEditPane` edit control (found :887, `SetMaxLength(c_cbName - 1)` :897), the `squadComboPane` and
  `civComboPane` combos (:888-890), and `OnButtonOk` (:1044-1075) which sends `FM_CS_SET_TEAM_INFO` with
  `squadID` (squad game) or `NA` + the typed `SideName` (non-squad game). The port equivalent is a small edit
  field + squad picker on the existing team/lobby pane, shown to the side's leader only, sending
  `ConnectionSession.SendSetTeamInfo`.
- **Game-settings form**: native `NewGameScreen` (`WinTrek/newgamescreen.cpp:11+`) — the full create-game form
  (game name, map type, teams, max players, rank window, win conditions, option flags) whose `OnButtonCreate`
  (:1034-1130) runs client-side `Invalid(true)` (:1040-1041) and queues `FM_CS_MISSIONPARAMS`. The port
  equivalent is an owner-only form on the pre-game lobby sending `SendMissionParams`; the server's
  `MissionParamsMsg.Invalid(true)` string can be surfaced directly as the form's validation message.
- **Echo application** the UI will read: side names via `ClientWorld.SideNameOverride(sideId)`, params via
  `ClientWorld.Mission` (the `MISSIONDEF` → `MissionInfo` snapshot).

## Unfinished / out of scope

- nTeams growth (divergence 1) — would need a live `InitSide` analogue; P853b+ if host-editing UI needs it.
- No new UI (P853b, needs a vision lane) — the client send methods + state are wired and tested at the
  session/state level only.
- The `bLockGameOpen`/`nTotalMaxPlayersPerGame` config flags are still not modeled in `GameConfig` (the guards
  compare against the native `Reset` defaults the advertisement carries); adding them is a config-schema change
  beyond this slice.
