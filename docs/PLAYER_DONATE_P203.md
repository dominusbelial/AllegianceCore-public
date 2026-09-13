# PLAYER_DONATE (67) — P203

Manual, direct player-to-player gift. Port of the native `case FM_C_PLAYER_DONATE`
handler (`FedSrv/FedSrv.CPP:7213-7262`) plus the `PLAYER_DONATE(67)` wire codec
(`Inc/Messages.h:527-530`). Landed on top of P202; baseline 3019, this stage
adds 7 tests (see bottom).

Depends on two already-landed stages: **P197** `PlayerWallet` (the per-player
wallet this task reads and writes) and **P190** `Side.AllyGroup` (the
allied-side eligibility). No further dependencies.

---

## What changed

1. **`src/Allegiance.Core/Wire/PlayerDonate.cs`** (new) — the `FM_C_PLAYER_DONATE`
   codec, fmid 67. Same shape as `SET_MONEY`: `Money moneyGiven` (int32 @4) +
   `ShipID shipID` (short @8) + 2 trailing pad bytes @10-11 = **12 B fixed**.
   Verified against native: `PLAYER_DONATE` sits at `Messages.h:527`, well before
   the first `#pragma pack(1)` region at `:637`, so default MSVC packing applies
   and the single trailing `ShipID` rounds the struct up to the 4-byte alignment
   of the `Money` member (the exact reasoning `SET_MONEY` already documents).

2. **`src/Allegiance.Bot/Bot.cs`** — `SendPlayerDonate(short shipId, int moneyGiven)`
   helper so live tests can drive the real message over the wire.

3. **`src/Allegiance.Server/Server.cs`**
   - Dispatch: an `else if (fmid == PlayerDonate.Fmid)` branch next to the other
     money handlers (P197/P199), decoding + `HandlePlayerDonate`.
   - `HandlePlayerDonate(from, live, req)` — the handler.
   - `AlliedSides(Mission, short?, short?)` — the port of native
     `IsideIGC::AlliedSides`.
   - `SendMoneyChangeToSideGroup(Mission, sideId, MoneyChange)` — a
     mission+side-scoped MONEY_CHANGE broadcast.

4. **`tests/Allegiance.Core.Tests/PlayerDonateTests.cs`** (new) — 7 tests.

---

## The exact `AlliedSides` comparison found in native — and that it matches P190

Native `IsideIGC::AlliedSides` (`Igc/igc.h:4297-4304`):

```c
static bool AlliedSides(IsideIGC *side1, IsideIGC *side2)
{
    if (side1 == side2) return true;
    if (side1 == NULL)  return false;
    if (side2 == NULL)  return false;
    if (side1->GetAllies() == NA) return false;
    return (side1->GetAllies() == side2->GetAllies());
}
```

That is: **same side → true; either side absent → false; side1's allies group is
`NA` → false; otherwise the two sides are allied iff their `GetAllies()` values
are equal.**

This is exactly the P190 `Side.AllyGroup` semantics (two sides allied iff they
share the same **non-null** `AllyGroup` value; `null` = native `NA`, igc.h:471).
Confirmed, not assumed. The port's helper maps: `a == b` (same side id) → true;
either id null → false; the donor's side `AllyGroup` null → false; else equal
group values. No behavioral gap.

---

## One-send vs two-send broadcast scoping — measured precisely

This is the one place the task brief's model differs from the literal native
code, and it matters for why the port sends the way it does.

The brief asserted: "one send to the sender's side group only when same-side;
two sends (to both groups) when cross-side/allied." Reading the live handler
carefully shows the literal code is different:

```c
bool bAllied = IsideIGC::AlliedSides(pfsPlayer->GetSide(), pfssTo->GetSide());
if (pfssTo && (pfsPlayer->GetSide() == pfssTo->GetSide() || bAllied)
           && (money covers)) {
    /* debit sender, credit recipient */
    if (bAllied)                                   // FedSrv.CPP:7242
        SendMessages(recipient's side group, ...);
    prcp = sender's side group;                    // FedSrv.CPP:7245
}
g.fm.SendMessages(prcp, ...);                      // FedSrv.CPP:7257
```

Crucially, **`IsideIGC::AlliedSides` returns TRUE for same-side too** (the
`side1 == side2` early return). Because the success branch requires
`sameSide || bAllied`, and same-side already implies `bAllied == true`, the
`if (bAllied)` send fires on **every** successful donation — including same-side
ones. So the literal native code always performs **two** `SendMessages` calls on
success; on a same-side donation the second is a *redundant duplicate* of the
same MONEY_CHANGE to the identical peer group.

The author's evident *intent* (and the task brief's wording) is that the extra
recipient-side send exists to inform a **distinct** audience only when the gift
crossed sides. Since on a same-side donation both calls target the exact same
group, the duplicate carries no extra information (a client shows one
money-change either way).

**Decision:** the port sends to the recipient's side group only when the gift is
cross-side *and* allied (`bAllied && toSide != donorSide`), and always sends to
the sender's side group. Net wire effect is identical to native (same audience,
same single logical money-change event) without emitting a pointless duplicate
frame. This is the one deliberate divergence from the literal C++, and it is the
brief's own stated model — adopted after verifying the live code. Documented
here and in the `HandlePlayerDonate` comment.

Verified consequences in tests:
- Same-side gift: one MONEY_CHANGE to the donor's side (both peers see it).
- Allied cross-side gift: one MONEY_CHANGE to each side's group (donor's side
  and recipient's side each see it) — proven by the must-fail test, which goes
  red when the recipient-side send is removed even though the wallets moved.

---

## Divergences / corrections vs the brief (all verified against live source)

1. **`AlliedSides` same-side quirk (above).** The brief's "one send on same-side"
   is the *intent*, not the literal code; port follows intent and documents why.

2. **Nonexistent-target native NULL-deref.** Native line `FedSrv.CPP:7231`
   (`IsideIGC::AlliedSides(pfsPlayer->GetSide(), pfssTo->GetSide())`) dereferences
   `pfssTo` **before** the `pfssTo &&` guard at `:7232` — a nonexistent target
   (which the `else` branch explicitly expects, "doesn't exist in mission")
   would crash native. The port resolves `pfssTo` and guards its null before
   computing `AlliedSides`, so a bad id cleanly takes the bounce-back path.

3. **NPC recipients.** Native credits `pfssTo` regardless of player status
   (`CFSShip::SetMoney` works on any ship). This port's wallets live only on live
   player ships (`LiveOf` returns null for NPCs), so an NPC target has no wallet
   to credit. The port still performs the transfer *shape* faithfully: the
   sender is debited and the broadcasts fire (native's "transfer happened"
   semantics for a same-side/allied target), but the NPC's (meaningless) credit
   is skipped. Out of scope of the tested scenarios; noted for completeness.

4. **Hard gates softened.** Native `TrapHackBoot(moneyGiven > 0)` and
   `TrapHackBoot(sender money >= moneyGiven)` (`FedSrv.CPP:7220-7221`) disconnect
   a cheating client. Per this port's standing convention they are softened to
   **log-and-ignore** (no forward at all — not even a bounce-back echo), matching
   the brief's "no forward at all — the initial hard gate".

5. **Second redundant money check ported faithfully.** The success `if` re-tests
   `sender money >= moneyGiven` (`FedSrv.CPP:7234`) even though the hard gate
   already enforced it — a defensive re-test against a race. Both checks are
   ported, as the brief requires.

---

## Scope boundary

- **No UI/chat announcement** of the gift (no chat-sound system for money events
  in this port; matches P197/P198/P199 precedent).
- **`AUTODONATE` untouched.** This is a one-shot manual gift, deliberately
  independent of P197's standing donate relationship; no overlap with
  `HandleAutoDonate`/`SetAutoDonate`.

---

## Tests (7)

- `PlayerDonate_client_codec_round_trips_exact_bytes` — fmid 67, 12 B, moneyGiven
  @4 / shipID @8 / pad @10, round-trips.
- `Same_side_donation_transfers_and_broadcasts_to_side` — donor debited,
  recipient credited, MONEY_CHANGE to the donor's side (both peers).
- `Allied_cross_side_donation_transfers_and_broadcasts_both_sides` — P190
  alliance harness (4-team mission, ally 0+1 while NotStarted, then Started);
  transfer + MONEY_CHANGE to both sides' groups.
- `Non_allied_different_side_donation_bounces_back_no_op` — no wallet movement,
  echo (sidFrom=NA) to the sender only; intended recipient hears nothing.
- `Over_wallet_donation_refused_no_forward` — over-wallet refused with no
  forward/echo/broadcast.
- `Nonexistent_target_bounces_back_to_sender_alone` — no movement, echo to the
  sender.
- `Multi_mission_donation_broadcast_scopes_per_mission` — a donation in mission A
  never reaches a mission-B peer sharing the same numeric side id.

Must-fail-if-removed was demonstrated by temporarily removing each behavior in
turn and watching the corresponding test go red (transfer → same-side test red;
recipient-side broadcast → allied test red; bounce-back echo → both bounce tests
red), then restoring.

Baseline was 3019 (post-P202); this stage adds 7 → **3026**.
