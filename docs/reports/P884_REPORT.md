# P884 — per-match server retention across game-over rotation

**Verdict: found and fixed a real per-match server leak — a port-only broadcast latch that was only ever
added to, so every finished match left one live entry per (recipient, same-side ship) behind forever, plus
two smaller per-world caches that carried retired ship ids forward. Locally the isolated server's
forced-GC floor grew ~+22 MB per rotation at 394 peers before the fix and stops growing after it; the
retired `Mission`/`Ship` were already collectable, so the leak was never the world object itself.**

**Claude's 200v200 multi-match run on rig 221 is the acceptance test for this fix. It was NOT run by this
lane** (the rig, the mini and the Mac were out of bounds), so nothing here claims the rig's +20 MB/match
is gone in production — only that the identified root is fixed, proven locally, and pinned by a test.

---

## 1. Reproduction (local, bounded, pre-fix)

### Harness

Scratch only (`.scratch/p884/`, git-excluded; raw dumps live there, never in git):

| file | purpose |
| --- | --- |
| `P884Probe/Program.cs` | `serve` mode: boots the real `Allegiance.Server` in its own process, seats N peers (via TCP from the second process) and rotates with the shared `DeclareGameOver`, printing the forced-GC floor + live latch counts per rotation. `clients` mode: N `ConnectionSession` peers that ready up after every rotation. |
| `P884Probe/split_run.sh` | runs the two processes (server heap is measured with **no in-process clients**, matching the rig where bots live elsewhere) |
| `showcase_rotation.py` | the MCP showcase harness (`spawn_scenario preset=showcase`, bots over TCP, matches ended by goals) used to sanity-check the tuning knobs and to prove real combat does not add another growth axis |
| `evidence-split/` | post-fix `dotnet-gcdump` snapshots (`postfix-rotation-N.gcdump`) + their `report` text; `evidence-split/prefix-reports/` holds the **pre-fix** census reports |
| `evidence-probe/`, `evidence/`, `evidence-long/` | pre-fix in-process dumps and the two pre-fix MCP showcase runs |
| `tools/dotnet-gcdump` | `dotnet-gcdump` was not installed on the box; installed as a scratch-local tool (never added to the repo) |

Match-length knobs used (existing ones, no new knobs were added): the probe drives the port's own
`DeclareGameOver` (the P807/P660 test seam) and the server config's `liveSettings.autoNextMatch`; the
showcase cross-check used `games[].goals.gameLengthSeconds` and `showcase.finaleTimeoutSeconds`.

Measurement point: **immediately after the rotation completed** (`_pendingRotations` empty) plus a
settle of 600 ticks, then `GC.Collect(2, Forced, blocking, compacting)` ×2 with
`WaitForPendingFinalizers` between and once more after, then `GC.GetTotalMemory(false)`. The rig's own
metric (`heapAfterGc` from `[gcprof]`, `TickProfiler.cs:495`) was cross-checked in the showcase run.

### Result — the growth is real and reproduces

Isolated server process, 394 loopback peers, one small generated world (`SingleRing`, 4 sectors),
matches started by readying every peer. Pre-fix build = dispatch base `2c20b601` (a scratch
`git worktree` of HEAD, no fix applied):

| rotation | floor after forced full GC (MB) | `_teammatePositionSent` | `_teammateLoadoutCache` |
| --- | --- | --- | --- |
| 1 | 225 | 154,448 | 396 |
| 2 | 252 | 231,672 | 398 |
| 3 | 271 | 308,896 | 400 |
| 4 | 291 | 386,120 | 402 |

**+22 MB and +77,224 latch entries per match, monotonic, never returned** — the local shape of the rig's
`408, 428, 537, 550, …, 620` MB. (An earlier in-process variant — server and 394 test clients in one
heap — showed the same trend, `361 → 400 → 405 → 430` MB; the two-process run above is the authoritative
one because the measured heap is the server's alone.)

The same probe, same protocol, **after the fix**, over 8 rotations:

| rotation | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| floor (MB), post-fix | 153 | 229 | 262 | 302 | 303 | 303 | 305 | 305 |
| floor (MB), pre-fix (4 rotations) | 225 | 252 | 271 | 291 | — | — | — | — |
| `_teammatePositionSent`, pre-fix | 154,448 | 231,672 | 308,896 | 386,120 | — | — | — | — |
| `_teammatePositionSent`, post-fix | 154,448 | 154,448 | 154,448 | 154,448 | 154,448 | 154,448 | 154,448 | 154,448 |
| `_teammateLoadoutCache`, pre-fix | 396 | 398 | 400 | 402 | — | — | — | — |
| `_teammateLoadoutCache`, post-fix | 396 | 396 | 396 | 396 | 396 | 396 | 396 | 396 |

The latch population is now **identical after every match**, and the floor climbs only while the
containers reach their one-match high-water mark, then plateaus (302, 303, 303, 305, 305 for matches
4-8). A second gcdump diff on the fixed build (rotation 2 → rotation 8, six matches) totals **+1.05 MB
of heap and no object-count growth in any latch type**; its only ≥30 KB mover is the pose-packet pool
(`PooledPosePacket[]` 4.19 → 8.39 MB, one doubling of a bounded pool), versus the pre-fix
rotation 1 → 4 diff of **+33 MB** below.

Cross-check with real gameplay: the MCP showcase (80 bots, matches ended by `gameLengthSeconds`) at the
same scale of activity showed `heapAfterGc` 36.8 → 44.7 MB over three matches, i.e. under the 1 MB
resolution of that counter — consistent with the per-match leak being proportional to
`peers × same-side ships` (≈1,600 pairs/match at 80 bots) and therefore only visible at the rig's 394
peers, where it is ~77 k entries/match even with an idle fleet and several hundred thousand per match
once bots respawn (each relaunch mints a new ship id).

## 2. What survived (roots)

### 2a. `_teammatePositionSent` — never pruned (the leak)

`Server.cs:601-614` — `HashSet<(ConnectionId Recipient, short ShipId)>`, P332's "this pair has had its
first teammate pose" latch. It is only ever **added to**:

- `Server.Broadcast.cs:1084` (`_teammatePositionSent.Add((kv.Key, teammateId))` in the port-mode
  teammate pose stream) and `Server.Broadcast.cs:1404` (parallel-build effect apply).
- No removal anywhere in `src/` (grep: only `Server.BroadcastTestSeams.cs:56`, the test reset).
  It was not in `ReleasePeer`'s connection-cache sweep (`Server.Lifecycle.cs:2038-2110`), not in
  `RunMigrationLatchSweep` (`Server.Lobby.cs:1219-1246`), and not in `RotateMissionAfterGameOver`'s
  per-mission purge (`Server.Lobby.cs:671-720`).

So it accumulated one entry per (recipient, same-side ship id) for every match ever played, plus one per
pair for every peer that ever disconnected. Ship ids are a 16-bit space that `AllocateShipId`
(`Server.Docking.cs:2170-2181`) **recycles**, so the stale pairs both leaked memory and could suppress the
first pose of a later ship that reused an id.

### 2b. `_teammateLoadoutCache` — old-world subject snapshots

`Server.cs:629` — `Dictionary<short, (long Version, IReadOnlyList<PartData> Snapshot)>`. Per-*subject*
ship, keyed by ship id. Rotation removed only the *migrant* peers' old ships
(`Server.Lobby.cs:1063`), so every retired **NPC** ship's snapshot stayed for good (the two initial miners
per side per match: the `396 → 398 → 400 → 402` column above). A recycled id would also be served the old
ship's snapshot, because the cache is validated only by `LoadoutVersion` and a fresh ship starts at the
same version.

### 2c. `_crossSideShipStatusSeen` — old-world visibility edges

`Server.cs:665` (`Dictionary<(short SideId, short ShipId), bool>`). Rotation step 12 removed only
`IsOldSubject(k.ShipId)`, i.e. migrating peers' old ships; every retired NPC's (side, ship) edge survived.
It read 0 in the local idle-fleet repro (no visibility edges are created by a docked fleet), so this one
is an inspected root rather than a measured one — but it has exactly the same per-world-key mistake.

### Dump diff that identified them

`dotnet-gcdump` (installed as a scratch local tool — it was not on the box) on the isolated server, after
rotation 1 vs rotation 4, pre-fix. Top growing types (lossy buffering on a ~300 MB heap, so sizes are
approximate; the latch counts in §1 are exact, read live):

| type | rot 1 | rot 4 | Δ |
| --- | --- | --- | --- |
| `BroadcastDelivery[]` (publisher pool high-water) | 12.6 MB | 25.2 MB | +12.6 MB |
| `Entry<(ConnectionId, short)>[]` ← **`_teammatePositionSent`** | 0 | 10.8 MB | **+10.8 MB** |
| `PooledPosePacket[]` (pose packet pool high-water) | 0 | 4.2 MB | +4.2 MB |
| `Int32[]` (latch-set bucket arrays) | 0 | 2.7 MB | +2.7 MB |

The 10.8 MB `Entry<(ConnectionId, short)>[]` is 448,548 slots × 24 B, i.e. the hash-set entries array for
the 386,120 live pairs §1 reports for `_teammatePositionSent`; `_crossSideShipStatusTick` (the other
`(ConnectionId, short)`-keyed dictionary) read 0 throughout, and `_teammateExportSent` /
`_lastTeammateStatusTick` / `_teammateLoadoutVersionSent` stayed flat at 154,448 because they *were*
swept. A reflection holder scan (`FindHolders` in the probe: every `Server` field plus every
`Allegiance.*` static, checked for identity references into the retired world) found **no** references
into the old world, and the retired `Mission`/`Ship` weak references were dead after the forced GC — the
world itself was already being retired correctly (`Mission.Dispose` via `DisposeRetiredMissions`,
`Server.NativeOwnership.cs:11-14`); what leaked was the per-world *latch* state.

## 3. Fix

| file:line | change |
| --- | --- |
| `src/Allegiance.Server/Server.Lobby.cs:696-720` | `RotateMissionAfterGameOver` now clears the three per-world leftovers next to the existing `_shipDetected.Clear()`: `_teammatePositionSent`, `_teammateLoadoutCache`, `_crossSideShipStatusSeen`. Every subject in them is a retired-world ship id; the fresh world repopulates lazily, so it is a pure drop of unreachable state (no wire-behaviour change: the fresh match's first pose/status/export send is still forced, and now *cannot* be suppressed by a stale pair). |
| `src/Allegiance.Server/Server.Lifecycle.cs:2049-2052` | `ReleasePeer` frees `_teammatePositionSent` on `Recipient`/`ShipId`, exactly like its `_teammateExportSent` / `_playerInfoSent` siblings — the missing dimension that also made it grow with plain connection churn. |
| `src/Allegiance.Server/Server.Broadcast.cs:1999-2012` | `AnnounceFreshShip` drops `_teammateLoadoutCache[ship.Id]` so a recycled id can never be served the previous ship's snapshot (same rationale as the existing `_playerInfoSent` drop in the same method). |
| `src/Allegiance.Server/Server.cs:601-614` | The field's doc comment now states the pair/entry lifetime (world) instead of implying it is permanent. |
| `src/Allegiance.Server/Server.cs:884-910` | The M1 `ConnectionScopedCacheCounts` test seam now also reports `TeammatePositionSent` and `CrossSideShipStatusSeen`, so the "these are freed with the peer" contract covers the new members. |

Deliberately **not** changed: the latch sets are emptied per rotation but .NET hash containers keep
their capacity, so the server retains one match's peak latch capacity (~154 k entries ≈ 4 MB at 394
peers with an idle fleet; several MB more at rig scale). That is a **bounded** high-water mark, not
per-match growth — it is the same trade-off the existing P807 sweep already makes, and
`TrimExcess`-ing per rotation would just re-allocate the same arrays every intermission. It is recorded
here as the documented bound.

**Native intent.** Native has no such latch at all: `ProcessGameOver` resets the **same** `CfsMission`
(`FedSrv/fsmission.cpp:3490-3527`, `m_pMission->ResetMission()` at `:3527`), and `ResetMission` moves
every ship to the NULL cluster, terminates **every** cluster and destroys each side's buckets
(`Igc/missionigc.cpp:3804-3828`); `CmissionIGC::Terminate` does the same for a discarded mission
(`Igc/missionigc.cpp:3073-3125`). Nothing of the old IGC world is left for a per-(player, ship) cache to
point at, so the port keeping per-world latches alive across the rotation was never native behaviour.

## 4. Regression test

`tests/Allegiance.ClientCore.Tests/P884RotationRetentionTests.cs` — `Tier=Live`, `Requires=Artwork`, 24
loopback `ConnectionSession` peers, one small generated world, **4 game-over rotations**
(`DeclareGameOver` + full migration drain), 60 settle ticks per rotation so the fresh world repopulates
its latches the way every match does. After each rotation it forces a full blocking gen2 GC and asserts:

1. the retired `Mission` **and** one of its ships are unreachable (`WeakReference`, no test-frame roots);
2. `_teammatePositionSent` does not grow with match count (and is non-zero, so the port ship-update path
   really was exercised — the latch only exists when `broadcast.shipUpdateModel=port` and
   `broadcast.interestTiers=false`, the defaults);
3. `_teammateLoadoutCache` and `_crossSideShipStatusSeen` do not grow with match count;
4. `SimPassStageFailures == 0`.

**Post-fix** (this working tree): passes, and 3× alone. Output:

```
rot=1 positionLatch=528 loadoutCache=26 crossSideSeen=0 oldMissionAlive=False oldShipAlive=False
rot=2 positionLatch=528 loadoutCache=26 crossSideSeen=0 oldMissionAlive=False oldShipAlive=False
rot=3 positionLatch=528 loadoutCache=26 crossSideSeen=0 oldMissionAlive=False oldShipAlive=False
rot=4 positionLatch=528 loadoutCache=26 crossSideSeen=0 oldMissionAlive=False oldShipAlive=False
```

**Pre-fix** (the same test file compiled against a scratch worktree of `2c20b601`, plus only the
measurement accessor in §3 — the pruning was not carried over): **fails at rotation 2**:

```
rotation 2: _teammatePositionSent grew with match count (528 after match 1, 792 after match 2);
            the finished world's (recipient, ship) pairs are never pruned
```

Run duration: ~3 s. Tier: Live (it boots the real server against the artwork corpus), so it is not in
the Fast sweep.

## 5. Gate results

| gate | result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **PASS** — 0 warnings, 0 errors |
| `tools/build-tools.sh` | **PASS** — all 93 tool projects |
| Server Native AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **PASS** — ELF 64-bit x86-64 PIE; booted from a camelCase config (static core loaded, transport listening, clean shutdown) |
| new test 3× alone | **PASS** 3/3 (~3 s each) |
| `tools/run-tests.sh --tier fast` (one run) | **11,677 cases, 458 s, 4 failures** — three are load flakes, each **3/3 alone** afterwards and newly ledgered in `docs/TEST_FLAKES.md` (`MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission`, `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell`, `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`); the fourth is the **already-ledgered** pre-existing `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` (line 179), not re-isolated here. The sweep is retained as red rather than re-run (one sweep is the gate); all three new flakes have their isolated receipts in the ledger. |
| showcase never-parks ×5 (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`) | **PASS 5/5** (3m49s-4m20s each, no parked >5 s) |
| rotation-specific live tests | **PASS** — `P807GameOverHandoffTests` (incl. the 394-peer handoff) + `P660NextMatchLiveTests` + `P765GameOverStallTests` = 11/11; `P860RotationWorldExportBoundaryTests` + `P480RotationKeepsPlayersLiveTests` = 3/3 |

`docker-compose.yml`, stores, ports and env vars are untouched by this change, so `docker compose
config` is unaffected.

### 5.1 showcase never-parks ×5

`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`, five sequential isolated
runs after the single Fast sweep: **passed 5/5** (3m52s, 4m20s, 3m49s, 4m11s, 3m54s) with the test's own
30-bot showcase and its "never parked longer than 5 s" assertion intact.

## 6. Unfinished / follow-ups

- The rig's 200v200 ten-match run (Claude, rig 221) is the acceptance test; it was not repeated here.
  Expected effect there: the `_teammatePositionSent` population stops growing across matches (it was the
  only latch with no prune path), so the retained floor should plateau at one match's peak instead of
  climbing ~+20 MB/match. Any *remaining* rig growth would then be the bounded latch/pool capacity
  high-water documented in §3, or a different root that this local reproduction could not see.
- The 394-peer local probe measures an *idle* fleet (peers ready up but do not fly). Real matches mint
  many more ship ids per match (bot respawns), which multiplies the same leak; that multiplier was not
  measured locally beyond the MCP showcase cross-check in §1.
- No parity-CSV row was added: no native rule changed. The footer below classifies the defect.
- `docs/TEST_FLAKES.md` records the three Fast-sweep flakes with their 3× isolation results.

## 7. Native-intent footer

**Classification: port defect (port-only state), not a native behaviour difference.** Native resets the
same `CfsMission` and destroys every cluster/ship binding of the old world
(`FedSrv/fsmission.cpp:3527` → `Igc/missionigc.cpp:3804-3828`; `CmissionIGC::Terminate`,
`Igc/missionigc.cpp:3073-3125`), so it has no per-(player, ship) "already sent" cache that could outlive a
match. The three caches fixed here are port inventions (`_teammatePositionSent` P332,
`_teammateLoadoutCache` P317, `_crossSideShipStatusSeen` P493) whose *message* behaviour is native-shaped
but whose lifetime was inadvertently the process's instead of the world's. The fix makes their lifetime
match native's world lifetime and changes nothing a client can observe except that a new ship can no
longer be silenced by a stale id entry.
