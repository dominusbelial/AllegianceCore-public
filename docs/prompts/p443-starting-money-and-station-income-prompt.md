# P443 — no starting money and no station income: seed the side pot and add station income to payday

## Why this task exists

Income in the port is thin and unanchored. This was verified on 2026-09-29;
line numbers are at `HEAD` `d5df96ec`, so re-check them. It matters as soon as
P441 makes purchases cost money, because today a player starts with 0.

- **Native seeds every side's pot at game start.** `fsmission.cpp:1992-2001`:
  `moneyStart = GetFloatConstant(c_fcidStartingMoney)`, then per side
  `m_rgMoney[side] = moneyStart × (MissionParams.fStartingMoney +
  civ.GetBonusMoney())` when that multiplier is > 0, else 0. Every ship's
  personal money is reset to 0 and non-leaders auto-donate to the leader
  (`:2004-2023`). `missionigc.cpp:2697,2752` override the constant slot with
  the hardcoded `c_fStartingMoney = 16000.0f`. That is the same override
  pattern the port already follows for payday by hardcoding
  (`Mission.cs:280-298`: `SecondsBetweenPaydays = 60`,
  `IncomePerPayday = 600`, from `missionigc.cpp:2691,2722`).
- **Native pays that pot out immediately at start.** After `EnterGame`, the
  start sequence ends with `DoPayday()` (`fsmission.cpp:2278`), and the next
  payday is armed at `now + SecondsBetweenPaydays` (`:2253`). The port has
  **no starting money anywhere** (no `StartingMoney` in `src`). Its first
  payday fires at `start + 60s` (`Mission.cs:597-598`).
- **Native payday includes station income.** `fsmission.cpp:2538-2546`: each
  payday also adds Σ over the side's stations of
  `GetStationType()->GetIncome()` to `m_rgMoney[side]`. The port's payday
  (`Mission.cs:588-607`) adds only the base `IncomePerPayday × (1 +
  civ.IncomeMoney)`. Its comment says station income "is omitted — always zero
  here because the server has no stations" (`Mission.cs:592-593`). That has
  been false since stations became runtime objects (`Sector.StationRuntimes`,
  `Sector.cs:54`; `Sim/Station.cs:85`, with `Type` at `:102` and `SideId` at
  `:123`). `DataStationTypeIGC.Income` is decoded (`DataStationTypeIGC.cs:37,
  276`) and read nowhere.
- `DataCivilizationIGC.BonusMoney` is decoded (`DataCivilizationIGC.cs:13,58`)
  and also unused.
- Payout mechanics already exist and are faithful: `Server.OnPayday` splits
  `side.Money / playerCount` and honours auto-donate
  (`Server.Economy.cs:~800-862`).

## Scope

1. **Starting money.** When a mission enters `Started`, seed each side's
   `Side.Money` with `16000 × (fStartingMoney + civ.BonusMoney)` (0 if the
   multiplier is ≤ 0). The native MissionParams header that holds
   `fStartingMoney`'s default **is not in this checkout**: only
   `AGCGameParameters.cpp:1058,1074` and WinTrek's "StartingMoneyValues" combo
   (`newgamescreen.cpp:194,1009`) are. **Use a multiplier of 1.0** as a named
   constant with that honesty note, like the existing hardcoded payday
   constants. Add no config.
2. **Immediate payout at start**, mirroring `fsmission.cpp:2278`. Right after
   seeding, run the existing payday payout for each side through the same seam
   the periodic payday uses (`Site.PaydayEvent` → `Server.OnPayday`). Don't
   write a second payout path. The periodic cadence must stay `start + 60s`,
   `+60s` thereafter.
3. **Station income on each payday.** In the payday pass (`Mission.cs:600-606`)
   add Σ `station.Type.Income` over every station runtime in the mission whose
   `SideId` is that side, per `fsmission.cpp:2538-2546`. Captured and destroyed
   stations must be counted by current ownership, and that must fall out of
   reading live station state. Fix the stale comment at `:592-593`.
4. **Debug mode:** no special case. There is no economy precedent for
   `liveSettings.debugMode`. Check that the P387 debug auto-start still reaches
   `Started` and so gets starting money, and say so in the report.

## Explicitly out of scope

- Server buy pricing (P441), the client wallet/HUD (P442), shop prices (P468)
  and BuildingTechs (P444).
- Reading `c_fcidIncome`/`c_fcidSecondsBetweenPaydays` from the core instead of
  the hardcoded constants.
- He3 ore value (`Ship.OreCreditPerUnit = 5`, `Ship.cs:2069`), a known
  labelled simplification that isn't changed here.
- P439's mission rotation: if P439 has landed, make sure a rotated fresh
  mission also gets starting money when it starts, and note it. Otherwise
  leave it alone.

## Verification

- Loopback wire tests with `Bot` + in-process `Server`. Run each **against
  the unfixed code first** and record the failure in the report:
  1. Start a 2-side game with one player per side: right at `Started`, each
     player receives a `PAYDAY(114)` and their server wallet equals
     `16000 × (1 + civ.BonusMoney)` for their side's real civ, with
     integer-division remainder rules as in `OnPayday`. The next payday comes
     60s later, not immediately again.
  2. A side owning N real stations earns exactly `600 × (1 + IncomeMoney) +
     Σ Type.Income` more in its payday than it did before the fix. Use real
     PCore001 station types and state their real `Income` values in the
     report.
  3. After a station changes owner (`Station.Capture`), its income moves to the
     new owner on the next payday.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: 0 warnings,
  0 errors. Bare `dotnet` is a broken snap stub.
- Full suite with `/home/domingo/.dotnet/dotnet test`, **3 consecutive clean
  runs**. Existing payday tests may assert old amounts. Update them only to
  the new native-correct values, and list each one. Only the pre-existing
  documented live-wire timing flakes are acceptable, and each one seen must be
  named.

## Deliverables

1. The change plus tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P443_STARTING_MONEY_STATION_INCOME.md` with the
   pre-fix failing-test evidence, the real per-civ starting amounts and
   per-station-type incomes from PCore001, the fStartingMoney=1.0 honesty
   note, and every test whose expected amount changed.
