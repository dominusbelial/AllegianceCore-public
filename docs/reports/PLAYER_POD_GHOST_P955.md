# P955 — player lifepod sector lifetime

The ghost pod is caused by collision cleanup terminating a ship that the server
has already turned into a live lifepod. Four lifetime checks in the three physical
collision handlers now preserve that survivor. No transport, oxygen, telemetry,
autopilot or client production code changes are required. The existing oxygen
display wire fixture also reads native heavy-update fractions now.

Base: `ec070cea4826ab8371619301c51362df7e9a8525`, branch `codex/p955`.
Changes remain in the working tree; no commits or pushes. An unexpected remote
discovery probe from an existing full-suite test is documented below.
The report was created before implementation/gating and updated with evidence.

## Reproduction

`P955PlayerPodLifetimeTests` uses a real loopback player connection, first TCP and
then direct UDP against the server's `DualTransport` listener (no TCP fallback).
It creates a player-class Fighter hull 121, with no Bot controller or autopilot,
and rams an enemy station through normal sector collision detection. A weakened
hull makes the ram deterministically lethal. Sphere contacts isolate lifetime
from CVH separation; actual artwork landing bays remain enabled. This uses side
0's civilization/pod; the supplied incident used side 1/pod 113.

The final ten test cases were also run against the unchanged base Core assembly:
**Passed 3 / Failed 7 / Skipped 0**. The two human weapon-hit controls and the
software-bot weapon control pass; all seven collision-survivor cases fail.
The same ten cases pass with the fix: **Passed 10 / Failed 0 / Skipped 0**.
See [base run](../../.scratch/p955/final-tests-on-base.log) and
[fixed run](../../.scratch/p955/regressions.log).

Immediately after the lethal station contact, both TCP and UDP reproduce:

| `PodTripTelemetry.Sample` removal predicate | Base result |
|---|---|
| `ship.Dead` | false — full-health pod |
| `ship.Sector is null` | false — original sector |
| `!ship.Sector.Models.Contains(ship)` | **true — the failing predicate** |
| ship's mission absent from live missions | false — same live mission |

Additionally, `Mission.GetShip(id)` returns null: termination also unregisters the
ship. Thus the original host's `removed` outcome was accurate. Accepting control
messages against the peer-owned object cannot restore sector updates, oxygen or
docking. The supplied excerpt's unchanged ejection position one tick later is
consistent with this loss of simulation membership.

## Root cause and fix

`Ship.ReceiveDamage` calls the site's `KillShipEvent` synchronously and then
returns `DamageResult.Killed` (`src/Allegiance.Core/Sim/Ship.cs:5446-5448`). The
server handles a player ejection by changing that same ship's hull, restoring its
fraction, arming its oxygen timer, and sending EJECT. On return, the station
collision handler used the damage result alone to call `Terminate(ship)`.
`Sector.Terminate` raises the termination notification, unregisters the model
and removes it from `Models` (`src/Allegiance.Core/Model/Sector.cs:577-582`), while
the peer and ship sector/mission references remain.

The weapon collision path already uses the post-callback `ship.Dead` state
(`Sector.cs:2501`). The fix applies the same lifetime guard to station contact
(`Sector.cs:3582`), asteroid contact (`Sector.cs:3077`) and both sides of ship
contact (`Sector.cs:3355-3356`). Killed results still suppress bounce correction,
so it cannot overwrite the eject pose/velocity. Genuinely dead ships retain
normal termination. The change also avoids unregistering a live docked restart
if a true-death callback already completed it.

Software bots are player-class network peers and enter the same server ejection
branch. The Bot comparison exercises both a weapon death and an enemy-station
ram; the latter reproduces the same defect on base. Healthy bot pods in the
incident therefore do not establish a separate working collision path: weapon
kills already preserve them. Native NPC drones are distinct from these peers.

## Native comparison

All following line numbers were re-grepped in the read-only native source tree.

| Rule | Native source |
|---|---|
| Damage returns `c_drKilled` after calling the site | `Igc/shipIGC.cpp:1333-1334` |
| Non-killed player ejection swaps the same ship into its side's pod, retains its cluster, sends EJECT and a new cookie | `FedSrv/FedSrv.CPP:11918-12021`, especially `11942`, `12011-12021` |
| Hull assignment restores full fraction | `Igc/shipIGC.cpp:1435` |
| Static collision skips correction after a killed result, without terminating the ejected survivor | `Igc/shipIGC.cpp:995-1001` (station fall-through at `868-869`); allied special bounce also at `825-831` |
| Ship contact delegates lethal damage to the site rather than terminating the survivor | `Igc/shipIGC.cpp:1172-1201` |
| Pod oxygen deadline is armed on hull change and tested with strict `now > deadline` | `Igc/shipIGC.cpp:1407-1408`, `361-364` |
| An existing pod dies rather than ejecting again; true death removes its cluster, selects a friendly restart station and sends ENTER_LIFEPOD | `FedSrv/FedSrv.CPP:11779`, `12025-12065` |
| Friendly/allied, capable ships dock inside a garage; enemy boarding requires the board capability; pod rescue also requires friendship | `Igc/shipIGC.cpp:727-740`, `843-867` |

The regression tests verify enemy refusal for an ordinary pod; they do not change
native boarding or allied landing restrictions. Parity evidence is attached to
existing `SIM-0200` and `SIM-0647` in `docs/parity/sim-rules-matrix.csv`.

## Client and wire proof

To run the player regressions after building:

```sh
set -a
. ./local.env
set +a
~/.dotnet/dotnet test tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj --no-build --filter FullyQualifiedName~P955PlayerPodLifetimeTests
```

The human-player test decodes the real socket traffic into the production shared
`ClientWorld`. Ejection leaves the entity alive/present with the pod hull and
EJECT cookie. A subsequent SHIP_UPDATE using that cookie changes the server's
controls, proving that the new survivor remains controllable.

The test uses the actual **300-second** core endurance, without an override. It
advances ordinary server ticks in clear, in-bounds space, verifies continued ship
updates and no premature docking, then receives true-death KILL_SHIP,
SHIP_DELETE/SDR_KILLED and ENTER_LIFEPOD. The server restarts the player alive at a
friendly station and the client receives its docked station/state. Both TCP and
UDP execute this flow. The clock is simulated headlessly; it does not wait five
wall-clock minutes.

A second TCP/UDP test places the ejected pod inside an enemy's actual landing
bay and verifies that it remains undocked. It then places that same ship in a
friendly station bay and verifies automatic simulation docking and the client's
docked state. Placement isolates docking from navigation, which is out of scope.
The docked oxygen fraction is full/inactive. No HUD behavior or renderer changed.

## Validation

`local.env` is present and loaded for artwork/native corpus tests. The targeted
gate covers 76 methods across the three client/core test projects, each qualified
with three isolated invocations, including every theory row (297 case executions).
Exploratory fixture setup failures are not final gate failures. The broad Fast
gate and full showcase checks retain the existing failures described below.

| Check | Passed | Failed | Skipped | Result |
|---|---:|---:|---:|---|
| Final regressions, initial fixed run | 10 | 0 | 0 | pass |
| New regression cases, each method ×3 alone | 30 | 0 | 0 | pass |
| Existing affected methods, ×3 alone | 267 | 0 | 0 | pass |
| Full fast tier, once | 13,422 | 10 | 3 | non-green; qualified below and in flake ledger |
| Showcase never-parks, base ×5 | 4 | 1 | 0 | arrival-count failure; no parking failure |
| Showcase never-parks, fixed ×5 | 4 | 1 | 0 | arrival-count failure; no parking failure |

Solution build: baseline **0 warnings / 0 errors**. Final solution build **0 warnings / 0 errors**.
`tools/build-tools.sh --drop-bins` **passed all 119 projects**, each with
0 warnings / 0 errors. The first attempt hit read-only global NuGet HTTP cache
audit-refresh errors; the successful rerun used command-local `NuGetAudit=false`
(no project or repository policy change). Linux x64 self-contained server
**AOT publish passed**, with no IL2026/IL3050 diagnostics. Glossary-link and parity
scorecard validation passed. Added-line/untracked-file hygiene and whitespace
checks passed; CSV retains LF endings. The publish guard reports no new commits;
working-tree content was scanned separately.

The existing `LiveLifepodEnduranceDisplayTests` initially passed 1/3 alone and
failed 2/3 with "same-side watcher never received ... relayed update". Its
recorder only decoded SINGLE_SHIP_UPDATE, while the native broadcast default
relays remote ships through HEAVY_SHIPS_UPDATE. The fixture now records those
per-ship fuel fractions as well, preserving the self-only oxygen assertion and
the native production settings. The corrected fixture passed its initial check;
its final three-run qualification **passed 3/3 alone (0 failed / 0 skipped)**. The initial
corrected check recorded 90 same-side watcher relays at full fuel while the
owner received 91 readings spanning 1.0 down to 0.1.

### Broader-gate failure qualification

All 12 failed or slow methods were run three times alone, including theory
companions: **30 passed / 18 failed / 0 skipped**. The five known bundle-path
cases fail with mandatory in-worktree `TMPDIR` and ancestor `local.env`; each
also fails on base (whole class: 4 passed / 5 failed per run, three runs). The
NPC defender charge-target case fails 3/3 on both fixed and unchanged base Core.
These existing failures remain open. The join-state, lead-cost and other timing
cases pass 3/3 alone. The warp-exit measurement body passes 3/3, but its middle
10.309 s isolation remains above the calibrated 10.288 s Fast limit.

The nine Python infrastructure checks pass. The three skips are optional private
Meshy-pack tests. Detailed per-method outcomes and durations are recorded in
`docs/TEST_FLAKES.md`; the full Fast suite was run once.

### Local-only exception from the existing discovery test

The mandated fast run executed
`DiscoveryTests.Coordinator_is_silent_when_tailscale_absent`. Its injected
`TailscaleStatus = () => null` fell back to the installed CLI in
`DiscoveryCoordinator.cs:92`, then probed a real Tailscale peer. This was an
unintended breach of the local-only instruction. The test fixture now supplies
`string.Empty`, simulating absent status without invoking the installed CLI or
probing remote peers. Production discovery is unchanged. The corrected fixture
**passed 3/3 alone (0 failed / 0 skipped)**; no peer address or hostname is included in the portable report evidence.

Raw local logs/TRX and the repeat runner are retained under the gitignored
[`.scratch/p955/`](../../.scratch/p955/) directory because this lane's writable
sandbox excludes the external evidence directory. Portable counts, individual
qualification outcomes, assembly hashes and showcase measurements are in
[validation.json](P955_assets/validation.json). No new media, service, application
environment variable or port configuration is introduced.

### Showcase comparison

The full P700 test passed **4/5 on base and 4/5 with the fix**. Its parking
assertion passed **5/5 in both batches**. Base run 2 reached 23/30 ships and fixed
run 5 reached 21/30, below the required 24; both ran through the 480-second
window. The fixed failure's longest speed-below-one interval with a target was
1.714 s (base failure: 3.511 s). These are the same arrival-count failure family,
with no observed parking regression. All runs used the existing loopback P700
fixture with native/pool/parallel production defaults, in the xUnit host.
Shared-host load varied, limiting conclusions about rates from five runs.

## Native intent

Ejection changes the ship's hull without ending its in-sector lifetime. The pod
keeps receiving normal simulation updates, including oxygen death and friendly
station docking.

## Modern approach

Use post-callback liveness to decide model removal, matching the existing weapon
path. Real TCP/UDP player tests check sector and mission registration, the
300-second timer, wire death/restart and client state; bot and sibling collision
controls keep the repair grounded in the shared lifetime contract.
