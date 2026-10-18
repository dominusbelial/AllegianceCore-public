# P736 — Parity wave 23: simulation SOUNDS, eleventh bundle (presence / side-change salutations)

## Summary

Ported **5 open `sounds` rows → `implemented`** — the **presence / side-change
salutation family**, reproduced client-side from the wire messages the port
server **already broadcasts** (JOIN_SIDE on every side join; QUIT_MISSION on a
LinkDead disconnect; QUIT_SIDE on the quit-half of a same-mission side switch),
with **no server change**: **SIM-0464** salRecruitsArrivedSound,
**SIM-0465** salEnemyJoinersSound, **SIM-0461** salChangeSidesSound,
**SIM-0463** salMIASound, and **SIM-0468** (the dynamic other-side leave voice →
salQuitSound for an ally, salEnemyLeavesSound for an enemy). All ride the
established `ClientSoundEvent → Game1.Audio → SoundManager` one-shot path from
waves 13–22, and all five assets (`vo_sal_recruitsarrived`, `vo_sal_enemyjoiners`,
`vo_sal_traitor`, `vo_sal_mia`, `vo_sal_quit`, `vo_sal_enemyleaves`) are now
bundled from `~/Development/Artwork` + registered in `Content.mgcb`.

This is the largest remaining asset-present cluster whose triggers the port
already streams to the client. It is **fully client-side and fully testable at
the `ClientWorld` level**, and it lifts the asset-inventory blocker P735
recorded for the salutation family. The remaining same-side boot/quit
salQuitSound rows (**SIM-0454–0460, SIM-0462**) stay open **with a reason**: the
port server only ever emits `LinkDead` and `SwitchingSides` quit reasons
(`QuitSideReason.cs` names only those two reachable values), so the specific
boot/kick/rank/squad/plain-quit reasons are never received — documented in the
matrix, not silently skipped.

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 410/592 (69.26%) | **425/592 (71.79%)** |
| unweighted | 150/227 (66.08%) | **155/227 (68.28%)** |

## Why these rows

The task lists the remaining sounds families as weapons/ordnance, station/economy,
UI/screens and ambient/positional loops. Reconnaissance of the 85 open rows (up
from P735's 80) and of waves 13–22's reports shows the **biggest remaining
asset-present cluster is the presence/side-change salutation family** (14 rows,
weight 3 each). P735 documented the salutation family's asset-inventory blocker
as lifted and deferred it to a client wire/trigger + server quit-reason-broadcast
follow-up. This wave does the **client side** of that: every one of the 5
implemented rows is triggered by a wire message the server already sends, so
none of them needs a server change, and all five share the one new
`Apply(JoinSide)`-salutation + `ApplySideLeave` code path — a coherent bundle
sharing code by construction.

The same-side **salQuitSound boot/quit** rows (SIM-0454–0460, 0462) are the one
part of the family that cannot yet be ported faithfully: they require the server
to broadcast each specific quit reason (leader boot, owner boot, admin boot,
squad change, rank-limits, team-size limits, plain quit, duplicate remote logon).
The port server today only emits LinkDead and SwitchingSides
(`QuitSideReason.cs`), so those specific reasons never reach the client — a
server-touching follow-up, documented in the matrix notes, not silently skipped.
(The `ApplySideLeave` same-side `default` branch maps them to `SalQuit` when the
server ever emits them.)

## Implementation

### The wire triggers (all already broadcast — no server change)

| wire | port site | salutation |
|---|---|---|
| `JOIN_SIDE(125)` | Server.Lobby.cs `BroadcastJoinSide` on every `TryJoinSide` | recruits-arrived / enemy-joiners |
| `QUIT_MISSION(124)` | Server.Lifecycle.cs `BroadcastQuitMission` on LinkDead | MIA (same side) / leave voice (other side) |
| `QUIT_SIDE(126)` | Server.Lobby.cs `BroadcastQuitSide` on SwitchingSides | change-sides (same side) / leave voice (other side) |

### SIM-0464 / SIM-0465 — join salutations (new client one-shots)

Native voices these from `BaseClient::AddPlayerToSide` (clintlib.cpp:4140-4172)
when a human player joins a side: a same-side joiner plays
`salRecruitsArrivedSound` (:4155), an enemy (non-allied) side joiner plays
`salEnemyJoinersSound` (:4162), gated additionally on us holding a real side;
an allied-but-different side join is TEXT ONLY (sal id 0, :4151). The port
extends the existing `Apply(JoinSide)` (P438) with native's exact gates —
human (`PlayerInfo.PilotType == PilotTypePlayer`), mission `STAGE_STARTED`,
not our own ship — and records `SalRecruitsArrived` (own side) or
`SalEnemyJoiners` (non-allied side, when `MySideId` is a real side). An
allied-different join records nothing, exactly as native plays sal id 0.

### SIM-0461 / SIM-0463 / SIM-0468 — leave salutations (new client one-shots)

Native voices these from `BaseClient::RemovePlayerFromSide` (clintlib.cpp:3867-4009).
The port adds `Apply(QuitMission)`, `Apply(QuitSide)` and a shared
`ApplySideLeave(shipId, reason)` with native's gates (human, STAGE_STARTED, not
self) and branches:
- **Same-side leaver** (leaver's `SideId` == `MySideId`): reason picks the cue —
  `LinkDead` → `SalMia` (SIM-0463, clintlib.cpp:3999); `SwitchingSides` →
  `SalChangeSides` (SIM-0461, clintlib.cpp:3975); any other same-side reason →
  `SalQuit` (SIM-0462's path, dormant until the server broadcasts such a reason).
- **Other-side leaver** (clintlib.cpp:4004-4009): allied → `SalQuit`, enemy
  (non-allied) → `SalEnemyLeaves` — this is SIM-0468, the dynamic leave voice.

Both `QUIT_MISSION` and `QUIT_SIDE` flow through the same `ApplySideLeave`;
they differ only in which reason the server attaches (LinkDead vs
SwitchingSides).

### Audio plumbing

Six new `ClientSoundEventKind` members (`SalQuit`, `SalChangeSides`, `SalMia`,
`SalEnemyLeaves`, `SalRecruitsArrived`, `SalEnemyJoiners`), six `SoundManager`
one-shot players + loads + disposals (waves `vo_sal_quit`, `vo_sal_traitor`,
`vo_sal_mia`, `vo_sal_enemyleaves`, `vo_sal_recruitsarrived`,
`vo_sal_enemyjoiners`), and six `Game1.Audio` dispatch cases, each with the
`[monogame] audio:` console line convention. `Content.mgcb` registers all six
waves; the oggs are copied from `~/Development/Artwork`.

## Failing-first

All six event kinds did not exist before P736: stashing the production changes
and rebuilding yields `error CS0117: 'ClientSoundEventKind' does not contain a
definition for 'SalRecruitsArrived'` (and the same for SalEnemyJoiners, SalMia,
SalChangeSides, SalEnemyLeaves, SalQuit) — the new `ClientWorldSoundEventTests`
cannot compile on the pre-change tree. The `P736SoundAssetTests` likewise fails
pre-change because none of the six waves was bundled.

## Tests

- `ClientWorldSoundEventTests` (Allegiance.ClientCore.Tests) — 10 new salutation
  cases (join/quit, same-side/enemy/allied, drone non-human, not-started,
  own-ship silence) + the existing suite. 59/59 total.
- `P736SoundAssetTests` (Allegiance.ClientCore.MonoGame.Tests, new) — all six
  waves present in `Content/Sounds` + registered in `Content.mgcb`. 1/1.

New/affected tests **3× clean** (59/59, 1/1, ShipStatusAlarmSoundTests 12/12,
LiveTreasurePickupTests 4/4).

Targeted existing tests **3× clean**: `P735SoundAssetTests`, `P723SoundAssetTests`,
`P714AnnounceSoundTests`, `P719StationEconomySoundTests`, `P717DeploySoundTests`,
`P721ThrusterLoopSoundTests` (MonoGame), plus the ClientWorld sound tests above.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first: see above (six new kinds are pre-change compile errors; six
  new waves fail the asset test pre-change).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release)** — 1m36s, 1m37s, 1m36s, 1m34s, 1m39s. The change records
  client-side one-shot sound events only; it cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh` lanes), exactly 1 run each** (the lighter
  gate). Because the shared terminal tool hard-caps a single blocking call at
  ~420s and the assemblies individually exceed that, each assembly's MAIN lane
  was run once as its own tracked foreground pass (the script already runs
  assemblies sequentially, so this is the same one-pass execution), and the
  QUARANTINE lane ran once per project with the script's isolation re-run:

  | pass | result |
  |---|---|
  | Lobby MAIN | 19/19, 0 fail |
  | MonoGame MAIN | 2320/2323 (3 skip), 0 fail |
  | ClientCore MAIN | 1189/1190 (1 skip), 0 fail |
  | Core MAIN | 4757/4757, 0 fail |
  | Quarantine | ClientCore 3/3; Core 18/18; MonoGame none |

  **Zero failures in the entire suite** — the first fully clean run in recent
  waves (live-network load flakes from waves 13–21 did not reappear). No test
  was re-run hoping for a clean result; every assembly's numbers above are the
  single measured pass.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — six new salutation kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(QuitMission)`,
  `Apply(QuitSide)`, `ApplySideLeave` (SIM-0461/0463/0468); join salutation in
  `Apply(JoinSide)` (SIM-0464/0465); QUIT_MISSION/QUIT_SIDE dispatch cases.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `QuitMission.Fmid`
  (124) + `QuitSide.Fmid` (126) decode + world dispatch.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — six fields, loads,
  one-shot players (`PlaySalQuit/ChangeSides/Mia/EnemyLeaves/RecruitsArrived/
  EnemyJoiners`) + disposals.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — six dispatch cases.
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/vo_sal_{quit,mia,traitor,
  recruitsarrived,enemyjoiners,enemyleaves}.ogg` (new, from Artwork) +
  `Content/Content.mgcb` registrations.
- `tests/.../ClientWorldSoundEventTests.cs` (10 new cases),
  `tests/.../P736SoundAssetTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 5 rows → `implemented` (SIM-0461, 0463,
  0464, 0465, 0468), evidence `verified`; the same-side boot/quit rows
  (SIM-0454–0460, 0462) annotated with the server-broadcast blocker; **LF
  endings kept** (validated by `scripts/parity-scorecard.py`).
- `docs/reports/P736_PARITY_WAVE23.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| join salutation (recruits/enemy-joiners) | clintlib.cpp:4140-4172 | design | client one-shot on JOIN_SIDE, native gates (human, STAGE_STARTED, not self); allied-other side → text only, no sound (native sal 0) |
| same-side quit → reason cue | clintlib.cpp:3928-3999 | design | ApplySideLeave: LinkDead→SalMia, SwitchingSides→SalChangeSides, other→SalQuit |
| other-side leave → ally/enemy voice | clintlib.cpp:4004-4009 | design | ApplySideLeave other-side branch: allied→SalQuit, enemy→SalEnemyLeaves (SIM-0468) |
| same-side boot/quit salQuitSound | clintlib.cpp:3952-3991 | design (trigger not yet reachable) | dormantly mapped in ApplySideLeave default; needs server quit-reason broadcasts to fire |

## Modern alternative considered

Native plays these as server-side `PlayNotificationSound` / chat-with-sound
calls whose buffers resolve at a `SoundSite`; the port emits a typed event id
(`ClientSoundEventKind`) and resolves the wave client-side — the established
"emit an event, resolve the asset on the receiver" split used across the whole
parity-sounds programme. No audio-engine change warranted; the six new one-shots
are plain cockpit-range VOs on the existing voice-budget path (MutexSal group),
so they respect the voice budget with no extra work. The one honest deviation:
native maps `salQuitSound` to the `vo_sal_mia` wave (sounddef.mdl:827-828), so
"MIA" and "X quit" share one buffer; this port uses the dedicated `vo_sal_quit`
wave so the plain-quit cue is distinguishable from the MIA cue the player hears.
The larger deferral is native's full boot/kick/quit reason set, which needs the
server to broadcast those quit reasons (a server-touching follow-up, documented).

## Limitations

- **SIM-0454–0460, SIM-0462** (same-side boot/quit salQuitSound) remain
  `missing` **with reason**: the port server only broadcasts LinkDead +
  SwitchingSides quit reasons (`QuitSideReason.cs`), so the specific boot/kick/
  rank/squad/plain-quit reasons are never received. The client `ApplySideLeave`
  `default` branch already maps them to `SalQuit` so enabling them later is a
  server-broadcast-only change.
- **SIM-0462** is served by the same `SalQuit` wave; its same-side trigger
  (QSR_Quit) is part of that same server-broadcast follow-up.
- The larger families (weapons/armament reload — per-magazine counts; drone
  in-transit chatter — server wingman AI; command-ack VOs — peer command flow;
  station completion/need-rock — SIM-0399 core sound-def table; ambient/positional
  loops — partly wired) remain open **with reasons** documented in waves 13–22
  and the matrix; none was silently skipped.

## P738 merge onto master

Retained all six salutation kinds, their playback cases and waves, while
combining them with P718/P722's existing departure lifecycle. Removed the
automatically duplicated `QuitMission`/`QuitSide` switches and `Apply` overloads.
The cue is classified **before** side cleanup changes the player's side or
mission cleanup removes the player. `QUIT_MISSION` retains its actual reason
for both the cue and departure feedback; local departure still resets the
world, disconnects the session and stops the remaining packet. Remote cleanup
still removes roster/request/mute state. Repeated departures do not refire a
cue, covered by strengthened sound-event assertions.

Native side cleanup and salutation decisions:
`~/Development/Allegiance/src/clintlib/clintlib.cpp:3889-4034`; local mission
shutdown: `~/Development/Allegiance/src/clintlib/clintlib.cpp:3805-3848`.
The port intentionally uses the received `QUIT_MISSION` reason for its sound,
as P736 did; native's defensive fallback for a mission departure from a real
side instead supplies `QSR_Quit`
(`~/Development/Allegiance/src/clintlib/appmsg.cpp:2516-2524`).

The earlier report's "only LinkDead and SwitchingSides" blocker predates P722:
this integration also has authorized leader-boot and ordinary-quit broadcasts.
Matrix rows are preserved according to their three-way row changes, without
claiming additional newly verified completion for those older missing rows.

Release build: **0 warnings, 0 errors**. All incoming/affected methods run **3×
alone**; showcase **5/5**. See [P737's integration notes](P737_PARITY_WAVE24.md#p738-merge-onto-master)
for the complete measured gate and `P738_master_assets/` for raw evidence.
