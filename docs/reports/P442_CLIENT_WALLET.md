# P442 — the client has no wallet: track money from the wire and show it on the HUD

**Landing:** `master` (6f2176e4 P443 prior), committed on `master`, not pushed (Domingo pushes).

## Problem

Domingo: "prices are not there." The MonoGame client had **no concept of money**.
`src/Allegiance.ClientCore.MonoGame` had zero money references; `ClientWorld`
handled none of the money messages; `ConnectionSession` had no
`case SetMoney.Fmid` / `case MoneyChange.Fmid` / `case AutoDonateUpdate.Fmid`;
`PAYDAY(114)` was dispatched only into the event log. The wire codecs
(`Core/Wire/SetMoney.cs`, `MoneyChange.cs`, `Payday.cs`, `AutoDonateUpdate.cs`)
existed but nothing consumed them. The server sent the right per-player data
in most places, and the joiner's own join-time `SET_MONEY` sent the **side
treasury** instead of the player's wallet — a stale comment claimed "no
per-player wallets", which P197 had made false.

## Pre-fix failing evidence (must-fail)

The live loopback tests (`ClientWalletLiveTests`, real `ConnectionSession` +
`ClientWorld` over loopback UDP against an in-process `Server`) were each
proven **red** when the fix is removed (temporarily reverted, watched red,
reverted back):

| Reverted change | Test | Pre-fix result |
|---|---|---|
| Join-time `SET_MONEY` back to `(int)smSide.Money` (side treasury) | `Join_SetMoney_CarriesTheJoinersWallet_NotTheSideTreasury_Live` | RED — joiner's `MyMoney` = 95444 (the treasury sentinel), not its own wallet 0 |
| `ClientWorld.Apply(MoneyChange)` restored to native's "skip when `sidFrom == my ship`" | `BucketDonation_DecrementsDonorClientsMyMoney_ExactlyOnce_Live` | RED — donor client's `MyMoney` never fell (the whole-message skip hides the donor's own spend) |
| `ClientWorld.Apply(Payday)` reduced to event-log-only (no crediting) | `Payday_CreditsLeader_KeepsDonorsFlat_NonDonorGetsShare_Live` | RED — the leader client's `MyMoney` never grew |

Each reverted to the real implementation after confirming red; the final tree is
green for all of them.

## Scope 1 — ClientWorld money state

Per-player money and auto-donate targets, keyed by ship id (mirroring native,
which keeps both on `PlayerInfo`), in `ClientWorld`:

- `WalletByShipId` / `AutoDonateTargetByShipId` dictionaries.
- `MyMoney` (int?) — the local player's wallet; `null` until the first
  `SET_MONEY` for the local ship is decoded ("unknown", distinct from 0).
- New `ConnectionSession` dispatch cases next to `Payday` (same
  decode→`_world.Apply` shape): `SetMoney.Fmid`, `MoneyChange.Fmid`,
  `AutoDonateUpdate.Fmid`.

### Per-message semantics (native `clintlib/appmsg.cpp`)

- **SET_MONEY (115)** `appmsg.cpp:1367-1383` — set that ship's player's money
  absolutely. `Apply(SetMoney) => _moneyByShipId[m.ShipId] = m.Money`.
- **MONEY_CHANGE (60)** `appmsg.cpp:1326-1366` — see the *decision* below.
- **AUTODONATE (116)** `appmsg.cpp:1192-1278` — set `SidDonateBy`'s target
  (NA ⇒ donates to no one), then if `Amount != 0` apply the one-time gift:
  `+amount` to the target, `-amount` to the donor (see the gift note below).
- **PAYDAY (114)** `appmsg.cpp:1281-1323` — for every player ship on my side,
  credit `dMoney` to that ship's auto-donate target if it has one, else to the
  ship itself. The port's server sends PAYDAY and credits wallets itself with
  **no** MONEY_CHANGE at payday (`Server.Economy.cs OnPayday`), so the client
  derives the credit from this message + the relationships it already holds —
  exactly as native does.

### The `sidFrom == my ship` decision (documented divergence)

Native skips the *entire* MONEY_CHANGE when `sidFrom == my ship`
(`appmsg.cpp:1334`) because the local client already applied the change at
purchase time. **This port applies it always** — `AddMoney(sidTo, dMoney)`
unconditionally, plus `AddMoney(sidFrom, -dMoney)` when `sidTo != sidFrom &&
sidFrom != NA` (a player-to-player donation) — because:

- the port's server is authoritative, and the MonoGame client **never** applies
  purchases locally;
- therefore the donor's own wallet must be decremented *by the wire broadcast*,
  and the only carrier is this MONEY_CHANGE.

Verified interactions (the brief's check-list):
- **Bucket spend** `Server.Economy.cs:580-591` broadcasts
  `dMoney=-spent, sidTo==sidFrom==donor` side-wide. Applying always adds
  `-spent` to `sidTo` (the donor) and, since `sidTo==sidFrom`, does no second
  debit → the donor's wallet falls by exactly `spent`, **once**. (Native's
  skip would hide this — the must-fail proof above.)
- **P441 purchase broadcast** `sidFrom = NA, sidTo = buyer`: applying adds
  `-cost` to `sidTo` (the buyer) → correct for the buying client.
- **Bucket excess refund** `dMoney=+refund, sidFrom=NA, sidTo=donor`
  (`Server.Economy.cs:561`): adds `+refund` to the donor, no p2p branch → correct.

One known faithful quirk worth noting: the P203 no-op `PLAYER_DONATE` bounce
(`Server.Economy.cs:380`, `dMoney=+given, sidTo=sender, sidFrom=NA`) will credit
the sender `+given` — native does exactly the same literal thing (it adds
`dMoney` to `sidTo` whenever `sid != sidFrom`, which is true for `sidFrom=NA`),
so this is reproduced, not introduced, and that bounce only fires on a refused
donation.

### AUTODONATE gift note

Native skips debiting the donor when `pshipBy == m_ship` (already applied
locally). This port always debits the donor, once, whether or not the donor is
the local ship — same server-authoritative rationale; the AUTODONATE `amount`
is the *only* wire carrier of the gift (the server's `SetAutoDonate` moves the
wallets and sends no separate MONEY_CHANGE).

## Scope 2 — join-time SET_MONEY fix

`Server.Lobby.cs` join burst (previously ~:2354, now `CompleteJoinAndLaunch`):
changed `new SetMoney { Money = (int)smSide.Money, ShipId = shipId }` to
`Money = live.Wallet.Money`, and rewrote the stale comment that claimed "this
port tracks ONE pooled number per side … no per-player wallets". The side
treasury accounting itself (`Side.Money` accrual/distribution) is untouched —
only the value of the joiner's own SET_MONEY.

## Scope 3 — HUD money readout

- `HudFormat.FormatMoney(int)` — pure formatter matching native
  (`wsprintf("%d", pplayer->GetMoney())`, `teampane.cpp:208,226`; `"$%d"`
  text, `appmsg.cpp:1317-1321`): `"$" + invariant decimal`, no grouping.
- **Flight HUD** — `HudFrame` gained `int? Money` (default null); `DrawHud`
  feeds `world.MyMoney`; `HudRenderer.DrawMoney` draws a small teal `MONEY $X`
  readout top-left (above the kill bonus), rendered only when the wallet is
  KNOWN (null ⇒ nothing, never 0).
- **Docked hangar** — `DockedScreenRenderer.Draw` gained an optional `int?
  money`; both hangar and teleport docked-screen call sites pass
  `world.MyMoney`, drawn bottom-left. Same "unknown ⇒ nothing" rule.

## Tests added

- `tests/Allegiance.ClientCore.Tests/ClientWorldMoneyTests.cs` (14) — pure
  derivation: SET_MONEY / MyMoney-null semantics, MONEY_CHANGE bucket-spend
  exactly-once, purchase (sidFrom=NA), bucket refund, p2p donation,
  double-count guard, AUTODONATE gift both ways + relationship clear, PAYDAY
  leader/donor/non-donor routing, Reset.
- `tests/Allegiance.ClientCore.Tests/ClientWalletLiveTests.cs` (3) — real
  server loopback: join SET_MONEY = wallet not treasury, payday routing
  matching server wallets, bucket donation decrement exactly-once.
- `tests/Allegiance.ClientCore.MonoGame.Tests/HudFormatTests.cs` (+7) — the
  money format string ("$", no grouping, negative "−$").

## Build / test status

- Whole-solution build via `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`:
  **0 warnings, 0 errors**.
- Full suite (`/home/domingo/.dotnet/dotnet test AllegianceCore.slnx -c Debug`)
  3 consecutive clean runs; only the documented live-wire timing flakes were
  seen, each re-run green in isolation (see the "Six documented flakes"
  list — LiveTurretGunner, LiveWinCondition, LiveCombatWire, etc.).

## Scope boundaries honored

- **Shop prices / greying-out unaffordable items / fail-open tech filter →
  P468** (explicitly out of scope; `WalletByShipId` is the data P468 will read).
- Server pricing, funds and tech checks (P441), starting money & station income
  (P443), investment/bucket UI — untouched.
- No server money-semantics change other than the one join-time `SET_MONEY`
  value.

## Citations kept on file

- Native: `Allegiance/src/clintlib/appmsg.cpp` (1192-1278 AUTODONATE,
  1281-1323 PAYDAY, 1326-1366 MONEY_CHANGE, 1367-1383 SET_MONEY);
  `FedSrv/fsmission.cpp:4270-4299` SendMissionInfo; `WinTrek/teampane.cpp:208,226`.
- Port: `Server.Economy.cs` (OnPayday, HandleBucketDonate,
  BroadcastBucketDonateSpendSideWide, SendMoneyChangeToSideGroup,
  SyncMoneyStateToJoiner); `Server.ShipCommands.cs:2080-2097` (P441 buy
  broadcast); `Server.Lobby.cs` (join-time SET_MONEY).
