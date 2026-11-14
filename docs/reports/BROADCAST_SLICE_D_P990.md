# P990 — Broadcast slice D, step 1: where the 400-peer bytes and the stale update ages come from

Base: Claude's local master at dispatch (`97a1c19d`, worktree `allegiance-pi-p990`). Origin: P983 broadcast
telemetry (`docs/reports/BROADCAST_TELEMETRY_P983.md`) and Claude's b104 rig run
(`~/allegiance-evidence/P990/b104-telemetry.log.gz`, 1505 five-second windows, 411 peers, 3-side showcase,
master `788178ba`, telemetry ON, paired with b103 OFF).

Status: complete in the working tree, **not committed**. Flag is still `profiler.broadcastBudget`, still default
OFF, still measurement-only: **nothing about what is sent, in which order, or at what rate changed.** The only
wire-relevant change is that the diagnostic's own bookkeeping got cheaper; the P983 off/on byte-identity pins
(`Wire_transcript_is_byte_identical_and_counters_equal_transport_bytes`, `Off_mode_records_nothing_and_on_mode_allocates_nothing_extra`,
`Record_path_allocates_nothing_after_warmup`) still pass unchanged.

---

## 0. Answers in one paragraph (what Claude needs for slice D)

- **`other` (47.7 % of the rig's egress) is not a mystery lane any more, but the rig's exact split is still one
  telemetry run away.** The new per-`fmid` row shows, on the largest local fixture, that the ordered one-shot lane is
  99.9 % `SHIP_STATUS(90)` 58.2 %, `EXPORT(26)` 27.9 %, `SHIP_WARNING_UPDATE(218)` 13.8 %, `CLUSTER_WARNING_UPDATE(214)` 0.1 %.
  Every one of those is *per-recipient* (or per-recipient-per-subject), i.e. **O(P²)-shaped** — the one lane whose
  share is expected to grow with peer count, and the one that did: 8.4 % of egress at 144 peers (one sector, one side,
  P983's fixture shape) against 47.7 % at 411 peers (3 sides, 3 sectors, b104); §2.3 does the arithmetic per
  contributor. The instruction for the next rig run: `grep '^\[bb-other\]'` — it names the ids and their
  byte/message shares directly.
- **The stale ages are a metric bug, not starvation — in the direction the brief suspected, but for a different
  reason than "docked ships".** Docked/dead/parented subjects are *already* excluded from the port's candidate list
  (`Sector.GetShipUpdateCandidates`), matching native (a docked ship leaves its cluster, `Igc/shipIGC.h:421-459`), so
  those reasons count 0. The pairs the `[bb-age]` metric counts as stale are dominated by **viewers whose pose pass
  stopped running** (`viewerNotSwept`) and by **parked subjects whose record is still exact** (`stationary`); real
  starvation (`starved`) is the residual. On the rig, `neverPosed` early in a match is a joiner whose visibility
  rotation has not come round yet (`newPair`, bounded by the 2 Hz floor). The metric needs one fix, described in §3.3 —
  the report now names the reason per pair so the next rig run can be read without guessing.
- **The +0.9 ms in `step:broadcastShipsUpdate` is the per-(peer, ship) pose-stamp bookkeeping** — a
  `Dictionary<short,double>` find+store into 411 separate per-peer dictionaries (cold lines, ~2 cache misses per
  stamp) inside the same step as the heavy-frame build. Replaced with a dense stamp table: the P983 §4 method now
  measures **0.0520 → 0.029-0.032 ms/tick** on the identical workload (share of the p95 tick 0.77 % → 0.50-0.54 %),
  and at rig shape (411 peers / 3 sectors / 136 visible) the whole projection tick pays **+0.30-0.45 ms** (2.9-4.2 %,
  inside this host's noise band of one run: −1.5 %) including the once-per-window report.
- **Slice D verdict (§6): do the removal candidates first; a per-peer byte budget is worth an experiment only after
  they land, and even then only as a per-pose-record budget.** The measured curve says the bytes are per-recipient
  *redundancy* (port-only streams and per-(recipient, subject) status/exports), not a lack of a budget: a budget that
  equalises bytes cannot remove them without changing what the peer knows, while four of them can be removed or
  change-gated with no loss of information at all.

---

## 1. What was added (measurement only)

All behind the existing `profiler.broadcastBudget` flag (default off; off = a null field, one check per send site).

### 1.1 The ordered lane per wire `fmid`

The sink already knows a frame's exact bytes (`Server.BroadcastSnapshot.cs`, `PublishMemory`/`PublishBytes`). P990
adds `BroadcastTelemetry.RecordSendFrame(...)`, which — for the ordered one-shot lane only (category `Other`, key 0) —
reads the frame's own `cbmsg/fmid` headers (`Inc/messagecore.h:76-87`, walked exactly as
`FedMessaging::PfmGetNext` walks a packet, `_Utility/Messages.cpp:495-515`) and books each message to its id. The
trailing CRC is booked to the frame's first id, so per-id bytes always add up to the frame's length; a frame with no
readable message lands in slot 0 ("unparsed"). 1024 slots covers every `Fmid` constant in
`src/Allegiance.Core/Wire` (1..996); the last slot is the overflow bucket.

Same treatment for the coalescing lane: every frame booked to `ReliableKeyed` is also attributed to its coalesce-key
family (keys 1..13 from `Server.Broadcast.cs`, plus an overflow slot), so `ownPose`, `posBatch`, `stations`,
`asteroids`, `probes`, `missiles`, `lock`, `track`, `mining`, `cloak` are separable without touching a send site.

Report rows (one block per profiler window):

```
[bb-lane]   ordered=bytes=… msgs=… ids=4 attributedBytes=… pct=8.4 keyed=bytes=… msgs=… pct=8.0
[bb-other]  fmid=90(SHIPSTATUS) bytes=… msgs=… pct=… | fmid=26(EXPORT…) … | fmid=218(SHIPWARNINGUPDATE) … | rest=…
[bb-keyed]  key=12:ownPose(78 own ship, guaranteed) bytes=… msgs=… pct=… | key=9:ownMining(206) … | key=8:missileTrack(208) …
```

plus one `bb-other` CSV row per id and one `bb-keyed` row per family when `profiler.csvPath` is set.

### 1.2 The stale-age reason counters

`[bb-age]` keeps P983's exact shape and math (`visiblePairs`, `ships`, `neverPosed`, binned p50/p95/max, `older1s`,
`older3s`). Two `[bb-age-why]` rows classify every pair the metric counts:

```
[bb-age-why] older3s=4 docked=1 dead=0 lifepod=1 parented=0 otherSector=0 viewerNotSwept=0 stationary=1 starved=1
             subjectUnknown=0 newPair=0 unserved=0
[bb-age-why] neverPosed=16 docked=0 dead=0 lifepod=2 parented=0 otherSector=0 viewerNotSwept=0 stationary=0
             starved=0 subjectUnknown=0 newPair=14 unserved=0
```

The reasons, in classification order (see `StaleReason` in `src/Allegiance.Server/Server.BroadcastTelemetry.cs`):

| Reason | Meaning | Native rule |
|---|---|---|
| `docked` | subject has a station (`StationId != null`) | a docked ship leaves its cluster's ship list (`Igc/shipIGC.h:421-459`, `SetStation` → `AddShip` "sets the ship's cluster to NULL"), and only the cluster's own list is walked (`FedSrv/FedSrv.CPP:3728-3732`) |
| `dead` | subject destroyed / awaiting respawn (`Ship.Dead`) | same: not in the cluster, never in `rgShips` |
| `lifepod` | subject hull carries `c_habmLifepod` (a pod / observer hull) | pods/observers are their own hull (`c_habmLifepod`, `Igc/igc.h:728`); a stale stamp on one is a leftover, not a live pair |
| `parented` | subject is a turret/observer child (`ParentShip != null`) | child ships go to the turret lanes, never `rgShips` (`FedSrv.CPP:3779-3791`) |
| `otherSector` | subject sits in another sector than the viewer | the update list is per cluster (`FedSrv.CPP:3728`), so a cross-sector pair is never posed |
| `viewerNotSwept` | the viewer's own pose pass has not run for > 3 s | a player that is not flying gets no per-player heavy send at all (`FedSrv.CPP:4180-4189`: heavy is sent only while `GetCluster() != NULL`) |
| `stationary` | subject has not moved since its stamp and is not moving now — the record the peer holds is still exact | native re-sends such a ship on its deviation rank anyway, so this is "wasted opportunity", not "missing information" |
| `starved` | visible, flying, in-sector, moving, viewer swept — really not re-posed for > 3 s | native owes it a heavy record within the deviation rotation (`FedSrv.CPP:3923-3950`) |
| `subjectUnknown` | tracked id that no live mission owns (retired world / reused id) | — (metric artefact) |
| `newPair` | never posed, but the viewer's visibility set is < 3 s old | the rotation has not come round yet; bounded by the floor/rank period |
| `unserved` | never posed although the viewer has been swept for > 3 s | a real gap (should always be 0) |

`[bb-top]` grew one field, `sweptAgoMs=`, so an outlier peer can be read at a glance.

### 1.3 The ON path made cheaper

- The per-peer pose stamps are now a **dense table + small hash index** (`PoseStampTable`): insertion-ordered
  `Entry` records (so the report's per-window walk touches only occupied slots) and a 4-byte-per-slot `int[]` index
  (`denseIndex + 1`, 0 = empty). One probe + one struct write per stamp; no comparer, no boxed enumerator, no
  resize churn. A whole tick's stamp sweep is one call (`MarkPoses(list, simMs)`) so the index array, mask and shift
  load once, not per ship. Each stamp also keeps the subject's position at send time, which is what makes the
  `stationary` reason possible.
- The report itself no longer pays a `Dictionary` lookup per (peer, ship): it walks the viewer's *tracked* set and
  probes the pose table once per pair (the missing ones fall out as `neverPosed` for free). Warm cost at 411 peers /
  55 896 stamps: **9.8 ms → 3.2-3.4 ms** per five-second window (144 peers / 20 592 stamps: 3.5 → 1.4 ms).
- Unchanged: off is a null field and a single check; the wire is byte-identical (P983 pins).

---

## 2. The lane breakdown (fixture numbers and how they map to the rig)

Fixture: the largest local loopback showcase (`Loopback_showcase_breaks_the_ordered_and_keyed_lanes_down`, Live tier) —
144 seated peers, one sector, 144 ships all on one side plus a probe and a mine, 400 recorded ticks of the full
broadcast suffix, diagnostic ON with the real wall clock, native ranked path plus the rig baseline
(`immutableSnapshot`+`encodeOnce`, pool transport, `shipUpdateModel=native`). Numbers are that run's window:

(`[broadcast-budget]` and `[bb-trend]`/`[bb-queue]`/`[bb-top]` elided; the category total is the sum of the five
category bytes below, 59 409 926.)

```
[bb-category] bytes=heavy=48063246 light=1612800 keyed=4723200 chat=0 other=5010680 pct=80.9/2.7/8.0/0.0/8.4
[bb-lane] ordered=bytes=5010680 msgs=182594 ids=4 attributedBytes=5010680 pct=8.4 keyed=bytes=4723200 msgs=172800 pct=8.0
[bb-other] fmid=90(SHIPSTATUS) bytes=2915192 msgs=104114 pct=58.2
         | fmid=26(EXPORTASTEROID/…/EXPORTSHIP/…) bytes=1400256 msgs=20592 pct=27.9
         | fmid=218(SHIPWARNINGUPDATE) bytes=691200 msgs=57600 pct=13.8
         | fmid=214(CLUSTERWARNINGUPDATE) bytes=4032 msgs=288 pct=0.1
[bb-keyed] key=12:ownPose(78 own ship, guaranteed) bytes=3456000 msgs=57600 pct=73.2
         | key=9:ownMining(206) bytes=691200 msgs=57600 pct=14.6
         | key=8:missileTrack(208) bytes=576000 msgs=57600 pct=12.2
[bb-age] visiblePairs=20592 ships=20592 neverPosed=0 p50=200ms p95=400ms max=450ms older1s=0 older3s=0
[bb-age-why] older3s=0 …(all zero)…   [bb-age-why] neverPosed=0 …(all zero)…
[p990-report] peers=144 stamps=20592 reportMsWarm=1.43
```

### 2.1 Ordered lane (`other`) contributors

| Wire id | Our send site | Frames per peer per tick (fixture) | Native's send rule | Class |
|---|---|---|---|---|
| `SHIP_STATUS(90)` 58.2 % | own status `Server.Broadcast.cs:866`; same-side teammate status `:1026`; cross-side status `:2532` (`PackStatus`/`PackCrossSideStatus`) | 1.8 | native sends a ship's status to a side **only when it changed** (`FedSrv/FedSrv.CPP:3672-3702`: per side, `if (*pss != *pssOld)` then send and update the old copy); the all-ships dump in `FedSrv/fsmission.cpp:4303-4323` is `CFSMission::SendMissionInfo` (`:4108`), i.e. once at join. Ours adds a **periodic refresh** (own 1/40 ticks, teammate 1/100, cross-side 1/100) on top of the edge-triggered sends | edges native; **periodic refresh is port-only redundancy** (Claude review: corrected from "native volume, port-throttled") |
| `EXPORT(26)` 27.9 % | teammate export `Server.Broadcast.cs:1000`, loadout re-export `:1134`, same-side NPC `:1176`; cross-side sweeps `:2156`, `Server.Combat.cs` | 0.36 (one-shot per pair) | native **never** exports an `OT_ship` to another client (grep `ExportObj(` in `FedSrv`: only statics/sides/stations); native creates a ship from `PLAYERINFO` (`clintlib/appmsg.cpp:2264`) | **port-only redundancy** |
| `SHIP_WARNING_UPDATE(218)` 13.8 % | `Server.ShipWarnings.cs:17` — one frame per peer per tick, **unconditional** | 1.0 | native has no such message; HUD warning state is derived client-side | **port-only redundancy** (change-gate-able) |
| `CLUSTER_WARNING_UPDATE(214)` 0.1 % | `BroadcastSectorThreats` (`Server.Broadcast.cs:3793`), change-driven | ~0.005 | native computes sector threat client-side; the port streams it because the renderer-only client has no static core | port-only extension, already change-gated |

### 2.2 Coalescing lane (`keyed`) contributors

| Family | Our send site | Frames per peer per tick (fixture) | Native | Class |
|---|---|---|---|---|
| `ownPose` (`SINGLE_SHIP_UPDATE(78)`, guaranteed, key 12) 73.2 % | `Server.Broadcast.cs:846` (`BroadcastShipStates`), `PackOwnPose` | 1.0 (only when dirty) | native sends a ship's single-ship update to the sector-flying group `FM_GUARANTEED` when it enters a cluster (`FedSrv/fsship.cpp:511-548`); the continuous per-tick own-pose stream is this port's choice because the bots steer from it (P608) | native-equivalent, port cadence |
| `ownMining(206)` 14.6 % | `Server.Broadcast.cs:3662` — one per peer per tick, **unconditional**, NA/NA when not mining | 1.0 | native transmits no rock id (the client infers it) | **port-only redundancy** (change-gate-able) |
| `missileTrack(208)` 12.2 % | `Server.Broadcast.cs:3638` — one per peer per tick, **unconditional** | 1.0 | native's client walks its own IGC sim for the incoming-missile warning (`WinTrek/trekigc.cpp:5025-5071`) | **port-only redundancy** (change-gate-able) |
| `ownCloak(220)`, `stations(57)`, `asteroids(179)`, `probes(92)`, `missiles(205)`, `lock(204)`, `shipStates` | `:3679`, `:2703`, `:2883`, `:2969`, `:3734`, `:3578`, `:1143` | 0 in this fixture (see caveat) | cloak is a continuous ramp (`Igc/cloakigc.cpp:121`), stations/asteroids/probes are native per-cluster-per-side batches (`fsship.cpp:817-915`) | native, with port-only cloak/track additions |
| — | — | — | — | — |

**Fixture caveat (honest):** the showcase fixture seeds peers directly (`TestSeedPeers`) instead of running a logon,
so the per-side `stations/asteroids/probes` syncs never fire there (they are join-triggered plus a 10-tick cadence) and
same-side teammate *position batches* do not exist in native mode (P604). The rig's `keyed` therefore carries more
families than this table shows; the row is what names them.

### 2.3 How the fixture maps to the rig's shares

Rig b104, whole run (1505 windows, all five categories summed, 411 peers):

| | heavy | light | keyed | chat | other |
|---|---|---|---|---|---|
| bytes | 34.48 GB | 1.38 GB | 24.05 GB | 0 | 54.74 GB |
| share | 30.08 % | 1.20 % | 20.98 % | 0 % | **47.75 %** |
| per peer per 5 s window | 55.7 KB | 2.2 KB | 38.9 KB | 0 | **88.4 KB** |

Same table for the 144-peer loopback fixture (§2): heavy 80.9 %, light 2.7 %, keyed 8.0 %, other 8.4 %. The whole
difference is the peer count and the sector layout, and it is explained by *which* contributors are O(P²):

| Contributor | Shape | At 144 peers (1 sector) | At 411 peers (3 sectors, 136 visible) |
|---|---|---|---|
| `SHIP_STATUS(90)` teammate refresh | P recipients × (P/3) mates / 100 ticks | 206 frames/tick | **555 frames/tick** |
| `SHIP_STATUS(90)` cross-side catch-up | P recipients × (2P/3) seen enemies / 100 ticks | 0 (one side) | **1 126 frames/tick** |
| `SHIP_STATUS(90)` edge (spotted/hidden, Detected flip) | event × recipients-of-that-side | small | grows with target churn |
| `EXPORT(26)` one-shots | per (recipient, subject) once, re-armed by launches/loadouts/warps | 20 592 total over the run | scales with bots dying/launching per window |
| `SHIP_WARNING_UPDATE(218)` | **1 per peer per tick** | 144 frames/tick | **411 frames/tick** |
| `ownMining(206)` / `missileTrack(208)` | 1 per peer per tick each | 288 frames/tick | **822 frames/tick** |

Local ordered-lane arithmetic at 411 peers: the three `SHIP_STATUS` rows alone are ≈ 1 700 frames/tick; add the
per-peer per-tick streams and the one-shot exports and the lane is in the right order of magnitude to be the rig's
~24 ordered frames per peer per tick (88.4 KB / ≈52 B per frame). **What is measured vs inferred:** the per-id
*shares* above are measured on the fixture; the rig's per-id split is inferred from the send shapes and the lane's
measured superlinear growth (2.2 ordered frames/peer/tick at 11 peers early in b104 → ~24 at 411). The new
`[bb-other]` row settles it on the next rig run; that is the one thing this step could not answer from this host.

---

## 3. The stale ages

### 3.1 What the rig's numbers look like

From `b104-telemetry.log.gz` (parsed, not re-quoted by hand): `[bb-age]` p50 100-250 ms, p95 400-500 ms, but
**768 of 1357** non-empty windows report `older3s > 0` (up to ~900 pairs), the run maximum is **1 241 338 ms** (20 min),
and `neverPosed > 0` appears in 239 of 15 030 `[bb-top]` rows, always early in a match. 355 of 406 top peers appear
with a `max > 3 s` at least once; in the worst rows *all* of a peer's stamped ships carry the same ~1 002 s age
(e.g. `peer=283 p50=1002295ms p95=1002295ms max=1002295ms ageShips=121 neverPosed=1`), which is one heavy frame's
worth of stamps that was never refreshed again.

### 3.2 What the classification says

The reason table in §1.2 is the answer, and the *pattern* in the log pins the two cases:

- **A whole peer's stamps frozen at one older age (`sweptAgoMs` ≫ 0)** = `viewerNotSwept`: the peer's own pose pass
  stopped running (it stopped flying: docked, awaiting a new ship, a ghost out of lives, or its mission left
  `Started`). It keeps the `_viewerPoseCadence` set it had at that moment, so the age metric keeps counting its old
  pairs. Nothing is starved; the pair is not a live visible pair any more. This is the dominant `max` blow-up.
- **A few ships per peer at 3-17 s while p50/p95 are fine** = `stationary` (a parked miner/bomber whose record is
  still exact) or `starved` (a ship that really lost its turn). In the fixture the stationary case is the common one;
  the ranked path re-poses every visible ship every `ceil(population/30)` ticks plus the 2 Hz floor, so a *moving*
  visible ship cannot legitimately exceed ~1 s at the rig's 136 visible ships — `starved` should be a small residual
  and is now countable.
- **`neverPosed` early in a match** = `newPair`: a joiner's visibility set exists from its first sweep, while the
  heavy rotation (target + `ceil(n/30)`) plus the floor take a few ticks to cover everyone. Bounded, not starvation.

### 3.3 Does the metric count pairs native would never update? Yes — two of them

1. **A viewer that is not flying.** Native only sends the per-player heavy message while the player's ship is in a
   cluster (`FedSrv/FedSrv.CPP:4180-4189`), and only poses ships from `pcluster->GetShips()` filtered by
   `GetFlyingPlayers()` (`:3728-3732`). Our `[bb-age]` counts every pair the *viewer's cadence* still holds, which
   outlives the flying state. Fix (not done here — see §6.4): skip pairs of a peer whose last sweep is older than the
   window, or drop the cadence set when a peer stops flying.
2. **A subject whose record is still exact.** A parked ship that has not moved since its stamp is not "stale" in any
   way that matters: the peer's copy is correct. The metric should exclude `stationary` from the stale count (or
   report it separately, which the `[bb-age-why]` row now does).

Docked/dead/lifepod/parented/other-sector subjects are **not** counted (they are not in the candidate list), which is
the correct behaviour and is now provable from the counters rather than asserted.

**Is any of it real starvation?** No evidence of it: with the port's ranked budget plus the 2 Hz floor every visible
moving ship is re-posed well inside the 1.5 s dead-reckoning window (`Igc/shipIGC.cpp:3108-3115`), which is what
p50/p95 (200/450 ms) show. So this step **stops at "metric fix + counters"** as the brief instructs, and no
reproducer for starvation is filed.

---

## 4. Overhead before/after

P1009 follow-up: the absolute 30 ns / 3% limits below failed on the original
commit on another host. The warmed, hardware-relative guard and full comparison
are documented in [P990_OVERHEAD_P1009.md](P990_OVERHEAD_P1009.md); the original
measurements below remain historical evidence.

Method: exactly P983 §4 — the largest local fixture (144 seated peers, one sector, native ranked path, rig baseline
flags), the diagnostic's own per-tick workload timed directly against the measured projection p95 (so a noisy
whole-tick wall-clock A/B, whose floor on this shared host is ±3 %, cannot hide a cost). Host: 24 vCPU shared,
load average 8-25 during these runs (other lanes), which is why the numbers are ranges over three runs.

| Arm | Workload | Before (P983 code) | After (P990) |
|---|---|---|---|
| P983 §4 gate (`Enabled_overhead_stays_under_one_percent_of_the_p95_tick`) | 144 peers × (8 keyed frames + 6 pose stamps) = 14 calls/peer | **0.0520 ms/tick**, 0.770 % of a 6.75 ms p95 (P983's own report: 0.043-0.048 ms) | **0.0290 / 0.0306 / 0.0319 ms/tick**, 0.500 / 0.535 / 0.541 % of a 5.7-5.9 ms p95 |
| New mixed-lane gate (`Mixed_lane_overhead_stays_within_30ns_per_call_and_three_percent_of_the_p95_tick`) | 144 peers × (1 heavy + 24 ordered + 8 keyed frames + 16 stamps) = 49 calls/peer, the rig's real mix | 0.1765 / 0.1848 / 0.1962 ms/tick (P990 counters with the *dictionary* stamp path), 25-28 ns/call | **0.1470 / 0.1564 / 0.1582 ms/tick**, 20.8-22.4 ns/call, 2.5-2.7 % of a 5.9 ms p95 |
| Rig-scale projection (`Rig_scale_projection_shows_what_the_diagnostic_adds_per_tick`) | 411 peers, 3 sectors, 136 visible/peer, 55 896 stamps, 2 937 frames/tick; OFF/ON blocks alternate | not measurable on this tree before the change (dictionary path) | **+0.30 / +0.45 / −0.17 ms per tick** (Δmean, median +0.30 ms = 2.9 %; ratio median 1.029; one run at −1.5 % = this host's noise floor) |
| Report cost, warm | once per five-second window, on the sim thread | 9.8 ms at 411 peers (56 k stamps) / 3.5 ms at 144 peers | **3.1-3.4 ms** at 411 peers / **1.4 ms** at 144 peers |

Per-arm attribution of the mixed workload (144 peers, ms/tick): heavy 0.0044, ordered ×24 0.090-0.095,
keyed ×8 0.027-0.030, stamps ×16 0.026-0.032. So at the rig's real per-peer frame count the *ordered-lane parse* is
the largest single piece (≈29 ns/frame), the stamps next (≈13 ns/stamp), and the heavy split point cheapest.

**Where the b104 +0.9 ms came from** (`step:broadcastShipsUpdate` is the step that builds and queues the heavy frame,
`Server.Broadcast.cs:470`): that step contains *only* the pose-stamp loop of the diagnostic. At the rig's ~19 stamps
per peer per tick (411 peers, 136 visible each, 2 Hz floor + deviation rank) the old `Dictionary<short,double>` write
cost ~120 ns per stamp (two cold cache lines per peer dictionary), i.e. ≈ 0.9 ms/tick — the measured value. The dense
table takes that to ~13 ns warm / ~20-25 ns cold: the same loop now costs ≈ 0.15-0.2 ms/tick at rig shape, and the
measured whole-projection delta is +0.30-0.45 ms including the once-per-window report (3.3 ms / 100 ticks ≈ 0.03 ms).

**Why the 1 %-of-p95 bound is not reachable at 144 peers for the real mix (the honest bound asked for):** the
diagnostic's work is exactly "one pass per frame and per pose stamp the transport already handles", so its cost is
`calls × ns/call`, not a share of one fixture's p95. P983's 1 % was measured on 14 calls/peer; the rig's real mix is
~50 calls/peer, i.e. ~7 200 calls per 144-peer tick, so at the ~20 ns/call this path measures the expectation is
~0.15 ms = ~2.5 % of a ~6 ms tick. The gate is therefore **two-part and shape-independent**:
`perCallNs ≤ 30` (measured 20.8-22.4; leaves ~35 % headroom for this host's load while still failing a regression back
to the 25-28 ns/call dictionary path) plus `costMs ≤ 3 % of the measured p95` (the derived 2.5 % with margin). The
rig-scale Perf test additionally gates the whole-projection delta at ≤ 1.5 ms per 411-peer tick. Nothing was loosened
silently: the 1 % figure is *superseded* by these two, and the reasoning is in the test's comment and here.

---

## 5. Everything that changed

| File:line | Change |
|---|---|
| `src/Allegiance.Server/BroadcastTelemetry.cs:75` | new `PoseStampTable` (dense entries + `int[]` index): `Set` `:131`, `SetMany` `:167`, `TryGet`, `Contains`, `Remove`, `RemoveExcept` `:235`, enumerator `:248` |
| `src/Allegiance.Server/BroadcastTelemetry.cs:333` | `BroadcastPeerTelemetry.MarkPoses(list, ms)` (batch stamp) and `MarkPose(in Ship, ms)`; `LastPoses` replaces P983's `LastPoseMs`; `Sweeps`/`LastSweepMs`/`FirstSweepMs` |
| `src/Allegiance.Server/BroadcastTelemetry.cs:533` | ordered-lane / keyed-family rings (`FmidSlots` = 1024, `KeyedFamilies` = 17) with `OrderedLaneWindowBytes/Messages` `:533`, `KeyedLaneWindowBytes/Messages` `:538` |
| `src/Allegiance.Server/BroadcastTelemetry.cs:567` | `RecordSendFrame` → `RecordSendCore` → `BookOrderedFrame` `:611` (per-`fmid` attribution off the frame's own headers) |
| `src/Allegiance.Server/BroadcastTelemetry.cs:659` | `MarkSweep(id, simMs)` — the viewer-swept stamp the age classification needs |
| `src/Allegiance.Server/Server.BroadcastTelemetry.cs:18` | `StaleReason`, `StaleReasonNames` `:59`, `AgeSubject` `:73`, `AgeViewer` `:77` |
| `src/Allegiance.Server/Server.BroadcastTelemetry.cs:245` | `AddLaneReport` (`[bb-lane]`/`[bb-other]`/`[bb-keyed]`, called from `:190`), `FillLaneTables` `:303` (also feeds the CSV rows) |
| `src/Allegiance.Server/Server.BroadcastTelemetry.cs:335` | `AddAgeReport` — P983 line unchanged, two new `[bb-age-why]` rows, tracked-set walk |
| `src/Allegiance.Server/Server.BroadcastTelemetry.cs:424` | `ClassifyStale` / `ClassifyNeverPosed` `:441` (pure, unit-tested), `SubjectState`/`ViewerState`, `BroadcastSubjectLookup` |
| `src/Allegiance.Server/Server.BroadcastTelemetry.cs:227` | `sweptAgoMs=` added to `[bb-top]` `:231` |
| `src/Allegiance.Server/Server.BroadcastTelemetry.cs:472` | `MessageName(fmid)` — the wire-id → message-name table for `[bb-other]`/CSV |
| `src/Allegiance.Server/Server.BroadcastSnapshot.cs:278` | `PublishMemory` books the frame span (`RecordSendFrame`); `PublishBytes` at `:300` — the byte path is otherwise untouched |
| `src/Allegiance.Server/Server.RankedPose.cs:41` | `MarkSweep` once per peer per tick; the heavy stamp loop becomes one `MarkPoses(heavy, SimNowMs)` at `:139` |
| `src/Allegiance.Server/Server.InterestTiers.cs:26` | `MarkSweep`; the tier stamp loop now carries the subject position |
| `src/Allegiance.Server/Server.Broadcast.cs:3203` | `MarkSweep` in the legacy port builder beside its `cadence?.Begin` (stamps themselves unchanged) |
| `src/Allegiance.Server/Server.UpdateCadence.cs:174` | `ViewerPoseCadence.TrackedShipIds()` (the report walks the tracked set once) |
| `tests/Allegiance.Core.Tests/P990BroadcastLaneTelemetryTests.cs` | new: lane reconciliation (per-fmid / per-family vs the P983 totals), rolling window, reason classification (unit + loopback fixture with a docked ship, a lifepod, a parked and a moving subject), reason sums, zero-allocation pin for the new record/stamp paths, `PoseStampTable` contract vs a `Dictionary` (4 000 randomised ops incl. retire), the 144-peer showcase, the mixed-lane Perf gate, the 411-peer rig-scale Perf measurement |
| `GLOSSARY.md` | the P983 telemetry entry now names the P990 lane/reason rows and files |
| `docs/reports/BROADCAST_TELEMETRY_P983.md` | one pointer line to this report (the rows it documents are a subset of the current block) |
| `docs/TEST_FLAKES.md` | the P990 Fast-tier triage section (both sweep cohorts, isolated re-runs, showcase 5/5) |
| `docs/reports/BROADCAST_SLICE_D_P990.md` | this report |

No parity CSV row: nothing about what the server sends changed, so there is no behaviour to score. The native rules
this report cites are referenced from the classification and the analysis, not ported.

---

## 6. The slice-D decision (report section, no code)

### 6.1 Is a per-peer byte budget (P978 #9) still worth an experiment?

**Not yet, and not as the first move.** P983 §7's decision list asked four questions; the rig answered them:

1. *heavy > 70 % at 394 peers?* **No — 30 %.** The byte-budget unit cannot be a message (heavy messages are the
   cheapest bytes per information); it would have to be a per-pose-record budget.
2. *keyed large?* **21 %, and 73 % of it is the guaranteed own-pose stream** plus port-only per-tick mining/track
   streams. A budget that charges keyed frames would start shaving the owner's own pose — the one stream P608 exists
   to protect.
3. *age p95 vs 1.5 s?* 400-500 ms, i.e. dead-reckoning has ~1 s of headroom; p95 age is not the constraint.
4. *outlier peers / neverPosed?* present, but §3 shows they are `viewerNotSwept` + `newPair`, i.e. metric artefacts,
   not starved peers.

Meanwhile the composition (§2) says the 47.7 % `other` lane is *per-recipient redundancy*, which a budget cannot
remove without removing information: a budget only decides **when** to stop telling a peer about something it is
currently entitled to know (its teammate's status, its own warning mask), and the cheap wins here are instead
**changes that remove or gate frames that carry no new information at all**.

### 6.2 If the experiment does happen later: its shape

- **Charges**: per *pose record* (one heavy entry), not per message. Light/presence records charge a small fixed
  amount (native's light budget is a separate 2-record quota, `FedSrv.CPP:3844-3845`). Ordered-lane frames are
  charged **outside** the budget but counted (they are one-shot, and starving a `SHIP_STATUS`/`EXPORT` breaks object
  creation — `appmsg.cpp:80-105`).
- **Lanes it must cover**: heavy + light **and** the keyed streams that can be traded against them (posBatch, turret
  relays), or the budget just pushes bytes from heavy into keyed. `other` should be *reported* against the budget but
  not enforced by it.
- **Reservations (never charged away)**: the viewer's own pose (`CoalesceOwnShip`, guaranteed, P608), the target
  (native promotes it ahead of the quota, `FedSrv.CPP:4141-4146`), the source/parent (P891), any guaranteed
  frame, and the turret relay lanes.
- **Rig A/B metric list** (P983 §7 + what the brief adds): total egress B/s and per-peer B/s; tick-wall p50/p95/p99;
  `step:broadcastShipsUpdate`; update-age p50/p95/p99 and per-peer max; `neverPosed` and the new `[bb-age-why]` reason
  counters (`starved` must not rise); correction distance / `[ctrlak]` rejected-correction counts; bot combat outcome
  (kills/deaths per bot, flag captures) so a byte win that costs the bots the match is visible; `droppedStreamFrames`
  and `coalescedSnapshots` (a shed that does not raise age is not a saving).
- **Expected size of the win**: bounded by the *shares* — heavy 30 % + light 1.2 % of egress, and the budget cannot
  remove more than the fraction that is genuinely redundant per peer. Given that p95 age has ~1 s of headroom and the
  client dead-reckons ±1.5 s (`Igc/shipIGC.cpp:3108-3115`), a plausible target is 10-20 % of heavy+light ≈ **3-6 % of
  total egress** at the cost of a measurably worse age tail. That is *smaller* than the removal candidates below, which
  is the argument for doing them first.

### 6.3 The removal candidates found by this step (each with its bytes)

Measured on the local fixtures / derived from the rig shares; each is a **separate task**, none is fixed here.

0. **`SHIP_STATUS(90)` periodic refresh → native change-gate (added in Claude's review).** Native only re-sends a status when it differs from the copy last sent to that side (`FedSrv/FedSrv.CPP:3672-3702`). The port's teammate (1/100) and cross-side (1/100) refreshes are the O(P²) rows of §2.3: ≈1 700 frames/tick at 411 peers, the largest single piece of the rig's `other`. Change-gating them per (side, subject) keeps the edges and removes the steady-state refresh; check first which client/bot paths rely on the refresh as a repair for a missed edge (ordered lane, so a missed edge should not exist).
1. **`SHIP_WARNING_UPDATE(218)`: send on change, not every tick.** `src/Allegiance.Server/Server.ShipWarnings.cs:17`
   sends one 18-byte frame (14 + CRC) to every peer every tick, unconditionally. Rig: 411 frames/tick ≈ 7.4 KB/tick ≈
   148 KB/s ≈ **0.74 MB per 5 s window = ~2 % of `other`** (7.4 kB/s per peer at 411 peers). Fixture: 100 % of
   `SHIP_WARNING` (13.8 % of `other`). Change-gating (per-peer last-sent mask, one comparison per tick) removes it in
   steady state and loses nothing: the mask is already the ship's `WarningMask`.
2. **`OwnShipMiningUpdate(206)` + `MissileTrackWarningUpdate(208)`: send on change.** `Server.Broadcast.cs:3662` and
   `:3638` each send one frame per peer per tick unconditionally (16 B and 24 B + CRC). Rig: 822 frames/tick ≈
   **16 KB/tick ≈ 320 KB/s ≈ 1.6 MB per 5 s window = ~4.4 % of `other`**, and they are 27 % of the fixture's keyed
   bytes. Both have a natural change key (the rock id; the tracked/lock pair) and a "none" value already on the wire.
3. **Teammate `EXPORT(26)` (P62) instead of native's `PLAYERINFO`-creates-the-ship rule.**
   `Server.Broadcast.cs:1000`, `:1134`, `:1176`: ~100-200 B per (recipient, teammate) instead of native's ~40-byte
   `PLAYERINFO` that creates the ship anyway (`clintlib/appmsg.cpp:2264`). Fixture: **27.9 % of `other`** (1.4 MB per
   window); rig: a share of the 47.7 % that the next run will size. This is the biggest single redundancy and the one
   that needs the most care (the port's object model learns hull/parts from it).
4. **Report hitch (telemetry-only, not a wire change):** the 3.3 ms report at 411 peers runs on the sim thread inside
   `TickProfiler.WindowReported`; moving it to a worker (or the existing CSV/formatting work) removes a 3.3 ms hitch
   once per five seconds. Cheap, and it also removes the last non-per-tick share of the ON cost.

### 6.4 The metric fix (do this first — it is what makes the next rig run readable)

`[bb-age]` should not count a pair whose viewer is not being swept, nor a subject whose record is still exact
(§3.3). Both are now *named* by `[bb-age-why]`; the follow-up task is to exclude them from the percentile/older3s math
(and from `[bb-top]`'s per-peer ages) while keeping the raw counters, so `p95 age` and `older3s` mean "stale while it
mattered". That is a metric change with P983 test pins to update, so it is deliberately not done in this diagnosis step.

---

## 7. Tests and gate

New tests (all in `tests/Allegiance.Core.Tests/P990BroadcastLaneTelemetryTests.cs`):

| # | Test | Tier |
|---|---|---|
| 1 | `Ordered_lane_frames_are_attributed_per_fmid_and_reconcile_exactly` (incl. a two-message packet) | Fast |
| 2 | `Keyed_frames_are_attributed_per_family_and_reconcile_exactly` | Fast |
| 3 | `Lane_attribution_rolls_with_the_five_second_window` | Fast |
| 4 | `Stale_reason_classification_covers_docked_lifepod_dead_parented_and_moving_subjects` (7 cases) | Fast |
| 5 | `Stale_reason_classification_separates_a_parked_subject_from_real_starvation` | Fast |
| 6 | `Never_posed_pairs_separate_a_new_viewer_from_a_tracked_but_unserved_pair` | Fast |
| 7 | `Lane_record_and_stamp_paths_allocate_nothing_after_warmup` | Fast |
| 8 | `Pose_stamp_table_matches_a_dictionary_under_arbitrary_insert_update_and_remove` | Fast |
| 9 | `Fmid_names_read_as_messages_not_numbers` | Fast |
| 10 | `Age_reasons_name_a_docked_lifepod_stationary_and_moving_subject` (loopback fixture) | Fast (artwork) |
| 11 | `Loopback_showcase_breaks_the_ordered_and_keyed_lanes_down` (144 peers, 400 ticks) | Live |
| 12 | `Mixed_lane_overhead_stays_within_30ns_per_call_and_three_percent_of_the_p95_tick` | Perf |
| 13 | `Rig_scale_projection_shows_what_the_diagnostic_adds_per_tick` (411 peers) | Perf |

P983's own `BroadcastTelemetry*` tests (11 unit + 7 fixture, incl. the wire-identity and zero-allocation pins and
`P983BroadcastTelemetryRotationTests`) are unmodified and stay green.

**Test counts (P990/P983 files, each run alone, final tree):** 19/0/0 (new `P990BroadcastLane*`), 18/0/0 (P983
`BroadcastTelemetry*`), 1/0/0 (P983 rotation Live test) — 3 runs each, Passed/Failed/Skipped, **0 skipped** (artwork
present via the worktree's gitignored `local.env`). The MonoGame project's 3 `NotExecuted` cases are the pre-existing
xunit skips (identical in every cohort).

### 7.1 Gate results

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings, 0 errors** |
| `tools/build-tools.sh` (with the same workaround via a `DOTNET` wrapper) | **all 129 tool projects built**, 0 warnings, exit 0 |
| AOT publish (`Allegiance.Server`, `linux-x64`, `PublishAot=true`) | **succeeded**, 0 IL2026/IL3050, 17,476,976-byte native binary |
| new tests, 3× alone | **19/19 passed each run**, 0 skipped (plus P983's 18/18 and the rotation Live test 1/1, 3× each) |
| `tools/run-tests.sh --tier fast` (one run) | 13,898 tests, **573.8 s** — 3 failures, all ledgered and 3/3 alone (see below) |
| showcase never-parks, 5× vs the base rate (5/0/0 recorded in P983) | **5 passed / 0 failed / 0 skipped** |
| `tools/check-glossary-links.py` | **OK** (735 distinct paths) |
| `grep -c $'\r'` on the touched docs | 0 (LF only) |

### 7.2 Fast-tier result

Two full Fast invocations on this host (the first before the final cosmetic simplification, the second on the final
tree):

- **Run 1** — 13,898 tests, 598.3 s: failures `LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores`
  (3/3 alone), `P739TimerLiveTests.Warp_bomb_notice…(delay: 7)` (3/3 alone), `LiveSideSelectTests.Flag_on_auto_seats…`
  (2 pass / 1 fail alone — ledgered clean-base rate 2 pass / 3 fail), `MissileLockLiveTests…` (2 pass / 1 fail alone,
  the ledgered 38 s partial-tip signature), plus 6 slow-Fast offenders (the ledgered near-limit families).
- **Run 2 (final tree, quiet host)** — 13,898 tests, 573.8 s: `MissionRotationLiveTests.Finished_mission_rotates…`
  (3/3 alone; ledgered ~12 % receipt-order intermittent), `LiveCombatWireTests.Moving_target_streams…` (3/3 alone;
  ledgered receipt order), `MissileLockLiveTests…` (pass / fail / pass alone; ledgered OPEN partial-tip family), plus
  4 slow-Fast offenders (same families).

Neither run failed a test that touches this task, and every non-task failure is either already in
`docs/TEST_FLAKES.md` with the same signature or passed 3/3 alone (both cohorts are recorded there under
"P990 (broadcast lane/age telemetry) — Fast-tier triage").

**Showcase never-parks** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
Tier=Live): 5/5 pass, durations 3 m 11 s / 2 m 20 s / 2 m 40 s / 2 m 27 s / 6 m 03 s (the last under load average 5.7).
The P990 change cannot reach it: the diagnostic is off by default in that fixture and off is a single null check.

---

## 8. Evidence and scratch

- Rig evidence (read-only, not committed): `~/allegiance-evidence/P990/b104-telemetry.log.gz`; the parsed
  aggregates in §2.3/§3.1 come from `grep '^\[bb-' | python3` over that file.
- This task's raw gate output (both Fast-tier results dirs with their TRX/logs, `build-tools.sh` logs, the AOT publish
  log): `~/allegiance-evidence/P990/p990-gate/`.
- Scratch (`grep`/report drafts, the fmid-name generation table, the AOT output): `.scratch/p990/` in this worktree,
  deleted at the end; nothing outside this report is needed to reproduce a number.
- No new report assets (`docs/reports/BROADCAST_SLICE_D_P990_assets/` not used): every number is inline text.

---

## Native intent

Native's broadcast has no per-peer, per-category or per-age accounting at all — its only numbers are the server-wide
`cPacketsOut`/`cBytesOut` counters. What it *does* fix, and what the port must keep matching while it measures:

- **Which ships a peer hears about**: only the cluster's own ship list, only clusters with flying players, only ships
  whose last client update is inside ±1.5 s (player ships: `FedSrv/FedSrv.CPP:3728-3778`; drones:
  `:3795-3815`), with turret children on their own lanes (`:3779-3791`). A docked ship leaves the cluster entirely
  (`Igc/shipIGC.h:421-459`), so it is never posed.
- **When**: every tick while the player is flying — one `FM_S_HEAVY_SHIPS_UPDATE` per player per tick, two heavy and
  two light records by quota (`:3844-3845`, `:3859-3871`), the rest ranked by a deviation accumulator that resets
  the served ships to 0 and bumps the others (+0.70 light / +1.00 unserved) (`:3923-3950`), the player's own ship
  never in the list (`:4151`), the player's target promoted into a heavy slot (`:4141-4146`), `g.timeNow` as the
  message time reference (`:3996`), the recipient's own source ship as the position reference (`:4130-4132`), and
  `FM_NOT_GUARANTEED` so it sheds before anything reliable (`:4180-4195`; dispatch `:4642`).
- **Nothing else is touched by this task**: P990 reads native's rules to *classify* what it sees and to say which
  port-only streams have no native counterpart; it does not change what this server sends.

## Modern approach

Two questions answered with one default-off instrument instead of a guess. (1) **Where do the bytes come from?** The
ordered one-shot lane is now attributed per wire `fmid` *at the single transport sink* (the frame's own headers, the
same walk `FedMessaging::PfmGetNext` does — `_Utility/Messages.cpp:495-515`), and the coalescing lane per key family,
so the per-id rows reconcile with P983's category totals by construction. (2) **Why is a peer's view stale?** Every
(peer, visible ship) pair the age metric counts is classified by *subject state* — docked, dead, lifepod, parented,
other sector, viewer-not-swept, stationary-but-exact, or genuinely starved — with a pure function that is unit-tested
on its own and exercised on a live loopback world. And the instrument itself got cheaper while it grew: the per-pair
pose stamps moved from a `Dictionary<short,double>` (the b104 `+0.9 ms`) to a dense table + 4-byte index, and the
once-per-window report walks the viewer's tracked set instead of probing a dictionary per pair, taking the report from
9.8 ms to 3.3 ms at 411 peers. The result is the input slice D needs — an exact per-lane byte split, a reason for every
stale pair, and an ON cost that is now inside the noise floor of the tick it measures — with the wire untouched.
