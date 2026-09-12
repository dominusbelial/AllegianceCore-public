# NPC_RIPCORD_TRAVEL_P172 — Stage 1: safety gate + station-anchor search

Date: 2026-09-12
Branch: `master`
Top commit before this task: `3c7b0ea` (P171 fixup). After: this task's P172 commit.

## One-paragraph summary

P172 gives this port's autonomous NPCs a second way to travel cross-cluster,
alongside P157/P158's warp-pathing: a **ripcord** to a friendly
`c_sabmRipcord`-capable station in (or warp-reachable from) the target cluster.
It ports the two native predicates — `CshipIGC::IsSafeToRipcord`
(`Igc/shipIGC.cpp:4200-4229`) and the **pilot-type-agnostic station-anchor core**
of `CshipIGC::bShouldUseRipcord` (`shipIGC.cpp:4231-~4390`) — and wires them
ahead of every cross-cluster leg of `Ship.ApplyNpcAutopilot`
(`Ship.cs`), initiating the ripcord through the existing P111 state machine
(`Server.RequestRipcord`) via the `IIgcSite` seam. The real-world stations in
the demo map are **not** ripcord-capable (verified, below), so the feature only
engages when a test injects a synthetic anchor — meaning it changes nothing for
the existing P157/P158/P161 cross-cluster tests, which keep warp-pathing
unchanged.

---

## 1. The `bShouldUseRipcord` real-vs-drone scope split (confirmed, cited)

Read in full at `Igc/shipIGC.cpp:4231-~4390`. The function has two genuinely
different anchor sources:

- **The pilot-type-agnostic core — ported.** Every ship (drone included) runs
  `FindTarget(this, c_ttFriendly | c_ttStation, NULL, pcluster, NULL, NULL,
  c_sabmRipcord)` — "is there a friendly ripcord-capable station in this
  cluster" — for the target cluster and then, failing that, a breadth-first
  walk outward through the warp graph repeating the same test
  (`shipIGC.cpp:4307-4311`, BFS at `:4361-4381`).
- **The real-player-only extension — NOT ported (out of scope).** Native wraps
  the allied-`GetRipcordCluster`-ships tier (`shipIGC.cpp:4238-4286`) and the
  `pcluster->GetProbes()` ripcord-probes tier (`shipIGC.cpp:4316-4333`) in
  `if (m_pilotType >= c_ptPlayer)`. A drone (`pilotType < c_ptPlayer`) never
  reaches those branches, so they are irrelevant to this task's NPC travel and
  are deliberately not built (see §7).

Both the early-outs — no hull or `c_habmNoRipcord` (`:4232-4233`) and
`pcluster == pclusterShip` (`:4234`) — are in the shared core and are ported
into `Ship.FindRipcordAnchorCluster`.

## 2. The station-anchor search (Scope 2) — `Ship.FindRipcordAnchorCluster`

Port of the core described above, in `src/Allegiance.Core/Sim/Ship.cs`:

- Bounded by native's two termination conditions: no further frontier
  (`shipIGC.cpp:4372`) or the search stepping no closer than the ship
  (`dShip-- <= 1`, `:4373`), where `dShip` is the warp-hop distance from the
  ship to the target cluster (`GetDistance`, `:4236`), computed by
  `Ship.WarpHopDistance`.
- The friendly-station test itself (`Ship.HasFriendlyRipcordStation`) is the
  concrete realisation of `FindTarget(..., c_sabmRipcord)` — a station on the
  ship's own side whose resolved type has the `c_sabmRipcord` ability — and is
  the **same** friendly-station tier the P111 `Server.FindRipcordModel` already
  uses for the actual anchor (`Server.cs:4932-4938`), so the decision search and
  the teleport anchor agree by construction.
- Traversal mirrors `Sector.FindPathToward`'s plain unweighted BFS
  (`Sector.cs:427-453`), as the task directs.

Returns the anchor cluster when found (scope-item-2 "return the anchor cluster
if found"), else `null`.

## 3. `IsSafeToRipcord` (Scope 1) — faithful formula + `SolveForImpact` reuse

`Ship.IsSafeToRipcord()` ports `shipIGC.cpp:4200-4229` verbatim:
`speed² < 1` → safe outright (`:4204-4206`); otherwise
`tRipcord = RipcordSpeed` (the hull's ripcord-speed field used as the **full
spin-up duration**, the interpretation this port already established at
`Ship.cs:494-497`, *not* the live countdown),
`v0 = |v|`, `k = thrust / (vMax·mass)`, `s = v0·(1−e^(−k·tRipcord))/k` (the
drifted distance under the same `dV/dT = −kV` drag the P155 `GotoPlan` doc
already discusses), and an effective horizon `tMax = 1 + s/v0` ("a bit of a
fudge", `:4220`). It is safe iff no scan candidate has a closest-approach time
`t ≤ tMax` — native `Dodge`'s positive-`tMax` branch (`common.cpp:1749`),
realised as `Ship.AnyHazardWithin`.

The imminence answer reuses the **same** `Ship.SolveForImpact` (P168's faithful
quadratic-solve port) that `TryComputeDodgeGoal` uses, over the same candidate
universe (same-side ships, asteroids, stations) with the same relative-motion
shapes. Consistent with native's `Dodge(this, NULL, ...)` call at `:4222`, the
ship's own nav target is deliberately *not* excluded here (unlike the P168/P170
waypoint dodges that pass `pmodelSkip`).

## 4. NPC-connection resolution for `RequestRipcord`

Native's NPC caller reaches the ripcord start via the IGC seam —
`pigc->RequestRipcord(this, pclusterTarget)` (`shipIGC.cpp:4161`), where `pigc`
is `IIgcSite`. This port's `IIgcSite.RequestRipcord(IShipIgc, IClusterIgc)`
already exists as a no-op default (`Sim/IIgcSite.cs:299`, the faithful port of
`igc.h:4865`). P172 fills it in:

- `Ship.TryRipcordOrDefer` calls `Mission.Site.RequestRipcord(this, target)`
  (Ship implements `IShipIgc`, Sector implements `IClusterIgc`).
- `ServerHost` now overrides `IIgcSite.RequestRipcord` to raise a
  `RequestRipcordReceived` hook; `Server` subscribes it to
  `OnNpcRequestRipcord`.
- The existing P111 state machine (`Server.RequestRipcord`) is refactored into
  a connection-agnostic `RequestRipcordCore(ship, target, ConnectionId? from)`.
  A real-player request passes the peer's connection; an NPC passes `null`.

**Why `null` is the right NPC sentinel** (the task's open question): an
ownerless NPC has no `_peerShips` entry (P147 — `SpawnNpcShip` creates no peer,
`Server.cs:2704`), and a stale `SendPeer(null-connection, ...)` would throw
"not connected" in the transport (`TcpTransport.Send`). So the two
**requester-only** sends (re-target activate, deny) are skipped when `from` is
null, while the sector-wide **first-activation broadcast**
(`SendRipcordSectorWide`) still reaches any real peers flying in the ripper's
sector — faithful and harmless for an NPC. Everything downstream (countdown in
`Sector.ResolveRipcordCountdowns`, teleport in `Server.PerformRipcordTeleport`)
is already connection-agnostic, so the NPC ripcord completes exactly like a
player's. This is the same "connection-agnostic core" convention the port
already established for undocking (P152 `PerformUndockCore`, `Server.cs:5211`).

## 5. Composition with P157/158 warp-pathing (confirmed, cited)

Native's call site 2 (`shipIGC.cpp:4153-4172`) runs `bShouldUseRipcord` /
`IsSafeToRipcord` **ahead of** the plan-driven GotoPlan movement, so:
ripcord takes priority whenever a valid friendly anchor is reachable in/around
the target cluster (`:4154`); the warp-path only engages in the else
(no-anchor) case. This task wires the same priority into
`Ship.ApplyNpcAutopilot` ahead of all three cross-cluster legs (station
`PlanTarget`, mining-asteroid, layer-asteroid — native's single `c_cmdPlan`
trigger is pilot-type agnostic): `TryRipcordOrDefer` returns `true` when the
ripcord path owns the tick (initiated / already active / deferred), and the
existing `CrossClusterWarpGoal` / `FindPathToward` fallback (P157/158) is
reached only when no anchor is reachable. **No P157/158 code changed.**

The demo map's real stations are **not** ripcord-capable (both garrisons are
type 15, `c_sabmRipcord=False` — verified by dumping `XXV_simple.igc`'s placed
stations; reachability shows "NONE" from every sector for both sides), so the
existing P157/P158/P161 cross-cluster tests find no anchor and keep
warp-pathing — confirmed by running them unmodified alongside the new tests.

## 6. Unsafe-retry behavior (with the stated simplification)

Native's unsafe branch (`shipIGC.cpp:4162-4166`) stores a pending ripcord and
re-tests 5 s later (`:2066-2067`) while GotoPlan simultaneously warp-paths. I
read that carefully and confirmed GotoPlan does keep moving the ship during the
pending window — so native's literal behavior is "warp-path *and* retry", not a
strict hold.

P172 states a **deliberate simplification**: when an anchor is reachable but
`IsSafeToRipcord()` fails, the ripcord path **owns the tick** — the ship defers
(does not warp-path) and re-attempts after a 5 s latch (`Ship.RipcordRetryInterval`,
mirroring `m_timeRequestRipcord`). This guarantees the cross-cluster journey
happens by ripcord, never racing a physical warp transit (which would undermine
the entire feature's purpose). The live test asserts the ship does **not** arm a
ripcord while the hazard is present, and does arm ~5 s after the hazard clears.

## 7. Explicit exclusions (unchanged by P172)

- **Real-player-only ship/probe anchors** — the `m_pilotType >= c_ptPlayer`
  tiers of `bShouldUseRipcord` (`shipIGC.cpp:4238-4286`, `:4316-4333`). Not
  built.
- **`bAllowAlliedRip` / mission-parameter-driven ally behavior** — tied to the
  same real-player-only branch. Not built.
- **P157/158 warp-pathing** — untouched; ripcord is an alternative checked
  first, not a replacement.
- **`FindRipcordModel`'s warp-adjacent-cluster expansion** — still scoped to the
  requested sector (existing P111 scoping, documented at `Server.cs:4884-4930`).
  Consequence, stated: a ripcord anchor reachable only in a *neighbouring*
  cluster would make `bShouldUseRipcord` decide-true but `FindRipcordModel`
  deny on request. The live tests place the anchor in the target cluster itself
  (hop 0), so decision and teleport agree; the neighbour-only corner is an
  unchanged P111 boundary, not regressed here.
- **Any change to P103-P171 code** beyond this task's own additions.

## 8. Verification

- `Ship.IsSafeToRipcord()` formula reused `SolveForImpact` exactly as
  `TryComputeDodgeGoal` does (§3).
- New live tests (`tests/.../NpcRipcordTravelLiveTests.cs`), proven
  **discriminating** (each fails when the wiring is temporarily removed):
  - `Cross_cluster_npc_rips_to_friendly_station_instead_of_warp_pathing` —
    NPC arms a ripcord (RipcordModel → target cluster), its sector then changes
    via the teleport, and its **minimum distance to the connecting warp stays
    far above the warp's collision radius** (measured ~700 u) — it never
    physically flew through a warp.
  - `Unsafe_npc_defers_ripcord_and_retries_after_hazard_clears` — with a decoy
    asteroid in the drift path (self-calibrated to the "ripcord-only" band so
    the P168 dodge does not mask it), the NPC does **not** ripcord immediately;
    after the hazard clears and the 5 s retry window lapses, it ripcords and
    teleports.
- Existing cross-cluster + ripcord suites pass unmodified
  (NpcCrossClusterMinerDecisionLiveTests, NpcLayerDefaultOrderLiveTests,
  NpcCrossClusterLiveTests, LiveRipcordTests, RipcordWireTests, plus the
  SabmRipcord-selecting NPCTakeCoverTests).
- Full-suite run(s) below.

## 9. Session log

See the P172 entry in the session feed; logged via
`python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.
