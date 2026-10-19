# P730 — bot efficiency

Working-tree implementation based on `939b163a`. No commit, push, deployment,
221, or 200v200 run. Native training/drone AI remains a separate track.

## Implementation

Each bot manager owns a separate `SharedBotWorlds` pool for each side. Combat
and research-commander sessions in that pool share decoded ship/map tables and a single receive/read
gate. Static clusters, stations, warps, asteroids, live station/rock conditions,
probes, mines and construction effects have one backing store per side and
mission in the worker. The separate startup/tool observer remains one fixed
control peer per worker. Identical immutable wire records reuse a bounded,
exact-byte-checked decode cache (2,048 entries). Framing/CRC validation remains
per connection, and recipient-specific heavy packets still require decoding.
Older cross-connection poses/status cannot replace newer authoritative samples.

Each pilot has a private own-ship overlay, including status, pose cookie, hull
and inventory. Wallets, orders, acknowledgements, hit/event cursors and
sector-relative transient effects remain private. A teammate's EXPORT or
telemetry cannot overwrite the owner's loadout. Changing sectors preserves
observations held by another pilot; late old-sector departure messages cannot
retire a ship already observed in its new sector. Rotation retires the shared
mission once; subsequent peers detach without clearing the new mission. Late
joins and reconnects attach to the current shared mission. Sharing begins only
after the wire confirms the expected side; a peer auto-seated elsewhere retains
an independent view, preventing cross-side knowledge leaks. Its already-received
own status and map records survive attachment to the correct pool.

When decision snapshots are enabled, their immutable ship/station/warp arrays
are built once per side and reused by small per-pilot identity wrappers. The
existing default **snapshot off** is retained, including on the rig; P730 does
not reverse the earlier rig decision about that switch.

Single-player worker selection uses both available cores and fleet size:
reserve two logical CPUs where possible, use at most four workers and at most
one worker per 64 pilots, then divide the remaining compute budget among them.
On this 24-logical-core host Small/Medium/Large use one worker, Epic two;
the prior host always used four. Bot preparation is capped at two threads per
worker (one on a one-core budget), matching the two shared side gates. The
runtime retains the full per-worker processor budget for networking and other
work. In a separate three-run Large headless experiment, this reduced median
OS CPU from 4.684 to 4.360 ms/bot/s, about 7%; the final rendered captures use
the two-thread cap. Dedicated worker CLI selection is unchanged.

Single-player enables AI LOD. A bot outside every known human/focus sector runs
its AI at 2 Hz; bots in any human/focus sector retain the existing 20 Hz cadence.
Lifecycle refresh, keepalive, ready-up and worker coordination continue to run.
Retained targets count toward engagement caps while their bot skips a decision.
Research-controller identities are excluded from human detection. The local host supplies the flying player's or spectator/benchmark camera's
sector through its owned protocol on the two-second health cycle. Ordinary
human ship observations also supply focus sectors. Focus older than six seconds conservatively keeps
the full rate. No distance approximation across unrelated sector coordinates is
used: the entire human sector gets the full rate.

Configuration:

- `ALLEGIANCE_BOT_LOD=off`: disable LOD, including in single player.
- `ALLEGIANCE_BOT_LOD=on`: enable it explicitly in an external/rig worker. With
  no human/focus sector, all bots use the distant cadence. External workers
  default to LOD off.
- `ALLEGIANCE_BOT_DISTANT_HZ=0.5..20`: distant cadence; default 2 Hz.
- `ALLEGIANCE_BOT_SHARED_WORLD=off`: independent worlds for comparison.
- `ALLEGIANCE_BOT_DECISION_SNAPSHOT=on`: existing opt-in snapshot path.

The bot-manager view includes cumulative `AiDecisions`; shared pools expose
decode/reuse counters for verification.

## Correctness evidence

The bundled real P729 battle was replayed through independent and shared
sessions. After **31,450 recorded deliveries**, the production target selector
and steering helper produced identical results, including **10,305 combat
choices**. This establishes representation equivalence for those recorded
inputs; live battles are asynchronous and are not claimed to be deterministic.
The separate multi-view tests cover private state, shared remote entities,
side/mission isolation, repeated cached states, reconnect/late join, sector
changes and immutable shared snapshot arrays.

A live cadence check observed **4 distant decisions versus 42 focused-sector
decisions in equal 2.1-second windows**, with no simulation-stage failures.

The local 15v15 two-match check observes natural gunfire, damage, mining and
research progress in each match. It deliberately causes one pilot death, waits
for that controller to observe the pod, invokes station rescue, and verifies an
ordinary purchase and armed relaunch. It then forces game over to qualify
migration. Death/rescue/end triggers are controlled; a natural conquest victory
is not claimed by this check. The complete suite also exercises the existing
finale paths.

## Benchmark and gate results

The original rendered Large command failed because its spectator default spawned
60 bots while the benchmark adds a flying observer and expects 59. Benchmark
options now explicitly reserve the pilot seat. The baseline Large retry uses
only that orchestration correction, built from the base source; its driver and
world/AI code remain unmodified. The same correction applies to the after build.
Small/Medium already reserved the pilot through their defaults. Their final
after captures are repeated after the commander/side-safety corrections; earlier
captures remain in the raw directory and are excluded from the delivered table.
The final sequence also restarts Small captures that ended during a temporary-file
quota interruption before their result JSON was saved; their aborted logs are retained and
excluded. Final stdout is retained compressed. Disposable baseline build sources were removed to recover space; the
baseline binaries, build log and exact orchestration patch remain available.

Validation and before/after measurements are in progress; final results will be
filled from the retained machine-readable evidence before delivery.

## Reproduction

Build with `dotnet build AllegianceCore.slnx --no-restore`. The P730 focused
classes are `P730BotEfficiencyTests` and `P730ShowcaseLiveTests` in
`tests/Allegiance.ClientCore.Tests`; the retained validation ledger names every
additional affected method and repetition. Live P660 coverage uses
`ALLEGIANCE_LIVE_P660_SHOWCASE=1`. Artwork and the bundled P729 battle are present
on this workstation; the recorded test requires artwork in a clean environment.

For each preset, run the built MonoGame executable with
`--benchmark skirmish:small --benchmark-seconds 30 --benchmark-exit
--benchmark-output /tmp/small.json`, substituting `medium` and `large`.
The P729 command performs its own three measured repetitions, plus warm-up and
auto-tune verification. Compare compatible results with
`python3 packaging/bench-compare.py before.json after.json`. The original
baseline binaries and the base-source Large seat correction are retained in the
raw evidence directory; no baseline working-tree checkout was used.

## Evidence and native basis

Raw logs, TRX, temporary binaries and original benchmark JSON are under
`/tmp/allegiance-evidence/P730/`. The requested `~/allegiance-evidence/P730/`
is outside this sandbox's writable roots. Only compact summaries/JSON are kept
under `docs/reports/P730_assets/`, with a task limit of 2 MB.

Pooling, decode caching, LOD, core/fleet budgets and focus delivery are port
orchestration policies. Existing gameplay rules are reused. Native citations
are relative to `~/Development/Allegiance/src/`:

| Reused rule | Native file:line |
| --- | --- |
| Visibility is queried by side | `Igc/modelIGC.h:120-125` |
| Ordinary client sector entry retires remote sector objects; the pooled bot view preserves another pilot's observations | `clintlib/appmsg.cpp:1009-1023` |
| Own buy acknowledgement installs the player's loadout | `clintlib/appmsg.cpp:3176-3183` |
| Wallet state belongs to the addressed player's private data | `clintlib/appmsg.cpp:1367-1378` |
| Queued and accepted order slots are distinct | `WinTrek/WinTrek.cpp:10659-10696` |
| Rescue chooses a friendly station and docks the survivor | `FedSrv/FedSrv.CPP:13065-13076` |
| Bot steering/fire gates continue using the existing wingman policy | `Igc/shipIGC.cpp:2470-2600` |
| Restart returns through the normal countdown | `FedSrv/fsmission.cpp:3539-3546` |
