using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1017 — the loadout screen's Buy / Launch flow, shared by the old loadout and
/// Loadout V2 (one copy of the rules, one wire path, no new message). Pure: it
/// decides what to send and reconciles the shop with what the server answered;
/// the caller does the sending.
/// <list type="bullet">
/// <item><b>Launch</b> — native <c>OnButtonLaunch</c>
/// (<c>WinTrek/load.cpp:1498-1509</c>): when the wallet plus the docked ship
/// covers the pending ship, one <c>BUY_LOADOUT</c> with <c>fLaunch</c>; otherwise
/// nothing at all.</item>
/// <item><b>Buy</b> — native <c>OnButtonBuy</c> (<c>load.cpp:1511-1531</c>): when
/// short, <c>REQUEST_MONEY</c> for the shortfall; otherwise <c>BUY_LOADOUT</c>
/// without launch and the pending ship saved as custom loadout 0.</item>
/// <item><b>The answer</b> — native <c>FM_S_BUY_LOADOUT_ACK</c>
/// (<c>clintlib/appmsg.cpp:3176-3187</c>) and <c>OnPurchaseCompleted</c>
/// (<c>load.cpp:2193-2210</c>): see <see cref="ShopModel.PurchaseCompleted"/>.</item>
/// </list>
/// </summary>
public sealed class LoadoutBuyFlow
{
    public enum Step
    {
        /// <summary>Nothing to send.</summary>
        None,
        /// <summary>Send <c>REQUEST_MONEY</c> for <see cref="Order.Amount"/>.</summary>
        RequestMoney,
        /// <summary>Send <c>BUY_LOADOUT</c>.</summary>
        Purchase,
    }

    public readonly record struct Order(Step Step, short HullId, IReadOnlyList<ExpandedPartData> Parts,
        bool Launch = false, int Amount = 0);

    /// <summary>What <see cref="Sync"/> found.</summary>
    /// <param name="Changed">A loadout acknowledgement was applied to the shop.</param>
    /// <param name="Answered">It answered a request of ours.</param>
    /// <param name="Complete">The server granted all of it.</param>
    /// <param name="LaunchRefused">We asked to launch and the server did not
    /// launch us: native ends the launch lock-down on an acknowledgement without
    /// <c>fLaunch</c> (<c>appmsg.cpp:3180-3181</c>).</param>
    public readonly record struct Outcome(bool Changed, bool Answered = false, bool Complete = true, bool LaunchRefused = false);

    private readonly record struct Asked(short HullId, int Parts, bool Launch, long Cost,
        short ShipHull, ExpandedPartData[] ShipParts);

    private Asked? _asked;
    private int _revision;
    private int _answer;

    /// <summary>Start following a session's acknowledgements from where it is
    /// now (a new dock: older answers are not this screen's).</summary>
    public void Attach(OwnHardwareLedger ledger)
    {
        _revision = ledger.Revision;
        _answer = ledger.LastAnswer.Serial;
        _asked = null;
    }

    /// <summary>The Buy button.</summary>
    /// <param name="nativeGate">Apply native's disabled state: Buy does nothing
    /// for the loadout the ship already has (<c>load.cpp:2181</c>). The old
    /// loadout screen has no disabled buttons and passes false.</param>
    public Order Buy(ShopModel shop, bool nativeGate)
    {
        if (nativeGate && !shop.BuyEnabled) return default;
        long shortfall = shop.BuyShortfall;
        if (shortfall > 0)
        {
            shop.NoteMoneyRequested(shortfall);
            return new(Step.RequestMoney, shop.EffectiveHullId, [], Amount: (int)Math.Min(shortfall, int.MaxValue));
        }
        shop.SaveCustomLoadout();                       // load.cpp:1524, :1527
        return Purchase(shop, launch: false);
    }

    /// <summary>The Launch button. Unaffordable: nothing is sent and
    /// <see cref="ShopModel.BlockReason"/> says why.</summary>
    public Order Launch(ShopModel shop) => shop.TryLaunch() ? Purchase(shop, launch: true) : default;

    private Order Purchase(ShopModel shop, bool launch)
    {
        var parts = shop.ChosenParts.ToArray();
        _asked = new(shop.EffectiveHullId, parts.Length, launch, shop.Cost, shop.CurrentHullId, shop.CurrentParts.ToArray());
        return new(Step.Purchase, shop.EffectiveHullId, parts, launch);
    }

    /// <summary>
    /// Bring the shop in line with the wallet and with the ship the server says
    /// we have. Call it every docked frame; the ship part does nothing until a
    /// new acknowledgement arrives.
    /// </summary>
    /// <param name="own">Our ship as the world holds it (already updated by the
    /// acknowledgement).</param>
    /// <param name="trackFreeHardware">False where there is no wallet (local
    /// training): every part then counts at its full value.</param>
    /// <param name="richestTeammate">The most money any other player on our
    /// side holds (native's "can anyone on the team afford it").</param>
    public Outcome Sync(ShopModel shop, ShipEntity own, OwnHardwareLedger ledger, int? money,
        bool trackFreeHardware = true, long? richestTeammate = null)
        => Sync(shop, own.HullId, own.Loadout, own.Cargo, ledger, money, trackFreeHardware, richestTeammate);

    /// <summary><see cref="Sync(ShopModel, ShipEntity, OwnHardwareLedger, int?, bool, long?)"/>
    /// with our ship given as its hull, mounted parts and cargo.</summary>
    public Outcome Sync(ShopModel shop, short ownHullId, IReadOnlyList<ExpandedPartData>? ownParts,
        IReadOnlyList<ExpandedPartData>? ownCargo, OwnHardwareLedger ledger, int? money,
        bool trackFreeHardware = true, long? richestTeammate = null)
    {
        // Money first, every call: SET_MONEY / MONEY_CHANGE / PAYDAY move the
        // wallet while docked and the buttons follow (OnMoneyChange, load.cpp:1417-1422).
        shop.RefreshMoney(money);
        shop.RefreshTeamMoney(richestTeammate);
        if (ledger.Revision == _revision) return default;
        _revision = ledger.Revision;
        var answer = ledger.LastAnswer;
        bool mine = answer.Serial != _answer && _asked is not null;
        _answer = answer.Serial;

        var parts = ownParts ?? [];
        bool complete = true, forMoney = false, launchRefused = false;
        if (mine && _asked is { } asked)
        {
            // The hull was not granted: the ship still has the hull it had.
            bool hullRefused = answer.HullId != asked.HullId && answer.HullId == answer.HullBefore;
            complete = !hullRefused && (asked.Parts == 0 || answer.BoughtEverything);
            bool unchanged = ownHullId == asked.ShipHull && parts.SequenceEqual(asked.ShipParts);
            forMoney = !complete && unchanged && money is { } wallet && asked.Cost > wallet;
            launchRefused = asked.Launch && !answer.Launch;
            _asked = null;
        }
        shop.PurchaseCompleted(ownHullId, parts, ownCargo, complete, forMoney,
            hullPaid: !trackFreeHardware || ledger.HullPaid(ownHullId),
            freeParts: trackFreeHardware ? ledger.FreeParts.ToArray() : []);
        return new(true, mine, complete, launchRefused);
    }
}
