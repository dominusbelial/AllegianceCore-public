# PAYDAY_P51 — wire up payday so real sides earn money on a 60s cadence

**Scope:** `AllegianceCore`-only, pure server-side sim + wire-message port (the
same shape as P47/P48's CLUSTERINFO arc). No native-repo changes, no Windows
VM, no client work — a real WOPR bot needs **zero** client changes because it
already ships the handler this message feeds
(`~/Development/Allegiance/src/Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:697`,
`MessageReceiver_FMD_S_PAYDAY`).

## What is now real

1. **A money field on `Side`.** `src/Allegiance.Core/Model/Side.cs`
   (`Money`, a `long`, initial 0). Port of the FedSrv accumulator
   `m_rgMoney[sideID]` (`fsmission.cpp:61,3504` start 0; `:2536` accrues;
   `:2309` drains).
2. **A 60s timer in the real tick path.** `Mission.Update`
   (`src/Allegiance.Core/Model/Mission.cs`), right beside the side pass it
   already owns. `NextPaydayTime` seeds lazily on the first `Started` tick to
   `now + 60` (the faithful stand-in for `m_timeNextPayday = missionStart + 60`,
   `fsmission.cpp:2253`) and re-arms `+= SecondsBetweenPaydays` keeping phase
   (`fsmission.cpp:2519`) — no drift accumulation.
3. **The reference accrual formula.** Per side per payday,
   `income = 600 · (1 + IncomeMoney)` (`fsmission.cpp:2536`, `c_fIncome=600.0f`
   `missionigc.cpp:2722`; `IncomeMoney` already parsed from the `.igc` into
   `DataCivilizationIGC`), added to `Side.Money` and reported through the
   `IIgcSite.PaydayEvent` seam (`igc.h:4864`).
4. **The site hook is real.** `ServerHost` overrides `PaydayEvent` and raises a
   `Payday` delegate (`src/Allegiance.Server/ServerHost.cs`), wired by `Server`
   at boot.
5. **A real wire codec.** `src/Allegiance.Core/Wire/Payday.cs` — `FM_S_PAYDAY`
   fmid **114** (`Messages.h:747`), single field `Money dMoney`. `Money` is
   `AGCMoney` = `typedef long` (`AGCIDL.idl:246`), a **32-bit** integer on the
   native MSVC toolchain, so the payload is a 4-byte int32 and `cbmsg = 8`.
6. **Per-side broadcast.** `Server.OnPayday`
   (`src/Allegiance.Server/Server.cs`) mirrors `CFSMission::DoPayday`
   (`fsmission.cpp:2294-2326`): count the side's connected peers (same peer
   grouping the chat `Team` relay uses, `Server.cs:1124-1128`), `delta =
   Money / playerCount`, send `PAYDAY(114)` to **that side's own peers only**
   (the reference scopes the send to the side's peer group, so the payload has
   no sideID/shipID), then `Money -= delta·np` (remainder stays in the treasury).
   Log line: `[payday] side {id} '{name}' -> PAYDAY(114) delta={delta} balance={bal}`.

## Deliberately omitted (out of scope, stated explicitly)

- **Station-income term** (`fsmission.cpp:2538-2546`). Always zero here
  because this server has no stations — it is **dropped, not stubbed**; there
  is no fabricated station concept to "complete" the formula. (The term is
  additive and orthogonal to docking.)
- Docking, stations, mining, tech/buckets, `MONEY_CHANGE`(60),
  `SET_MONEY`(115), `REQUEST_MONEY`(182) — separate follow-up arcs.
- The reference `bAllowDevelopments` gate (`fsmission.cpp:2520`) around the
  accrual: this port has no developments flag, so accrual is unconditional.

## Proven here (loopback)

`tests/Allegiance.Core.Tests/PaydayTests.cs` (5 tests, deterministic):

- **Codec**: round-trips; emits the exact 8 declared bytes
  (`cbmsg=8`, `fmid=114`, int32 `dMoney`), hand-composed from `Messages.h:747`
  + `AGCIDL.idl:246`.
- **Cadence + formula + single-fire (sim level)**: two sides driven at
  exactly-representable `0.25s` steps; first payday fires on the tick reaching
  `60.25` (= seed `0.25+60`), exactly one `PaydayEvent` per side, none before
  60s, none doubled inside an interval; accrued `Side.Money` equals
  `(long)(600 · (1 + IncomeMoney))` computed independently from each civ's
  decoded `IncomeMoney`.
- **Wire cadence + per-side scoping (real TCP)**: two `Bot`s on two sides;
  across two boundaries each bot decodes exactly one `PAYDAY(114)` per boundary
  with its own side's formula value, none early / none doubled; treasuries
  drained to `< playerCount`.
- **Zero-player side**: a side with no connected peers accrues every payday but
  never pays out — no divide-by-zero, no crash, matching the reference
  `if (np != 0)` guard (`fsmission.cpp:2298-2299`). The populated side pays
  normally.

**3 consecutive clean runs:** 2240/2240 (baseline 2235/2235 + 5 added; no test
removed).

## What a follow-up live pass must still confirm

This task proves correct cadence, formula, and per-side broadcast via loopback
(real encoded/framed/CRC'd messages over TCP, decoded by the ported `Bot`). A
**live pass against a real `Wopr.exe`** is still needed to confirm the actual
in-game effect:

- `CommanderResearchAndExpand`'s own `MessageReceiver_FMD_S_PAYDAY`
  (`CommanderResearchAndExpand.cs:697`) **observing real, increasing money** over
  a multi-minute live session — i.e. that the decoded `dMoney` actually lands in
  the bot's wallet and compounds on each 60s boundary. The handler exists and is
  wired for exactly this message; it has never been exercised against this
  server because the message was previously never sent.
- That the bot's researcher/commander then *spends* that money (research, station
  building) — the actual metagame unlock this slice is the precondition for.
  That needs the follow-on docking/stations/mining arcs, so it is explicitly a
  later milestone, not part of this one.

Standard live harness per the P41→P42/P45-46→live-pass/P47→P48→live-pass
precedent (`tools/vizfight` or similar).
