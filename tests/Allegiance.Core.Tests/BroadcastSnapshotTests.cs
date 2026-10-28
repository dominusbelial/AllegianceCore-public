using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

public sealed class BroadcastPublisherTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "1", true)]
    [InlineData(true, "false", false)]
    [InlineData(false, "TRUE", true)]
    public void Flag_follows_config_and_environment_wins(bool configured, string? env, bool expected)
        => Assert.Equal(expected, Srv.ResolveSnapshotFlag(configured, env));

    [Fact]
    public void Json_setting_binds_and_default_is_on()
    {
        // 2026-10-27 rig A/B (b42 vs b39): snapshot + encode-once are the default.
        Assert.True(new BroadcastConfig().ImmutableSnapshot);
        Assert.True(new BroadcastConfig().EncodeOnce);
        string path = Path.Combine(Path.GetTempPath(), $"p668-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"broadcast":{"immutableSnapshot":false}}""");
            Assert.False(ServerConfigLoader.Load(path, []).Broadcast!.ImmutableSnapshot);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Default_encode_once_follows_a_disabled_snapshot_but_an_explicit_request_is_rejected()
    {
        Assert.False(Srv.ResolveEncodeOnceFlag(true, null, snapshot: false));
        Assert.True(Srv.ResolveEncodeOnceFlag(true, null, snapshot: true));
        Assert.False(Srv.ResolveEncodeOnceFlag(true, "0", snapshot: true));
        Assert.Throws<InvalidDataException>(() => Srv.ResolveEncodeOnceFlag(false, "1", snapshot: false));
    }

    [Fact]
    public void After_publish_actions_run_after_their_deliveries_even_when_coalesced()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var log = new ConcurrentQueue<string>();
        using var publisher = new BroadcastPublisher((delivery, bytes) => log.Enqueue($"send{bytes[0]}"), 2);
        int calls = 0;
        publisher.BeforeEncode = () => { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        BroadcastSnapshot Tick(int tick, string? after)
        {
            var snapshot = publisher.Rent(tick);
            snapshot.Deliveries.Add(new(new(1), new BroadcastPacket([(byte)tick]), 0, true));
            if (after is not null) snapshot.AfterPublish.Add(() => log.Enqueue(after));
            return snapshot;
        }
        publisher.Submit(Tick(1, null));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));   // tick 1 is publishing
        publisher.Submit(Tick(2, "close2"));                      // pending
        publisher.Submit(Tick(3, "close3"));                      // coalesced into the pending one
        release.Set();
        publisher.Flush();
        Assert.Equal(new[] { "send1", "send2", "send3", "close2", "close3" }, log.ToArray());
    }

    [Fact]
    public void Invalid_environment_is_rejected() => Assert.Throws<InvalidDataException>(
        () => Srv.ResolveSnapshotFlag(false, "tru"));

    [Fact]
    public void Stalled_publisher_coalesces_to_one_pending_tick_preserving_reliable_events_and_own_pose()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var output = new ConcurrentQueue<(int Value, int Key, bool Guaranteed, int Thread)>();
        var errors = new ConcurrentQueue<Exception>();
        using var publisher = new BroadcastPublisher((delivery, bytes) =>
            output.Enqueue((bytes[0], delivery.Key, delivery.Guaranteed, Environment.CurrentManagedThreadId)), 2);
        int producer = Environment.CurrentManagedThreadId;
        publisher.OnError = errors.Enqueue;
        publisher.BeforeEncode = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        BroadcastSnapshot Tick(int tick)
        {
            var snapshot = publisher.Rent(tick);
            snapshot.Deliveries.Add(new(new(1), new BroadcastPacket([(byte)tick]), 0, true));
            snapshot.Deliveries.Add(new(new(1), new BroadcastPacket([(byte)tick]), 2, false));
            if (tick == 1) snapshot.Deliveries.Add(new(new(1), new BroadcastPacket([201]), 12, true));
            return snapshot;
        }
        try
        {
            publisher.Submit(Tick(0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            for (int tick = 1; tick <= 100; tick++) publisher.Submit(Tick(tick));
            Assert.Equal(99, publisher.CoalescedSnapshots);
            Assert.Equal(3, publisher.SnapshotContainersCreated);
        }
        finally { release.Set(); }
        publisher.Flush();
        Assert.Empty(errors);
        Assert.Equal(Enumerable.Range(0, 101), output.Where(x => x.Key == 0).Select(x => x.Value));
        Assert.Equal([100], output.Where(x => x.Key == 2).Select(x => x.Value));
        Assert.Contains(output, x => x.Key == 12 && x.Guaranteed && x.Value == 201);
        Assert.All(output, x => Assert.NotEqual(producer, x.Thread));
        Assert.Equal(100, publisher.DroppedStreamFrames);
        Assert.Equal(2, publisher.PublishedSnapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void P788c_complementary_tick_phases_keep_streams_and_later_ticks_replace_them(bool newerTick)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var values = new ConcurrentQueue<byte>();
        using var publisher = new BroadcastPublisher((delivery, bytes) => values.Enqueue(bytes[0]), 1);
        publisher.BeforeEncode = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        BroadcastSnapshot Phase(int tick, int key, byte value)
        {
            var snapshot = publisher.Rent(tick);
            snapshot.Deliveries.Add(new(new(1), new BroadcastPacket([value]), key, key == 0));
            return snapshot;
        }
        try
        {
            publisher.Submit(Phase(0, 0, 0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            publisher.Submit(Phase(1, 2, 12));
            publisher.Submit(Phase(1, 3, 13));
            if (newerTick)
            {
                publisher.Submit(Phase(2, 2, 22));
                publisher.Submit(Phase(2, 3, 23));
            }
        }
        finally { release.Set(); }
        publisher.Flush();
        Assert.Equal(newerTick ? new byte[] { 0, 22, 23 } : new byte[] { 0, 12, 13 }, values.ToArray());
        Assert.Equal(0, publisher.EncodeFailures);
    }

    [Fact]
    public void Dispose_drains_the_final_snapshot_and_joins_workers()
    {
        int count = 0;
        var publisher = new BroadcastPublisher((_, _) => Interlocked.Increment(ref count), 2);
        var snapshot = publisher.Rent(1);
        snapshot.Deliveries.Add(new(new(1), new BroadcastPacket([42])));
        publisher.Submit(snapshot);
        publisher.Dispose();
        publisher.Dispose();
        Assert.Equal(1, count);
    }
}

[Collection("LiveNetwork")]
public sealed class BroadcastSnapshotTests
{
    private sealed record Input(Vector3 Position, Vector3 Velocity, float Yaw, float Shield, bool Docked);
    private sealed record Frame(uint Tick, uint Peer, int Key, bool Guaranteed, byte[] Bytes);

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Captured_pose_is_detached_and_byte_identical_after_live_ship_mutation()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var ship = UpdateCadenceTests.Seed(server, 1, 1)[0].Ship;
        ship.Position = new(111.25f, -87, 12);
        ship.Velocity = new(-21, 3, 8);
        ship.Orientation.Yaw(0.7f);
        ship.Controls = new(0.1f, -0.2f, 0.3f, 0.8f);
        ship.SetTurnRate(0, .2f);
        ship.SetTurnRate(1, -.15f);

        var expectedSingle = new SingleShipUpdate { Ship = ServerShipUpdate.FromModel(ship.Id, ship, 19, 7, true) }.Encode();
        var expectedHeavy = new HeavyShipsUpdate { HeavyShipEntries = [HeavyShipUpdateEntry.From(19, Vector3.Zero, ship, 18)] }.Encode();
        var single = new SingleShipUpdate { Ship = ServerShipUpdate.CaptureModel(ship.Id, ship, 19, 7, true) };
        var heavy = new HeavyShipsUpdate { HeavyShipEntries = [HeavyShipUpdateEntry.Capture(19, Vector3.Zero, ship, 18)] };
        Assert.Null(typeof(ServerShipUpdate).GetField("_bytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(single.Ship));
        Assert.Null(typeof(HeavyShipUpdateEntry).GetField("_bytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(heavy.HeavyShipEntries[0]));
        BroadcastSnapshotGuard.AssertDetached(single);
        BroadcastSnapshotGuard.AssertDetached(heavy);
        Assert.Throws<InvalidOperationException>(() => BroadcastSnapshotGuard.AssertDetached(ship));
        Assert.Throws<InvalidOperationException>(() => BroadcastSnapshotGuard.AssertDetached(server.TestMission!));
        ship.Position = new(9999, 9999, 9999);
        ship.Velocity = Vector3.Zero;
        ship.Orientation.Yaw(2);

        ship.SetTurnRate(0, 9);
        ship.Controls = ControlData.Neutral;
        Assert.Equal(expectedSingle, Task.Run(single.Encode).GetAwaiter().GetResult());
        Assert.Equal(expectedHeavy, Task.Run(heavy.Encode).GetAwaiter().GetResult());
    }

    [SkippableTheory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 3)]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")]
    public void Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes(bool native, bool parallel, double floor)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(floor, native);
        config.Broadcast!.ImmutableSnapshot = true;
        config.Broadcast.ParallelBuild = parallel;
        config.Broadcast.MaxDegreeOfParallelism = 4;
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, ships: 144, viewers: 144);
        // 144 in one sector exercises P632 density scaling; mixed sides test
        // cross-side identity/status, target promotion, and teammate batches.
        for (int i = 0; i < peers.Count; i++)
        {
            peers[i].Ship.SideId = (short)(i % 2);
            peers[i].Ship.CommandTarget = peers[(i + 1) % peers.Count].Ship;
        }
        server.TestMission!.Stage = MissionStage.Started;
        var sector = server.TestSectors[0];
        var core = server.TestMission.Core;
        var probe = new Probe(25000, sector, core.ProbeTypes.Values.First(), new(10, 20, 30), 0, 0, peers[0].Ship);
        var mine = new Mine(25001, sector, core.MineTypes.Values.First(), new(20, 30, 40), 0, peers[0].Ship);
        var missile = new Missile(25002, sector, core.MissileTypes.Values.First(), new(30, 40, 50),
            new(0, 0, -10), new(0, 0, -1), 0, peers[0].Ship, peers[1].Ship);
        sector.Add(probe); sector.Add(mine); sector.Add(missile);
        var launcher = new DataPartTypeIGC(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "",
            Name: "P668 missile launcher", Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
            Mass: 0, Signature: 0, PartId: 29000, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MissileLauncher(core.MissileTypes.Values.First().ExpendableTypeId));
        peers[0].Ship.MountPart(launcher, 4, 0, 255);
        var npc = new Ship(25003, peers[0].Ship.HullType, new(100, 200, 300), Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true };
        server.TestMission.PlaceShip(npc, sector);
        foreach (var station in sector.StationRuntimes)
            for (short side = 0; side < 2; side++) station.SideVisibility.SetSideVisibility(side, true);
        foreach (var asteroid in sector.Mineable)
            for (short side = 0; side < 2; side++) asteroid.SideVisibility.SetSideVisibility(side, true);
        // The fixture is recorded ONCE and replayed twice in the SAME session.
        var random = new Random(668);
        var tape = Enumerable.Range(0, 105).Select(tick => peers.Select((p, i) => new Input(
            new(i * 13 + tick * (i % 3 + 1), (i % 7) * 100, tick * 2),
            new(i % 3, 0, 2), (float)random.NextDouble(), (tick % 20) / 20f,
            i % 31 == 0 && tick < 12)).ToArray()).ToArray();
        List<Frame> Replay(bool snapshot, bool encodeOnce = false)
        {
            server.TestResetSnapshotReplay();
            server._broadcastEncodeOnceEnabled = encodeOnce;
            server.TestSeedPeers(peers);
            server._broadcastParallelBuild = parallel;
            var frames = new List<Frame>();
            uint tick = 0;
            server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
                frames.Add(new(tick, peer.Value, key, guaranteed, bytes));
            try
            {
                for (; tick < tape.Length; tick++)
                {
                    now = tick / 20d;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        var input = tape[tick][i];
                        var ship = peers[i].Ship;
                        ship.Position = input.Position;
                        ship.Velocity = input.Velocity;
                        ship.SetShieldFraction(input.Shield);
                        for (short side = 0; side < 2; side++)
                            ship.SideVisibility.SetSideVisibility(side, side == ship.SideId || (tick / 10 + i) % 3 != 0);
                        ship.Orientation.Reset();
                        ship.Orientation.Yaw(input.Yaw);
                        ship.StationId = input.Docked ? (short)1 : null;
                    }
                    for (short side = 0; side < 2; side++)
                    {
                        probe.SideVisibility.SetSideVisibility(side, tick % 20 < 10);
                        mine.SideVisibility.SetSideVisibility(side, tick % 20 < 10);
                    }
                    server.TestProjectRecordedTick(tick, snapshot);
                    server.FlushBroadcastSnapshots();
                }
            }
            finally { server.TestPublicationCapture = null; }
            return frames;
        }
        var off = Replay(false);
        long before = server.ShipEncodingCounters.HeavyRecords;
        var on = Replay(true);
        long unsharedHeavyEncodes = server.ShipEncodingCounters.HeavyRecords - before;
        before = server.ShipEncodingCounters.HeavyRecords;
        var shared = Replay(true, encodeOnce: true);
        long sharedHeavyEncodes = server.ShipEncodingCounters.HeavyRecords - before;
        Assert.True(sharedHeavyEncodes <= (peers.Count + 1L) * tape.Length);
        Assert.True(sharedHeavyEncodes < unsharedHeavyEncodes);
        Assert.Equal(off.Count, on.Count);
        foreach (var peer in peers)
        {
            var left = off.Where(f => f.Peer == peer.Conn.Value).ToArray();
            var right = on.Where(f => f.Peer == peer.Conn.Value).ToArray();
            Assert.Equal(left.Select(f => (f.Tick, f.Key, f.Guaranteed)), right.Select(f => (f.Tick, f.Key, f.Guaranteed)));
            Assert.Equal(left.Select(f => f.Bytes), right.Select(f => f.Bytes));
            var once = shared.Where(f => f.Peer == peer.Conn.Value).ToArray();
            Assert.Equal(right.Select(f => (f.Tick, f.Key, f.Guaranteed)), once.Select(f => (f.Tick, f.Key, f.Guaranteed)));
            Assert.Equal(right.Select(f => f.Bytes), once.Select(f => f.Bytes));
        }
        Assert.Contains(on, f => f.Key == 12 && f.Guaranteed);
        Assert.Contains(on, f => f.Key == 2 && !f.Guaranteed);
        Assert.Contains(on, f => f.Key == 0 && f.Guaranteed);
        Assert.Equal(0, server.TestPublisher!.CoalescedSnapshots);
        Assert.Equal(0, server.TestPublisher.EncodeFailures);
        static string Hash(IEnumerable<Frame> frames)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var f in frames.OrderBy(f => f.Peer).ThenBy(f => f.Tick))
            {
                hash.AppendData(BitConverter.GetBytes(f.Tick));
                hash.AppendData(BitConverter.GetBytes(f.Peer));
                hash.AppendData(BitConverter.GetBytes(f.Key));
                hash.AppendData([f.Guaranteed ? (byte)1 : (byte)0]);
                hash.AppendData(f.Bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        var result = new { native, parallel, floor, ticks = tape.Length, peers = peers.Count,
            packets = off.Count, bytes = off.Sum(f => (long)f.Bytes.Length), offHash = Hash(off), onHash = Hash(on), sharedHash = Hash(shared), unsharedHeavyEncodes, sharedHeavyEncodes };
        // P697: pin the source-pose-before-missile and sector-local tracking transcript.
        // P672 roster deltas remain included. The four
        // off/on/encode-once comparisons above still check every byte and lane.
        // NT-1: regenerated at 30c8912c after three intentional native-parity wire
        // changes: P762 (FedSrv.CPP:4093-4104) writes bpTargetShield only when the
        // target source ship has a mounted shield, so a shieldless target reports 255;
        // P773 retires the port-only OwnShipKillBonusUpdate(212); P775 sends
        // PLAYERINFO only at native moments (fsmission.cpp:411,:3958-3968). The
        // native/parallel/encode-once equality assertions are unchanged: each case
        // changes for the same reason and all three transcripts still hash equal.
        // Claude 2026-10-28: regenerated again after P864c (a3b8819f) — native
        // GenerateMission file-map top-up (missionigc.cpp:4048-4051 → common.cpp:2963
        // PopulateCluster for asteroid-less / treasure-less sectors) and station-type
        // replacement by side techs (:4011-4049) change the recorded world. Bisected
        // across the merge chain: f6e9e480/dff1e87c green, a3b8819f red.
        // NT-2: regenerated after P797 (7734a197) — native map discovery. The
        // cross-side DISCOVERY sweep (Server.Broadcast.cs SweepCrossSideDiscoveryExports,
        // the port of FedSrv.CPP:10766-10775 ShowObject + :11055-11062 OBJECT_SPOTTED,
        // fsmission.cpp:4169-4250) now exports each newly-seen static object (aleph,
        // asteroid + building effect, probe, treasure) to the seeing side's peers
        // exactly once, so the fixture's recorded ticks carry those one-shot discovery
        // packets on top of the previous transcript. Intentional native-parity wire
        // change, bisected (fd510d2e green … 7734a197 red, --first-parent). The
        // equality assertions are unchanged: all four variants still agree with each
        // other and across off/on/encode-once.
        string golden = (native, parallel) switch
        {
            (false, false) => "7FA94E792A05FCB3",
            (false, true) => "4E0158A1483C8D22",
            (true, false) => "1E9A19A6A20A83A5",
            _ => "E8D661910C691E42",
        };
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"p669-identity-{native}-{parallel}-{floor}.json"), JsonSerializer.Serialize(result));
        Assert.StartsWith(golden, result.offHash);
        Assert.Equal(result.offHash, result.onHash);
        Assert.Equal(result.onHash, result.sharedHash);
    }
}
