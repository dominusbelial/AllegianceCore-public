# P697 — source poses before combat effects

Changes are left in the working tree; no commits or pushes. Starting HEAD was
`b43ebbfb` (includes `111b92be`, the sector relay fix `3119bf5e`, and P695).
No rig/221/200v200 session was run.

## Native findings

All native references below are to `/home/domingo/Development/Allegiance/src/`
(native checkout `052f5179`).

| Question | Native evidence | Finding |
| --- | --- | --- |
| Missile launch audience | `FedSrv/FedSrv.CPP:5504-5505`; sector group definition `:2004-2007` | `FIRE_MISSILE` is reliably forwarded to the flying sector group, across sides; no shooter detection gate. |
| Chaff/mine/probe deployment audience | `FedSrv/FedSrv.CPP:5636-5644`, `:5652-5671`, `:5724-5781` | The fire-expendable/chaff batch is flushed to that same sector group. |
| Gunfire | `Igc/weaponIGC.cpp:142-179`, `:290-303`; `Inc/Messages.h:131-135` | Weapons create projectiles in their local cluster simulation. The CS projectile message declaration is not evidence of a server broadcast audience; native does not use the port's thin-client shot relay. |
| Ship pose audience | `FedSrv/FedSrv.CPP:3751-3814`, `:4141-4169` | Sector candidates are selected by update recency/parent state and the heavy/light budget, without a per-recipient `SeenBySide` gate. |
| Pose content / sparse updates | `Igc/igc.h:2423-2442`; `FedSrv/FedSrv.CPP:3836-3846`, `:3918-3950`, `:4179-4196` | LIGHT has no pose; ranked HEAVY is budgeted and replaceable. Native clients also have a local simulation. |
| Firing reveal | `Igc/weaponIGC.h:103-125` | Activating a weapon adds its signature; deactivation removes it. It does **not** unconditionally spot the ship for every side. |
| Other signature contributions and detection | `Igc/magazineigc.h:70-80`, `Igc/dispenserigc.h:70-81`, `Igc/shipIGC.h:268-270`, `:341-366` | Mounted launchers contribute signature; cloak scales it; detection still depends on scanner range, geometry and line of sight. |
| Native client visibility | `WinTrek/trekigc.cpp:2100-2131`, `:2166-2169`, `:2216-2236` | The client performs its own scanner/side visibility work. Network ship knowledge and side detection are separate concepts. |
| Armed probe source | `Igc/probeigc.cpp:491-505` | The round originates at the stationary probe. Native assigns the deploying ship as launcher for credit when available, falling back to the probe. Spatial emitter and credited ship can differ. |
| Independently discovered hazards | `Igc/mineigc.cpp:68-86`, `:103-104`, `Igc/probeigc.cpp:114-120`; `FedSrv/FedSrv.CPP:10754-10784` | Mines/probes are predictable side-visible objects. Discovering such an object exports the **object** to the detecting side, independently of the deployer's current visibility or continued existence. |

The brief's proposed alternatives assume native either detection-gates fire or
forces a global spotted-on-fire reveal. It does neither. The faithful network
rule is sector fire **alongside sector ship knowledge**. This fix therefore
orders a source pose before the port's relayed effects without changing the
simulation's detection flags or inventing a global reveal.

The port combines a design adaptation (server-simulated shots for a renderer)
and an era difference (native's sparse updates plus a local client simulation).
The actionable bug was that P369's identity/pose/fire audience includes ordinary
non-cloaked sector ships, while P659's legacy floor excluded enemies outside
`SeenBySide`. Shared deviation ranking could keep those ships unposed/stale
while relaying their fire. Effects also occur during the sim/inbound phase,
before that tick's normal identity/pose suffix, permitting first-shot races.
An identity export alone contains no position.

## Implementation

[Server.FireAudience.cs](../../src/Allegiance.Server/Server.FireAudience.cs) centralizes the source predicate and reliable pose
dependency. The ranked pose builder, tier builder and P659 floor now agree:
ordinary sector ships qualify; cloak-hidden enemies do not; own-side ships and
admin viewers retain their existing all-ships exception. Effects and missile
tracking follow an authenticated spectator's viewed sector, matching its pose
stream; they do not follow the parked player ship into a different sector. The default floor remains
2 Hz and the zero-floor option remains available.

A first relayed effect orders the source's EXPORT, sector SHIP_STATUS and
SINGLE_SHIP_UPDATE before the effect on the reliable FIFO. Active emitters get
a reliable pose refresh at most every 0.5 seconds. These source poses cannot be
replaced by coalesced HEAVY/LIGHT packets. This also protects the zero-floor
configuration and the immutable snapshot/publisher path. Source identity and
sector are established before pose/effect processing, including unexported NPCs.
No `SeenBySide` bit is forced by this mechanism.

Covered paths:

- Projectile creation and projectile hull/shield impacts share one sector/source
  audience. Same-side NPC fire no longer depends on a connected-player export
  latch. An impact after a launcher's death may use existing source history,
  without sending a pose that revives the dead launcher.
- Missile launch and late-viewer missile tracking establish the same source
  dependency. Tracking is per recipient's sector. A recorded missile audience
  continues tracking and receives destruction even after its launcher warps,
  cloaks or dies; unknown
  viewers do not receive an unexplained expiry explosion.
- Direct mine/probe creation supplies the in-sector viewer with the emitter's
  identity/pose, including NPC layer drones. Existing remote own-side persistent
  object knowledge is retained. Mine/probe destruction uses each object's owning-side
  audience, including NPC emitters, independently of the deployer's later state/side.
  Persistent object creation, discovery and cleanup also retain the owning
  mission boundary when different games reuse side/sector IDs.
- A cross-side mine discovery supplies the emitter pose when that live emitter
  qualifies in this sector. A discovered persistent mine can legitimately outlive
  its deployer or be detected while the deployer is hidden; native independently
  exports that hazard. Such discovery is not a new launch/reveal of its source.
- Armed probe rounds use the probe as their spatial source, while retaining the
  deploying ship for damage credit. A late viewer receives the static probe
  export before its rounds; a departed/dead deployer is not reintroduced. The
  projectile codec retains ordinary ship packets at 54 bytes and appends an
  object-type trailer for probe sources (56 bytes), distinguishing overlapping
  ship/probe IDs and preventing own-shot echo suppression of a probe round.
  Probe packets require the matching updated client. Client diagnostics retain
  their existing counters/format and check the actual probe pose.
- Chaff's complete fire/create/spoof batch uses the source gate and ordered pose.
  Mine/probe fire animations are sector-local. Their persistent object exports
  retain their established audiences.

Viewer history resets on viewer ship/side/sector changes, retirement and server
shutdown. Source records distinguish object identity from numeric ship ID and
are pruned on departure, docking, boarding, death and visibility loss.

The extra reliable refresh costs about 120 payload bytes/s per continuously
active viewer/source pair (56-byte SINGLE plus 4-byte packet CRC, at 2 Hz), plus
one initial identity/status bootstrap. The newly covered floor population adds
39 bytes per additional HEAVY entry at the existing floor rate. This is an
intentional delivery dependency cost; production density and throughput still
need Claude's rig measurement.

## Metrics for the rig rerun

Existing client `[proj-diag]` counters are unchanged. With the existing profiler
enabled, each server profiler window now also emits:

```
[fire-audience] sent=... neverposed=... initialPoses=... refreshPoses=... hidden=...
```

`sent` counts viewer/event deliveries of shots, impacts, missile launches, direct
in-sector mine/probe exports and expendable batches. `neverposed` counts those
sent without prior reliable source-pose history; the ordered repair happens
before that check. It should remain zero. `initialPoses` and `refreshPoses` count
source dependency work; they may also include missile tracking or independent
mine discovery. `hidden` counts visibility/scope rejections at the shared source gate, not shots
excluded before audience enumeration. Window counters reset at report time;
pose history persists. Armed probe deliveries also count; a prior static probe
export is their source pose. This measures server enqueue order, not TCP acknowledgments
or actual mesh rendering. Existing `[update-age]`, `[egress]` and client diagnostics
remain necessary to assess delivery stalls and rendering on the rig.

## Verification

The recorded transcript's four golden prefixes change intentionally: reliable
source dependencies and per-viewer missile tracking change the packet tape.
Full per-peer bytes, lane/order comparisons across direct, snapshot and
encode-once paths remain asserted; the prefixes were stable in all three
pre-update runs.

Three older direct-factory ordnance fixtures assumed that fresh default seats
were flying. They now send the real undock request and assert that the source
is flying/alive before emission. The missile expiry fixture also aims along the
real outward launch kick, so its flight clears the station hull. Their
creation, destruction, type, source,
position and audience assertions are retained. Native fresh-seat docking is at
`FedSrv/fsmission.cpp:791-796`; the native fire handler requires a cluster at
`FedSrv/FedSrv.CPP:5430-5435`, `:5634-5635`.

A warp-bomb live fixture manually inserted a missile without invoking its real
creation notification. With destruction now scoped to viewers who know the
missile, this failed three isolated runs. The fixture now invokes
`MissileCreatedEvent` before the missile reaches the warp; the launch, destruction,
warp notice and delayed damage assertions remain.

The opt-in P542 live proof also failed three isolated enabled runs: its first-hull
selection did not require a usable fixed gun, and no rounds were simulated.
The fixture now chooses a hull with energy/ammo and a fixed hardpoint and
asserts that its shooter is known and flying. The enabled live proof passed
three repetitions, retaining the received/drawn-enemy and zero-dropped-enemy
assertions. This was a fixture correction, not a load-flake classification.

The crowded loopback uses 65 sector ships: 32 idle enemies, 32 fresh weapon-firing
enemies and one viewer. Actual weapon simulation produces the shots before the
normal first export sweep. Shooters remain outside the viewer's detection; the
client receives their actual hull/sector/pose without altering detection flags.
The unchanged HEAD implementation reproduced **232 neverposed / 640 enemy rounds**.
Each fixed run receives **640 / 640 with neverposed=0 and stale=0**. Four modes
cover native and port update models, disabled floor, and native interest tiers
with snapshot/encode-once/parallel publishing. Each is repeated three times.
See [baseline](P697_assets/baseline.txt), [baseline test](P697_assets/baseline_test.cs)
and [fixed loopback metrics](P697_assets/loopback.txt).

The final build passed with **0 warnings / 0 errors**; the subsequent ClientCore
fixture build also passed with 0 warnings/errors. All **147 exact methods** in
the focused/reclassification matrix have three most-recent isolated passing
invocations (**441 invocations / 567 case executions**). Both
`ALLEGIANCE_LIVE_P542_PROOF=1` and `ALLEGIANCE_LIVE_P660_SHOWCASE=1` were enabled
for the final live checks and suites. Every new P697 case ran three times.

| Final unfiltered suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4555 | 3 | 0 |
| ClientCore | 1031 | 0 | 0 |
| ClientCore.MonoGame | 2199 | 0 | 3 |
| Lobby | 19 | 0 | 0 |

The three Core failures were `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`
(no same-side observer during setup), `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`
(empty owner receipt), and `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`
(empty bystander receipt). Each passed **3/3 exact-method isolated reruns**;
these match already documented failure modes and meet the brief's load-flake
rule. The MonoGame skips are the three existing optional private Meshy-pack
asset checks. No rig/221/200v200 run was attempted.

An earlier full pass reported Core **4557/1**, ClientCore **1027/1**,
MonoGame **2199/0/3 skips**, Lobby **19/0**. The Core failure was the manually
inserted warp-missile fixture corrected above. The ClientCore failure was
`P682NextMatchCombatLiveTests.Mcp_bots_buy_launch_see_enemies_and_kill_in_both_matches`
(native model, pipeline disabled; match-2 credited-kill timeout). After the
final clean ClientCore suite, that method passed **all four cases ×3** alone.
Earlier focused single failures in cross-side mine discovery, warp-return
combat visibility and same-side export likewise passed three subsequent
isolated repetitions. See [the flake ledger](../TEST_FLAKES.md).

[Invocation ledger](P697_assets/results.json) retains the failed iterations,
initial suite results and reruns; [development iterations](P697_assets/development_results.json)
retain the original golden/fixture failures. The four golden transcripts were
updated only after reproducing their new hashes three times. The log and
TRX directories for the final isolated Core/client failure checks are
`/tmp/p697-post-core/` and `/tmp/p697-post-client/`. See also
[build output](P697_assets/build.txt), [ClientCore fixture build](P697_assets/client_build.txt)
and [the reproducible driver](P697_assets/validate.py).


To reproduce locally with the existing artwork configuration:

```bash
set -a
source local.env
set +a
NUGET_HTTP_CACHE_PATH=/tmp/p697-nuget-http dotnet build -m:8 -v:q -p:UseSharedCompilation=false -nodeReuse:false
ALLEGIANCE_LIVE_P542_PROOF=1 ALLEGIANCE_LIVE_P660_SHOWCASE=1 P697_MODE=focused python3 docs/reports/P697_assets/validate.py
ALLEGIANCE_LIVE_P542_PROOF=1 ALLEGIANCE_LIVE_P660_SHOWCASE=1 P697_MODE=full python3 docs/reports/P697_assets/validate.py
```

The focused driver starts a separate `dotnet test --no-build --filter
FullyQualifiedName=...` invocation for each method/repetition, including every
inline theory case, and writes TRX files and an append-only result ledger.
The full mode runs unfiltered Core, ClientCore, MonoGame and Lobby suites.
Build/test logs and TRX files from this work remain under `/tmp/p697-final/`
(with the build logs under `/tmp/p697-*-build.log`).
