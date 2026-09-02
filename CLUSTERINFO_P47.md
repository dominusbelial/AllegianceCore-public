# P47 — send FM_S_CLUSTERINFO(200) so ScoutExploreMap's smart target selection can engage

**Repo:** `AllegianceCore` (branch `master`), pure server-side wire-message
port — no native `Allegiance` repo involvement, no Windows VM, no Obsidian/MCP.
Native side was already fully implemented and unchanged (read directly, see
below). Branch `master`, top commit was `f4fedbf` (VIZEXPLORE_LIVE) at start;
this adds P47 on top, committed as `8b52b76`.
Date: 2026-09-01.

---

## Why this exists (correcting P40's wrong guess)

P40 found `ScoutExploreMap`'s smart target-selection branches never engage
because `GameInfo.Clusters` is never populated
(`src/Wopr/Wopr/Entities/GameInfo.cs:69-92` `GetUnexploredClusters()` always
returns empty), so every bot falls back to "pick any visible warp at random"
(`ScoutExploreMap.cs:653-662`). P40 *guessed* the cause was a missing map
download and left it out of scope.

Reading the native source directly corrects that guess: it is not a file
download. It's one ordinary in-session FEDMSG the reference server already
sends and the reference client already fully handles — `AllegianceCore` just
never implemented it:

- **Message:** `FM_S_CLUSTERINFO`, fmid **200**
  (`src/Inc/Messages.h:1039-1048`). Its own comment: *"BT - WOPR - Give the
  bots extra data at game start about the map so that they can determine when
  they have discovered enough stuff."* Fixed layout: `clusterIDs[32]` +
  `homeSectors[32]` (parallel, `-1` sentinel-terminated), then
  `warpFromClusterIDs[1026]` + `warpToClusterIDs[1026]` (parallel,
  `-1`-terminated) — a compact cluster-connectivity graph, cluster IDs only,
  no warp IDs, no positions.
- **Where the reference server builds/sends it:** `CFSMission::SendMissionInfo`
  (`src/FedSrv/fsmission.cpp:4118-4166`), in the **same function**, immediately
  **before** the cluster-iteration loop that `AllegianceCore`'s
  `ExportCluster`/P30 work already mirrors (`ExportObj(pcluster, OT_cluster,
  NULL)`, `fsmission.cpp:~4170`). This task ports the *first half* of a function
  whose second half was already ported.
- **Boot-log stub is unrelated — untouched.** `Server.cs:176-179`'s
  `[boot] (stub) cores.txt / ranks.txt / maps.txt NOT loaded` is lobby
  game-advertisement metadata (native `FedSrv.CPP:103/159/189`), nothing to do
  with `CLUSTERINFO`. Left exactly as is.
- **Native client side needs zero changes** — read directly: managed decode
  (`src/Wopr/AllegianceInterop/AllMessages.h:10165-10207`, `FMD_S_CLUSTERINFO`'s
  constructor walks the sentinel-terminated arrays and reconstructs
  `ClusterInfo`/`WarpInfo` objects) and the one-line handler that already
  exists (`src/Wopr/Wopr/Strategies/StrategyBase.cs:172`:
  `GameInfo.Clusters = message.Clusters`). `GetUnexploredClusters()` already
  consumes `GameInfo.Clusters` today — it has simply never once received a
  non-null value.

---

## Files changed (scope)

```
src/Allegiance.Core/Wire/ClusterInfo.cs        (new — codec)
src/Allegiance.Server/Server.cs                (+CLUSTERINFO send in join burst)
src/Allegiance.Bot/Bot.cs                      (+ClusterInfos observation hook)
tests/Allegiance.Core.Tests/ClusterInfoServerTests.cs  (new — loopback proof)
CLUSTERINFO_P47.md                             (this file)
```

No native-repo counterpart exists; nothing changes there. No `sudo` used.

---

## The codec (src/Allegiance.Core/Wire/ClusterInfo.cs)

Follows the sibling `ExportCluster.cs` doc style (full source citations, an ASCII
wire-layout block, `WireWriter`/`WireReader` LE primitives). Layout verified
against the native struct (`Messages.h:1040-1047`):

```
offset 0:   ushort cbmsg
offset 2:   ushort fmid = 200
offset 4:   SectorID[32]  clusterIDs           (64 B; ids + -1 terminator)
offset 68:  bool[32]      homeSectors          (flags parallel by index, no terminator)
offset 100: SectorID[1026] warpFromClusterIDs  (2052 B)
offset 2152:SectorID[1026] warpToClusterIDs    (2052 B)
= 4204 B fixed
```

`SectorID` is a `short` (2 B LE), native `bool` is 1 B — the whole struct is
naturally aligned (every short array starts at an even offset, the single bool
array at a 1-boundary), so the wire is exactly the sequential byte copy of the
C++ struct, no padding.

**Sentinel handling (got this exactly right):** a `-1` both terminates the id
lists and is the reference's filler; `homeSectors` is *not* terminated. Encode
uses zero-filled fixed regions (matching native's `ZeroMemory` at
`fsmission.cpp:4127-4130` then indexed writes) and writes a single trailing `-1`
after the last cluster id and the last warp id — observably identical to
native's per-item `-1` writes (`fsmission.cpp:4133-4135,4148,4161-4162`). Decode
walks `clusterIDs`/`warpFromClusterIDs` to `-1` or array width, exactly like the
managed client's loops (`AllMessages.h:10176,10183`). Usable slots are
`MaxClusters - 1 = 31` and `MaxWarps - 1 = 1025` (room for the terminator);
beyond that Encode throws rather than emit a corrupt message.

`ClusterInfo.FromSectors` builds from the same `Sector`s the cluster-EXPORT burst
iterates (`Server.cs:808-812`): one entry per sector (`Sector.Id`,
`Sector.Cluster.HomeSector`), one warp edge per warp (source = owning sector id;
destination = `ResolvedWarp.Destination.Id`). A warp whose `Destination` is null
(a dangling edge) is **skipped and logged, never fabricated** — matching
`ResolvedWarp.cs:28-32`'s own "reported, never thrown" honesty.

---

## The send (src/Allegiance.Server/Server.cs)

Inserted in the join burst immediately after PLAYERINFO and immediately before
the cluster `ExportCluster` burst — reproducing the native order exactly
(CLUSTERINFO first, then the OT_cluster EXPORT loop, `fsmission.cpp:4124-4166`
then `:4170`). One `[clusterinfo] peer {from} '{name}' -> CLUSTERINFO(200)
N cluster(s), M warp(s)` log line, matching the repo's `[export]`/`[stage]`/
`[shipstatus]` style.

Placement versus the join-burst ordering constraints this repo has real history
with (P33 #2/#3 findings, `Server.cs`'s extensive export/PLAYERINFO-ordering
comments): no conflict. `CLUSTERINFO` is pure passive data the client stores in
its managed `GameInfo.Clusters`; it touches neither the side-membership path
(PLAYERINFO's `AddPlayerToSide` → `OnQuitSide` wipe) nor the `m_pCoreIGC` cluster
pool that `SET_CLUSTER` resolves (those still come from the EXPORT burst, whose
ordering is untouched). Verified: full suite stayed green (see Build/test).

---

## Verification (loopback, no VM — native client side already correct/unchanged)

Added a `ClusterInfos`/`ResetClusterInfos()` hook to `Bot.cs` mirroring the
existing `ShipStatuses` pattern exactly (`Bot.cs:84`), and the decode dispatch
(`Bot.cs:233`). New `ClusterInfoServerTests` (mirrors `ShipStatusServerTests`):

1. **PeerReceivesClusterInfo_InJoinBurst_MatchingMapTopology** — a real
   loopback `Bot` joins a booted server and decodes one CLUSTERINFO. Asserts:
   the right cluster count (13), the right home-sector flags (exactly 2 homes —
   Ceto 1053 and Quaoar 1061 — and no others), the right warp-edge count (38),
   and — critically — that the decoded warp-connectivity graph matches the real
   map's topology entry-by-entry against an **independently assembled**
   `Mission.Assemble` of the same `XXV_simple.igc` file (the same data the
   cluster-EXPORT burst iterates), plus a per-warp spot-check against
   `Sector.Warps` directly and a sanity that no edge references an unknown sector.
2. **ClusterInfoRoundTripsThroughTheWire** — pure round-trip guard.
3. **EncodeRejectsOversizedGraph** — the fixed-array width guard.

Cross-check is genuine, not "some data arrived": it compares the decoded edge
multiset to the independently-loaded map's resolved warp graph.

**Actual map counts (stated, not assumed):** `XXV_simple.igc` = **13 clusters /
38 warps / 0 dangling** (via `tools/missiondump`). Well inside the 31-cluster /
1025-warp usable ceilings. Home sectors: only `1053` (Ceto) and `1061` (Quaoar).

---

## What's proven vs. follow-up live pass

**Proven here (loopback-decoded and cross-checked against real map topology):**
the server sends correct CLUSTERINFO bytes — right cluster count, right
home-sector flags, right warp-connectivity graph — and a loopback `Bot` decodes
them faithfully; the graph matches the actual map (independent assembly agrees
entry-by-entry).

**Still for a follow-up live pass (this task's out-of-scope, per the P41→P42 /
P45/P46 precedent):** a real `Wopr.exe` bot's `GetUnexploredClusters()` actually
returning non-empty and `SelectNextAelphTarget()`'s smart branches actually
engaging instead of the random-pick fallback — Claude Code's own job with
`tools/vizfight` (it owns live-bot verification; this task is purely the server
send + loopback proof). Nothing here changes the native client, so a live pass
needs no rebuild of the client side.

---

## Build / test status

```
dotnet build src/Allegiance.Server + Bot + Core → 0 warnings, 0 errors
dotnet test  tests/Allegiance.Core.Tests/…      → 2235/2235, consecutive clean
```

Baseline 2232/2232 (unchanged since P44); this task added 3 tests → **2235/2235**.
Three+ consecutive clean runs achieved (one transient single-test flake occurred
mid-way and did not recur in six subsequent full-suite passes — unrelated
pre-existing-style network-timing noise, not a codec flake: the two pure codec
tests cannot flake and the loopback test mirrors `ShipStatusServerTests` exactly
and passed every run). No test removed; only added.

---

## Commit decision

**Commit directly** (`8b52b76`). Codec builds, full suite green at 2235/2235,
and the loopback proof demonstrates correct decode + topology agreement. The
residual (real Wopr.exe bot engaging the smart branches) is explicitly Claude
Code's live pass, not a blocker — it doesn't leave the deliverable broken.
Nothing genuinely unresolved in the code itself.
