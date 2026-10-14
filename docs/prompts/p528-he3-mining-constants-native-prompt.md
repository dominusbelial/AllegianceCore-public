# P528 — He3 mining economy: replace the three test-convenience stand-ins (rate ×100, capacity 150, price 5) with native formulas + the loaded core's FloatConstants

## Why this task exists

**Domingo's live observation on 221 (2026-10-04):** he asked Claude to watch
whether the economy loop works. Structurally it does: miners auto-undock,
warp to Alderan, mine, fill, return, sell and relaunch, and payday works. But
the server timestamps showed a **miner filling its hold in 1.3–1.8 seconds**
(`8096 began 15:56:20.8 → full 15:56:22.1` for 112.5 ore; `8097 15:57:10.5 →
15:57:12.3` for 150 ore). Natively a miner sits at the rock for about two
minutes.

Tracing it turned up **three labelled stand-ins**. Each is self-documented
as "a simplification, not a verified native value", chosen so unit tests
finish fast:

| | Native | Port today |
|---|---|---|
| **Mining rate** | `minedOre = dT * ga.GetAttribute(c_gaMiningRate)` (`~/Development/Allegiance/src/Igc/shipIGC.cpp:2212`; same formula client-side at `WinTrek/WinTrek.cpp:7981`). GA baseline 1.0 (port's own `GlobalAttributeSet.cs:29-30` cites `igc.h:923-927`), so ≈ **1 ore/s** × tech | `Sector.MiningRate = 100.0f` (`src/Allegiance.Core/Model/Sector.cs:1515-1525`) × GA via `Ship.EffectiveMiningRate()` (`Sim/Ship.cs:804`), used at `Sector.cs:3679`. **100× too fast** |
| **Hold capacity** | `GetFloatConstant(c_fcidCapacityHe3) * ga[c_gaMiningCapacity]` (`shipIGC.cpp:2213`, also `:2165`, `common.cpp:537`). `c_fcidCapacityHe3 = 7` (`igc.h:66`), default `c_fCapacityHe3 = 90.0f` (`missionigc.cpp:2692`, set at `:2747`) | `Ship.MineCapacity = 150.0f` literal (`Sim/Ship.cs` ~:805-818; its doc admits native is 90) × GA via `EffectiveMineCapacity()` (`:818`) |
| **Sale price** | `PaydayEvent(side, m_fOre * GetFloatConstant(c_fcidValueHe3) * ga[c_gaMiningYield])` (`shipIGC.cpp:1985-1988`). `c_fcidValueHe3 = 8` (`igc.h:67`), default `c_fValueHe3 = 80.0f` (`missionigc.cpp:2693`, set at `:2748`) | flat `OreCreditPerUnit` = **5** × GA via `EffectiveOreCreditPerUnit()` (`Sim/Ship.cs:829`), sold at `src/Allegiance.Server/ServerHost.cs:775-800` (comment `:784` calls it a stand-in). **16× too cheap** |

Net effect: natively a full load is worth about 90 × 80 = 7,200 credits ×
GA. The port pays about 750, and mines it almost instantly. He3 is meant
to be the dominant income source next to the base payday (~525–600/min
observed), so the port's economic balance is badly off.

**The port already reads core float constants elsewhere.** It reads them
from the loaded core rather than hardcoding the wiki number, for example
`FloatConstants[31]` (lifepod endurance, `Sim/Ship.cs:178-185`, P279) and
indices 28/29 (P215), via `Data/DataConstantsIGC.cs`. Follow that pattern
for indices **7 and 8**, falling back to native's defaults (90 / 80) only when
no core is loaded.

## Scope

1. **Rate:** `EffectiveMiningRate()` = `SideGlobalAttributes().Get(MiningRate)`,
   with no ×100 base. Remove or retire `Sector.MiningRate` as a production
   constant.
2. **Capacity:** `EffectiveMineCapacity()` = loaded core
   `FloatConstants[7]` (fallback 90) × GA `MiningCapacity`.
3. **Price:** `EffectiveOreCreditPerUnit()` = loaded core `FloatConstants[8]`
   (fallback 80) × GA `MiningYield`.
4. **Report the real PCore001 values** for indices 7 and 8 (read them; don't
   assume the defaults) and the resulting per-civ capacity, value per load
   and fill time for the two civs on 221 (IC civId 25, GT civId 3).
5. **Tests:** the existing mining tests (`tests/Allegiance.Core.Tests/
   MiningTests.cs`, ~:170 assumes "MiningRate 100/s ⇒ 25 ore per Update";
   `P477SideGaMiningTests.cs` :143-181, :338-359) must not keep production
   on test-speed constants. Make them compute expectations from the
   effective values, or use a **test-only** seam (e.g. an injectable
   rate/capacity override on the mission or ship, internal and
   test-visible) so they still finish quickly. No production behaviour may
   depend on that seam.
6. Update the doc comments that call these "simplifications" to cite the
   native formulas.

## Explicitly out of scope

- The base payday amount and cadence (`Mission.Update` payday). Observed at
  525/600 per ~minute; it may have its own parity question, but it's a
  separate task. Note its observed values in the report for comparison.
- Miner AI routing, asteroid selection, mining-crowding rules, or the
  offload/teleport-unload distance check.
- Asteroid ore pool sizes (they come from the core/map already).

## Verification

- **Failing-first** unit tests: with a GA of 1.0, a ship mines at 1 ore/s
  (not 100); capacity equals core `FloatConstants[7]` × GA; sale proceeds
  equal ore × core `FloatConstants[8]` × GA `MiningYield`. All three must
  fail against today's code.
- A **live headless proof**: run the server/sim loop (the existing vizfight or
  roster tooling, or a Core-level mission run) long enough for one initial
  miner to mine and sell once. Log the real fill time (≈ capacity / rate
  seconds) and the proceeds, and quote those log lines in the report.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs. Cite `docs/TEST_FLAKES.md` for any remaining flake. **Report only
  numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P528_HE3_MINING_CONSTANTS.md`: the
   native-vs-port table above with final values, the real PCore001
   constants, the per-civ numbers, test results, the live proof lines, and
   the out-of-scope payday note.
