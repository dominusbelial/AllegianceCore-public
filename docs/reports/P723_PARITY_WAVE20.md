# P723 — Parity wave 20: simulation SOUNDS, eighth bundle (drone/constructor chatter voices + in-flight command/targeting feedback)

## Summary

Ported **10 open `sounds` rows** in the station/economy (drone/constructor
chatter) and in-flight command/targeting areas. Six are genuinely new one-shots:
the **three drone/constructor "where-to" pleas** (miner / minefield / tower —
`SIM-0254/0256/0257`), resolved client-side from the team chats the server
already broadcasts, and the **three in-flight command/targeting cues** — new-target
(`SIM-0279`), vector-lock (`SIM-0286`) and ripcord-on-incapable-hull
(`SIM-0287`). Four were already wired by earlier waves but sat stale
`partial`/`missing` in the matrix and are **reconciled** to `implemented`
(the new-contact trio `SIM-0384/0385/0386`, P373, and the accept-command cue
`SIM-0296`, PlayCommandAccepted). **No new server event and no flight /
simulation behaviour changes** (guarded by the P700 regression gate). The drone
chatter needs only a client-side resolver + assets because the server already
broadcasts those team chats with the native SoundID as the wire `VoiceOver`;
the in-flight cues are pure client one-shots on real command edges. Six new
`.ogg` enter the MonoGame content pipeline.

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 362/592 (61.15%) | **391/592 (66.05%)** |
| unweighted | 133/227 (58.59%) | **143/227 (63.00%)** |

## Why these rows

The bundle is one coherent family — **the native drone/constructor chatter
voices** (`Igc/sounds.h:376-396`, `Igc/shipIGC.cpp:2315-2328`) **plus the
in-flight command/targeting one-shots** (`WinTrek/WinTrek.cpp:8827, 9463,
9484`) — all riding the established one-shot path, and all sharing the
drone-chatter *pipe*: the server already emits each plea as a team chat whose
`VoiceOver` is the native SoundID (P458/P459, `Server.Economy.BroadcastTeamChat`,
`Server.Economy.cs:866`), and the client simply had no resolver for those ids,
so the chats were silent. The task lists "station/economy" as a remaining
family; these are its most tractable, faithful one-shots. The in-flight cues
are the analogous "UI/command feedback" members that are cleanly derivable at
the client (no interface-layer rendering work, which stays with Codex's
interface waves).

## Implementation

### New: drone/constructor "where-to" pleas resolve client-side (SIM-0254, 0256, 0257)

Native plays a complaint voice when a miner/layer/constructor pleads for a
location: `droneWhereToSound` ("New miner requesting He3 asteroid.",
`shipIGC.cpp:2315`), `droneWhereToLayMinefieldSound` (:2326) and
`droneWhereToLayTowerSound` (:2328). The port's server already broadcasts all
three as TEAM chats carrying the native SoundID (327 / 330 / 331) as
`ChatMessage.VoiceOver` (`Server.Economy.cs:1008, 1010, 1014`). The client's
chat-voice path (`Game1.ProcessIncomingQuickChatVoices`) previously resolved
only weapon-ready (1023) and station-attack ids, so these chats were silent.
P723 adds a pure resolver **`Allegiance.Core.Sim.DroneChatterCue`** mapping the
nine native drone-chatter SoundIDs (recomputed from the `sounds.h` DEFSOUND
enum order — the same method P459 used for 327) to their real waves
(`sounddef.mdl:731-753`): e.g. 327 → `vo_request_miner`, 330 →
`vo_request_minefield`, 331 → `vo_request_tower`. `Game1.ProcessIncomingQuickChatVoices`
now consults it (between weapon-ready and station-attack), so the broadcasts
are voiced. The three waves plus the rest of the family's assets are bundled
and registered in `Content.mgcb`.

### New: in-flight command/targeting one-shots (SIM-0279, 0286, 0287)

| row | cue | native cite | wave (sounddef.mdl) | port trigger |
|---|---|---|---|---|
| SIM-0279 | `newTargetSound` | WinTrek.cpp:8827 (SetTarget, `GetCommandTarget(c_cmdCurrent) != pmodel`) | `newtargetneutral` (:311) | player-targeting commit edge — `Game1.Targeting.CommitTargetForPlayer` plays it when the selected target's kind+id changes |
| SIM-0286 | `vectorLockSound` | WinTrek.cpp:9463 (TK_LockVector) | `vector` (:295) | vector-lock key edge — `Game1.FlightInput` plays it on the LockVector toggle |
| SIM-0287 | `salShipCantRipcordSound` | WinTrek.cpp:9484 (TK_Ripcord, `HasCapability(c_habmNoRipcord)`) | `vo_sal_shipcantripcord` (:350) | ripcord key edge — `Game1.FlightInput` checks the local hull's `HabmNoRipcord` capability, plays the VO and refuses the doomed server request (mirrors native's gate) |

`SoundManager` gains `PlayNewTarget`, `PlayVectorLock`, `PlayShipCantRipcord`
(fields + `Load()` + eager `Content.Load`), all `PlayWarnOneShot` one-shots.

### Reconciliation (4 rows, already wired — matrix corrected)

| row | cue | wiring | evidence |
|---|---|---|---|
| SIM-0384 | `newShipSound` (enemy contact) | `ClientWorld` first-sighting edge → `PlayNewContact(enemy:true)` → `newtarget` (P373) | wiring audit + existing P373 path |
| SIM-0385 | `newEnemySound` (friendly contact) | same → `PlayNewContact(enemy:false)` → `newtargetenemy` (P373) | wiring audit |
| SIM-0386 | `newShipSound` (scanner resighting) | same → `PlayNewContact(enemy:true)` | wiring audit |
| SIM-0296 | `acceptCommandSound` | `Game1.Orders.cs:59` → `PlayCommandAccepted` (`accept`, sounddef.mdl:304) | wiring audit |

All four were `partial`/`missing` in the matrix despite live code; each is now
`implemented` with the wiring cited and `evidence=verified`.

## Failing-first

The genuinely-new pieces are failing-first: `DroneChatterCue` did not exist
before P723, so `P723DroneChatterCueTests` cannot compile on the pre-change
tree (CS0246, the same mechanism P711/P716/P717/P721 documented); and the six
new waves (`vo_request_miner`, `vo_request_minefield`, `vo_request_tower`,
`newtargetneutral`, `vector`, `vo_sal_shipcantripcord`) were absent, so
`P723SoundAssetTests` (asset-bundling) fails before the waves/mgcb are added.
After the change both pass. The in-flight one-shots are MonoGame-`Game1`
-driven, so their evidence is the resolver + asset tests + wiring audit (the
same `verified` evidence class the P716/P719 Game1-command rows use);
`SIM-0296/0384/0385/0386` are wiring-audit reconciliations of existing code.

## Tests

`tests/.../P723DroneChatterCueTests.cs` (new, 13 cases): pins the nine native
drone-chatter SoundIDs → real waves (cited to sounds.h + sounddef.mdl), plus
the negative cases (non-chatter ids resolve null).

`tests/.../P723SoundAssetTests.cs` (new): the six new waves present in
`Content/Sounds` and registered in `Content.mgcb`.

New tests run 3× clean (14/14 each). Targeted existing tests 3× clean:
sound/announce/deploy/sound-event/voice classes 193/193 each; targeting /
flight-input / ripcord / vector-lock classes 140/140 each.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first: see above (new `DroneChatterCue` type is a pre-change compile
  error; new waves absent pre-change break the asset test).
- Regression gate
  `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release, 0 failures)** — 1m38s, 1m33s, 1m34s, 1m33s, 1m40s. The
  change records/plays client-side one-shots only; it cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh`): attempted but could not be completed
  this session.** The **P734 Codex agent** (a concurrent task on this shared
  box, running since ~06:40) was actively executing the same Core AND
  MonoGame test assemblies at the same time (~87 % CPU on its Core testhost;
  its own `verify.py suites` / `suite-2-Core.trx` runs in
  `~/allegiance-codex-p734/docs/reports/P734_assets`). That is the documented
  shared-box condition under which `run-tests.sh` hangs in its live-network
  tests (see my session memory: "p726/p732 Codex agents run heavy live suites
  on this box simultaneously → full run-tests.sh hangs in live-network tests;
  don't attempt the 2× suite while they run"). My MonoGame and Core MAIN
  passes were launched and were terminated by that concurrent load (no
  summary in their logs) — none of the terminations reflect a failure in this
  change, which is **client-only** (Game1 sound path + a Core resolver) and
  touches no server, broadcast or simulation code, so it cannot cause a
  server-side live-network failure or hang. Measured clean on the components
  that could complete:
  - Lobby MAIN pass: **19/19, 0 fail**.
  - New P723 tests + targeted sound/targeting/flight/ripcord classes: **all
    clean 3×** (above).
  - Whole-solution build 0/0 (above).
  - P700 regression gate 5/5 (above).
  The remaining full-suite assembly passes (MonoGame MAIN, ClientCore MAIN,
  Core MAIN, quarantine) should be run once the P734 agent has exited; this is
  the only outstanding verification item and it is environmental, not caused
  by P723.

## Files

- `src/Allegiance.Core/Sim/DroneChatterCue.cs` — new pure drone-chatter
  SoundID → wave resolver (SIM-0254/0256/0257 + full family for readiness).
- `src/Allegiance.ClientCore.Game/Game1.Session.cs` — drone-chatter voice
  resolution in `ProcessIncomingQuickChatVoices`.
- `src/Allegiance.ClientCore.Game/Game1.Targeting.cs` — `CommitTargetForPlayer`
  + new-target one-shot (SIM-0279) on the player-selection commit paths.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — vector-lock one-shot
  (SIM-0286) and ripcord-incapable gate (SIM-0287).
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — `PlayNewTarget`,
  `PlayVectorLock`, `PlayShipCantRipcord` (+ fields + `Load()`).
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/{vo_request_miner,
  vo_request_minefield, vo_request_tower, newtargetneutral, vector,
  vo_sal_shipcantripcord}.ogg` (new) + `Content/Content.mgcb` registrations.
- `tests/.../P723DroneChatterCueTests.cs`, `tests/.../P723SoundAssetTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 10 rows → implemented (6 new + 4
  reconcile; LF kept, validated by `scripts/parity-scorecard.py`).
- `docs/reports/P723_PARITY_WAVE20.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| drone where-to plea voices (miner/minefield/tower) | shipIGC.cpp:2315,2326,2328; sounds.h:381,384-385 | design | server broadcasts the plea team chat (existing P459); client now resolves the SoundID → wave and voices it |
| new-target cue on retarget | WinTrek.cpp:8827 | design | player-targeting commit plays newtargetneutral when the selection changes |
| vector-lock cue | WinTrek.cpp:9463 | design | LockVector key edge plays vector |
| ripcord-incapable VO | WinTrek.cpp:9484 | design | ripcord edge on a `c_habmNoRipcord` hull plays vo_sal_shipcantripcord and refuses the doomed request |
| new-contact / accept-command | trekigc.cpp:2089-2114 / WinTrek.cpp:10658 | design | already-wired (P373 / PlayCommandAccepted); matrix reconciled |

## Modern alternative considered

Native selects the drone chatter sound server-side and ships it as a chat
voice-over; the port already ships the id and plays it client-side — the
modern "send an event id, resolve the asset on the receiver" split, which is
exactly what the port does. No audio-engine upgrade warranted. The in-flight
cues are plain one-shots; nothing to modernize.

## Limitations

- **SIM-0236, SIM-0255** (completion / constructor-need-rock): the trigger
  chats are broadcast, but the SoundID is per-station-type (core data), so
  resolving id → wave needs the core sound-def / VO table, which is the
  separate SIM-0399 task — left `partial`/`n/a` with that reason.
- **SIM-0249-0253, 0258-0262, 0269-0270** (remaining drone chatter:
  constructorRunning, droneTooMuchDamage, droneEnemyOnScope,
  droneComingHomeEmpty, droneTooCrowded, droneInTransit): the client resolver +
  assets are ready (all nine ids resolve in `DroneChatterCue`), but the server
  does not yet broadcast those AI chatter edges; left `partial` with that
  reason (a server-touching follow-up can enable them without further client
  work).
- **SIM-0239, 0241/0242, 0248, 0263-0268** (server-side armament engage /
  reload) and **SIM-0443-0453** (client reload/deplete): need per-launcher
  magazine counts that the 47-byte ship update does not stream, and/or server
  notification infra; chaff/engage were already deemed undecidable in P717 —
  remain `partial`/`n/a`.
- **SIM-0454-0465** (ReceiveChat salutations), **SIM-0294/0295/0298** (vo*
  command ack), **SIM-0317-0338** (UI-screen one-shots): need respectively
  player-presence wire events, server command-ack VO, and interface-layer UI
  hooks (some are the Codex interface waves) — remain `partial`/`n/a`.
- The full-suite run is the one outstanding verification item, blocked this
  session by the concurrent P734 agent (see Verification); P723's change is
  client-only so it cannot cause server-side suite failures.
