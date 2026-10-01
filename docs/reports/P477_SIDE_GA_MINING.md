# P477 — the side's GlobalAttributeSet scales He3 mining rate, yield and capacity

The economic half of the side-global-attribute (GA) wiring. The parity pass
found mining GA (computed, composed into `side.GlobalAttributes` and broadcast
as `SIDE_ATTRIBUTE_CHANGE(76)`) never applied: "+x% mining rate / yield /
capacity"-style research changed nothing in the miner sim or the He3 payout.
P446 wired flight, P467/P467b wired combat & durability, P476 wired visibility;
this task wires the three mining attributes through the same machinery.

Branch: `master` (verified with `git branch --show-current`). **Committed, not
pushed.**

---

## 1. P446/P467 accessor machinery — reused, not reinvented

- **Per-side resolution** — P446's private `Ship.SideGlobalAttributes()`
  (manual `foreach` over `Mission.Sides`, identity when `SideId` is null or the
  side is unresolvable) is the single resolution path.
- **The GA-aware accessors** — P477 adds three thin reads over it, each a raw
  base × the owning side's factor, in the same style as P446/P467b's
  `Effective…()` methods:

  ```csharp
  // Ship.cs
  public float EffectiveMiningRate()      => Sector.MiningRate * SideGlobalAttributes().Get(GlobalAttributeSet.MiningRate);
  public float EffectiveMineCapacity()    => MineCapacity       * SideGlobalAttributes().Get(GlobalAttributeSet.MiningCapacity);
  public float EffectiveOreCreditPerUnit()=> OreCreditPerUnit   * SideGlobalAttributes().Get(GlobalAttributeSet.MiningYield);
  ```

  The raw bases are the port's established flat stand-ins — `Sector.MiningRate`
  (100/s) for the native `dT · GA[c_gaMiningRate]` term, `Ship.MineCapacity`
  (150) for the native `c_fcidCapacityHe3` (a real native value,
  `missionigc.cpp:2692`, = 90), and `Ship.OreCreditPerUnit` (5) for the native
  `c_fcidValueHe3` (= 80, `missionigc.cpp:2693`) — each × the side factor, exactly
  the shape native derives (`GetOreCapacity() = c_fcidCapacityHe3 ·
  GA[c_gaMiningCapacity]`). The server-host yield consumer is owned by the
  server, so it reads the ship's accessor (same underlying side set) rather than
  duplicating a second resolution path.
- **Named constants** — P477 adds the three mining indices to
  `GlobalAttributeSet.cs`, citing `igc.h` each: `MiningRate = 17` (igc.h:611),
  `MiningYield = 18` (igc.h:612), `MiningCapacity = 19` (igc.h:613).

Identity (1.0) when `SideId` is null → the no-side path is byte-for-byte the
pre-P477 behaviour.

## 2. Every site wired (native → port)

| GA | Index | Native site | Port site(s) |
|---|---|---|---|
| `c_gaMiningRate` | 17 | `shipIGC.cpp:2212` `minedOre = dT · GA` | `Model/Sector.cs` `ResolveMining`: `minedOre = dt · ship.EffectiveMiningRate()` |
| `c_gaMiningCapacity` | 19 | `shipIGC.cpp:2166,2213`; `shipIGC.h:331` (`GetOreCapacity`), `shipIGC.h:2249` | `Sector.cs` mining gate (`Ore ≥ eff. capacity → skip`) + capacity clamp; `Ship.DecideMiningOrSelling` (mine-vs-sell) + full-bag log; `Ship` idle/full legs; wire ore-fraction (`ServerShipUpdate` byte 45, `HeavyShipsUpdate.From`, `Server.Broadcast` `FractionsOre`) — all `ship.Ore / ship.EffectiveMineCapacity()` (native `SetOre(GetOreCapacity(), m_fOre)`, `shipIGC.h:1128,1148`) |
| `c_gaMiningYield` | 18 | `shipIGC.cpp:1988`; `shipIGC.h:449` (`PaydayEvent` He3 sale `m_fOre · value · GA`) | `ServerHost.DockWithStationEvent` (P56/P443 sell): `proceeds = sh.Ore · sh.EffectiveOreCreditPerUnit()` |

Notes on fidelity:

- **Whose side applies.** Native reads `GetSide()->GetGlobalAttributeSet()` on
  the *miner*'s side for rate and capacity, and `pside` (the ship's own side) for
  the yield sale. The port reads the *selling/mining ship's* accessor throughout
  — the same side set. A sideless ship → identity, so no-side miners fly/sell
  exactly as before.
- **Capacity is applied everywhere capacity is read**, not just at accrual: the
  mining gate (`Sector.ResolveMining`), the capacity clamp, the NPC mine-or-sell
  decision, and — critically — the **wire ore-fraction**, which native computes
  against `GetOreCapacity()` (`shipIGC.h:1128,1148`), the GA-scaled capacity. A
  capacity-raised side fills to more ore and the client shows the same 0..1
  fill fraction against its larger hold — mirroring how P467b wired the energy
  fraction against `EffectiveMaxEnergy`.
- **Economic end to end.** The three attributes compose multiplicatively along a
  trip: a boosted rate fills the (boosted) hold faster, and each unit sold earns
  the boosted yield — the aggregate "more per miner trip" that makes mining
  research worth buying (verified in §4/§5).

## 3. Real developments and civs in PCore001 touching 17/18/19

Scanned `PCore001.igc` (via the port's `IgcReader`/`StaticCore`). Developments
(non-tech-only, with the `gas[i]` delta):

| Dev id | Name | gas[17] miningRate | gas[18] miningYield | gas[19] miningCapacity |
|---|---|---|---|---|
| 145 | GA: Faster He3 1 | **1.1** | 1.0 | 1.0 |
| 146 | GA: Faster He3 2 | **1.1** | 1.0 | 1.0 |
| 147 | GA: He3 Yield 1 | 1.0 | **1.1** | 1.0 |
| 148 | GA: He3 Yield 2 | 1.0 | **1.1** | 1.0 |

Civs with a non-identity base in these three (`GasBaseAttributes`):

| Civ | rate[17] | yield[18] | cap[19] |
|---|---|---|---|
| Belters | 0.60 | **1.25** | 0.80 |
| Dreghklar | 0.65 | **1.35** | 0.95 |
| Technoflux | **2.00** | 1.0 | 0.55 |
| Ga'Taraan | 0.80 | 1.0 | 1.0 |
| Phoenix | 1.0 | **1.1** | 1.0 |
| Omicron | 1.0 | 1.0 | 0.85 |
| Gigacorp | 1.0 | 1.0 | **1.25** |
| Iron Coalition | 0.85 | 1.0 | 0.75 |
| Rixian | 0.90 | 1.0 | 1.0 |
| Bios | 1.0 | 1.0 | 1.0 |

So there ARE real developments for **rate (dev 145) and yield (dev 147)**; there
is **no** development touching **capacity (19)** — only civ bases do — so the
E2E uses dev 145 (rate), and the synthetic test (§5) covers capacity.

## 4. End-to-end: a real development raises real mining accrual

Completed **dev 145 'GA: Faster He3 1'** (`gas[17] = 1.1`) on a real miner's side
(civ 1 Dreghklar, base rate 0.65) through the real funding/completion pipeline
(`BucketDonate → bucket completes → CompleteDevelopment → Apply →
side.GlobalAttributes[17]`), and showed a real miner's per-tick ore accrual rise
by exactly that factor over a fixed tick window:

```
P477-E2E: dev 145 'GA: Faster He3 1' (gas[17] 1.100) complete on side 0;
          12-tick ore 97.5 -> 107.2, ratio 1.100 (expect ~1.1; civ base rate 0.650)
```

(The run also identifies the yield sibling — dev 147 'GA: He3 Yield 1',
`gas[18] = 1.1` — whose economic effect is asserted by the MiningSellTests update
in §5.)

## 5. Verification

**Each P477 behavioural test fails on the unfixed code first — recorded.**
Temporarily reverting the four wiring lines (Sector mining gate + accrual,
ServerHost yield) while keeping the constants + accessors produced failures on
the unfixed code:

```
MiningRate_GA_scales_ore_accrued_per_tick     FAIL   (rate GA had no effect)
MiningCapacity_GA_scales_the_capacity_clamp   FAIL   (capacity GA had no effect)
Indices_and_accessors_match_native_...        PASS   (plumbing, GA-agnostic)
MiningYield_GA_scales_credits_per_unit        PASS   (accessor test — plumbing)
```

Restored, all pass, with the exact ratios:

```
P477-RATE:  2 ticks ore identity=50.0, rate2.0=100.0, ratio 2.000 (expect ~2)
P477-CAP:   identity capped at 150; capacity2.0 capped at 300 (expect 300 = 2×150)
P477-YIELD: effective per-unit = 7.50 (base 5 × GA1.5) — 100 ore pays 750
```

New test file: `tests/Allegiance.Core.Tests/P477SideGaMiningTests.cs` — 4 domain
unit tests (each sets a synthetic non-1.0 multiplier on the side's GA and asserts
the scaled value: rate doubles per-tick ore; capacity 2.0 doubles the clamp to
300; yield 1.5 raises the per-unit rate to 7.5; plus index/identity plumbing) +
1 live E2E in `[Collection("LiveNetwork")]`.

**Regression found & fixed (collateral of wiring).** Because real civs have
non-1.0 mining GA bases (Dreghklar cap 0.95, yield 1.35), four pre-existing tests
that assumed the flat constants now fail with the native-faithful values — the
exact P467-missile precedent. Verified each fails on the newly-wired build and
passes pre-P477 (i.e. the new behaviour, not a flake), then made them GA-aware by
folding the miner's side factor in:

- `MinerDecisionCycleLiveTests` — no change needed (its NPC is the other side,
  civ 3 Ga'Taraan: cap 1.0, yield 1.0).
- `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` — miner is
  Dreghklar (cap 0.95) → fills to `EffectiveMineCapacity()` 142.5, not 150.
- `MiningSellTests` (4 tests: `Dock_transition…`, `Redock_after_undock…`, and the
  two loopback sell tests) — selling side is Dreghklar (yield 1.35) → proceeds =
  `ore × EffectiveOreCreditPerUnit()` (5 × 1.35 = 6.75/u), not `ore × 5`; the
  cumulative-treasury assertion sums per-sale `(long)` truncations.

**Whole-solution build** `/home/domingo/.dotnet/dotnet build`: **0 warnings,
0 errors**.

**`dotnet test` — 3 consecutive runs** (Core 3650, ClientCore 351, MonoGame 1053
tests). Every P477 test and the four repaired mining tests passed in **all three**
runs. The only failures are the pre-existing documented flakes/bugs, cited in
`docs/TEST_FLAKES.md` (P471) — a rotating subset each run, **none** touching any
mining/ore/capacity/yield code path:
- `ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` — fails
  3/3, **real production bug** (P472, TEST_FLAKES #1; not P477).
- `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill` —
  runs 2 & 3, suspect live-wire timing (P472).
- run-1-only: `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility`
  (suspect P472), `ChatTargetComposeLiveTests.ComposeDefault_…`,
  `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine…`,
  `AddPartTests.Part_pickup_decline_recreates_treasure…` — load-flakes.
- run-2-only: `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown…`.
- run-3-only: `LoadoutChangeLiveTests.Boarding_and_leaving…`,
  `LiveStationRescueTests.Lifepod_reaching_rescue_station…`,
  `LiveChatSectorWireTests.Individual_AllSector_…`.

## 6. Still-unwired GA consumers (the updated remaining-GA ledger)

P477 wires mining (17–19). Combined with P446 (flight 0–3), P475 (stations 4–7),
P467b (ship durability 8–10, 13), P476 (visibility 11–12), P467 (weapons 14–16,
21–22), the updated ledger of **still-unwired** consumers is:

| Index | Constant | Native use | Owner |
|---|---|---|---|
| 20 | `c_gaRipcordTime` | `shipIGC.cpp:4665` | **P479** |
| 23 | `c_gaDevelopmentCost` | `bucketigc.cpp:72` | **P478** |
| 24 | `c_gaDevelopmentTime` | `bucketigc.cpp:73` | **P478** |
| — | probe projectile speed | `probeigc.cpp:367` (a probe fires its own projectile with its own speed — not a shared-weapon-path one-liner) | named follow-up (folded into P479) |

Everything else (0–19, 21–22) is now wired by P446/P467/P467b/P475/P476/P477.

## 7. Out of scope (unchanged)

- Any GA index not in this task's scope (20, 23, 24 and the probe-projectile site
  belong to P478/P479 — not pre-empted).
- How developments complete or GA accumulates (already correct, P189).
