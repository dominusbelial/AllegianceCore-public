# P742 — Parity wave 26: simulation SOUNDS, fourteenth bundle (station/economy sound-id chat voice cues + close blocked rows)

## Summary

Ported **2 open `sounds` rows → `implemented`** (the two remaining genuinely-reachable
members of the station/economy voice-cue family) and closed **4 provably-unreachable
rows → `n/a` with concrete reasons**, so the sounds area advances toward its practical
maximum. The two implemented rows are **reconciliations**: the port's server **already
broadcasts** both chats (one shared code path), and the port's client **already resolves**
them to their sounddef.mdl waves — the rows were `partial` only because nobody reconciled
the live producers:

- **SIM-0236** — `GetCompletionSound` ("Finished building %s", buildingeffectigc.h:320):
  the port's `OnStationBuilt` (Server.Research.cs:23) broadcasts the team chat carrying
  `station.Type.CompletionSound`; the client resolves it to the per-station-type completion
  wave (`expansionCompletedSound`=805→`vo_builder_expansion`, garrison=800, mine=808).
- **SIM-0255** — `GetConstructorNeedRockSound` ("New constructor requesting asteroid.",
  shipIGC.cpp:2319): the port's `BroadcastConstructorNeedRock` (Server.Economy.cs:843, P458)
  broadcasts the team chat carrying `stationType.ConstructorNeedRockSound`; the client
  resolves it to the resource-specific `vo_request_builder*` wave (constructorReq* 745-749).

Both are verified end-to-end at the `ClientWorld` wire level with failing-visible tests
(the rows were `partial`/`missing`, not code defects; the tests pin the now-confirmed
resolution and are the same evidence class P741 used for its drone-chatter reconciliations).

**Marked `n/a` with precise reasons** (structurally unreachable — each needs a server or
renderer system outside the sounds area, not a missing cue):

- **SIM-0251** (`droneEnemyOnScopeSound`, shipIGC.cpp:1901): the port's NPC flee is
  hull/shield-**damage**-triggered only (Ship.cs:7381-7383 maps native `bDamage`,
  shipIGC.cpp:1780-1801); the separate **healthy enemy-on-scope** flee branch that plays
  this cue (shipIGC.cpp:1899) is not modelled in the port drone AI — the same class of
  blocker as the P741 defend-command `n/a` rows. The client already resolves
  `1323→vo_miner_enemyonscope` (sounddef.mdl:731) but no producer exists.
- **SIM-0395 / SIM-0413** (`salRepairedAtCarrierSound`, trekigc.cpp:4237 / appmsg.cpp:163):
  the cue fires on the own ship completing a carrier repair/**relaunch**; the port renderer
  has **no client `RELAUNCH_SHIP` handler** (documented since P719), so the relaunch-at-carrier
  edge is unobservable. The catalog resolves `1399→vo_sal_shipisrepaired` (sounddef.mdl:802)
  but no trigger exists.
- **SIM-0412** (`m_soundId`, trekmdl.cpp:471): native plays it from an mdl **animation
  `OnEvent`** via `GetWindow()->StartSound`; the port has no mdl animation-event sound
  playback system, so this GUI/model-animation cue is unrepresentable.

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 461/559 (82.47%) | **468/550 (85.09%)** |
| unweighted | 171/216 (79.17%) | **173/212 (81.60%)** |

(The weighted denominator shrinks because 4 unreachable rows moved to `n/a`, excluding them
from eligibility — the "reach the practical maximum" outcome the task calls for. Simulation
TOTAL: 993/1298 → 1000/1289.)

## Why these rows

Reconnaissance of the 45 remaining open `sounds` rows (30 `missing` + 15 `partial`) against
waves 13-25 shows the area is now **nearly exhausted of cheap, faithful, wire-testable
rows**. Each remaining open family is blocked on a non-sound system:

| remaining family | open rows | blocker |
|---|---|---|
| **station/economy voice cues** (completion, constructor-need-rock) | SIM-0236, 0255 | *none — reconciled this wave (server already broadcasts)* |
| drone/AI voice chatter | SIM-0251 | enemy-on-scope flee branch not modelled in port drone AI (P741-style n/a) |
| accept/reject command VO (voNegative/voAffirmative) | SIM-0294/0295/0298 | needs client outgoing "Negative."/"Affirmative." response chats + last-sender tracking (a command feature) |
| UI/screens GUI cues | SIM-0272/0273/0274/0285/0317-0338 | GUI-layer mouseover/click/key cues — not wire/ClientWorld-testable (P719/P741 documented) |
| depleted ordnance (reload-FAILURE), cargo-full, mounted, chaff-deploy | SIM-0445/0452/0453/0446/0239/0267 | needs server launcher-magazine / pickup-rejection / mount-streaming / CREATE_CHAFF-launcher wires (P717/P737 documented) |
| ambient/positional loops (station interior, object ambient) | SIM-0345/0411 | needs model/sector ambient-loop assets + interior-loop path |
| weapon-mount activate / manned-turret turn loops | SIM-0347/0348 | renderer fire model has no continuous-charge / manned-turret-rotation state (P721 documented) |

The two implemented rows are the **last clean slice** of the station/economy voice-cue
family: they share one code path by construction — the server's
`BroadcastTeamChat(... voice=SoundID ...)` producer (`Server.Economy.cs:866`,
`Server.Research.cs:23`) feeding the client's `ClassifyChatSound`/`ChatVoiceOver`
resolution (trekigc.cpp:4530-4536) — and are fully testable at the `ClientWorld` wire level
(exactly the P741 drone-chatter reconciliation pattern).

## Implementation

### The wire trigger (already broadcast — reconciliation)

Native plays both cues as team chats from the sim's construction/site code:

| row | native trigger | cite |
|---|---|---|
| SIM-0236 | `SendChatf(... GetCompletionSound(), "Finished building %s", ...)` when an asteroid finishes building into a station | buildingeffectigc.h:320 |
| SIM-0255 | `Complain(((IstationTypeIGC*)...)->GetConstructorNeedRockSound(), "Constructor requesting asteroid.")` from the builder drone's pick-default-order | shipIGC.cpp:2319 |

The port's server broadcasts both today:

| row | port producer | cite |
|---|---|---|
| SIM-0236 | `OnStationBuilt` → `BroadcastTeamChat(mission, side, station.Type.CompletionSound, "Finished building {name}")` | Server.Research.cs:23 |
| SIM-0255 | `CompleteStationType` → `BroadcastConstructorNeedRock(... stationType.ConstructorNeedRockSound ...)` (P458) | Server.Economy.cs:843 |

The port's client resolves any chat carrying a non-`NA` `VoiceOver` to
`ClientSoundEventKind.ChatVoiceOver` and maps the id to its wave via
`NativeVoiceCatalog.Resolve` (ClientWorld.ReceiveChat, ClientWorld.Callbacks.cs:83-99;
`ClassifyChatSound`, ClientWorld.cs:3876-3907; native trekigc.cpp:4530-4536). Both sound
families are present in the generated catalog:

- completion: `expansionCompletedSound`=805→`vo_builder_expansion`,
  `garrisonCompletedSound`=800→`vo_builder_garrison`, `mineCompletedSound`=808→`vo_builder_mine`;
- constructor request: `constructorReq{Carbon,Uranium,Silicon,Helium,Special}Sound`=
  746/745/747/748/749 → `vo_request_builder{carbon,uranium,silicon,helium,special}`
  (sounddef.mdl:1170-1175).

No production code change was required — these two rows were `partial` only because no wave
reconciled the already-live producer + client path.

### Tests

`ClientWorldSoundEventTests` (+8): two `[Theory]`s feed a `ChatMessage` with the server's
actual `VoiceOver` id and assert `ChatVoiceOver` is recorded with the exact resolved wave
stem — `StationCompletionChat_ResolvesToCompletionWave` (3 cases) and
`ConstructorNeedRockChat_ResolvesToResourceWave` (5 cases). These pin the end-to-end
resolution so a catalog mis-read or a server id that drifts off-catalog fails fast.

## Failing-first

The two rows were `partial`/`missing` (not code defects): their producers and the client
resolution path were already live but never reconciled — the new tests pin the now-confirmed
behavior, the same failing-first evidence class P741 used for the drone in-transit chatter
rows (the trigger reaching the pre-existing cue path). Because no new enum kind or wave
asset was added, the tests are failing-**visible** rather than failing-compile: they encode
the previously-unreconciled contract.

## Tests

- `ClientWorldSoundEventTests` (Allegiance.ClientCore.Tests): **82/82 ×3** (74 at P741 + 8
  new reconciliation cases).
- Targeted existing: the sound-event suite itself is the code-adjacent class (nothing else
  touched). No new assets/enum kinds → no MonoGame asset test needed.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors** in
  both Debug and Release.
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release)** — 1m35, 1m37, 1m36, 1m33, 1m37. The change is test+matrix only; it
  cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh`), exactly 1 run** (the lighter gate for this task),
  run as a single tracked pass over all four assemblies sequentially (build 0/0, then each
  project's MAIN lane, then QUARANTINE):

  | pass | result |
  |---|---|
  | Lobby MAIN | passed, 0 fail (19/19) |
  | MonoGame MAIN | passed, 0 fail (2431/2434, 3 skip) |
  | ClientCore MAIN | 1309/1311, 1 skip — **1 fail**: `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` (live-wire load flake, **3/3 isolated** here, already in `docs/TEST_FLAKES.md`) |
  | Core MAIN | 4832/4836 — **4 fail**, all pre-existing documented live-wire/transport load flakes in code this wave did not touch: `TcpTransportCoalesceAndReasonThreadPerPeerTests.Coalesce_distinct_keys_remain_independent`, `Transport.P670SendStallTests.Departed_peer_bytes_remain_in_host_window_totals(ThreadPerPeer)`, `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`, `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` — all **3/3 isolated** here, all in `docs/TEST_FLAKES.md` |
  | Quarantine | ClientCore 1 flake (`ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`, documented); Core 2 flakes (`LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`, `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`) + 1 intermittent `TcpTransportPriorityLanePoolTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog` (2/8 isolated, documented `0/10` load-flake family at `TEST_FLAKES.md:1038`, transport code not touched) |
  | exit | nonzero, driven solely by the pre-existing live-wire/transport load flakes above — none in code this wave touched |

  Every failure is a pre-existing, documented live-wire/transport load flake in code this
  wave did **not** touch (the change is test+matrix only). No test was re-run in the hope of
  a clean run; the numbers above are the single measured pass, and each failing test was
  verified in isolation per the gate.

## Files

- `tests/.../ClientWorldSoundEventTests.cs` — +8 reconciliation cases (SIM-0236/0255).
- `docs/parity/sim-rules-matrix.csv` — SIM-0236, SIM-0255 → `implemented` (verified);
  SIM-0251, SIM-0395, SIM-0413, SIM-0412 → `n/a` with reasons; **LF endings kept**.
- `docs/reports/P742_PARITY_WAVE26.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| station-completion team chat + voice | buildingeffectigc.h:320 | design | server broadcasts "Finished building %s" with CompletionSound; client resolves to vo_builder_* (reconciliation) |
| constructor-need-rock plea + voice | shipIGC.cpp:2319 | design | server broadcasts "New constructor requesting asteroid." with ConstructorNeedRockSound; client resolves to vo_request_builder* (reconciliation) |
| enemy-on-scope flee chatter | shipIGC.cpp:1899-1901 | design (AI branch absent) | n/a: port NPC flee is damage-triggered only; healthy enemy-flee branch not modelled |
| carrier-repair/relaunch cue | trekigc.cpp:4237, appmsg.cpp:163 | design (trigger unreachable) | n/a: no client RELAUNCH_SHIP handler |
| mdl animation-event sound | trekmdl.cpp:471 | design (system absent) | n/a: no mdl animation-sound playback |

## Modern alternative considered

No audio-engine change was warranted. Native plays these cues as team chats carrying a
numeric SoundID that the receiving client resolves to a clip — exactly the "emit an event id
on the wire, resolve the asset on the receiver" split the parity-sounds programme already
uses (the established `ChatVoiceOver`/`NativeVoiceCatalog` path), so both rows required zero
new audio plumbing. As with P741, the drone/economy chatters are short VOs on the existing
voice budget; no mixing-bus or voice-budget change is indicated.

## Limitations

- The implemented set is intentionally small: the sounds area is now **near its practical
  maximum** for rows reachable by the port's existing systems. The two implemented rows are
  the last clean wire-testable slice; the remaining open families (accept/reject command VO,
  UI/screens GUI cues, depleted-ordnance server wires, ambient loops, weapon-mount loops)
  each need a non-sound system that is documented above and in the matrix, and none was
  silently skipped.
- SIM-0294/0295/0298 (`voNegativeSound`/`voAffirmativeSound`) stay `partial`: they are the
  local player's "Negative."/"Affirmative." response chats sent on rejecting/accepting a
  teammate's money request or queued command (WinTrek.cpp:10596,10615,10705) — a client
  outgoing-chat + last-sender-tracking command feature, not a sound cue; left partial with
  the reason in the matrix.
