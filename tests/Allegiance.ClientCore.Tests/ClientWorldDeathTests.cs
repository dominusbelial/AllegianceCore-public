using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// No-socket unit tests for <see cref="ShipEntity.IsDead"/> death tracking
/// (step 1 of the death task), the out-of-order upsert claim (step 2), and
/// the <c>IsMine</c> early-arrival correction claim (step 3). Messages are
/// constructed directly (same construct-then-round-trip style as
/// <see cref="ClientWorldUnitTests"/>) and fed through
/// <see cref="ClientWorld.Apply(IFedMessage)"/>.
/// </summary>
public class ClientWorldDeathTests
{
    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 801, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 618f, HardpointOffset: 640,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship MakeShip(short id, Vector3 position) =>
        new(id, TestHull(), position, Vector3.Zero, Orientation.Identity);

    // ── Step 1: IsDead ───────────────────────────────────────────────────

    /// <summary>A KILL_SHIP(105) marks its victim's entity <c>IsDead=true</c>,
    /// keeps the entity in the table (still queryable), and also records the
    /// kill in the event log — the death is both entity-table state and a
    /// transient event, and neither half drops the other.</summary>
    [Fact]
    public void KillShip_MarksVictimIsDead_AndKeepsEntity()
    {
        const short victim = 707;
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = victim, HullId = 708, SideId = 910, CharacterName = "doomed" });
        Assert.False(world.Ships[victim].IsDead, "a living export must not be pre-marked dead");

        world.Apply(new KillShip
        {
            ShipId = victim,
            TypeCredit = 0,
            IdCredit = 709,
            SideIdKiller = -1,
            BKillCredit = true,
            BDeathCredit = true,
        });

        Assert.True(world.Ships[victim].IsDead, "the victim entity must be flagged dead");
        Assert.Single(world.Ships, e => e.Key == victim);   // still present, not removed
        Assert.Contains(world.Events.Kills, k => k.ShipId == victim);  // event log half intact
    }

    /// <summary>A KILL_SHIP for a ship never seen before still upserts a dead
    /// entity rather than throwing (create-if-absent), and the entity survives
    /// in the table so "did this ship die" stays answerable.</summary>
    [Fact]
    public void KillShip_ForUnknownShip_CreatesThenMarksDeadEntity()
    {
        var world = new ClientWorld();
        const short ghost = 770;
        Assert.False(world.Ships.ContainsKey(ghost));

        world.Apply(new KillShip
        {
            ShipId = ghost, TypeCredit = 0, IdCredit = 771,
            SideIdKiller = -1, BKillCredit = false, BDeathCredit = true,
        });

        Assert.Single(world.Ships);
        Assert.True(world.Ships[ghost].IsDead);
    }

    // ── Step 2: out-of-order arrival ──────────────────────────────────────

    /// <summary>A <c>ShipStatusMessage</c> for an id NOT yet in the table
    /// arrives first (created the entity), then an <c>ExportShip</c> for the
    /// SAME id arrives — the reverse of the usual export-then-status order —
    /// and they must collapse onto ONE entity carrying both the status fields
    /// and the export fields, not crash and not create a second row.</summary>
    [Fact]
    public void StatusBeforeExport_ForSameId_CollapsesOntoOneEntity()
    {
        const short shipId = 887;
        var world = new ClientWorld();

        // Arrival #1 — SHIP_STATUS for a ship we've never seen: upsert creates it.
        world.Apply(new ShipStatusMessage
        {
            ShipId = shipId,
            HullId = 609,
            SectorId = 5,
            StationId = 2,
            ParentId = -1,
            State = ShipStatusMessage.StateFlying,
            Unknown = false,
            Detected = true,
            StateTime = 4242u,
        });
        Assert.Single(world.Ships);                      // created by the status alone
        Assert.Equal(5, world.Ships[shipId].SectorId);

        // Arrival #2 — the delayed EXPORT for the SAME id.
        world.Apply(new ExportShip
        {
            ObjectType = ExportShip.OtShip,
            ShipId = shipId,
            HullId = 610,
            SideId = 933,
            CharacterName = "late_arrival",
        });

        Assert.Single(world.Ships, e => e.Key == shipId);   // one entity, not two
        var e = world.Ships[shipId];

        // Status fields survived on the single entity.
        Assert.Equal(5, e.SectorId);
        Assert.Equal(2, e.StationId);
        Assert.Equal(-1, e.ParentId);
        Assert.Equal(ShipStatusMessage.StateFlying, e.State);
        Assert.Equal(4242u, e.StateTime);

        // Export fields (applied last) also landed on that same entity.
        Assert.Equal(610, e.HullId);
        Assert.Equal(933, e.SideId);
        Assert.Equal("late_arrival", e.CharacterName);
    }

    // ── Step 3: IsMine early-arrival correction ───────────────────────────
    //
    // WHY THIS IS A UNIT TEST, NOT LIVE: the read-only server's join burst
    // sends the joining peer's OWN ship export with ShipId = NA(-1) (Server.cs
    // OwnShipExportId) — the placeholder id the native client's dummy ship
    // uses — which ClientWorld.Apply(ExportShip) skips precisely because it
    // has no real id. So the real server NEVER puts an ExportShip for OUR OWN
    // real ship id on the wire before our SET_CLUSTER: there is no live
    // scenario that produces the "early own-ship export" ordering this test
    // exercises. It therefore proves the claim directly via ClientWorld.Apply
    // in the reversed order, which is the honest way to lock the invariant.
    /// <summary>An <c>ExportShip</c> carrying OUR OWN (real) ship id arriving
    /// before our <c>SET_CLUSTER</c> leaves <c>IsMine=false</c> transiently
    /// (we simply don't know yet); once <c>SET_CLUSTER</c> assigns the ship the
    /// SAME entity flips to <c>IsMine=true</c>. No second, separate entity is
    /// ever created, and no persistent wrong <c>IsMine</c> occurs.</summary>
    [Fact]
    public void OwnIdExportBeforeSetCluster_FlipsSameEntityToIsMineOnAssignment()
    {
        const short myId = 912;
        var world = new ClientWorld();

        // Early own-ship export (the hypothetical "would-be" ordering).
        world.Apply(new ExportShip
        {
            ObjectType = ExportShip.OtShip,
            ShipId = myId,
            HullId = 913,
            SideId = 914,
            CharacterName = "me",
        });
        Assert.True(world.Ships.ContainsKey(myId), "the entity must exist from the early export");
        Assert.False(world.Ships[myId].IsMine, "not yet known to be ours — IsMine stays false");
        Assert.Null(world.MyShipId);                       // no assignment yet
        var before = world.Ships[myId];                    // remember the instance identity

        // SET_CLUSTER(7) lands and assigns our ship.
        world.Apply(new SetCluster
        {
            SectorId = 918,
            Ship = ServerShipUpdate.FromModel(myId, MakeShip(myId, Vector3.Zero), 919u),
            Cookie = 0x4,
        });

        Assert.Same(before, world.Ships[myId]);            // SAME entity, not a replacement
        Assert.Equal(myId, world.MyShipId);
        Assert.True(world.Ships[myId].IsMine, "once the server assigns our id, the same entity is mine");
        Assert.Single(world.Ships, e => e.Key == myId);    // one entity throughout
        Assert.Equal((short)914, world.Ships[myId].SideId); // export field retained post-SetCluster
        Assert.Equal((short)913, world.Ships[myId].HullId); // export hull retained post-SetCluster
    }
}
