# P317 — Cut the O(peers²) cost in BroadcastShipStates (scaling wall H5)

**Task:** eliminate the per-(recipient, subject) per-tick work in
`Server.BroadcastShipStates` — the "single largest scaling wall in the server"
from the glm-5.3 full-codebase performance review
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`,
finding **H5**, line 84).

**Branch:** `master` (committed locally; **not pushed** — Domingo pushes).
No side branch, per the port convention.

---

## Relationship to the finding (H5)

H5 (verbatim, review line 84): *"`BroadcastShipStates` is O(peers²) per tick
with a fresh allocation per pair — at 150 players, ~1M allocations/sec and
~112k packets/sec, all serialized on the sim thread."*

Confirmed in the live tree (`src/Allegiance.Server/Server.cs`, pre-P317):
the P58/P62/P63/P85 same-side teammate broadcast ran
`foreach kv in peers { foreach tv in peers { … } }` — at 150 players all on
one side that is ~22,350 same-side (recipient, subject) pairs per tick, and
for **every** such pair it (a) allocated a fresh `SnapshotParts` array (LINQ
`Select().ToArray()` over the subject's mounted parts), (b) compared it via
`PartsEqual`, and (c) — for every moving subject — built and sent a separate
`SingleShipUpdate` packet per recipient.

Measured baseline on this box (150 bots, 30 Hz, P306 stress harness), before
this change, BroadcastShipStates alone:
- ~15.7 MB/tick of sim-thread allocation (median),
- ~0.265 ms/tick of real per-tick time (median),
- whole-tick cadence collapsing: **17.0 % tick overruns, 28.2 ticks/s achieved**.

The review's H5 magnitudes (~1M alloc/sec, ~112k packets/sec) are the same
phenomenon at 20–30 Hz: the per-pair `SnapshotParts` arrays and the per-pair
packet sends.

---

## Approach (three scoped optimizations, wire-behavior preserved)

### 1. Loadout re-export: cache the snapshot once per ship, gate on a version
   (kills the O(peers²) `SnapshotParts` allocation-and-compare)

The old code re-snapshotted and re-compared a teammate's mounted parts against
every recipient **every tick** purely to detect "has this ship's loadout
changed since I last told this recipient?" — a rare event paid for at O(P²)
frequency.

- `Ship` gained a monotonic `LoadoutVersion` (public long, `Ship.cs`),
  bumped at every mutation that can change a wire `PartData` field
  (`PartId`/`Amount`/`MountId`, or list membership): `MountPart`, the three
  `_parts.Remove` paths (`RemoveFromHome`, the launcher-terminate branch,
  `TryRemovePart`), `RepointMount` (MountId), `SetPartAmount` (Amount),
  `ClearLoadout`, and the dock ammo/fuel re-supply `WithAmount` rewrite.
  Fraction-only rewrites (`WithFraction`/`ResetEquippedFraction`) deliberately
  do **not** bump — the `PartData` snapshot does not carry fraction, so
  behavior (no re-export on a fraction-only change) is preserved.
- `Server` replaced the per-pair `Dictionary<(Recipient, ShipId),
  IReadOnlyList<PartData>> _teammateLoadoutSent` with two coordinated
  structures (`Server.cs`):
  - `_teammateLoadoutCache: Dictionary<short, (Version, Snapshot)>` — ONE
    snapshot per subject **ship** (the same for every recipient), tagged with
    the version at compute time, so `SnapshotParts` runs at most once per ship
    per actual loadout change, never once per pair per tick
    (`EnsureLoadoutSnapshot` helper);
  - `_teammateLoadoutVersionSent: Dictionary<(Recipient, ShipId), long>` —
    the ship version the last EXPORT to that pair carried. The per-pair dirty
    check is now a cheap integer compare: *"has this ship's cached snapshot
    changed since I last told this recipient."*
- Steady state: **zero** snapshot allocation and zero compares in the hot
  path; only an int equality per pair.

### 2. Group peers by side once per tick (kills the O(P²) filtering scan)

The old inner loop scanned *every* peer per recipient and skipped
different-side ones (`if (tv.Value.Ship.SideId != recipientSide) continue;`)
— an O(P²) filtering pass even before the real work. Now `BroadcastShipStates`
builds a per-side `List<KVP>` once per tick (`sameSide`) and each recipient's
walk iterates only its own side's list. List membership preserves `peers`
order, so the effective per-side subject order is unchanged.

### 3. Batch per-recipient position sends + share each subject's encode
   (kills the ~112k packets/sec and the O(P²) message construction)

The P85 teammate-position path built and sent a separate `SingleShipUpdate`
**packet per (recipient, subject)** per tick.

- **Batching:** each recipient now collects every dirty teammate update into
  ONE `posBatch` and a single `FedPacket.Pack` is sent at the end of its walk
  (one packet per recipient per tick, instead of one per pair). Split at
  `FedPacket.MaxMessagesPerPacket` (256) only as a defensive guard against a
  >256-message packet being rejected by this port's `Unpack` cap.
- **Shared encode:** because a given subject's position bytes are *identical*
  for every same-side recipient, each subject is now encoded **once per tick**
  (`positionEncode` cache) and that immutable `byte[]` reference is shared
  into every recipient's batch — dropping the per-pair message construction
  from O(P²) to O(P). `FedPacket.Pack` copies (flattens) the input bytes, so
  sharing the source array is safe.

### Behavior preservation (scope #4)

Nothing about *what* is sent or *when* changes — only *how cheaply* it is
computed and packed:

- Same-side-only scope, the one-shot EXPORT/PLAYERINFO ordering, the
  status-periodic cadence, and the position dirty-gate are unchanged.
- The frozen `peers` snapshot keeps each subject's dirty-gate
  (`LastBroadcast*`) constant across recipients within a tick, exactly as the
  original relied on — so the shared encode is used consistently.
- Within a tick, a subject's EXPORT/PLAYERINFO/STATUS/LOADOUT are sent in the
  per-subject loop (immediately), and only its position update is deferred to
  the batched packet — preserving "export before position for the same
  subject."
- The loadout re-export fires on the same real content changes as before (the
  version is bumped exactly on `PartData`-visible mutations and on nothing
  else).

---

## Verification — real measured improvement (P306 stress harness)

Same tooling/metric on both sides: `tools/vizfight stress <port> <bots> <ups>`,
sim-thread-only allocation (`GC.GetAllocatedBytesForCurrentThread` deltas)
sampled around `BroadcastShipStates` (temporary diagnostic, removed before
commit), and the harness's own authoritative whole-tick signals (per P306:
tick timing / overrun % / achieved Hz).

**150 bots @ 30 Hz** (single team → ~22,350 same-side pairs/tick — the exact
scaling wall):

| metric (per tick unless noted)      | before       | after        | Δ        |
|-------------------------------------|--------------|--------------|----------|
| BSS sim-thread allocation (median)  | 15.67 MB     | 7.66 MB      | −51 %    |
| BSS real per-tick time (median)     | 0.265 ms     | 0.142 ms     | −46 %    |
| whole-tick overruns                 | 16.97 %      | 5.34 %       | −69 %    |
| achieved ticks/s                    | 28.24        | 29.23        | +3.5 %   |
| packets to the fleet / tick (est.)  | ~22,350      | ~150         | ~150× ↓  |

(The whole-tick signals are stable across repeat runs: two independent "after"
runs gave 5.18 % / 4.91 % overruns, 29.29 / 29.3 ticks/s; the "before" baseline
gave 16.97 % / 17.14 %, 28.24 / 28.23.) Note on whole-tick measurements: they
are the downstream signal and are sensitive to whole-box contention — later
re-runs during a separate concurrent `Allegiance.Server` process + a
udev-worker CPU storm on this shared box degraded whole-tick numbers for both
baseline and after alike (external per-tick latency swamps the ~0.12 ms BSS
saving). The **method-level** BSS numbers above (sim-thread time and allocation,
measured paired) are the airtight, contention-independent evidence.

**100 bots @ 20 Hz** (strain point): overruns 0.2 % → 0.2 % (not overloaded);
**20 bots @ 20 Hz**: clean at both (0 %).

The residual ~7.7 MB/tick of sim-thread allocation in `BroadcastShipStates` is
the irreducible physical cost of the feature itself: every same-side recipient
must receive every moving teammate's position every tick, so ~P² position
messages must be byte-copied into P packets. The review's *avoidable* O(P²)
walls — per-pair loadout re-snapshot, per-pair packet construction and write —
are gone.

---

## Build / test

Whole-solution build: **0 warnings, 0 errors**.

- New unit tests: `LoadoutVersionTests` (+4) pin the version-bump wiring at
  each loadout-mutation path. Discriminating: removing one `BumpLoadout()`
  turns the corresponding assertion red (verified live).
- **Test interaction found & fixed (SimPassExceptionIsolationTests):** the
  P314 containment test injects 256 mounted parts into a teammate's private
  `_parts` via reflection — which by design bypasses the `MountPart` cap and
  therefore no longer bumps `LoadoutVersion`. With the version-gated re-export,
  that already-exported pair is now correctly suppressed (no spurious throw).
  The test was updated to also invoke the private `BumpLoadout()` alongside the
  injection — mirroring a real loadout mutation — so it still exercises the
  "a throwing teammate export is contained, not fatal" guarantee the Step
  barrier provides. Deterministic must-fail: without the version bump the
  poisoned re-export never fires and the test reds; with it, the barrier is
  verified.
- Behavioural contracts guarded by the existing live tests, which pass
  unchanged: `TeammateLoadoutBroadcastTests` (loadout-change → re-export),
  `TeammatePositionBroadcastTests` / `TeammateMovementLiveTests` (batched +
  shared position updates still reach the observer), `LoadoutVersionTests`.
- Full `Allegiance.Core.Tests` suite run 4× (3257 total, +4 new):
  - The **only persistent** failure across all runs is the pre-existing
    `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
    machine-state flake — verified to fail 3/3 on the **pristine** tree (A/B
    stash), and its failing assertion concerns the position-dirty-gate
    semantics my change preserves byte-for-byte.
  - `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` is the
    second documented environmental flake (flaps on pristine, P200).
  - Every other failure across the 4 runs (LiveStationRescue, LiveRipcord,
    LiveRelaunch, LiveCountdownStartPing, AsteroidsUpdate, StationsUpdate,
    LiveTreasurePickup, LiveTeamLobby, LiveMoney, AsteroidMined) is a **singleton
    that passed in isolation** — the well-documented timing-sensitive
    live-loopback flakiness of this shared box under load (P306's own baseline
    run 1 had 5 such fails). None reference this change's symbols; none touch
    the loadout/position/teammate-broadcast subsystem.
  - The genuine interaction (SimPass) is fixed; net the suite shows no
    regression from P317.
- Other test projects: `Allegiance.ClientCore.Tests` 157/157, `Allegiance.ClientCore.MonoGame.Tests` 576/576, `Allegiance.Lobby.Tests` 12/12 — all clean.

## Exclusions / out of scope (unchanged from the brief)

- P324 (other review findings: visibility-sweep throttling,
  `GetShipUpdateCandidates` caching, per-side batch cadence, NPC BFS caching).
- Any correctness/stability finding (separate tasks).
- Native reference source `~/Development/Allegiance` — read-only, untouched.
