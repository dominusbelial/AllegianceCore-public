# P741 — Parity wave 25: simulation SOUNDS, thirteenth bundle (own-ship shield power edges + reachable quit reasons + drone in-transit reconciliation)

## Summary

Ported **6 open `sounds` rows → `implemented`** and moved **11 structurally
unreachable rows → `n/a` with concrete reasons**, so the sounds area reaches
its practical maximum for this wave. The implemented rows split across two
code-sharing paths, both fully testable at the `ClientWorld` wire level:

- **Own-ship shield power edges** (new client one-shots on the LOCAL ship's
  streamed `ShieldFraction`, extending the P717 shields-offline edge):
  **SIM-0241** (shield power-up `activateSound`, shieldIGC.cpp:94) and
  **SIM-0242** (shield power-down `deactivateSound`, shieldIGC.cpp:185, fired
  on the same overload-collapse edge that already plays the SIM-0243 VO).
  Two brand-new `ClientSoundEventKind`s (`ShieldActivate`, `ShieldDeactivate`),
  two `SoundManager` one-shots (`shieldup.ogg`, `noshld.ogg` — the native
  `fastShieldActivate`/`fastShieldDeactivate` waves, sounddef.mdl:1310-1311),
  two `Game1.Audio` dispatch cases, two `Content.mgcb` registrations, and
  failing-first tests.
- **Reachable same-side quit-reason salutations** (verification of the P736
  `ApplySideLeave` default→SalQuit path, now genuinely reachable because the
  port server broadcasts these reasons):
  **SIM-0454** (QSR_LeaderBooted — teammate booted by leader) and
  **SIM-0460** (QSR_Quit — teammate voluntarily quit), both voice
  `salQuitSound` (wave `vo_sal_quit`). Failing-first on the trigger: pre-P722
  the server only emitted LinkDead/SwitchingSides, so these were unreachable;
  now `HandleQuitMission` (Server.Moderation.cs:19) broadcasts Quit and
  LeaderBooted.
- **Drone in-transit chatter reconciliation** (the port's drone combat AI
  already emits two of the eight native `droneInTransitSound` combat
  chatters): **SIM-0260** ("Switching target", Ship.NpcUpdates.cs:306) and
  **SIM-0262** ("Attacking", Ship.NpcUpdates.cs:161), both carrying
  `droneInTransitSound`(1337) → the client's `ChatVoiceOver` path →
  `vo_miner_intransit`. Verified end-to-end at the wire level.

**Marked `n/a` with precise reasons** (structurally unreachable, each needing
server systems outside the sounds area): the six sibling boot/quit reasons the
port server can never produce — **SIM-0455** (QSR_OwnerBooted, no mission-owner
moderation), **SIM-0456** (QSR_AdminBooted, no admin system), **SIM-0457**
(QSR_SquadChange, no squads), **SIM-0458** (QSR_RankLimits, no rank limits),
**SIM-0459** (QSR_TeamSizeLimits, no team-size limits), **SIM-0462**
(QSR_DuplicateRemoteLogon, no duplicate-logon detection) — and the five
native defend-command drone chatter branches the port drone AI does not
reproduce — **SIM-0258/0259/0261/0269/0270** (attacking-to-defend,
returning-to-defend, defend-target head). None was silently skipped; each
carries a concrete reason in the matrix.

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 445/592 (75.17%) | **461/559 (82.47%)** |
| unweighted | 165/227 (72.69%) | **171/216 (79.17%)** |

(The weighted denominator shrinks because 11 unreachable rows moved to `n/a`,
which excludes them from eligibility — that is the "reach the practical
maximum" outcome the task calls for. Simulation TOTAL: 935/1331 → 993/1298.)

## Why these rows

Reconnaissance of the 62 open `sounds` rows (15 `partial` + 47 `missing`)
against waves 13–24's reports shows the remaining open space is dominated by
structurally blocked families. The coherent, code-sharing, wire-testable
bundles this wave could genuinely advance are:

1. **Own-ship shield power edges (SIM-0241/0242)** — the client already
   streams the own ship's `ShieldFraction` and edge-tracks its collapse for
   SIM-0243 (P717). The native power-up (shieldIGC.cpp:94) and power-down
   (shieldIGC.cpp:185) cues are two more edges on that same stream — one
   shared code path, two new one-shots, real bundled assets. Cleanly
   failing-first (new enum kinds + new waves).
2. **Reachable same-side quit reasons (SIM-0454/0460)** — the remaining open
   quit rows that the port server can now actually broadcast. They share the
   P736 `ApplySideLeave` path and are reconcilable (were falsely `missing`;
   the code was latent behind an unreachable-trigger blocker).
3. **Drone in-transit chatter reconciliation (SIM-0260/0262)** — the port's
   drone combat AI (Ship.NpcUpdates.cs) already emits two of the eight native
   `droneInTransitSound` combat chatters; these were `missing` only because
   nobody reconciled the producer. Verified.
4. **The 11 genuinely unreachable siblings → n/a** — no port system can
   produce their native triggers (server moderation/squad/rank/team-size/
   duplicate-logon, or the defend-command drone-AI branch). Converting them
   with reasons advances the area to its practical maximum.

Families assessed and ruled out for this wave (documented, not skipped):
**station/economy completion/need-rock** rides the SIM-0399 core sound-def
table (Codex P732 owns the voice-over table); **UI/screens** mouseover/click
cues are GUI-layer (not wire/world-testable per this task's verification);
**ambient/positional loops** remain partly wired and mostly blocked on
model/sector asset specifics; the **depleted reload-failure** cues
(SIM-0445/0452/0453) and **cargo-full/mounted** (SIM-0267/0239) stay `partial`
— they need a server launcher-magazine / mount-progress / pickup-rejection
wire (documented P737).

## Implementation

### SIM-0241 / SIM-0242 — own-ship shield power edges (new client one-shots)

Native plays both cues from the OWN `CshieldIGC` object:
- power-up (`m_typeData->activateSound`): when the shield regenerates from
  below 5% to at-or-above 5% (shieldIGC.cpp:94,
  `previousFraction < 0.05 && m_fraction >= 0.05`);
- power-down (`m_typeData->deactivateSound`): when overload damage knocks the
  shield past the downed threshold (shieldIGC.cpp:185), the SAME branch that
  plays the shields-offline VO (:186, SIM-0243).

The port's client already edge-tracks the own ship's streamed `ShieldFraction`
for the SIM-0243 collapse. P741 adds:
- `ShipStatusAlarms.ShieldActivateThreshold = 0.05f` (native threshold).
- `_prevOwnShieldFraction` tracking in `UpdateOwnStatusAlarms`; a rising-cross
  from below to at-or-above the threshold records `ShieldActivate` (once per
  power-up); the existing positive→≈0 collapse records `ShieldDeactivate`
  alongside `ShieldsOffline` (once per collapse).
- `ClientSoundEventKind.ShieldActivate` / `ShieldDeactivate`.
- `SoundManager.PlayShieldActivate()`/`PlayShieldDeactivate()` playing
  `shieldup.ogg` / `noshld.ogg` — the native `fastShieldActivate` /
  `fastShieldDeactivate` waves (sounddef.mdl:1310-1311), copied from
  `~/Development/Artwork` and registered in `Content.mgcb`.
- `Game1.Audio` dispatch cases with the `[monogame] audio:` console lines.

Native plays these as positional `ThreeDSound` SFX on the ship; because they
are the LOCAL ship's own cues, the port plays them as one-shots on the
established warning one-shot path (equivalent at the cockpit for the own
ship).

### SIM-0454 / SIM-0460 — reachable same-side quit reasons

Native's `RemovePlayerFromSide` same-side branch maps each quit reason to a
cue (clintlib.cpp:3928-3999): QSR_LeaderBooted (:3934) and QSR_Quit (:3970)
both play `salQuitSound`. The port's `ApplySideLeave` (ClientWorld.cs:4617)
already routes every same-side reason that is not LinkDead/SwitchingSides to
`SalQuit` (wave `vo_sal_quit`). What was missing was the TRIGGER: the server
didn't broadcast these reasons. Since P722's integration, `HandleQuitMission`
(Server.Moderation.cs:19) accepts a leader boot (`LeaderBooted`) or a
self-quit (`Quit`) and `ReleasePeerWithReason` broadcasts the QUIT_MISSION
with that reason (Server.Lifecycle.cs:1856). So both rows are now reachable
and verified at the `ClientWorld` level: a same-side human teammate leaving
with `QuitSideReason.LeaderBooted` or `Quit` fires `SalQuit`.

### SIM-0260 / SIM-0262 — drone in-transit chatter reconciliation

Native broadcasts `droneInTransitSound`(1337) team chatters from the drone
combat AI ("Switching target to %s", shipIGC.cpp:2561; "Attacking %s",
shipIGC.cpp:2779). The port's drone combat AI already emits both —
`SendDroneChat(DroneInTransitSound, "Switching target to {Id}")`
(Ship.NpcUpdates.cs:306) and `SendDroneChat(DroneInTransitSound,
"Attacking {Id}")` (Ship.NpcUpdates.cs:161). The client resolves any
VoiceOver-carrying chat to `ChatVoiceOver` (trekigc.cpp:4530-4536) and the
drone id to `vo_miner_intransit` via `DroneChatterCue.Resolve` (DroneChatterCue.cs:52). These rows were `missing` only because no wave reconciled the
already-present producer + client path; verified with a wire test.

## Failing-first

- The two new `ClientSoundEventKind` members (`ShieldActivate`,
  `ShieldDeactivate`) did not exist before P741: the new
  `ClientWorldSoundEventTests` cases reference them, a pre-change compile
  error (CS0117) — the same mechanism P711/P716/P717/P735/P736/P737
  documented.
- Neither `shieldup.ogg` nor `noshld.ogg` was bundled before P741:
  `P741SoundAssetTests` fails on the pre-change tree and passes after the
  waves + `Content.mgcb` entries land.
- SIM-0454/0460/0260/0262 are reconciliation rows: their production code was
  already present (latent behind a previously-unreachable trigger, or an
  unreconciled producer); the new tests pin the now-reachable behavior. Their
  failing-first is the trigger reaching the pre-existing cue path (the rows
  were `missing`, not a code defect).

## Tests

- `ClientWorldSoundEventTests` (Allegiance.ClientCore.Tests): renamed the
  P717 collapse test to expect BOTH `ShieldsOffline` + `ShieldDeactivate`
  (native plays both on the same branch), and added 6 new cases: shield
  regenerate-past-5% → `ShieldActivate`, already-above → none, stays-below →
  none, collapse-no-activate, teammate booted-by-leader → `SalQuit`,
  teammate-voluntary-quit → `SalQuit`, drone-in-transit chat → `ChatVoiceOver`
  + wave resolution. **74/74 ×3.**
- `P741SoundAssetTests` (Allegiance.ClientCore.MonoGame.Tests, new): the two
  waves present + mgcb-registered. **1/1 ×3** (part of the 9-test MonoGame
  sound-asset batch, 9/9 ×3).
- Targeted existing: `ShipStatusAlarmSoundTests` 12/12 ×3; MonoGame sound
  asset/announce/deploy/station/thriller batch 9/9 ×3.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings,
  0 errors** in both Debug and Release.
- Failing-first: see above (two new enum kinds are pre-change compile errors;
  two new waves fail the asset test pre-change).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release)** — 1m37, 1m37, 1m34, 1m33, 1m38. The change records
  client-side one-shot sound events only; it cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh`), exactly 1 run** (the lighter gate for
  this task), run as a single tracked pass over all four assemblies sequentially
  (build 0/0, then each project's MAIN lane, then QUARANTINE):

  | pass | result |
  |---|---|
  | Lobby MAIN | passed, 0 fail (19/19) |
  | MonoGame MAIN | passed, 0 fail (2431/2434, 3 skip) |
  | ClientCore MAIN | passed, 0 fail (1302/1303, 1 skip: pre-existing `P660ShowcaseLiveTests` finale-rearm `SkippableFact`) |
  | Core MAIN | 4835/4836 (1 fail: `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` — documented load flake) |
  | Quarantine | ClientCore 3/3; Core `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` reported by the runner as a quarantine flake (passes in isolation, not failing); Lobby & MonoGame none |

  The single Core MAIN failure is a **pre-existing, documented live-wire load
  flake in code this wave did not touch** (the transport pool-churn test):
  `docs/TEST_FLAKES.md` already lists it (many prior occurrences, always 3/3
  isolated passes), and it passes **3/3 in isolation** (221 ms each) here, so
  per the gate it is a flake, not a blocker — no whole-suite re-run was made.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — two new shield-edge
  kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `_prevOwnShieldFraction` +
  the shield power-up/power-down edges in `UpdateOwnStatusAlarms`
  (SIM-0241/0242). (Quit/drone paths already present; verified.)
- `src/Allegiance.ClientCore/State/ShipStatusAlarms.cs` —
  `ShieldActivateThreshold`.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — two fields, loads,
  one-shot players, disposals (`shieldup`, `noshld`).
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — two dispatch cases.
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/shieldup.ogg`,
  `noshld.ogg` (new, from Artwork) + `Content/Content.mgcb` registrations.
- `tests/.../ClientWorldSoundEventTests.cs` (+6 cases, 1 renamed/strengthened),
  `tests/.../P741SoundAssetTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 6 rows → `implemented` (verified),
  11 rows → `n/a` with reasons; **LF endings kept**.
- `docs/reports/P741_PARITY_WAVE25.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| shield power-up cue | shieldIGC.cpp:94 | design | own-ship ShieldFraction rising past 5% → ShieldActivate (shieldup) |
| shield power-down cue | shieldIGC.cpp:185 | design | own-ship collapse edge → ShieldDeactivate (noshld), same branch as SIM-0243 |
| leader-boot salutation | clintlib.cpp:3934 | design | same-side QSR_LeaderBooted → SalQuit (vo_sal_quit), reachable via HandleQuitMission |
| voluntary-quit salutation | clintlib.cpp:3970 | design | same-side QSR_Quit → SalQuit, reachable via HandleQuitMission |
| drone "switching target" chatter | shipIGC.cpp:2561 | design | port drone AI emits it with 1337; client ChatVoiceOver → vo_miner_intransit |
| drone "attacking" chatter | shipIGC.cpp:2779 | design | port drone AI emits it with 1337; client ChatVoiceOver → vo_miner_intransit |
| remaining boot/quit reasons | clintlib.cpp:3939-3991 | design (trigger unreachable) | n/a: no owner/admin/squad/rank/team-size/duplicate-logon systems |
| defend-command drone chatter | shipIGC.cpp:2386-2692, shipIGC.h:2408-2449 | design (AI absent) | n/a: defend-command drone-AI branch not reproduced |

## Modern alternative considered

Native fires the shield cues from the client's own `CshieldIGC` physics object
as positional `ThreeDSound`s on the ship; this pure-renderer client edge-tracks
the authoritative streamed `ShieldFraction` the server already sends — no new
wire, and the cues are the local ship's own, so one-shot playback is
equivalent. The two shield SFX (`shieldup`/`noshld`) are cockpit-range on the
existing warning bus. For the quit and drone rows no new audio engine work was
needed: the established "emit an event id on the wire, resolve the asset on
the receiver" split (used across the whole parity-sounds programme) already
carries `SalQuit` and `ChatVoiceOver`. No voice-budget change warranted.

## Limitations

- The six unreachable quit reasons (SIM-0455/0456/0457/0458/0459/0462) and
  five defend-command drone chatters (SIM-0258/0259/0261/0269/0270) are
  `n/a` with reasons: the port server has no path producing those native
  triggers. Reaching them requires server systems (owner/admin moderation,
  squads, rank/team-size limits, duplicate-logon detection, a defend-command
  drone-AI branch) that are out of the sounds area.
- SIM-0242's power-down cue is tied to the same streamed collapse edge as
  SIM-0243; native fires it from an exact damage-leakage branch. The
  player-visible intent (hear the shield go down under overload) is preserved.
- GUI-layer UI cues (load.cpp/sectormap/console/teamscreen/teleportpane/
  loadout/teampane/Slideshow mouseover/click) remain open with reasons — they
  are implementable in the GUI but not wire/world-testable under this task's
  verification; station completion/need-rock stays on the Codex P732
  voice-over table. None was silently skipped.
