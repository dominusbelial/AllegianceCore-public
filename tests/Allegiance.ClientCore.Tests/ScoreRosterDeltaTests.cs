using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class ScoreRosterDeltaTests
{
    private static ScoreRosterEntry Row(string name, short kills = 0, short deaths = 0) =>
        new(name, 1, kills, deaths, 3, 9f, 5f);

    private static void Receive(ConnectionSession session, IFedMessage message) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(message.Encode()));

    [Fact]
    public void Real_dispatch_applies_kills_deaths_joins_leaves_and_full_refresh_heals_a_gap()
    {
        using var session = new ConnectionSession();
        ScoreRosterEntry[] rows = [Row("A"), Row("B")];
        Receive(session, new ScoreRosterUpdate { Entries = rows });
        foreach (ScoreRosterEntry[] next in new ScoreRosterEntry[][]
            { [Row("A", 2), Row("B", deaths: 2)], [Row("A", 2), Row("B", deaths: 2), Row("C")],
              [Row("B", deaths: 3), Row("C", 1)] })
        {
            Receive(session, ScoreRosterDelta.Between(rows, next));
            Assert.Equal(next, session.World.ScoreRoster);
            rows = next;
        }
        // Lose a patch with the same count: the reliable lane normally prevents
        // this, but the periodic authoritative snapshot must still repair it.
        ScoreRosterEntry[] missed = [Row("B", 5, 3), rows[1]];
        ScoreRosterEntry[] later = [missed[0], Row("C", 2)];
        Receive(session, ScoreRosterDelta.Between(missed, later));
        Assert.NotEqual(later, session.World.ScoreRoster.ToArray());
        Receive(session, new ScoreRosterUpdate { Entries = later });
        Assert.Equal(later, session.World.ScoreRoster);
        Receive(session, new ScoreRosterUpdate { Entries = [] });
        Assert.Empty(session.World.ScoreRoster);
    }

    [Fact]
    public void Reset_discards_baseline_and_incompatible_delta_waits_for_full_snapshot()
    {
        using var session = new ConnectionSession();
        ScoreRosterEntry[] rows = [Row("A"), Row("B")];
        Receive(session, new ScoreRosterUpdate { Entries = rows });
        session.World.Reset();
        Receive(session, ScoreRosterDelta.Between(rows, [Row("A", 1), rows[1]]));
        Assert.Empty(session.World.ScoreRoster);
        Receive(session, new ScoreRosterUpdate { Entries = rows });
        Assert.Equal(rows, session.World.ScoreRoster);
    }
}
