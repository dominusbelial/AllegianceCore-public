using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the P364 projectile-impact
/// spark: the <see cref="ProjectileHullHit"/>(213) wire codec and the world's
/// transient <see cref="HullHitEntity"/> buffer fed from it and drained via
/// <see cref="ClientWorld.TakeNewHullHits"/>. Messages are built directly and fed
/// through <see cref="ClientWorld.Apply"/> exactly like the other
/// ClientWorld*Tests; <see cref="ClientWorld.Apply"/> stamps the local monotonic
/// receipt instant.
/// </summary>
public class ClientWorldHullHitTests
{
    // ── wire codec ──────────────────────────────────────────────────────────

    [Fact]
    public void ProjectileHullHit_EncodesAndDecodes_RoundTripsAllFields()
    {
        var msg = new ProjectileHullHit
        {
            TargetId = 501,
            ClusterId = 1052,
            Radius = 540f,
            AbsorbedByShield = true,                 // P463 — the shield-hit discriminator
            Position = new Vector3(1f, 2f, 3f),
            Normal = new Vector3(0f, 1f, 0f),
        };

        var decoded = ProjectileHullHit.Decode(msg.Encode());

        Assert.Equal(ProjectileHullHit.Fmid, decoded.GetFmid());
        Assert.Equal((ushort)213, ProjectileHullHit.Fmid);
        Assert.Equal((short)501, decoded.TargetId);
        Assert.Equal((short)1052, decoded.ClusterId);
        Assert.Equal(540f, decoded.Radius);
        Assert.True(decoded.AbsorbedByShield);
        Assert.Equal(1f, decoded.Position.X);
        Assert.Equal(2f, decoded.Position.Y);
        Assert.Equal(3f, decoded.Position.Z);
        Assert.Equal(0f, decoded.Normal.X);
        Assert.Equal(1f, decoded.Normal.Y);
        Assert.Equal(0f, decoded.Normal.Z);
    }

    [Fact]
    public void ProjectileHullHit_AbsorbedFlagClearsAndSetsOnDecode()
    {
        Assert.False(ProjectileHullHit.Decode(new ProjectileHullHit { AbsorbedByShield = false }.Encode()).AbsorbedByShield);
        Assert.True(ProjectileHullHit.Decode(new ProjectileHullHit { AbsorbedByShield = true }.Encode()).AbsorbedByShield);
    }

    [Fact]
    public void ProjectileHullHit_IsFixedFortyBytes_HeaderMatches()
    {
        var msg = new ProjectileHullHit { TargetId = 1, ClusterId = 2 };
        byte[] bytes = msg.Encode();
        Assert.Equal(ProjectileHullHit.FixedSize, bytes.Length);
        Assert.Equal(40, bytes.Length);
        Assert.Equal((ushort)40, FedMessageHeader.Read(bytes).CbMsg);
        Assert.Equal((ushort)213, FedMessageHeader.Read(bytes).Fmid);
    }

    [Fact]
    public void ProjectileHullHit_Decode_RejectsWrongFmid()
    {
        var msg = new MineDestroyed { ClusterId = 1, MineId = 2 };   // fmid 84
        Assert.Throws<ArgumentException>(() => ProjectileHullHit.Decode(msg.Encode()));
    }

    // ── world ingestion ─────────────────────────────────────────────────────

    [Fact]
    public void ProjectileHullHit_AppendsEntity_WithCapturedFields()
    {
        var world = new ClientWorld();
        world.Apply(Hit(target: 507, cluster: 7,
            radius: 526f, pos: new Vector3(100f, 200f, 300f), normal: new Vector3(0f, 1f, 0f), absorbed: true));

        var e = Assert.Single(world.HullHits);
        Assert.Equal((short)507, e.TargetId);
        Assert.Equal((short)7, e.ClusterId);
        Assert.Equal(526f, e.Radius);
        Assert.True(e.AbsorbedByShield);               // P463 — carried onto the spark entity
        Assert.Equal(100f, e.Position.X);
        Assert.Equal(200f, e.Position.Y);
        Assert.Equal(300f, e.Position.Z);
        Assert.Equal(1f, e.Normal.Y);
        Assert.NotEqual(0, e.ReceivedLocalMs);   // stamped on receipt
    }

    [Fact]
    public void ProjectileHullHit_BuffersHitSoundEvent_DistinctFromSparkStream()
    {
        var world = new ClientWorld();
        world.Apply(Hit(target: 507, cluster: 7,
            radius: 526f, pos: new Vector3(1f, 2f, 3f), normal: new Vector3(0f, 1f, 0f), absorbed: true));

        // The same hit feeds the SOUND stream (P463)…
        var sound = Assert.Single(world.HitSoundEvents);
        Assert.Equal((short)507, sound.TargetId);
        Assert.Equal((short)7, sound.ClusterId);
        Assert.True(sound.AbsorbedByShield);
        Assert.Equal(1f, sound.Position.X);

        // …and the two streams drain independently without stealing each other.
        var drained = world.TakeHitSoundEvents();
        Assert.Single(drained);
        Assert.Empty(world.HitSoundEvents);
        Assert.Single(world.HullHits);   // the renderer's spark stream is untouched
    }

    [Fact]
    public void HullHits_AppendInArrivalOrder()
    {
        var world = new ClientWorld();
        world.Apply(Hit(target: 1, cluster: 1, radius: 10f, pos: Vector3.Zero, normal: new Vector3(1f, 0f, 0f)));
        world.Apply(Hit(target: 2, cluster: 2, radius: 20f, pos: Vector3.Zero, normal: new Vector3(1f, 0f, 0f)));

        Assert.Equal(2, world.HullHits.Count);
        Assert.Equal((short)1, world.HullHits[0].TargetId);
        Assert.Equal((short)2, world.HullHits[1].TargetId);
    }

    [Fact]
    public void TakeNewHullHits_DrainsAllExactlyOnce()
    {
        var world = new ClientWorld();
        world.Apply(Hit(target: 1, cluster: 1, radius: 10f, pos: Vector3.Zero, normal: new Vector3(1f, 0f, 0f)));
        world.Apply(Hit(target: 2, cluster: 2, radius: 20f, pos: Vector3.Zero, normal: new Vector3(1f, 0f, 0f)));

        var drained = world.TakeNewHullHits();
        Assert.Equal(2, drained.Count);
        Assert.Empty(world.HullHits);          // drained: the world is now empty
        Assert.Empty(world.TakeNewHullHits()); // nothing left to drain
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static ProjectileHullHit Hit(short target, short cluster, float radius,
        Vector3 pos, Vector3 normal, bool absorbed = false) => new()
    {
        TargetId = target,
        ClusterId = cluster,
        Radius = radius,
        AbsorbedByShield = absorbed,
        Position = pos,
        Normal = normal,
    };
}
