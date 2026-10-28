using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.MapGen;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P863bSurgeBudgetTests(ITestOutputHelper output)
{
    private sealed record Frame(uint Tick, int Key, bool Guaranteed, byte[] Bytes);
    private sealed record Result(Dictionary<ConnectionId, List<Frame>> Frames, float[] Deviations);

    // This deliberately compresses forty kills into a ten-second capture. Four
    // synchronized bursts exercise the surge tail, rather than averaging it away
    // over the rig's thirty-second kill-rate interval. No synthetic CPU load.
    private Result Replay(bool legacy, int population, bool capture, int run)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(2, native: true);
        config.Games[0].Map = "m2xspiderw.igc";
        config.Broadcast!.ParallelBuild = true;
        config.Broadcast.MaxDegreeOfParallelism = 15;
        server.Boot(config);
        server.TestLegacySurgeProjection = legacy;
        server.TestSeedSurgeRandom(863);
        var peers = UpdateCadenceTests.Seed(server, population, population);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        // Boot tops up empty file-map sectors with ambient random rocks/loot.
        // Rebuild those recorded inputs with seed=863 in BOTH replay arms.
        foreach (var s in mission.Sectors)
        {
            foreach (var rock in s.Mineable.ToArray()) s.RemoveAsteroid(rock);
            foreach (var treasure in s.Models.OfType<Treasure>().ToArray()) s.Remove(treasure);
        }
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(config.ArtPath, config.Games[0].Map)));
        foreach (var data in map.Asteroids)
            mission.PlaceAsteroid(new Asteroid(data), mission.Sectors.First(s => s.Id == data.ClusterId));
        MissionMapGenerator.PopulateFileMapAsteroids(mission, new MapGenParams
        {
            Seed = 863, Teams = 2, PlayerSectorAsteroids = 20, NeutralSectorAsteroids = 20,
            PlayerSectorMineableAsteroids = 2, NeutralSectorMineableAsteroids = 4,
            PlayerSectorSpecialAsteroids = 1, NeutralSectorSpecialAsteroids = 1,
        });
        var sector = peers[0].Sector;
        var hull = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter)
            && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        for (int i = 0; i < peers.Count; i++)
        {
            var ship = peers[i].Ship;
            ship.ChangeHull(hull);
            ship.SideId = (short)(i % 2);
            ship.Position = new Vector3(1500 * MathF.Cos(i * 2.4f), 1500 * MathF.Sin(i * 2.4f), i % 11 * 50);
            ship.SetFraction(1);
            ship.CommandTarget = peers[(i + 1) % peers.Count].Ship;
            ship.SideVisibility.SetSideVisibility(0, true);
            ship.SideVisibility.SetSideVisibility(1, true);
        }
        foreach (var s in mission.Sectors)
            foreach (var rock in s.Mineable)
                for (short side = 0; side < 2; side++) rock.SideVisibility.SetSideVisibility(side, true);
        var frames = peers.ToDictionary(p => p.Conn, _ => new List<Frame>());
        var deviations = new List<float>();
        uint tick = 0;
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        if (capture) server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
            frames[peer].Add(new(tick, key, guaranteed, bytes));
        var missiles = new List<Missile>();
        var missileType = mission.Core.MissileTypes.Values.First();
        var walls = new List<double>();
        var surgeWalls = new List<double>();
        for (; tick < 30; tick++)
        {
            now = tick / 20d;
            server.TestProjectRecordedTick(tick, true, discovery: true);
            server.FlushBroadcastSnapshots();
        }
        TickProfiler.Configure(true, 3600, null);
        long allocated = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        try
        {
            for (; tick < 230; tick++)
            {
                now = tick / 20d;
                int phase = (int)(tick - 30) % 50;
                int group = (int)(tick - 30) / 50 * 10;
                long start = Stopwatch.GetTimestamp();
                server.TestProjectRecordedTick(tick, true, measure: true, discovery: true, surge: () =>
                {
                    if (phase == 0)
                    {
                        using (TickProfiler.Enter("surge:deaths"))
                            for (int k = 0; k < 10; k++)
                            {
                                var victim = peers[(group + k) % peers.Count].Ship;
                                var killer = peers[(group + k + 1) % peers.Count].Ship;
                                // Eight real ejections and two true deaths/restarts.
                                mission.Site.KillShipEvent(now, victim, killer, k < 8 ? 1 : -1,
                                    victim.Position, victim.Position + new Vector3(1, 0, 0));
                            }
                        using (TickProfiler.Enter("surge:missileFire"))
                            for (short k = 0; k < 16; k++)
                            {
                                var source = peers[(50 + k) % peers.Count].Ship;
                                var missile = new Missile((short)(24000 + group * 16 + k), sector,
                                    missileType, source.Position, new Vector3(1, 0, 0) * 100, new Vector3(1, 0, 0),
                                    now, source, peers[(group + 51 + k) % peers.Count].Ship);
                                sector.Add(missile); missiles.Add(missile);
                                mission.Site.MissileCreatedEvent(missile);
                            }
                    }
                    if (phase == 5)
                    {
                        using (TickProfiler.Enter("surge:missileDestroy"))
                            foreach (var missile in missiles)
                            { sector.Remove(missile); mission.Site.MissileDestroyedEvent(missile); }
                        missiles.Clear();
                        using (TickProfiler.Enter("surge:relaunch"))
                        {
                            for (int k = 0; k < 10; k++)
                            {
                                var ship = peers[(group + k) % peers.Count].Ship;
                                ship.Sector?.Remove(ship);
                                mission.PlaceShip(ship, sector);
                                ship.StationId = null;
                                ship.ChangeHull(hull); ship.SetFraction(1);
                            }
                            // Recorded world input: restore the live-sector records
                            // after the two real restart moves. No socket/AI timing.
                            server.TestSeedPeers(peers);
                        }
                    }
                });
                server.FlushBroadcastSnapshots();
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                walls.Add(elapsed);
                if (phase is 0 or 5) surgeWalls.Add(elapsed);
                if (capture) deviations.AddRange(peers.Select(p => p.Ship.Deviation));
            }
            long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            output.WriteLine($"P863b run={run} arm={(legacy ? "before" : "after")} allocMB={bytes / 1048576d:F3} gen0={GC.CollectionCount(0) - g0} gen1={GC.CollectionCount(1) - g1} gen2={GC.CollectionCount(2) - g2} wallMean={walls.Average():F4} wallP95={Percentile(walls, .95):F4} wallP99={Percentile(walls, .99):F4} surgeMean={surgeWalls.Average():F4} surgeMax={surgeWalls.Max():F4}");
            foreach (var (bucket, size) in TickProfiler.SnapshotAlloc().OrderBy(kv => kv.Key))
                output.WriteLine($"P863b run={run} arm={(legacy ? "before" : "after")} allocationStep={bucket} MB={size / 1048576d:F4}");
            foreach (var (bucket, v) in TickProfiler.Snapshot().OrderBy(kv => kv.Key))
                output.WriteLine($"P863b run={run} arm={(legacy ? "before" : "after")} step={bucket} mean={v.MeanMs:F4} p95={v.P95Ms:F4} max={v.MaxMs:F4} n={v.Count}");
        }
        finally { TickProfiler.Shutdown(); server.TestPublicationCapture = null; }
        Assert.Equal(0, server.TestPublisher!.EncodeFailures);
        Assert.Equal(population, sector.Models.OfType<Ship>().Count());
        return new(frames, deviations.ToArray());
    }

    private static double Percentile(List<double> values, double p) => values.Order().ElementAt((int)Math.Ceiling(values.Count * p) - 1);

    [SkippableTheory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(80, 2)]
    [Trait("Requires", "Artwork")]
    public void Ranked_packets_remain_detached_while_publisher_is_blocked(int population, double floor)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config(floor, native: true));
        var peers = UpdateCadenceTests.Seed(server, population, population);
        server.TestMission!.Stage = MissionStage.Started;
        server.TestUpdateCadenceClock = () => 1;
        foreach (var peer in peers)
        {
            peer.Ship.Orientation.Yaw(.7f); peer.Ship.SetFraction(.6f);
            peer.Ship.SetShieldFraction(.3f); peer.Ship.SetTurnRate(0, .2f);
            peer.Ship.Controls = new(.1f, -.2f, .3f, .8f);
            peer.Ship.CommandTarget = peers[(int)(peer.Conn.Value % (uint)peers.Count)].Ship;
        }
        var expected = new List<(ConnectionId Peer, int Key, bool Guaranteed, byte[] Bytes)>();
        var actual = new List<(ConnectionId Peer, int Key, bool Guaranteed, byte[] Bytes)>();
        server.TestLegacySurgeProjection = true;
        server.TestPublicationCapture = (peer, bytes, key, guaranteed) => expected.Add((peer, key, guaranteed, bytes));
        server.TestProjectRecordedTick(1, true); server.FlushBroadcastSnapshots();
        server.TestResetSnapshotReplay(); server.TestSeedPeers(peers);
        server.TestLegacySurgeProjection = false;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        server.TestPublisher!.BeforeEncode = () => { entered.Set(); release.Wait(); };
        server.TestPublicationCapture = (peer, bytes, key, guaranteed) => actual.Add((peer, key, guaranteed, bytes));
        try
        {
            server.TestProjectRecordedTick(1, true);
            Assert.True(entered.Wait(5000));
            foreach (var peer in peers)
            {
                peer.Ship.Position = new(9999, -9999, 1234); peer.Ship.Velocity = new(10, 20, 30);
                peer.Ship.Orientation.Yaw(1); peer.Ship.Controls = default;
                peer.Ship.SetFraction(.1f); peer.Ship.SetShieldFraction(.9f);
                peer.Ship.SetTurnRate(0, 3);
            }
        }
        finally { release.Set(); server.FlushBroadcastSnapshots(); server.TestPublisher.BeforeEncode = null; }
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal((expected[i].Peer, expected[i].Key, expected[i].Guaranteed),
                (actual[i].Peer, actual[i].Key, actual[i].Guaranteed));
            Assert.Equal(expected[i].Bytes, actual[i].Bytes);
        }
        Assert.Equal(0, server.TestPublisher.EncodeFailures);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")]
    public void Surge_preserves_every_peer_packet_lane_order_and_deviation()
    {
        Require.Artwork();
        var before = Replay(true, 400, true, 0);
        var after = Replay(false, 400, true, 0);
        Assert.Equal(before.Deviations, after.Deviations);
        long count = 0, bytes = 0;
        using var transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = stackalloc byte[21];
        var types = new HashSet<ushort>();
        foreach (var (peer, expected) in before.Frames)
        {
            var actual = after.Frames[peer];
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal((expected[i].Tick, expected[i].Key, expected[i].Guaranteed),
                    (actual[i].Tick, actual[i].Key, actual[i].Guaranteed));
                Assert.True(expected[i].Bytes.AsSpan().SequenceEqual(actual[i].Bytes),
                    $"peer={peer} tick={expected[i].Tick} index={i} fmid={FedMessageHeader.Read(FedPacket.Unpack(expected[i].Bytes)[0]).Fmid} expected={Convert.ToHexString(expected[i].Bytes)} actual={Convert.ToHexString(actual[i].Bytes)}");
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, peer.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(prefix.Slice(4), expected[i].Tick);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(8), expected[i].Key);
                prefix[12] = expected[i].Guaranteed ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(13), expected[i].Bytes.Length);
                transcript.AppendData(prefix); transcript.AppendData(expected[i].Bytes);
                foreach (var message in FedPacket.Unpack(expected[i].Bytes)) types.Add(FedMessageHeader.Read(message).Fmid);
                count++; bytes += expected[i].Bytes.Length;
            }
        }
        Assert.Contains(Eject.Fmid, types); Assert.Contains(KillShip.Fmid, types);
        Assert.Contains(ShipDelete.Fmid, types); Assert.Contains(FireMissile.Fmid, types);
        Assert.Contains(MissileDestroyed.Fmid, types); Assert.Contains(ExplosionEvent.Fmid, types);
        Assert.Contains(EnterLifepod.Fmid, types);
        output.WriteLine($"P863b byte proof: {count} packets, {bytes} bytes, {before.Deviations.Length} deviations; 400 peers, 40 kills, 64 missiles, 40 relaunches; SHA256={Convert.ToHexString(transcript.GetHashAndReset())}.");
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")]
    public void Surge_profiles_three_alternating_runs_per_arm()
    {
        Require.Artwork();
        for (int run = 1; run <= 3; run++)
            foreach (bool legacy in run % 2 == 1 ? new[] { true, false } : new[] { false, true })
                Replay(legacy, 400, false, run);
    }
}
