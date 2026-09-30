using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P369 — the local player's OWN ship must carry its real side on
/// <see cref="ShipEntity.SideId"/>, never the C# default 0 (which is a real,
/// valid side in some missions, so the local hull silently resolves as the
/// wrong civ — e.g. a Ga'Taraan/Bravo pilot whose own ship renders as if it
/// were an Alpha craft).
/// <para>
/// Wire truth: the server's own-ship <c>EXPORT(26)</c> deliberately carries
/// <c>ShipId = NA(-1)</c> (the native "this is mine" dummy) and seats the real
/// side into <see cref="ClientWorld.MySideId"/>; the real own-ship entity is
/// keyed by the real <c>MyShipId</c>, which only <c>SET_CLUSTER(7)</c> assigns.
/// <c>ShipEntity.SideId</c> has exactly ONE other assignment site
/// (the real-id branch of <c>Apply(ExportShip)</c>), which the dummy-id export
/// always skips — so without P369 the own-ship entity's <c>SideId</c> stayed 0
/// forever. P369 stamps it from <c>MySideId</c> in both orderings of
/// dummy-EXPORT vs SET_CLUSTER.
/// </para>
/// </summary>
public class ClientWorldSideIdTests
{
    private static SetCluster SetClusterFor(short myId) => new()
    {
        SectorId = 1,
        Ship = ServerShipUpdate.FromValues(
            myId, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f),
        Cookie = 0x1,
    };

    private const short MyId = 42;

    /// <summary>⭐ The bug's exact order (the usual join burst): the own-ship
    /// dummy EXPORT (ShipId = -1, carries MySideId) arrives, THEN SET_CLUSTER
    /// assigns MyShipId. The entity created by SET_CLUSTER must pick up the
    /// already-known MySideId.</summary>
    [Fact]
    public void OwnShip_gets_side_when_SetCluster_follows_dummy_export()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 3 });   // own side = 3 (Bravo/Ga'Taraan)
        world.Apply(SetClusterFor(MyId));

        Assert.Equal<short?>(3, world.MySideId);
        Assert.Equal(MyId, world.MyShipId);
        Assert.True(world.Ships[MyId].IsMine, "SET_CLUSTER marks the own ship");
        // The whole point of P369: the own entity's SideId must be the REAL side.
        Assert.Equal<short>(3, world.Ships[MyId].SideId);
    }

    /// <summary>⭐ The converse ordering: SET_CLUSTER (MyShipId = 42) first, then
    /// the own-ship dummy EXPORT arrives with MySideId. Once MySideId is known,
    /// the dummy branch stamps the already-existing own entity's SideId.</summary>
    [Fact]
    public void OwnShip_gets_side_when_dummy_export_follows_SetCluster()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId));
        Assert.Equal<short>(0, world.Ships[MyId].SideId);   // correct: side not known yet
        world.Apply(new ExportShip { ShipId = -1, SideId = 3 });

        Assert.Equal<short?>(3, world.MySideId);
        Assert.Equal<short>(3, world.Ships[MyId].SideId);
    }

    /// <summary>A real-id EXPORT of our own ship (the join-refresh path) still
    /// sets the side via the existing real-id branch — P369 must not regress it.
    /// A side of 0 is legitimate here because the wire genuinely says 0 (the
    /// exporter's side), not a default.</summary>
    [Fact]
    public void RealId_export_of_own_ship_still_sets_side()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 3 });
        world.Apply(SetClusterFor(MyId));
        world.Apply(new ExportShip { ShipId = MyId, HullId = 13, SideId = 3, CharacterName = "me" });

        Assert.Equal<short>(3, world.Ships[MyId].SideId);
        Assert.Equal<short>(13, world.Ships[MyId].HullId);
    }

    // ── P438: ClientWorld.Apply(JoinSide) — the JOINT_SIDE(125) handler ──────

    /// <summary>A JOIN_SIDE for a KNOWN teammate updates that ship's
    /// <c>ShipEntity.SideId</c> (the input to the isEnemy classification) —
    /// this is the whole P438 fix.</summary>
    [Fact]
    public void JoinSide_updates_a_known_teammate_side()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });       // I am side 0
        world.Apply(SetClusterFor(MyId));
        const short Mate = 7;
        world.Apply(new ExportShip { ShipId = Mate, HullId = 5, SideId = 1, CharacterName = "mate" });
        Assert.Equal<short>(1, world.Ships[Mate].SideId);              // enemy latch

        // B (a known ship) switches to MY side.
        world.Apply(new JoinSide { ShipId = Mate, SideId = 0 });

        Assert.Equal<short>(0, world.Ships[Mate].SideId);
        // MySideId is untouched by a teammate's JOIN_SIDE.
        Assert.Equal<short?>(0, world.MySideId);
    }

    /// <summary>A JOIN_SIDE naming the LOCAL player updates BOTH MySideId and
    /// the own <c>ShipEntity.SideId</c> — the step-3 requirement.</summary>
    [Fact]
    public void JoinSide_naming_own_ship_updates_MySideId_and_own_entity()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });       // MySideId seeded 0
        world.Apply(SetClusterFor(MyId));

        world.Apply(new JoinSide { ShipId = MyId, SideId = 2 });       // I switch to side 2

        Assert.Equal<short?>(2, world.MySideId);
        Assert.Equal<short>(2, world.Ships[MyId].SideId);
    }

    /// <summary>A JOIN_SIDE for a ship the client has NO entity for yet must
    /// NOT mint a phantom identity-less entity (P373 HasRealIdentity invariant),
    /// and must not disturb an unrelated existing ship.</summary>
    [Fact]
    public void JoinSide_for_unknown_ship_creates_no_phantom()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(SetClusterFor(MyId));

        world.Apply(new JoinSide { ShipId = 99, SideId = 1 });         // never seen ship

        Assert.False(world.Ships.ContainsKey(99), "JOIN_SIDE must not fabricate an unknown ship");
        Assert.Single(world.Ships);                                     // only the own ship
        Assert.Equal<short>(0, world.Ships[MyId].SideId);               // untouched
        Assert.Equal<short?>(0, world.MySideId);                        // untouched
    }

    /// <summary>SIDE_TEAMLOBBY (-2) arriving as a JOIN_SIDE SideId is stored on
    /// the entity without aliasing to a real side: isEnemy compares
    /// SideId == MySideId, and -2 can never equal the real MySideId, so the ship
    /// simply resolves as "not on my side" — it is never mistaken for friendly.
    /// Also confirms HasRealIdentity is not flipped by JOIN_SIDE.</summary>
    [Fact]
    public void JoinSide_with_SIDE_TEAMLOBBY_does_not_alias_to_a_real_side()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(SetClusterFor(MyId));
        const short Mate = 7;
        world.Apply(new ExportShip { ShipId = Mate, HullId = 5, SideId = 0, CharacterName = "mate" });
        Assert.True(world.Ships[Mate].HasRealIdentity);                // identity came from EXPORT

        world.Apply(new JoinSide { ShipId = Mate, SideId = -2 });      // SIDE_TEAMLOBBY

        // -2 != MySideId (0), so isEnemy resolves true (non-friend) — not aliased
        // to a real friendly side. Identity is NOT granted by JOIN_SIDE.
        Assert.Equal<short>(-2, world.Ships[Mate].SideId);
        Assert.True(world.Ships[Mate].HasRealIdentity);                // unchanged by JOIN_SIDE
    }

}
