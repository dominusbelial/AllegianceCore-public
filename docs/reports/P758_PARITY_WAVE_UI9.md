# P758 — interface parity wave 9: fedmsg treasure/warp-bomb, commander succession, lobby boot text

**Base:** `pi/p758b` (worktree of `master`), created from the unfinished `pi/p758`
WIP commit `6a2d36c8` ("WIP p758 (Pi, zai/glm-5.3) — pending Claude review").
First action this pass: `git merge master` (resulting merge commit `dcab38fa`),
which brought `master` at `e1c528b2` into the branch and resolved nothing
(clean merge).

**Hand-over:** the GLM 5.3 agent implemented the fedmsg 162/188 production code
(server `Server.Docking.cs`, client `ConnectionSession`/`ClientWorld`,
`TreasureMountPicker`, `WarpBombVisual`/`WarpRenderer`, `Game1`), but ran out of
credit before tests, report, parity-CSV edits or a scorecard run — and broke
`FedMsgDispatchCoverageTests.SupportedServerSentSet_IsFullyAccountedFor`
(3/3 failures). This pass reviewed that diff against native, finished the
tests, fixed the coverage test honestly, added the commander and lobby pieces,
ran the full light gate, updated the parity matrices and wrote this report.

## What changed (this pass, on top of the GLM WIP)

### 1. fedmsg 162 `ACQUIRE_TREASURE` — verified against native + wire-pinned
- Confirmed the server sends the native per-player notice
  (`FedSrv.CPP:13622-13641`) to the **picker's own connection only**, for every
  treasure code, with `treasureCode` / `treasureID` / `amount` straight from the
  treasure:
  - **Powerup**: `treasureID` = the `PowerupCode` bitmap (native
    `treasure->GetTreasureID()`; the port's `(short)bits` is
    `treasure.Data.TreasureId`, the same bitmap). `amount` = `NA` (`-1`,
    native `dt.amount = NA`, `missionigc.cpp:4188`).
  - **Cash**: `amount = treasureID` (native `treasureIGC.cpp:91-92`), so both
    fields carry the dollar value — the port sends `(short)amount` for both,
    matching native.
  - **Part**: `treasureID` = part type id, `amount` = the offered count; the
    client answers with `FM_C_TREASURE_ACK(163)` via `TreasureMountPicker`
    (port of `IshipIGC::HitTreasure(c_tcPart, …)`, `shipIGC.h:1239-1305`).
- The cash reward/donation text is present in `ClientWorld.Apply(AcquireTreasure)`
  (`appmsg.cpp:289-299`): "You received a reward of $N." / "You donated your
  reward of $N to NAME."
- **Test added:** `LiveTreasurePickupTests.Powerup_and_cash_pickups_send_ACQUIRE_TREASURE_with_native_fields_to_the_picker_only`
  (real in-process server + TCP bots; asserts powerup bitmap/`NA` and cash
  amount/amount fields, and that the notice reaches only the picker).
- **Test added:** `P758AcquireTreasureClientTests` pins the cash reward and
  donation lines, the unknown-target fallback, and no-line-for-non-cash.
- Existing `AddPartTests` already pinned the part-offer fields; left intact.

### 2. fedmsg 188 `WARP_BOMB` — handler tested
- `ClientWorld.WarpBomb.cs` is the port of `clintlib/appmsg.cpp:850-868`
  (`IsInGame()` gate, `AddBomb` state with `ClientTimeFromServerTime` deadline,
  sector-gated "Aleph to %s destabilized" line naming the far end).
- **Test added:** `P758WarpBombHandlerTests` (own-sector names the far end,
  far-end names the bombed aleph's sector, other-sector records state with no
  line, unknown warp ignored, lobby client ignored).

### 3. `FedMsgDispatchCoverageTests` fixed honestly
- Moved 162/188 from the unconsumed registry to the consumed registry with
  citations (the GLM diff already did the move; the executable counts were
  stale). Updated the literal assertions to the real counts: 99 native
  consumed / 17 port-added / 116 total consumed / 3 unconsumed / 102 supported.

### 4. Commander — donation-preferred leader succession
- `Server.HandleLeaderSuccession` (`Server.Lobby.cs`) now runs the donation-vote
  `PickNewLeader` (`fsmission.cpp:955-961`, `FedSrv.CPP:13981-14024`) when the
  mission is `Started`, preferring the remaining player with the most teammates
  donating to them; otherwise it falls back to the first remaining player
  (native's `GetCanLeadSquad` preference is unported — squads don't exist here).
  The stale "always first other player" doc is replaced with the real rule.
- **Test added:** `LiveTeamLeadershipTests.Started_mission_leader_succession_prefers_donation_target`
  (C succeeds A over lower-id B when B and C both donate to C). The existing
  first-other fallback tests still pass.

### 5. Lobby — native `QuitSideReason` + booted-player text (partial)
- Extended `QuitSideReason` to all 18 native `QSR_*` values
  (`Messages.h:787-808`) so the wire enum no longer depends on raw integers.
- Added `ClientWorld.OwnBootMessage` — the port of native `OnQuitMission`'s
  `QSR_*` → `strMessage` switch (`trekigc.cpp:5265-5335`), including the
  "You have been booted by a server administrator!" line (`:5286-5291`).
- `Game1.FlightInput` now uses it for the lobby error line (previously only
  distinguished leader-boot vs "You left the mission.").
- **Test added:** `P758QuitSideReasonTests` (33 assertions pin every enum value
  and the native message text / message-less reasons).
- **Not done:** the server-side Steam moderator ban and defection-side boot of
  `FedSrv.CPP:6749-6818` (needs Steam group-membership infrastructure absent in
  this port); the parity row stays `missing` — see "Unfinished".

## Verification

- **Whole-solution build:** `dotnet build AllegianceCore.slnx -m:8 -v:minimal`
  → **0 warnings, 0 errors**.
- **AOT publish** (Server/Core changed):
  `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot-publish`
  → **exit 0, no IL2026/IL3050, no warnings**.
- **New/affected tests, 3× alone:**
  - `FedMsgDispatchCoverageTests` + `P758WarpBombHandlerTests` +
    `P758AcquireTreasureClientTests`: 12/12 ×3.
  - `Powerup_and_cash_pickups_send_ACQUIRE_TREASURE_with_native_fields_to_the_picker_only`: 1/1 ×3.
  - `Started_mission_leader_succession_prefers_donation_target`: 1/1 ×3.
  - `P758QuitSideReasonTests`: 33/33.
  - `LiveTeamLeadershipTests` (regression): 12/12.
- **Fast tier:** `tools/run-tests.sh --tier fast` → **9058 tests PASS** in 269.8 s
  (full `TestResults/tiers-*/summary.json`).

## Parity CSV edits

- `docs/parity/fedmsg-wiring.csv`: rows 162 `ACQUIRE_TREASURE` and 188
  `WARP_BOMB` `partial → done` (real client consumes both).
- `docs/parity/parity-matrix.csv`:
  - `fedmsg,162 ACQUIRE_TREASURE` `partial → done`.
  - `fedmsg,188 WARP_BOMB` `partial → done`.
  - `commander … Donation-preferred departure succession` `partial → done`.

### Scorecard (after)

```
parity-matrix.csv: validated 665 rows
area | weighted (credit/weight) | unweighted (complete/eligible)
commander | 86.62% (70.164/81.000) | 86.96% (40/46)
commands  | 95.09% (562/591)       | 93.16% (177/190)
fedmsg    | 99.21% (503/507)       | 99.19% (122/123)
hud       | 1.85% (6/324)          | 1.85% (2/108)
lobby     | 69.96% (32.880/47.000) | 66.67% (22/33)
parts     | 100.00% (153/153)      | 100.00% (40/40)
screens   | 70.78% (80.693/114.000)| 68.13% (62/91)
TOTAL     | 77.48% (1407.737/1817.000) | 73.69% (465/631)
sim-rules-matrix.csv: validated 473 rows
TOTAL     | 81.62% (1048/1284) | 79.24% (332/419)
```

(Before this pass: parity-matrix total 76.93% weighted / fedmsg 97.63% /
commander 84.15%.)

## Unfinished (left `partial`/`missing` honestly)

- **Lobby moderator ban + defection boot** (`FedSrv.CPP:6749-6818`): the
  server-side Steam moderator identity/bans and defection-side boots are not
  implemented — no Steam group-membership infrastructure in this port. The
  client booted-by-administrator text (item 5's client half) is done.
- **Lobby connect-progress retry/abort** (`introscreen.cpp:1667-1699`),
  **timed automatic side assignment** (`teamscreen.cpp:2962-2982`), and
  **civ-generated briefing/tips** were not attempted this pass.
- **Commands** `TK_ConModeInvest`, `TK_RollModifier`, `TK_ToggleLODSlider`,
  `TK_LODUp`, `TK_LODDown`: not attempted; rows remain `missing`/`partial`.
- **fedmsg 178 `BOARD_NACK`** left alone per the brief (P757's BOARD_NACK work
  covers it).
