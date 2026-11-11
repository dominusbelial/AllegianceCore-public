using System.Buffers.Binary;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P994GunfireBatchTests
{
    private static ProjectileInstance Shot(Vector3 velocity, Vector3? forward = null) => new()
    {
        TimeCreate = uint.MaxValue, ProjectileTypeId = short.MinValue, LauncherId = short.MaxValue,
        LauncherType = IgcObjectType.Probe, Position = new(149.99998f, -1e20f, float.MaxValue),
        Forward = forward ?? new Vector3(1, -2, -3).Normalize(), Velocity = velocity, Lifespan = 0.12345679f,
    };

    [Fact]
    public void Flag_defaults_off_environment_wins_and_rejects_typos()
    {
        Assert.False(new BroadcastConfig().GunfireBatch);
        Assert.False(Server.Server.ResolveGunfireBatchFlag(false, null));
        Assert.True(Server.Server.ResolveGunfireBatchFlag(false, " true "));
        Assert.False(Server.Server.ResolveGunfireBatchFlag(true, "0"));
        Assert.Throws<InvalidDataException>(() => Server.Server.ResolveGunfireBatchFlag(false, "yes"));
    }

    [Theory]
    [InlineData(0f, 2)]
    [InlineData(127.99f, 0)]
    [InlineData(-128f, 0)]
    [InlineData(128f, 1)]
    [InlineData(-32768f, 1)]
    [InlineData(32767.99f, 1)]
    [InlineData(32768f, 2)]
    [InlineData(float.MaxValue, 2)]
    [InlineData(float.NaN, 2)]
    public void Projectile_fields_and_extremes_round_trip(float speed, int tag)
    {
        var original = Shot(new(speed, -0.001953125f, 0.001953125f));
        var bytes = new GunfireBatch { Tick = uint.MaxValue, Records = [original] }.Encode();
        Assert.Equal(tag, bytes[GunfireBatch.HeaderSize]);
        var decoded = Assert.IsType<ProjectileInstance>(Assert.Single(GunfireBatch.Decode(bytes).Records));
        Assert.Equal(original.TimeCreate, decoded.TimeCreate);
        Assert.Equal(original.ProjectileTypeId, decoded.ProjectileTypeId);
        Assert.Equal(original.LauncherId, decoded.LauncherId);
        Assert.Equal(original.LauncherType, decoded.LauncherType);
        Assert.Equal(original.Position, decoded.Position); // includes exact own-echo boundary
        Assert.Equal(original.Lifespan, decoded.Lifespan); // no type-default substitution
        if (tag == 2) { Assert.Equal(original.Velocity, decoded.Velocity); Assert.Equal(original.Forward, decoded.Forward); }
        else
        {
            Near(original.Velocity, decoded.Velocity, GunfireBatch.VelocityError);
            Assert.True((original.Forward - decoded.Forward).Length() < GunfireBatch.DirectionError);
        }
    }

    [Theory]
    [InlineData(-131072f, 3)]
    [InlineData(131071.98f, 3)]
    [InlineData(131072f, 4)]
    [InlineData(float.PositiveInfinity, 4)]
    public void Hit_ids_flags_radius_and_position_round_trip(float x, int tag)
    {
        foreach (bool shield in new[] { false, true })
        {
            var h = new ProjectileHullHit { SourceId = short.MinValue, TargetId = short.MaxValue, ClusterId = -1,
                Radius = float.MaxValue, Position = new(x, -0.0078125f, 0.0078125f),
                Normal = new Vector3(-1, 2, -3).Normalize(), AbsorbedByShield = shield };
            var bytes = new GunfireBatch { Records = [h] }.Encode();
            Assert.Equal(tag, bytes[GunfireBatch.HeaderSize]);
            var actual = Assert.IsType<ProjectileHullHit>(Assert.Single(GunfireBatch.Decode(bytes).Records));
            Assert.Equal(h.SourceId, actual.SourceId); Assert.Equal(h.TargetId, actual.TargetId);
            Assert.Equal(h.ClusterId, actual.ClusterId); Assert.Equal(h.AbsorbedByShield, actual.AbsorbedByShield);
            Assert.Equal(h.Radius, actual.Radius);
            if (tag == 4) { Assert.Equal(h.Position, actual.Position); Assert.Equal(h.Normal, actual.Normal); }
            else
            {
                Near(h.Position, actual.Position, GunfireBatch.PositionError);
                Assert.True((h.Normal - actual.Normal).Length() < GunfireBatch.DirectionError);
            }
        }
    }

    [Fact]
    public void Direction_bounds_cover_poles_fold_seams_and_seeded_sphere()
    {
        var random = new Random(994);
        var directions = new List<Vector3> { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0),
            new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
        for (int i = 0; i < 10000; i++)
            directions.Add(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1,
                random.NextSingle() * 2 - 1).Normalize());
        foreach (var direction in directions)
        {
            var bytes = new GunfireBatch { Tick = uint.MaxValue, Records = [Shot(Vector3.Zero, direction)] }.Encode();
            var p = (ProjectileInstance)GunfireBatch.Decode(bytes).Records[0];
            Assert.True((direction - p.Forward).Length() < GunfireBatch.DirectionError);
        }
    }

    [Theory]
    [InlineData(16f, 1)]
    [InlineData(16.001f, 2)]
    [InlineData(-1f, 2)]
    [InlineData(float.MaxValue, 2)]
    [InlineData(float.NaN, 2)]
    public void Long_or_unusual_lifespans_keep_exact_velocity_and_all_lifespans_are_exact(float lifespan, byte tag)
    {
        var shot = Shot(new(1000.123f, 2000.234f, -3000.345f));
        shot.Lifespan = lifespan;
        var bytes = new GunfireBatch { Tick = uint.MaxValue, Records = [shot] }.Encode();
        Assert.Equal(tag, bytes[GunfireBatch.HeaderSize]);
        var actual = (ProjectileInstance)GunfireBatch.Decode(bytes).Records[0];
        Assert.Equal(BitConverter.SingleToInt32Bits(lifespan), BitConverter.SingleToInt32Bits(actual.Lifespan));
        if (tag == 2) Assert.Equal(shot.Velocity, actual.Velocity);
    }

    [Theory]
    [InlineData(.0005f, 0)]
    [InlineData(.0011f, 2)]
    [InlineData(1f, 2)]
    [InlineData(63.99f, 2)]
    [InlineData(64f, 0)]
    public void Slow_rounds_preserve_the_renderer_velocity_branch_and_heading(float speed, byte tag)
    {
        var original = Shot(new(speed, 0, 0));
        var bytes = new GunfireBatch { Tick = uint.MaxValue, Records = [original] }.Encode();
        Assert.Equal(tag, bytes[GunfireBatch.HeaderSize]);
        var actual = (ProjectileInstance)GunfireBatch.Decode(bytes).Records[0];
        Assert.Equal(original.Velocity.LengthSquared() > 1e-6f, actual.Velocity.LengthSquared() > 1e-6f);
        if (speed >= .0011f)
            Assert.True((original.Velocity.Normalize() - actual.Velocity.Normalize()).Length() < GunfireBatch.DirectionError);
    }

    [Theory]
    [InlineData(0, 1)] // bad cbmsg
    [InlineData(2, 12)] // wrong fmid
    [InlineData(4, 0)] // bad array length
    [InlineData(6, 15)] // overlapping offset
    [InlineData(12, 0)] // zero count
    [InlineData(14, 2)] // version
    [InlineData(15, 1)] // reserved
    [InlineData(16, 9)] // unknown record
    public void Malformed_batch_is_rejected_before_world_application(int offset, byte value)
    {
        var bytes = new GunfireBatch { Tick = uint.MaxValue, Records = [Shot(Vector3.Zero)] }.Encode();
        bytes[offset] = value;
        Assert.Throws<ArgumentException>(() => GunfireBatch.Decode(bytes));
    }

    [Fact]
    public void Truncation_and_trailing_bytes_are_rejected()
    {
        var bytes = new GunfireBatch { Tick = uint.MaxValue, Records = [Shot(Vector3.Zero)] }.Encode();
        for (int i = 0; i < bytes.Length; i++)
            Assert.Throws<ArgumentException>(() => GunfireBatch.Decode(bytes.AsSpan(0, i)));
        var extra = bytes.Concat(new byte[] { 0 }).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(extra, (ushort)extra.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(6), (ushort)(extra.Length - GunfireBatch.HeaderSize));
        Assert.Throws<ArgumentException>(() => GunfireBatch.Decode(extra));
    }

    [Fact]
    public void Encoder_rejects_empty_oversized_mixed_tick_and_foreign_records()
    {
        Assert.Throws<ArgumentException>(() => new GunfireBatch().Encode());
        Assert.Throws<ArgumentException>(() => new GunfireBatch { Records = new IFedMessage[3000] }.Encode());
        Assert.Throws<ArgumentException>(() => new GunfireBatch { Tick = 1, Records = [Shot(Vector3.Zero)] }.Encode());
        Assert.Throws<ArgumentException>(() => new GunfireBatch { Records = [new Ping()] }.Encode());
    }

    [Fact]
    public void Mixed_records_expand_to_identical_world_and_bot_inputs_in_order()
    {
        var legacy = new ClientWorld();
        var batched = new ClientWorld();
        var records = new List<IFedMessage>();
        var random = new Random(994);
        for (int i = 0; i < 100; i++)
        {
            var p = Shot(new(random.NextSingle() * 1000, -123.4567f, 1234.5678f));
            p.LauncherType = IgcObjectType.Ship;
            p.LauncherId = (short)(i % 7);
            p.Position = new(150 + (i % 3 - 1) * 0.0000152587890625f, 0, 0);
            records.Add(p);
            records.Add(new ProjectileHullHit { SourceId = p.LauncherId, TargetId = (short)(i % 5),
                ClusterId = (short)(i % 3), AbsorbedByShield = i % 2 == 0,
                Radius = 50.5f, Position = new(100.123f, -200.234f, 300.345f), Normal = p.Forward });
        }
        foreach (var record in records) legacy.Apply(record);
        batched.Apply(GunfireBatch.Decode(new GunfireBatch { Tick = uint.MaxValue, Records = records.ToArray() }.Encode()));
        Assert.Equal(legacy.Projectiles.Count, batched.Projectiles.Count);
        for (int i = 0; i < legacy.Projectiles.Count; i++)
        {
            var a = legacy.Projectiles[i]; var b = batched.Projectiles[i];
            Assert.Equal(a.LauncherId, b.LauncherId); Assert.Equal(a.ProjectileTypeId, b.ProjectileTypeId);
            Assert.Equal(a.Position, b.Position); Assert.Equal(a.Lifespan, b.Lifespan);
            Near(a.Velocity, b.Velocity, GunfireBatch.VelocityError);
            Assert.True((a.Forward - b.Forward).Length() < GunfireBatch.DirectionError);
        }
        // These are precisely the fields SampleCombatStats consumes for own hit
        // credit, RecentAttackers and friendly-asset/station-attack alerts.
        Assert.Equal(legacy.HullHits.Select(h => (h.SourceId, h.TargetId, h.ClusterId, h.AbsorbedByShield, h.Radius)),
            batched.HullHits.Select(h => (h.SourceId, h.TargetId, h.ClusterId, h.AbsorbedByShield, h.Radius)));
        Assert.Equal(legacy.HitSoundEvents.Select(h => (h.TargetId, h.ClusterId, h.AbsorbedByShield)),
            batched.HitSoundEvents.Select(h => (h.TargetId, h.ClusterId, h.AbsorbedByShield)));
        for (int i = 0; i < legacy.HullHits.Count; i++)
            Near(legacy.HullHits[i].Position, batched.HullHits[i].Position, GunfireBatch.PositionError);
    }

    [Fact]
    public void Record_build_has_no_steady_path_allocations()
    {
        var buffer = new byte[GunfireBatch.MaxRecordSize];
        var direction = new Vector3(1, -2, 3).Normalize();
        for (int i = 0; i < 1000; i++) Write();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Write();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        void Write()
        {
            GunfireBatch.WriteProjectile(buffer, 1, 2, IgcObjectType.Ship, new(50000, 0, 1), direction, new(10, -20, 1000), 3.125f);
            GunfireBatch.WriteHit(buffer, 2, 3, 1, true, 50, new(50000, 0, 100), direction);
        }
    }

    [Fact]
    public void Own_echo_and_bot_own_shot_counts_are_exact_at_the_radius_boundary()
    {
        foreach (bool countOnly in new[] { false, true })
        {
            var legacy = World(); var batch = World();
            legacy.CountOnlyProjectiles = batch.CountOnlyProjectiles = countOnly;
            var records = new List<IFedMessage>();
            foreach (float x in new[] { MathF.BitDecrement(150), 150, MathF.BitIncrement(150) })
                foreach (short launcher in new short[] { 5, 6 })
                {
                    var shot = Shot(new(12.345f, 67.89f, -1000));
                    shot.LauncherId = launcher; shot.LauncherType = IgcObjectType.Ship; shot.Position = new(x, 0, 0);
                    records.Add(shot);
                }
            foreach (var record in records) legacy.Apply(record);
            batch.Apply(GunfireBatch.Decode(new GunfireBatch { Tick = uint.MaxValue, Records = records.ToArray() }.Encode()));
            Assert.Equal(2, legacy.Diag.DroppedOwn);
            Assert.Equal(legacy.Diag.DroppedOwn, batch.Diag.DroppedOwn);
            Assert.Equal(legacy.Diag.DroppedTeammate, batch.Diag.DroppedTeammate);
            Assert.Equal(legacy.Projectiles.Select(p => (p.LauncherId, p.Position)),
                batch.Projectiles.Select(p => (p.LauncherId, p.Position)));
            Assert.Equal(legacy.TakeOwnProjectileCount(), batch.TakeOwnProjectileCount());
        }

        static ClientWorld World()
        {
            var world = new ClientWorld { NowSeconds = () => 100 };
            var bytes = new byte[ServerShipUpdate.Width];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, 5);
            var reader = new WireReader(bytes);
            world.Apply(new ExportShip { ShipId = 5, SideId = 0, HullId = 15, CharacterName = "own" });
            world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.DecodeBody(ref reader) });
            world.OwnEchoDropPredicate = (_, spawn, anchor, _) => (spawn - anchor).LengthSquared() <= 150 * 150;
            return world;
        }
    }

    internal static void Near(Vector3 a, Vector3 b, float bound)
    {
        Assert.InRange(MathF.Abs(a.X - b.X), 0, bound);
        Assert.InRange(MathF.Abs(a.Y - b.Y), 0, bound);
        Assert.InRange(MathF.Abs(a.Z - b.Z), 0, bound);
    }
}
