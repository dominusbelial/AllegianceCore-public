using System.Reflection;
using System.Security.Cryptography;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P1089TeammateScratchTests
{
    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(true, true, "336557327C7A267A78D51381A5B87411E396B501B4EB490738EE40D71119F7B5")]
    [InlineData(true, false, "4A31E8E95488A8530AE1A7EA0FF2561147EFA8A53D2D74F6371DF913ABCF8876")]
    [InlineData(false, true, "001D46BD34BC3DD3AACEBAE180BCA4CA9004F1FA080C30E75700BF225EEEC95C")]
    [InlineData(false, false, "954D22296E52C14360AEB3C1E86BF5C0E79C2D9C0FDD7C42F260AD50BE9D78FF")]
    public void Teammate_transcript_matches_serial_and_base(bool native, bool gate, string baseDigest)
    {
        Require.Artwork();
        var parallel = Replay(native, gate, parallel: true);
        Assert.Equal(parallel, Replay(native, gate, parallel: false));
        Assert.Equal(baseDigest, Convert.ToHexString(SHA256.HashData(parallel)));
    }

    // Inspect reachable storage directly. Process heap totals also count the
    // shared latches, publisher, static core, and unrelated test allocations.
    private static object?[] Slots(Srv server) => (object?[])typeof(Srv)
        .GetField("_teammateResults", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;

    private static void AssertCleanSlots(Srv server)
    {
        foreach (var slot in Slots(server).OfType<object>())
        {
            Assert.Null(slot.GetType().GetField("NewLiveShip")!.GetValue(slot));
            Assert.Null(slot.GetType().GetField("NextTeammateRefreshTick")!.GetValue(slot));
            foreach (var field in slot.GetType().GetFields())
            {
                if (field.GetValue(slot) is not System.Collections.IEnumerable collection) continue;
                Assert.Empty(collection);
                int capacity = (int)field.FieldType.GetProperty("Capacity")!.GetValue(collection)!;
                Assert.InRange(capacity, 0, 64);
            }
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Mass_leave_releases_burst_buffers_and_retired_slots()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config(native: false));
        var peers = UpdateCadenceTests.Seed(server, 80, 80);
        server.TestMission!.Stage = MissionStage.Started;
        server.TestSweepShipDetection();
        server.TestCollectBroadcast(0, true, "shipStates");
        AssertCleanSlots(server);
        var peak = Slots(server);
        Assert.Equal(peers.Count, peak.Length);
        Assert.Equal(peers.Count, peak.Distinct().Count());
        var first = peak[0];
        server.TestCollectBroadcast(1, true, "shipStates");
        Assert.Same(peak, Slots(server));
        Assert.Same(first, Slots(server)[0]);

        server.TestSeedPeers(peers.Take(2).ToArray());
        server.TestCollectBroadcast(2, true, "shipStates");
        Assert.Equal(2, Slots(server).Length);
        Assert.Same(first, Slots(server)[0]);
        AssertCleanSlots(server);
        server.TestSeedPeers([]);
        server.TestCollectBroadcast(3, true, "shipStates");
        Assert.Empty(Slots(server));
    }

    private static byte[] Replay(bool native, bool gate, bool parallel)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(native: native);
        config.Broadcast!.ShipStatusChangeGate = gate;
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, 8, 8);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var subject = peers[1].Ship;
        peers[6].Ship.SideId = 1;
        var npc = new Ship(16000, subject.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true };
        mission.PlaceShip(npc, peers[0].Sector);
        var parts = mission.Core.PartTypes.Values.OrderBy(p => p.PartId).Take(2).ToArray();
        Assert.NotNull(subject.MountPart(parts[0], 1, 0, 255));
        var host = (ServerHost)typeof(Srv).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!;
        var dispatch = typeof(Srv).GetMethod("HandleTransportEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<ITransportEvent>>(server);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        bool leaveQueued = false;

        void Tick(uint tick)
        {
            writer.Write(tick);
            server.TestSweepShipDetection();
            server.TestPublicationCapture = (conn, packet, key, guaranteed) =>
            {
                writer.Write(conn.Value);
                writer.Write(key);
                writer.Write(guaranteed);
                writer.Write(packet.Length);
                writer.Write(packet);
                if (tick == 103 && !leaveQueued)
                {
                    // Transport arrival during ordered delivery. As in the live
                    // server, the sim thread consumes it on the next ingress drain.
                    leaveQueued = true;
                    ((ITransportSite)host).OnPeerLeft(peers[7].Conn);
                }
            };
            if (tick == 0 && parallel)
            {
                // The broken live record faults inside the worker, after its
                // own position packet has been appended. Snapshot uses the ship.
                var broken = peers.ToArray();
                broken[0].Sector = null!;
                server.TestSeedPeers(broken);
                long before = bytes.Length;
                try
                {
                    var failure = Assert.Throws<AggregateException>(() => server.TestCollectBroadcast(tick, true, "shipStates"));
                    Assert.All(failure.Flatten().InnerExceptions, e => Assert.IsType<NullReferenceException>(e));
                    Assert.Equal(before, bytes.Length);
                    AssertCleanSlots(server);
                }
                finally { server.TestSeedPeers(peers); }
            }
            try { server.TestCollectBroadcast(tick, parallel, "shipStates"); }
            finally { server.TestPublicationCapture = null; }
        }

        Tick(0);
        Tick(1);
        Tick(100); // already-exported viewers whose staggered refresh is due
        subject.ClearLoadout();
        Assert.NotNull(subject.MountPart(parts[1], 1, 0, 255));
        Tick(101);
        subject.SideId = 1;
        npc.StationId = 1;
        Tick(102);
        peers[0].Ship.Position += new Vector3(50, 0, 0);
        Tick(103);
        Assert.True(leaveQueued);
        Assert.Equal(1, host.Drain(dispatch));
        Assert.DoesNotContain(server.TestSnapshotPeers(), p => p.Conn == peers[7].Conn);
        Tick(104);
        // Seat a new connection where the departed viewer's scratch was used.
        var replacement = new Ship(16001, subject.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
        mission.PlaceShip(replacement, peers[0].Sector);
        peers[7] = (new ConnectionId(800), replacement, peers[0].Sector);
        server.TestSeedPeers(peers);
        Tick(105);
        Tick(205);
        return bytes.ToArray();
    }
}
