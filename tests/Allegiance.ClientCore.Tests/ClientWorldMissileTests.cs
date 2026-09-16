using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the P234 missile tracking:
/// <see cref="ClientWorld"/>'s id-keyed <see cref="MissileEntity"/> table fed
/// from the three real missile wire messages — creation
/// <see cref="FireMissile"/> (70), the per-tick ongoing-position
/// <see cref="MissileUpdate"/> (205), and destruction <see cref="MissileDestroyed"/>
/// (83) — plus the stale pruner. Messages are built directly and fed through
/// <see cref="ClientWorld.Apply"/>, exactly like the other
/// <c>ClientWorld*Tests</c>. <see cref="ClientWorld.Apply"/> stamps the local
/// monotonic receipt instant, so each test captures it and asserts relative to
/// that instant (deterministic regardless of wall clock).
/// </summary>
public class ClientWorldMissileTests
{
    private static FireMissile Creation(short id = 901, short type = 34, short cluster = 1050,
        Vector3? pos = null, Vector3? fwd = null) => new()
    {
        Lock = 1f, TimeFired = 880u, MissileTypeId = type, LauncherId = 7, ClusterId = cluster,
        TargetType = 0, TargetId = -1, BDud = false,
        Entries =
        [
            new MissileLaunchData(pos ?? new Vector3(5f, 0f, -5f), fwd ?? new Vector3(0f, 0f, -1f),
                                  new Vector3(0f, 0f, -25f), id),
        ],
    };

    private static MissileUpdate Update(short id = 902, short type = 34, short cluster = 1050,
        Vector3? pos = null, Vector3? fwd = null) => new()
    {
        Records =
        [
            new MissileStateRecord(id, type, cluster, pos ?? new Vector3(50f, 0f, -50f), fwd ?? new Vector3(0f, 0f, -1f)),
        ],
    };

    // ── creation (FIRE_MISSILE 70) ────────────────────────────────────────

    [Fact]
    public void FireMissile_HydratesMissileEntity_WithTypeClusterAndSpawnPose()
    {
        var world = new ClientWorld();
        world.Apply(Creation(id: 904, type: 34, cluster: 5100, pos: new Vector3(1f, 2f, 3f), fwd: new Vector3(0f, 0f, -1f)));

        var m = Assert.Single(world.Missiles);
        Assert.Equal((short)904, m.Key);
        Assert.Equal((short)34, m.Value.TypeId);
        Assert.Equal((short)5100, m.Value.ClusterId);
        Assert.Equal(1f, m.Value.Position.X);
        Assert.Equal(2f, m.Value.Position.Y);
        Assert.Equal(3f, m.Value.Position.Z);
        Assert.Equal(0f, m.Value.Forward.X);
        Assert.Equal(-1f, m.Value.Forward.Z, 3);
    }

    [Fact]
    public void FireMissile_ZeroForward_FallsBackToNegativeZ()
    {
        var world = new ClientWorld();
        world.Apply(Creation(id: 905, fwd: Vector3.Zero));

        var m = Assert.Single(world.Missiles).Value;
        Assert.Equal(0f, m.Forward.X);
        Assert.Equal(0f, m.Forward.Y);
        Assert.Equal(-1f, m.Forward.Z, 3);
    }

    [Fact]
    public void FireMissile_EmptySalvo_AddsNothing()
    {
        var world = new ClientWorld();
        var fm = Creation(id: 906);
        fm = new FireMissile
        {
            Lock = 0, TimeFired = 881u, MissileTypeId = 34, LauncherId = 7, ClusterId = 5099,
            TargetType = 0, TargetId = -1, BDud = false, Entries = [],
        };
        world.Apply(fm);
        Assert.Empty(world.Missiles);
    }

    // ── ongoing position (MISSILE_UPDATE 205) ─────────────────────────────

    [Fact]
    public void MissileUpdate_UpsertsExistingEntity_RefreshingPositionAndHeading()
    {
        var world = new ClientWorld();
        world.Apply(Creation(id: 910, pos: new Vector3(5f, 0f, -5f)));
        Assert.Equal(5f, world.Missiles[(short)910].Position.X);

        // A later per-tick update moves it (real homing flight).
        world.Apply(Update(id: 910, pos: new Vector3(12f, 3f, -18f), fwd: new Vector3(0.2f, 0.1f, -0.97f)));

        Assert.Single(world.Missiles);                      // same id, not a new entry
        Assert.Equal(12f, world.Missiles[(short)910].Position.X);
        Assert.Equal(3f, world.Missiles[(short)910].Position.Y);
        Assert.Equal(-18f, world.Missiles[(short)910].Position.Z);
        // Forward is normalised on intake (the raw wire value 0.2,0.1,-0.97 is
        // not unit-length) and its Z is negative — still pointing aft along the
        // new heading.
        var fwd = world.Missiles[(short)910].Forward;
        Assert.Equal(1f, fwd.Length(), 3);
        Assert.True(fwd.Z < 0f);
    }

    [Fact]
    public void MissileUpdate_HydratesUnseenId_WithoutPriorCreation()
    {
        // A peer that missed the one-shot FIRE_MISSILE can still render the
        // missile from the per-tick stream (upsert on update).
        var world = new ClientWorld();
        world.Apply(Update(id: 911, type: 44, cluster: 5088, pos: new Vector3(9f, 9f, 9f)));

        var m = Assert.Single(world.Missiles).Value;
        Assert.Equal((short)44, m.TypeId);
        Assert.Equal((short)5088, m.ClusterId);
        Assert.Equal(9f, m.Position.Z);
    }

    // ── destruction (MISSILE_DESTROYED 83) ────────────────────────────────

    [Fact]
    public void MissileDestroyed_RemovesEntityById()
    {
        var world = new ClientWorld();
        world.Apply(Creation(id: 912));
        world.Apply(Update(id: 912));
        world.Apply(new MissileDestroyed { Position = new Vector3(0f, 0f, -200f), ClusterId = 5123, MissileId = 912 });

        Assert.Empty(world.Missiles);
    }

    // ── stale pruner ──────────────────────────────────────────────────────

    [Fact]
    public void PruneExpiredMissiles_RemovesOnlyEntitiesNotRefreshedWithinHorizon()
    {
        var world = new ClientWorld();
        // Stamp 801 first, then sleep so 802 is stamped measurably LATER (both
        // use Environment.TickCount64, so same-millisecond applies collide).
        world.Apply(Creation(id: 801));
        Thread.Sleep(60);
        world.Apply(Update(id: 802));

        long s1 = world.Missiles[(short)801].LastUpdatedLocalMs;
        long s2 = world.Missiles[(short)802].LastUpdatedLocalMs;
        Assert.True(s2 > s1, "sleep must separate the two receipt stamps");

        // Prune at a cut-off strictly between the two horizons: 801 (stamped s1)
        // is past its horizon and removed; 802 (stamped s2 > s1) is not.
        world.PruneExpiredMissiles(s1 + ClientWorld.MissileStaleHorizonMs + 1);
        Assert.DoesNotContain((short)801, world.Missiles.Keys);
        Assert.Contains((short)802, world.Missiles.Keys);
    }
}
