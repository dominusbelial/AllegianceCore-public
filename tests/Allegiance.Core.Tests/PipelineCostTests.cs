using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.Core.Collision;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class PipelineCostTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Identity_packet_cache_invalidates_loadout_side_name_and_reused_id()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(InterestTierTests.Config());
        var peers = UpdateCadenceTests.Seed(server, 2, 2);
        var subject = peers[1].Ship;
        server.BeginBroadcastSnapshot();
        try
        {
            var first = server.CrossSideExportPacket(subject, 0);
            byte[] original = first.Encode();
            Assert.Same(first, server.CrossSideExportPacket(subject, 0));
            var part = server.TestMission!.Core.PartTypes.Values.First();
            Assert.NotNull(subject.MountPart(part, 1, 0, 255));
            var loadout = server.CrossSideExportPacket(subject, 0);
            Assert.NotSame(first, loadout);
            Assert.Single(ExportShip.Decode(FedPacket.Unpack(loadout.Encode())[0]).Parts);
            Assert.Equal(original, first.Encode()); // detached cached data remains immutable
            subject.SideId = 1;
            var side = server.CrossSideExportPacket(subject, 0);
            Assert.NotSame(loadout, side);
            Assert.Equal(1, ExportShip.Decode(FedPacket.Unpack(side.Encode())[0]).SideId);
            peers[1] = (new(997), subject, peers[1].Sector);
            server.TestSeedPeers(peers);
            var name = server.CrossSideExportPacket(subject, 0);
            Assert.NotSame(side, name);
            Assert.Equal("seed-997", ExportShip.Decode(FedPacket.Unpack(name.Encode())[0]).CharacterName);
            var replacement = new Ship(subject.Id, subject.HullType, subject.Position, Vector3.Zero, Orientation.Identity)
                { SideId = subject.SideId };
            Assert.NotSame(name, server.CrossSideExportPacket(replacement, 0));
        }
        finally { server.CompleteBroadcastSnapshot(); server.FlushBroadcastSnapshots(); }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Span_pose_writers_match_canonical_codecs_for_rotations_and_endurance()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(InterestTierTests.Config());
        var ship = UpdateCadenceTests.Seed(server, 1, 1)[0].Ship;
        var random = new Random(679);
        var single = new byte[ServerShipUpdate.Width];
        var heavy = new byte[HeavyShipUpdateEntry.Width];
        for (int n = 0; n < 200; n++)
        {
            ship.Orientation.Yaw((float)random.NextDouble());
            ship.Orientation.Roll((float)random.NextDouble());
            ship.Position = new(n * 17, -n * 23, n * .5f);
            ship.Velocity = new(n - 100, n % 7, 12);
            ship.Controls = new(.1f, -.5f, .8f, n / 200f);
            ship.SetShieldFraction(n / 200f);
            ServerShipUpdate.WriteModel(single, ship.Id, ship, (uint)n, 7, true);
            var expected = new SingleShipUpdate { Ship = ServerShipUpdate.FromModel(ship.Id, ship, (uint)n, 7, true) }.Encode();
            Assert.Equal(expected.AsSpan(8, ServerShipUpdate.Width).ToArray(), single);
            HeavyShipUpdateEntry.WriteModel(heavy, 13, new(90, -20, 70), ship, (uint)n);
            var entry = HeavyShipUpdateEntry.From(13, new(90, -20, 70), ship, (uint)n);
            var body = new byte[HeavyShipUpdateEntry.Width];
            var writer = new WireWriter(body);
            entry.EncodeBody(ref writer);
            Assert.Equal(body, heavy);
        }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Spatial_membership_certificates_match_exact_classification_through_changes()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(InterestTierTests.Config());
        var peers = UpdateCadenceTests.Seed(server, 2, 2);
        var viewer = peers[0].Ship;
        var subject = peers[1].Ship;
        var cache = new Srv.InterestMembership();
        int[] evaluations = new int[4];
        foreach (float origin in new[] { -10000f, -501, 0, 499, 10000 })
        foreach (float distance in new[] { 1f, 1499, 1500, 1501, 2400, 5999, 6000, 6001, 9000 })
        foreach (bool allied in new[] { false, true })
        {
            viewer.Position = new(origin, 0, 0);
            subject.Position = new(origin + distance, 0, 0);
            subject.SideId = allied ? viewer.SideId : (short)1;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                Assert.Equal(server.ClassifyInterest(viewer, subject),
                    server.ClassifyInterestCached(Srv.CaptureInterestShip(viewer), subject, ref cache));
                evaluations[cache.LastEvaluation]++;
            }
            viewer.CommandTarget = subject;
            Assert.Equal(0, server.ClassifyInterestCached(Srv.CaptureInterestShip(viewer), subject, ref cache));
            viewer.CommandTarget = null;
        }
        // Out-of-grid finite coordinates must not alias into a nearby cell.
        subject.SideId = 1;
        foreach (var (v, s) in new[] { (1e20f, 2e20f), (-1e20f, 1e20f),
            (float.MaxValue, -float.MaxValue), (0f, float.PositiveInfinity), (float.NaN, 0f) })
        {
            viewer.Position = new(v, 0, 0);
            subject.Position = new(s, 0, 0);
            Assert.Equal(server.ClassifyInterest(viewer, subject),
                server.ClassifyInterestCached(Srv.CaptureInterestShip(viewer), subject, ref cache));
        }
        Assert.True(evaluations[2] > 0);
        Assert.True(evaluations[0] > 0);
        Assert.True(evaluations[1] > 0);
        BroadcastSnapshotGuard.AssertDetached(Srv.CaptureInterestShip(viewer));
    }

    [Fact]
    public void Stalled_publication_preserves_pooled_ordered_leases_and_latest_stream()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var values = new ConcurrentQueue<(uint Tick, int Key)>();
        using var publisher = new BroadcastPublisher((delivery, bytes) =>
            values.Enqueue((Ping.Decode(FedPacket.Unpack(bytes)[0]).TimeServer, delivery.Key)), 4);
        publisher.BeforeEncode = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        static PooledPosePacket Packet(uint tick)
        {
            var packet = PooledPosePacket.Rent(Ping.FixedSize + FedPacket.CrcWidth);
            new Ping { TimeServer = tick }.Encode().CopyTo(packet.Buffer);
            packet.Seal(Ping.FixedSize);
            return packet;
        }
        try
        {
            var first = publisher.Rent(0);
            first.Deliveries.Add(new(new(1), Packet(0), 0));
            publisher.Submit(first);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            for (uint tick = 1; tick <= 100; tick++)
            {
                var snapshot = publisher.Rent(tick);
                snapshot.Deliveries.Add(new(new(1), Packet(tick), 0));
                snapshot.Deliveries.Add(new(new(1), Packet(tick), 2, false));
                publisher.Submit(snapshot);
            }
        }
        finally { release.Set(); }
        publisher.Flush();
        Assert.Equal(Enumerable.Range(0, 101).Select(i => (uint)i), values.Where(v => v.Key == 0).Select(v => v.Tick));
        Assert.Equal(new uint[] { 100 }, values.Where(v => v.Key == 2).Select(v => v.Tick));
        Assert.Equal(0, publisher.EncodeFailures);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Steady_512_peer_pipeline_allocation_budget()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(InterestTierTests.Config());
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, 512, 512);
        for (int i = 0; i < peers.Count; i++)
        {
            peers[i].Ship.SideId = (short)(i % 2);
            peers[i].Ship.Position = new((i % 32) * 250, 10000 + (i / 32) * 250, 0);
        }
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        void Tick(uint tick)
        {
            now = tick / 20d;
            foreach (var peer in peers) peer.Ship.Position += new Vector3(1, 0, 0);
            server.TestProjectRecordedTick(tick, true);
            server.FlushBroadcastSnapshots();
        }
        for (uint tick = 0; tick < 120; tick++) Tick(tick);
        var stages = new Dictionary<string, long>();
        server.TestProjectionAllocationCapture = (stage, bytes) => stages[stage] = stages.GetValueOrDefault(stage) + bytes;
        // Populate diagnostic keys outside the allocation interval.
        Tick(120);
        foreach (var key in stages.Keys) stages[key] = 0;
        long producer = GC.GetAllocatedBytesForCurrentThread();
        long publisher = server.TestPublisher!.PublishAllocatedBytes;
        const int ticks = 100;
        for (uint tick = 121; tick < 121 + ticks; tick++) Tick(tick);
        producer = (GC.GetAllocatedBytesForCurrentThread() - producer) / ticks;
        publisher = (server.TestPublisher.PublishAllocatedBytes - publisher) / ticks;
        File.WriteAllText("/tmp/p679-allocation.json", JsonSerializer.Serialize(new { producer, publisher,
            stages = stages.ToDictionary(p => p.Key, p => p.Value / ticks) }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.InRange(producer, 0, 104_000);
        Assert.InRange(publisher, 0, 16_000);
        Assert.InRange(producer + publisher, 0, 104_857);
        Assert.Equal(0, server.TestPublisher.EncodeFailures);
    }
}
