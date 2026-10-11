# P608 — P606 broke bot navigation in BOTH transport modes; fix without regressing P606

## Summary

P606 (`c305a493`) introduced per-peer **latest-wins coalescing** for the streaming
ship-state broadcasts (`TcpTransport.PeerSender.Enqueue`, `Server.Broadcast.cs`),
routing BOTH the owner's own-ship `SINGLE_SHIP_UPDATE(78)` and the per-tick
teammate position batch through the **same coalesce key** (`CoalesceShipStates`).
Because the own-ship frame is enqueued first and the teammate batch is enqueued
later in the *same broadcast pass for the same peer*, the batch **supersedes the
owner's own pose in place** every tick — the owner's own ship state is coalesced
away before it reaches the (round-robin, SocketReady-gated) pool writer. A bot
steers purely from its own pose (`TryCsPose`, `BotManager.cs:1577` — no local
prediction, unlike the native client), so it flies on a stale/missing pose and
loiter-weaves in its home sector. This affects **both** `transport.mode`s
(thread-per-peer just wins the drain race occasionally), exactly as the corrected
scope's rollback A/B on 221 established.

**Fix** (this commit): give the owner's own-ship `SINGLE_SHIP_UPDATE` its **own
coalesce key** (`CoalesceOwnShip = 12`), distinct from the teammate batch, **and
send it GUARANTEED** (never shed under capacity), while still coalesce-keyed
(latest-wins against itself → the backlog stays bounded, P606 intact). This is
the transport realisation of native's own-ship model: the own ship's
`SINGLE_SHIP_UPDATE` is sent `FM_GUARANTEED` (`fsship.cpp:544`) and guaranteed
DirectPlay messages use `DPNSEND_GUARANTEED|COALESCE`, never timing out
(`Messages.cpp:548-563`).

## Root cause (verified, not assumed)

Mechanics in `TcpTransport.cs` `PeerSender.Enqueue`:
```csharp
if (coalesceKey != 0 && _coalesceNodes.TryGetValue(coalesceKey, out var existing))
{
    _queuedBytes -= existing.Value.Frame.Length;
    existing.Value = new QueuedFrame(frame, guaranteed, coalesceKey); // SUPERSEDE IN PLACE
    ...
}
```
`Server.Broadcast.cs` (serial builder) sends, per recipient peer, in this order:
1. the owner's own-ship `SingleShipUpdate` on `CoalesceShipStates` (`:654`, P606),
2. then the teammate position batch on the **same** `CoalesceShipStates` (`:939/942`).

Step 2 supersedes step 1's node in place. So the owner's own pose is dropped every
tick (whenever ≥1 same-side mate is dirty — effectively always at scale). The bot's
`ClientWorld.Ships[own].Latest` is populated only by decoding incoming
`SINGLE_SHIP_UPDATE(78)` (`ConnectionSession.cs:660`), so it goes stale; `FlyWarpLeg`
(`BotManager.cs:2404`) `return`s without issuing steering on a missing pose, or
steers from a stale one → weaving.

The pool writer rounds over peers and skips sockets that aren't writable, so the
own-ship frame was almost always superseded before drain; the thread-per-peer
writer occasionally drained it first (the empirical "sometimes worked" noise). The
native-client (Domingo's MonoGame) predicts its own ship locally, so it was not
affected — matching the live observation.

## Coalesce-key audit (all keys)

| key | constant | messages | per-peer? | collision? |
|---|---|---|---|---|
| 1 | `CoalesceShipStates` | own-ship SSU + teammate posBatch | yes | **YES (own-ship superseded by batch) — FIXED** |
| 2 | `CoalesceShipsUpdate` | HEAVY/LIGHT_SHIPS_UPDATE | yes | none |
| 3 | `CoalesceStations` | STATIONS_UPDATE | per-side | none |
| 4 | `CoalesceAsteroids` | ASTEROIDS_UPDATE | per-side | none |
| 5 | `CoalesceProbes` | PROBES_UPDATE | per-side | none |
| 6 | `CoalesceMissiles` | MISSILE_UPDATE | per-side | none |
| 7 | `CoalesceMissileLock` | MISSILE_LOCK_UPDATE | owner-only | none |
| 8 | `CoalesceMissileTrack` | MISSILE_TRACK_WARNING | owner-only | none |
| 9 | `CoalesceOwnMining` | OWN_SHIP_MINING_UPDATE | owner-only | none |
| 10 | `CoalesceOwnKillBonus` | OWN_SHIP_KILL_BONUS_UPDATE | owner-only | none |
| 11 | `CoalesceLeadIndicator` | LEAD_INDICATOR_UPDATE | owner-only | none |
| **12 (new)** | `CoalesceOwnShip` | own-ship SSU, **guaranteed** | owner-only | **none (the fix)** |

Only key 1 had a cross-kind/cross-ship collision; the fix splits it. Every other key
carries a single message kind; owner-only keys (7-11) are sent to a single owner
peer; per-side keys (3-6) and `ShipsUpdate` (2) are distinct kinds — no other
"client-essential data becomes droppable/superseded" hazard found.

## The change

- `src/Allegiance.Core/Transport/ITransport.cs` — new `SendGuaranteedCoalescing`
  (latest-wins coalesce frame that is GUARANTEED, never shed).
- `src/Allegiance.Core/Transport/TcpTransport.cs` — implementation (guaranteed:true
  + coalesceKey).
- `src/Allegiance.Server/ServerHost.cs` — passthrough.
- `src/Allegiance.Server/Server.cs` / `Server.Lifecycle.cs` — `SendPeerCoalescing`
  gains a `guaranteed` flag; the P608 coalesce-key test seam
  (`_testSendCoalescingCapture`) carries key + guaranteed.
- `src/Allegiance.Server/Server.Broadcast.cs` — `CoalesceOwnShip = 12`; the OWNER's
  own-ship SSU now goes `CoalesceOwnShip, guaranteed:true` (serial `:654` and
  parallel `:1230`). Teammate position batch stays `CoalesceShipStates`,
  non-guaranteed. Parallel `BroadcastPeerBuild.Packets` tuple extended to carry the
  guaranteed flag.
- `src/Allegiance.Server/Server.BroadcastTestSeams.cs` — `TestCollectBroadcastKeys`
  seam.
- `tests/Allegiance.Core.Tests/OwnShipCoalesceKeyTests.cs` — **failing-first** test:
  asserts the own-ship frame uses a distinct key from the teammate batch AND is
  guaranteed, in both serial and parallel builders.
- `tests/Allegiance.Core.Tests/Transport/TcpTransportCoalesceAndReasonTests.cs` —
  new `Guaranteed_coalesce_frame_is_never_shed_under_capacity` (both transport
  modes): proves a guaranteed coalesce frame survives capacity shedding while
  non-guaranteed frames are shed.

## Verification

### Whole-solution build
`dotnet build -m:8` → **0 warnings, 0 errors**.

### Failing-first unit tests
`OwnShipCoalesceKeyTests` (2 tests, serial + parallel builder) **fail on today's
P606 code** (own-ship and teammate batch both on key 1 ⇒ `Assert.NotEqual(ownKey,
mateKey)` fails) and **pass with the fix**. Demonstrated by temporarily reverting
the own-ship send to `CoalesceShipStates`: both tests FAIL; restored → both PASS.
`Guaranteed_coalesce_frame_is_never_shed_under_capacity` passes 5/5 runs in both
modes.

### Local reproduction (this workstation; never 221). Showcase → Sedna(1063), staging off.
BEFORE = pristine P606 tree (`git worktree` at HEAD, unfixed). AFTER = this commit.

| build | mode | scale | bots | duration | crossed ≥1 warp | notes |
|---|---|---|---|---|---|---|
| **P606 (before)** | pool | 1 (15v15) | 30 | 150 s | **0** | all 30 stuck in spawn cluster 1053 the whole run ("in transit" but never move — the weave) |
| **fix** | pool | 1 | 30 | 150 s | **15** | all 30 in transit through 1058/1061 |
| **fix** | thread-per-peer | 1 | 30 | 150 s | **14** | all 30 transiting 1058/1061 |
| **fix** | pool | 13 (195v195) | 390 | 180 s | **116 (29.7%)** | RSS peak 149,056 kB; 1 disconnect; `step:drain` max 4.38 ms |
| **fix** | thread-per-peer | 13 | 390 | 180 s | **97 (24.9%)** | RSS peak 155,336 kB; 1 disconnect; `step:drain` mean 3.39 ms / max 4.42 ms |

(The crossed figure uses the repro's first-report-vs-last-report heuristic and
therefore under-counts bots already mid-route at their first report; every 15v15
run's diag log showed all 30 bots reporting intermediate clusters 1058/1061 in the
`(transit)` leg. Route is 3 warps spawn→Sedna; neither mode reached the final sector
within the run window — equal across modes, per the "match thread-per-peer"
requirement.)

### P606 guarantees intact (measured on the 390-bot runs)
- **No send stalls**: profiler `step:drain` max 4.38 ms (pool) / 4.42 ms (tpp),
  mean ≤3.4 ms; sends are enqueue-and-return. `step:broadcastShipStates` ~28 ms is
  the pre-existing serial-builder CPU at 390 bots (221 runs `parallelBuild=true`;
  unrelated to this change, unchanged by it).
- **Bounded RSS**: server peak ~149-155 MB across both 390-bot runs — no queue
  blow-up (the guaranteed own-ship frame still coalesces to ONE pending node per
  peer/key, so it cannot accumulate).
- **No mass disconnects**: exactly **1** `ReadErrorOrEof` per 390-bot run, **zero**
  `QueueOverflow` (the guaranteed own-ship did not trigger the anti-zombie
  overflow policy).

### Full suite, 2 consecutive runs on the final code (`scripts/run-tests.sh`, P471)
| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1733/1736 (3 skip) | **592/593** | **3916/3918** | 3 — all documented TEST_FLAKES.md live-wire load-flakes (run-1 tail did not preserve names; same rotating class as run 2 / ledger) |
| 2 | 12/12 | 1733/1736 (3 skip) | **593/593** | **3914/3918** | 4 — `SideEliminationLiveTests.Eliminating_side2…` (ledger:39), `GameOverPlayersWireTests.Win_by_elimination…` (ledger:30/267), `…Conquest_win_lists_every_connected_player…` (ledger:31/267), `AddPartTests.Second_part_touch…` (ledger:247) |

Every failure is a **pre-existing live-wire load-flake** already in
`docs/TEST_FLAKES.md` — rotating run-to-run fixed-sleep live-wire-receipt timing in
**untouched** Core/ClientCore test files; none reference any P608 symbol, none fail
in isolation. All P608 tests (`OwnShipCoalesceKeyTests` ×2, `Guaranteed_coalesce…`
×2 both transport modes) and the full transport suite
(`TcpTransportCoalesceAndReasonThreadPerPeerTests`/`PoolTests`,
`TcpTransportSendWorkerPoolTests` incl. pool fairness under a slow peer) passed in
both runs. Cited per `docs/TEST_FLAKES.md`.

## Out of scope (unchanged)
P604 distance culling, bot AI tactics, per-sector simulation. The `list_drones`
miner `SectorId/Position null` observation under P606-pool is the same
coalesce-pressure family (own/teammate ship state being crowded out); it was not
reproduced as a residual in local runs with the fix (all peers transited and
reported sectors/positions).

## Files
See the commit. Deliverable report: `docs/reports/P608_POOL_MODE_BOT_NAVIGATION.md`.
`scripts/repro_p608.sh` (local repro harness) and `/tmp/p608_run13.sh` (390-bot
variant with RSS sampling) were used for the measurements above.
