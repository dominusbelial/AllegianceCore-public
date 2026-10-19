# SC-4 — dispensers and scouting

SC-4 adds incoming-missile chaff defense, deliberate scout probe deployment, and a legal mine-carrying fleet share with defensive aleph placement. Changes remain in the working tree; no commit or push was made. Base: `d39525de`. Native references below are relative to `~/Development/Allegiance/src/`, revision `052f5179`.

## Behavior and native rules

**Chaff.** Every flying combat bot checks its own received `MissileTrack` warning, including a tracking missile with zero lock. Native computes that warning by finding in-sector missiles targeting the player's source ship (`WinTrek/trekigc.cpp:5028-5065`). The bot's outgoing missile lock does not trigger defense. Deployment requires the accepted inventory's mounted, loaded, nonempty chaff part, and is paced by its decoded load time, with a 0.5-second policy floor. Counter 1 is part 80, with a 3-second load time and 2-second decoy lifespan in PCore001.

The bot sends the ordinary `C_FIRE_EXPENDABLE` request. Native requires an unparented ship, the requested mount-0 dispenser, positive ammo, and a cluster (`FedSrv/FedSrv.CPP:5624-5647`); it then creates chaff and applies the existing chaff-strength/missile-resistance spoof probability (`FedSrv/FedSrv.CPP:5650-5712`). Native dispenser mounting and load-time pacing are in `Igc/dispenserigc.cpp:89-123`; decoy expiry is in `Igc/chaffigc.cpp:61,75-86`. Those rules and probabilities are unchanged. A chaff counter measures an actual deployment, not guaranteed missile diversion.

**Probes.** Dedicated scout roles deploy scanner probes at the designated contested sector, at nearby alephs in sectors without an owned station, or during visible combat. Quiet owned-station sectors are excluded. Live friendly probes suppress redundant nearby drops; native destruction/expiry removes that suppression. The 8-second minimum interval and spacing `min(1500, scannerRange × 0.15)` are bot policy. No ship is parked to deploy.

EWS Probe 1 is part 81/type 22: scanner range 500, load time 2 seconds, lifespan 3,600 seconds, and expendable size 2. Starting scouts have dispenser capacity 2, so each carries one. These are loaded core values, not SC-4 overrides. Probe creation uses the ordinary dispenser path (`FedSrv/FedSrv.CPP:5747-5774`); native lifetime and expiry are `Igc/probeigc.cpp:131-134,335-338`. Rescue capability bits are defined separately (`Igc/igc.h:781-782`), and rescue devices are excluded from scanner-probe selection, deployment, and duplicate-coverage checks.

**Mines.** One in three requested fighter slots per side receives defensive mine duty. It buys a legal mine-capable scout when the current dock, side techs, wallet, and mount permit one, retaining that duty through ordinary death/relaunch. Other locked combat roles temporarily flying scouts also prefer legal mines; dedicated scouts retain probes. A missing or unaffordable mine option falls back through the ordinary role policy. Finale bomber selection remains authoritative for reserved strike lives.

Mine-equipped bots lay while passing an aleph in an owned-station sector. The policy uses a 2,500-unit approach radius, a minimum interval of 3 seconds or the loaded dispenser interval, and separation of at least twice the mine-field radius. The existing opportunistic wingman trigger also remains: a visible different-side combat ship must be strictly closer than 2,500 units. Native wingman equipment, type, behavior-mask, pilot, and range checks are `Igc/shipIGC.cpp:2651-2665`. Deliberate defensive laying uses the native human dispenser action, whose acceptance has no enemy-range prerequisite (`FedSrv/FedSrv.CPP:5624-5779`). The approach radius and placement policy do not relax the wingman range comparison.

The controller runs dispenser decisions before travel, staging, aleph-exit clearing, defense, and engagement branches. It reads the bot's own accepted inventory, consumes normal wire echoes, and resets deployment timers with the ship's new life. Docked ships, pods, boarded ships, cargo, empty launchers, and unmounted parts cannot initiate these actions.

## Why SC-1 selected no mines

[Decoded catalogue and gate evidence](SC4_assets/catalog-gates.json) distinguishes availability from policy:

- Prox Mine 1, part 59/type 11, is already buyable by both starting civilizations. Its required bit is 355, mount mask 2048, and expendable size 2. Both starting scouts (410 and 114) have dispenser mask 7169 and capacity 2, so they can legally carry one.
- The selector assigned mines a zero score for scouts. Iron Coalition's unavailable fighter role fell back to those probe-only scouts; its starting interceptor 415 has no dispenser socket.
- Ga'Taraan's starting fighter 121 has dispenser mask 1024 and capacity 1. Prox Mine 1 does not mount there. Mine Pack 1 matches that socket but requires bits 210, 225, and 355, which are not all initially available. The defensive scout share addresses this asymmetry without changing tech bits or mounts.
- The old mine helper ran only during engagement and required a nearby enemy. It could not lay a defensive approach field while travelling even after the loadout gate opened.

Purchases continue through the existing station-local/side-tech, successor, human-hull, mount, capacity, and budget gates. Native purchase and mount references are `Igc/stationIGC.h:383-433` and `Igc/hullTypeIGC.h:163-213`. Tests independently revalidate the selected loadouts and remove the mine prerequisite to check rejection.

There was also a placement bug behind the equipment gap. Native `CmineTypeIGC::GetRadius()` returns the derived mine-field radius (`Igc/minetypeigc.h:104-106`). The port's dispenser drop used the unused base `Radius`, which is approximately −107,374,180 for Prox Mine 1, instead of `MineRadius = 100`. SC-4 corrects that lookup in `Ship.Equipment.cs`. The native drop formula, three-second deployment epoch, ammo consumption, and lifespan remain unchanged (`FedSrv/FedSrv.CPP:5715-5746,5778-5779`; `Igc/mineigc.cpp:111-114`). Real-core tests cover moving and stationary drops, lifetime, load deadline, and ammo decrement.

## Local research proof

The repeatable Live test runs Iron Coalition (25) against Ga'Taraan (3), SingleRing, research mode ×16, income multiplier 5000, 60 combat peers per side, ordinary research commanders/economy NPCs, home launches, staging, and contested sector 1. Tech gates remain enabled. The test config permits 256 loopback connections per IP because 120 combat clients plus controllers exceed the public-server default of 64. This is a local transport allowance. No 221/200v200 or rig run was performed.

Each run requires the original SC-1 minimums **on each side**: chaff 10, probes 10, mines 5. It also requires actual deployed mine models near an aleph in an owned-station sector and actual probe models in contested sectors on both sides. It reads the authoritative SC-1 counters without changing, resetting, or summing snapshots. The table contains individual successful snapshots, not final game-over totals.

| Run | Wall seconds | Side | Chaff ≥10 | Probes ≥10 | Mines ≥5 |
|---:|---:|---:|---:|---:|---:|
| 1 | 47.9 | 0 | 10 | 12 | 36 |
| 1 | 47.9 | 1 | 24 | 12 | 10 |
| 2 | 46.4 | 0 | 10 | 12 | 36 |
| 2 | 46.4 | 1 | 16 | 12 | 10 |
| 3 | 49.0 | 0 | 10 | 12 | 36 |
| 3 | 49.0 | 1 | 26 | 12 | 10 |

All three runs are separate exact-test invocations. [Showcase summaries and model traces](SC4_assets/showcases.json) preserve the measured clocks, counters, locations, launchers, and decoded lifetimes. [Bounded policy traces](SC4_assets/traces.txt) show incoming missile warnings, probe decisions, and defended-aleph mine decisions alongside legal loadout selections. The authoritative model observations corroborate the requests.

A supplementary 30-per-side run also passed at 198.4 wall seconds: side 0 **78/10/40**, side 1 **47/16/14** (chaff/probes/mines). Six initial scout probes per side made that smaller run depend on ordinary deaths and relaunches. The repeatable 60-per-side proof supplies twelve scouts and does not need scout deaths to reach ten probes. These fleet sizes differ from SC-1's 15-per-side run; the results establish action coverage, not a controlled combat-effectiveness comparison or 200v200 qualification. Mine deployment coverage does not establish that an enemy subsequently crossed or was damaged by a field.

## Validation

- Whole-solution build: **0 warnings / 0 errors**.
- Release/linux-x64/self-contained Native AOT publish: **success**, no IL2026/IL3050 or other warnings.
- **23 new/affected case identities, 3× alone each (69 individual-case invocations), all passed**, including all theory rows separately and the three Live showcase runs. Additional grouped development/qualification passes are retained but not added to that total.
- Never-parks: **base 5/5; changed 5/5**. Maximum stopped-with-target interval was **2.006 s base / 2.606 s changed**, below the unchanged 5-second assertion.
- **One fast-tier run**, 8,783 C# cases: **8,775 passed / 5 failed / 3 skipped**. All 9 Python infrastructure checks passed. There were no calibrated Fast timing offenders. The three skips require the optional private Meshy pack.

| Fast project | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 4832 | 5 | 0 |
| ClientCore | 1441 | 0 | 0 |
| MonoGame | 2483 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

[Validation ledger](SC4_assets/validation.json) records every qualification, exact theory selection, path-fixture comparison, parking sample, source hash, and full fast-tier summary.

The single fast-tier result is retained honestly with its five `PlayerBundlePathsTests` fixture failures. Those fixtures assume temporary directories have no repository ancestor. The sandbox disallows the brief's home scratch location, so the physical workspace `TMPDIR` made their explicit path searches find this checkout's `local.env`. Each failed case also fails **3/3 with unchanged base production binaries** in that same environment. Each passes **3/3 alone** when a workspace scratch directory is addressed through an open directory descriptor (`/proc/<runner-pid>/fd/<directory-fd>`), which removes the repository ancestor from the fixture's logical paths. All writes still land in the workspace. No path-resolution code, assertion, or fixture was changed, and the fast tier was not repeated. This is an environment/fixture limitation, not classified as a load flake.

The new showcase is tagged `Tier=Live`. Existing affected Live purchase/relaunch cases were also qualified three times individually. Temporary qualification selectors that matched multiple theory rows, the first development fixtures, and an interrupted large-showcase setup that hit the 64/IP default remain in raw evidence; they are excluded from the final qualification counts.

## Evidence and reproduction

Small review evidence is under [SC4_assets](SC4_assets/), below the 2 MB task limit. Full logs, TRX, setup diagnostics, and qualification scripts are retained under [TestResults/SC4](/home/domingo/allegiance-codex-sc4/TestResults/SC4/), which is ignored by Git. The sandbox returned `Read-only file system` for both `~/allegiance-scratch/SC4/` and `~/allegiance-evidence/SC4/`. Accordingly, scratch used the ignored workspace `.verify-sc4/` directory, and retained raw evidence uses the ignored workspace `TestResults/SC4/` directory. Nothing was placed in `/tmp`. Disposable scratch and comparison/publish binaries were removed at the end; the retained evidence directory is the documented sandbox exception to the requested external evidence location.

The authoritative build used `dotnet build AllegianceCore.slnx -m:8 -p:UseSharedCompilation=false -v:minimal`; the shared compiler process could not write inside this sandbox, so compilation ran in-process. AOT used the brief's Release/linux-x64/self-contained `PublishAot=true` command with the same compiler setting. The single tier invocation was `tools/run-tests.sh --tier fast --no-build --parallel 2 --results-dir <evidence>/fast`, with its lock, NuGet HTTP cache, and `TMPDIR` placed in workspace scratch.

To repeat the coverage test after building, load the workstation's `local.env`, use a disk-backed scratch `TMPDIR`, optionally set `SC4_PROOF_DIRECTORY`, and run:

```sh
dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj --no-build \
  --filter FullyQualifiedName=Allegiance.ClientCore.Tests.SC4ShowcaseLiveTests.Research_showcase_deploys_chaff_probes_and_defensive_mines_on_both_sides
```
