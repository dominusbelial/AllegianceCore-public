using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the P231 projectile tracking:
/// <see cref="ClientWorld"/>'s transient <see cref="ProjectileEntity"/> list fed
/// from the real <see cref="ProjectileInstance"/> (fmid 12) wire message, its
/// expiry pruner, and its velocity motion extrapolation. Messages are built
/// directly and fed through <see cref="ClientWorld.Apply"/>, exactly like the
/// other <c>ClientWorld*Tests</c> — the same shape as
/// <see cref="ClientWorldExportTests"/>. <see cref="ClientWorld.Apply"/> stamps
/// the local monotonic receipt instant, so each test captures it and asserts
/// relative to that instant (deterministic regardless of wall clock).
/// </summary>
public class ClientWorldProjectileTests
{
    private static ProjectileInstance Shot(
        short typeId = 4,
        float lifespan = 2.0f,
        Vector3? pos = null,
        Vector3? vel = null,
        Vector3? fwd = null) => new()
    {
        TimeCreate = 555u,
        ProjectileTypeId = typeId,
        Position = pos ?? Vector3.Zero,
        Velocity = vel ?? new Vector3(0f, 0f, -1000f),
        Forward = fwd ?? new Vector3(0f, 0f, -1f),
        Lifespan = lifespan,
    };

    // ── arrival into the transient list ────────────────────────────────────

    [Fact]
    public void ProjectileInstance_AppendsEntity_WithCapturedFields()
    {
        var world = new ClientWorld();
        world.Apply(Shot(pos: new Vector3(10f, 20f, 30f), vel: new Vector3(1f, 2f, -3f)));

        var p = Assert.Single(world.Projectiles);
        Assert.Equal((short)4, p.ProjectileTypeId);
        Assert.Equal(2.0f, p.Lifespan);
        Assert.Equal(10f, p.Position.X);
        Assert.Equal(20f, p.Position.Y);
        Assert.Equal(30f, p.Position.Z);
        Assert.Equal(1f, p.Velocity.X);
        Assert.Equal(2f, p.Velocity.Y);
        Assert.Equal(-3f, p.Velocity.Z);
    }

    [Fact]
    public void ProjectileInstances_AppendInArrivalOrder_NotKeyedById()
    {
        var world = new ClientWorld();
        world.Apply(Shot(typeId: 4));
        world.Apply(Shot(typeId: 7));

        Assert.Equal(2, world.Projectiles.Count);
        Assert.Equal((short)4, world.Projectiles[0].ProjectileTypeId);
        Assert.Equal((short)7, world.Projectiles[1].ProjectileTypeId);
    }

    [Fact]
    public void ReceiptInstant_IsStampedPerProjectile()
    {
        var world = new ClientWorld();
        world.Apply(Shot());
        world.Apply(Shot());

        long t0 = world.Projectiles[0].ReceivedLocalMs;
        long t1 = world.Projectiles[1].ReceivedLocalMs;
        Assert.True(t0 > 0);
        Assert.True(t1 >= t0);   // monotonic, second received at or after first
    }

    // ── expiry pruning ─────────────────────────────────────────────────────

    [Fact]
    public void Prune_RemovesExpired_KeepsAlive()
    {
        var world = new ClientWorld();
        world.Apply(Shot(lifespan: 1.0f));
        long t0 = world.Projectiles[0].ReceivedLocalMs;

        world.PruneExpiredProjectiles(t0 + 900);   // before the 1s lifetime
        Assert.Single(world.Projectiles);

        world.PruneExpiredProjectiles(t0 + 1000);  // at/after the 1s lifetime
        Assert.Empty(world.Projectiles);
    }

    [Fact]
    public void Prune_KeepsMultipleYoung_RemovesOnlyExpired()
    {
        var world = new ClientWorld();
        world.Apply(Shot(lifespan: 10.0f));   // young
        long young = world.Projectiles[^1].ReceivedLocalMs;
        world.Apply(Shot(lifespan: 0.5f));    // short-lived
        long old = world.Projectiles[^1].ReceivedLocalMs;

        // Middle ground: the short one is gone, the long one remains.
        world.PruneExpiredProjectiles(old + 510);
        Assert.Single(world.Projectiles);
        Assert.Equal((short)4, world.Projectiles[0].ProjectileTypeId);
        Assert.True(world.Projectiles[0].ReceivedLocalMs == young);
    }

    // ── motion extrapolation ───────────────────────────────────────────────

    [Fact]
    public void PositionAt_ExtrapolatesAlongVelocity()
    {
        var world = new ClientWorld();
        world.Apply(Shot(pos: new Vector3(10f, 20f, 30f), vel: new Vector3(0f, 0f, -100f)));
        long t0 = world.Projectiles[0].ReceivedLocalMs;

        var at = world.Projectiles[0].PositionAt(t0 + 2000);   // 2 s later
        Assert.Equal(10f, at.X, precision: 3);
        Assert.Equal(20f, at.Y, precision: 3);
        Assert.Equal(-170f, at.Z, precision: 3);               // 30 + (-100 * 2)
    }

    [Fact]
    public void PositionAt_DoesNotTravelBeforeReceipt()
    {
        var world = new ClientWorld();
        world.Apply(Shot(pos: new Vector3(1f, 2f, 3f), vel: new Vector3(0f, 0f, -50f)));
        long t0 = world.Projectiles[0].ReceivedLocalMs;

        var at = world.Projectiles[0].PositionAt(t0 - 5000);   // 5 s before receipt
        Assert.Equal(1f, at.X, precision: 3);
        Assert.Equal(2f, at.Y, precision: 3);
        Assert.Equal(3f, at.Z, precision: 3);
    }
}
