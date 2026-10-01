# P478 — the side's GlobalAttributeSet scales development cost and build time

**Landing:** `master` (committed, not pushed). Repo:
`/home/domingo/Development/AllegianceCore`.

## Problem

The parity pass found side global attributes (GA) computed and broadcast but
never applied. P446/P467/P467b/P475/P476/P477 wired flight, weapon output, ship
durability, station durability, visibility and mining. The last two economy
consumers — `c_gaDevelopmentCost` (23) and `c_gaDevelopmentTime` (24) — were
still unwired: a development bucket's money cost and build time came straight
from the core, ignoring the owning side's GA. Native multiplies both at bucket
creation by the side's `GetGlobalAttributeSet().GetAttribute(c_ga…)`
(`src/Igc/bucketigc.cpp:70-74`).

## What native does (read exactly, not guessed)

`CbucketIGC::Initialize` (`bucketigc.cpp:66-75`):

```cpp
if (m_data.buyable->GetObjectType() == OT_development)
{
    ...
    if (pdevelopment->GetObjectID() != c_didTeamMoney)
    {
        m_price = (Money)(m_price * m_data.side->GetGlobalAttributeSet().GetAttribute(c_gaDevelopmentCost));
        m_timeToBuild = (DWORD)(m_timeToBuild * m_data.side->GetGlobalAttributeSet().GetAttribute(c_gaDevelopmentTime));
    }
}
```

Three fidelity facts fall out, all preserved in the port:

1. **Only `OT_development` buckets are scaled.** The multiplier block sits
   *inside* the `== OT_development` guard, so ship-type, drone-type and
   station-type buckets keep their raw core price/time. (The task asked to
   check the P458/P459 drone/station buckets specifically: native does **not**
   scale them.)
2. **Both price and time are scaled, at bucket creation** — the multiplier is
   applied once when the bucket is constructed (from the side's GA *then*),
   not live on every read. The port reproduces this at the single creation
   point (`BucketCatalog.ForSide`, the port of `CsideIGC::CreateBuckets`).
3. **The `c_didTeamMoney` development (id 1) is exempt** — it is the
   prosperity/economic-win shortcut, priced by the core, not by research GAs.

## Change (reuses P446's single-GA-path pattern)

### `GlobalAttributeSet.cs` — named index constants

Added `DevelopmentCost = 23` (`c_gaDevelopmentCost`, `igc.h:620`) and
`DevelopmentTime = 24` (`c_gaDevelopmentTime`, `igc.h:621`) in the same named-
constant style P446/P467b/P477 established, each with its `igc.h` citation.
(The port's `Get(index)` is the exact port of native
`GetAttribute(c_ga…)`.)

### `BucketCatalog.ForSide` — scale development buckets at creation

`ForSide` gains a `GlobalAttributeSet civBaseAttributes` parameter — the owning
side's GA at bucket creation, i.e. its civ's `GasBaseAttributes` (native reads
the side's GA while `CreateBuckets` runs, before any development completes). At
the single creation point each **development** bucket's `Price` is multiplied
by `Get(DevelopmentCost)` and its `TimeToBuild` by `Get(DevelopmentTime)`,
except the `c_didTeamMoney` (id 1) development, which stays raw. The ship-type,
drone-type and station-type buckets are deliberately untouched (native scales
only `OT_development`).

Call site: `Mission.Assemble` (`Mission.cs:482`) passes
`GlobalAttributeSet.FromArray(civ.GasBaseAttributes)`. Buckets are created once
at join, so the scaling is baked from the side's civ-base GA — exactly the
native semantics of scaling at creation.

## Client display (scope 2 — verified no-op, documented)

The task's second item: "The client's investment/bucket display must show the
scaled price and time, matching what the server charges." Investigation found
there is **no client development/bucket price-or-time display to update**:

- The MonoGame client has no research/development-bucket panel: the docked
  shop (`ShopCatalog`, `PartInfoLayout`) lists only **hulls and parts**, and
  native does **not** GA-scale those (only `OT_development` buckets), so they
  are untouched. No `research`/`invest`/`BucketData` price rendering exists
  (the client only plays research-complete sounds).
- `BUCKET_STATUS(65)` carries only invested build-ms and invested money
  (`BucketStatus.cs` — the wire format has no total price/time field), so no
  client path reads a development's total cost/time.
- Consistency is automatic: the server's bucket now carries the scaled
  `Price`/`TimeToBuild`; `Bucket.AddMoney` clamps/refunds against the scaled
  price, so a donor pays exactly the scaled cost, and P442's `MONEY_CHANGE`
  decrements the donor wallet by that exact spent amount. So the existing
  wallet HUD and any future bucket display agree with what the server charges.

Conclusion: no client source change is required; the requirement is met by the
server-side scaling flowing through the existing money/donation path.

## Real developments and civs in PCore001 that touch these two

Scanned all 305 developments of `PCore001.igc` for `Gas[23]`/`Gas[24]`:

- **No development changes either attribute** — every one of the 305
  developments has `Gas[23] == Gas[24] == 1.0`. So there is no real development
  that raises/lowers research cost or time; per the task, "if none exists…
  the synthetic test suffices."
- **The civ base attributes do vary widely** (these are the real non-1.0
  multipliers a side starts with at bucket creation):

| Civ | GasBase[23] cost | GasBase[24] time |
|---|---|---|
| Belters | 1.25 | 2 |
| Bios | 0.5 | 5 |
| Dreghklar | 1.25 | 0.5 |
| Ga'Taraan | 0.8 | 1 |
| Gigacorp | 1 | 1 |
| Iron Coalition | 1 | 1 |
| Omicron | 1 | 2.5 |
| Phoenix | 1 | 1.5 |
| Rixian | 1 | 0.1 |
| Technoflux | 0.75 | 3 |

Because no development exists, the mandated synthetic non-1.0 test is the
primary assertion; the real-civ test additionally exercises the full
`Mission.Assemble` path with Bios (cost 0.5, time 5).

## Verification

### Pre-fix failure (recorded)

Ran the P478 tests with the `BucketCatalog.ForSide` scaling temporarily
reverted to the raw price/time (new signature kept so it compiled). **2 of the
5 tests failed** on the unfixed code:

```
P478SideGaDevelopmentCostTimeTests.Development_cost_and_time_scale_development_buckets_at_creation [FAIL]
  Assert.Equal() Failure: Values differ
  Expected: 37500      (raw 25000 × 1.5)
  Actual:   25000      (raw — GA had no effect)
P478SideGaDevelopmentCostTimeTests.Real_civ_base_GA_scales_development_buckets_through_mission_assemble [FAIL]
```

The other three (`Economy_indices_match_native_and_identity_is_raw`,
`TeamMoney_development_is_exempt_from_scaling`,
`Non_development_buckets_are_not_scaled`) pass either way because they assert
identity/exemption/non-development behaviour that does not depend on the
scaling being present.

Restored, all 5 pass:

```
P478-DEV:     scaled 295 development buckets (cost×1.5, time×2.0)
P478-CIV:     Bios costMul=0.5 timeMul=5; 285 dev buckets scaled through Mission.Assemble
P478-NONDEV:  ship raw; 5 drone-type, 6 station-type buckets all raw (not GA-scaled)
```

(Team-money: the `c_didTeamMoney` dev id 1 is a live bucket on the catalog and
is asserted NOT scaled.)

### New tests

`tests/Allegiance.Core.Tests/P478SideGaDevelopmentCostTimeTests.cs` (5 unit
tests, all deterministic — no live server needed because scaling is baked at
bucket creation):

- `Economy_indices_match_native_and_identity_is_raw` — indices 23/24; identity
  GA reproduces raw development price/time.
- `Development_cost_and_time_scale_development_buckets_at_creation` — the
  mandated synthetic non-1.0 test (cost 1.5, time 2.0): every non-team-money
  development bucket's `Price` is 1.5× and `TimeToBuild` 2.0× raw.
- `TeamMoney_development_is_exempt_from_scaling` — dev id 1 stays raw.
- `Non_development_buckets_are_not_scaled` — ship-type / drone-type /
  station-type buckets keep their raw core price/time (native fidelity).
- `Real_civ_base_GA_scales_development_buckets_through_mission_assemble` — the
  real production `Mission.Assemble` path with Bios (civ 35, cost 0.5, time 5):
  285 dev buckets scaled by the civ's real base factors.

### Build & suite

- Whole-solution build `/home/domingo/.dotnet/dotnet build`: **0 warnings,
  0 errors**.
- Full `/home/domingo/.dotnet/dotnet test`, **3 consecutive runs**. No failure
  in any run is attributable to P478; the only failures are the documented
  `docs/TEST_FLAKES.md` (P471) environmental flakes and the documented P472
  production bug. Across the three runs the complete set of failures was:

  - `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`
    — the documented P472 **production bug** (one-shot `SDR_DOCKED` latch
    defeated by same-tick `HEAVY_SHIPS_UPDATE`); fails 3/3 by design, present
    in all three runs.
  - `FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill`
    (run 1), `Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_…
    WhenServerDies` (run 1), `ThreePeerSeededSessionTests.SeedThreePeers_…
    DeterministicVisibility` (run 2),
    `SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover…`
    (run 2), `LiveWinConditionTests.Board_refused_once_mission_over` (run 2),
    `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (run 3),
    `AddPartTests.Part_pickup_destroys_then_accept_merges…` (run 3) — a
    rotating set of pre-existing live-socket / state-machine load-flakes, every
    one documented in `docs/TEST_FLAKES.md` or verified green in isolation
    (`TcpLoopbackProofTests` 4/4, `AddPartTests` 6/6).

  The P478 tests passed in **all three** runs (absent from every failure list),
  and no pre-existing development-bucket test changed (the P446/P467 live
  E2Es donate `bucket.Price` dynamically, so the now-scaled price is paid
  correctly and the `TimeToBuild`-based completion budgets stay ample for both
  test civs, Dreghklar `GA[24]=0.5` and Ga'Taraan `GA[24]=1`).

## Updated remaining-GA ledger (what is still unwired after P478)

Wired so far across the parity pass: flight (0-3, P446), station durability
(4-7, P475), ship durability (8-10,13, P467b), visibility (11-12, P476),
weapon output (14-16,21,22, P467), mining (17-19, P477), economy cost/time
(**23-24, P478 — this task**).

**Still unwired after P478 — exactly one GA:**

| Index | Constant | Native use |
|---|---|---|
| 20 | `c_gaRipcordTime` | `shipIGC.cpp:4665` |

(`c_gaRipcordTime` is the sole remaining consumer and is a named follow-up,
P479.)

## Scope boundaries honored

Only `c_gaDevelopmentCost` (23) and `c_gaDevelopmentTime` (24) were wired. No
other GA index was touched (P479 covers the remainder). How developments
complete / GA accumulates is unchanged (already correct, P189/P302).
