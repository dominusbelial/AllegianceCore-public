# P664 — parity wave 3: station event sound identity

Implemented SIM-0421–SIM-0424 in the working tree. No commits or pushes.

Selected four weight-4 rows (16 total weight) that share the complete station-event
path: STATION_DESTROYED / STATION_CAPTURE → client sound queue → core sound field
→ bundled clip → warning mixer. These are the highest-weight outstanding station
sound rows. They affect every base loss/capture, including off-sector notices,
and distinguish outposts, shipyards, research bases and the other station families.
Previously every family announced a garrison, allied losses used enemy cues, and
unrelated enemy captures announced a friendly loss.

The ranking considered both matrices. Outstanding weight-5 rows remaining after
all brief exclusions include JOINED_MISSION, swept collision ordering, missile
lock conditions and ripcord revalidation. Those are separate handshake/physics/
equipment/transit systems; they do not form a 4–8-row bundle sharing this code.
This pack closes the coherent four-row station sound bundle rather than mixing
unrelated singleton fixes. All excluded task areas remain outside this pack.
The offline KillStationEvent rows SIM-0396/0397 and missile launch SIM-0442 retain
their existing status: their separate execution paths are not certified here.

Native paths below are relative to `~/Development/Allegiance/src/`.

| Row | Native rule and citation | Implementation and per-row test |
|---|---|---|
| SIM-0421 | Known destroyed station owned by our side **or an ally** selects its type's `GetDestroyedSound()`, before termination. `clintlib/appmsg.cpp:523,548-553` | `ClientWorld.cs:2810`; `P664StationEventSoundTests.cs:57` verifies own/allied destruction, retained type after removal and silent duplicate destruction. |
| SIM-0422 | Otherwise select that type's `GetEnemyDestroyedSound()`. `clintlib/appmsg.cpp:550-551` | `ClientWorld.cs:2810`; test `:72` verifies enemy cue after a real alliance-change packet. |
| SIM-0423 | Capture from our side or an ally selects `GetCapturedSound()`; this branch has priority when both sides are friendly. `clintlib/appmsg.cpp:674-675` | `ClientWorld.cs:2825`; test `:81` covers own/allied old sides, both-friendly transfers, ownership change and type preservation across a later export. |
| SIM-0424 | Otherwise capture by our side or an ally selects `GetEnemyCapturedSound()`; captures involving no friendly side produce no cue; then change ownership. `clintlib/appmsg.cpp:676-679` | `ClientWorld.cs:2825`; test `:96` covers own/allied gains; test `:108` covers unrelated captures and missing subjects. |

The four fields are core data, not station-name/model heuristics:
`Igc/stationtypeigc.h:308-325`. Native initializes the numeric SoundID lookup from
`soundList` (`WinTrek/soundinit.cpp:25-28`), invokes these station notices without
a model/sector restriction (`WinTrek/trekigc.cpp:4955-4962`), and declines invalid
or absent sound templates (`WinTrek/WinTrek.cpp:11253-11280`).

`ClientSoundEvent.StationTypeId` snapshots the subject type before removal or
replacement. `StationEventSoundMap.TryResolve` selects the decoded core field;
Game1 passes the event and its loaded station-type catalog to
`SoundManager.PlayStationEvent`. Playback uses the existing cached `PlayVoice`
warning mixer, including its voice budgeting and cleanup. An unresolved type,
missing core, NA or unlisted sound ID stays silent instead of announcing a false
station family.

The mapping covers all 48 station-event IDs in the shipped sound definition:
860–871, 880–891, 900–911 and 920–931. It preserves the transceiver variants and
the teleport/refinery capture aliases to `errorSound` (`ef03`). Asset evidence:
`Content/sounddef.mdl:76-79,272,1239-1284,2291-2341`. Added 41 original Artwork OGGs
verbatim and their MGCB build entries; existing FreeAllegiance MIT attribution
in NOTICE applies, and CREDITS now reflects the actual 460 bundled sounds.
The native template interpreter's gain envelopes/MutexSal scheduling remain the
existing port warning mixer's responsibility. Arbitrary sound IDs outside the
station-event registry are not added by this pack. No audible-device listening
or offline callback parity is claimed.

Both matrices were updated: the four simulation rows move from partial to
implemented with traced evidence and core-read bindings. The already-done
interface rows 58/59 retain their status and gain actual consumer/test citations.
There is no additional interface completion credit for the same change.

Scorecard uses actual task-entry HEAD, not an older wave report. Exclude `na`
(interface) / `n/a` (simulation), count only `done` / `implemented`; partial rows
receive zero completion credit. Weighted = completed weight / eligible weight;
unweighted = completed rows / eligible rows. All row weights and denominators
are unchanged.

| Matrix / area | Weighted before → after | Unweighted before → after |
|---|---:|---:|
| Interface commands | 87.48% → 87.48% | 83.16% → 83.16% |
| Interface fedmsg | 79.03% → 79.03% | 78.40% → 78.40% |
| Interface screens | 86.81% → 86.81% | 79.17% → 79.17% |
| Interface parts | 86.27% → 86.27% | 82.50% → 82.50% |
| Interface commander | 100.00% → 100.00% | 100.00% → 100.00% |
| Interface lobby | 76.47% → 76.47% | 75.00% → 75.00% |
| **Interface total** | **1255/1482 = 84.68% → 84.68%** | **336/410 = 81.95% → 81.95%** |
| Simulation constants | 33.33% → 33.33% | 48.72% → 48.72% |
| Simulation updates | 35.53% → 35.53% | 33.33% → 33.33% |
| Simulation periodic | 6.38% → 6.38% | 6.67% → 6.67% |
| Simulation callbacks | 33.33% → 33.33% | 33.33% → 33.33% |
| Simulation warnings | 0.00% → 0.00% | 0.00% → 0.00% |
| Simulation sounds | 56/594 = 9.43% → 72/594 = 12.12% | 14/228 = 6.14% → 18/228 = 7.89% |
| **Simulation total** | **294/1333 = 22.06% → 310/1333 = 23.26%** | **85/437 = 19.45% → 89/437 = 20.37%** |

Validation:

- `NUGET_HTTP_CACHE_PATH=/tmp/p664-nuget-http dotnet build AllegianceCore.slnx -m:8 -v:minimal`: **0 warnings, 0 errors**, including the real MGCB audio build. Initial default-cache restore failed NU1900 because the home NuGet HTTP cache is read-only; redirecting only that cache resolved it.
- Eight new facts and all six existing `ClientWorldSoundEventTests` facts ran **three times each in their own exact FullyQualifiedName-filtered dotnet test invocation: 42/42 passed, zero skipped**. The four row facts feed encoded messages through production ConnectionSession dispatch. Shared tests check all 48 ID/template/alias mappings against sounddef, every OGG/MGCB entry, every decoded real PCore001 station type's four fields, and missing/unrelated subjects.
- Full suites ran **sequentially, once each after the build, without filters**, including real TCP/loopback and `LiveTiming` tests:

  | Suite | Passed | Failed | Initially skipped | Duration |
  |---|---:|---:|---:|---:|
  | Core | 4197 | 2 | 0 | 4m31s |
  | ClientCore | 894 | 0 | 1 | 4m07s |
  | MonoGame | 2128 | 0 | 3 | 4m22s |
  | Lobby | 12 | 0 | 0 | <1s |

  All four initial skips were subsequently executed successfully: the actual
  opt-in 15v15 lifecycle proof (`ALLEGIANCE_LIVE_P660_SHOWCASE=1`) passed **1/1**
  in 21 seconds; configuring the installed built Meshy pack through
  `ALLEGIANCE_MESHY_PACK_PATH` passed **6/6** pack tests, including the three
  previously skipped cases. The excluded 221-map/large-rig proof drivers retain
  their existing opt-in gates; they are not claimed as executed proofs here.
- Core's failures are both existing `Lane=LiveTiming` cases already listed in
  `docs/TEST_FLAKES.md`:
  - `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`: full-run shutdown raised `ThreadStateException` at `TcpTransport.cs:447`; **3/3 exact isolated retries passed** (load flake under the brief's rule).
  - `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`: the mission-B `GameStates` emptiness assertion at test line 161 failed. Exact isolated retries were **fail/pass/pass**, followed by **pass/pass/fail**: **4/6 passed**, with the same assertion on both failures. This remains the already documented intermittent quarantine case; **no 3/3 isolation stability or clean Core suite is claimed**. It never failed 3/3. Core.Tests references only unchanged Core/Server/Bot/Lobby projects, so this test does not load the modified ClientCore or Game assemblies. No unrelated backend or quarantine changes were made to obtain a green result. Updated the flake ledger with this observation.
- `python3 scripts/check-p642-parity.py`: passed (231 audit rows, 438 interface rows). Both CSVs parse with exact column counts and unchanged weights; before/after scores recomputed from CSVs and `git show HEAD:<matrix>`.
- `git diff --check`: passed.

Build and test evidence is in `/tmp/p664-build.log`,
`/tmp/p664-isolation.log`, `/tmp/p664-iso-*.log`,
`/tmp/p664-suite-*.log`, `/tmp/p664-showcase.log`, `/tmp/p664-meshy.log`,
`/tmp/p664-rerun-*.log`, `/tmp/p664-conquest-followup*.log`,
and `/tmp/p664-results/*.trx`.
