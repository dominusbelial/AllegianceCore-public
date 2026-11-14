namespace Allegiance.Core.Sim;

/// <summary>
/// P1017 — the one rule for hardware a player holds without having paid for it
/// (the port's spawn kit). Native never hands out priced hardware: a fresh
/// player's ship has no hull, "it has to be free so they don't get a cash back
/// bonus when they trade it in for a new ship" (<c>FedSrv/FedSrv.CPP:902-909</c>).
///
/// <para>Native prices a buy as "new ship value minus old ship value"
/// (<c>FedSrv/FedSrv.CPP:6183-6193</c>, <c>Igc/shipIGC.h:396-406</c>). For that
/// sum to stay honest with free hardware in play, a free part is worth nothing
/// on <b>both</b> sides of it: it refunds nothing when it leaves the ship and it
/// costs nothing while the ship keeps it. A part is kept when the new loadout
/// asks for the same part type the ship already holds free, with no more units
/// than it holds — the same "is this part type already on the old ship" match
/// native uses to re-use an old part (<c>Igc/shipIGC.h:1044-1052</c>).</para>
///
/// <para>The server applies this to the real ship; the client applies it to its
/// own record of the spawn kit, so the price it shows is the price the server
/// takes.</para>
/// </summary>
public static class FreeHardware
{
    /// <summary>One part instance: its type and how many units it holds.</summary>
    public readonly record struct Unit(short PartId, short Amount);

    /// <summary>
    /// For each part of <paramref name="next"/>, in order: true when it is one of
    /// the free parts in <paramref name="held"/> being kept. Every held part is
    /// matched at most once.
    /// </summary>
    public static bool[] Kept(IReadOnlyList<Unit> held, IReadOnlyList<Unit> next)
    {
        var kept = new bool[next.Count];
        if (held.Count == 0) return kept;
        var used = new bool[held.Count];
        for (int i = 0; i < next.Count; i++)
            for (int j = 0; j < held.Count; j++)
            {
                if (used[j] || held[j].PartId != next[i].PartId || next[i].Amount > held[j].Amount) continue;
                used[j] = kept[i] = true;
                break;
            }
        return kept;
    }
}
