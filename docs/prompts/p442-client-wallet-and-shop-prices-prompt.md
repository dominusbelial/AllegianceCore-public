# P442 — the client has no wallet: track money from the wire and show it on the HUD

## Why this task exists

Domingo: "prices are not there." The MonoGame client has **no concept of
money at all.** This was verified on 2026-09-29; line numbers are at `HEAD`
`d5df96ec`, so re-check them.

- `src/Allegiance.ClientCore.MonoGame` has **zero** references to money.
- `ClientWorld` handles none of the money messages. `ConnectionSession.cs` has
  no `case SetMoney.Fmid`, `case MoneyChange.Fmid` or
  `case AutoDonateUpdate.Fmid`. `PAYDAY(114)` is dispatched
  (`ConnectionSession.cs:659`) only into the event log
  (`ClientWorld.cs:679`, `Apply(Payday m) => _events.Record(m)` at `:1612`).
  The wire codecs exist: `Core/Wire/SetMoney.cs`, `MoneyChange.cs`,
  `Payday.cs`, `AutoDonateUpdate.cs`.
- The server already sends the right per-player data in most places:
  - `SyncMoneyStateToJoiner` (`Server.Economy.cs:~423-442`) sends each
    teammate's `AUTODONATE(116)` and `SET_MONEY(115)` (their **wallet**) to a
    joiner.
  - Bucket donations, payday and (after P441) purchases all broadcast
    `MONEY_CHANGE(60)`.
  - **Exception:** the joiner's *own* join-time `SET_MONEY` in
    `CompleteJoinAndLaunch` sends the **side treasury**,
    `new SetMoney { Money = (int)smSide.Money, ... }`
    (`Server.Lobby.cs:2354`). Its comment (`:~2336-2346`) says "this port
    tracks ONE pooled number per side … no per-player wallets." That has been
    false since P197 added `PlayerWallet`. Native sends the player's own money
    (`fsmission.cpp:4270-4299`, `SendMissionInfo`).
- **Native client semantics**, from `clintlib/appmsg.cpp`, to mirror exactly:
  - `FM_S_SET_MONEY` (`:1367-1380`): set that ship's player's money.
  - `FM_S_MONEY_CHANGE` (`:1326-1365`): if `sidFrom != my ship`, add `dMoney`
    to `sidTo`'s player; if `sidTo != sidFrom && sidFrom != NA` it is a
    player-to-player donation, so also subtract from `sidFrom`. When
    `sidFrom == my ship` the local client already applied it and ignores the
    message.
  - `FM_S_PAYDAY` (`:1281-1320`): for every **player** ship on my side, credit
    `dMoney` to that ship's **auto-donate target** if it has one, else to the
    ship itself. So the client needs each teammate's auto-donate target, which
    comes from `AUTODONATE(116)`.

## Scope

1. **ClientWorld money state.** Track money per ship id, at least for the
   local player and ideally for side teammates, since native keeps it per
   `PlayerInfo`. Handle `SET_MONEY`, `MONEY_CHANGE`, `PAYDAY` and
   `AUTODONATE(116)` with exactly the native semantics above. Add the
   `ConnectionSession` dispatch cases next to the existing `Payday` case, in the
   same decode→`_world.Apply` shape. Expose `MyMoney` (null until known).
   The port's client does not apply purchases locally, so decide how
   `sidFrom == my ship` should be handled, and document it. The likely answer
   is to apply it, because the port's server is authoritative. **Check how
   P441's purchase broadcast (`sidFrom = NA`) and the bucket-donate spend
   (`sidFrom == sidTo == donor`, `Server.Economy.cs:580-591`) interact with
   that rule, and make sure the donor's own wallet is decremented exactly once.**
2. **Fix the join-time SET_MONEY** at `Server.Lobby.cs:2354` to send
   `live.Wallet.Money`, and correct its stale comment. Don't touch the
   side-treasury accounting itself.
3. **HUD money readout.** Put a small, always-visible money display on the
   flight HUD and in the docked hangar, following the existing HUD text
   conventions (`Rendering/HudRenderer.cs`, `HudFormat.cs`). Format it the way
   native does; find native's money formatting in WinTrek. When money is
   unknown, show nothing rather than 0.

## Explicitly out of scope

- **Shop prices and greying out unaffordable items**, and the fail-open
  tech filter. That's **P468**; split from this task to fit the session.
- Server pricing, funds and tech checks (P441). Starting money and station
  income (P443). Investment/bucket UI.
- Changing any server money semantics other than the one join-time
  `SET_MONEY` value.

## Verification

- Loopback wire tests using a real `ConnectionSession` + `ClientWorld` against
  an in-process `Server`, following existing ClientCore live-test patterns. Run
  them **against the unfixed code first** and record the failure:
  1. Join: `ClientWorld.MyMoney` equals the server's `live.Wallet.Money`, not
     the side treasury. Set the two to different values in the test to prove
     it.
  2. Payday with auto-donate to the leader: the leader client's `MyMoney` grows
     by `dMoney × (number of donors + 1)`, a donor's stays flat, and a
     non-donor's grows by `dMoney`. All three must match the server wallets.
  3. A bucket donation (existing `Bot`/`LiveBucketTests` path) changes the
     donor client's `MyMoney` by exactly the amount spent, with no
     double-count.
- A pure test for the HUD money format string.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: 0 warnings,
  0 errors. Bare `dotnet` is a broken snap stub.
- Full suite with `/home/domingo/.dotnet/dotnet test`, **3 consecutive clean
  runs**. Only the pre-existing documented live-wire timing flakes are
  acceptable, and each one seen must be named.

## Deliverables

1. The change plus tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P442_CLIENT_WALLET.md` with the pre-fix
   failing-test evidence, the per-message semantics and the
   `sidFrom == my ship` decision, the join `SET_MONEY` fix, and the HUD
   placement.
