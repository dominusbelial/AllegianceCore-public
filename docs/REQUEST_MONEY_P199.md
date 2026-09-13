# P199 — REQUEST_MONEY(182): ask whoever holds the funds

Landing on top of **P197/P198** (per-player `PlayerWallet` + auto-donate).
Branch `master`, prior top commit `2db8f8c P198: BUCKET_DONATE spends from
the donating player's own wallet (P197)`.

## What changed

### New wire codec — `src/Allegiance.Core/Wire/RequestMoney.cs`
`FM_CS_REQUEST_MONEY(182)` (`Inc/Messages.h:934-938`, quoted in full):

```c
DEFINE_FEDMSG(CS, REQUEST_MONEY, 182)
    Money               amount;
    ShipID              shipidRequest;
    HullID              hidFor;
END_FEDMSG
```

`REQUEST_MONEY` sits outside Messages.h's `#pragma pack(1)` regions (the nearest
region is `AsteroidState` at `Messages.h:917-924`, popped before message 179),
so it uses **default MSVC packing**. Scalar widths verified in native:
`Money = AGCMoney` = 32-bit (`AGCIDL.idl:246`); `ShipID`/`HullID` =
`ObjectID` = 16-bit `short` (`igc/igc.h:477,480,483`). Because `amount` (the
4-aligned `Money`) comes FIRST, the two `short`s follow contiguously with no
interior pad and 12 is already 4-aligned — so the codec is a bare
`header(4) + int32 amount@4 + int16 shipidRequest@8 + int16 hidFor@10 = 12 B`
(**no padding**, unlike `AutoDonate(112)` whose leading `ShipID` forced a pad
between itself and `Money`). `Fmid = 182`, `FixedSize = 12`.

This is a `CS` message: the client sends `amount` and `hidFor`; the server
fills in `shipidRequest` and forwards the request unmodified otherwise.

### `REQUEST_MONEY` dispatch + handler — `src/Allegiance.Server/Server.cs`
Added an `else if (fmid == RequestMoney.Fmid)` branch in the C-message switch
(right after the P197 `AutoDonate` branch) and a private
`HandleRequestMoney(ConnectionId from, LiveShip live, RequestMoney req)` —
port of `FedSrv.CPP:5361-5406 case FM_CS_REQUEST_MONEY`:

1. **Gate** on `Stage == Started` (else refuse/log; native `:5363-5364`).
2. **`amount <= 0` refused** — native `TrapHackBoot(amount > 0)` (`:5368`)
   would disconnect a lying client; per this port's standing convention that
   harsh trap is softened to **log-and-ignore** (no forward). 
3. **Recipient resolution** (native `:5375-5396`): start with the sender's own
   `PlayerWallet.AutoDonateTarget`; if that ship is null **or** that player's
   wallet holds **less than** `amount`, run the richest-other search over the
   sender's side excluding the sender, with `moneyMax` seeded at
   `amount - 1` and a strict `>` comparison — so **ties resolve to the first
   ship to reach each new maximum** (first-to-maximum wins, matching native).
   Scope is mission **and** side (side ids repeat across world missions).
4. **Silent drop** when no recipient resolves (empty/solo side): no reply, no
   error (native `:5398` has no `else`).
5. **Forward** a fresh `RequestMoney` with `Amount`/`HidFor` unchanged and
   `ShipIdRequest = sender's ship id` to the resolved recipient's own
   connection via `SendPeer` — a single-target send, not a broadcast.

Crucially, as native does, **this handler never transfers money** — it only
forwards the request. The resolved recipient is expected to answer with its own
follow-up (e.g. an `AUTODONATE(112)` gift). No automatic transfer was added
(out of scope, and would not match native).

### Bot support — `src/Allegiance.Bot/Bot.cs`
Added a `SendRequestMoney(int amount, short hidFor)` client helper, a
`RequestMoneys` accumulator (locked, snapshot-on-read), `ResetRequestMoneys`,
and a `case RequestMoney.Fmid:` in the receive switch — so live tests can
observe the forwarded request landing on the resolved recipient.

## Chat rate-limit mechanism — did this port have one to reuse?
**No.** Native gates `REQUEST_MONEY` behind `CFSPlayer::DecrementChatBudget`
(the chat-spam rate limiter, `FedSrv.CPP:5370`), the same limiter `CHATMESSAGE`
uses. This port has **no chat budget / rate-limit mechanism at all** — nothing
equivalent exists for `CHATMESSAGE` (grep of `Server.cs` for
budget/ratelimit/throttle/spam/cooldown finds only unrelated status-refresh and
launch-cooldown code). Rather than fabricate a limiter for this one task, the
gate is **deliberately skipped and the omission logged** (in the handler comment
and here). Should a future task introduce a shared chat rate-limiter, this is
the documented hook point to attach it.

## Tie-break behavior confirmed for "richest teammate"
Verified against the live native source (`FedSrv.CPP:5378-5395`): the search
seeds `moneyMax = pfmRequest->amount - 1` and uses a strict `>`
(`pfsship->GetMoney() > moneyMax`), assigning `pshipAD = pship` on every
strict improvement. So among ships with **equal** top wallets, the **first
ship iterated that reaches the max wins** — no re-assignment on ties. The port
preserves this exactly (same seed, same `>`, same first-to-maximum outcome).
A live test (`Tied_richest_teammates_forward_to_exactly_one`) verifies that of
two equal-top teammates exactly **one** (never both, never a broadcast) receives
the forward.

## Divergences from the prompt's assumptions (all verified against live source)
- **TrapHackBoot on `amount <= 0`**: the prompt listed "refuse (log, ignore)
  `amount <= 0`", which is what the port does — native actually *disconnects*
  via `TrapHackBoot`. The prompt's reading already matches the port convention;
  the only divergence from native is the softening to log-and-ignore (standing
  port convention, consistent with prior stages).
- **`hidFor` validation**: confirmed native does **not** validate `hidFor` at
  this call site — it is decode-only payload for the recipient's UI, forwarded
  verbatim. The port matches (no hull-catalog lookup).
- **Wire layout**: the prompt did not pin exact byte layout; derived from the
  native typedefs + default MSVC packing (12 B, no pad). A codec round-trip
  test pins the exact bytes (`amount@4`, `shipidRequest@8`, `hidFor@10`).
- **Report location**: the prompt asked for `docs/REQUEST_MONEY_P199.md`, which
  matches the established repo convention (see `docs/BUCKET_DONATE_PLAYER_WALLET_P198.md`).

## Tests added — `tests/Allegiance.Core.Tests/RequestMoneyTests.cs` (7)
All live-loopback over real wire, observing the forward via the Bot
accumulator. Suite baseline 2987 → **2994**.

1. `RequestMoney_codec_round_trips_exact_bytes` — pins `Fmid=182`, `FixedSize=12`,
   and the exact `amount@4 / shipidRequest@8 / hidFor@10` byte layout.
2. `Request_goes_to_auto_donate_target_when_it_holds_enough_even_if_not_richest`
   — **⭐ the must-fail-if-removed discriminator for AD-preference**: requester's
   AD target holds `100 >= 50` while a different teammate is richer (`500`); the
   forward must go to the **AD target**, not the richest teammate. Proven red
   when the AD fast-path is disabled (request went to the 500-credit teammate).
3. `Request_goes_to_richest_other_teammate_when_auto_donate_target_lacks_funds`
   — AD target holds `10 < 50`, richer teammate holds `700` → forward goes to
   the richer teammate.
4. `Tied_richest_teammates_forward_to_exactly_one` — two equal-top teammates →
   exactly one receives it (first-to-maximum tie-break; no broadcast/double).
5. `Solo_player_request_is_silently_dropped` — a leader alone on a side → no
   recipient resolves → nothing forwarded, no echo to self.
6. `Non_positive_amount_is_refused_no_forward` — `amount = 0` and `-5` are
   refused even though a funded recipient exists.
7. `Multi_mission_request_forward_scopes_per_mission_and_side` — a request on
   mission A side0 is forwarded to its one recipient there and **never** reaches
   a peer on mission B (shared numeric side id) nor a peer on a different side
   of mission A, nor echoes back to the requester.

### Discrimination ("must fail if removed") verification
- Removing the forward `SendPeer` → the 4 forwarding live tests FAIL red; the
  codec/solo-drop/non-positive tests (which don't depend on the forward) stay
  green — exactly the expected split. Reverted.
- Disabling the AD-preference fast-path (forcing always-richest) → test 2 FAILS
  (request went to the richer teammate, `C=1, A=0`) — proving the
  AD-preference logic is genuinely exercised. Reverted.

## Verification
- `/home/domingo/.dotnet/dotnet build tests/Allegiance.Core.Tests/...` (builds
  Core + Server + Bot + Lobby transitively) — clean, 0 warnings / 0 errors.
- New `RequestMoneyTests` green in isolation (**7/7**).
- Full solution `dotnet test`, Debug: three consecutive clean runs — see run
  log below; any failures cross-checked against the seven documented flakes.
