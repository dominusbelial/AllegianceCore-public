using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P292 — pure-domain tests (no sockets, no server) for the client-side chaff
/// tracking: <see cref="ClientWorld"/>'s transient <see cref="ChaffEntity"/> list
/// fed from the real <see cref="CreateChaff"/> (fmid 109) deploy broadcast, its
/// local-receipt-anchored coasting motion, and its lifespan pruner (resolved from
/// the static core's chaff-type table, since the wire carries no lifespan).
/// Messages are built directly and fed through <see cref="ClientWorld.Apply"/>,
/// exactly like the other <c>ClientWorld*Tests</c> (mirroring
/// <see cref="ClientWorldProjectileTests"/> — chaff is transient/no-id like
/// projectiles, not id-keyed like probes/mines).
/// </summary>
public class ClientWorldChaffTests
{
    private static CreateChaff Chaff(short etid = 21, Vector3? pos = null, Vector3? vel = null) => new()
    {
        P0 = pos ?? new Vector3(10f, 20f, 30f),
        V0 = vel ?? new Vector3(0f, 0f, -40f),
        Time0 = 555u,
        Etid = etid,
    };

    private static Dictionary<short, DataChaffTypeIGC> Types(params (short Id, float Lifespan, float Radius)[] entries)
    {
        var d = new Dictionary<short, DataChaffTypeIGC>();
        foreach (var (id, life, rad) in entries)
        {
            DataChaffTypeIGC rec = default;
            d[id] = rec with { ExpendableTypeId = id, Lifespan = life, Radius = rad, TextureName = "fxmine" };
        }
        return d;
    }

    // ── arrival into the transient list ────────────────────────────────────

    [Fact]
    public void CreateChaff_AppendsEntity_WithCapturedFields()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(etid: 21, pos: new Vector3(10f, 20f, 30f), vel: new Vector3(1f, 2f, -3f)));

        var c = Assert.Single(world.Chaff);
        Assert.Equal((short)21, c.Etid);
        Assert.Equal(10f, c.P0.X);
        Assert.Equal(20f, c.P0.Y);
        Assert.Equal(30f, c.P0.Z);
        Assert.Equal(1f, c.V0.X);
        Assert.Equal(2f, c.V0.Y);
        Assert.Equal(-3f, c.V0.Z);
    }

    [Fact]
    public void CreateChaff_AppendsInArrivalOrder_NotKeyedById()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(etid: 21));
        world.Apply(Chaff(etid: 50));

        Assert.Equal(2, world.Chaff.Count);
        Assert.Equal((short)21, world.Chaff[0].Etid);
        Assert.Equal((short)50, world.Chaff[1].Etid);
    }

    [Fact]
    public void ReceiptInstant_IsStampedPerChaff()
    {
        var world = new ClientWorld();
        world.Apply(Chaff());
        world.Apply(Chaff());

        long t0 = world.Chaff[0].ReceivedLocalMs;
        long t1 = world.Chaff[1].ReceivedLocalMs;
        Assert.True(t0 > 0);
        Assert.True(t1 >= t0);   // monotonic, second received at or after first
    }

    // ── expiry pruning (lifespan from the type table) ──────────────────────

    [Fact]
    public void Prune_RemovesExpired_KeepsAlive()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(etid: 21));                      // lifespan 2s in the table
        long t0 = world.Chaff[0].ReceivedLocalMs;
        var types = Types((21, Lifespan: 2f, Radius: 5f));

        world.PruneExpiredChaff(t0 + 1999, types);         // just before the 2s lifetime
        Assert.Single(world.Chaff);

        world.PruneExpiredChaff(t0 + 2000, types);         // at/after the 2s lifetime
        Assert.Empty(world.Chaff);
    }

    [Fact]
    public void Prune_KeepsMultipleYoung_RemovesOnlyExpired()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(etid: 21));                      // lifespan 10s in the table
        long young = world.Chaff[^1].ReceivedLocalMs;
        world.Apply(Chaff(etid: 50));                      // lifespan 0.5s in the table
        long old = world.Chaff[^1].ReceivedLocalMs;
        var types = Types((21, Lifespan: 10f, Radius: 5f), (50, Lifespan: 0.5f, Radius: 5f));

        world.PruneExpiredChaff(old + 510, types);         // the short one is gone, the long one remains
        Assert.Single(world.Chaff);
        Assert.Equal((short)21, world.Chaff[0].Etid);
        Assert.Equal(young, world.Chaff[0].ReceivedLocalMs);
    }

    [Fact]
    public void Prune_UsesDefaultLifespan_WhenTypeTableAbsentOrUnknown()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(etid: 21));
        long t0 = world.Chaff[0].ReceivedLocalMs;

        // No table at all → the documented DefaultLifespan (5s) governs expiry.
        world.PruneExpiredChaff(t0 + (long)(ChaffEntity.DefaultLifespan * 1000.0) + 1, null);
        Assert.Empty(world.Chaff);
    }

    // ── motion extrapolation ───────────────────────────────────────────────

    [Fact]
    public void PositionAt_ExtrapolatesAlongVelocity()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(pos: new Vector3(10f, 20f, 30f), vel: new Vector3(0f, 0f, -40f)));
        long t0 = world.Chaff[0].ReceivedLocalMs;

        var at = world.Chaff[0].PositionAt(t0 + 2000);     // 2 s later
        Assert.Equal(10f, at.X, precision: 3);
        Assert.Equal(20f, at.Y, precision: 3);
        Assert.Equal(-50f, at.Z, precision: 3);            // 30 + (-40 * 2)
    }

    [Fact]
    public void PositionAt_DoesNotTravelBeforeReceipt()
    {
        var world = new ClientWorld();
        world.Apply(Chaff(pos: new Vector3(1f, 2f, 3f), vel: new Vector3(0f, 0f, -40f)));
        long t0 = world.Chaff[0].ReceivedLocalMs;

        var at = world.Chaff[0].PositionAt(t0 - 5000);     // 5 s before receipt
        Assert.Equal(1f, at.X, precision: 3);
        Assert.Equal(2f, at.Y, precision: 3);
        Assert.Equal(3f, at.Z, precision: 3);
    }

    // ── dispatch-switch routing (the P264 double-wiring lesson) ────────────

    [Fact]
    public void CreateChaff_ArrivingViaTheDispatchSwitch_StillPopulatesTheTable()
    {
        // The wire path feeds the DECODED message into Apply(IFedMessage), whose
        // switch must route CreateChaff to the per-type handler (the P264
        // lesson: a case-less per-type Apply is dead code). Boxing to IFedMessage
        // exercises exactly that dispatch, not the specific overload.
        var world = new ClientWorld();
        world.Apply((IFedMessage)Chaff(etid: 27, pos: new Vector3(4f, 5f, 6f)));

        var c = Assert.Single(world.Chaff);
        Assert.Equal((short)27, c.Etid);
        Assert.Equal(4f, c.P0.X);
    }
}
