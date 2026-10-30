using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P891BoardedPoseTests
{
    [SkippableTheory]
    [InlineData(false, "ranked")]
    [InlineData(true, "ranked")]
    [InlineData(false, "legacy")]
    [InlineData(true, "legacy")]
    [InlineData(false, "tiers")]
    [InlineData(true, "tiers")]
    [Trait("Requires", "Artwork")]
    public void Parent_pose_is_present_every_flight_tick_under_400_candidate_pressure(bool parallel, string projection)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            // Exercise the ranked budget itself; the optional cadence floor must
            // not hide a missing source-ship guarantee.
            Broadcast = new() { MinUpdateHz = 0, InterestTiers = projection == "tiers" },
            Games = [new() { Name = "P891", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        server.TestLegacySurgeProjection = projection == "legacy";
        var mission = server.TestMission!;
        var sector = server.TestSectors[0];
        var hull = mission.Core.HullTypes.Values.First();
        Ship Make(short id) => new(id, hull, new Vector3(id, 3000, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
        var parent = Make(10000);
        var child = Make(10001);
        var viewer = Make(10002);
        mission.PlaceShip(parent, sector);
        mission.PlaceShip(child, sector);
        mission.PlaceShip(viewer, sector);
        child.ParentShip = parent;
        child.TurretId = 2;
        parent.SetFraction(0.4f);
        child.SetFraction(0.8f);
        for (short id = 11000; id < 11398; id++) mission.PlaceShip(Make(id), sector);
        var gunner = new ConnectionId(700);
        var observer = new ConnectionId(701);
        server.TestSeedPeers([(gunner, child, sector), (observer, viewer, sector)]);
        Assert.Equal(400, sector.GetShipUpdateCandidates().Count);
        HeavyShipsUpdate Read(Dictionary<ConnectionId, List<byte[]>> packets, ConnectionId conn) =>
            packets[conn].SelectMany(p => FedPacket.Unpack(p))
                .Where(m => FedMessageHeader.Read(m).Fmid == HeavyShipsUpdate.Fmid)
                .Select(m => HeavyShipsUpdate.Decode(m)).Single();

        // A docked parent is never manufactured into a flight pose.
        parent.StationId = 1;
        child.StationId = parent.StationId;
        sector.Terminate(parent);
        sector.Add(parent);
        var docked = Read(server.TestCollectBroadcast(1, parallel, "shipsUpdate"), gunner);
        Assert.DoesNotContain(docked.HeavyShipEntries, e => e.ShipId == parent.Id);
        parent.StationId = child.StationId = null;
        sector.Terminate(parent);
        sector.Add(parent);

        int remoteCount = 0;
        for (uint tick = 2; tick < 42; tick++)
        {
            // Force the source to the very end of each ranked list, even if
            // earlier peers or targets otherwise happen to promote it.
            child.CommandTarget = tick < 15 ? null : tick < 28 ? parent : viewer;
            parent.Deviation = -10000;
            parent.Position += new Vector3(3, 0, 0);
            parent.Velocity = new Vector3(tick, 0, 0);
            var packets = server.TestCollectBroadcast(tick, parallel, "shipsUpdate");
            var heavy = Read(packets, gunner);
            var pose = Assert.Single(heavy.HeavyShipEntries, e => e.ShipId == parent.Id);
            Assert.Equal(tick, pose.Time(heavy.TimeReference));
            Assert.True((pose.Position(heavy.PositionReference) - parent.Position).Length() < 2);
            Assert.True((pose.Velocity - parent.Velocity).Length() < 2);
            Assert.DoesNotContain(heavy.HeavyShipEntries, e => e.ShipId == child.Id);
            // The unboarded peer still uses the existing ranked quota.
            if (projection != "tiers")
                Assert.DoesNotContain(Read(packets, observer).HeavyShipEntries, e => e.ShipId == parent.Id);
            Assert.Equal(parent.Position, heavy.PositionReference);
            Assert.InRange(heavy.FractionsHull / 240f, 0.39f, 0.41f);
            remoteCount = heavy.HeavyShipEntries.Length;
        }
        Console.WriteLine($"P891 pressure: 400 candidates, 40/40 current parent poses, parallel={parallel}, projection={projection}, heavy count={remoteCount}; extra source record width={HeavyShipUpdateEntry.Width}");
    }
}
