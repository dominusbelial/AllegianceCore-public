# P698 — retire stale sector ships and reset flight effects

Changes are left in the working tree; no commit or push was made. The requested
base is master `111b92be`. This checkout started at `3119bf5e`, its immediate
successor, which already contains the P697-related fix scoping projectile,
hit and missile relays to the shooter's sector. That fix is preserved.

## Findings and native evidence

All native citations below are relative to
`/home/domingo/Development/Allegiance/src/`.

| Native rule | File:line | Consequence here |
| --- | --- | --- |
| Docking and sector departure issue guaranteed `SHIP_DELETE` to the old sector's flying group | `FedSrv/fsship.cpp:196-226` | Existing `Server.BroadcastShipDeparted` already implements this audience and message; no new wire message is needed. |
| The client caches deleted remote ships by moving them into the null cluster | `clintlib/appmsg.cpp:456-489` | Retire render membership while preserving identity and strategic last-known position. |
| **Every** `SET_CLUSTER` removes all other ships from their clusters, including a same-sector re-seat | `clintlib/appmsg.cpp:1009-1023` | Reset every remote sector pose, rather than carrying old-sector objects into the new scene. |
| Detection loss marks the seeing side's ship status unknown; spotting restores flying state and current sector | `FedSrv/fsship.cpp:237-260`, `:287-291`; hide dispatch at `FedSrv/FedSrv.CPP:10855-10874` | Consume the existing hidden `SHIP_STATUS`; strategic information cannot establish a render pose. |
| In-cluster telemetry is supplied independently of scanner visibility; visibility is resolved by the native client's scanners | `FedSrv/FedSrv.CPP:4141-4165`; `WinTrek/trekigc.cpp:2060-2118`, `:2167-2169` | A HEAVY update must **not** clear the `Unknown` flag. Wait for spotted status. |
| Docked status identifies the station and sector for teammates and eligible viewers | `FedSrv/fsship.cpp:353-380` | Dock status retires a pod even if a delete was not observed. |
| Command tracking resolves the source ship before checking cluster and visibility | `WinTrek/WinTrek.cpp:7506-7513` | A boarded teammate has no separate rendered hull; the camera follows the visible carrier. |
| ThingSite visibility controls the ship geometry and visible bolt | `WinTrek/trekigc.cpp:1647-1658` | Apply one visibility/membership gate before gathering the hull and all attached emitters. |
| Particles, debris bits and explosions belong to a cluster's scene | `WinTrek/trekigc.cpp:23-40`, `:216-225` | The port's globally shared flight pools must reset at a cluster boundary. |
| The visibility sweep updates every ten passes | `Igc/clusterIGC.h:22`; `Igc/clusterIGC.cpp:348-354` | The existing server sweep supplies detection transitions; clients retire immediately on applying the status. |
| Damage smoke has a 0.35 s duration | `effect/thinggeo.cpp:744-751` | The source-removal test checks this boundary rather than an arbitrary long cleanup delay. |
| Exhaust smoke and hull debris bits have 2 s lifetimes | `effect/thinggeo.cpp:237`, `:860`, `:908`; `Igc/missileigc.cpp:54-55`; `effect/efgeo.cpp:764`, `:831` | Detached-particle checks finish at 2.1 s without retaining frozen smoke. |
| Particle motion/fade uses elapsed age and stops rendering after duration | `effect/particlegeo.cpp:51-58`, `:364-366`, `:376-400` | Detached puffs keep moving and fading when their source is gone. |
| HEAVY's ±1.5 seconds is a simulation timestamp acceptance window | `Igc/shipIGC.cpp:3107-3116` | This is **not** a wall-clock unseen-ship expiry. No arbitrary TTL was added. |

The server already announces warp/dock departures and the hidden status edge.
The client had three concrete gaps:

1. `SET_CLUSTER` left all remote ships and their old poses in the world, unlike
   native's explicit null-cluster sweep.
2. Identity and loadout refreshes cleared the old `IsGone` latch without a new
   sector pose. A retained `Latest` could therefore re-enter the gather. The
   gather also lacked its own sector, unknown-status and docking checks.
3. Persistent flight particle pools had no shared cluster reset. Old world
   coordinates could appear in another sector, or resume after a pause.

The supplied Juniper and Organia logs were read. Their projectile diagnostics
show cumulative stale/never-posed counts, consistent with the brief, but do not
identify a ship's server/client membership at the same instant. They are not
claimed as a measured before/after reproduction. The controlled loopback tests
below exercise the actual state and transport transitions.

## Implementation

`ShipEntity.PoseSectorId` records membership confirmed by in-sector HEAVY
telemetry or own-ship `SET_CLUSTER`, separately from the strategic last-known
`SectorId` and `Latest`. `CanDrawInSector` is the production hull/effect gather
gate: it requires a matching sector pose and sector, a pose, a free-flying ship,
and no death, departure, docking, parent or unknown-status exclusion.

Deletes, death, hidden status, dock status and status relocation retire that
membership. Every `SET_CLUSTER` retires all remote memberships. An identity,
loadout or cross-sector teammate `SINGLE_SHIP_UPDATE` can update its existing
information but cannot restore membership. A genuine new in-sector pose does.
HEAVY retains `Unknown`, because native can continue transmitting an undetected
ship. A spotted status restores detection; normal telemetry then restores the
rendered ship. A docked commander's explicit sector preview can establish a
same-side object's membership from a fresh teammate pose plus its flying status;
identity alone still cannot do so. Own-ship dock status does not set the remote
`IsGone` latch. No wall-time pose-age heuristic hides live ships under load.

`SectorGeneration` increments for every `SET_CLUSTER` and reconnect. Wire
projectiles, missiles, chaff, explosions, pending hits and positional effect
sound queues are discarded at the transition. Generation checking also prevents
a frame gathered before a received cluster transition from drawing afterward.

`FlightEffectPools` owns the persistent missile exhaust, warp sparks, damage
smoke, engine exhaust, motion ribbons, hull sparks/flashes, shield flares, local
shot echoes and aleph pulse state. The game resets them together on sector or
generation change, plus ambient debris, thrust history, pending bursts/hits,
ship transit history and the warp renderer's arrival swells. The shared smoke
renderer and billboard batcher have no persistent particles of their own.
All detached particles continue aging without an emitter; ribbons disappear
when their source is no longer gathered. Already emitted independent particles
fade according to their existing lifetimes; retirement does not emit new ones.

## Tests and verification

New ClientCore tests exercise departure followed by identity/loadout/teammate
pose refresh, docking and relocation status without a delete, hidden status
followed by continuing HEAVY telemetry, reacquisition, same/different-sector
`SET_CLUSTER`, and clearing buffered wire effects.

New MonoGame tests populate every persistent flight pool and verify sector and
same-sector generation resets. Source-removal checks prove damage smoke keeps
moving/fading, missing ribbons are pruned and damage smoke expires at 0.35 seconds and every detached particle is gone by
2.1 seconds (the longest pool lifetime is 2 seconds).

Three real TCP loopback cases use the real server, `ConnectionSession`,
`ClientWorld` and the production `CanDrawInSector` gather gate:

- An enemy is observed, actually crosses a warp and is killed in the destination.
- A detected enemy moves outside the scanner net and becomes `Unknown`.
- A teammate lifepod actually docks at a friendly station.

Each case requires initial render presence, disappearance and sustained exclusion
through subsequent updates, plus absence from the attached strobe gather.
Retirement is immediate when the authoritative event/status is applied. The
loopback wait permits the native ten-pass visibility sweep (0.5 simulation
seconds at 20 Hz) and transport scheduling; its 2-second wall-time ceiling is a
test scheduling allowance, not a new native timeout.

Final solution build: **passed, 0 warnings / 0 errors**.

The initial focused pass ran the new and directly affected tests **three times
in isolation** after a solution build: **70 methods, 210 invocations, 267 passing
test cases, zero failures or skips**. This includes all three real loopback cases in each
repetition, the existing warp/dock/relaunch/combat visibility cases (including
the P697 relay regression), and the commander receive cases.

The first full ClientCore pass exposed an existing spoofing fixture that
created a chaff decoy **before** entering its sector, then expected it to survive
`SET_CLUSTER`. It failed **3/3** exact-method isolated checks. The fixture now
enters the sector before creating its decoy; all retargeting assertions remain.
This preserves the required sector-effect reset. The strengthened reset regression also creates a missile before the reset and
proves that old chaff cannot retarget a newly created missile afterward.

The first MonoGame pass caught camera follow rejecting a retired boarded hull
before resolving its carrier. That test also failed **3/3** alone. Camera follow
now resolves the carrier and checks the carrier's visibility, matching native
`WinTrek/WinTrek.cpp:7506-7513`; an unparented gone ship is still rejected by the
final visibility check. Core does not reference the game assembly and its passing
full result remains applicable. Both affected suites are rerun after rebuilding.
After the final rebuild (again **0 warnings / 0 errors**), the revised
sector-effects test, corrected spoof fixture, opted-in local 15v15 showcase,
and camera-follow test each passed **3/3 isolated runs**. Together with the
initial focused checks, this is **222 successful isolated invocations / 279
passing cases**, covering **73 distinct methods**, each at least three times.
The Core test assembly and Core/Server/Bot/Lobby product assemblies were
SHA-256 identical after this rebuild to the binaries in the passing Core run
(`/tmp/p698-core-binary-check.log`).

Completed/full-suite verification (assemblies run sequentially):

| Suite | Passed | Failed | Skipped | Result |
| --- | ---: | ---: | ---: | --- |
| Core | 4,558 | 0 | 0 | Passed |
| ClientCore | 1,021 | 0 | 0 | Passed, local 15v15 showcase enabled |
| ClientCore.MonoGame | 2,202 | 0 | 3 | Passed |

The three skips are the existing optional Meshy content-pack checks; the pack
is absent. No new or affected test was skipped. The local 15v15 showcase passed
three isolated repetitions and the full ClientCore pass with
`ALLEGIANCE_LIVE_P660_SHOWCASE=1`.

The discovery passes were ClientCore **1,019 pass / 1 fail / 1 opt-in skip** and
MonoGame **2,201 pass / 1 fail / 3 optional-pack skips**. Both failures were
reproducible 3/3 and corrected as described above. Both affected full suites
then passed on the rebuilt tree. No failure was waived as a load flake, and no
assertion, timeout or skip condition was relaxed. Final TRX records are
`/tmp/p698-test-results/p698-Core.trx`, `p698-ClientCore-final.trx` and
`p698-MonoGame-final.trx`.

Reproduction uses the workstation's existing `local.env` path overrides and a
writable NuGet HTTP cache:

```bash
set -a
. ./local.env
set +a
export NUGET_HTTP_CACHE_PATH=/tmp/p698-nuget-cache
dotnet build -m:8 -v:q
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~P698'
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --filter 'FullyQualifiedName~P698'
dotnet test tests/Allegiance.Core.Tests --no-build --nologo -v:m
ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build --nologo -v:m
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --nologo -v:m
```

The isolated runner and per-invocation logs are under `/tmp/p698-*`.
No rig/221/200v200 run was performed; that lane remains with Claude as requested.
No GPU/video claim is made: render membership, emission inputs and persistent
particle state are verified headlessly with the real loopback transport.
