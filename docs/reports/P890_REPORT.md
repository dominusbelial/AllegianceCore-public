# P890 — local aim authority through the real ship-update path

Status: **complete**, with the single Fast-tier run retaining the unrelated failures/timing limits ledgered below. The fix, regression, real Classic completion, full training repetitions, Live flight repetitions and build/AOT gates are verified. Base: `8181619e`. No commits, pushes, stashes, remote hosts, synthetic CPU load, or display `:0` were used.

## Reproduction outside the proof

`P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim` connects a production `ConnectionSession` over TCP to either the real `TrainingHost.BasicWeaponry` (native authored start at the origin) or an ordinary two-team loopback match. It runs production `OwnShipSimulator`, sustained zero-dispersion core weapon fire, saturated yaw and pitch reversals deliberately between packet boundaries, six seconds hovering and six seconds cruising. It sends real pose/cookie packets every 0.15 simulation seconds and measures against the actual server ship and newly created real projectiles at the same simulation instant. No Game1 proof code, replicated steering, pose injection, collision outcome injection, or private dispatch calls drive the test. The receive counter synchronizes socket delivery before each deterministic tick; it does not fast-forward while waiting for TCP.

Both cases explicitly select `native` or the comparison-only `port` model. Immutable snapshots, encode-once, parallel broadcast, pool transport, and native NPC travel are the baseline defaults (also explicit on the ordinary server). Samples are deterministic simulation time, not a wall-clock ten-minute soak. The 171° historical spike is **not** claimed as reproduced by this bounded scenario.

Values below are **median / p95 / maximum**, degrees for forward/shot and metres for position; 240 pose samples and 144 training / 119 skirmish projectile samples per case. Machine-readable receipts: [loopback.json](P890_assets/loopback.json). Before means the original `HEAD` dispatch compiled with the same final test; all four cases fail its sustained-aim assertion. After means the corrected dispatch.

| Case × broadcast model | Before forward ° | After forward ° | Before position m | After position m | Before shot ° | After shot ° |
|---|---:|---:|---:|---:|---:|---:|
| Training × native | 0.7778 / 8.5645 / 8.6789 | 0 / 0.2382 / 2.5282 | 0.0013 / 0.0198 / 0.0334 | 0 / 0.0179 / 0.0236 | 0.7778 / 8.5308 / 8.6789 | 0 / 0.1988 / 2.5282 |
| Training × port | 0.7778 / 8.5645 / 8.6789 | 0 / 0.2382 / 2.5282 | 0.0013 / 0.0198 / 0.0334 | 0 / 0.0179 / 0.0236 | 0.7778 / 8.5308 / 8.6789 | 0 / 0.1988 / 2.5282 |
| Skirmish × native | 0 / 2.1801 / 3.8100 | 0 / 0.1887 / 1.9068 | 0.0007 / 0.0243 / 0.0396 | 0 / 0.0249 / 0.0310 | 0 / 2.1801 / 3.8091 | 0 / 0.1520 / 1.6337 |
| Skirmish × port | 0 / 2.1801 / 3.8100 | 0 / 0.1887 / 1.9068 | 0.0007 / 0.0243 / 0.0396 | 0 / 0.0249 / 0.0310 | 0 / 2.1801 / 3.8091 | 0 / 0.1520 / 1.6337 |

**Conclusion:** the origin failure amplifies training's divergence, but the omitted live turn rates affect ordinary human clients in either broadcast model too. These results do not establish that every player experiences the historical 171° extreme. Reversals, packet spacing, and frame cadence determine the visible error.

## Root cause and native intent

1. **The server discarded the training client's entire attitude.** `TrainingHost.cs:258-262,305` intentionally spawns the trainee at `(0,0,0)`, matching native `training/trainingmission.cpp:134-139`. The original `Server.Dispatch.cs:376` classified `Time==0 OR Position==0` as legacy controls-only, bypassing pose/cookie validation and leaving the server to integrate separately. `Game1.Prediction.cs:305-311` nevertheless sent the actual camera/own-sim pose. At rest during weaponry, zero position persisted throughout the firing exercise. Controls shared between different frame/tick integrations are not a replacement for native pose authority.
2. **The server discarded the replayed live angular state even away from the origin.** `ProcessPeerShipUpdate` decoded `payload.TurnRates` into a temporary array and `Ship.CalculateShipPose` advanced that array, then accepted orientation without assigning those rates back to the ship. Its next `ExecuteMove` used its old angular rates. Native `GetOVTP` exports straight into **`m_turnRates`**, `Igc/shipIGC.cpp:3074-3082`; `ProcessShipUpdate`, `3139-3187`, calls `CalculateShip` and always accepts orientation. `CalculateShip`, `3244-3293`, and `ExecuteShipMove`, `2885-2917`, continue with that same member state.

Native send/apply chain, rechecked in the read-only reference:

- `WinTrek/WinTrek.cpp:8650` calls `SendUpdate(lastUpdate)`; `clintlib/clintlib.cpp:1815-1863` gates on a flying, unparented ship, sends at 0.15 s, exports its own ship pose, echoes the cookie, and converts the ship's timestamp to server time.
- `FedSrv/FedSrv.CPP:4851-4864` checks cookie, cluster, and no parent before `SetShipUpdate`; `FedSrv/fsship.cpp:1173-1200` checks increasing timestamps, calls IGC validation, records accepted updates, and sends a new-cookie `SHIP_RESET` on rejection.
- `Igc/shipIGC.cpp:3145-3146,3162-3187` checks time/position, imports controls and live turn rates, replays to the server instant, always accepts orientation, and checks position before accepting position/velocity.
- **Projectile creation is shared IGC simulation, not an independently supplied client aim command.** Native's client advances its own IGC mission at `WinTrek/WinTrek.cpp:8721`; FedSrv advances its server IGC mission at `FedSrv/FedSrv.CPP:4251-4254`. Mounted weapon updates call `FireWeapon` (`Igc/shipIGC.cpp:319-341`, `Igc/weaponIGC.h:50-56`). `Igc/weaponIGC.cpp:180-203` takes the firing ship's pose (the gunner's orientation for a manned turret); `255-269` builds forward and velocity; `290-303` creates the projectile in that IGC mission. Thus the client's own rounds use its own visible ship, while FedSrv's IGC copy creates authoritative rounds from the copy it just accepted. The port's authoritative spawn remains `Sim/Weapon.cs:217-355`; correcting that copy preserves the native rule.

This evidence does not support recoil as the cause: `Weapon.FireWeapon` adds dispersion to the projectile and consumes resources; it does not apply a recoil impulse or alter ship orientation. Training approvals already feed identical controls to own simulation and wire (`Game1.FlightInput.cs:2452-2499`). No new integrator, camera reconciliation, or input-mask exception was added.

## Fix and regression

- `src/Allegiance.Server/Server.Dispatch.cs`: only the explicit time-zero command selects the legacy scripted path. A timed origin packet goes through the ordinary cookie, timestamp, and movement validation.
- Only `LocalTraining && mission.IsTraining` permits the authored zero position. Native offline training has no FedSrv legal-position rejection, so this is an explicit adaptation for our embedded host's TCP boundary. Ordinary network games retain native's strict nonzero legal position; no scene coordinates are shifted.
- Client-flown updates retain all three replayed turn rates on the live ship, including the native always-accept-attitude/rejected-position case. Scripted server-flown echoes keep their existing full-precision angular authority.
- `Game1.TrainingProof.cs` removes `TryReplicatedOwnPose`, `ReplicatedAimDegreesTo`, and `SteerTowardsReplicated`; kill steering uses the same local pose as the camera and pointing gates. The proof's final approach now uses real held W thrust after braking, since its previous throttle-zero tap parked outside the hangar.
- `P890OwnAimLoopbackTests` asserts p95 forward error <2°, peak forward and projectile error <3°, position error <5 m, real projectiles, and no pending correction. Reversal controls can be unknown to the server for up to 0.1 s at the 0.15 s send cadence; the measured residual peaks are 2.53°/1.91°. These bounds allow that transient and compact-wire rounding while failing all four original cases. There is no claim of exact same-instant equality across arbitrary network delay.
- `P507OwnShipModelLiveTests.Timed_origin_does_not_bypass_the_native_cookie_gate` verifies that a timed origin with a wrong cookie cannot apply steering, while the existing time-zero compatibility case continues to pass.

The first training cohort also exposed a test-driver cost: `TrainingHostMission2Tests.Mission2_RunsToCompletion_AgainstTheRealEmbeddedHost` passed but consumed **24m44.4s**, nearly all cadence sleeps. This fixture has no TCP client. Its `Frame` now calls the existing `TrainingHost.StepLight(0.05f)` instead of paced `Step()`, preserving the 20 Hz mission/host-autopilot step and all assertions. This changes test driving only; production ticking and the real loopback/window coverage are untouched. The two affected fixture tests pass 3× alone in about 2–3 seconds per run (`headless-m2-1..3.trx`). The final complete 76-case ClientCore training cohort is also revalidated, and the three-round runner completes with the unpaced fixture.

Parity: SIM-0585 updated in `docs/parity/sim-rules-matrix.csv`; TRN-0097 records the real completion without replicated steering in `training-matrix.csv`; glossary now indexes own-ship pose authority.

## Real Classic window

**PASS.** `.scratch/p890/bay-completion/window.log` records all weapon goals, green-bay approach, `FM_S_DOCKED(61) station 1010`, `[training] goal complete (mission 3)`, `[training] mission complete`, and `[training] slideshow post for mission 3 (1 slides)`. The host shuts down at 2,181 ticks after genuine docking; no lesson outcome or pose was injected. The direct `mission FINISHED` proof log is bypassed when `UpdateTraining` immediately replaces its completed session with the post slideshow; the production completion/dock/slideshow receipts are the evidence.

The real MonoGame run used `SDL_VIDEODRIVER=offscreen`, isolated Classic HUD settings and XDG config, `timeout 400`, `--exit-after 380`, `ALLEGIANCE_TRAINING_START=3`, and `ALLEGIANCE_TRAIN_PROOF=1`. It uses only the local aim and actual player keys. Earlier before/fixed runs passed every weapon goal but exposed the proof-only station-centre/zero-throttle landing stall. The final proof selects the nearest authored bay, stages outside it, brakes, and enters with held W thrust; it imports no autopilot controls or host movement.

Classic approach frame (kept out of the repo; local evidence), personally inspected: the cockpit HUD shows the blue outpost structure, target “Base” at 492 m, a roster overlay, and speed 12 m/s while approaching. **This frame precedes completion**; the production log above proves completion. Timed in-flight screenshots cease after the client returns to the post slideshow, so no post-slideshow image is claimed.

Reproduction command (from this worktree, with isolated settings):

```bash
set -a
source local.env
set +a
export SDL_VIDEODRIVER=offscreen
export XDG_CONFIG_HOME="$PWD/.scratch/p890/bay-completion/xdg"
export ALLEGIANCE_HUD_SETTINGS="$PWD/.scratch/p890/bay-completion/hud.json" # {"Style":"Classic"}
export ALLEGIANCE_TRAINING_START=3
export ALLEGIANCE_TRAIN_PROOF=1
timeout 400 "$HOME/.dotnet/dotnet" src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.dll \
  --autoshots 100,180,300 --autoshot-dir "$PWD/.scratch/p890/bay-completion/shots" --exit-after 380
```


## Boarded gunner

**Separate defect; deliberately not folded into pose authority.** With `ALLEGIANCE_SHIP_UPDATE_MODEL=native`, the existing P803 docked-parent test still fails specifically at `P803TurretGunnerLoopbackLiveTests.cs:205` (`parent.Latest` is null). Port mode supplies that parent pose through teammate posBatch. Native mode suppresses posBatch (`Server.Broadcast.cs:1066,1556`); `Sector.GetShipUpdateCandidates`, `Sector.cs:735-740`, excludes a docked parent before dispatch or turn-rate replay is involved.

Native also removes a docked ship from the cluster: `Igc/shipIGC.h:421-459`, especially `s->AddShip(this)` and the explicit null-cluster assertion, `457-459`; native's send eligibility is cluster-based (`clintlib/clintlib.h:1198-1203`). Our gunner HUD's dependence on a remote `parent.Latest` while in the hangar differs from native's local IGC representation. Fixing the hangar source model or its initial pose is separate work, not a reason to restore quadratic per-tick posBatch. No rig contacted. In-flight turret firing and ordinary P803 port-mode checks passed in all three repetitions, together with the long human-flight cases; [receipts](P890_assets/flight.json).

## Performance and gates

The correctness change adds a constant three float stores per client pose update and one local-training origin condition. It adds no allocations, scanner work, broadcast candidates, packets, locks, or per-tick passes. Pose replay and its temporary array already existed. The origin case starts using that existing validator instead of silently abandoning local authority; its new cost is training-only. No experimental performance feature or baseline flag was changed. A separate bounded A/B uses the baseline **native + immutable + encode-once + parallel + pool + native GotoPlan + server GC** settings, one real TCP human, sustained gunfire and turning/cruising. It alternates original/fixed dispatch binaries three times, sampling 2,000 ticks after 400 warm-up ticks per run (6,000 ticks per version). The original server is built in a scratch project from `git show HEAD:src/Allegiance.Server/Server.Dispatch.cs`; the working source is never reverted for this benchmark. Harness source: `.scratch/p890/bench/Program.cs`. Raw logs: `cost-before-1..3.log` / `cost-after-1..3.log`; compact receipts: [cost.json](P890_assets/cost.json).

| Metric, milliseconds | Original | Fixed |
|---|---:|---:|
| Mean of the three run means | 0.72793 | 0.73025 |
| Median run median | 0.65380 | 0.63470 |
| Median run p95 | 1.29010 | 1.26670 |
| Run means, retained individually | 0.75343 / 0.68429 / 0.74607 | 0.74499 / 0.72748 / 0.71827 |

The mean difference is **+0.00232 ms (+0.32%)**, within the variation between runs; this bounded local sample shows no measurable tick regression. One 26.8132 ms fixed-run maximum is retained in the receipt; no unmeasured cause is assigned to that outlier. Mean sim-thread allocation does not increase (roughly 299 KB/tick before versus 297 KB/tick after, dominated by existing world/projection work). This is not a 200v200 claim; Claude's rig A/B remains the large-match authority.

| Check | Result |
|---|---|
| Solution build | PASS, zero warnings/errors (`build-final.log`) |
| All tools | PASS, all 94 (`build-tools-final.log`) |
| Server Native AOT linux-x64 | PASS (`aot-final.log`), no IL2026/IL3050 |
| Four new aim cases + existing native-own-flight test, 3 serial runs | PASS, 5/5 in each (`own-1..3.trx`) |
| P507 validation incl. new origin-cookie case, 3 serial runs | PASS, 8/8 in each (`p507-1..3.trx`) |
| Gunner/long human-flight affected tests, 3 serial runs | **PASS 7/7 ×3** (21 executions), including each ten-minute turn/boost/TiDi soak, rotations, reconnect clock, port-mode docked P803 and native in-flight turret fire. [Receipts](P890_assets/flight.json). |
| Full artwork training Fast + Live, both projects, 3 runs | **PASS 3/3**, 76 ClientCore + 60 MonoGame per round (408 executions), including Nanite and real loopback M2/M3. [Receipts](P890_assets/training.json); raw `.scratch/p890/training/`. Final ClientCore cohort also revalidated 76/76 after the fixture-only cadence change. |
| ONE Fast tier | Executed: **FAIL**, 11,741 tests, 538.2 s; 11,735 passed / 3 failed / 3 skipped. All new cases pass. Three unrelated failures and five timing violations are ledgered with 3× isolation; no second sweep. [Summary](P890_assets/fast.json). |
| Showcase never-parks 5×, server GC | **PASS 5/5**, longest targeted idle ≤2.006 s; [receipts](P890_assets/never-parks.json). |
| Glossary links, parity validation, whitespace | PASS; final receipts `parity.log` and link check (347 paths). |

Raw receipts and the retained benchmark sources are local, git-excluded under `.scratch/p890/` (training receipts moved there after completion); the Fast runner's receipt directory is `TestResults/tiers-20261030T011055.114989Z/`. Restore/build needed `NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p890/nuget-http` because the sandbox cannot write the shared default cache. The initial default-cache failure was infrastructure setup, not a build warning waived. The three non-task failures and five timing-limit violations are recorded with their three isolated outcomes in `docs/TEST_FLAKES.md` and [isolation.json](P890_assets/isolation.json). The profile failure persists only at the sandbox's read-only default user config path (passes with writable XDG config); the lobby case passes 3/3 alone; the previously ledgered NPC defender case fails 3/3. Four timing cases pass below the limit 3/3 alone; P777 remains near/over the timing threshold. These failures are **not** claimed fixed, and no second Fast run was used to erase them.

## Native-intent footer

**Classification:** native behavior restoration, plus the explicit local-training wire adaptation for native's legal origin. Own-ship orientation and angular state come from the pilot's native update; position remains validated; server-flown scripted pilots keep server authority. Visuals and weapon/damage balance are unchanged. The proof uses player keys and the same visible pose. Docked-gunner pose provisioning is a separately reproduced limitation. No rig validation is claimed and no commits or pushes were made.
