# P853b — host-editing UI: the team-name edit (side leader) and the game-settings form (mission owner)

Lane: **Pi**, base **public master `6abac0ea`** (the P853 merge), worktree
`allegiance-pi-p853b`. Rules: `~/.claude/jobs/82c1509b/tmp/pi-lane-rules.md`.
**Nothing is committed or pushed** — every change is left in the working tree.
No remote host was contacted; the native checkout (`~/Development/Allegiance/src/`) was
read-only and **every line number below was re-grepped this session** (two of the
brief's own cites were off by 2–9 lines: the `SetMaxLength(c_cbName - 1)` is
`teamscreen.cpp:895`, not `:897`; `OnButtonOk` is `:1035-1082`, not `:1044-1075`).

## 1. Result

P853 made host editing work on the wire and left the UI to this slice. Both
affordances now exist, on the pre-game **team lobby** (the ready-check pane the
port shows after seating), gated exactly where native gates them:

| Affordance | Who sees it | Gate | Sends |
|---|---|---|---|
| **TEAM NAME** edit (key **T**) | the side's own team leader | `IsTeamLeader`, `STAGE_NOTSTARTED`, `!bLockTeamSettings`, not a squad game | `FM_CS_SET_TEAM_INFO(15)` with `squadID = NA` + the trimmed ≤24-char name |
| **GAME SETTINGS** form (key **G**) | the mission owner | `AmMissionOwner`, `STAGE_NOTSTARTED` | `FM_CS_MISSIONPARAMS(113)`, only after the shared `MissionParamsMsg.Invalid(true)` returns null |

Everything else lives where the port already puts it: the merged side name shows in
the lobby header and in the `TEAM NAME:` row (P853's `SideNameOverride`), and the
params echo lands in `ClientWorld.Mission` (the existing `MISSIONDEF(38)` path).

```
READY CHECK                                  GAME SETTINGS
  TEAM Alpha  READY: --                        > TEAMS                3
  ...                                          MAX PLAYERS/TEAM     100
  TEAM NAME: Alpha                             MIN RANK             NONE
  T  CHANGE TEAM NAME                          MAX RANK             1000
  G  GAME SETTINGS (MISSION OWNER)              LIVES                  4
                                               CONQUEST GOAL        100%
  (T) TEAM NAME: Blue Squad_                    TERRITORY GOAL       OFF
      TYPE A NAME (MAX 24)  ENTER APPLY         ... 18 rows ...
      ESC CANCEL  BACKSPACE DELETE              FRIENDLY FIRE         NO
      Type a team name first.        <- error  ALLIED RIP            NO
                                               SQUAD GAME            NO
                                               You must choose one or more
                                               winning conditions.   <- error
```

## 2. Native rules, re-grepped, and where the port puts them

| Native | Rule | Port |
|---|---|---|
| `WinTrek/teamscreen.cpp:1592-1593` | the team-settings button is **hidden** unless you lead the viewed side (`!IsTeamLeader() \|\| m_sideCurrent != GetSideID()`) | `State/TeamNameEdit.CanEdit` requires `ClientWorld.IsTeamLeader` + own side; non-leaders see the name read-only, no hint |
| `teamscreen.cpp:1594` | the button is **disabled** while `bLockTeamSettings` | `CanEdit` requires `!MissionInfo.LockTeamSettings` (from the params bit); a leader sees “TEAM SETTINGS LOCKED” instead of a dead click |
| `teamscreen.cpp:960-965` | in a squad game the name edit is hidden in favour of the squad combo | `CanEdit` requires `!SquadGame`; the row reads “SQUAD GAME - NAME COMES FROM THE SQUAD” |
| `teamscreen.cpp:895` | `SetMaxLength(c_cbName - 1)` = **24** | `TeamNameEdit.MaxLength = SetTeamInfo.SideNameCapacity - 1`; `OnTextInput` stops at 24; `Sanitize` trims + truncates (the server truncates identically, `fsmission.cpp:1576-1577`) |
| `teamscreen.cpp:967-973` | the edit box opens pre-filled with the side's current name (`UpdateNameAndSquad`) | the T key pre-fills the buffer with `TeamNameEdit.CurrentName` (rename echo → roster name → `SIDE n`) |
| `teamscreen.cpp:1012-1029` | `OnKey`/`OnChar`: the edit control owns typed characters; Enter = OK | `Game1.Session.OnTextInput` (the same `Window.TextInput` path the chat line and lobby filter use) + `HandleTeamNameEditKeys` |
| `teamscreen.cpp:1035-1082` | `OnButtonOk`: closes, then sends `FM_CS_SET_TEAM_INFO` with `squadID = NA` + `strcpy(SideName, edit->GetString())` in a non-squad game (`:1049-1058`) | `TeamNameEdit.TryPlan` → `(SideId, SquadId = -1, sanitised Name)`; Game1 calls `ConnectionSession.SendSetTeamInfo` |
| `teamscreen.cpp:1085-1093` | `OnButtonCancel` discards | Esc closes the edit, buffer dropped |
| `teamscreen.cpp:935-936,940-951` | the dialog is dismissed when team settings lock or the player leaves the side | the handler closes the pane the frame `CanEdit` goes false (the same check runs in `DrawReadyCheck`) |
| `teamscreen.cpp:1674-1683` | the dialog closes when the mission starts | the ReadyCheck→Flying handoff clears both panes and their drafts |
| `FedSrv/FedSrv.CPP:7430-7436` | the server accepts the rename only at `STAGE_NOTSTARTED` with `!bLockTeamSettings` (silently otherwise) | the client never sends outside that window — the brief's “hide/disable instead of letting the click vanish” |
| `newgamescreen.cpp:960-1032` | `ReadControls`: the whole `MissionParams` is re-read from the controls, starting from `GetBaseMissionParams()` | `State/MissionSettingsForm` starts from a **clone of the advertised params** (`MissionParamsMsg.Clone`) and edits only the modelled rows |
| `newgamescreen.cpp:1034-1041` | `OnButtonCreate` (`:1034`) → `Invalid(true)` first (`:1041`) | `MissionSettingsForm.Validate()` = `Draft.Invalid(bFromClient: true)`; an invalid draft is never sent |
| `newgamescreen.cpp:1092-1107` | a shrink that leaves every surviving team inactive is refused | same check, from `ClientWorld.IsSideActive` (`CaptureSideActivity`) |
| `newgamescreen.cpp:1117-1120` | the refusal string goes into a message box and **nothing is queued** | the string is drawn as the form's red error row; `TryBuildPlan` returns false → no `SendMissionParams` |
| `newgamescreen.cpp:1124-1125` | a valid form queues `FM_CS_MISSIONPARAMS` | `ConnectionSession.SendMissionParams(plan)` |
| `artwork/newgamescreen.mdl:283-353` | the combo value lists (teams/lives/goals/length/prosperity/kills/…) | the same lists in `MissionSettingsForm` (`TeamCountValues = 2..6`, `LivesValues = 32767,0,1,2,3,4,9`, `CountdownTimeValues` minutes ×60, …) |
| `teamscreen.cpp:1586-1589` | the settings button is owner-only, pre-start (`bCanChangeSettings`) | `MissionSettingsForm.CanEdit` = `AmMissionOwner && Stage == NotStarted && Mission.Params != null`; non-owners see no hint at all |
| `FedSrv.CPP:7518-7597` | the server validates with `Invalid(true)` (`:7534`, **TrapHackBoot**) and compares `bLockGameOpen` (`:7545`) / `nTotalMaxPlayersPerGame` (`:7547`) with its own def | the draft is a faithful echo of the advertisement for exactly those fields, so the guards are satisfied by construction (asserted in two tests) |

## 3. The one Core/Server change (a proven gap, not a new feature)

The form cannot work from the port's advertisement as it stood: `MISSIONDEF(38)`
carries the **whole** `MissionParams` on the wire (native's client memcpy's it out
of the misdef — `appmsg.cpp:2416` → `clintlib.cpp:670-673` — and re-sends it with
only the user's edits, which is why `SetMissionParams` adopts everything else
wholesale, `fsmission.cpp:1841`), but the port's codec decoded **3 of 36** flags
and a handful of scalars, and the server encoded only those. A form built on that
would have to send zeros for treasure rates, He3 density, starting money, the
other 33 `bool:1` flags and `rgCivID` — and the server would adopt them
(`fsmission.cpp:1841`), so simply opening the form would silently zero game
settings. That is exactly the kind of data loss P853's own report warns about
(“the advertisement must be truthful about them”).

So, minimal and additive (no wire change — the bytes were already on the wire):

- `Core/Wire/MissionParamsMsg`: `DecodeParams(252-byte block)` (the MISSIONDEF-
  embedded form, reusing the one field decoder) and `Clone()`.
- `Core/Wire/MissionDef`: a `Params` field. Decode fills it from the embedded
  block; encode stamps it over the params region when set (the typed fields stay
  as a compatibility view, and the belt-and-braces placement checks now validate
  against whichever source was authoritative).
- `Server`: `ParamsOf` also carries the IGC-static identity (`szIGCStaticFile`)
  and the native Reset `iGoalArtifactsPercentage = 100` (so the advertisement's
  byte is unchanged), and all three `MISSIONDEF` constructions stamp
  `Params = ParamsOf(mission)` (the two join-burst ones included — that is the
  message the client actually learns the mission from).
- `ClientCore`: `MissionInfo.Params` (+ `SquadGame` / `LockTeamSettings`
  accessors) populated in `ClientWorld.Apply(MissionDef)` and carried through the
  `MISSION_STAGE` refinement.

One adjacent **client-state bug** fixed while there (it would have made the
owner's LOCK LOBBY / AUTO-ACCEPT controls lie): `ClientWorld.Apply(MissionStageMessage)`
dropped `LockGameOpen`, so every stage broadcast un-locked a locked game in the
UI. It now survives the refinement like its two sibling option flags.

## 4. Deliberate divergences (all small, all documented in code)

1. **Empty team name is refused client-side** (“Type a team name first.”). Native
   would send the empty string; P853's `SideNameOverride` ignores empty names, so
   the side would just become unnameable in the pane. The server is unchanged.
2. **No squad picker**: in a squad game the name edit is hidden (native does the
   same, `teamscreen.cpp:962`) but the port has no client-side squad-membership
   list to fill native's `squadComboPane` (`:996-1005`), so it must not send a
   squad re-point it cannot represent. Read-only squad name is shown instead.
3. **TEAMS is capped at the assembled side count** (`SIDE_ROSTER` length): P853
   clamps a live grow server-side (its recorded divergence 1, `InitSide` needs a
   re-assembly here), so the control cannot offer a number the server will
   quietly reduce.
4. **Two native create-game pre-checks are not re-implemented client-side**: the
   owner's own rank-window test (`newgamescreen.cpp:1043-1053`) and the
   squad-leader test for `bSquadGame` (`:1054-1073`). The client has no
   authoritative persist-rank for the local pilot on this path and no squad
   membership list; the server owns the outcome. `Invalid(true)` (the one check
   the brief requires) runs in full.
5. **Rank rows step numerically** (−1…1000, with `NONE` for −1) instead of native's
   zone-club skill combo, whose values are server-populated.
6. **The form's rows replace the ready-check body** while open (native's settings
   screen is a whole screen); 18 rows plus the roster would not fit 720p.

## 5. Behaviour in both HUD styles

Both affordances work and look right in **Classic** and **Modern**. Honest
observation, verified by opening the pairs: **the two captures are
byte-identical** (`md5sum` equal). The ready-check/team-lobby overlay is one
shared panel (`ReadyCheckOverlayRenderer` with `_hudSmall`/`_hudBig`, the same
fonts in both skins) — `HudStyle` swaps the *in-flight* HUD renderer and its
artwork/typography, not this lobby pane. Behaviour lives in the shared models
(`TeamNameEdit`, `MissionSettingsForm`) and the layout files, so both styles show
identical rows by construction; the P856 precedent reported the same finding for
the server browser and skirmish setup. Nothing in this slice touched the in-flight
HUD/team pane, and the P730 showcase run (which flies both skins) is clean.

## 6. Verification (light gate)

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 errors / 0 warnings** (re-checked after the last edit) |
| `tools/build-tools.sh` | **91/91 tool projects built** (the new `p853b-hostedit-proof` included) |
| AOT publish (`Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | completed, **0 IL2026 / 0 IL3050** (required: Core + Server changed) |
| New tests `P853bHostEditUiTests` (13) **3× alone** | **13/13, 13/13, 13/13** (`P853B_assets/new-tests-3x.txt`) |
| Affected tests (`P853*` + `MissionDef*`, Core.Tests, 36) **3× alone** | **36/36 ×3** (`P853B_assets/affected-core-tests-3x.txt`) |
| ONE `tools/run-tests.sh --tier fast` | 11255 tests; **one failure: the ledgered P660 next-match load flake** (“loopback condition timed out”, `Pump:49` via `:201`); **3/3 alone afterwards** → load flake, `docs/TEST_FLAKES.md` 2026-10-28, load average 4.47 (`P853B_assets/fast-tier-run1-tail.txt`) |
| Showcase never-parks **5×** (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`) | **pass, pass, pass, pass, pass** (4m20s / 3m59s / 4m31s / 4m07s / 4m01s, no parking) — `P853B_assets/showcase-never-parks-5x.txt` |
| `python3 scripts/parity-scorecard.py` | validates (production 640/671 eligible implemented; test-only 6/6) — no CSV row changed, the ported rules are the already-tracked SIM-0491/0508/0513/0625/0628 |
| `python3 tools/check-glossary-links.py` | validates (GLOSSARY.md has the new “Host editing (P853b)” entry) |
| Raw evidence | `P853B_assets/verification.json`, full TRX/logs under `TestResults/tiers-20261028T131719…` and `…T132446…` (repo test output, outside the report assets) |

The gate earned its keep — the **first** fast-tier run caught two of my own
regressions, both fixed and re-verified:

1. `P625bActionMapGuardTests` — my raw `XnaKeys.Back` in `HandleTeamNameEditKeys`
   (the guard forbids raw keys outside its allow-list). Fixed by adding the
   documented *text entry* allow-list entry (native's edit pane owns `VK_BACK`,
   `teamscreen.cpp:1012-1029`); the guard then passes 3×.
2. `PackagingManifestTests` — my `artwork/newgamescreen.mdl` citation in a scanned
   `ClientCore.Game` file made `packaging/generate-manifest.py` pull two `.mdl`
   roots into the artwork manifest. Fixed by keeping that citation in the
   non-scanned model file; regenerating the manifest now produces **no diff**.

## 7. Live proof (this host: real server + windowed client)

Two independent runs, both on this machine with the real MonoGame window (no
remote host, no rig):

- `scripts/live-proof.sh --port 6187 --autoshots 8,14 --exit-after 22 --window 1280x720`
  — real `Allegiance.Server` + the windowed client over loopback: exited cleanly,
  **2 real backbuffer PNGs** produced (in-flight, cockpit). No regression from the
  changes (the client still seats, readies, launches and renders).
- `tools/p853b-hostedit-proof <out> classic|modern` — the same topology (real
  `Server` instance, real windowed `Game1` over loopback, real backbuffer PNGs via
  `SaveAsPng`), seated as side 0's leader **and** mission owner, mission held at
  `STAGE_NOTSTARTED`. It drives the *production* handlers (`UpdateReadyCheck` with
  synthetic keyboard states, `OnTextInput` with real `TextInputEventArgs`) and
  reads the result back from the **server**:

```
[p853b] start: side name 'Alpha', lives 3, teams 4
[monogame] ready-check: TEAM NAME edit OPEN ('Alpha')
[monogame] ready-check: TEAM NAME edit refused: Type a team name first.
[teaminfo] side 0 renamed 'Blue Squad' (squad NA): SET_TEAM_INFO(15) whole-mission to 1 peer(s)
[p853b] rename echo: side 0 'Alpha' -> 'Blue Squad'
[monogame] ready-check: GAME SETTINGS open (owner, STAGE_NOTSTARTED) — teams 4/4, lives 3, maxPerTeam 100
[missionparams] side 3 destroyed by nTeams shrink -> SIDE_INACTIVE(68)
[missionparams] ship 8096: mission params updated (nTeams 4, maxPerTeam 100, rank -1..1000)
[p853b] params echo: lives 3 -> 4, teams 4 -> 3
[p853b] SERVER state: lives=4 params.nTeams=3 sides=[0:Blue Squad, 1:Bravo, 2:Charlie, 3:Delta(inactive)]
[monogame] ready-check: GAME SETTINGS refused: You must choose one or more winning conditions. (nothing sent)
```

The rename echo is visible **in the pane that issued it** (header `TEAM Blue
Squad` + `TEAM NAME: Blue Squad`) and in the authoritative server state; P853's
in-flight team-pane header reads the same `SideNameOverride` and is covered by
P853's own tests.

## 8. Screenshots (opened and described one by one)

`docs/reports/P853B_assets/` (16 PNGs + 4 text/JSON evidence files, 1.2 MB total), 1280×720, 60–87 KB each (< 1 MB). Every claim below
is what the pixels actually show.

**Team name edit — `p853b-teams-closed-{classic,modern}.png` (identical frames):**
the READY CHECK panel with `TEAM Alpha  READY: --`, the same-side roster
(`[WAIT ] you`), `TEAM CIV: Dreghklar`, the four lobby control lines
(`[ ] TEAM READY  O` … `[ ] LOCK SIDES  H`), then the new rows
**`TEAM NAME: Alpha`**, **`T  CHANGE TEAM NAME`** and
**`G  GAME SETTINGS (MISSION OWNER)`**; the TEAM CHAT pane sits bottom-left.

**`p853b-rename-open-{...}.png`:** the same panel with the name row replaced by a
highlighted edit line **`TEAM NAME: Alpha_`** (caret) plus the hint
`TYPE A NAME (MAX 24)   ENTER APPLY   ESC CANCEL   BACKSPACE DELETE`.

**`p853b-rename-error-{...}.png`:** the box emptied (`TEAM NAME: _`) and the
refusal drawn in the new error colour under the hint: **`Type a team name
first.`** — the pane stays open, nothing was sent.

**`p853b-rename-applied-{...}.png`:** the edit closed and the server's echo
visible twice — header **`TEAM Blue Squad  READY: --`** and
**`TEAM NAME: Blue Squad`**.

**Game settings — `p853b-settings-open-{...}.png`:** the whole 18-row form with
values taken from the server's advertisement: `> TEAMS  4 (MAX)`,
`MAX PLAYERS/TEAM 100`, `MIN RANK NONE`, `MAX RANK 1000`, `LIVES 3`,
`CONQUEST GOAL 100%`, `TERRITORY GOAL OFF`, `FLAGS/ARTIFACTS/KILLS TO WIN 0`,
`PROSPERITY TARGET OFF`, `GAME LENGTH OFF`, `FRIENDLY FIRE NO`,
`ALLIED VISIBILITY NO`, `ALLIED RIP NO`, `ALLOW EMPTY TEAMS YES`,
`DEVELOPMENTS YES`, `SQUAD GAME NO`, with the hint row
`UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER SEND   G CLOSE`.

**`p853b-settings-edited-{...}.png`:** the draft after the key-driven edit —
**`> TEAMS  3`** and **`LIVES  4`** — i.e. the state that is about to be sent.

**`p853b-settings-applied-{...}.png`:** the form reopened after the send with the
cursor parked on `LIVES`: **`TEAMS 3`**, **`> LIVES 4`** — the values the server
echoed back in the re-advertised `MISSIONDEF` (verified against the server's own
mission in the log line above).

**`p853b-settings-error-{...}.png`:** `> CONQUEST GOAL  0%` with the red
`You must choose one or more winning conditions.` under the list — native's
`Invalid(true)` string, drawn instead of sent.

## 9. Native-intent classification

- **Team-name edit (side leader)** — *intended-feature*, full parity: native's
  team-settings dialog is a real host-editing path (`teamscreen.cpp:860-1092`,
  `FedSrv.CPP:7430-7467`) and the port now offers it with the same audience, the
  same length cap, the same message and the same pre-start/unlocked gate.
- **Game-settings form (mission owner)** — *intended-feature*, parity on the
  fields the port models, with the recorded limits: the live-grow clamp (P853's
  divergence, hence the capped TEAMS control), the two native client pre-checks
  that need server-only data, and the missing client squad-membership list.
- **Whole-`MissionParams` advertisement** — *port bug fixed*, not a new feature:
  native forwards and re-reads the entire struct; the port was decoding a third
  of it, which made the owner's form unsafe to use at all.
- **`bLockGameOpen` lost on `MISSION_STAGE`** — *port bug fixed* (client state).

## 10. Modern / better-approach note

The native design makes the client send the **entire 252-byte `MissionParams`
back** and has the server boot anyone whose echo disagrees, which is why this
slice had to make the advertisement complete before the form could exist — a
design that forces every client to know every server field, and any field added
later silently becomes a data-loss risk the moment a client builds a form from a
stale snapshot. A port-native design would:

1. send a **diff** (`field → value` for only the fields the player touched) so the
   server applies exactly what changed and nothing can be zeroed by omission;
2. resolve the form's schema **from the server** (field list + allowed value
   ranges + the `Invalid` strings), so the client never duplicates native's combo
   lists (which is what `MissionSettingsForm` still does, straight from
   `newgamescreen.mdl`);
3. model `bLockGameOpen` / `nTotalMaxPlayersPerGame` in `GameConfig` so the
   advertisement, the guards and the UI read one source of truth (P853's
   recommendation, still open).

Those are wire/API changes and belong in a later slice; this one keeps the native
contract byte-for-byte.

## 11. Unfinished / out of scope

- **Live nTeams growth** (P853 divergence 1) — the TEAMS control deliberately
  stops at the assembled count.
- **Squad picker** in a squad game (needs a client-side squad membership list).
- **Rank-window self check** and the **squad-leader check** client-side
  (`newgamescreen.cpp:1043-1073`) — server-owned for now.
- **Port's own default games have no winning condition** (`GameConfig.Goals`
  defaults to conquest 0 / territory 100), unlike native `MissionParams::Reset`
  (`igc.h:1314`: conquest 100). `Invalid(true)` therefore refuses the very first
  owner edit on such a game until the owner picks a goal — the form shows the
  native string, so it is correct, but it is a rough edge worth deciding on
  (either default the config to `MissionGoals.NativeDefaults()` or seed the
  advertisement with conquest 100). My proof config sets conquest 100 explicitly.
- **New media for review**: the 16 PNGs in `docs/reports/P853B_assets/` are new
  media and need Domingo's look before the push (public-repo rule).
