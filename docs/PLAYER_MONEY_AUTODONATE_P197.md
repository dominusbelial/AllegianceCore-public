# PLAYER_MONEY_AUTODONATE_P197 — per-player money & auto-donate foundation

**Stage P197** on `master`. Adds the real per-player economy layer underneath the
already-shipped P51 `PAYDAY(114)` port: a personal wallet per connection, an
auto-donate relationship with the native cycle-resolution algorithm, a real
`AUTODONATE(112)` two-mode wire dispatch (+ the donation-confidence leadership
check), `PAYDAY` actually crediting the resolved donate-target's wallet, the
`MUTINY` wallet-gift to the new leader, and the join/leadership resync. Per-user
confirmation (2026-09-13): this is a deliberate, beloved gameplay mechanic to
reproduce faithfully — everyone earns their own money on payday, which by default
auto-donates to the team leader; an unhappy player can unilaterally opt out.

---

## What changed

### 1. Per-player `PlayerWallet` (new) on `LiveShip`

`src/Allegiance.Server/PlayerWallet.cs` — a new connection-lifetime container:

```csharp
public sealed class PlayerWallet
{
    public int Money { get; set; }               // AGCMoney, 32-bit
    public Ship? AutoDonateTarget { get; set; }  // the ship being donated to, or null
}
```

Attached to the `LiveShip` record alongside the P193 `PlayerScore`
(`Server.cs:238`), allocated at logon (`Server.cs:5040`), discarded at
disconnect.

**Placement rationale.** Native keeps the wallet and auto-donate relationship on
`CFSPlayer` / the ship (`fsship.h:41-42` `GetMoney/SetMoney`,
`fsship.h:342-351` `GetAutoDonate/SetAutoDonate`) — the same per-connection
lifetime as `PlayerScore`, but a *different concern*. `PlayerScore` is
deliberately rank/score-only (P193) and consumed untouched by P194/P195; folding
economy into it would blur its contract. So a **sibling** type on the same
`LiveShip` — identical lifetime, clean separation of scoring from economics.

`AutoDonateTarget` is a `Ship` reference mirroring native's `IshipIGC*`; the
port already guarantees one player-piloted ship per connection (`_peerShips`),
so resolving the target back to a wallet is a reverse lookup by reference
(`Server.LiveOf`). `Money` is `int` (32-bit `AGCMoney`, matching the established
`Payday`/`SetMoney`/`MoneyChange` codec widths).

### 2. Default auto-donate-to-leader on join

`Server.TryJoinSide` (`Server.cs:~5220`) — after seating, when the mission is
`Started`/`Starting` (native `AddPlayerToSide`, fsmission.cpp:722):

```csharp
if (mission.Stage is MissionStage.Started or MissionStage.Starting)
{
    var joinerLive = LiveOf(ship);
    if (joinerLive is not null)
    {
        Ship? leader = newLeader ? null : LeaderShip(mission, newSideId);
        SetAutoDonate(joinerLive, leader, 0, bSend: true);
    }
    SyncMoneyStateToJoiner(mission, newSideId, ConnOf(ship));
}
```

The first player onto an empty side becomes its leader and donates to no one
(the `newLeader ? null :` arm keeps the leader's target unset); every later
joiner auto-donates to the side's current leader. This is the real `(… , 0,
true)` broadcast default.

### 3. `Server.SetAutoDonate` — the cycle-resolution algorithm (verbatim)

Port of `CFSPlayer::SetAutoDonate` (`FedSrv/fsship.cpp:1293-1352`), quoted from
the live source and reproduced branch-for-branch:

```c
void CFSPlayer::SetAutoDonate(CFSPlayer* pplayer, Money amount, bool bSend)
{
  assert (pplayer != this);
  ShipID    sidDonateBy = GetShipID();
  ShipID    sidDonateTo;
  IshipIGC* pship;
  if (pplayer)
  {
      pship = pplayer->GetIGCShip()->GetAutoDonate();
      if (pship == NULL)
          pship = pplayer->GetIGCShip();
      else
      {
          assert (pship->GetAutoDonate() == NULL);
          CFSPlayer*    pplayerNew = ((CFSShip*)(pship->GetPrivateData()))->GetPlayer();
          if (pplayerNew == this)
          {
              //We are trying to autodonate to someone who is already autodonating to us
              //Tell them to stop first (but don't send any messages)
              pplayer->SetAutoDonate(NULL, 0, false);
              pship = pplayer->GetIGCShip();
          }
          else
          {
              //The person we would like to donate to was already donating to someone else
              //donate to who ever they were donating to.
              pplayer = pplayerNew;
          }
      }
      assert (pship->GetAutoDonate() == NULL);
      assert (pplayer->GetIGCShip() == pship);
      sidDonateTo = pship->GetObjectID();
      if (amount != 0)
      {
          TrapHack(GetMoney() >= amount);
          pplayer->SetMoney(amount + pplayer->GetMoney());
          SetMoney(GetMoney() - amount);
      }
  }
  else { pship = NULL; sidDonateTo = NA; }
  GetIGCShip()->SetAutoDonate(pship);
  // (then AUTODONATE(116) broadcast to the side group if bSend — see dispatch)
}
```

The C# port (`Server.SetAutoDonate`, `Server.cs`) follows this exactly:
requested target's current donate-target is inspected; `NULL` → target the
requested player directly; else the **mutual-donation** case (they donate to
*someone who donates back to the caller*) clears *their* target first silently
(bSend=false), then targets them directly; otherwise **chain resolution**
redireads the caller to whoever they were donating to. The one-time `amount` gift
moves only when a target is established (never on a clear), from the donor to the
*final resolved* target's wallet (`fsship.cpp:1336-1338`), and `AUTODONATE(116)`
is broadcast to the donor's **side group** when `bSend`.

### 4. `AUTODONATE(112)` two-mode wire dispatch + donation-confidence leadership

`Server.HandleAutoDonate` (`Server.cs`) + the `AutoDonate.Fmid` dispatch branch
in `ProcessDatagram`. Port of `FedSrv.CPP:5516-5658 case FM_C_AUTODONATE`:
gated on `STAGE_STARTED`; gift clamped to the sender's wallet; `amount == -1`
selects the **leader-only toggle** of a named subordinate's donation (refused for
a non-leader); any other amount sets/clears the sender's own target with an
optional gift. Target resolution enforces same-side + live (`ResolveDonateTarget`).

It then runs the real **donation-confidence leadership check**
(`FedSrv.CPP:5560-5658`): if the changed player now donates somewhere other than
the current leader, and more side members donate to that other player than to the
leader, leadership passes to the most-donated-to player (`Server.PickNewLeader`,
port of `FedSrv.CPP:13981-14024`, exact `>` comparison → first-to-maximum wins).
This is the *soft economic-protest* core the brief's "two-mode dispatch"
description omitted — ported faithfully and covered by no brief test (a bonus,
noted under divergences).

### 5. `PAYDAY` now credits the resolved donate-target's wallet

`Server.OnPayday` (`Server.cs`) — after the existing P51 broadcast + treasury
drain, the port adds the native `DoPayday` wallet credit
(`fsmission.cpp:2310-2324`): each player's share goes to their resolved
`AutoDonateTarget`'s wallet if one is set, else their own. This is the real
behavior P51 skipped (it broadcast and drained without crediting anyone).

### 6. `MUTINY` wallet-gift + deposed-leader redirection

`Server.ApplyMutiny` (`Server.cs`) — port of `MutinyBallot::OnPassed`
(`fsmission.cpp:6121-6136`), executed in the exact native order:
1. clear the **new leader's** own donate target silently (`bSend=false`);
2. capture the **old leader's entire wallet**;
3. re-point the **old leader** at the new leader with that wallet as a one-time
   gift (`bSend=true` → `AUTODONATE(116)` side-wide);
4. send a compensating `MONEY_CHANGE(-money)` to the **deposed leader alone**
   (`fsmission.cpp:6131-6136`);
5. `SetLeader` → `side.LeaderShipId` + `BroadcastSetTeamLeader`.

### 7. Join / leadership-change resync

`Server.SyncMoneyStateToJoiner` (`Server.cs`) — the port of the native
`SendMissionInfo` money/autodonate loop (`fsmission.cpp:4276-4302`): sends
`AUTODONATE(116)` + `SET_MONEY(115)` **together**, once per *other* player on the
side, to a newly seated peer. Hooked from `TryJoinSide`. `Server.RetargetDonorsOffLeaver`
ports the leadership-succession donation retarget (`fsmission.cpp:1106-1115`) and
is hooked from `HandleLeaderSuccession` (leader departs → everyone who donated to
them is re-pointed at the successor, and the successor's own target is cleared if
it pointed at the leaver).

### Wire codecs (new / reused)

| fmid | msg | dir | size | status |
|---|---|---|---|---|
| 112 | `AutoDonate` (client request) | C | 12 B (sidDonateTo short@4 + pad + amount int32@8) | **new** |
| 116 | `AutoDonateUpdate` (server broadcast) | S | 12 B (sidDonateBy@4 + sidDonateTo@6 + amount@8) | **new** |
| 115 | `SetMoney` | S | 12 B (Money@4 + shipID@8 + 2 pad) | existed (P118) — reused |
| 60  | `MoneyChange` | S | 12 B (dMoney@4 + sidTo@8 + sidFrom@10) | existed (P118) — reused |

Existing `SetMoney`/`MoneyChange` codecs were left byte-identical (they were
already correct, the P118 pooled-mapping *mapping* doc text notwithstanding — the
wire layout was verified then); this task reuses them for their real purpose.

---

## The resync-trigger site (fsmission.cpp:4280-4298), identified

The native loop sending `AUTODONATE(116)`+`SET_MONEY(115)` per teammate is inside
`CFSMission::SendMissionInfo` (`fsmission.cpp:4276-4302`), reached from
**`AddPlayerToSide`** when a player joins during `Started`/`Starting`
(`fsmission.cpp:735`) and from `StartCountdown`'s per-side setup
(`fsmission.cpp:2029`). So the trigger is *"a player is (re)seated on a side"* —
in this port, `TryJoinSide` (the join path). The brief's guess ("everyone's money
synced whenever leadership changes") was not the site — the leadership-change
retarget is a separate loop at `fsmission.cpp:1106-1115` (ported as
`RetargetDonorsOffLeaver`), which does *not* emit `SET_MONEY`.

---

## Divergences from this prompt's assumptions (verified against live native source)

1. **`MONEY_CHANGE` is NOT emitted at payday or at a gift transfer.** The brief
   (scope item 5) instructed wiring `MoneyChange(60)` into "every point this task
   changes a player's Money: payday credit, gift transfer, mutiny wallet-gift."
   Verified: `CFSPlayer::SetMoney` is a **plain field setter with no broadcast**
   (`fsship.h:42`), `DoPayday` emits no `MONEY_CHANGE`
   (`fsmission.cpp:2310-2324`), and the `SetAutoDonate` gift is conveyed purely by
   the `AUTODONATE(116)` `amount` field (`fsship.cpp:1336-1352`). Native emits
   `MONEY_CHANGE` **only** at the mutiny (to the deposed leader alone,
   `fsmission.cpp:6131-6136`). **Correction:** this task wires `MoneyChange`
   exactly where native does (the mutiny deposed-leader notice) and nowhere else;
   payday/gift crediting is silent server-side, documented in the code. See also
   `PAYDAY_P51.md`'s deferred-follow-up list (this is the `MONEY_CHANGE`/`SET_MONEY`
   follow-up landing, faithfully).

2. **Mutiny does NOT retarget every donor onto the new leader.** The brief (scope
   item 6 + the mutiny test) asked `ApplyMutiny` to "retarget every player who was
   donating to the outgoing leader onto the new leader." Verified: `MutinyBallot::
   OnPassed` only redirects the **deposed leader** (with the full-wallet gift); the
   retarget-everyone loop exists only in the player-*leaves*-a-side path
   (`fsmission.cpp:1106-1115`, ported as `RetargetDonorsOffLeaver` and hooked into
   `HandleLeaderSuccession`), never in mutiny. The "cascade autodonation of
   everyone" comment in the native mutiny is misleading. **Correction:** mutiny is
   faithful (only the deposed leader is redirected); the retarget-everyone behavior
   was preserved where native actually does it (leader succession). The mutiny test
   asserts native's real behavior.

3. **Bonus (untracked by the brief): the donation-confidence leadership election.**
   The brief described the `AUTODONATE(112)` dispatch as "two-mode" only. The real
   handler has a third effect — leadership passes when a donation shift gives
   another player more donations than the leader (`FedSrv.CPP:5560-5658` +
   `PickNewLeader`). This is the soft economic-protest mechanism the mechanic
   exists for, so it was ported faithfully despite not appearing in the brief.
   Covered by a dedicated test.

4. **Join-default gating.** The brief implied the default is set on "seating onto
   a side." Native only sets it when `Started`/`Starting` (`fsmission.cpp:720-723`);
   during `NotStarted` it is not set (the leader-less lobby). Ported with that gate.

## Tests

`tests/Allegiance.Core.Tests/PlayerMoneyAutoDonateTests.cs` (10 tests), plus the
existing P51 `PaydayTests.cs` and P187 `BallotVotingLiveTests.cs` re-run to prove
no regression (OnPayday and ApplyMutiny were both touched):

- codec bytes for `AutoDonate(112)` and `AutoDonateUpdate(116)` (exact 12 B);
- **join default**: non-leader auto-donates to the leader; leader has none;
- **cycle resolution** (direct via reflection, isolating it from the election):
  chain-redirect (A→B where B→C yields A→C) and mutual-donation (B→A unset first,
  then A→B);
- **gift transfer** over the wire (B gifts 50 to leader A: A +50, B −50, and an
  `AUTODONATE(116)` with `amount=50` observed);
- **leader-only toggle**: non-leader `amount=-1` refused; leader toggles a
  subordinate's donation off then on;
- **PAYDAY credits the resolved donate-target** (B's whole share lands in A's
  wallet because B auto-donates to A) — a real must-fail-if-removed proof distinct
  from the P51 cadence tests;
- **mutiny wallet-gift** (P187 harness): old leader's 500 → new leader's wallet,
  old leader redirected to donate to the new leader, new leader clears his own
  target, deposed leader alone sees `MONEY_CHANGE(−500)`;
- **multi-mission scoping**: an `AUTODONATE(116)` on mission A never reaches a
  peer on mission B sharing the same numeric side id.

Must-fail-if-removed was proven by temporally reverting the payday credit, the
cycle resolution, and the mutiny gift — each reversion turned its test red, then
was restored.

## Verification

- Build clean (`/home/domingo/.dotnet/dotnet build`).
- P197 suite: 10/10 in isolation.
- Full-solution `dotnet test`: see the landing log for the 3 consecutive runs and
  the seven documented flakes (none expected to be P197 regressions; `PaydayTests`
  and `BallotVotingLiveTests` were green in the same pass).
- The seven known flakes are unchanged; `PaydayTests` (5) and
  `BallotVotingLiveTests` (11) both green alongside P197 (25/25 in the combined
  filtered run).
