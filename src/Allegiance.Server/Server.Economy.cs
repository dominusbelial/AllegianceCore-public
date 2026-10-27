using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// Money, donation, bucket-research, development-tech and payday members of
/// <see cref="Server"/> (P329 stage-3 split from <c>Server.cs</c>). Partial
/// class — shares the <c>Server</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Server
{
    // The donation transaction sends status after the wallet debit, matching
    // FedSrv/FedSrv.CPP:7154-7196. Other authoritative money snapshots have
    // no such transaction and must still be sent by OnBucketChanged.
    private Bucket? _donatingBucket;

/// <summary>Port of <c>CFSPlayer::SetAutoDonate</c>
    /// (<c>FedSrv/fsship.cpp:1293-1352</c>) — the real cycle-resolution
    /// algorithm. Sets <paramref name="donor"/>'s
    /// <see cref="PlayerWallet.AutoDonateTarget"/> to <paramref name="target"/>
    /// (or clears it when <paramref name="target"/> is null), applying native's
    /// chain-redirection and mutual-donation-unset rules, optionally transferring
    /// a one-time <paramref name="amount"/> gift, and (when <paramref name="bSend"/>)
    /// broadcasting <c>AUTODONATE(116)</c> to the donor's side group. Every
    /// branch is read from the live native function; <c>bSend=false</c> is the
    /// silent variant native uses for intermediate clears and leadership plumbing
    /// (no immediate broadcast; native's explicit side-group flush after a
    /// <c>bSend=false</c> batch is reproduced by the caller passing <c>bSend:true</c>
    /// for each member of that batch, giving the identical wire effect).</summary>
    private bool SetAutoDonate(LiveShip donor, Ship? target, int amount, bool bSend)
    {
        // Native's pointer check (FedSrv.CPP:5523) never checked the amount.
        // Validate again here: leadership/internal callers share this primitive.
        if (amount < 0 || amount > 0 && (donor.Wallet.Money < amount || donor.Wallet.Money < 0)) return false;
        LiveShip? clearTarget = null;
        Ship? resolved = null;
        if (target is not null)
        {
            var targetLive = LiveOf(target);
            var targetDonatesTo = targetLive?.Wallet.AutoDonateTarget;   // who THEY currently donate to
            if (targetDonatesTo is null)
            {
                // they donate to nobody → target them directly
                resolved = target;
            }
            else
            {
                // they were already donating to someone else — resolve the chain
                var targetNew = LiveOf(targetDonatesTo);
                if (targetNew is not null && ReferenceEquals(targetNew.Ship, donor.Ship))
                {
                    // They're already donating to US — stop that first (silent,
                    // no messages), then donate to them anyway
                    // (fsship.cpp:1313-1321).
                    clearTarget = targetLive;
                    resolved = target;
                }
                else
                {
                    // donate to whoever THEY were donating to (chain resolution,
                    // fsship.cpp:1322-1326).
                    resolved = targetDonatesTo;
                }
            }

            // An optional one-time up-front gift when establishing a NEW target
            // (amount == 0 means no gift; separate from the AUTODONATE(112)
            // amount==-1 toggle sentinel handled in the dispatch).
            if (amount != 0)
            {
                var receiver = LiveOf(resolved!);
                if (receiver is not null)
                {
                    if (ReferenceEquals(receiver.Ship, donor.Ship) || receiver.Wallet.Money < 0
                        || (long)receiver.Wallet.Money + amount > int.MaxValue) return false;
                    receiver.Wallet.Money = checked(receiver.Wallet.Money + amount);
                    donor.Wallet.Money = checked(donor.Wallet.Money - amount);    // fsship.cpp:1336-1338
                }
            }
        }
        // else: clearing the target entirely — no gift (native's if(amount!=0)
        // sits inside the if(pplayer) block, so a clear never transfers money).

        if (clearTarget is not null) clearTarget.Wallet.AutoDonateTarget = null;
        donor.Wallet.AutoDonateTarget = resolved;

        if (bSend)
            BroadcastAutoDonate(donor.Ship, donor.Ship.Id, resolved?.Id ?? Na, amount);
        return true;
    }

/// <summary>Broadcast <c>AUTODONATE(116)</c> to the donor's <em>side</em>
    /// group (native <c>CFSSide::FromIGC(GetSide())->GetGroup()</c>,
    /// fsship.cpp:1350-1352) — not the whole mission.</summary>
    private void BroadcastAutoDonate(Ship donor, short sidDonateBy, short sidDonateTo, int amount)
    {
        if (donor.Mission is not Mission mission || donor.SideId is not { } sideId) return;
        var msg = new AutoDonateUpdate { SidDonateBy = sidDonateBy, SidDonateTo = sidDonateTo, Amount = amount };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[autodonate] ship {sidDonateBy} side {sideId} -> AUTODONATE(116) target={sidDonateTo} amount={amount} to {targets.Length} peer(s)");
    }

/// <summary>Resolve a wire <c>sidDonateTo</c> (as sent in
    /// <c>AUTODONATE(112)</c>) to a live <em>same-side</em> player ship, or
    /// <c>null</c> when it is <c>NA</c>, the actor's own ship, or not a live
    /// same-side player (native FedSrv.CPP:5535-5544: <c>GetShipFromID</c> +
    /// <c>IsPlayer()</c> + <c>pside == GetSide()</c>).</summary>
    private Ship? ResolveDonateTarget(LiveShip actor, short sidDonateTo)
    {
        if (sidDonateTo == Na || sidDonateTo == actor.Ship.Id) return null;
        foreach (var l in _peerShips.Values)
            if (l.Ship.Id == sidDonateTo
                && ReferenceEquals(l.Ship.Mission, actor.Ship.Mission)
                && l.Ship.SideId == actor.Ship.SideId)
                return l.Ship;
        return null;
    }

/// <summary>
    /// P197 — the <c>FM_C_AUTODONATE(112)</c> dispatch, port of
    /// <c>FedSrv.CPP:5516-5658</c> <c>case FM_C_AUTODONATE</c>: a two-mode
    /// request (set-own-target-with-optional-gift, or the leader-only
    /// <c>amount==-1</c> toggle of a subordinate's donation) followed by the
    /// donation-confidence leadership check. Runs on the sim thread, so no extra
    /// locking is needed.
    /// </summary>
    private void HandleAutoDonate(ConnectionId from, LiveShip live, AutoDonate req)
    {
        var ship = live.Ship;
        if (ship.Mission is not Mission mission) return;
        if (mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[autodonate] {from} ship {ship.Id}: stage {mission.Stage} != STAGE_STARTED — AUTODONATE(112) refused (FedSrv.CPP:5517-5518)");
            return;
        }
        if (ship.SideId is not { } sideId) return;

        if (req.Amount < -1 || req.Amount >= 0 && live.Wallet.Money < 0) return;
        // Preserve native's upper clamp, without converting a gift to a negative
        // amount or clamping away the leader-only sentinel (FedSrv.CPP:5525-5532).
        if (req.Amount >= 0) req.Amount = Math.Min(req.Amount, live.Wallet.Money);

        // amount == -1 is the LEADER-ONLY "toggle someone else's donation" mode.
        if (req.Amount == -1 && !IsSideLeader(mission, ship))
        {
            ConsoleLog.Write($"[autodonate] {from} ship {ship.Id}: AUTODONATE amount=-1 toggle from a non-leader — refused (FedSrv.CPP:5531-5532)");
            return;
        }

        Ship? pfsDonate = ResolveDonateTarget(live, req.SidDonateTo);

        LiveShip? changed;
        if (req.Amount == -1)
        {
            if (pfsDonate is null)
            {
                ConsoleLog.Write($"[autodonate] {from} ship {ship.Id}: toggle target {req.SidDonateTo} not a live same-side player — ignored (FedSrv.CPP:5548)");
                return;
            }
            var donateLive = LiveOf(pfsDonate)!;
            // toggle: if the subordinate currently donates to someone, stop them;
            // otherwise set them to donate to the leader (the sender).
            if (donateLive.Wallet.AutoDonateTarget is not null)
                SetAutoDonate(donateLive, null, 0, bSend: true);
            else
                SetAutoDonate(donateLive, ship, 0, bSend: true);
            changed = donateLive;
        }
        else
        {
            if (!SetAutoDonate(live, pfsDonate, req.Amount, bSend: true)) return;
            changed = live;
        }

        // Donation-confidence leadership check (native FedSrv.CPP:5560-5658):
        // if the changed player now donates somewhere other than the current
        // leader, and more side members donate to that other player than to the
        // leader, leadership passes to the player with the most donations.
        if (changed is null || changed.Ship.Mission is not Mission cm) return;
        if (changed.Ship.SideId is not { } csid) return;
        var cside = cm.Sides.FirstOrDefault(s => s.Id == csid);
        if (cside is null || cside.LeaderShipId is null) return;
        var leader = LeaderShip(cm, csid);
        if (leader is null || ReferenceEquals(changed.Wallet.AutoDonateTarget, leader)) return;

        int cLeader = 0, cOther = 0;
        foreach (var l in _peerShips.Values)
        {
            if (!ReferenceEquals(l.Ship.Mission, cm) || l.Ship.SideId != csid) continue;
            var t = l.Wallet.AutoDonateTarget;
            if (t is null) continue;
            if (ReferenceEquals(t, leader)) cLeader++;
            else cOther++;
        }
        if (cLeader < cOther)
        {
            var sideShips = _peerShips.Values
                .Where(l => ReferenceEquals(l.Ship.Mission, cm) && l.Ship.SideId == csid)
                .Select(l => l.Ship)
                .ToList();
            var newLeader = PickNewLeader(sideShips, leader, cLeader);
            if (newLeader is not null && cside.LeaderShipId != newLeader.Id)
            {
                cside.LeaderShipId = newLeader.Id;
                BroadcastSetTeamLeader(cm, csid, newLeader.Id);
                Console.WriteLine($"[autodonate] donation-confidence: side {csid} leadership passed to ship {newLeader.Id}");
            }
        }
    }

/// <summary>
    /// P199 — the <c>FM_CS_REQUEST_MONEY(182)</c> dispatch, port of
    /// <c>FedSrv.CPP:5361-5406</c> <c>case FM_CS_REQUEST_MONEY</c>: a player asks
    /// whoever actually holds the team's money to send them some. Crucially this
    /// handler <b>never transfers money</b> — it only <em>forwards</em> the request
    /// (with <c>shipidRequest</c> filled in) to exactly one recipient connection:
    /// the sender's own auto-donate target when that player's wallet holds at
    /// least <paramref name="req"/>.Amount, otherwise the richest <em>other</em>
    /// teammate on the sender's side (native's <c>&gt;</c> comparison from
    /// <c>moneyMax = amount - 1</c> ⇒ the first ship to reach each new maximum
    /// wins — ties resolve to whichever is encountered first). If no recipient
    /// resolves (empty/solo side, nobody with funds) the request is silently
    /// dropped — no reply, no error. The resolved recipient is expected to answer
    /// with its own follow-up (e.g. an <c>AUTODONATE(112)</c> gift). Runs on the
    /// sim thread, so no extra locking is needed.
    /// </summary>
    private void HandleRequestMoney(ConnectionId from, LiveShip live, RequestMoney req)
    {
        var ship = live.Ship;
        if (ship.Mission is not Mission mission) return;
        if (mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[requestmoney] {from} ship {ship.Id}: stage {mission.Stage} != STAGE_STARTED — REQUEST_MONEY refused (FedSrv.CPP:5363-5364)");
            return;
        }
        if (ship.SideId is not { } sideId) return;

        // Native uses TrapHackBoot(req.Amount > 0) here (FedSrv.CPP:5368), which
        // would DISCONNECT a client lying about a non-positive amount; per this
        // port's standing convention that harsh trap is softened to log-and-ignore.
        if (req.Amount <= 0)
        {
            ConsoleLog.Write($"[requestmoney] {from} ship {ship.Id}: amount {req.Amount} <= 0 — REQUEST_MONEY ignored (no forward)");
            return;
        }

        // Native gates REQUEST_MONEY behind CFSPlayer::DecrementChatBudget(true)
        // (FedSrv.CPP:5370-5373): a paid charge; an exhausted budget drops the request.
        if (!TryChargeChatBudget(from))
        {
            ConsoleLog.Write($"[requestmoney] {from} ship {ship.Id}: chat budget exhausted — REQUEST_MONEY dropped (FedSrv.CPP:5370)");
            return;
        }

        Ship? pshipAD = live.Wallet.AutoDonateTarget;
        var adLive = pshipAD is null ? null : LiveOf(pshipAD);
        if (adLive is null || adLive.Wallet.Money < req.Amount)
        {
            // Not auto-donating to anyone holding enough — find the richest
            // teammate instead, EXCLUDING the sender (FedSrv.CPP:5378-5395).
            int moneyMax = req.Amount - 1;
            Ship? richest = null;
            foreach (var l in _peerShips.Values)
            {
                if (!ReferenceEquals(l.Ship.Mission, mission) || l.Ship.SideId != sideId) continue;
                if (ReferenceEquals(l.Ship, ship)) continue;   // exclude the sender
                if (l.Wallet.Money > moneyMax)
                {
                    moneyMax = l.Wallet.Money;
                    richest = l.Ship;
                }
            }
            // Native keeps the (too-poor) auto-donate target when nobody else beats
            // amount-1 (pshipAD is only overwritten inside the scan, FedSrv.CPP:5377-5393).
            if (richest is not null) pshipAD = richest;
        }

        if (pshipAD is null)
        {
            // Side empty / nobody has any money / solo — silently dropped
            // (FedSrv.CPP:5398 has no else: no reply, no error).
            Console.WriteLine($"[requestmoney] {from} ship {ship.Id}: no recipient resolves — REQUEST_MONEY silently dropped");
            return;
        }

        // Fill in the requester's ship id and forward to the single recipient.
        var forward = new RequestMoney { Amount = req.Amount, ShipIdRequest = ship.Id, HidFor = req.HidFor };
        if (ConnOf(pshipAD) is { } conn)
        {
            SendPeer(conn, FedPacket.Pack(new[] { forward.Encode() }));
            Console.WriteLine($"[requestmoney] {from} ship {ship.Id}: REQUEST_MONEY({req.Amount}, hidFor {req.HidFor}) forwarded to ship {pshipAD.Id}");
        }
    }

/// <summary>
    /// P203 — the <c>FM_C_PLAYER_DONATE(67)</c> dispatch, port of
    /// <c>FedSrv.CPP:7213-7262</c> <c>case FM_C_PLAYER_DONATE</c>: a manual,
    /// direct player-to-player gift, deliberately independent of
    /// <c>AUTODONATE(112)</c>'s standing relationship. Runs on the sim thread,
    /// so no extra locking is needed.
    /// </summary>
    private void HandlePlayerDonate(ConnectionId from, LiveShip live, PlayerDonate req)
    {
        var ship = live.Ship;
        if (ship.Mission is not Mission mission) return;
        if (mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[playerdonate] {from} ship {ship.Id}: stage {mission.Stage} != STAGE_STARTED — PLAYER_DONATE refused (FedSrv.CPP:7215-7216)");
            return;
        }
        if (ship.SideId is not { } donorSideId)
        {
            ConsoleLog.Write($"[playerdonate] {from} ship {ship.Id}: no side yet — PLAYER_DONATE ignored");
            return;
        }

        // Softened TrapHackBoot(moneyGiven > 0) (FedSrv.CPP:7220) — log-and-ignore,
        // no forward at all (no bounce-back).
        if (req.MoneyGiven <= 0)
        {
            ConsoleLog.Write($"[playerdonate] {from} ship {ship.Id}: moneyGiven {req.MoneyGiven} <= 0 — PLAYER_DONATE ignored (no forward)");
            return;
        }
        // Softened TrapHackBoot(sender money >= moneyGiven) (FedSrv.CPP:7221) — the
        // initial hard gate; log-and-ignore, no forward.
        if (live.Wallet.Money < req.MoneyGiven)
        {
            ConsoleLog.Write($"[playerdonate] {from} ship {ship.Id}: wallet {live.Wallet.Money} < given {req.MoneyGiven} — PLAYER_DONATE ignored (no forward)");
            return;
        }

        // Resolve the recipient ship by its wire id (native CFSShip::GetShipFromID,
        // FedSrv.CPP:7223; this port's global id resolver FindShipAnySide).
        Ship? pfssTo = FindShipAnySide(req.ShipID, mission);

        var mc = new MoneyChange { DMoney = req.MoneyGiven };

        bool sameSide = pfssTo is not null && ship.SideId == pfssTo.SideId;
        bool bAllied = pfssTo is not null && AlliedSides(mission, donorSideId, pfssTo.SideId);
        // The SECOND (redundant-looking) money check — native's defensive re-test
        // against a race where the sender's wallet changed between the TrapHackBoot
        // gate and this point (FedSrv.CPP:7234). Port both faithfully.
        if (pfssTo is not null && (sameSide || bAllied) && live.Wallet.Money >= req.MoneyGiven)
        {
            // Validate both balances before any mutation, using a wider sum.
            if (LiveOf(pfssTo) is { } recipient && !ReferenceEquals(recipient.Ship, live.Ship)
                && (recipient.Wallet.Money < 0 || (long)recipient.Wallet.Money + req.MoneyGiven > int.MaxValue)) return;
            // Real transfer (FedSrv.CPP:7236-7240).
            mc.SidTo = req.ShipID;
            mc.SidFrom = ship.Id;
            live.Wallet.Money = checked(live.Wallet.Money - req.MoneyGiven);
            if (LiveOf(pfssTo) is { } toLive)
                toLive.Wallet.Money = checked(toLive.Wallet.Money + req.MoneyGiven);
            // else: an NPC target has no per-player wallet to credit (the port's
            // LiveOf returns null for non-players); the sender is still debited and
            // the broadcasts still fire, matching native's "transfer happened"
            // semantics for an ineligible-but-same-side/allied recipient.

            Console.WriteLine($"[playerdonate] {from} ship {ship.Id} -> ship {req.ShipID}: MONEY_CHANGE dMoney={req.MoneyGiven} sidTo={req.ShipID} sidFrom={ship.Id} (real transfer)");

            // Cross-side/allied: ALSO tell the recipient's side group. Native fires
            // this inside `if (bAllied)` (FedSrv.CPP:7242-7243). Because native
            // IsideIGC::AlliedSides returns TRUE for same-side too, the literal code
            // sends to the recipient's group even on a same-side gift — but there
            // that group is the SAME as the sender's group, i.e. a redundant
            // duplicate send of the identical message to the identical peer set. The
            // author's intent (and this brief) is a second, distinct audience only
            // when the gift CROSSED sides; this port sends the recipient's side
            // group only in that case, collapsing the same-side duplicate (identical
            // net wire effect). Documented in docs/PLAYER_DONATE_P203.md.
            if (bAllied && pfssTo.SideId != donorSideId)
                SendMoneyChangeToSideGroup(mission, pfssTo.SideId!.Value, mc);

            // Always tell the sender's own side group (native prcp, FedSrv.CPP:7245).
            SendMoneyChangeToSideGroup(mission, donorSideId, mc);
        }
        else
        {
            // No-op bounce-back: bad target, or the sender's money changed since the
            // hard gate (FedSrv.CPP:7247-7256). No wallets move; the echo informs the
            // sender alone. sidTo = the sender's own ship, sidFrom = NA.
            mc.SidTo = ship.Id;
            mc.SidFrom = Na;
            SendPeer(from, FedPacket.Pack(new[] { mc.Encode() }));
            Console.WriteLine($"[playerdonate] {from} ship {ship.Id}: target {req.ShipID} ineligible — no-op MONEY_CHANGE echo to sender alone (sidFrom=NA)");
        }
    }

/// <summary>Native <c>IsideIGC::AlliedSides</c> (<c>igc.h:4297-4304</c>),
    /// adapted to the port's <see cref="Side.AllyGroup"/> model (P190): two
    /// sides are allied iff they are the same side (ids equal), or both carry
    /// the same non-null alliance-group value. Mirrors native exactly — same
    /// side → true; either side absent → false; donor's group NA (null) →
    /// false; otherwise equal group values.</summary>
    private static bool AlliedSides(Mission mission, short? a, short? b)
    {
        if (a == b) return true;                      // side1==side2
        if (a is null || b is null) return false;     // NULL guards
        var sa = mission.Sides.FirstOrDefault(s => s.Id == a.Value);
        var sb = mission.Sides.FirstOrDefault(s => s.Id == b.Value);
        if (sa is null || sb is null) return false;
        if (sa.AllyGroup is not { } ga) return false; // side1->GetAllies()==NA
        return ga == sb.AllyGroup;
    }

/// <summary>Broadcast a <c>MONEY_CHANGE(60)</c> to every peer seated on the
    /// given mission/side — the port of native <c>g.fm.SendMessages(
    /// CFSSide::FromIGC(...)-&gt;GetGroup(), ...)</c>. Scoped by BOTH mission and
    /// side id (side ids repeat across world missions), the same mission+side
    /// filter every side-group broadcast in this arc uses.</summary>
    private void SendMoneyChangeToSideGroup(Mission mission, short sideId, MoneyChange mc)
    {
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;
        byte[] packet = FedPacket.Pack(new[] { mc.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] mission side {sideId} -> MONEY_CHANGE(60) dMoney={mc.DMoney} sidTo={mc.SidTo} sidFrom={mc.SidFrom} to {targets.Length} peer(s)");
    }

/// <summary>P197 — send <c>AUTODONATE(116)</c> + <c>SET_MONEY(115)</c> together,
    /// once per <em>other</em> player on a side, to a joining peer — the port of the
    /// native <c>SendMissionInfo</c> money/autodonate loop
    /// (<c>fsmission.cpp:4276-4302</c>), reached from the join path so a newly
    /// seated player learns every teammate's donate relationship and wallet.</summary>
    private void SyncMoneyStateToJoiner(Mission mission, short sideId, ConnectionId? joinerConn)
    {
        if (joinerConn is not { } conn) return;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;
        foreach (var kv in _peerShips)
        {
            var live = kv.Value;
            if (!ReferenceEquals(live.Ship.Mission, mission) || live.Ship.SideId != sideId) continue;
            if (joinerConn == kv.Key) continue;   // don't tell the joiner about themself
            var ad = new AutoDonateUpdate
            {
                SidDonateBy = live.Ship.Id,
                SidDonateTo = live.Wallet.AutoDonateTarget?.Id ?? Na,
                Amount = 0,
            };
            var sm = new SetMoney { Money = live.Wallet.Money, ShipId = live.Ship.Id };
            SendPeer(conn, FedPacket.Pack(new[] { ad.Encode(), sm.Encode() }));
        }
        Console.WriteLine($"[syncmoney] side {sideId} -> AUTODONATE(116)+SET_MONEY(115) per teammate to joining peer");
    }

/// <summary>P197 — the leader-change donation retarget (native
    /// fsmission.cpp:1106-1115, the <c>else</c> branch of the leadership
    /// succession block in the player-left-side path): first, if the new leader
    /// was itself auto-donating to the departing player, clear that relationship
    /// (avoiding a self-referential state); then every player who was
    /// auto-donating to the departing player is retargeted to the new leader.
    /// Runs with <c>bSend:true</c> for each member so every changed relationship
    /// is broadcast as <c>AUTODONATE(116)</c> to the side group (native gathers
    /// the same batch with <c>bSend=false</c> and flushes it once to the side
    /// group — identical net wire effect).</summary>
    private void RetargetDonorsOffLeaver(Mission mission, short sideId, Ship departing, Ship newLeader)
    {
        var newLeaderLive = LiveOf(newLeader);
        if (newLeaderLive is not null && ReferenceEquals(newLeaderLive.Wallet.AutoDonateTarget, departing))
            SetAutoDonate(newLeaderLive, null, 0, bSend: true);

        foreach (var kv in _peerShips)
        {
            var l = kv.Value;
            if (!ReferenceEquals(l.Ship.Mission, mission) || l.Ship.SideId != sideId) continue;
            if (ReferenceEquals(l.Ship, newLeader)) continue;   // native excludes the new leader's ship
            if (ReferenceEquals(l.Wallet.AutoDonateTarget, departing))
                SetAutoDonate(l, newLeader, 0, bSend: true);
        }
    }

/// <summary>Broadcast a <c>MONEY_CHANGE(60)</c> to every peer seated on
    /// side <paramref name="sideId"/> — the P118 side-wide money broadcast,
    /// port of the native <c>MONEY_CHANGE</c> sends that go to the whole side's
    /// peer group (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, e.g.
    /// <c>FedSrv.CPP:2435-2441</c>, <c>7202-7206</c>). <c>sidFrom = NA</c>
    /// marks the server-initiated pooled-treasury credit, matching the native
    /// convention (<c>Messages.h:444</c>) for this port's refunds.</summary>
    private void BroadcastMoneyChangeSideWide(Mission mission, short sideId, int dMoney, short sidTo)
    {
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new MoneyChange { DMoney = dMoney, SidTo = sidTo, SidFrom = -1 /* NA */ };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] side {sideId} -> MONEY_CHANGE(60) dMoney={dMoney} sidTo={sidTo} sidFrom=NA to {targets.Length} peer(s)");
    }

/// <summary>
    /// Handle a client <c>FM_C_BUCKET_DONATE(64)</c> — the P119 port of
    /// <c>FedSrv.CPP:7137-7211</c> <c>case FM_C_BUCKET_DONATE</c>, changed in
    /// P198 to spend from the donating player's own wallet. Gates on
    /// <c>STAGE_STARTED</c> (:7139), resolves the donating side and its bucket
    /// by id (:7145), and validates the <b>donating player's personal wallet</b>
    /// (<see cref="PlayerWallet.Money"/>, P197) covers the offered amount —
    /// the real native gate <c>pfsPlayer-&gt;GetMoney() &gt;= moneyGiven</c>
    /// (:7148). <see cref="Side.Money"/> is no longer consulted or debited by
    /// this handler at all: income still accrues there via payday, but it is no
    /// longer a bucket-spend source (P198 scope boundary — accrual untouched).
    /// The donation is capped by <c>Bucket.AddMoney</c> at what the bucket still
    /// needs (bucketigc.h:281-293); only the actually-spent figure is deducted
    /// from the player's wallet, and the excess is returned to the player as an
    /// explicit refund <c>MONEY_CHANGE(60)</c> (native :7151-7160, :7199-7208).
    /// On a successful spend a negative <c>MONEY_CHANGE(60)</c> is broadcast to
    /// the whole side (:7162-7168), and finally a <c>BUCKET_STATUS(65)</c> to
    /// the whole side (:7183-7196).
    /// </summary>
    private void HandleBucketDonate(ConnectionId from, LiveShip live, BucketDonate donate)
    {
        var mission = live.Ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[bucketdonate] {from}: ship {live.Ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate FedSrv.CPP:7139)");
            return;
        }
        if (live.Ship.SideId is not { } sideId)
        {
            ConsoleLog.Write($"[bucketdonate] {from}: ship {live.Ship.Id}: no side yet — ignored");
            return;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            ConsoleLog.Write($"[bucketdonate] {from}: side {sideId} not found in mission — ignored");
            return;
        }
        var bucket = side.Buckets.FirstOrDefault(b => b.Id == donate.IBucket);
        if (bucket is null)
        {
            ConsoleLog.Write($"[bucketdonate] {from}: side {sideId} has no bucket {donate.IBucket} — ignored");
            return;
        }
        if (donate.MoneyGiven < 0)
        {
            ConsoleLog.Write($"[bucketdonate] {from}: negative donation {donate.MoneyGiven} — ignored (native trap FedSrv.CPP:7149)");
            return;
        }
        // P198: the real native gate — the DONATING PLAYER's own wallet, not the
        // side treasury (FedSrv.CPP:7148, pfsPlayer->GetMoney() >= moneyGiven).
        if (live.Wallet.Money < donate.MoneyGiven)
        {
            ConsoleLog.Write($"[bucketdonate] {from}: player {live.Ship.Id} wallet {live.Wallet.Money} < donated {donate.MoneyGiven} — ignored (native per-player gate FedSrv.CPP:7148)");
            return;
        }

        long refund = donate.MoneyGiven;   // native mnyRefund (FedSrv.CPP:7151)
        if (side.CanBuy(bucket))
        {
            // P551: the whole spend is gated on the side being ABLE to buy the
            // bucket (native `if (pside->CanBuy(pbucket))`, FedSrv.CPP:7152) —
            // an unbuyable bucket accepts nothing. The bucket caps at what it
            // still needs (bucketigc.h:281-293) and only the actually-spent
            // figure leaves the player's wallet (FedSrv.CPP:7154-7160).
            var previousDonation = _donatingBucket;
            _donatingBucket = bucket;
            long spent;
            try { spent = bucket.AddMoney(donate.MoneyGiven); }
            finally { _donatingBucket = previousDonation; }
            refund -= spent;
            live.Wallet.Money -= (int)spent;   // spend from the PLAYER's wallet, not the side (FedSrv.CPP:7160)
            Console.WriteLine($"[bucketdonate] {from}: player {live.Ship.Id} donated {donate.MoneyGiven} into bucket {bucket.Id} -> spent {spent}, invested {bucket.InvestedMoney}/{bucket.Price}, wallet now {live.Wallet.Money}");

            // MONEY_CHANGE(60) to the whole side: negative delta, sidTo == sidFrom ==
            // the donating player's own ship (native FedSrv.CPP:7162-7168).
            BroadcastBucketDonateSpendSideWide(mission, sideId, -(int)spent, live.Ship.Id);

            // BUCKET_STATUS(65) — to the whole mission when the bucket's buyable
            // is the c_didTeamMoney development ("Everyone knows about anyone's
            // development of the game winning tech", FedSrv.CPP:7190-7196),
            // otherwise to the side group.
            bool teamMoney = bucket.ItemType == BucketItemType.BitDevelopment
                && bucket.Development is { } dev
                && dev.DevelopmentId == Mission.TeamMoneyDevelopmentId;
            if (teamMoney)
                BroadcastBucketStatusMissionWide(mission, sideId, bucket);
            else
                BroadcastBucketStatusSideWide(mission, sideId, bucket);
        }
        else
        {
            Console.WriteLine($"[bucketdonate] {from}: player {live.Ship.Id} tried to donate {donate.MoneyGiven} into bucket {bucket.Id} but the side cannot buy it — spend refused, full refund (native CanBuy gate FedSrv.CPP:7152-7153,7199)");
        }

        // Excess the bucket didn't need (or the whole amount, when the side
        // cannot buy the bucket): explicit refund MONEY_CHANGE(60) to the
        // donating player alone, positive delta, sidFrom = NA (native
        // :7199-7208 — mnyRefund stays = moneyGiven when CanBuy is false, so
        // the full donation is refunded).
        if (refund > 0)
        {
            var mc = new MoneyChange { DMoney = (int)refund, SidTo = live.Ship.Id, SidFrom = -1 /* NA */ };
            SendPeer(from, FedPacket.Pack(new[] { mc.Encode() }));
            Console.WriteLine($"[moneychange] -> {from}: MONEY_CHANGE(60) dMoney=+{refund} sidTo={live.Ship.Id} sidFrom=NA (bucket-donation refund)");
        }
    }

/// <summary>
    /// Broadcast the bucket-donation <em>spend</em> <c>MONEY_CHANGE(60)</c> to
    /// every peer seated on side <paramref name="sideId"/> — the P198 port of
    /// the native donation handler's spend notification (<c>FedSrv.CPP:7162-7168</c>,
    /// <c>g.fm.SendMessages(CFSSide::FromIGC(pside)-&gt;GetGroup(), ...)</c>).
    /// Unlike the server-initiated <see cref="BroadcastMoneyChangeSideWide"/>
    /// (whose <c>SidFrom = NA</c> marks a pooled-treasury credit), this is a
    /// <b>player-initiated</b> purchase, so <c>SidFrom == SidTo</c> — both the
    /// donating player's own ship — matching native's
    /// <c>pfmMoneyChange-&gt;sidFrom = pfmMoneyChange-&gt;sidTo</c>.
    /// </summary>
    private void BroadcastBucketDonateSpendSideWide(Mission mission, short sideId, int dMoney, short shipId)
    {
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new MoneyChange { DMoney = dMoney, SidTo = shipId, SidFrom = shipId };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] side {sideId} -> MONEY_CHANGE(60) dMoney={dMoney} sidTo={shipId} sidFrom={shipId} to {targets.Length} peer(s)");
    }

/// <summary>
    /// Broadcast a <c>BUCKET_STATUS(65)</c> for <paramref name="bucket"/> to
    /// every peer seated on side <paramref name="sideId"/> — the P119 side-wide
    /// bucket-status broadcast, port of the native donation handler's send to
    /// the whole side's peer group (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>,
    /// <c>FedSrv.CPP:7183-7196</c>). timeTotal = invested build ms,
    /// moneyTotal = invested money (bucketigc.h:264-272).
    /// </summary>
    private void BroadcastBucketStatusSideWide(Mission mission, short sideId, Bucket bucket)
    {
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new BucketStatus
        {
            TimeTotal = (uint)bucket.InvestedMilliseconds,
            SideId = sideId,
            MoneyTotal = (int)bucket.InvestedMoney,
            IBucket = bucket.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[bucketstatus] side {sideId} -> BUCKET_STATUS(65) iBucket={bucket.Id} money={bucket.InvestedMoney}/{bucket.Price} timeMs={bucket.InvestedMilliseconds} to {targets.Length} peer(s)");
    }

/// <summary>
    /// Broadcast a <c>BUCKET_STATUS(65)</c> for <paramref name="bucket"/> to
    /// <b>every peer in the whole mission</b> (all sides) — the P551 port of the
    /// native donation handler's <b>whole-mission</b> branch
    /// (<c>FedSrv.CPP:7190-7196</c>): when the bucket's buyable is the
    /// <c>c_didTeamMoney</c> development, the send group is
    /// <c>pfsMission->GetGroupMission()</c> (everyone), not the side group,
    /// because it is the game-winning economic development.</summary>
    private void BroadcastBucketStatusMissionWide(Mission mission, short sideId, Bucket bucket)
    {
        var targets = _peerShips.Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)).Select(kv => kv.Key).ToArray();   // whole mission, every side
        if (targets.Length == 0) return;
        var msg = new BucketStatus
        {
            TimeTotal = (uint)bucket.InvestedMilliseconds,
            SideId = sideId,
            MoneyTotal = (int)bucket.InvestedMoney,
            IBucket = bucket.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[bucketstatus] mission-wide -> BUCKET_STATUS(65) iBucket={bucket.Id} (team-money dev) money={bucket.InvestedMoney}/{bucket.Price} timeMs={bucket.InvestedMilliseconds} to {targets.Length} peer(s)");
    }

    private void OnBucketChanged(BucketChange change, Bucket bucket)
    {
        if (change == BucketChange.MoneyChange && ReferenceEquals(bucket, _donatingBucket)) return;
        // Native client simulates bucket time locally. This port's investment
        // pane consumes authoritative snapshots instead: carry the same integer
        // percentage transition over the existing BUCKET_STATUS message.
        // Money snapshots also carry obsolete research and station-successor
        // transfers (Igc/sideigc.h:629-656), which have no donation transaction.
        if (change is not (BucketChange.PercentCompleteChange or BucketChange.Emptied or BucketChange.MoneyChange)
            || bucket.Side is not { Mission: { } mission } side) return;
        if (bucket.Development is { DevelopmentId: Mission.TeamMoneyDevelopmentId })
            BroadcastBucketStatusMissionWide(mission, side.Id, bucket);
        else
            BroadcastBucketStatusSideWide(mission, side.Id, bucket);
    }

/// <summary>
    /// P119 completion-<em>detection</em> handler, wired to
    /// <see cref="ServerHost.BucketCompleted"/> (raised from
    /// <see cref="Bucket.Update"/>'s <c>Complete</c> on the sim thread the
    /// instant a construction bucket is fully funded AND fully time-invested —
    /// the native gate bucketigc.h:51). This is the task's explicit "detect but
    /// don't act yet" boundary: it <b>records</b> the completed bucket id so the
    /// event is observable (logging + a test-visible list) and builds
    /// <b>nothing</b> — no spawned drone, no factory/storage-pit, no further
    /// broadcast. Those completion-effects are the named follow-up task. Runs
    /// on the sim thread, so <c>_bucketCompletions</c> is guarded by
    /// <c>_statsLock</c> for test-thread readers.
    /// </summary>
    private void OnBucketCompleted(IBucketIgc bucketIfc, double now)
    {
        if (bucketIfc is not Bucket bucket)
            return;
        short id = bucket.Id;
        lock (_statsLock) _bucketCompletions.Add(id);

        // The owning side is resolved by scanning the world missions' bucket
        // lists (a bucket carries no side back-ref, and completion is a rare
        // event, so the scan is negligible).
        Side? owner = null;
        Mission? ownerMission = null;
        foreach (var m in _missions)
        {
            var s = m.Sides.FirstOrDefault(s => s.Buckets.Contains(bucket));
            if (s is not null) { owner = s; ownerMission = m; break; }
        }
        if (owner is null)
        {
            Console.WriteLine($"[bucketcomplete] bucket {id} ('{bucket.Name}') completed but no owning side found — nothing produced");
            return;
        }

        // P189: a development bucket's completion runs the native
        // DevelopmentCompleted flow (FedSrv.CPP:11259-11276).
        if (bucket.ItemType == BucketItemType.BitDevelopment && bucket.Development is { } dev)
        {
            ownerMission!.Skills?.Record(owner.Id, ShowcaseSkill.Research);
            CompleteDevelopment(ownerMission!, owner, dev);
            return;
        }

        // P458: a station-type bucket's completion runs the native
        // StationTypeCompleted flow (FedSrv.CPP:11343-11372) — it spawns the
        // construction drone.
        if (bucket.ItemType == BucketItemType.BitStationType && bucket.StationType is { } st)
        {
            CompleteStationType(ownerMission!, owner, st, now);
            return;
        }

        // P459: a drone-type bucket's completion runs the native
        // DroneTypeCompleted flow (FedSrv.CPP:11518-11546) — it spawns that
        // drone type's real-hulled NPC at the home station (miner, wingman,
        // layer, carrier), gives a layer its laid-expendable base data, and
        // sends the team the native where-to-go chats.
        if (bucket.ItemType == BucketItemType.BitDroneType && bucket.DroneType is { } dt)
        {
            CompleteDroneType(ownerMission!, owner, dt, now);
            return;
        }

        // Every other bucket type stays detection-only (the P119 boundary
        // preserved): the synthetic ship-type and any factory/storage-pit/
        // hull/part buckets.
        Console.WriteLine($"[bucketcomplete] bucket {id} ('{bucket.Name}') fully funded AND time-invested at {now:0.###}s — completion detected (P119: non-development/station/drone, nothing produced)");
    }

    /// <summary>
    /// P458 — run the native <c>FedSrvSiteBase::StationTypeCompleted</c> flow
    /// (<c>FedSrv.CPP:11343-11372</c>) for a completed station-type bucket on
    /// <paramref name="side"/>:
    /// <list type="number">
    /// <item><b>Active gate</b> — nothing spawns for an inactive side
    /// (<c>FedSrv.CPP:11348</c>, <c>pside-&gt;GetActiveF()</c>).</item>
    /// <item><b>Spawn the construction drone</b> — the station type's
    /// <c>ConstructionDroneTypeId</c> via <see cref=\"SpawnNpcShip\"/> at the
    /// side's home station (native <c>CreateStockDrone(...GetConstructionDroneType(),
    /// pstation-&gt;GetSide(), pstationtype)</c>, <c>FedSrv.CPP:11351</c>,
    /// <c>FedSrv.CPP:2220-2250</c>).</item>
    /// <item><b>Assign the base data</b> — <c>Ship.BuilderStationType</c> =
    /// the completed station type, the port's stand-in for native
    /// <c>SetBaseData(pstationtype)</c> (what the drone will build).</item>
    /// <item><b>Don't auto-build</b> — native rewrites a default
    /// <c>c_cidBuild</c> to a Goto-to-that-rock + queued DoNothing
    /// (<c>FedSrv.CPP:11358-11366</c>), so the constructor does <b>not</b>
    /// build on its own; this port collapses that to the
    /// <see cref=\"Ship.WaitsForBuildOrder\"/> gate on the builder default
    /// order (see <see cref=\"Ship.DecideBuilderDefaultOrder\"/>).</item>
    /// <item><b>Start undocked</b> — native docks then immediately undocks the
    /// drone (<c>SetStation(pstation)</c> then <c>SetStation(NULL)</c>,
    /// <c>FedSrv.CPP:11354-11356</c>); the port spawns it already-undocked at
    /// the home station position (the native post-undock end state), so the
    /// builder decision cycle sees it available.</item>
    /// <item><b>Request a rock</b> — the team chat "New constructor requesting
    /// asteroid." with the station type's <c>ConstructorNeedRockSound</c> as
    /// the voice id (<c>FedSrv.CPP:11369-11370</c>).</item>
    /// </list>
    /// </summary>
    private void CompleteStationType(Mission mission, Side side, DataStationTypeIGC stationType, double now)
    {
        if (!side.Active)
        {
            Console.WriteLine($"[constr] station-type bucket '{stationType.Name}' (type {stationType.StationTypeId}) completed on INACTIVE side {side.Id} — no constructor (native GetActiveF gate, FedSrv.CPP:11348)");
            return;
        }

        short constructionDroneId = stationType.ConstructionDroneTypeId;
        if (!mission.Core.DroneTypes.TryGetValue(constructionDroneId, out _))
        {
            Console.WriteLine($"[constr] station-type bucket '{stationType.Name}' (type {stationType.StationTypeId}) completed on side {side.Id} but construction drone type {constructionDroneId} absent from the core — no constructor spawned");
            return;
        }

        var home = ResolveSideHomeStation(mission, side.Id);
        if (home is null)
        {
            Console.WriteLine($"[constr] station-type bucket '{stationType.Name}' completed on side {side.Id} but the side has no home station — no constructor spawned");
            return;
        }
        DataStationIGC hs = home.Value;
        var sector = mission.Sectors.FirstOrDefault(s => s.Id == hs.ClusterId);
        if (sector is null)
        {
            Console.WriteLine($"[constr] station-type bucket '{stationType.Name}' completed on side {side.Id} but home station {hs.StationId} has no resident sector — no constructor spawned");
            return;
        }

        var pos = new Vector3(hs.Position[0], hs.Position[1], hs.Position[2]);
        var ship = SpawnNpcShip(mission, sector, side.Id, pos, constructionDroneId);
        ship.BuilderStationType = stationType;   // base data = what it builds (native SetBaseData, FedSrv.CPP:11351,2236)
        ship.WaitsForBuildOrder = true;          // native "Don't auto-build" (FedSrv.CPP:11358-11366)

        Console.WriteLine($"[constr] station-type bucket '{stationType.Name}' complete on side {side.Id} → spawned constructor ship {ship.Id} (drone type {constructionDroneId}, hull '{ship.HullType.Name}', BuilderStationType '{stationType.Name}', waits-for-build) at home '{hs.Name}'");

        BroadcastResearchProduction(mission, ship);
        BroadcastConstructorNeedRock(mission, side.Id, stationType.ConstructorNeedRockSound, ship.Id);
    }

    /// <summary>
    /// P458 — the team chat "New constructor requesting asteroid." with the
    /// station type's <c>ConstructorNeedRockSound</c> as the voice id
    /// (<c>SendChat(pship, CHAT_TEAM, pside, sound, "...")</c>,
    /// <c>FedSrv.CPP:11369-11370</c>) to every peer on the side in
    /// <paramref name="mission"/>.
    /// </summary>
    private void BroadcastConstructorNeedRock(Mission mission, short sideId, short voice, short shipId)
        => BroadcastTeamChat(mission, sideId, voice, "New constructor requesting asteroid.");

    /// <summary>
    /// P459 — a generic team chat to every peer on <paramref name="sideId"/>
    /// in <paramref name="mission"/>, the exact wiring of native
    /// <c>SendChat(pship, CHAT_TEAM, pside, sound, text)</c>
    /// (<c>FedSrv.CPP:11538-11548</c>, <c>FedSrv.CPP:11369-11370</c>). The
    /// <paramref name="voice"/> is the native SoundID passed through verbatim
    /// as the wire <c>VoiceOver</c>; the client resolves the native catalog
    /// while the numeric id is sent unchanged (the same
    /// convention P451/P458 use for core-sourced and constant sound ids).
    /// </summary>
    private void BroadcastTeamChat(Mission mission, short sideId, short voice, string text)
    {
        var msg = new ChatMessage
        {
            Message = text,
            ChatTarget = (int)ChatTarget.Team,
            VoiceOver = voice,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != sideId) continue;
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[teamchat] '{msg.Message}' (voice {voice}) -> side {sideId} [{n} peer(s)]");
    }

    /// <summary>
    /// P725 — the wire target of the host's <see cref="ServerHost.DroneChat"/>
    /// hook: forward a drone/constructor's in-flight chatter line
    /// (<c>Ship.SendDroneChat</c> → <c>IIgcSite.DroneChatEvent</c>) to the
    /// side's team as a <c>CHAT_TEAM</c> chat carrying the native SoundID as
    /// the wire <c>VoiceOver</c> — the exact pipe the where-to pleas
    /// (P723/P459) use, and the direct port of native
    /// <c>GetIgcSite()->SendChat(this, CHAT_TEAM, pside, sound, text)</c>
    /// (<c>shipIGC.cpp:1895-1901,2171,2199</c>). The client resolves the
    /// SoundID back to a clip via <c>NativeVoiceCatalog</c>
    /// through <c>ClientWorld</c>'s sound-event receive path. The delegate runs
    /// on the simulation thread so <see cref="_peerShips"/> is sim-thread-owned
    /// (no lock), matching every other broadcast hook.
    /// </summary>
    private void BroadcastDroneChat(Ship ship, short sideId, short soundId, string text)
    {
        if (ship.Mission is { } mission)
            BroadcastTeamChat(mission, sideId, soundId, text);
    }

    /// <summary>
    /// P459 — the native SoundIDs for the drone where-to-go pleas. These are
    /// <b>compile-time enum constants</b> in native (<c>DEFSOUND(...)</c> entries
    /// in <c>src/Igc/sounds.h</c> expanded by the <c>enum { ... }</c> in
    /// <c>igc.h:455-456</c>, giving sequential SoundID values from 1001), not core
    /// data fields — hence the explicit literals, indexed from that enum:
    /// <list type="bullet">
    /// <item><c>droneWhereToSound = 1328</c> — \"No Helium in Scan range\"
    /// (sounds.h:381): the miner plea \"New miner requesting He3 asteroid.\"
    /// (FedSrv.CPP:11548, shipIGC.cpp:2315).</item>
    /// <item><c>droneWhereToLayMinefieldSound = 1331</c> (sounds.h:384) — \"New
    /// minefield requesting location.\" (FedSrv.CPP:11540).</item>
    /// <item><c>droneWhereToLayTowerSound = 1332</c> (sounds.h:385) — \"New tower
    /// requesting location.\" (FedSrv.CPP:11543).</item>
    /// </list>
    /// (Indices recomputed by counting every <c>DEFSOUND</c>/<c>DEFEMPTYSOUND</c>
    /// entry in file order; the port sends them verbatim, see
    /// <see cref="BroadcastTeamChat"/>.)
    /// </summary>
    private const short DroneWhereToSound = DroneChatterCue.DroneWhereToSound;
    private const short DroneWhereToLayMinefieldSound = DroneChatterCue.DroneWhereToLayMinefieldSound;
    private const short DroneWhereToLayTowerSound = DroneChatterCue.DroneWhereToLayTowerSound;

    /// <summary>
    /// P459 — run the native <c>FedSrvSiteBase::DroneTypeCompleted</c> flow
    /// (<c>FedSrv.CPP:11518-11546</c>) for a completed drone-type bucket on
    /// <paramref name="side"/>:
    /// <list type="number">
    /// <item><b>Active gate</b> — nothing spawns for an inactive side
    /// (<c>FedSrv.CPP:11521</c>, <c>pside-&gt;GetActiveF()</c>).</item>
    /// <item><b>Builder impossible</b> — native <c>assert(pilotType != c_ptBuilder)</c>
    /// (<c>FedSrv.CPP:11522</c>): builders come only from station-type buckets
    /// (P458). This port logs and ignores rather than assert (native debug-build
    /// behaviour).</item>
    /// <item><b>Spawn the real-hulled drone</b> — <c>CreateStockDrone(pdronetype,
    /// pside, layer ? GetLaidExpendable() : NULL)</c> (<c>FedSrv.CPP:11523-11525</c>)
    /// via <see cref="SpawnNpcShip"/> at the side's home station, which after
    /// P458 carries the drone type's real hull and pilot type.</item>
    /// <item><b>Start at the station and undock</b> — native docks
    /// (<c>SetStation(pstation)</c>), picks the default order
    /// (<c>PickDefaultOrder(cluster, pos, true)</c>, <c>FedSrv.CPP:11528</c>),
    /// then undocks (<c>SetStation(NULL)</c>, <c>FedSrv.CPP:11529</c>). This port
    /// spawns the ship already-undocked at the home station position (the native
    /// post-undock end state); its per-pilot-type default order (Miner P156 /
    /// Layer P161 / Wingman P162) is decided lazily in <see cref="Ship.OnUpdate"/>'s
    /// decision cycle.</item>
    /// <item><b>Layer base data</b> — for a <c>c_ptLayer</c>, native passes
    /// <c>GetLaidExpendable()</c> (the <c>EtidLaid</c> expendable) as the drone's
    /// base data; the port records it on the ship
    /// (<see cref="Ship.LayerLaidExpendableTypeId"/>). Whether that expendable is
    /// a mine type selects the team chat text. Actual deployment is P161's known
    /// gap.</item>
    /// <item><b>Team chats</b> (<c>FedSrv.CPP:11530-11548</c>):
    /// <list type="bullet">
    /// <item>Layer with a <c>OT_mineType</c> laid expendable → \"New minefield
    /// requesting location.\" (<see cref="DroneWhereToLayMinefieldSound"/>); any
    /// other layer → \"New tower requesting location.\"
    /// (<see cref="DroneWhereToLayTowerSound"/>).</item>
    /// <item>A Miner whose default order is <em>not</em> <c>c_cidMine</c> (native
    /// <c>GetCommandID(c_cmdCurrent) != c_cidMine</c>, <c>FedSrv.CPP:11546</c>) →
    /// \"New miner requesting He3 asteroid.\" (<see cref="DroneWhereToSound"/>).
    /// The port's miner order is decided lazily in <see cref="Ship.OnUpdate"/>, so
    /// a freshly-spawned miner has no order yet at completion — the exact state
    /// native guards on — and the plea is sent; whether it subsequently finds a
    /// rock is orthogonal (documented approximation of the native gate).</item>
    /// <item>Carrier and every other pilot type get no chat (native has none).</item>
    /// </list></item>
    /// </list>
    /// A Carrier (and any unrecognized pilot type, e.g. rescue/recon pilot type 1)
    /// is spawned so the investment isn't lost, but has <b>no behaviour arm</b> —
    /// it sits idle (documented gap; carrier AI is out of scope).
    /// </summary>
    private void CompleteDroneType(Mission mission, Side side, DataDroneTypeIGC droneType, double now)
    {
        if (!side.Active)
        {
            Console.WriteLine($"[drone] drone-type bucket '{droneType.Name}' (drone type {droneType.DroneTypeId}) completed on INACTIVE side {side.Id} — no drone spawned (native GetActiveF gate, FedSrv.CPP:11521)");
            return;
        }

        // Native assert(pilotType != c_ptBuilder) — builders come only from
        // station-type buckets (P458). Log and ignore, the port's stand-in for
        // the debug-build assert (never produced by the real catalog anyway).
        if (droneType.PilotType == Ship.PilotTypeBuilder)
        {
            Console.WriteLine($"[drone] drone-type bucket '{droneType.Name}' (drone type {droneType.DroneTypeId}) completed with pilot type Builder — builders come only from station buckets (native assert, FedSrv.CPP:11522); ignoring");
            return;
        }

        var home = ResolveSideHomeStation(mission, side.Id);
        if (home is null)
        {
            Console.WriteLine($"[drone] drone-type bucket '{droneType.Name}' completed on side {side.Id} but the side has no home station — no drone spawned");
            return;
        }
        DataStationIGC hs = home.Value;
        var sector = mission.Sectors.FirstOrDefault(s => s.Id == hs.ClusterId);
        if (sector is null)
        {
            Console.WriteLine($"[drone] drone-type bucket '{droneType.Name}' completed on side {side.Id} but home station {hs.StationId} has no resident sector — no drone spawned");
            return;
        }

        var pos = new Vector3(hs.Position[0], hs.Position[1], hs.Position[2]);
        var ship = SpawnNpcShip(mission, sector, side.Id, pos, droneType.DroneTypeId);
        BroadcastResearchProduction(mission, ship);

        // Layer base data = the laid expendable (native CreateStockDrone's
        // GetLaidExpendable(), FedSrv.CPP:11523-11525), recorded on the ship;
        // whether it is a mine type selects the team chat text.
        bool isMineLayer = droneType.PilotType == Ship.PilotTypeLayer
            && mission.Core.MineTypes.ContainsKey(droneType.EtidLaid);
        if (droneType.PilotType == Ship.PilotTypeLayer)
            ship.LayerLaidExpendableTypeId = droneType.EtidLaid;

        Console.WriteLine($"[drone] drone-type bucket '{droneType.Name}' complete on side {side.Id} → spawned drone ship {ship.Id} (drone type {droneType.DroneTypeId}, pilotType {droneType.PilotType}, hull '{ship.HullType.Name}', laysExpendable {(droneType.PilotType == Ship.PilotTypeLayer ? droneType.EtidLaid.ToString() : "-")}) at home '{hs.Name}'");

        // Team chats (FedSrv.CPP:11530-11548).
        if (droneType.PilotType == Ship.PilotTypeLayer)
        {
            if (isMineLayer)
                BroadcastTeamChat(mission, side.Id, DroneWhereToLayMinefieldSound, "New minefield requesting location.");
            else
                BroadcastTeamChat(mission, side.Id, DroneWhereToLayTowerSound, "New tower requesting location.");
        }
        else if (droneType.PilotType == Ship.PilotTypeMiner)
        {
            BroadcastTeamChat(mission, side.Id, DroneWhereToSound, "New miner requesting He3 asteroid.");
        }
        // Carrier and every other pilot type: spawned (investment not lost), no
        // chat (native has none), no behaviour arm (documented gap).
    }

/// <summary>
    /// Run the native <c>FedSrvSiteBase::DevelopmentCompleted</c> flow
    /// (<c>FedSrv.CPP:11259-11276</c>) for a completed development bucket on
    /// <paramref name="side"/>, preserving the exact native ordering:
    /// <list type="number">
    /// <item><b>Apply the effect techs</b> (<c>pside-&gt;ApplyDevelopmentTechs</c>,
    /// <c>FedSrv.CPP:11261</c>): only genuinely-new bits (the native
    /// <c>rc = !(ttbm &lt;= ttbmDevelopmentTechs)</c>, <c>sideigc.h:194-210</c>)
    /// are OR-ed into <see cref="Side.DevelopmentTechs"/>, and only then — and
    /// only while the mission is <c>Started</c> — is <c>SIDE_TECH_CHANGE(75)</c>
    /// broadcast to the side's own group (<c>SideDevelopmentTechChange</c>,
    /// <c>FedSrv.CPP:11282-11289</c>).</item>
    /// <item><b>Broadcast <c>DEV_COMPLETED(8)</c></b> to the side's own group
    /// (<c>FedSrv.CPP:11262-11266</c>) — unconditional (no stage gate).</item>
    /// <item><b>Unless tech-only</b>, apply the development's attribute delta
    /// (<c>pside-&gt;ApplyGlobalAttributeSet(pdev-&gt;GetGlobalAttributeSet())</c>,
    /// <c>FedSrv.CPP:11270</c>) — the per-element multiply
    /// <see cref="GlobalAttributeSet.Apply"/> — which fires
    /// <c>SIDE_ATTRIBUTE_CHANGE(76)</c> to the <b>whole mission</b>
    /// (<c>SideGlobalAttributeChange</c>, <c>FedSrv.CPP:11313-11318</c>,
    /// unconditional).</item>
    /// </list>
    /// The <c>c_didTeamMoney</c> economic-victory shortcut is out of scope (see
    /// the stage report).</summary>
    private void CompleteDevelopment(Mission mission, Side side, DataDevelopmentIGC dev)
    {
        var effects = TechTreeBitMask.FromBytes(dev.TtbmEffects);

        // 1. ApplyDevelopmentTechs — OR in only genuinely-new bits; announce via
        //    SIDE_TECH_CHANGE (gated on Stage == Started), native FedSrv.CPP:11283.
        bool newTechs = !effects.IsSubsetOf(side.DevelopmentTechs);   // sideigc.h:197
        if (newTechs)
        {
            side.DevelopmentTechs = side.DevelopmentTechs.Or(effects);
            if (mission.Stage == MissionStage.Started)
                BroadcastSideTechChange(mission, side);
            // P845 — native SideDevelopmentTechChange (FedSrv.CPP:11304-11313):
            // after the (stage-gated) broadcast, upgrade every station the side
            // owns to its buyable successor type, unconditionally. (The trailing
            // UpgradeDrones — same native fn, FedSrv.CPP:11312 — is SIM-0636's
            // scope, realised in this port at dock-time.)
            side.UpgradeStationsToSuccessors();
        }

        // 2. DEV_COMPLETED — unconditional, side's own group (FedSrv.CPP:11262-11266).
        BroadcastDevCompleted(mission, side, dev.DevelopmentId);

        // 3. Unless tech-only, apply the attribute delta (the native multiply,
        //    igc.h:935-946) → whole-mission SIDE_ATTRIBUTE_CHANGE.
        bool techOnly = DevelopmentLogic.IsTechOnly(dev);
        if (!techOnly)
        {
            side.GlobalAttributes = side.GlobalAttributes.Apply(GlobalAttributeSet.FromArray(dev.Gas));
            BroadcastSideAttributeChange(mission, side);
        }

        Console.WriteLine($"[devcomplete] side {side.Id}: completed development {dev.DevelopmentId} '{dev.Name}' -> " +
                          $"{(newTechs ? $"+{effects.PopCount} new tech bit(s)" : "no new techs")}; " +
                          $"DEV_COMPLETED(8) side-group" +
                          $"{(newTechs && mission.Stage == MissionStage.Started ? "; SIDE_TECH_CHANGE(75) side-group (stage Started)" : (newTechs ? " (SIDE_TECH_CHANGE suppressed: stage != Started)" : ""))}" +
                          $"{(techOnly ? "; tech-only, no attribute delta" : "; SIDE_ATTRIBUTE_CHANGE(76) whole-mission")}");

        // 4. P302 — the prosperity win: completing the c_didTeamMoney development
        //    ends the game for its owner (FedSrv.CPP:11274-11279). Gated on
        //    ProsperityEnabled so a non-prosperity mission completing this dev is
        //    unaffected; the recompute confirms the winner and raises the seam the
        //    host consumes (GAME_STATE + P178 DeclareGameOver).
        if (dev.DevelopmentId == Mission.TeamMoneyDevelopmentId && mission.ProsperityEnabled)
        {
            var (pWinner, pChanged) = mission.RecomputeProsperityAndArtifacts();
            if (pChanged || pWinner is not null)
                mission.Site.ProsperityArtifactComputed(mission, pChanged, pWinner);
        }
    }

/// <summary>Broadcast <c>SIDE_TECH_CHANGE(75)</c> to the side's own connected
    /// peers — the port of <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c> (the same
    /// side-group analogue <c>BroadcastMoneyChangeSideWide</c>/the SetWingId relay
    /// use, <c>Server.cs:3626-3637</c>), scoped to BOTH the side's id <em>and</em>
    /// its mission (side ids are not unique across this server's world missions).
    /// Carries the side's post-OR development tech mask. Only called when the
    /// mission is <c>Started</c> (native stage gate, FedSrv.CPP:11283).</summary>
    private void BroadcastSideTechChange(Mission mission, Side side)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Mission == mission && kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        var msg = new SideTechChange { SideId = side.Id, TtbmDevelopments = side.DevelopmentTechs.ToBytes() };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[sidetech] side {side.Id} -> SIDE_TECH_CHANGE(75) devMask({side.DevelopmentTechs.PopCount} bits) x{targets.Length} side-group peer(s)");
    }

/// <summary>Broadcast <c>DEV_COMPLETED(8)</c> <c>{devId}</c> to the side's own
    /// connected peers — unconditional, side's own group
    /// (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, FedSrv.CPP:11264-11266),
    /// scoped to both the side's id and its mission (side ids repeat across
    /// world missions).</summary>
    private void BroadcastDevCompleted(Mission mission, Side side, short devId)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Mission == mission && kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        var msg = new DevCompleted { DevId = devId };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[devcompleted] side {side.Id} -> DEV_COMPLETED(8) devId={devId} x{targets.Length} side-group peer(s)");
    }

/// <summary>Broadcast <c>SIDE_ATTRIBUTE_CHANGE(76)</c> to the <b>whole
    /// mission</b> (every peer whose ship belongs to <paramref name="mission"/>,
    /// all sides) — the port of native <c>GetGroupMission()</c>
    /// (<c>FedSrv.CPP:11317</c>), unconditional. Carries the side's post-Apply
    /// 25 attribute multipliers.</summary>
    private void BroadcastSideAttributeChange(Mission mission, Side side)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Mission == mission)
            .Select(kv => kv.Key)
            .ToArray();
        var msg = new SideAttributeChange { SideId = side.Id, GasAttributes = side.GlobalAttributes.ToArray() };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[sideattr] side {side.Id} -> SIDE_ATTRIBUTE_CHANGE(76) x{targets.Length} whole-mission peer(s)");
    }

/// <summary>
    /// Handle a simulated payday (<see cref="ServerHost.PaydayEvent"/>, raised
    /// from <see cref="Mission.Update"/>'s payday pass on the sim thread) by
    /// performing the reference <c>CFSMission::DoPayday(IsideIGC*)</c> payout
    /// (<c>fsmission.cpp:2294-2326</c>) on the wire. <see cref="Mission"/>
    /// has already accrued the side's income into <see cref="Side.Money"/> and
    /// passed it through the site seam; this handler does the distribution
    /// half — the <c>delta</c> split by the side's player count, the
    /// <c>FM_S_PAYDAY(114)</c> broadcast to that side's own connected peers
    /// (the same per-side peer grouping <c>SelectChatTargets</c> uses for
    /// <c>Team</c>, Server.cs:1124-1128), and the <c>delta·np</c> deduction
    /// that leaves only the remainder in the side treasury. Runs on the sim
    /// thread (the same one that owns <c>_peerShips</c> and broadcasts ship
    /// states), so no extra locking is needed — mirroring
    /// <see cref="BroadcastKillShip"/>.
    /// </summary>
    private void OnPayday(ISideIgc sideIfc, float incomeAccrued)
    {
        if (sideIfc is not Side side)
        {
            Console.WriteLine("[payday] PaydayEvent with a non-Side side — ignored");
            return;
        }

        // Per-side player count, matching GetCountOfPlayers(pside, true)
        // (fsmission.cpp:2298): peers seated on this side via their ship's
        // SideId. The grouping is the same peer-resolution the chat Team relay
        // uses (Server.cs:1124-1128).
        var sidePeers = _peerShips
            .Where(kv => kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        int playerCount = sidePeers.Length;

        // A side with zero players accrues but never pays out — the money stays
        // in the side treasury (matching the reference `if (np != 0)` guard at
        // fsmission.cpp:2298-2299, which is what avoids a divide-by-zero). Not a
        // crash, not a fabricated fallback.
        if (playerCount == 0)
        {
            Console.WriteLine($"[payday] side {side.Id} '{side.Name}': 0 players — accrued {incomeAccrued:F0} kept in treasury (balance {side.Money})");
            return;
        }

        long deltaLong = side.Money / playerCount;   // integer division, fsmission.cpp:2301
        if (deltaLong <= 0)
        {
            Console.WriteLine($"[payday] side {side.Id} '{side.Name}': delta {deltaLong} — nothing to pay (balance {side.Money})");
            return;
        }

        int delta = (int)deltaLong;                  // native Money is 32-bit (AGCIDL.idl:246)
        var msg = new Payday { DMoney = delta };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        foreach (var t in sidePeers)
            SendPeer(t, packet);                     // side-scoped broadcast, fsmission.cpp:2307

        side.Money -= deltaLong * playerCount;       // keep the remainder < np, fsmission.cpp:2309

        // P197 — the actual per-player wallet credit (native DoPayday,
        // fsmission.cpp:2310-2324): each player's share lands in their resolved
        // auto-donate target's wallet (if one is set), else in their own. This
        // is the real behavior the P51 port skipped (it only broadcast PAYDAY
        // and drained the treasury without crediting anyone). Faithful to native:
        // CFSPlayer::SetMoney is a plain setter that fires NO MONEY_CHANGE here —
        // the per-side gross share is already announced by PAYDAY(114), and the
        // client derives its own wallet from the donate relationship, so there is
        // deliberately no MONEY_CHANGE at payday (see PLAYER_MONEY_AUTODONATE_P197.md).
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.SideId != side.Id) continue;
            var donate = kv.Value.Wallet.AutoDonateTarget;
            var receiver = donate is null ? kv.Value : LiveOf(donate);
            if (receiver is not null)
                receiver.Wallet.Money = (int)Math.Clamp((long)receiver.Wallet.Money + delta, 0, int.MaxValue);
        }

        var payNote = $"[payday] side {side.Id} '{side.Name}' -> PAYDAY(114) delta={delta} balance={side.Money}";
        Console.WriteLine(payNote);
        Notify(payNote);                      // spectator event feed (same text)
    }
}
