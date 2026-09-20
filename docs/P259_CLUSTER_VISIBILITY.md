# Baseline Per-Side Cluster Visibility (P259)

## What this is

The server-side **baseline cluster-visibility capability**: whether a side has a
cluster on its "radar" at all, independent of the in-sector object-scanner
system (P131/P132). Concretely:

- `Side.VisibilityRange` — the per-side visibility range (float), seeded from a
  mission constant at `Mission.Assemble`.
- `Side.CanSeeCluster(short clusterId)` — the real capability test: a side sees
  a cluster iff it owns a live ship or station in a cluster that is within
  `VisibilityRange` of it (presence in the cluster itself is the distance-zero,
  always-true case).
- `Side.VisibleClusters` — the side's recomputed visible-cluster set.
- `Mission.RefreshSideVisibility()` — the recompute, driven **periodically**
  (every `Mission.VisibilityRefreshTicks = 200` mission updates), not every tick.

This is the **first of two** P-tasks: P260 (separate) wires the probe-as-exception
(the original probe mechanic that "uncovers everything") on top of what this
builds. This task deliberately touches **no** broadcast/export path — the same
cluster a peer is physically in stays visible to it exactly as before (that was
already correct). The deliverable is real, correct, **tested** server-side sim
state a later "what has my side actually detected" starmap overlay (cf.
`STARMAP_OVERVIEW_P249.md`'s deferred per-side-coloring note) and P260 consume.

## Honest sourcing finding — the brief's cited native mechanic is NOT in the on-disk tree

The task brief stated the mechanic is "confirmed real in native, precisely
researched", citing `CFSSide::UpdateVisibility`/`GetVisibilityRange()`
(`sideigc.cpp:1466-1523`), `CFSSide::CanSeeCluster/CanSeeObject`
(`sideigc.cpp:1525-1560`), and the mission constant `c_fcidVisibilityRange`.
**Verified directly this session: none of that exists in the read-only native
reference `~/Development/Allegiance`.**

That tree is the **modern FreeAllegiance** repository (origin =
`github.com/FreeAllegiance/Allegiance.git`), whose `src/Igc/sideigc.cpp` is 532
lines (not ~1500+) and contains **no** `UpdateVisibility`, `GetVisibilityRange`,
`CanSeeCluster`, `CanSeeObject`, or `m_visibilityRange` — confirmed by (a)
reading the file in full, (b) case-insensitive grep of the whole `src/` tree,
and (c) `git log -S` over the entire history for all four symbols (zero hits).
Its `c_fcid` float-constant enum (`igc.h:59-98`) has **no** visibility-range
entry. Modern FreeAllegiance models visibility differently: **presence** (own a
ship/station/probe in a sector → seen) plus per-**object** scanner range
(`probeigc.h` `InScannerRange`, gated by signature × `gaScanRange`) — there is
no per-cluster, mission-constant-seeded visibility radius at all.

The brief's `CFSSide`/`sideigc.cpp` citations describe the **original 2003
Microsoft Allegiance** source, which is **not on disk** anywhere on this machine
and could not be retrieved (multiple web/GitHub-code-search attempts returned
nothing). Per the repo's established honesty-about-unrecoverable-native-values
convention (cf. `Mission.ConquestWinThreshold` — a "stated default" for a
MissionParams field this port cannot decode), P259 implements the capability to
the brief's **stated semantics** (presence OR within-range) with the exact
values flagged as **stated**, since the authoritative original could not be read.

**Deliverable summary of what could/couldn't be verified:**

| Item | Status |
|---|---|
| Feature exists in on-disk native (`~/Development/Allegiance`) | ❌ absent (verified exhaustively) |
| Constant name `c_fcidVisibilityRange` | ✅ as cited by brief (original), but value unverifiable |
| Constant default value | ⚠️ **stated** `0.5` (in strategic-map screen units) |
| Update cadence (native `UpdateVisibility` interval) | ⚠️ **stated** 200 updates (port precedent) |
| Distance metric for "within range" | ⚠️ **stated proxy**: strategic-map screen distance |

## The distance proxy (stated, and why)

"Within `VisibilityRange` of a cluster" needs a cluster-to-asset distance. The
port's `DataClusterIGC` carries **no in-world bounding geometry** — only the
authored 2-D strategic-map coordinates `ScreenX`/`ScreenY` (normalized, ~0..1)
— and this port has already established that raw 3-D positions are **not
comparable across cluster frames** (P158: "cross-frame distances meaningless";
its pathfinder uses hop count, not geodesic). So a 3-D cross-cluster range is
meaningless here, and P259 measures the cross-cluster "within range" distance on
the **strategic map**: the screen distance between two clusters'
`ScreenX`/`ScreenY` — the same 2-D layout the original "map that can see to some
range / radar" Domingo described operates in. Concretely, an asset in cluster X
reveals cluster C iff `screenDist(X, C) ≤ VisibilityRange`; presence in C is the
distance-zero case (same sector ⇒ screen distance 0). The default `0.5`
(normalized screen units) reveals an owned asset's neighbours within half a
map-screen unit — not the whole map. A probe (P260) later widens this.

## Semantics (as implemented)

`Mission.CanSideSeeCluster(side, clusterId)`:
- asset universe = the side's live ships (every sector's `Sector.Models`, a
  `Ship` with the side's id, not dead) and live stations (every sector's
  `StationRuntimes`, same side, not dead); docked ships still count as presence.
- a cluster is visible iff any such asset sits in a sector whose screen distance
  to the target cluster ≤ `side.VisibilityRange` (presence = distance zero).
- **no probe exception** — that is P260.

`Mission.RefreshSideVisibility()` recomputes every side's `VisibleClusters` from
the live state; `Mission.Update` calls it every `VisibilityRefreshTicks = 200`
updates (the port's periodic-not-every-tick precedent `Server.ClusterInfoRefreshTicks
= 200`, Server.cs:1070). `Side.CanSeeCluster` queries live state through the
side's back-linked `Mission`; a mission-less side reads its cached set.

## Test coverage (all real corpus data)

`tests/Allegiance.Core.Tests/SideClusterVisibilityTests.cs` — 6 tests driving the
real `Mission.Assemble`/`PlaceShip`/`PlaceStation` path over real corpus sectors,
controlling `VisibilityRange` explicitly so every branch is deterministic:

1. **Physical presence → visible at zero range** (`VisibilityRange = 0`).
2. **Owned asset reveals a within-range neighbour** (not physically present;
   `VisibilityRange = screenDist(a,b)` ⇒ b visible; `= 0` ⇒ b hidden, presence stays).
3. **Unseen when nothing owned is in range**; another side's asset does **not**
   leak (a side-1 ship does not grant side-0 visibility).
4. **`RefreshSideVisibility` populates `VisibleClusters`** (presence-only).
5. **Re-evaluates over real ticks**: ship placed in A, tick past
   `VisibilityRefreshTicks` → cached set has A not B; move the ship to B (real
   `Sector.Remove` + `PlaceShip`), live query flips immediately, tick past the
   next refresh → cached set forgets A, adopts B.
6. **An owned station grants visibility** (hand-built `Station` owned by the side).

Tests pick **station-free sectors** so real corpus stations (the representative
map has a side-0 station at Nadir/1051 and a side-1 at Rib'zki/1062) cannot
grant the test sides incidental presence — each test's own placed asset is the
only possible source of visibility.

**Must-fail proven:** temporarily forcing `Mission.CanSideSeeCluster` to `false`
turned all 6 tests red; reverted green.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.Core.Tests`: **3079 pass / 3080 total** over two full-suite runs
  (baseline 3074 + 6 new); the only failure in both is the pre-existing,
  documented `LiveTurretGunnerBindingTests` timing flake. A transient
  `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` live-wire
  failure appeared once; it **also fails on the pristine master tree in
  isolation** (verified via `git stash -u` → clean build → reproduce) — a
  pre-existing live-loopback timing flake, not a P259 regression (P259 is pure
  server-side sim, zero wire changes).
- `Allegiance.Lobby.Tests` 12/12, `Allegiance.ClientCore.Tests` 85/85,
  `Allegiance.ClientCore.MonoGame.Tests` 311/311 — unaffected.

## Files

- `src/Allegiance.Core/Model/Side.cs` — `Mission` back-link, `VisibilityRange`,
  `VisibleClusters`, `CanSeeCluster`.
- `src/Allegiance.Core/Model/Mission.cs` — `VisibilityRangeDefault` /
  `VisibilityRefreshTicks` constants; `CanSideSeeCluster` / `ComputeVisibleClusters`
  / `RefreshSideVisibility`; periodic hook in `Update`; seeding in `Assemble`.
- `tests/Allegiance.Core.Tests/SideClusterVisibilityTests.cs` (new) — 6 tests.

## Out of scope (deliberately NOT built here)

- The **probe-as-exception** (an owned probe revealing clusters beyond the range
  baseline) and its sounds — **P260**.
- Any change to `Sector.GetShipUpdateCandidates`, `BroadcastShipsUpdate`, or any
  existing broadcast/export path — same-cluster physical-presence visibility is
  already correct; rewiring the live multiplayer broadcast around this new
  capability is explicitly out of scope (P259 is pure server-side sim state).
- Any client-visible surface (the starmap "seen" overlay is a later consumer).

No native source (`~/Development/Allegiance`) was modified (read-only).
