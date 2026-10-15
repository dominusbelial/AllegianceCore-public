using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P655GameOverDeliveryTests
{
    [SkippableTheory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 51)]
    [InlineData(true, 51)]
    public void Results_are_flushed_together_to_the_native_audience_before_reset(bool sideOnly, int playerCount)
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(new()
        {
            Port = 0, ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc",
            Games = [new() { Name = "A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
                new() { Name = "B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0];
        var otherMission = server.Missions[1];
        mission.Stage = otherMission.Stage = MissionStage.Started;
        var hull = mission.Core.HullTypes.Values.First();
        var winner = new Ship(300, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var loser = new Ship(301, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1 };
        var other = new Ship(302, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(winner, mission.Sectors[0]);
        mission.PlaceShip(loser, mission.Sectors[0]);
        otherMission.PlaceShip(other, otherMission.Sectors[0]);
        ConnectionId a = new(31), b = new(32), c = new(33);
        var peers = new List<(ConnectionId Conn, Ship Ship, Sector Sector)>
            { (a, winner, mission.Sectors[0]), (b, loser, mission.Sectors[0]) };
        for (int i = 2; i < playerCount; i++)
        {
            var ship = new Ship((short)(300 + i), hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = (short)(i % 2) };
            mission.PlaceShip(ship, mission.Sectors[0]);
            peers.Add((new ConnectionId((uint)(100 + i)), ship, mission.Sectors[0]));
        }
        server.TestSeedPeers([.. peers, (c, other, otherMission.Sectors[0])]);
        server.Ships.Single(l => l.Ship == winner).PlayerScore.AddKill();
        server.Ships.Single(l => l.Ship == winner).PlayerScore.AddRescue();
        server.Ships.Single(l => l.Ship == loser).PlayerScore.AddRescue();
        typeof(Srv).GetField("_simNow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, 10.0);
        var results = new List<(ConnectionId Peer, byte[][] Messages)>();
        server._testSendCapture = (peer, packet) =>
        {
            var messages = FedPacket.Unpack(packet);
            if (!messages.Any(m => FedMessageHeader.Read(m).Fmid is GameOver.Fmid or GameOverPlayers.Fmid)) return;
            // Observe at the actual send boundary, before any reset/reseat can
            // replace these ships or erase the previous match's counters.
            Assert.Same(mission, server.Missions[0]);
            Assert.Same(winner, server.Ships.Single(l => l.Ship.Id == winner.Id).Ship);
            results.Add((peer, messages));
        };

        if (sideOnly)
            typeof(Srv).GetMethod("SendSideOnlyGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [mission, (short)0, "side eliminated", new ConnectionId[] { b }]);
        else
            typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [mission, (short)0, "conquest"]);

        var recipients = sideOnly ? new[] { b } : peers.Select(p => p.Conn).ToArray();
        Assert.Equal(recipients.Length, results.Count);
        foreach (var peer in recipients)
        {
            var result = Assert.Single(results, r => r.Peer == peer);
            Assert.Equal(GameOver.Fmid, FedMessageHeader.Read(result.Messages[0]).Fmid);
            Assert.All(result.Messages.Skip(1), m => Assert.Equal(GameOverPlayers.Fmid, FedMessageHeader.Read(m).Fmid));
            var chunks = result.Messages.Skip(1).Select(m => GameOverPlayers.Decode(m).Entries).ToArray();
            Assert.Equal((playerCount + 49) / 50, chunks.Length);
            Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 50));
            var entries = chunks.SelectMany(chunk => chunk).ToArray();
            Assert.Equal(playerCount, entries.Length);
            Assert.Equal(peers.Select(p => "seed-" + p.Conn.Value).Order(), entries.Select(e => e.CharacterName).Order());
            var winEntry = entries.Single(e => e.CharacterName == "seed-31");
            var loseEntry = entries.Single(e => e.CharacterName == "seed-32");
            Assert.Equal((short)0, winEntry.SideId);
            Assert.Equal((short)1, loseEntry.SideId);
            Assert.Equal((short)1, winEntry.Kills);
            Assert.Equal((short)1, loseEntry.Rescues);
            if (!sideOnly)
            {
                Assert.True(winEntry.Score > 0);
                Assert.True(loseEntry.Score > 0);
            }
        }
        Assert.DoesNotContain(results, r => r.Peer == c);
        Assert.Same(otherMission, server.Missions[1]);
        Assert.Equal(MissionStage.Started, otherMission.Stage);
        if (sideOnly)
        {
            Assert.Same(mission, server.Missions[0]);
            Assert.Equal(MissionStage.Started, mission.Stage);
        }
        else
        {
            Assert.NotSame(mission, server.Missions[0]);
            Assert.All(server.Ships.Where(l => l.Ship.Mission == server.Missions[0]), l =>
            {
                Assert.Equal(0, l.PlayerScore.Kills);
                Assert.Equal(0, l.PlayerScore.Rescues);
            });
        }
    }
}
