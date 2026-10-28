using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P868PaydayTests
{
    // Native fsmission.cpp:2294-2326: each mission's own side supplies the
    // count, recipients and wallet changes; integer remainder stays on it.
    [SkippableFact]
    public void Same_side_number_in_another_mission_cannot_receive_or_dilute_payday()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config();
        config.Games.Add(new()
        {
            Name = "Second game", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
        });
        server.Boot(config);
        var first = server.Missions[0];
        var second = server.Missions[1];
        var peers = new List<(ConnectionId Conn, Ship Ship, Sector Sector)>();
        short id = 10000;
        foreach (var mission in new[] { first, first, second })
        {
            var ship = new Ship(id++, mission.Core.HullTypes.Values.First(), Vector3.Zero,
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, mission.Sectors[0]);
            peers.Add((new ConnectionId((uint)id), ship, mission.Sectors[0]));
        }
        server.TestSeedPeers(peers);
        var wallets = peers.Select(p => server.Ships.Single(l => ReferenceEquals(l.Ship, p.Ship)).Wallet).ToArray();
        var side = first.Sides[0];
        side.Money = 11;
        second.Sides[0].Money = 29;
        var recipients = new List<ConnectionId>();
        server.TestPublicationCapture = (peer, packet, _, guaranteed) =>
        {
            if (!FedPacket.Unpack(packet).Any(message =>
                FedMessageHeader.Read(message).Fmid == Allegiance.Core.Wire.Payday.Fmid)) return;
            Assert.True(guaranteed);
            recipients.Add(peer);
        };
        void Payday(Side target) => typeof(Srv).GetMethod("OnPayday", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, [target, 0f]);
        Payday(side);
        server.FlushBroadcastSnapshots();
        Assert.Equal(peers.Take(2).Select(p => p.Conn).OrderBy(p => p.Value), recipients.OrderBy(p => p.Value));
        recipients.Clear();
        Assert.Equal(5, wallets[0].Money);
        Assert.Equal(5, wallets[1].Money);
        Assert.Equal(0, wallets[2].Money);
        Assert.Equal(1, side.Money);
        Assert.Equal(29, second.Sides[0].Money);
        Payday(second.Sides[0]);
        server.FlushBroadcastSnapshots();
        Assert.Equal(peers[2].Conn, Assert.Single(recipients));
        recipients.Clear();
        Assert.Equal(29, wallets[2].Money);
        Assert.Equal(5, wallets[0].Money);
        Assert.Equal(5, wallets[1].Money);
        Assert.Equal(0, second.Sides[0].Money);
        // delta <= 0 and zero-player branches preserve treasury/wallets.
        Payday(side);
        Assert.Equal(1, side.Money);
        second.Sides[1].Money = 17;
        Payday(second.Sides[1]);
        server.FlushBroadcastSnapshots();
        Assert.Empty(recipients);
        Assert.Equal(17, second.Sides[1].Money);
    }
}
