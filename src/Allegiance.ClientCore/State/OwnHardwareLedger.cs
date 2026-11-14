using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P1017 — what the local player's ship is worth to the server, kept from the
/// messages the client already receives (no wire change).
///
/// <para>The server refunds only hardware that was paid for: the spawn hull and
/// the spawn kit are free (native's fresh ship "has to be free so they don't get
/// a cash back bonus when they trade it in", <c>FedSrv/FedSrv.CPP:902-909</c>),
/// and a free part the ship keeps through a buy stays free
/// (<see cref="FreeHardware"/>). The client cannot read that off a loadout, but
/// it can follow it: until it has sent a <c>BUY_LOADOUT</c> of its own, every
/// loadout it is told about is hardware it never paid for; after that, each
/// acknowledgement keeps a part free only while it is still on the ship, and a
/// hull is paid for when a buy of ours changed it.</para>
///
/// <para>Known limits, both on the cautious side (the shown refund is never more
/// than the real one for these): a hull inherited by promotion from a paid ship
/// is shown as free; a session that rejoins an already-bought ship shows it as
/// free until the next buy.</para>
/// </summary>
public sealed class OwnHardwareLedger
{
    private readonly Queue<short> _hullWhenAsked = new();
    private FreeHardware.Unit[] _free = [];
    private short? _paidHull;
    private bool _everBought;

    /// <summary>One answer to a <c>BUY_LOADOUT</c> of ours.</summary>
    /// <param name="Serial">Counts up with every answer; 0 = none yet.</param>
    /// <param name="HullBefore">Our hull when the request was sent.</param>
    public readonly record struct Answer(int Serial, short HullBefore, short HullId, bool BoughtEverything, bool Launch);

    /// <summary>The last answer to a request of ours.</summary>
    public Answer LastAnswer { get; private set; }

    /// <summary>Counts every loadout acknowledgement for our ship, asked for or not.</summary>
    public int Revision { get; private set; }

    /// <summary>Requests sent and not yet answered.</summary>
    public int Pending => _hullWhenAsked.Count;

    /// <summary>The parts on our ship that were never paid for.</summary>
    public IReadOnlyList<FreeHardware.Unit> FreeParts => _free;

    /// <summary>True when the hull we sit in was bought.</summary>
    public bool HullPaid(short currentHullId) => _paidHull == currentHullId;

    public void Reset()
    {
        _hullWhenAsked.Clear();
        _free = [];
        _paidHull = null;
        _everBought = false;
        LastAnswer = default;
        Revision = 0;
    }

    /// <summary>A <c>BUY_LOADOUT</c> of ours is on its way.</summary>
    public void RequestSent(short currentHullId)
    {
        _everBought = true;
        _hullWhenAsked.Enqueue(currentHullId);
    }

    /// <summary>A loadout acknowledgement for our ship arrived.</summary>
    public void Acknowledged(BuyLoadoutAck ack)
    {
        var units = new FreeHardware.Unit[ack.Parts.Count];
        for (int i = 0; i < units.Length; i++) units[i] = new(ack.Parts[i].PartId, ack.Parts[i].Amount);
        bool mine = _hullWhenAsked.TryDequeue(out short before);
        if (!_everBought)
        {
            _free = units;               // nothing was ever bought: all of it is the spawn kit
            _paidHull = null;
        }
        else
        {
            bool[] kept = FreeHardware.Kept(_free, units);
            _free = units.Where((_, i) => kept[i]).ToArray();
            if (mine && ack.HullId != before) _paidHull = ack.HullId;
            else if (!mine && _paidHull != ack.HullId) _paidHull = null;   // re-hulled without a buy (pod)
        }
        Revision++;
        if (mine)
            LastAnswer = new(LastAnswer.Serial + 1, before, ack.HullId, ack.FBoughtEverything, ack.FLaunch);
    }
}
