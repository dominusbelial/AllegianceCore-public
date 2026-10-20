# P755 — simulation sounds A (finish of the WIP wave)

Task: **P755b** — finish the unfinished P755 WIP (`fc9cfdbd`, "pending Claude review").
Base: `pi/p755b` = `fc9cfdbd` + `git merge master` (merge committed as `5c45bc31`, no conflicts).

Native citations are against `~/Development/Allegiance/src/` (WinTrek/, clintlib/, Igc/).
Port asset inventory: `src/Allegiance.ClientCore.MonoGame/Content/sounddef.mdl` +
`src/Allegiance.ClientCore.MonoGame/Content/Sounds/*.ogg`.

## 1. Fixed what existed (WIP defects)

### 1.1 Build: undeclared `_chaffDeploy` (CS0103 ×2)
`SoundManager.cs:772`/`:1775` (WIP) referenced `_chaffDeploy` without a field.
Added `private SoundEffect _chaffDeploy` (field block, `PlayChaffDeploy` neighbour) and its
`Dispose()` call. Whole-solution build is now 0 warnings / 0 errors.

### 1.2 Regression: `AcceptQueuedCommand` returned illegal orders
The WIP returned the pending order even when `LegalQueuedCommand` failed, so
`CommandActionInput.cs:31` built an accepted order, `Game1.Orders` played the accept sound
and `EngageAutopilotToward` could fire — native does all of that only inside
`if (pmodel && trekClient.GetShip()->LegalCommand(cid, pmodel))` (**WinTrek.cpp:10662-10699**;
`acceptCommandSound` at :10658, `SetCommand(c_cmdQueued, NULL, c_cidNone)` at :10698 are both
inside the legal branch). Fixed in `ClientWorld.Orders.cs:AcceptQueuedCommand`:

- illegal order → returns `null`, accepted slot untouched, pending order **stays** (native
  consumes nothing until the 15 s auto-reject);
- the `voAffirmativeSound` reply still goes out for any pending order, legal or not, and
  `LastSender` is dropped after the first accept (**WinTrek.cpp:10703-10709**);
- join (`CidJoin`) is now inside the legal branch, as native (:10666-10670).

Tests: `AcceptQueuedCommand_IllegalOrder_IsNotConsumed` (regression), `AcceptQueuedCommand_LegalOrder_IsConsumed`.
The WIP's own `AcceptQueuedCommand_SendsAffirmativeToSender` had targeted a **nonexistent**
asteroid and only passed because of the bug — retargeted to a real station.

### 1.3 Regression: `AcceptMoneyRequest` lost the own-request guard
The WIP could donate to yourself. Native donates only when
`pshipSender != trekClient.GetShip()` (**WinTrek.cpp:10639**). Restored the
`r.SenderId == MyShipId → return null` guard in `ClientWorld.Callbacks.cs:AcceptMoneyRequest`
(the request is still consumed; native clears `LastSender` regardless, :10650/:10707-10709).
Tests: `AcceptMoneyRequest_SelfRequest_NoDonation` (regression), `AcceptMoneyRequest_ClampsToWallet`
(WinTrek.cpp:10643-10644 wallet clamp; also fixed the WIP tests' `SetMoney` to carry `ShipId` —
they never set the wallet at all).

### 1.4 Wire note (report-only, per brief)
`QUEUED_COMMAND_UPDATE` **fmid 215 grew from 10 B to 12 B** with the `SenderId` field
(`src/Allegiance.Core/Wire/QueuedCommandUpdate.cs`, offset 10, short; `FixedSize = 12`).
Server-side `QueueCommandForPlayer(..., senderId)` is threaded from
`QueueOrdersFromRelayedChat` (`Server.ShipCommands.cs:1542`). `docs/parity/fedmsg-wiring.csv`
does not track fmid 215, so nothing to update there. Codec round-trip pinned by
`AcceptRejectQueuedOrderLiveTests.QueuedCommandUpdate_roundtrips_full_shape`.

### 1.5 Chaff row SIM-0446 — honest update + probe stand-in removed
- **SIM-0446 (deployChaffSound, clintlib.cpp:3249) → implemented.** The P717 blocker
  ("CREATE_CHAFF(109) carries no launcher id") applied to keying the cue off the chaff-create
  broadcast; the WIP keys it off **FM_S_FIRE_EXPENDABLE(74)**, which carries `LauncherId`, so
  the native `pShip == m_ship` own-deploy gate is decidable with no wire/format change.
  Own ship firing a `ChaffLauncher` → `ClientSoundEventKind.DeployChaff` →
  `SoundManager.PlayChaffDeploy` (wave `dropobject`, sounddef.mdl:367, bundled by the WIP).
  Tests: `OwnShipFireExpendable_Chaff_RecordsDeployChaff` /
  `OtherShipFireExpendable_Chaff_DoesNotRecordDeployChaff` (both were dead in the WIP —
  `EnterGame` was never applied so `InGame` was false — and one had a syntax error (CS1002);
  fixed both) + `P755SoundAssetTests` (dropobject bundled).
- **SIM-0448 (deployProbeSound) — asset stand-in removed.** The probe deploy one-shot now
  loads the real `dropobject` wave (**sounddef.mdl:369**; native clintlib.cpp:3257) instead of
  the `lrgslideout` stand-in. `lrgslideout` remains only the P236 missile-launch cue.
  `ProbeAmbientSoundCurve` remarks updated to match.

## 2. Wave continuation — 5 more sounds rows

Coordination check: no *sounds* rows are touched by unmerged `pi/*` branches (scanned
`git diff master..pi/p753,p754,p757,p758,p759` etc. against `docs/parity/sim-rules-matrix.csv`;
the WIP itself owns SIM-0294/0295/0298). P756 (sounds B) rows are untouched here.

| Row | Native | Wave (sounddef.mdl) | Port wiring | Test |
|---|---|---|---|---|
| SIM-0322 | mountSound, load.cpp:1887 (slot-menu part selection) | mount (:286) | `ShopModel.ChoosePartInOpenSlot` raises `SlotPartChosen(true)`; `Game1.Panes` → `SoundManager.PlayMountPart` | ShopModelTests.Slot_part_choice_raises_mount_and_clear_events |
| SIM-0323 | mountSound, load.cpp:1480-1487 (hull-menu selection + `RefreshLoadout` default loadout) | mount (:286) | `ShopModel.SelectHullAtCursor` raises `HullChosen` (only on a real pick; de-pick plays nothing) | ShopModelTests.Hull_pick_raises_hull_chosen_only_on_change |
| SIM-0324 | unmountSound, load.cpp:1889 (slot-menu "(empty)" row) | unmount (:288) | `SlotPartChosen(false)` → `PlayUnmountPart` | same as SIM-0322 |
| SIM-0331 | mouseclickSound, sectormap.cpp:1523 (left click landing on a cluster, before the action branches) | mousedown (:321, **newly bundled** from the Artwork wave set) | `Game1.StarmapClick.ExecuteStarmapClick` plays `SoundManager.PlayMapClick` once the hit-test resolves, before Ripcord/Teleport/ViewCluster/Goto | P755SoundAssetTests (asset); Game1-level call has no headless test (noted honestly in the row) |
| SIM-0338 | personalJumpSound, teleportpane.cpp:319-320 (`OnButtonTeleport` OT_ship branch; the station branch plays nothing, :325-352) | jumpin (:274) | `Game1.Panes.ActivateTeleport` BoardShip branch → `SoundManager.PlayPersonalJump` | TeleportPaneModelTests (model-level trigger) |

Not taken (left open honestly): SIM-0285/0317-0320/0325/0327-0330/0332-0337 (no ported trigger
site or missing wave — e.g. chat-key/pane-slide/team-screen UI the port does not have one-to-one),
loop rows (SIM-0345/0347/0348/0380/0411), depleted-cue rows blocked on the launcher-magazine wire
(SIM-0445/0452/0453).

## 3. Gate results (light gate, Domingo 2026-10-17)

- **Whole-solution build**: 0 warnings / 0 errors.
- **AOT publish** (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release
  -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot-out`): clean, no IL2026/IL3050
  (Server + Core changed: QueuedCommandUpdate 12 B, Server.ShipCommands senderId threading).
- **New/affected tests, 3× alone** (all green ×3):
  - `Allegiance.ClientCore.Tests` → `ClientWorldSoundEventTests` 94/94 ×3 (includes the fixed WIP
    chaff + voice-reply tests and the 4 new regression tests);
  - `Allegiance.ClientCore.MonoGame.Tests` → `ShopModelTests` + `P755SoundAssetTests` +
    `P717DeploySoundTests` + `TeleportPaneModelTests` 29/29 ×3;
  - `Allegiance.Core.Tests` → `AcceptRejectQueuedOrderLiveTests` 7/7 ×3 (codec 12 B round-trip).
- **ONE fast-tier run** (`tools/run-tests.sh --tier fast`): 8978 tests, 269 s — one failure,
  `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`,
  which passes 3/3 alone and is the documented load flake (docs/TEST_FLAKES.md:880,1149).
  Summary: `docs/reports/P755_assets/fast-summary.json`.
- **Showcase never-parks test** (`P700ShowcaseLiveTests`, flight/orders touched): **5/5**.

Raw TRX/logs: `~/allegiance-evidence/P755/` (outside the repo). `.scratch/` (worktree-local,
git-excluded) holds the AOT publish output and was deleted after the run.

## 4. Scorecard

```
docs/parity/sim-rules-matrix.csv, area=sounds (total 235)
BEFORE: implemented 173, partial 12, missing 27, n/a 23
AFTER:  implemented 179, partial 11, missing 22, n/a 23
```

New `implemented` (6): SIM-0322, SIM-0323, SIM-0324, SIM-0331, SIM-0338, SIM-0446.
Updated in place (1): SIM-0448 (real asset). No row was marked `n/a` in this task.

## 5. Files touched

- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — `_chaffDeploy` field + dispose;
  `_probeDeploy` → real `dropobject`; new `PlayPersonalJump`, `PlayMapClick`.
- `src/Allegiance.ClientCore.Game/Audio/ProbeAmbientSoundCurve.cs` — remarks.
- `src/Allegiance.ClientCore/State/ClientWorld.Orders.cs` — `AcceptQueuedCommand` legality gate.
- `src/Allegiance.ClientCore/State/ClientWorld.Callbacks.cs` — `AcceptMoneyRequest` own-request guard.
- `src/Allegiance.ClientCore.Game/Rendering/ShopModel.cs` — `SlotPartChosen`/`HullChosen` events.
- `src/Allegiance.ClientCore.Game/Game1.Panes.cs` — shop sound subscription; teleport board cue.
- `src/Allegiance.ClientCore.Game/Game1.StarmapClick.cs` — map-click cue.
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` + `Sounds/mousedown.ogg` — new wave.
- `tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs` — fixed WIP tests + 6 new.
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopModelTests.cs` — 2 new event tests.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P755SoundAssetTests.cs` — new.
- `docs/parity/sim-rules-matrix.csv` — 7 rows.

Nothing committed or pushed; all changes are in the working tree.
