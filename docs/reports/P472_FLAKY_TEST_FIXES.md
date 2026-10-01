# P472 — Top Remaining Flaky-Test Fixes

Date: 2026-10-01
Branch: `master` (committed, not pushed)
Predecessor: P471 (systemic live-socket parallelism fix + `docs/TEST_FLAKES.md` ledger)

## Summary

P471's ledger ranked the remaining flaky live tests by post-fix failure rate.
P472 took the top 5 and, for each, determined the real race. **Four were
test-side** and are now fixed; **one (DockOut) is a genuine production race**
and is documented as a real bug with a named follow-up rather than papered over.

Top 5 by post-P471 failure rate (from `docs/TEST_FLAKES.md` "After" table):

1. `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` — 3/3
2. `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` — 3/3
3. `LiveTurretGunnerBindingTests.Board_binds_gunner_…_roundtrips_live` — 3/3
4. `LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side` — 2/3
5. `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` — 2/3

## Per-test findings and fixes

### 1. ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears — PRODUCTION RACE (no test change)

**Race.** The docker docks; the server logs `[shipdelete] ship … sdr=DOCKED`
and sends the one-shot `SHIP_DELETE(20)` `SDR_DOCKED` to the same-sector watcher
(`BroadcastShipDeparted`, `Server.Combat.cs:2155-2176`, triggered at
`Server.Combat.cs:2266`). The watcher's `Apply(ShipDelete)` sets `ShipEntity.IsGone
= true` (`ClientWorld.cs:2096-2108`). But the same tick's periodic
`HEAVY_SHIPS_UPDATE(176)` is computed from `Sector.GetShipUpdateCandidates`
(`Sector.cs:456-486`), whose per-tick candidate cache can still include the
just-docked ship (the dock removes it from `_models` only *after* the cache
snapshot for that tick), and the client's `Apply(HeavyShipsUpdate)`
unconditionally does `e.IsGone = false` (`ClientWorld.cs:1325`). The delete
latch is therefore defeated within the same tick and never persists — the
watcher ends with the docker's entity `StationId=1051` (docked, via SHIP_STATUS)
but `IsGone=false`. Deterministic in isolation; the test is correct.

**Action.** Per P472 rules, this is a production bug, not a test issue: the test
is **not modified**. Documented as a real bug with a named follow-up.

**Follow-up title:** `Fix HEAVY_SHIPS_UPDATE defeating the SDR_DOCKED IsGone
latch` — the same-tick ship-update broadcast must exclude a ship the moment it
docks (fix the per-tick candidate-cache staleness, or have the client not clear
`IsGone` for a ship that just received `SHIP_DELETE SDR_DOCKED`).

Before: 3/3 (isolated) — After: **unfixed (documented production bug)**.

### 2. JoinFlowLiveTests.EntityTable_TracksARealTickingServer — TEST FIX (stale assertion)

**Race (stale test, not a bug).** The ledger guessed "EntityTable registers 2
entries where 1 expected (dup entity)". Investigation proved it is **not** a
duplicate: the server boots each side's initial-miner NPC docked at its home
station (**P382**, commit `1582e605`), so the joining peer's entity table
legitimately contains its own ship plus the side's miner. The test's
`Assert.Single(session.World.Ships)` was written before P382 and is obsolete.

**Fix.** Scope the single-ship guarantee to the **own ship**: the own ship is
present, `IsMine`, exactly one `IsMine` entry (no duplicate own-ship entity), and
`MyShipId` matches. Preserves the original anti-dup intent (SET_CLUSTER /
SHIP_STATUS / SINGLE_SHIP_UPDATE collapse onto one own-ship entity) while
accepting legitimate sector-resident NPCs.

Before: 3/3 — After: 0/20 isolated, clean in full-suite runs (below).

### 3. LiveTurretGunnerBindingTests.Board_binds_gunner_…_roundtrips_live — TEST FIX (stale assertion)

**Race (stale test, not a bug).** Scenario 7 asserted an orientation-only change
must **not** reach a same-side observer via `SINGLE_SHIP_UPDATE`. Production has
intentionally included orientation in the broadcast dirty-gate since **P209**
(commit `06b142fa`, "a pure rotation changes no position at all, so the
position-only check alone would never re-broadcast it"; `Server.Broadcast.cs:404-
408` own-ship and `:663-667` teammate gate). So the observer now correctly
receives the rotated facing. The test encoded a pre-P209 contract.

**Fix.** Rewrite scenario 7 to assert the **current intended contract**: an
orientation-only change *does* reach the same-side observer via
`SINGLE_SHIP_UPDATE`, and the delivered update carries the rotated facing
(`rotUpd.Ship.Orientation.GetForward()` ≠ identity). This is correcting a stale
test to match intentional, documented production behavior — not a bug-hiding
loosening (no `[Skip]`, no deleted assertion; the guarantee is made stronger by
checking the propagated orientation).

Before: 3/3 — After: 0/20 isolated, clean in full-suite runs (below).

### 4. LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side — TEST FIX (missing dock precondition)

**Race (test-side).** The board handler **refuses** unless both ships are docked
(`Server.Docking.cs:90-95`, FedSrv.CPP:2342-2345). The carrier and boarder are
spawned inside the station's dock radius and latch `StationId` on an early
Started tick, but the test sent `FM_C_BOARD_SHIP` **immediately after seating**,
racing the dock. Under scheduling jitter the board was refused, no refund fired,
and the `MONEY_CHANGE` wait timed out. This is the same docking precondition the
P108 turret and win-condition boarding recipes wait for explicitly — the refund
test simply omitted it.

**Fix.** Add a condition-wait for `sp.Carrier.StationId`/`sp.Boarder.StationId`
(the real observable) before `SendBoard`, plus a generous ceiling on the
existing MONEY_CHANGE condition-wait (3000 → 9000) for load headroom. First,
ceilings alone were tried (3000 → 9000) — Board_refund still failed 3/20 in
isolation, proving the cap was treating the symptom; the dock-precondition is
the actual fix (0/20 after). No assertion weakened.

Before: 2/3 (post-P471) — After: 0/20 isolated, clean in full-suite runs (below).

### 5. LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission — TEST FIX (seating-order)

**Race (test race).** `InjectMultiMissionSpawn` routes placements by *spawn
sequence*, so which connected peer lands on mission A / side 1 depends on seating
order. The test disconnected a client by connection index and asserted on
another by index; under load the seating order varied and the "victim" was not
on mission A / side 1, so mission A never ended (solo repro: "mission A did not
end / a0 did not receive GAME_OVER").

**Fix.** Resolve each seated peer's real mission + side from the server's ship
table (`Ship.Mission` / `Ship.SideId`) after seating, choose the disconnect
target as the peer on mission A / side 1, and assert on the peer on mission A /
side 0 (winner receiver) and the mission B peer. Immune to seating order.

Before: 2/3 (post-P471) — After: 0/20 isolated, clean in full-suite runs (below).

## Verification

Per-task protocol:
- **20/20 isolated** per fixed test via `--filter`, repeated.
- **3 consecutive full-suite runs** (`scripts/run-tests.sh`, assemblies
  sequential per P471), each fixed test clean in every run.

| test | before (post-P471) | after isolated | after full-suite (3 runs) |
|---|---|---|---|
| DockOut_…UndockReturnsAndClears | 3/3 | unfixed (prod bug) | fails 3/3 (documented prod bug) |
| EntityTable_TracksARealTickingServer | 3/3 | 0/20 | clean 3/3 |
| Board_binds_gunner_…roundtrips_live | 3/3 | 0/20 | clean 3/3 |
| Board_refund_… | 2/3 | 0/20 | clean 3/3 |
| Multi_mission_disconnect_… | 2/3 | 0/20 | clean 3/3 |

Build: `/home/domingo/.dotnet/dotnet build -m:8` → 0 warnings, 0 errors.

**Residual full-suite failures (all out of P472's top-5 scope, pre-existing load
flakes).** The three full-suite runs still show a rotating set of live-socket
load flakes in tests below the top-5 (each run differing): `DockOut` (the
documented P472 production bug, fails every run by design), plus
`FullSession_ThreePeers…`, `SideElimination…`, `Conquest_win_in_one_mission…`,
`LiveRipcordTests.Cancel…`/`Re_target…`, `CargoBayReloadTests.SwapPart…`,
`StationsUpdateLiveTests…`, `LoadoutChangeLiveTests.Boarding_and_leaving…`,
`LiveChatSectorWireTests…`, `GameOverPlayersWireTests…`, and
`LiveWinConditionTests.Board_refused_once_mission_over`. These are the ledger's
listed 1/3 load-flakes and are explicitly out of P472's scope (top 5 only); none
of the four P472 fixes appears in any run's failure list. They are left ranked in
`docs/TEST_FLAKES.md` for a future sweep.

## Out-of-scope respected

- xunit collection/parallelism setup (P471) untouched.
- No `[Skip]`, no deleted test, no assertion loosened.
- DockOut is documented, not papered over.

## Ledger

`docs/TEST_FLAKES.md` updated: fixed rows annotated with cause + fix; the
DockOut production bug promoted to an explicit follow-up.
