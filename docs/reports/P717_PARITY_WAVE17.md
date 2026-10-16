# P717 — Parity wave 17: simulation SOUNDS, fifth bundle (ordnance deploy & own-ship shield status)

## Summary

Ported **6 open `sounds` rows** in the weapons/ordnance + own-ship shield-status
area. Two are genuinely new, wired and tested with failing-first coverage —
the **mine-deploy** one-shot (the local ship's own FM_S_EXPORT(26)/OT_mine) and
the **shields-offline** VO (the local ship's own streamed shield fraction
collapsing). Four were already substantively wired by earlier waves but sat
stale `partial` in the matrix and are **reconciled** to `implemented` with a
wiring audit + asset test (rock-hit, missile-launch, probe-deploy,
dev-completion). **No new server event was required** (EXPORT-mine and the
own-ship ServerShipUpdate are already broadcast) and **no flight/simulation
behaviour changes** (guarded by the P700 regression gate).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 282/594 (47.47%) | **299/594 (50.34%)** |
| unweighted | 100/228 (43.86%) | **106/228 (46.49%)** |

## Why these rows

The bundle is one coherent family — **ordnance deploy + own-ship weapons/shield
status feedback** — sharing the native deploy one-shot region
(`BaseClient::FireExpendable`, `clintlib.cpp:3245-3257`) and the local-ship
status one-shot region (`CshieldIGC::ApplyDamage`, `shieldIGC.cpp:185-186`),
plus the already-built announce one-shots those two native regions overlap
(missile launch, probe deploy, rock hit, dev completion). All ride the
established `ClientSoundEvent → Game1.Audio → SoundManager` one-shot path and
are testable at the `ClientWorld` wire level:

| row | cue | native cite | wave | disposition |
|---|---|---|---|---|
| SIM-0447 | `deployMineSound` | clintlib.cpp:3253 | `dropmine` (:368) | **new** (own FM_S_EXPORT OT_mine) |
| SIM-0243 | `salShieldsOfflineSound` | shieldIGC.cpp:186 | `vo_sal_shieldsoffline` (:791) | **new** (own shield collapse edge) |
| SIM-0448 | `deployProbeSound` | clintlib.cpp:3257 | `dropobject` (:369) | reconcile → implemented |
| SIM-0442 | `GetLaunchSound` | clintlib.cpp:3103 | (per-magazine; port uses fixed stem) | reconcile → implemented |
| SIM-0235 | `rockHitSound` | asteroidigc.h:154 | `hitrock` (:266) | reconcile → implemented |
| SIM-0439 | `GetCompletionSound` (dev) | appmsg.cpp:3326 | `vo_sal_researchcomplete` | reconcile → implemented |

Two rows that cannot yet be ported faithfully are **documented, not silently
skipped** (see Limitations): SIM-0446 (chaff deploy — the CREATE_CHAFF(109)
wire carries no launcher id, so the native `pShip == m_ship` gate is
undecidable) and SIM-0241/0242 (shield activate/deactivate — per-shield-type
`SoundID`s from the core's shieldtype data).

## Implementation

### New: mine-deploy one-shot (SIM-0447)

Native plays `deployMineSound` only when the LOCAL ship fires an expendable
(`BaseClient::FireExpendable`, `if (pShip == m_ship)`, clintlib.cpp:3253). This
pure-renderer client observes the deploy as the server's FM_S_EXPORT(26)/OT_mine
broadcast whose `LauncherId` equals the local ship. `ClientWorld.Apply(ExportMine)`
now records a new `ClientSoundEventKind.MineDeployed` on that own-deploy edge;
`Game1.Audio` dispatches it to the new `SoundManager.PlayMineDeploy`, which plays
the real `dropmine.ogg` (native `deployMineSound`, sounddef.mdl:368), bundled and
registered in `Content.mgcb` this wave.

### New: shields-offline VO (SIM-0243)

Native plays `salShieldsOfflineSound` when overload damage knocks the ship's
shield past the downed threshold (`CshieldIGC::ApplyDamage`, shieldIGC.cpp:185-186).
The client already streams the local ship's own `ShieldFraction` on its
`ServerShipUpdate` (the same source P711's status alarms sample), so the P717
extension to `ClientWorld.UpdateOwnStatusAlarms()` edge-detects the shield
collapsing from online (> `ShipStatusAlarms.ShieldOfflineEpsilon`) to ≈0 and
records a new `ClientSoundEventKind.ShieldsOffline` exactly once per collapse.
`Game1.Audio` dispatches it to `SoundManager.PlayShieldsOffline` (`vo_sal_shieldsoffline.ogg`,
sounddef.mdl:791), bundled and registered this wave.

### Reconciliation (4 rows, already wired — matrix corrected)

| row | cue | wiring | evidence |
|---|---|---|---|
| SIM-0448 | probe deploy | `ProbeSoundPlanner` Deploy action (own launcher) → `PlayProbeDeploy` (P260) | wiring audit + planner tests |
| SIM-0442 | missile launch | `MissileSoundPlanner` local-launch → `PlayMissileLaunch` (P236) | wiring audit + P236 planner tests |
| SIM-0235 | rock hit | impact cue pass → `PlayRockHitSound` (`hitrock`, P463c) | wiring audit + asset inventory |
| SIM-0439 | dev completion | `Apply(DevCompleted)` → `PlayDevComplete` (`vo_sal_researchcomplete`) | wiring audit + existing tests |

All were `partial` in the matrix despite the code being live; each is now
`implemented` with the wiring cited and `evidence` upgraded (`verified`).

## Failing-first

Both new rows are failing-first: `ClientSoundEventKind.MineDeployed` and
`ClientSoundEventKind.ShieldsOffline` did not exist before P717, so the new
tests cannot compile on the pre-change tree (CS0117, the same failing-first
mechanism P711/P716 documented); after the change all pass. The four
reconciliation rows were already working code (like P714's/P716's
reconciliations) — verified by the wiring audit + existing planner tests + the
new asset-bundling test, not by a failing-first test.

## Tests

`tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs` (+5):
- `OwnShipDeploysMine_RecordsMineDeployed`, `RemoteShipDeploysMine_RecordsNoDeploySound`
- `OwnShieldCollapses_RecordsShieldsOffline`, `OwnShieldAlreadyDown_DoesNotRefire`,
  `OwnShieldStaysOnline_NoShieldsOffline`

`tests/Allegiance.ClientCore.MonoGame.Tests/P717DeploySoundTests.cs` (new):
- `DeployAndShieldOfflineWaves_AreBundledForTheRealSoundPlayer` — `dropmine.ogg`
  and `vo_sal_shieldsoffline.ogg` present in `Content/Sounds` and registered in
  `Content.mgcb`.

New tests **3× clean** in isolation; affected classes
`ClientWorldSoundEventTests` + `ShipStatusAlarmSoundTests` +
`ClientWorldMineTests` = 57/57 pass.

## Real assets

`dropmine.ogg` (from `/home/domingo/Development/Artwork`) and
`vo_sal_shieldsoffline.ogg` copied into
`src/Allegiance.ClientCore.MonoGame/Content/Sounds/` and registered in
`Content.mgcb` (verified they compile to `.xnb` in the built content and the
asset test asserts their presence + mgcb registration).

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first tests: see above (new enum members are a pre-change compile error).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. The change records two one-shot sound
  events only — it cannot alter bot flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).

### Suite run 1 (recorded as it happened)
_SUITE RUN 1 NUMBERS_

### Suite run 2 (recorded as it happened)
_SUITE RUN 2 NUMBERS_

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — new `MineDeployed`,
  `ShieldsOffline` kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(ExportMine)` own-deploy
  detection; `UpdateOwnStatusAlarms()` shield-collapse edge.
- `src/Allegiance.ClientCore/State/ShipStatusAlarms.cs` — `ShieldOfflineEpsilon`.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — `PlayMineDeploy`,
  `PlayShieldsOffline` + load/dispose.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — dispatch cases.
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` + `dropmine.ogg`,
  `vo_sal_shieldsoffline.ogg`.
- `tests/.../ClientWorldSoundEventTests.cs` (+5), `P717DeploySoundTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 6 rows → `implemented`, SIM-0446 documented (LF kept).
- `docs/reports/P717_PARITY_WAVE17.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| mine-deploy one-shot (own ship) | clintlib.cpp:3253 | design | identical own-deploy one-shot from `dropmine` |
| shields-offline VO (own shield knock-down) | shieldIGC.cpp:186 | design | identical one-shot on own shield-collapse edge |
| probe-deploy one-shot (own ship) | clintlib.cpp:3257 | design | already wired via ProbeSoundPlanner (P260) |
| missile-launch one-shot | clintlib.cpp:3103 | design | already wired (P236); fixed stem vs per-magazine core SoundID |
| rock-hit one-shot | asteroidigc.h:154 | design | already wired (P463c), distance-attenuated |
| dev-completion VO | appmsg.cpp:3326 | design | already wired (DEV_COMPLETED → PlayDevComplete) |

## Modern alternative considered

Native computes shield state and deploys in-process and plays their cues against
its own IGC. This pure-renderer client instead derives both from wire the server
already sends: the own-deploy edge from the FM_S_EXPORT OT_mine `LauncherId`,
and the shield collapse from the local ship's own `ServerShipUpdate`
`ShieldFraction` — zero extra wire and no client-side physics sim, player-
identical for a cockpit/own-ship announce. This matches the P711 status-alarm
pattern (sample the already-streamed fractions, edge-detect, play). No modern
audio-engine upgrade was warranted for a handful of one-shot cues.

## Limitations

- SIM-0446 (chaff deploy) remains **open, documented**: the server's
  CREATE_CHAFF(109) broadcast carries only Etid/P0/V0 — no launcher/source ship
  id — so the native `pShip == m_ship` gate cannot be decided on this wire.
  Porting it needs a wire/format change (add a launcher id) plus a server
  broadcast change, which is deliberately out of this wave's scope.
- SIM-0241/0242 (shield activate/deactivate) remain **partial**: native uses a
  per-shield-type `SoundID` (`m_typeData->activateSound/deactivateSound`,
  shieldIGC.cpp:94,185) loaded from the core's shieldtype data; resolving that
  needs per-type SoundID → wave lookup in the port. The audible feedback for the
  player is dominated by the shields-offline VO (SIM-0243), which this wave
  ports.
- SIM-0448 (probe deploy) and SIM-0442 (missile launch) use documented sound
  substitutions: probe deploy plays the generic expendable asset and missile
  launch a fixed Hornet stem instead of the per-type/per-magazine core SoundID
  (pre-existing P260/P236 generalizations, unchanged by P717).
