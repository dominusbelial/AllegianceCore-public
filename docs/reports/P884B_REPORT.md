# P884b — the rig's per-match server heap growth under the rig's real settings

**Verdict: reproduced at the rig's peer count, and the root is not a retired world — it is the
per-(recipient, object) *discovery* latch keeping the backing arrays of the widest moment a match
ever reached.** At 394 peers, with exactly the rig's boot settings (`broadcast.shipUpdateModel =
native`, immutable snapshot, encode-once, parallel build 15, pool transport 16, no interest tiers,
`sim.nativeGotoPlan`, `ALLEGIANCE_TICK_PROFILER=1`), the forced-compaction floor went
**188.9 → 197.0 → 244.0 → 244.9 → 245.8 → 245.1 MB** over six rotations, and a `dumpheap -stat`
diff between rotation 2 and rotation 5 attributed **+33.9 MB (entry array) + 8.6 MB (bucket array)
— 42.5 MB of the +56.7 MB total — to one type:**
`HashSet<(ConnectionId Recipient, byte Kind, short Id)>`, i.e. `_crossSideDiscoverySent`. The
rotation pruned its *entries* but not its *capacity*; every match whose peak exceeded the previous
one re-grew the arrays in doublings and the floor ratcheted up. The retired world itself was
already collectable (`retiredRefsAlive 0/24` across six rotations of the pre-fix build).

After the fix (retire per world **and** release the peak capacity at the rotation boundary) the same
protocol gives **177.1 → 181.5 → …** (see §4): the per-match step is gone at the same measurement
point.

Along the way three smaller real per-world survivors were found and fixed, all measured with
`dotnet-dump` gcroot chains (not inferred):

1. `_ballots` — the per-tick ballot resolution and the inbound VOTE handler used a
   get-**creating** accessor, so a world with no ballots was given an empty list every tick; a VOTE
   from a peer still seated in the retiring world re-created the entry *after* the rotation removed
   it, keeping that retired `Mission` reachable for the rest of the process
   (gcroot: `Server._ballots → Entry[] → Mission`).
2. `_lastTurretUpdates` — native's per-ship `m_atu` (`FedSrv/fsship.h:541`) is keyed by ship id and
   was never retired with the world, so every gunner ship id of every match stayed forever.
3. the allied-dock pending sets (`_alliedRearmPending`, `_alliedPodRedirectPending`) hold `Ship`
   references that only the same-tick dock callback drains; a rotation in between kept a retired
   ship — and through it a retired `Mission` — reachable.

Finally, P884's three clears were **server-wide**, which is wrong on a multi-mission server
(P883b): rotating one world wiped every other world's latches. They are now per mission.

**Claude's 200v200 rig-221 run is the acceptance test for this work. It was NOT run by this lane**
(the rig, the mini and the Mac were out of bounds), so nothing here claims the rig's number is
gone; the report states only what was measured locally, and §8 records what the rig run must show.

---

## 1. Reproduction

### 1.1 Harness (scratch only, `.scratch/p884b/`, git-excluded; raw dumps stay there)

| file | purpose |
| --- | --- |
| `Probe/Program.cs` | hosts the real `Allegiance.Server` in its own process, boots it from a `ServerConfig` built with the rig settings, ticks it, forces game-over rotations **on the tick thread** (via the existing `DeclareGameOver` seam, at the position production's victory callback runs at), then prints the forced-GC floor + a per-field collection census per rotation. |
| `run.py` | starts the probe and, in a **separate process**, `tools/allegiance-mcp`, whose `spawn_scenario preset=showcase` seats 197 bots per side (394 connections, the rig's count) that fly, fight, die and relaunch; collects `dotnet-dump` snapshots at requested rotations while the probe holds. |
| `Probe` is built **Release**, and the rotation is a `[MethodImpl(NoInlining)]` helper that returns only `WeakReference`s + ids | Debug-mode locals *and* callers' locals keep a retired world alive in a test frame; the first attempt measured exactly that and produced a false "the retired Mission survives" result. The tick-thread injection removed the other false positive (see §2.1). |
| pre-fix | `.scratch/p884b/prefix394/` (floors, census, `dumps/rot2.dmp`, `dumps/rot5.dmp`) |
| post-fix | `.scratch/p884b/postfix394/` |
| `.scratch/p884b/base-worktree/` | `git worktree` of the dispatch base, used to show the new tests fail pre-fix |

### 1.2 The rig's settings: how each is set, and that the probe uses the same mechanism

| rig setting | config key (`ServerConfig`) | env override (env wins) |
| --- | --- | --- |
| `broadcast.interestTiers = False` | `broadcast.interestTiers` (`BroadcastConfig.InterestTiers`, default false) | `ALLEGIANCE_BROADCAST_TIERS` |
| `broadcast.immutableSnapshot = True` | `broadcast.immutableSnapshot` (default true) | `ALLEGIANCE_BROADCAST_SNAPSHOT` |
| `broadcast.encodeOnce = True` | `broadcast.encodeOnce` (default true, requires snapshot) | `ALLEGIANCE_BROADCAST_ENCODE_ONCE` |
| `broadcast.parallelBuild = true (max 15)` | `broadcast.parallelBuild` + `broadcast.maxDegreeOfParallelism` | `ALLEGIANCE_BROADCAST_PARALLEL_BUILD` |
| `broadcast.shipUpdateModel = native` | `broadcast.shipUpdateModel` | `ALLEGIANCE_SHIP_UPDATE_MODEL` |
| `transport = pool (16 workers)` | `transport.mode` + `transport.poolWorkers` | `ALLEGIANCE_TRANSPORT_MODE` / `ALLEGIANCE_TRANSPORT_POOL_WORKERS` |
| `sim.nativeGotoPlan = true` | `sim.nativeGotoPlan` (`SimConfig`) | `ALLEGIANCE_SIM_NATIVE_GOTO_PLAN` |
| `ALLEGIANCE_TICK_PROFILER=1` | `profiler` section | `ALLEGIANCE_TICK_PROFILER` |

The probe sets them through `ServerConfig` (the same object `Server.Boot` resolves for a
`server.json`), and the tick profiler through its env var, matching the rig. Boot lines confirm them
(`[boot] broadcast.shipUpdateModel = native`, `[boot] transport mode = pool (16 workers)`,
`[boot] broadcast.parallelBuild = true — … (max 15 workers)`, `[boot] broadcast.interestTiers =
False`, `[boot] sim.nativeGotoPlan = true`).

World: the rig's file map `m2xspiderw.igc` (3 sectors, 2 sides), 197 bots/side = 394 peers + the
MCP observer, matches ended by the existing `DeclareGameOver` seam after 150 s of combat. No new
knobs were added. Measurement point: rotation completed (`_pendingRotations` empty) + 600 settle
ticks into the fresh match, then two blocking compacting gen2 collections (+finalizer drain) and one
more, then `GC.GetTotalMemory(false)` — the same point of every match.

### 1.3 Pre-fix result — the growth is real, and it needs the rig's peer count

| rotation | 1 | 2 | 3 | 4 | 5 | 6 |
| --- | --- | --- | --- | --- | --- | --- |
| forced-GC floor (MB), 394 peers | 188.9 | 197.0 | 244.0 | 244.9 | 245.8 | 245.1 |
| `GCMemoryInfo.HeapSizeBytes` (MB) | 201.6 | 210.9 | 260.2 | 261.2 | 261.8 | 261.2 |
| retired `Mission`/`Ship` weak refs alive | 0/4 | 0/8 | 0/12 | 0/16 | 0/20 | 0/24 |

The two `dotnet-dump` snapshots (rotations 2 and 5) put the step at +56.7 MB of live heap, of which
the discovery latch's arrays are 42.5 MB (§3.1). The probe's own `[gcprof] heapAfterGc` (the rig's
exact metric) tracked the same climb — 201.6 → 261.2 MB — and the per-window `[gcprof]` series rose
from 34 MB at boot to 311 MB during the last match, then fell back only to 288 MB at the rotation.
The dump-collection pause at rotation 2 let match 3 run ~300 s longer than the other rotations,
which is why the step lands between rotations 2 and 3; rotations 4–6 show the residual steady state.

**Which knob makes it appear: player count.** The same harness at 201 peers (100/side, §1.4) is flat;
at 394 peers it steps. Nothing else had to be varied — the growth is present with the rig's own
model/snapshot/encode/pool/parallel settings, and only the peer count (the retained capacity is
`peers × subjects`) decided whether it was visible. The 201-peer control also explains why P884's
earlier reproduction (fewer, idle peers) could not see it.

### 1.4 The 201-peer control (same rig settings, half the peers)

| rotation | 1 | 2 | 3 | 4 | 5 | 6 |
| --- | --- | --- | --- | --- | --- | --- |
| floor (MB), 201 peers | 84.3 | 87.6 | 88.8 | 89.2 | 89.7 | 89.8 |

Flat after warm-up: the retained latch/pool capacity is proportional to `peers × subjects`, so at
201 peers it is already below the one-match high-water the first matches establish. This is why
P884's local reproduction (which also used fewer, idle peers) could not see the rig's step.

---

## 2. What survives — measured, not inferred

### 2.1 Measurement discipline (two false positives that had to be removed first)

1. **Test-frame roots.** With the rotation helper inlined into the probe's `Main` and the probe
   built Debug, the retired world stayed alive in the *probe's* stack slots (`gcroot` showed
   `Program.<Main>$ … rbp-600 → Mission`). Release build + `NoInlining` helper + returning only
   `WeakReference`s/ids turned that into `0/N alive` at every rotation. P884's test already knew
   this (`WeakReference` after `NoInlining` rotation); this task added the same rule to the probe.
2. **Rotation on a foreign thread.** The first attempt invoked `DeclareGameOver` from the probe's
   main thread while the tick thread ran `SimPass`. In production the call happens *inside* the tick
   (the victory callback). With the race, a concurrent `SimPass` could run
   `ResolveBallots(_missions[i])` between `RotateMissionAfterGameOver`'s `_ballots.Remove` and its
   `_missions[idx] = fresh`, re-creating `_ballots[retiredMission]` — and `gcroot` then showed
   `Server._ballots → Entry[] → Mission`, one retired `Mission` alive per rotation. Retiring a world
   from another thread is not a production path; running the rotation on the tick thread removed it.
   **The hazard it exposed is nevertheless real for normal operation** (a VOTE datagram drained from
   a peer still seated in the retiring world during the multi-tick migration), and is fixed (§3.3).

With both removed, the pre-fix build's `gcroot` for the retired `Mission` reaches only
`Mission.Site → ServerHost` (a process-lifetime object) — i.e. the disposed mission is unreachable
and collected. Nothing of a retired IGC world survives the rotation at 394 peers under the rig's
settings.

### 2.2 The dump diff that found the root (pre-fix, rotation 2 → rotation 5)

`dotnet-dump` `dumpheap -stat` diff (raw dumps: `.scratch/p884b/prefix394/dumps/`):

| Δ | type | rot 2 | rot 5 |
| --- | --- | --- | --- |
| **+33.88 MB** | `HashSet<(ConnectionId, byte, short)>+Entry[]` — **`_crossSideDiscoverySent`** | 10.27 MB = 672,827 slots | 44.15 MB = 2,893,249 slots |
| **+8.60 MB** | `Int32[]` (that set's bucket array, plus other collections) | 5.93 MB | 14.53 MB |
| +6.11 MB | `Byte[]` (per-peer transport frame pools / packet buffers, 23,829 more arrays) | 74.88 MB | 80.99 MB |
| +4.10 MB | `Free` (fragmentation between segments) | 14.00 MB | 18.10 MB |
| +1.78 MB | `TcpTransport+PeerSender[]` (send-worker scan snapshots) | 2.31 MB | 4.09 MB |
| +0.53 MB | `Dictionary<CollisionDetector+Geometry,int>+Entry[]` | 0.53 MB | 1.06 MB |
| +0.44 MB | `Collider` | 0.53 MB | 0.98 MB |
| +0.22 MB | `PooledPosePacket` (static pool, bounded high-water) | 86,621 objects | 90,672 objects |
| +0.15 MB | `TcpTransport+PeerSender+QueuedFrame` (transient backlog) | 195,352 | 197,605 |
| total | all objects | 222.2 MB | 278.9 MB (+56.7 MB) |

The single set's entry array plus its buckets is **42.5 MB of the 56.7 MB growth**; everything else
is below 1 MB except transient buffers and fragmentation. `dumpobj` on the set (entry width 16 B, so
`(arrayBytes − 24) / 16` = slot count):

```
rot 2 dump: _entries = 10,765,256 B  ->   672,827 slots
rot 5 dump: _entries = 46,292,008 B  -> 2,893,249 slots   (4.3x)
```

At the measurement point of every rotation the probe's census reads the **same live population —
~10.3 k entries** (10,271 / 10,271 / 10,271 / 11,059 / 10,271 / 11,459 pre-fix; 10,271 … post-fix).
So the set was holding ~1.5 % occupancy in a slot array 4.3× larger than at rotation 2: the entries
are retired correctly, only the capacity is not. `gcroot` on that array (rotation-5 dump) is the
whole story — a server field, not a retired world:

```
Server._crossSideDiscoverySent
  -> SweepLatchSet<(ConnectionId, byte, short)>
  -> HashSet<(ConnectionId, byte, short)>
  -> HashSet<...>+Entry[]  46,292,008 B   (2,893,249 slots for ~10.3 k live entries)
```

The rotation *does* remove the entries (the sweep is per recipient, `RunMigrationLatchSweep` step 5
— the post-rotation census reads ~10 k entries at the same point of every match), but a .NET
`HashSet` never shrinks: the fresh match re-grows the arrays from the *last* match's peak, and any
match whose peak is higher doubles them again. That is the ratchet, and it is the shape of the rig's
"+10–20 MB per match, never returns".

### 2.3 What the census rules out

Per rotation the probe prints a census of every server collection (including the
`SweepLatchSet`-wrapped latches). Between rotations 1 and 4 nothing grows: `_teammatePositionSent`
(0 on the native path, as P884 predicted), `_teammateLoadoutCache`, `_crossSideShipStatusSeen`,
`_lastTeammateStatusTick`, `_teammateLoadoutVersionSent`, `_crossSideShipStatusTick`,
`_sameSideNpcExportSent`, `_playerInfoSent`, `_teammateExportSent`, `_crossSideExportSent`,
`_crossSideMineExportSent`, `_buildingEffectExportSent`, `_stationIdentitySent`,
`_crossSideDiscoverySent` (count only), `_shipDetected`, `_lastTurretUpdates`, `_ballots`,
`_retiredMissions`, `_viewerPoseCadence`, `_rankedHeavy`, `_lastRelayedAcceptedCommand`,
`_lastBaseNotices`, `_parkedShips`, `_podTrips._active` are all constant or transient. So the growth
is **capacity and buffer pools, not live entries** — which is why a leak-hunting diff that only
compares counts would have missed it.

---

## 3. Fix

Native intent for every item below: `CFSMission::ProcessGameOver` resets the **same** mission
(`FedSrv/fsmission.cpp:3490-3527`, `m_pMission->ResetMission()` at `:3527`), and
`CmissionIGC::ResetMission` moves every ship to the NULL cluster, terminates every cluster and
destroys each side's buckets (`Igc/missionigc.cpp:3804-3828` — ships `:3808-3812`, clusters
`:3815-3820`, side buckets `:3822-3827`). Nothing of the old IGC world, and no per-object record of
it, survives a reset.

| file | change |
| --- | --- |
| `src/Allegiance.Server/Server.WorldRetirement.cs` (new) | `RetirePerWorldShipState(Mission retired)` — drops every ship-keyed per-world entry whose subject is **not a live ship of another mission** (`_teammatePositionSent`, `_crossSideShipStatusSeen`, `_teammateLoadoutCache`, `_shipDetected`, `_shipDetectedChanged`, `_lastTurretUpdates`, `_pendingTurretRelays`, `_alliedRearmPending`, `_alliedPodRedirectPending`). The complement rule also covers ships destroyed earlier in the match (no longer in any registry), which a `mission.GetShips()`-only filter would miss. |
| same file, `TrimRetiredLatchCapacity()` | releases the **peak backing arrays** of every `(recipient, subject)` latch (`TrimExcess` only when `capacity > 2·count + 64`, so a set already near its live size is not re-allocated every intermission), after the retired world's entries are gone. This is the actual fix for the rig's step: the latches' retained size now follows the live population instead of the widest match ever played. Called from `RetirePerWorldShipState` and again when the deferred migration sweep finishes (`RunMigrationLatchSweep`), so a large multi-tick migration is covered. |
| `src/Allegiance.Server/Server.Lobby.cs` `RotateMissionAfterGameOver` | replaces P884's server-wide `_teammatePositionSent.Clear(); _teammateLoadoutCache.Clear(); _crossSideShipStatusSeen.Clear();` and HUD-17's `_shipDetected.Clear()` with `RetirePerWorldShipState(mission)` — the same retirement, **per mission**, so another world's latches are untouched on a multi-mission server (P883b). |
| `src/Allegiance.Server/Server.Lobby.cs` `RunMigrationLatchSweep` | calls `TrimRetiredLatchCapacity()` once the last sweep step has removed the retired world's entries. |
| `src/Allegiance.Server/Server.Lobby.cs` `BallotsOf`/`HasBallotOfType`/`ResolveBallots` | the per-tick resolution and `HasBallotOfType` no longer materialise a list: only ballot **creation** calls the creating accessor. A world with no ballots has no `_ballots` entry. |
| `src/Allegiance.Server/Server.Dispatch.cs` VOTE handler | read-only lookup (`_ballots.TryGetValue`); a VOTE never creates the per-mission list. Native `TallyVote` (`FedSrv/fsmission.cpp:1209-1219`) and `HasBallots` (`:1225-1235`) only scan the mission's existing list. |
| `src/Allegiance.Server/CrossSideSweepCache.cs` | `SweepLatchSet<T>` gains `Capacity` (test seam) and `TrimExcess()`. |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs` | P884b seams (inert in production): `WorldCacheCounts`, `WorldCacheContainsShip`, `WorldCacheCapacities`, `TeammatePositionLatch`, `TestHasBallotList`, `TestTeammatePositionPairs`. |

Why `_lastTurretUpdates` is a real per-world cache: native stores the accepted ACTIVE_TURRET_UPDATE
in `CFSShip::m_atu` (`FedSrv/fsship.h:541`), i.e. **on the ship object** the next generation
replaces. The port stores it in a server map keyed by ship id
(`Server.ShipCommands.cs:203`), so without retirement it outlives every ship and every world.

**Deliberately not changed:** the per-peer transport frame pools and the static `PooledPosePacket`
bags are bounded by their per-connection / per-bucket caps and are *not* keyed by world; their
+0.2 MB movement between rotations 2 and 5 is a high-water mark, not a leak. `EgressAccounting`'s
20.9 MB `Counters[]` is a single fixed boot-time allocation. Both are recorded here as constants so
a future dump diff does not mistake them for growth.

---

## 4. After

Same probe, same settings, same 197 bots/side, same rotation timing and same measurement point
(`.scratch/p884b/postfix394/`):

| rotation | 1 | 2 | 3 | 4 | 5 | 6 |
| --- | --- | --- | --- | --- | --- | --- |
| **pre-fix** floor (MB) | 188.9 | 197.0 | 244.0 | 244.9 | 245.8 | 245.1 |
| **post-fix** floor (MB) | 177.1 | 181.5 | 186.8 | 187.2 | 187.8 | 188.5 |
| pre-fix `HeapSizeBytes` (MB) | 201.6 | 210.9 | 260.2 | 261.2 | 261.8 | 261.2 |
| post-fix `HeapSizeBytes` (MB) | 203.5 | 223.0 | 233.5 | 225.8 | 208.2 | 233.0 |
| floor after the 300 s dump hold (mid-match, MB) | pre 230.4 (rot 2) | post 229.9 (rot 2) | — | — | post 232.2 (rot 5) | — |
| retired refs alive (both) | 0/4 | 0/8 | 0/12 | 0/16 | 0/20 | 0/24 |

The transient *within-match* peak is essentially unchanged (pre-fix 230.4 MB, post-fix 229.9 MB after
the rotation-2 hold) — as intended: a side's live knowledge of the objects it has seen is real state
while the match runs. What changed is what survives the rotation:

- the **retained** floor at the same point of each match drops from 244.0 to 186.8 MB at rotation 3
  and ends **−56.6 MB** vs pre-fix at rotation 6 (245.1 → 188.5 MB);
- the per-match step is gone: rotations 3–6 move by **+1.7 MB** post-fix, versus **+49.0 MB** from
  rotation 2 to rotation 3 pre-fix (the extra 300 s match the dump hold allows at rotation 2 is
  exactly the condition that grew the pre-fix slot array 4.3× and then kept it);
- the first-match floor is also lower (177.1 vs 188.9 MB) because match 1's peak is released before
  the first measurement instead of being carried by every later match.

Raw post-fix dumps: `.scratch/p884b/postfix394/dumps/`.

---

## 5. Regression test

`tests/Allegiance.ClientCore.Tests/P884bWorldRetirementTests.cs` — `Tier=Live`, `Requires=Artwork`,
both cases boot the real server with the rig's settings.

1. `Rotation_retires_every_per_world_ship_cache_under_the_rig_settings` — 24 loopback peers, one
   small world, **4 rotations**, `shipUpdateModel=native` + snapshot + encode-once + parallel build +
   pool transport. Per rotation it plants native's per-ship `m_atu` through
   `RecordAcceptedActiveTurretUpdate`, sends a VOTE for an unknown ballot, and then asserts:
   - the retired `Mission` **and** one of its ships are unreachable after a forced full GC;
   - no per-world cache names a retired ship id (`WorldCacheContainsShip`), the ballot table holds
     no entry for a world with no ballots, and `_pendingTurretRelays`/the allied-dock sets are empty;
   - the caches that persist across a rotation do not grow with match count;
   - the latches' **retained capacity** is released (`DiscoveryCapacity <= 4·count + 64`,
     same for `_shipDetected`) — the rig-step invariant;
   - `SimPassStageFailures == 0`.
2. `Rotating_one_world_leaves_another_worlds_per_world_caches_alone` — two worlds (`Games` ×2) with
   8 peers each, `shipUpdateModel=port` (the only model that fills the P332 teammate latch).
   Rotating world A must leave world B's latch population (per ship id **and** per-pair count)
   untouched, while world A's own ids are gone afterwards.

**Post-fix (this tree): 2/2 pass, 3× alone, ~5 s per run.**

**Pre-fix** (scratch `git worktree` of dispatch base `bfbd0d8a` + only these seams: `Capacity`
property, `WorldCacheCounts`/`WorldCacheContainsShip`/`WorldCacheCapacities`/
`TestTeammatePositionPairs`/`TestHasBallotList`) — the same tests fail, in four separate assertions
(each shown from a dedicated base-worktree run, so every one of them is a real pre-fix failure):

```
rotation 1: an inbound VOTE for an unknown ballot created a ballot list for the live mission —
            read paths must never create per-mission state (a retired world's list keeps it reachable)
rotation 1: a per-world ship cache still names retired ship 8096
            (native m_atu/Detected/loadout state lives on the ship, fsship.h:541 — …)
rotation 2: _lastTurretUpdates grew with match count (1 -> 2)
rotating mission A cleared mission B's per-world cache for ship 8104 — the rotation must scope its
            retirement to the world it retires (P883b multi-mission server)
```

The 394-peer capacity ratchet itself is proven by the §2.2 dump diff and the §4 before/after table,
not by this small test (at 24 peers one match's peak is barely larger than the fresh world's first
population, so the capacity assertion is a bound rather than the failing assertion at that scale).

---

## 6. Gate results

| gate | result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **PASS** — 0 warnings, 0 errors |
| `tools/build-tools.sh` | **PASS** — all 94 tool projects |
| Server Native AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **PASS** — 15.9 MB ELF 64-bit x86-64 PIE; booted from a scratch camelCase config with the rig settings (`broadcast.shipUpdateModel = native`, pool transport 16, parallel build 15, `sim.nativeGotoPlan = true`), static core loaded, transport listening, then killed by the timeout |
| new tests 3× alone | **PASS** 3/3 (2 tests, ~5 s each) |
| existing rotation/ballot live tests | **PASS** 12/12 — `P884RotationRetentionTests`, `BallotVotingLiveTests`, `P807GameOverHandoffTests`, `P660NextMatchLiveTests`, `P765GameOverStallTests` |
| `tools/run-tests.sh --tier fast` (one run) | **11,725 tests, 514.8 s, FAIL** — infrastructure checks PASS; 5 non-task failures, each isolated afterwards (table below). The sweep is retained as red rather than re-run (one sweep is the gate). |
| showcase never-parks ×5 (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`) | **7 passes / 8 runs**: runs 1, 2, 3, 5, two re-runs and runs 6–7 passed (3m25s–6m10s); run 4 failed after the full 480 s window **while another lane (p887) was executing the same showcase test concurrently on this machine** (its runs passed). P700's config has `autoNextMatch.enabled = false`, i.e. that test never rotates a world, so this change's rotation path is not exercised by it. Ledgered with the attribution. |
| `docker compose config` | **PASS** (no service/store/port/env change) |
| `tools/check-glossary-links.py` | **PASS** (335 paths) |

### 6.1 Fast-sweep failures, 3× isolation (`docs/TEST_FLAKES.md`)

| test | sweep | isolated afterwards |
| --- | --- | --- |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | failed once | **0/3 — the pre-existing baseline failure** already ledgered under P872/P884 (`NpcWingmanDefendLiveTests.cs:179`), not this lane's path |
| `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at` | failed once | **3/3 pass** — load flake |
| `LiveShieldRegenBroadcastTests.Stationary_ship_streams_its_regenerating_shield_over_the_wire` | failed once | **3/3 pass** — load flake |
| `ArmedProbeLiveTests.Armed_probe_spawns_hits_and_damages_enemy_with_ammo_decrement_live` | failed once (37.3 s, a calibrated-limit offender too) | **3/3 pass** (~0.6 s) — load flake |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` (MonoGame) | failed once | **3/3 pass** — the already-ledgered launcher load flake |

Two calibrated-limit offenders were also reported: `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell`
(18.6 s, already ledgered under P884) and `ArmedProbeLiveTests…` (37.3 s, 3/3 alone in ~0.6 s). Nothing
in the change set touches NPC steering, shield regeneration, armed probes or the shop path; all of
those tests run without a game-over rotation. Ledger updated with a P884b section.

---

## 7. Native-intent footer

**Classification: port defect (port-only state lifetime), not a native behaviour difference.**
Native has no per-(recipient, object) "already told this peer about this object" set that outlives a
world: it exports at join and on `ShowObject`/`HideObject` and resets the same mission
(`FedSrv/fsmission.cpp:3490-3527`), whose `ResetMission` destroys every cluster and side bucket
(`Igc/missionigc.cpp:3804-3828`). The port invented `_crossSideDiscoverySent` (P797),
`_teammatePositionSent` (P332), `_teammateLoadoutCache` (P317), `_crossSideShipStatusSeen` (P493)
and the HUD-17 Detected baseline to stand in for native's per-client IGC knowledge; their *message*
behaviour is native-shaped, but their *lifetime* must be the world's, and their *retained capacity*
must not be the widest match the process ever played. Native's `m_atu` is per ship
(`FedSrv/fsship.h:541`), so a per-ship server map must be retired with the ship/world. Native's
`m_ballots` is a per-mission list that `TallyVote`/`HasBallots` only ever read
(`FedSrv/fsmission.cpp:1209-1219`, `:1225-1235`), so a vote lookup must not create one. No
client-visible rule changed: the fresh world still re-sends its first pose/status/export/discovery
messages exactly as before, and the only behavioural difference is that a new ship/object can no
longer be silenced by a stale id entry from a retired world, and one world's rotation no longer
re-sends another world's latched messages.

## 8. Unfinished / for the rig run (Claude)

- **Claude's 200v200 run on rig 221 is the acceptance test.** Expected effect there: the per-match
  forced-GC/`heapAfterGc` floor stops stepping by whatever the difference between one match's peak
  discovery-latch population and the previous high-water was (locally that was +34 MB entry array +
  8.6 MB buckets). The rig's `[gcprof] heapAfterGc` should now plateau after the first full match
  instead of climbing ~+10–20 MB/match; a residual rise would be the transient within-match peak
  (objects a side has actually seen are still latched during the match, native-equivalent knowledge)
  plus the transport frame pools, both released/bounded at the next rotation.
- That within-match peak is *not* capped: a very long match on a map with continuously spawned
  treasures/probes can still hold a large discovery latch while it runs. It is now released at every
  rotation instead of becoming the next match's baseline; pruning dead-object entries within a match
  would need a reverse index and was out of scope (measured cost/benefit did not justify it).
- No parity-CSV row was added: no native rule changed, only the lifetime/capacity of port-only
  stand-ins.
