# P719 — Parity wave 18: simulation SOUNDS, sixth bundle (station/economy + loadout command)

## Summary

Ported **14 open `sounds` rows** in the station/economy announce area and the
in-flight loadout/ordnance command-feedback area. Three are genuinely new,
wire/state-driven one-shots with failing-first tests — the local ship's **dock**
edge (docked + welcome-home), the local ship being **boarded**, and the existing
`HIGHLIGHT_CLUSTER`/`POSITIONREQ` producers reconciled (matrix stale `missing`).
Nine are Game1-command-driven one-shots (group-fire toggle, mount/unmount/drop/
cargo-full/reload) that ride the real in-flight commands the renderer already
has. **No new server event was required** (DOCKED_NOTIFY, LOADOUT_CHANGE,
HIGHLIGHT_CLUSTER, POSITIONREQ are all already broadcast by this port's server)
and **no flight/simulation behaviour changes** (guarded by the P700 regression
gate).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 299/594 (50.34%) | **327/594 (55.05%)** |
| unweighted | 106/228 (46.49%) | **120/228 (52.63%)** |

## Why these rows

The bundle is one coherent family — **station/economy announce + in-flight
loadout/ordnance command feedback** — sharing the established
`ClientSoundEvent → Game1.Audio → SoundManager` one-shot path. The task hint
lists "station/economy" and "weapons/ordnance" as remaining families; these are
the most tractable members of each, all faithful one-shots that the renderer
can genuinely trigger:

- **Docked / welcome-home** — `dockedSound` + `salWelcomeHomeSound`
  (`WinTrek/trekigc.cpp:2859-2860`) on the local ship entering the hangar;
- **Boarded** — `boardSound` (`clintlib/appmsg.cpp:2071`) when a player boards
  the LOCAL ship;
- **newCommand / player-waiting** — `newCommandMsgSound` (appmsg.cpp:1757) and
  `salPlayerWaitingSound` (appmsg.cpp:2481), already wired but stale in the
  matrix (the P711/P714-style reconciliation);
- **group-fire / mount / unmount / cargo-full / drop / reload** —
  `groupWeaponsSound`/`ungroupWeaponsSound` (WinTrek.cpp:9047,9053,9529,9531),
  `mountSound`/`unmountSound`/`salCargoFullSound` (WinTrek.cpp:9098,9102,9114),
  `dropSound` (WinTrek.cpp:9719), `salReloadingSound` (WinTrek.cpp:9693) — the
  in-flight commands the renderer already implements.

## Implementation

### New: dock edge (SIM-0388, SIM-0389)

Native plays `dockedSound` + `salWelcomeHomeSound` when the local ship enters
the hangar (trekigc.cpp:2859-2860). This pure-renderer client observes the dock
as the server's `DOCKED_NOTIFY` (sent to the docking peer) setting the own
ship's `StationId`. `ClientWorld.Apply(DockedNotify)` now edge-detects
`StationId` going from none to a real station and records two one-shot events:

| row | native | wave (sounddef.mdl) | event |
|---|---|---|---|
| SIM-0388 | dockedSound (trekigc.cpp:2859) | landed (:316) | `Docked` |
| SIM-0389 | salWelcomeHomeSound (:2860) | vo_sal_welcomehome (:801) | `WelcomeHome` |

### New: boarded (SIM-0432)

Native plays `boardSound` when a player boards the LOCAL ship
(`if (pship == m_ship && m_ship->GetCluster() == NULL)`, appmsg.cpp:2071). The
client learns of a boarding via `LOADOUT_CHANGE` re-seating passengers onto the
local ship (the same passenger loop P648 ports). `Apply(LoadoutChange)` now
edge-detects a passenger whose `ParentId` becomes the local ship (was not
already parented to it) and records a `Boarded` one-shot:

| row | native | wave | event |
|---|---|---|---|
| SIM-0432 | boardSound (appmsg.cpp:2071) | vo_sal_playerboarded (:782) | `Boarded` |

### Reconciliation (2 rows, already implemented — matrix corrected)

| row | cue | wiring | evidence |
|---|---|---|---|
| SIM-0430 | newCommandMsgSound | `Apply(HighlightCluster)` → `RecordSoundEvent(SectorHighlighted)` (ClientWorld.Commanding.cs:31) → `PlayCommandMessage` (receivedobjective) | trigger test added |
| SIM-0433 | salPlayerWaitingSound | `Apply(PositionReqBroadcast)` → `PlayerWaiting` (ClientWorld.Lobby.cs:35) → `PlayVoice(vo_sal_playerwaiting)` | trigger test added |

Both were `missing` in the matrix despite the code being live; each is now
`implemented` with the wiring cited and a ClientWorld-level trigger test.

### Game1-command one-shots (9 rows)

These are local player-command cues native plays from the in-flight/loadout
command handlers. This port's renderer already implements the same commands, so
each rides the existing command site directly (the `_sound?.PlayX()` convention,
no wire event needed — the MonoGame-level command path):

| row | native | wave | command site |
|---|---|---|---|
| SIM-0280 | ungroupWeaponsSound (WinTrek.cpp:9047) | ungroupweapons (:377) | `ToggleFixedWeapon` → `PlayGroupFire(false)` |
| SIM-0281 | groupWeaponsSound (:9053) | groupweapons (:376) | `ToggleFixedWeapon` → `PlayGroupFire(true)` |
| SIM-0288 | groupWeaponsSound (WinTrek.cpp:9529) | groupweapons | `ToggleGroupFire` → `PlayGroupFire(true)` |
| SIM-0289 | ungroupWeaponsSound (:9531) | ungroupweapons | `ToggleGroupFire` → `PlayGroupFire(false)` |
| SIM-0282 | mountSound (WinTrek.cpp:9098) | mount (:286) | `ChangeFlightPart` → `PlayWarnPartSound` |
| SIM-0283 | unmountSound (:9102) | unmount (:288) | `ChangeFlightPart` → `PlayWarnPartSound` |
| SIM-0284 | salCargoFullSound (:9114) | vo_sal_cargofull (:810) | `ChangeFlightPart` no-slot → `PlayCargoFull` |
| SIM-0291 | dropSound (WinTrek.cpp:9719) | drop (:290) | `ChangeFlightPart(Drop)` → `PlayDropPart` |
| SIM-0290 | salReloadingSound (WinTrek.cpp:9693) | vo_sal_reloading (:819) | `TryReload` → `PlayReloading` |

`SoundManager` gained the one-shot fields/`Play*` methods + load/dispose, and
the 10 new `.ogg` were copied from the artwork tree into `Content/Sounds` and
registered in `Content.mgcb` (verified by the new asset-bundling test).

## Failing-first

The 3 genuinely-new rows are failing-first: `ClientSoundEventKind.Docked`,
`WelcomeHome` and `Boarded` did not exist before P719, so the new tests cannot
compile on the pre-change tree (CS0117, the same failing-first mechanism
P711/P716/P717 documented); after the change all pass. The 9 Game1-command rows
were genuinely `missing` (no condition-to-cue path existed — the new SoundManager
`Play*` methods did not exist), so the asset-bundling test fails before the
waves/mgcb are added. The 2 reconciliation rows were already-working code (like
P711's SIM-0410) — verified by wiring audits + the new trigger tests.

## Tests

`tests/.../ClientWorldSoundEventTests.cs` (+6):
- `DockingOwnShip_RecordsDockedAndWelcomeHome`, `RepeatingDockNotice_DoesNotRefireDockedCue`
- `PassengerBoardsLocalShip_RecordsBoarded`, `PassengerAlreadyBoarded_DoesNotRefireBoarded`
- `HighlightCluster_RecordsSectorHighlighted`, `PositionRequestToOurSide_RecordsPlayerWaiting`

`tests/.../MonoGame.Tests/P719StationEconomySoundTests.cs` (new):
- `StationEconomyAndLoadoutWaves_AreBundledForTheRealSoundPlayer` — all 10 new
  `.ogg` present in `Content/Sounds` and registered in `Content.mgcb`.

46/46 `ClientWorldSoundEventTests` pass; 77/77 across the affected classes
(`ClientWorldSoundEventTests`, `ClientWorldExportTests`,
`ClientWorldAlephTransitTests`, `P640CommandingReceiveTests`); the new MonoGame
asset test passes.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first tests: see above (new enum members are a pre-change compile
  error; new waves absent pre-change breaks the asset test).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. The change records/gates client-side
  one-shot sound events only — it cannot alter bot flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).

### Suite run 1 (recorded as it happened)
- Lobby 19/19, 0 fail.
- MonoGame 2293 pass + 3 skip, 0 fail.
- ClientCore 1176 pass + 1 skip, 0 fail.
- Core 4753 pass, 0 fail.
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit **0** (fully clean run).

### Suite run 2 (recorded as it happened)
- Lobby 19/19, 0 fail.
- MonoGame 2293 pass + 3 skip, 0 fail.
- ClientCore 1176 pass + 1 skip, 0 fail.
- Core 4751 pass + **2 fail** —
  `Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`
  (GC-hot-path allocation flake; **3/3 in isolation** here; unrelated to P719 —
  code not touched) and
  `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
  (documented load flake, `docs/TEST_FLAKES.md`; **3/3 in isolation** here;
  unrelated to P719 — code not touched).
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit 1 (driven solely by the two documented Core load flakes;
  both pass 3/3 in isolation).

Across both runs P719's code contributed **zero failures**. Run 1 was fully
clean; run 2's only failures are the two pre-existing, documented load flakes in
code P719 does not touch, each verified **3/3 in isolation**.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — new `Docked`,
  `WelcomeHome`, `Boarded` kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(DockedNotify)` dock
  edge, `Apply(LoadoutChange)` boarded edge.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — 10 new one-shots +
  `Play*` methods + `PlayGroupFire`/`PlayWarnPartSound` + load/dispose.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — dispatch for the 3 wire kinds.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — group-fire toggle cue
  (SIM-0288/0289).
- `src/Allegiance.ClientCore.Game/Game1.FlightParts.cs` — single-weapon / mount /
  unmount / drop / cargo-full cues (SIM-0280..0284, 0291).
- `src/Allegiance.ClientCore.Game/Game1.InFlightUtility.cs` — reload cue
  (SIM-0290).
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` + 10 new `.ogg`.
- `tests/.../ClientWorldSoundEventTests.cs` (+6), `P719StationEconomySoundTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 14 rows → `implemented` (LF kept).
- `docs/reports/P719_PARITY_WAVE18.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| docked + welcome-home on own dock | trekigc.cpp:2859-2860 | design | identical one-shots on the DOCKED_NOTIFY own-ship dock edge |
| boarded cue on the local ship | appmsg.cpp:2071 | design | identical one-shot on the LOADOUT_CHANGE passenger-re-seat edge |
| new-command msg cue | appmsg.cpp:1757 | design | already wired (HIGHLIGHT_CLUSTER); matrix corrected |
| player-waiting cue | appmsg.cpp:2481 | design | already wired (POSITIONREQ); matrix corrected |
| group/ungroup weapons | WinTrek.cpp:9047,9053,9529,9531 | design | played on the same command sites (ToggleFixedWeapon / ToggleGroupFire) |
| mount/unmount/cargo-full/drop | WinTrek.cpp:9098,9102,9114,9719 | design | played on the in-flight loadout commands (ChangeFlightPart) |
| reload cue | WinTrek.cpp:9693 | design | played on TryReload |

## Modern alternative considered

Same as P710/P714: native resolves these local announce cues as ship-positioned
one-shots; the port plays them as one-shots through the voice budget
(`SoundCategory.Warning`), player-identical for cockpit/own-ship announces and
cheaper. The loadout/group-fire/reload cues are player-command feedback (not
remote events), so they are played directly at the command site rather than
routed through a wire event — the modern equivalent of native's immediate
`PlaySoundEffect` in the input handler. No modern audio-engine upgrade was
warranted for a handful of one-shot cues.

## Limitations

- SIM-0395/0413 (repaired-at-carrier) remain open: the RELAUNCH_SHIP(192) wire
  is not consumed by this renderer client, so the relaunch-at-carrier edge is
  not observable without a client relaunch handler; docked (SIM-0388/0389) is
  ported but the carrier-repair-specific cue is not separately derivable this
  wave (documented, not skipped).
- SIM-0284's cargo-full cue fires on the renderer's own "no compatible slot"
  refusal (the mount/unmount planner finding no room) — the same user-facing
  condition native guards with salCargoFullSound (WinTrek.cpp:9114), but the
  renderer's refusal message text differs (native's is "Cargo is full.").
- The 9 Game1-command rows are `verified` evidence (wiring audit + asset test),
  not a ClientWorld-level trigger test — the command cues live in MonoGame
  `Game1`, the same evidence class the P716 critical/non-critical reconciliation
  uses.
