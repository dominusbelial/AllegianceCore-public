# P732 — native recorded voice-over

Implemented SIM-0399: received sonic chats and direct notification SoundIDs now
select their native recorded wave through `ClientSoundEvent → Game1.Audio →
SoundManager`. Voice selection precedes plain command/personal/team chat cues.
The native VO and SAL mutexes serialize their respective channels, with native
gain, priority, expiration and duplicate rules. Menu sends preserve null-text
sonic indices; server drone notifications carry corrected native SoundIDs.

| Scorecard | Before | After |
|---|---:|---:|
| Sounds, weighted | 299/594 (50.34%) | 301/594 (50.67%) |
| Sounds, completed rows | 106/228 (46.49%) | 107/228 (46.93%) |
| Simulation matrix, weighted total | 789/1333 (59.19%) | 791/1333 (59.34%) |
| Simulation matrix, completed rows | 237/437 (54.23%) | 238/437 (54.46%) |
| Callbacks, weighted | 90/198 (45.45%) | 90/198 (45.45%) |

The raw before/after scorecards are in `P732_assets/scorecard-before.txt` and
`scorecard-after.txt`. SIM-0172's explanation and port references are updated;
its generic `IIgcSite.ReceiveChat` contract remains partial. SIM-0173/0174/0178
remain partial: catalog coverage does not implement missing simulation callback
producers. The edited simulation CSV has LF endings; the other parity CSV is
unchanged.

## Native mapping and producer audit

All native source citations below are relative to
`~/Development/Allegiance/src/`. The original sound and quick-chat MDL files
are artwork data at `~/Development/Artwork/`; their verbatim repository copies
are `src/Allegiance.ClientCore.MonoGame/Content/sounddef.mdl` and `quickchat.mdl`.
Their hashes and the native enum input hash are recorded in
`P732_assets/catalog-provenance.json`.

| Native rule or producer | Native file:line | Result |
|---|---|---|
| Chat carries a short SoundID | `Inc/Messages.h:204-223`; `Igc/igc.h:453` | `ChatData.VoiceOver` is preserved on both chat wire shapes. |
| Enum IDs start after 1000; removed entries retain their ordinals | `Igc/igc.h:456-464`; `Igc/sounds.h:1` | Generate ordinals including `DEFEMPTYSOUND`, starting at 1001. |
| Enum symbols exported as MDL `...SoundId`; `soundList` binds IDs to templates | `WinTrek/soundinit.cpp:26-28,45-47`; `WinTrek/WinTrek.cpp:11271-11280` | Generate a direct SoundID table from the real MDL bindings, including numeric core IDs such as 820. |
| Quick-chat menu sends the selected sonic-chat index | `WinTrek/WinTrek.cpp:990-1001`; `WinTrek/console.cpp:1534,1593-1596` | Menu sends its native index with absent text (`cbMessage=0`), alongside the resolved command and target. |
| Sonic index resolves through `sonicChats` to voice zero's sound template and caption | `WinTrek/WinTrek.cpp:1080-1083,1095-1128` | Generate all 310 indexed sonic entries and captions. Index 28 resolves to `vo_player_donatecredits`; it is distinct from direct SoundID 1131. |
| Null text selects sonic lookup; present text selects direct SoundID; voice precedes plain chat sound | `WinTrek/trekigc.cpp:4507-4540` | Preserve the absent-versus-present-empty wire distinction. Unknown sonic indices never fall back to direct SoundIDs. Accept the older port's exact caption+index format as a compatibility path. |
| Even absent text retains its fixed-message blob offset | `_Utility/Messages.cpp:403-413`; `FedSrv/FedSrv.CPP:4922` | Encode the fixed-size offset with zero count for NULL text, on both chat codecs. The tests assert the actual offset as well as the absent-text round trip. |
| Local send already receives the chat; own server echo is ignored | `clintlib/clintlib.cpp:2463-2473`; `clintlib/appmsg.cpp:2642-2655` | Port retains one server-echo playback for its own sonic macro instead of native's immediate local playback; own ordinary/direct echoes remain silent. |
| Constructor requests use station-type `ConstructorNeedRockSound` | `FedSrv/FedSrv.CPP:11369-11370` | Existing core-derived SoundID is preserved and now resolves to audio. |
| Miner/layer spawn requests use enum SoundIDs | `FedSrv/FedSrv.CPP:11538-11548`; `Igc/sounds.h:381,384-385` | Correct IDs from 327/330/331 to 1328/1331/1332. |
| Accepted drone command says affirmative | `FedSrv/FedSrv.CPP:3256-3257`; `Igc/sounds.h:218` | Add direct `voAffirmativeSound=1178` to the existing server reply. The commented-out rejection branch at `FedSrv.CPP:3287-3291` does not justify new automatic negative speech. |
| Station attack/critical uses per-type core sound; friendly fire uses drone watch-fire sound | `FedSrv/FedSrv.CPP:12202-12228`; `Igc/sounds.h:377` | Preserve attack/critical IDs; correct watch-fire from 1325 to 1324 and its station notification sender to NA, matching native NULL. |
| Hull sound fields are core data | `Igc/hullTypeIGC.h:315-339`; `Igc/shipIGC.h:112-117` | Interior/exterior/thruster sound accessors are sound producers, not new player speech. Their flight mixer work stays outside this task. |
| Weapon activation/shot/burst are core sounds; mounting produces notification | `Igc/weaponIGC.h:46,220-232` | Existing mounted-ready owner notification (1023, `unmount`) joins the same receive path. Per-weapon world sounds retain their existing paths. |
| Missile launch and ambient are core sounds | `Igc/missiletypeigc.h:177-183` | These are weapon/flight cues rather than sonic-chat indices; their existing launch/ambient implementation is unchanged. |
| Ship reload/cargo notifications and drone complaints carry sound IDs | `Igc/shipIGC.h:616-617,646-647,1302-1307,1723-1733,2408-2409,2449-2450` | Catalog can resolve the serialized voice templates; this does not invent absent reload/cargo/complaint callback producers. Native own/source-ship notification gate is `WinTrek/trekigc.cpp:4975-4983`. |
| Pigs chat defaults to NA and forwards explicit voice IDs | `Pigs/PigSrv/Pig.h:163-164,244-246`; `Pigs/PigSrv/Pig.cpp:529-534,1762-1773` | Bot chat defaults to NA; explicit IDs remain supported. No new autonomous bot speech is added. |
| Pigs Oink command explicitly attaches an oink sound; ripcord notification updates Pig error state | `Pigs/PigSrv/Pig.cpp:349-356,1343-1344,1777-1794` | Oink IDs are in the catalog if explicitly received. Native's error-state callback does not authorize broadcasting player voice lines from every bot. |

## Playback and assets

`NativeVoiceCatalog.Generated.cs` contains 536 templates, 449 direct SoundID
bindings and 310 sonic entries. `scripts/generate-voice-catalog.py` reproduces
them from the checked-in MDLs and native enum, and bundles only the referenced
templates' real waves. There are 509 available distinct waves, including 484
`vo_*` waves; 119 waves (2,010,067 bytes) are newly bundled. The provenance
ledger verifies every new file is byte-identical to the artwork source.

P644's `artwork-manifest.txt` is absent on this base. The task therefore supplies
`Content/voice-over-manifest.txt`, checks every available catalog reference
against it, checks the actual `.ogg`, and checks its MGCB `/build` registration.
The solution content build compiles the added assets. `voice-over-missing.txt`
is the explicit exclusion list:

| Referenced asset | Status | Reason |
|---|---|---|
| `vo_ma_noimeanit` | n/a | The native MDL references it; no corresponding `.ogg` exists in the artwork corpus. Caption is retained, audio is silent. |
| `vo_ma_woopie` | n/a | The native MDL references it; no corresponding `.ogg` exists in the artwork corpus. Caption is retained, audio is silent. |

These are per-asset exclusions within SIM-0399; the matrix has no independent
native-rule row for either wave. The whole voice-over path is not marked n/a
because two source assets are unavailable.

Native creates distinct `MutexSal` and `MutexVO`
(`WinTrek/WinTrek.cpp:2881-2882,2930-2931`). Sounds sharing a mutex cannot overlap;
different mutexes can (`soundengine/soundtemplates.cpp:2019-2021`). The port
reproduces active duplicate suppression, pending duplicate expiration refresh
without urgency reset (`soundtemplates.cpp:1954-1988`), expiry and selection by
minimum expiration-minus-priority (`soundtemplates.cpp:1890-1937`). It checks
actual instance completion before advancing each queue. A failed load/device
releases the channel. Queue advancement and voice-budget cleanup run in lobby,
docked and flying UI states.

Gain includes native's default `VoiceOverGain=-13 dB`
(`WinTrek/WinTrek.cpp:2928-2929`) plus numeric nested MDL gains. In particular,
SAL watch-fire and drone requests are -20 dB (the MDL's extra -7), while donate
credits is -13 dB. MDL defaults are ten-second timeouts (`sounddef.mdl:4-5`).
Direct mounted/critical/noncritical effects use native's default `SFXGain=-8 dB`
(`WinTrek/WinTrek.cpp:2926-2927`).
The port's existing master/category volume controls apply after those template
gains.
Mission countdown and ripcord countdown use their distinct native templates,
even though they share wave files (`sounddef.mdl:332-362`). Existing serialized
SAL/VO callers register with the shared queue; their producer conditions and
parity rows are not reimplemented.

`ClientWorld` resolves the cue once at receipt, retains captions/HUD feedback,
and records an immutable template in the bounded sound-event queue. The old
Game1 chat-log polling path is removed, so quick chats, mounted-ready and station
alerts cannot be replayed by two receive paths. Notification HUD feedback marks
its cue as already queued, preventing a second critical/noncritical sound in
`Game1.Feedback`.

## Scope and validation

Read P710/P711/P714/P716/P717 reports. Only SIM-0399 changes completion status;
SIM-0172 gets an accurate partial explanation. Other sound waves' triggers,
simulation rules and row statuses are preserved. No later/in-flight Hermes
reports are present on this base. This task does not add missing generic IGC
callbacks or claim the native lobby visibility/mute/training filters are fully
ported. New receiver handling covers both CHATMESSAGE and CHATBUOY. No rig,
221 or 200v200 runs are attempted.

The final solution build has **0 warnings / 0 errors**. All **139 affected test
methods passed 3/3 individually**: **417 test invocations / 528 case passes**, with
zero failures or skips. This includes the new wire/ClientWorld tests, native
catalog and manifest checks, queue policy, previous sound/feedback/countdown
coverage, and real loopback quick-chat/drone/constructor/station senders. Every
one of the 509 available manifest waves also exists as a compiled `.xnb` in
the final client output (`P732_assets/compiled-content.json`).
The raw ledger also retains three empty discovery commands for an `IDisposable`
helper; those are excluded from the test counts above.

The never-parks showcase passed **10/10**, in Debug, with zero failures or
skips. No base-rate comparison was required. Individual transcripts and
durations are in `P732_assets/showcase-*.log` and `showcase-summary.json`.

Exactly **two** real full-suite passes completed using `scripts/run-tests.sh`.
Both solution builds were 0 warnings / 0 errors. Raw main-lane counts:

| Assembly | Run 1 passed / failed / skipped | Run 2 passed / failed / skipped |
|---|---:|---:|
| Lobby | 19 / 0 / 0 | 19 / 0 / 0 |
| MonoGame | 2,299 / 0 / 3 | 2,299 / 0 / 3 |
| ClientCore | 1,187 / 0 / 0 | 1,187 / 0 / 0 |
| Core | 4,752 / 1 / 0 | 4,752 / 1 / 0 |

Both scripts exited **1** because of their respective Core main-lane failure;
the raw suites were not all green. Run 1 quarantine passed ClientCore 3/3 and
Core 18/18. Run 2 quarantine passed ClientCore 3/3 and reported
`LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` as a
flake after its built-in isolated retry passed. The existing runner suppresses
the failing quarantine batch's counters and original assertion output, so no
additional raw Core quarantine count or failure detail is inferred for run 2.

After both suites, all three reported methods passed **3/3 fresh exact-method
isolated runs** (nine invocations, nine passes, zero failures/skips):

| Method (under `Allegiance.Core.Tests`) | Full-suite observation | Post-suite isolation |
|---|---|---|
| `Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events` | Run 1: expected zero allocation bytes, observed 7,768. | 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Run 2: `TimeoutException: no OnPeerJoined for raw peer`. | 3/3 pass |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | Run 2 quarantine: runner reported a failure that passed its isolated retry. | 3/3 pass |

These satisfy the brief's load-flake rule and are recorded in
`docs/TEST_FLAKES.md`. No assertions, deadlines, quarantine traits or product
behavior were changed for these failures; isolated passes do not establish
their underlying causes or fixes. Exact commands and per-attempt transcripts
are in `P732_assets/post-failure-isolation.json` and `post-isolate-*.log`.

The three MonoGame skips per pass are the existing private Meshy-pack checks.
The local P660 showcase opt-in and all ordinary live/loopback tests were enabled,
including the full ten-minute human-flight soak. No third full-suite pass ran.
The invocation ledger is
`P732_assets/verification.json`; complete full-suite transcripts are saved beside
it. The final solution build is in `P732_assets/build.log`. Review diagnostics
are retained separately. Interrupted shell wrappers initially left child test
runners alive; those overlapping diagnostics are not counted as isolated
evidence. A temporary admission barrier stopped all of them before any real
full-suite pass, and was removed before final verification. The final cohort
runs on one serial runner; diagnostic admission failures are recorded in
`P732_assets/diagnostic-runner-stop.json` and `diagnostic-concurrent.json`.

No commits or pushes were made. All changes remain in the working tree.
