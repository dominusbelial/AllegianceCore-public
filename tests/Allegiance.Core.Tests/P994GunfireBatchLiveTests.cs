using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P994GunfireBatchLiveTests(ITestOutputHelper output)
{
    private static ServerConfig Config(bool batch, bool parallel = true) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Profiler = new() { BroadcastBudget = true },
        Sim = new() { NativeGotoPlan = true },
        Broadcast = new() { GunfireBatch = batch, ShipStatusChangeGate = true,
            ImmutableSnapshot = true, EncodeOnce = true, ShipUpdateModel = "native", ParallelBuild = parallel, MinUpdateHz = 2 },
        Games = [new() { Name = "P994 seed=994", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static List<(ConnectionId Conn, Ship Ship, Sector Sector)> Seed(Srv server, int peers, int sectors, float spacing)
    {
        var mission = server.TestMission!;
        mission.Random = new Random(994);
        mission.Stage = MissionStage.Started;
        var hull = mission.Core.HullTypes[55];
        var weapon = hull.PreferredPartsTypes.Where(id => id >= 0 && mission.Core.PartTypes.ContainsKey(id))
            .Select(id => mission.Core.PartTypes[id]).First(p => p.EquipmentType == EquipmentType.Weapon);
        var result = new List<(ConnectionId, Ship, Sector)>();
        for (int i = 0; i < peers; i++)
        {
            short side = (short)((i / sectors) % 2);
            var orientation = Orientation.Identity;
            if (side == 1) orientation.Yaw(MathF.PI);
            var ship = new Ship((short)(10000 + i), hull,
                new(50000 + i / (2 * sectors) * spacing, 0, side == 0 ? 300 : -300),
                Vector3.Zero, orientation) { SideId = side };
            var sector = server.TestSectors[i % sectors];
            mission.PlaceShip(ship, sector);
            ship.MountWeapon((DataPartTypeIGC.Weapon)weapon.Type, 0);
            result.Add((new ConnectionId((uint)(700 + i)), ship, sector));
        }
        server.TestSeedPeers(result);
        return result;
    }

    [SkippableFact]
    public void Off_seeded_wire_is_pinned_and_serial_equals_parallel_and_batch_expansion()
    {
        Require.Artwork();
        var serial = Recorded(false, false);
        var parallel = Recorded(false, true);
        var batch = Recorded(true, true);
        Assert.Equal(serial.Digest, parallel.Digest);
        Assert.Equal(serial.FullDigest, parallel.FullDigest);
        Assert.Equal("75E96CC95BE876D9BA791D1F9AAA566AB9EE2CBBA2313C066E7496118B45AF33", serial.Digest);
        output.WriteLine($"[p994-off-pin] sha256={serial.Digest}");
        Assert.Equal(serial.Records.Count, batch.Records.Count);
        Assert.True(serial.Records.Count > 0);
        for (int i = 0; i < serial.Records.Count; i++)
        {
            var a = serial.Records[i]; var b = batch.Records[i];
            Assert.Equal(a.Peer, b.Peer);
            if (a.Message is ProjectileInstance p)
            {
                var q = Assert.IsType<ProjectileInstance>(b.Message);
                Assert.Equal(p.TimeCreate, q.TimeCreate); Assert.Equal(p.LauncherId, q.LauncherId);
                Assert.Equal(p.ProjectileTypeId, q.ProjectileTypeId); Assert.Equal(p.LauncherType, q.LauncherType);
                Assert.Equal(p.Position, q.Position); Assert.Equal(p.Lifespan, q.Lifespan);
                P994GunfireBatchTests.Near(p.Velocity, q.Velocity, GunfireBatch.VelocityError);
            }
            else
            {
                var h = (ProjectileHullHit)a.Message; var q = Assert.IsType<ProjectileHullHit>(b.Message);
                Assert.Equal((h.SourceId, h.TargetId, h.ClusterId, h.AbsorbedByShield, h.Radius),
                    (q.SourceId, q.TargetId, q.ClusterId, q.AbsorbedByShield, q.Radius));
                P994GunfireBatchTests.Near(h.Position, q.Position, GunfireBatch.PositionError);
            }
        }
    }

    [SkippableFact]
    public void Batch_append_allocates_nothing_after_capacity_warmup()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(true));
        var peers = Seed(server, 2, 1, 100);
        Assert.Equal(0, server.TestGunfireAppendAllocatedBytes(peers[0].Conn, 1000));
    }

    [SkippableFact]
    public void Oversize_bursts_split_at_record_boundaries_without_loss()
    {
        Require.Artwork();
        var burst = Recorded(true, true, 2000);
        Assert.Equal(2000 * 2 * 8, burst.Records.Count);
    }

    [SkippableFact]
    public void Probe_identity_and_creation_lifespan_override_survive_the_server_path()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(true));
        var peers = Seed(server, 2, 1, 100);
        var mission = server.TestMission!;
        var source = peers[0].Ship;
        var sector = source.Sector!;
        var probe = new Probe(313, sector, mission.Core.ProbeTypes.Values.First(), source.Position, 0, 0, source);
        sector.Add(probe);
        var messages = new List<IFedMessage>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        {
            if (peer != peers[0].Conn) return;
            foreach (var frame in FedPacket.Unpack(bytes))
                if (FedMessageHeader.Read(frame).Fmid == GunfireBatch.Fmid)
                    messages.AddRange(GunfireBatch.Decode(frame).Records);
        };
        var type = default(DataProjectileTypeIGC) with { ProjectileTypeId = 41, Lifespan = 2 };
        var projectile = new Projectile(1, sector, type, source.Position, new(0, 0, -1000),
            new(0, 0, -1), 0, source, lifespanOverride: .125f) { OriginProbe = probe };
        var host = (ServerHost)mission.Site;
        server.TestProjectRecordedTick(994, true, surge: () =>
        {
            host.ProjectileCreatedEvent(projectile);
            host.ProjectileHitEvent(projectile, source.Position, new(0, 0, 1), 35, source.Id, sector.Id, true);
        });
        server.FlushBroadcastSnapshots();
        var shot = Assert.IsType<ProjectileInstance>(messages[0]);
        Assert.Equal((short)313, shot.LauncherId); Assert.Equal(IgcObjectType.Probe, shot.LauncherType);
        Assert.Equal(.125f, shot.Lifespan); Assert.Equal(994u, shot.TimeCreate);
        var hit = Assert.IsType<ProjectileHullHit>(messages[1]);
        Assert.Equal(source.Id, hit.SourceId); Assert.Equal(source.Id, hit.TargetId);
        Assert.Equal(sector.Id, hit.ClusterId); Assert.True(hit.AbsorbedByShield);
    }

    [SkippableFact]
    public void Sector_reseats_keep_pre_reset_effects_before_the_reset_in_both_send_paths()
    {
        Require.Artwork();
        var off = Recorded(false, true, reset: true);
        var on = Recorded(true, true, reset: true);
        Assert.Equal(off.WorldCounts, on.WorldCounts);
        Assert.All(on.WorldCounts, counts => Assert.Equal((12, 12, 12), counts));
    }

    private static (string Digest, string FullDigest, List<(uint Peer, IFedMessage Message)> Records,
        (int Projectiles, int Hits, int Sounds)[] WorldCounts) Recorded(bool batch, bool parallel, int shots = 24, bool reset = false)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(batch, parallel));
        var peers = Seed(server, 8, 1, 100);
        var host = (ServerHost)server.TestMission!.Site;
        var packets = new List<(uint Peer, byte[] Packet)>();
        var fullPackets = new List<(uint Peer, byte[] Packet)>();
        int encodes = 0;
        server.TestGunfireSharingCapture = count => encodes += count;
        server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
        {
            fullPackets.Add((peer.Value, bytes));
            foreach (var m in FedPacket.Unpack(bytes))
                if (FedMessageHeader.Read(m).Fmid is ProjectileInstance.Fmid or ProjectileHullHit.Fmid or GunfireBatch.Fmid)
                {
                    Assert.Equal(0, key); Assert.True(guaranteed);
                    packets.Add((peer.Value, bytes));
                }
        };
        var type = default(DataProjectileTypeIGC) with { ProjectileTypeId = 41, Lifespan = 2f };
        var random = new Random(994);
        server.TestProjectRecordedTick(994, snapshot: true, surge: () =>
        {
            for (int i = 0; i < shots; i++)
            {
                var shooter = peers[i % 8].Ship;
                var projectile = new Projectile((short)i, shooter.Sector!, type,
                    new(50000 + random.NextSingle() * 100, 0, 300),
                    new(12.3456f, -23.4567f, -1000), new(0, 0, -1), 0, shooter);
                host.ProjectileCreatedEvent(projectile);
                if (batch)
                {
                    var legacyView = ProjectileInstance.Decode(Assert.Single(FedPacket.Unpack(server.TestGunfireAudienceToken)));
                    Assert.Equal(projectile.Position, legacyView.Position);
                    Assert.Equal(shooter.Id, legacyView.LauncherId);
                }
                host.ProjectileHitEvent(projectile, new(50000.123f, 12.345f, -300.456f),
                    new Vector3(1, 2, -3).Normalize(), 35.25f, peers[(i + 1) % 8].Ship.Id, shooter.Sector!.Id, i % 2 == 0);
                if (batch)
                {
                    var legacyView = ProjectileHullHit.Decode(Assert.Single(FedPacket.Unpack(server.TestGunfireAudienceToken)));
                    Assert.Equal(shooter.Id, legacyView.SourceId);
                }
                if (reset && i == shots / 2 - 1)
                    foreach (var (peer, viewer, sector) in peers)
                        server.TestSendGunfireBoundary(peer, new SetCluster { SectorId = sector.Id,
                            Ship = ServerShipUpdate.FromModel(viewer.Id, viewer, 994) }, deferred: peer.Value % 2 == 0);
            }
        });
        server.FlushBroadcastSnapshots();
        Assert.Equal(0, server.BroadcastPublishFailures);
        var sorted = packets.OrderBy(p => p.Peer).ToList();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var records = new List<(uint, IFedMessage)>();
        foreach (var (peer, packet) in sorted)
        {
            digest.AppendData(packet);
            foreach (var bytes in FedPacket.Unpack(packet))
                switch (FedMessageHeader.Read(bytes).Fmid)
                {
                    case ProjectileInstance.Fmid: records.Add((peer, ProjectileInstance.Decode(bytes))); break;
                    case ProjectileHullHit.Fmid: records.Add((peer, ProjectileHullHit.Decode(bytes))); break;
                    case GunfireBatch.Fmid:
                        foreach (var record in GunfireBatch.Decode(bytes).Records) records.Add((peer, record));
                        break;
                }
        }
        Assert.Equal(batch ? (shots == 24 && !reset ? 1 : 2) * peers.Count : shots * 2 * peers.Count, packets.Count);
        if (batch && shots == 24 && !reset) Assert.Equal(1, encodes); // eight identical audiences, one encode
        using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (_, packet) in fullPackets.OrderBy(p => p.Peer)) full.AppendData(packet);
        var worlds = new List<(int, int, int)>();
        foreach (var peer in peers)
        {
            var world = new ClientWorld { NowSeconds = () => 100 };
            foreach (var (_, packet) in fullPackets.Where(p => p.Peer == peer.Conn.Value))
                foreach (var frame in FedPacket.Unpack(packet))
                    switch (FedMessageHeader.Read(frame).Fmid)
                    {
                        case ProjectileInstance.Fmid: world.Apply(ProjectileInstance.Decode(frame)); break;
                        case ProjectileHullHit.Fmid: world.Apply(ProjectileHullHit.Decode(frame)); break;
                        case GunfireBatch.Fmid: world.Apply(GunfireBatch.Decode(frame)); break;
                        case SetCluster.Fmid: world.Apply(SetCluster.Decode(frame)); break;
                    }
            worlds.Add((world.Projectiles.Count, world.HullHits.Count, world.HitSoundEvents.Count));
        }
        return (Convert.ToHexString(digest.GetHashAndReset()), Convert.ToHexString(full.GetHashAndReset()), records, worlds.ToArray());
    }

    [SkippableTheory]
    [InlineData(150f)]
    [InlineData(15f)]
    public void Spread_and_packed_showcase_measure_off_on(float spacing)
    {
        Require.Artwork();
        int peers = int.TryParse(Environment.GetEnvironmentVariable("P994_PEERS"), out var n) ? n : 411;
        int ticks = int.TryParse(Environment.GetEnvironmentVariable("P994_TICKS"), out var t) ? t : 100;
        var off = Measure(false, peers, ticks, spacing);
        var on = Measure(true, peers, ticks, spacing);
        output.WriteLine("[p994-off] " + JsonSerializer.Serialize(off));
        output.WriteLine("[p994-on] " + JsonSerializer.Serialize(on));
        Assert.Equal(off.Shots, on.Shots); Assert.Equal(off.Hits, on.Hits);
        Assert.True(on.Hits > 0); Assert.True(on.GunfireFrames < off.GunfireFrames);
        Assert.True(on.GunfireBytes < off.GunfireBytes);
        Assert.True(on.TotalBytes < off.TotalBytes);
    }

    private sealed record Measurement(bool Batch, int Peers, int Ticks, float Spacing, long Shots, long Hits,
        long GunfireFrames, long GunfireBytes, long TotalBytes, double TickP95Ms, double ShipsUpdateMeanMs,
        long AllocatedPerTick, long ProjectionAllocatedPerTick);

    private static Measurement Measure(bool batch, int peers, int ticks, float spacing)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(batch));
        var population = Seed(server, peers, 3, spacing);
        var mission = server.TestMission!;
        foreach (var (_, ship, _) in population.Take(Math.Min(36, peers))) ship.StateM = ShipControlState.AllWeapons;
        long shots = 0, hits = 0;
        var host = (ServerHost)mission.Site;
        host.ProjectileCreated += _ => shots++;
        host.ProjectileHit += (_, _, _, _, _, _, _) => hits++;
        double now = mission.LastUpdate;
        server.TestUpdateCadenceClock = () => now;
        var telemetry = server.BroadcastBudgetTelemetry!;
        telemetry.SetSecond(1000);
        long projectionAllocated = 0;
        server.TestProjectionAllocationCapture = (_, bytes) => projectionAllocated += bytes;
        var durations = new double[ticks];
        long allocatedStart = 0;
        long shipsElapsed = 0;
        server.TestProjectionTimingCapture = (_, elapsed) => shipsElapsed += elapsed;
        for (uint tick = 0; tick < ticks + 20; tick++)
        {
            if (tick == 20)
            {
                telemetry.Reset(); telemetry.SetSecond(1000);
                shots = hits = projectionAllocated = shipsElapsed = 0;
                allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
            }
            now += 0.05;
            foreach (var (_, ship, _) in population)
            {
                ship.SetFraction(1); ship.Ammo = ship.HullType.MaxAmmo; ship.Energy = ship.EffectiveMaxEnergy();
            }
            long start = Stopwatch.GetTimestamp();
            server.TestProjectRecordedTick(tick, snapshot: true, surge: () => mission.Update(now));
            server.FlushBroadcastSnapshots();
            if (tick >= 20) durations[tick - 20] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedStart;
        Array.Sort(durations);
        long Msgs(int id) => telemetry.OrderedLaneWindowMessages(id, 1000);
        long Bytes(int id) => telemetry.OrderedLaneWindowBytes(id, 1000);
        long total = telemetry.WindowTotal(1000);
        long categories = Enum.GetValues<BroadcastCategory>().Sum(c => telemetry.WindowBytes(c, 1000));
        Assert.Equal(total, categories);
        Assert.Equal(0, server.BroadcastPublishFailures);
        return new(batch, peers, ticks, spacing, shots, hits,
            Msgs(12) + Msgs(213) + Msgs(221), Bytes(12) + Bytes(213) + Bytes(221), total,
            durations[(int)Math.Ceiling(ticks * 0.95) - 1], shipsElapsed * 1000d / Stopwatch.Frequency / ticks,
            allocated / ticks, projectionAllocated / ticks);
    }
}
