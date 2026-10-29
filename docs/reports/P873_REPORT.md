# P873 — real-client training launch repair and native inventory

The real client now joins the training host's trainee instead of creating a second, docked ship. Missions **2, 3, 4, 5, 6, 8 and 10** all launched into space with an active lesson in real windows, in **Classic and Modern**. This certifies launch and initial progression. It does **not** certify full playable completion: the inventory exposes production bindings that earlier headless tests replaced with injected outcomes.

No commits, pushes, stashes, remote-machine contact or synthetic CPU load. Changes remain in the working tree. Native citations are relative to native `src/`.

## Changes

- `TrainingHost.Start` installs a `ShipSpawner`. Side selection removes the headless id-0 placeholder and creates the trainee using the LOGONACK-reserved id, lesson hull, origin, +X forward and +Z up. The custom spawn bypasses initial hangar docking. `PlayerShip` and computed `PlayerShipId` identify that same ship; headless callers still have a trainee before login. Native: `training/trainingmission.cpp:132-140`.
- The actual `Game1.UpdateTraining` waits for an airborne replicated player pose before starting the script. The existing 20-second failure path remains. The new integration tests invoke that production pump around real loopback logon/side-selection/replication for all seven variants.
- Background ticking now advances host autopilot and preserves the connected trainee's input. Scripted creation/command/autopilot writes share the simulation lock. Station Goto uses the station record's sector rather than assuming station id equals sector id. The host's simplified cruise driver remains partial relative to native AI (`training/trainingmission.cpp:589-647`).
- Audited all host `PlayerShip`/`PlayerShipId` uses: home station derives from the trainee side/start sector; command/autopilot and M6 kill credit resolve the current ship; M8 mounting uses the script's live ship id. No host operation remains pinned to ship 0. `TrainingSession` detects the replicated dock edge and invokes `ShipLanded`, including Mission2's termination (`WinTrek/trekigc.cpp:4131`, `training/mission2.cpp:90-94`). Complete same-frame hangar suppression remains separately partial.
- Added `[training] goal N` diagnostics using a read-only nested goal path, plus prompt/voice logs. Graph inspection does not evaluate conditions.
- Real inspection found absent narration callbacks, which left the first sound goal permanently playing. `TrainingSoundCatalog` maps script-local ids to native clip stems, including different meanings of 3049 in M3/M4 and Free Flight's reused M6 voices. Game1 supplies playback/status/stop callbacks; absent audio is stopped rather than permanently playing. Native: `training/playsoundaction.cpp:35-64`, `WinTrek/soundinit.cpp:15-31`. No new sound media.
- The first live window exposed another crash in the procedural ship fallback: `ModelBone.AddMesh` did not establish `mesh.ParentBone`. The factory now sets it explicitly. The commander is also announced with hull metadata before join. Automatic stock miners are disabled because the lessons create their own cast.

## Real windows

Each run used the existing `ALLEGIANCE_TRAINING_START=<id>`, scratch `XDG_CONFIG_HOME`, an explicit `ALLEGIANCE_HUD_SETTINGS` JSON file, `--window 1280x720 --autoshots 5,15 --autoshot-dir ... --exit-after 20`, bounded by `timeout 55s`. All 14 final runs exited normally and produced two backbuffer PNGs. Every retained screenshot was opened and inspected. An exploratory run used lowercase `style` and did not select Classic; those captures were discarded and Classic was rerun with `"Style": "Classic"`.

| Mission | Classic | Modern |
|---|---|---|
| 2 Basic Flight | [frame](P873_assets/m2_classic.png) | [frame](P873_assets/m2_modern.png) |
| 3 Basic Weaponry | [frame](P873_assets/m3_classic.png) | [frame](P873_assets/m3_modern.png) |
| 4 Enemy Engagement | [frame](P873_assets/m4_classic.png) | [frame](P873_assets/m4_modern.png) |
| 5 Command View | [frame](P873_assets/m5_classic.png) | [frame](P873_assets/m5_modern.png) |
| 6 Practice Arena | [frame](P873_assets/m6_classic.png) | [frame](P873_assets/m6_modern.png) |
| 8 Nanite | [frame](P873_assets/m8_classic.png) | [frame](P873_assets/m8_modern.png) |
| 10 Free Flight | [frame](P873_assets/m10_classic.png) | [frame](P873_assets/m10_modern.png) |


[Sanitised launch/goal evidence](P873_assets/launch-evidence.txt) includes each trainee binding and active script/goal. M3 shows its target-centre prompt and M4 its chat-pane callout. The other lessons have live narration/goal state in the logs. Modern arena frames show a close model/lighting obstruction; camera/visual parity is not claimed. M5 starts airborne as this brief requires; native M5 instead docks at station 1050 in command view (`training/mission5.cpp:84-106`). That override is explicitly **missing** in the matrix.

## Inventory

`docs/parity/training-matrix.csv` contains **241 rules**, the same 17 columns as the sim matrix, TRN ids, weights 1–5 and only implemented/partial/missing/n/a statuses. `scripts/parity-scorecard.py` validates and prints its own TOTAL by default. Training-specific schema, prefix, weights and required rule/evidence fields fail closed.

The native directory has 188 direct entries: 180 C++ source/headers (88 cpp, 92 h), five historical project/workspace files, `spec.html` and two directories. The source/header audit covers every runtime pair; only `pch.cpp`/`pch.h` are compile-time plumbing. All **82 framework types/macros** have a rule; the two destruction-constructor macros share their observable predicate. Recipe/cache behavior has additional rows. Shared Mission6/10 AI is split into target preference, assistance, sector attack restrictions, pursuit, local patrol and hidden-buoy rules; paused-frame and respawn-consumer rules are explicit too. Nested `asteroids/main.cpp` and `specification/format.pl` are inventoried and ported. Historical DOC/XLS/HTML specifications are design artifacts, not additional executable rules. The recursive tree contains 198 files.

Integration coverage includes WinTrek, clintlib and Igc launch, chooser/slideshows, frame/input, HUD/camera/radar/panes, chat/acknowledgement, tips, visibility, docking/death/restore/station loss, queued-order timeout, team actions, offline loadouts/commands/mines, and legacy Mission7 hooks. Training `ImissionIGC` creation/default-order paths are included. Searches covered `src/`, `tools/`, older CSVs and TR1–TR5/P785 reports before assigning missing. A wrapper calling an inert site method does not earn implemented credit.

Each cell is **rows / summed weight**. Partial earns no scorecard credit.

| Area | Implemented | Partial | Missing | n/a |
|---|---:|---:|---:|---:|
| training-framework | 30 / 91 | 42 / 170 | 13 / 37 | 1 / 1 |
| training-integration | 22 / 82 | 37 / 138 | 27 / 109 | 1 / 1 |
| training-missions | 1 / 4 | 63 / 270 | 0 / 0 | 2 / 2 |
| training-tools | 2 / 3 | 0 / 0 | 0 / 0 | 0 / 0 |
| TOTAL | 55 / 180 | 142 / 578 | 40 / 146 | 4 / 4 |


Headline: **19.36% weighted (175/904), 21.94% unweighted (52/237)**. Four n/a rows, weight 4, are excluded. Sixteen framework rows remain test-only in the denominator: thirteen absent/unbound primitives plus three implemented helpers (NoAction, OneTimeAction, ProxyAction) that no production script instantiates. This inventory overlaps broader matrices; its denominator must not be added to theirs as independent features.

Highest-weight missing rules (partial work is also outstanding):

| Row | Weight | Behavior |
|---|---:|---|
| TRN-0161 | 5 | Command View setup override: Start Mission5 docked at station 1050 with command view displacement enabled and all radar contacts. |
| TRN-0167 | 5 | Native default training loadout: Buy the local default loadout or mount preferred parts and prescribed cargo then fully charge shield ammo fuel and energy. |
| TRN-0179 | 5 | Death and eject: Save the old camera and sector move the killed trainee away from the launcher halve escape speed eject and detach the ship. |
| TRN-0183 | 5 | Last restart station lost: After the last friendly restart station dies show defeat clear pending waits and end the lesson after four seconds. |
| TRN-0184 | 5 | Default drone order dispatch: Let the training site choose mission-specific orders before ordinary NPC defaults. |
| TRN-0217 | 5 | M3 starting equipment and energy: Equip anti-shield part 92 remove missiles select only the first weapon and reduce side maximum energy to one quarter. |
| TRN-0220 | 5 | M4 initial stealth setup: Multiply maximum energy by ten dock the commander enable group fire and remove the missile magazine. |
| TRN-0222 | 5 | M8 nanite cargo and tech: Grant Nanite2 tech replace probes with proximity mines place Nanite2 in cargo and grant enemy sniper tech. |
| TRN-0223 | 5 | Arena initial equipment: Grant arena parts and hull tech rebuild the default loadout and replace mounted and cargo magazines with five Seeker2 missiles each. |
| TRN-0234 | 5 | Arena target selection: Scan visible live ships prefer miners by weighted distance and ignore player targets until more than two training kills. |
| TRN-0235 | 5 | Arena nearby attack and assistance: Attack a close threat first recruit a nearby armed friendly wingman or inherit a nearby ally Attack or sufficiently distant Goto with temporary aggression. |
| TRN-0060 | 4 | ResetShipAction: Snapshot position velocity orientation rotation fuel shield armour ammo and energy on first execution and restore on later executions. |
| TRN-0075 | 4 | SuspendedPlaySoundAction: Insert a sound-finished wait before playing a voice so ordinary goals stop advancing until it finishes. |
| TRN-0186 | 4 | Station destruction hook: Forward destroyed stations and their launcher to the active training mission. |
| TRN-0187 | 4 | Chat recipient and self command: Record the training chat recipient and prevent self-directed targets while command view is disabled. |
| TRN-0189 | 4 | Training enemy visibility: Use the enemy side visibility and eye latch for other sides during local training. |
| TRN-0216 | 4 | M2 enlarged flight bounds: Use universe radius 20000 and out-of-bounds limit 100 to allow flight practice. |
| TRN-0236 | 4 | Arena sector attack restrictions: Attack in home or enemy sectors and attack Mars targets only when the home sector contains no ships. |

The largest partial gaps are ordinary lesson keys, live target/geometry resolution, real equipment/damage, command-map picks/view state, native training NPC orders and spawn recipes, and death/end flows. All seven interactive mission rows remain partial. Headless completion that manually mounts parts, moves ships, credits kills or changes fractions cannot certify those interactions in the client.

## Native intent

**Design:** preserve goal/constraint/wait ordering, strict timer boundaries, masks, lesson branches and completion rules. Both HUD styles remain supported; visual layouts may differ. A future data-driven lesson format should retain these semantics.

**Era constraints:** direct local trekClient/IGC actions become embedded-server operations plus ordinary loopback replication; no new training protocol is needed. Shipping/debug clock-key differences remain. Managed references replace pointer ownership only with an equivalent lifetime argument. Proxy references retain pure graph nodes without disposing their referents. Buoy consumer release is **missing**, since a retained waypoint dictionary does not reproduce native removal lifetime.

**Bugs:** HasAmmo means ammo below threshold; asteroid Y uses width's multiplier; ResetAction's entire Execute body is commented out. The authoring tools explicitly reject nonpositive counts (native divides by zero) and avoid the formatter's infinite loop on an unbroken word over 60 characters. Valid-input behavior and the Y formula are preserved.

Only ResetAction's disabled reset body, nonexistent Mission7/Mission9 scripts, and the unreachable legacy download URL flow are n/a. Native `IsInstalled` always returns true. Mission1 is an implemented Introduction slideshow. Remaining executable Mission7 flag/hooks are inventoried separately as partial. No modding tool is excluded merely because it is a developer tool.

## Authoring tools

`tools/p873-training-tools.py asteroids --count 11 --seed 873` emits native TSV and logs `seed=873` to stderr. It preserves floor-grid count (11 emits 9), radius/ore/texture/mass/signature/rotation and jitter formulas including the Y bug. A portable seeded RNG replaces time-seeded platform C rand; identical libc random sequences are not claimed.

`tools/p873-training-tools.py format-comments <csv>` ports the Perl first-comma split and 60-column comment output. `tools/test-p873-training.py` checks generator/formatter behavior and malformed matrix contracts. No service, port or new environment variable was introduced.

## Proposed follow-up split

Suggestions only. Each task has at most 20 rows, with exact membership in the matrix `queue` column. Implement bindings before promoting dependent mission rows. These batches contain UI/camera/input work and need an image-capable lead; Codex can implement the sim and pure-model portions. Real-window lesson completion is the acceptance check.

| Suggestion | Rows | Scope (exact membership in CSV) | Lane |
|---|---:|---|---|
| P874a | 20 | AbstractTarget through GetShipHasAmmoCondition | Image-capable lead; Codex sim/model |
| P874b | 20 | GetShipHasCargoCondition through SetRadarLODAction | Image-capable lead; Codex sim/model |
| P874c | 15 | SetSignatureAction through CurrentTarget eliminated cache | Image-capable lead; Codex sim/model |
| P875a | 20 | Mission 2: Basic Flight through M3 lesson 09 | Image-capable lead; Codex sim/model |
| P875b | 20 | M3 lesson 10 through M5 lesson 04 | Image-capable lead; Codex sim/model |
| P875c | 20 | M5 lesson 05 through M10 early waves | Image-capable lead; Codex sim/model |
| P875d | 3 | M10 bomber arrival through M10 final wave | Image-capable lead; Codex sim/model |
| P876a | 20 | Local mission factory through Last restart station lost | Image-capable lead; Codex sim/model |
| P876b | 20 | Default drone order dispatch through No career training unlock | Image-capable lead; Codex sim/model |
| P876c | 16 | Training HUD/keymap restore through Too-close rotating warnings | Image-capable lead; Codex sim/model |
| P876d | 8 | Shared arena targeting, assistance, sector pursuit/patrol, paused frame and respawn retention | Codex sim/model; image-capable client acceptance |


## Hosting architecture

Keep scripts in ClientCore against `ITrainingSite`. Game1 supplies real input, target/pick/camera state, narration and both HUD renderers. `Allegiance.SinglePlayer.TrainingHost` supplies the PCore014/authored-map authoritative world. Creation/loadout/commands/damage should mutate actual IGC models and use ordinary replication; replace diagnostic dictionaries with real model reads. Add explicit native training AI/default-order and end policies rather than relying on multiplayer defaults. Headless `TrainingHostSite` and an MCP bot adapter can drive deterministic integration scenarios, but cannot replace real-player completion proof. Native chooser advancement is a local recommendation/startup flag, not a career score unlock.

## Verification

- Solution build: zero warnings/errors.
- All 92 tool projects built: zero warnings/errors.
- New C# suite: 13/13 three times alone, including seven real join orders, narration id collisions and absent-audio completion.
- New Python suite: 4/4 three times alone.
- Real windows: 14 final normal exits, both styles, reviewed above.
- Native audit: 241 rules, 82 types/macros, zero errors.
- Scorecard, glossary links, `docker compose config --quiet`, artwork manifest `--check`, and whitespace checks validated.

The full Fast gate ran once: **11,475 cases, 11,468 passed / 4 failed / 3 optional skips**, 464.4 seconds (the runner's automatic quarantine retry also passed). Infrastructure passed. The gate is **red**, not reported as a clean sweep:

| Failure / timing observation | Result and attribution |
|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | Default user configuration is read-only in this sandbox; passed alone with scratch `XDG_CONFIG_HOME`. Already ledgered under P868. |
| `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight` | NPC moved in front of the peer before the off-screen premise assertion. Existing fixture failure, already baseline-tested and ledgered under P864d/P868. No radar edits. |
| `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` | Existing network/relay timing fixture, ledgered under HUD-19/P761b. No relay/transport edits; passed alone after the sweep. |
| `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` | Quarantine failure; runner's automatic retry passed. Existing ambient-treasure fixture assumption. |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | Assertion passed; 10.623 s exceeded the calibrated 10 s limit. Same timing observation already ledgered; passed alone after the sweep. |

The separate broader training regression sweep passed **61/62**. Nanite's headless completion test failed at “goal 3: bomber never crossed into sector 1052.” An isolated compilation/run with the **original HEAD TrainingHost** failed at the identical assertion (~74 s), establishing the host change did not introduce it. The P873 host was restored and the final solution rebuilt cleanly. This existing gap is recorded in `docs/TEST_FLAKES.md`; it is not called a passing test or proven intermittent flake.

Raw task logs remain under `.scratch/p873/`; the full gate summary is `TestResults/tiers-20261029T000219.057074Z/summary.json`. Retained public evidence contains no absolute home paths. No commit-range publish guard was claimed: this lane creates no commits. Working-tree source hygiene and all 14 new screenshots were reviewed.

