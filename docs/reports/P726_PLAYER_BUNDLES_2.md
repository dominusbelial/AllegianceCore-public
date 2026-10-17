# P726 — player bundles round 2

The Codex lane implementation adds an unsigned Apple silicon `.app` ZIP, silences
unrequested private Meshy notices, and removes the generated background-music fallback.
Changes remain in the working tree. No commit, push, tag or release was created.

## Implementation and native evidence

- Regenerated the source/data dependency closure after the master merge. The pinned
  artwork closure contains **2,773 files / 976,788,863 bytes**, with **2,076 roots**.
  The full-source census loads **447 hulls / 143 stations**, opens every manifest file,
  and reports no unlisted artwork violations. Source pins remain unchanged.
- The absent Meshy pack no longer logs a configuration notice for ordinary native
  rendering. `--ships meshy` and explicit Meshy/catalogue overrides retain the one-time
  diagnostic and native fallback. The default inspector is quiet too; its Meshy-only
  selection still warns. Normal startup also omits the developer pack hint.
- Searched the native client and the pinned artwork for music: there is no
  `flightmusic`, `stationmusic`, `musicdef.mdl`, or music bed/audio file in the pinned
  artwork. `sounddefvalor.mdl` still refers to the absent music namespace; that is
  not an available recording. Native names are at
  `~/Development/Allegiance/src/WinTrek/artwork.h:284-285`; native explicitly marks its
  old score slots **UNUSED** and declares them with `DEFEMPTYSOUND` at
  `~/Development/Allegiance/src/Igc/sounds.h:118-132`. The default music toggle remains
  off (native `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:2717`). `Music.Load`
  now loads real supplied content beds if present and stays silent on a missing bed;
  it never calls `PlaceholderMusicFactory`. Toggle/volume/dock-state planning remains
  usable, including when one or both real beds are absent.
- The additional rendered loopback check found six master sound references
  absent from compiled Content: `payday`, `powerupdevelopment`, `powerupshield`,
  `receivechat`, `receivedcomchat`, `receivepersonalchat`. This stopped SoundManager
  initialization early. Added their exact Ogg bytes from the pinned public artwork
  and registered them in MGCB. Packaging now requires all fixed SoundManager sound
  XNBs before archiving. Native identifiers: `~/Development/Allegiance/src/Igc/sounds.h:26-29`
  and `~/Development/Allegiance/src/Igc/sounds.h:39-45`; artwork wave mappings: pinned `sounddef.mdl:279-281,294-301`.
  No event selection or gameplay behavior changed.
- `package.py --rid osx-arm64` places the self-contained runtime, Content, artwork,
  sky, manifests, catalogue and licenses in `AllegianceCore.app/Contents/Resources`.
  `Contents/MacOS/Allegiance` changes to Resources and forwards all arguments to the
  real arm64 apphost. Thus both executable-relative and current-directory asset
  lookups work without changing shared path resolution. The outer folder also has
  a visible player README. `Info.plist` declares APPL, an identifier, the executable,
  Retina support and the icon. Both launcher and apphost have mode 0755.
- The checked-in ICNS is generated from `packaging/allegiance.svg`, with transparent
  128/256/512/1024 PNG representations. `generate-icon.py` documents/reproduces it
  using FFmpeg/librsvg; normal packaging needs no icon-conversion dependencies.
- ZIP records preserve Unix modes and ordinary directories for Finder/ditto.
  `smoke.py` recognizes the `.app`, validates metadata/resources/icon/execute bits,
  restores modes lost by Python ZIP extraction, and invokes the app launcher with
  the real headless census from outside the repository with ALLEGIANCE overrides
  cleared. It still supports Linux and Windows archives.
- Release packaging now builds three RIDs. A `macos-latest` job runs the native
  arm64 ZIP smoke and an additional real `ditto` extraction/launcher check. Publishing
  waits for Linux packaging smoke plus Windows and macOS native smoke gates.
  GitHub documents `macos-latest` as an arm64 standard runner in its
  [hosted-runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
- The player README includes Apple silicon, unsigned Gatekeeper instructions,
  `xattr -dr com.apple.quarantine`, per-user configuration, direct-connect commands,
  and the headless check. The macOS 14 minimum matches
  [Microsoft's .NET support table](https://learn.microsoft.com/en-us/dotnet/core/install/macos).

## Validation

The final whole-solution build passed with **0 warnings / 0 errors**. The sandbox's
shared NuGet cache is read-only: the build uses the existing cached package source,
and cross-publishing uses a worktree-local writable package/cache overlay to fetch
the previously absent Windows runtime packs. No host cache permissions were changed.

Final focused verification passed **42 C# exact-method invocations / 63 data cases**:
MusicLoadingTests, both MeshyNoticeTests methods, and all MusicPlannerTests methods,
three fresh processes per method. All **8 packaging tests passed 3× individually**
(**24 case passes**), including app launch/argument forwarding from paths with
spaces, plist/icon/resources, ZIP execute-bit preservation and rejection of a
missing required runtime sound.

The suite ran **exactly two complete unfiltered sweeps**, sequentially through
Core, ClientCore, MonoGame and Lobby. Live/loopback and quarantine-tagged tests
were included. Both MonoGame passes include the existing ten-minute flight soak.
Raw results total **16,274 passed / 2 failed / 8 skipped**:

| Sweep | Assembly | Passed | Failed | Skipped | Wall seconds |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Allegiance.Core.Tests | 4734 | 0 | 0 | 800.714 |
| 1 | Allegiance.ClientCore.Tests | 1125 | 0 | 1 | 686.190 |
| 1 | Allegiance.ClientCore.MonoGame.Tests | 2259 | 1 | 3 | 959.162 |
| 1 | Allegiance.Lobby.Tests | 19 | 0 | 0 | 0.951 |
| 2 | Allegiance.Core.Tests | 4733 | 1 | 0 | 812.157 |
| 2 | Allegiance.ClientCore.Tests | 1125 | 0 | 1 | 686.472 |
| 2 | Allegiance.ClientCore.MonoGame.Tests | 2260 | 0 | 3 | 958.215 |
| 2 | Allegiance.Lobby.Tests | 19 | 0 | 0 | 0.938 |

Skip counts above use actual TRX outcomes (the TRX counter field `notExecuted`
is misleadingly zero). The skips are three optional private Meshy-pack cases
per MonoGame sweep and the P660 opt-in proof per ClientCore sweep; the latter
passed 3/3 separately with its opt-in enabled.

The first MonoGame pass failed the existing radar target-highlight receipt test
with a null position update; the second passed it. The second Core pass failed
the existing reload-relay observer setup; the first passed it. After all full sweeps and ten standalone showcase runs finished, both failed
methods passed **3/3 fresh exact-method isolated invocations**. These meet the
brief's load-flake rule and are recorded in `docs/TEST_FLAKES.md`; raw failures
remain in the table and evidence. No assertion, timeout or quarantine change was
made. Exact isolated methods:

- `Allegiance.ClientCore.MonoGame.Tests.RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight` — **3/3 pass**.
- `Allegiance.Core.Tests.AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` — **3/3 pass**.


## Final artifacts and platform checks

All archives and adjacent `.sha256` / `.json` files are in ignored `dist/`.
Their content inventories are byte-identical across RIDs. Each is below 2 GiB.

| RID | Archive bytes | Unpacked bytes | Packaging seconds |
| --- | ---: | ---: | ---: |
| linux-x64 | 541,712,784 | 1,227,783,650 | 57.329 |
| win-x64 | 543,653,500 | 1,224,733,811 | 54.705 |
| osx-arm64 | 542,035,489 | 1,234,102,700 | 54.275 |

Final SHA256:

```
3ee604aa5cea260fada50cc1b6c617a2ba05f6ece0b0d1e634b4a3b1c726f45d  Allegiance-linux-x64.tar.gz
9f4a37693f0f91b5b51458a53da7524c4854ccf767445a24ad3219b34cb2239c  Allegiance-win-x64.zip
27dff0b5dd13c1adb1926f1d5b9d5c4f3f7d82ca0375ac724e4d6247f5b86afd  Allegiance-osx-arm64.zip
```

- Linux: the actual extracted self-contained apphost passed `smoke.py` outside the
  repo with ALLEGIANCE overrides removed. Inventory and full census passed.
- Linux rendered loopback: the supported `allegiance.sh` launcher connected to a
  temporary local server, completed countdown/launch and rendered HD sky at about
  57 FPS for 20 seconds. SoundManager fully initialized. The session recorded 495
  assets (253 artwork / 242 sky files); no unlisted artwork or private Meshy notices occurred.
- Windows: cross-publish and a Linux-host managed census of the extracted Windows
  payload passed. The native Windows apphost remains gated by `windows-smoke` in CI.
- macOS: cross-publish, real Mach-O arm64 header, plist, ICNS, execute modes and
  licenses passed. Both bundled SDL2 and OpenAL dylibs contain arm64 slices.
  The extracted Mac assets passed the same full census/inventory
  using the identical-source **Linux entry assembly substituted only in the temporary
  extraction**. Attempting to load the arm64 entry assembly on x64 Linux predictably
  failed with architecture incompatibility; this is not native macOS execution.
  `smoke.py`'s native Mac invocation and real ditto extraction are in the new
  `macos-latest` gate. CI has not been triggered from this unpushed worktree.
  Finder, Gatekeeper, native GL/audio and real M4 Max validation are Claude's checks
  below, as requested by the brief.
- The opt-in local P660 15v15 bots/humans next-match proof passed **3/3**, with
  `ALLEGIANCE_LIVE_P660_SHOWCASE=1`. It runs separately from the two default unfiltered
  sweeps (where its opt-in guard skips it). No rig/221/200v200 proof was run.

## Standalone never-parks gate

**10/10 passed** in fresh individual processes, in addition to both
passes inside the full ClientCore sweeps. The five-second parking assertion,
45-second combat window, objective-arrival, gunfire/hit and simulation-error
checks are unchanged. Maximum observed low-speed-with-target interval across
the standalone runs: **3.324 seconds**.

| Run | Result | Observed seconds | Longest low-speed interval (s) |
| --- | --- | ---: | ---: |
| 1 | Passed | 96.6 | 2.006 |
| 2 | Passed | 94.4 | 3.107 |
| 3 | Passed | 94.3 | 2.006 |
| 4 | Passed | 94.8 | 3.324 |
| 5 | Passed | 96.6 | 2.004 |
| 6 | Passed | 95.4 | 3.208 |
| 7 | Passed | 94.8 | 2.007 |
| 8 | Passed | 96.5 | 2.004 |
| 9 | Passed | 96.6 | 2.004 |
| 10 | Passed | 96.2 | 3.107 |

## Review evidence

- [Invocation ledger, outcomes, skips, source hashes and archive metadata](P726_assets/validation.json).
- [Build 0/0](P726_assets/build-final.log), [Linux extracted smoke](P726_assets/smoke-linux-x64.log),
  [artifact audit](P726_assets/artifact-audit.log), and [source audit](P726_assets/source-audit.log).
- [Rendered Linux client log](P726_assets/live-client.log) and [HD-sky frame](P726_assets/linux-hd-sky.png).
- [Every retained test TRX/console log, compressed](P726_assets/validation-logs.tar.gz).
  Additional raw files and reproducibility helpers remain in ignored `.verify-p726/`.
- [Full-source census](P726_assets/full-source-census.json.gz) and
  [live session recording](P726_assets/live-assets.json.gz).

No commit, push, tag, release, signing, auto-update, installer/AppImage or Android
work was performed. All implementation/report changes remain in this working tree;
player archives remain in `dist/` for Claude's native-platform tests.

## Exact M4 Max checks for Claude

Copy `dist/Allegiance-osx-arm64.zip` and its `.sha256` file to the Mac. In the folder
containing both files, with Python 3.11+ available for the smoke helper:

```sh
shasum -a 256 -c Allegiance-osx-arm64.zip.sha256
python3 /path/to/this/worktree/packaging/smoke.py "$PWD/Allegiance-osx-arm64.zip"
proof_dir="$(mktemp -d -t allegiance-p726)"
ditto -x -k Allegiance-osx-arm64.zip "$proof_dir"
app="$proof_dir/Allegiance-osx-arm64/AllegianceCore.app"
plutil -lint "$app/Contents/Info.plist"
test -x "$app/Contents/MacOS/Allegiance"
test -x "$app/Contents/Resources/Allegiance"
file "$app/Contents/Resources/Allegiance"
cd /tmp
env -u ALLEGIANCE_ARTWORK_PATH -u ALLEGIANCE_SKY_PACK_PATH -u ALLEGIANCE_MESHY_PACK_PATH \
  "$app/Contents/MacOS/Allegiance" --check-assets --asset-manifest "$proof_dir/census.json"
```

Expected: Mach-O arm64 apphost; clean plist; census 447 hulls / 143 stations / 2,773
artwork files; all sky/Content/artwork SHA256 checks pass; exit 0 without a display,
audio device, server or .NET installation. The Python helper requires Python only
for testing; playing requires neither Python nor the .NET SDK.

Then drag the app into Applications, open via Finder/right-click → Open, and
confirm its icon and window. If quarantine blocks the verified unsigned app:

```sh
xattr -dr com.apple.quarantine /Applications/AllegianceCore.app
open /Applications/AllegianceCore.app
```

First launch should create
`~/Library/Application Support/AllegianceCore/player.json` and show the configured
lobby. This follows .NET’s macOS ApplicationData mapping, documented in
[Microsoft’s folder-path compatibility note](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/8.0/getfolderpath-unix). Edit its server/port/pilot/lobby to the operator's values and relaunch through
Finder. Also run a direct-connect command (substitute the real host and port):

```sh
/Applications/AllegianceCore.app/Contents/MacOS/Allegiance SERVER_HOST SERVER_PORT "P726 Mac Pilot" --sky hd
```

Join a team, ready/dock/launch, check native ships, shop, F1/F6, HD sky and the
standard black-sky layers. Enable music and dock/undock: no placeholder tones should
play; effects/voices should still work. Inspect the console for manifest violations
and verify no private Meshy configuration notice on an ordinary native launch.
Explicit `--ships meshy` without a pack should warn once and render native ships.
Move/reinstall the app and confirm player/keymap settings survive. Keep the Mac
log and census with Claude's rig evidence. Signing/notarization and real rig/221/
200v200 tests remain in Claude's lane.

## P738 merge onto master

Resolved the `codex/p726` merge into `integ/batch2` using file edits only.
No git command, staging, commit, push, tag or release was performed; the merge
index remains for the dispatcher to finalize.

- Retained all master sound events, dispatch cases and sound-event tests,
  including P719's dock, welcome-home and boarding cues. Native rules remain
  those at `~/Development/Allegiance/src/WinTrek/trekigc.cpp:2859-2860`
  and `~/Development/Allegiance/src/clintlib/appmsg.cpp:2071`.
- Unioned the master MGCB registrations with P726's six repaired fixed sounds
  (`payday`, `powerupdevelopment`, `powerupshield`, `receivechat`,
  `receivedcomchat`, `receivepersonalchat`). Retained the P719/P723 assets and
  the complete loop bank. Native fixed-sound identifiers remain those at
  `~/Development/Allegiance/src/Igc/sounds.h:26-29,39-45`.
  The audit finds **36 distinct event kinds / 36 distinct dispatch cases**,
  **510 unique MGCB build entries**, and all **70 fixed SoundManager assets**
  registered, with every MGCB source present.
- Preserved P726's macOS bundle/launcher/icon, packaging smoke tests, quiet
  default Meshy selection and silent missing music beds.
- Unioned both `TEST_FLAKES.md` additions. Kept master's completed P717 report
  byte for byte, including its measured suite results.
- Compared both parity matrices by row against both parents and their common
  ancestor, reading repository objects directly without invoking git. There
  are no incoming-only row changes and no differently changed rows on both
  sides. Seven simulation rows changed identically on both sides. All other
  changes belong to master (11 interface rows and 43 simulation rows differ
  between the parents). Retained every master row and its evidence, and
  normalized the interface matrix's mixed line endings to **LF**. Both matrices
  now contain only LF endings.
- The first broad Core pass exposed an obsolete P652 test contract: the miner
  lockout regression still asserted `SendChat` with the old, incorrect ID 1342.
  It failed **3/3 exact-method reruns** before editing. Updated the assertion
  to P725's `DroneChatEvent` seam with **ID 341**, retaining the initial-backoff,
  two-miner complaint, cooldown and zero-ore assertions. Native evidence:
  `~/Development/Allegiance/src/Igc/shipIGC.cpp:2199` and
  `~/Development/Allegiance/src/Igc/sounds.h:395`. This is a stale test corrected
  during integration, not a load flake; no product code changed for it.

`python3 scripts/parity-scorecard.py` validates **438 interface rows** and
**473 simulation rows**:

| Matrix / area | Weighted | Unweighted |
| --- | --- | --- |
| Interface total | 1314/1479 (88.84%) | 351/409 (85.82%) |
| Simulation total | 894/1331 (67.17%) | 278/436 (63.76%) |
| Simulation sounds | 404/592 (68.24%) | 147/227 (64.76%) |

The Release whole-solution build passed with **0 warnings / 0 errors**, then
passed **0/0 again after correcting the P652 assertion**. All **95 selected C#
methods passed 3/3 fresh exact-method processes** (285 invocations / 363 data
case passes). All **8 packaging methods passed 3/3 individually** (24 passes).
The standalone never-parks gate passed **5/5**, with low-speed-with-target
maxima of **2.104, 2.013, 1.503, 2.004 and 2.017 seconds**, all below the
unchanged five-second threshold.

Completed the sequential Core, ClientCore and MonoGame passes, then repeated
Core after the fixture correction. Raw broad-suite results are retained:

| Pass | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Initial Core | 4780 | 2 | 0 |
| ClientCore | 1177 | 2 | 1 |
| MonoGame | 2344 | 0 | 3 |
| Post-fix Core | 4781 | 1 | 0 |

The corrected P652 assertion passed **3/3 exact-method reruns** after rebuilding
and passed the post-fix Core sweep. It is the one corrected deterministic
failure. Every remaining failing case passed **three fresh isolated processes**:

| Case | Observed failure | Isolation |
| --- | --- | --- |
| `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Initial Core: no `OnPeerJoined` for raw peer. | 3/3 unchanged exact-method runs |
| `P655ProbeWorkersLiveTests.Actual_workers_two_seats_unique_probes_and_serves_MCP` (authenticated) | ClientCore: bot did not confirm launch after two buy attempts. | 3/3 exact-method runs, both modes each time |
| `P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches` (4/35) | ClientCore: research/upgrade condition timed out in match one. | 3/3 case passes in isolated method runs |
| Same P705 method (25/3) | First isolation: side 1's Teleport constructor remained travelling/building while side 0 had upgraded. | Next two isolated method runs plus one exact-case standalone run: 3/3 after the timeout |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | Post-fix Core: empty notification collection. | 3/3 unchanged exact-method runs |

The P705 isolated method invocations reported **3 pass / 1 fail**, then **4/4**,
then **4/4**. Its final standalone 25/3 invocation selected exactly one case and
passed both full matches. No failed attempt was discarded. These satisfy the
brief's load-flake rule; research, worker, rotation and transport assertions,
deadlines and product behavior were unchanged. Underlying timing/navigation
causes are not established as fixed. The existing optional P705 diagnostic
file snapshots were enabled for its isolated invocations, recorded in
[diagnostic options](P738_P726_assets/diagnostic-options.json).

The Core passes exclude only
`MeshyGuardTests` and `PrivatePathGuardTests`, which invoke git internally and
therefore conflict with this task's explicit prohibition. Real loopback tests
and LiveTiming cases remain enabled; external rig/221/200v200 proofs remain
in Claude's lane.

Evidence: [initial build](P738_P726_assets/build.log),
[final build](P738_P726_assets/build-final.log),
[scorecard](P738_P726_assets/scorecard.txt),
[resolution audit](P738_P726_assets/resolution-audit.json),
[parity row audit](P738_P726_assets/parity-row-audit.json),
[focused methods](P738_P726_assets/focused-methods.json),
[invocation ledger](P738_P726_assets/verification.json),
[case qualification](P738_P726_assets/case-results.json),
[pre-fix regression triplet](P738_P726_assets/regression-red.json), and
[all raw TRX/logs and diagnostic snapshots](P738_P726_assets/validation-logs.tar.gz).
Reproduction scripts: [main gates](P738_P726_assets/verify.py),
[post-fix Core](P738_P726_assets/finish.py), and
[25/3 follow-up](P738_P726_assets/research-followup.py).
