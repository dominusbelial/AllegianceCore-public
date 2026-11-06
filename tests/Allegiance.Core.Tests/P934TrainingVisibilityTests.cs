using System.Buffers.Binary;
using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P934TrainingVisibilityTests
{
    private sealed class Site : IIgcSite { }

    private static Mission World(bool training = true, bool predictablePlayer = false)
    {
        var body = new byte[DataConstantsIGC.Size + DataCivilizationIGC.Size];
        for (int i = 0; i < DataCivilizationIGC.GaCount; i++)
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(DataConstantsIGC.Size + 160 + 4 * i), 1);
        var core = StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize, Version = 1,
            Body = body, BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, DataConstantsIGC.Size, 0),
                new(IgcObjectType.Civilization, DataCivilizationIGC.Size, DataConstantsIGC.Size)] });
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0, Active = true };
        var m = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new(0, "Player", 0), new(1, "Other A", 0), new(2, "Other B", 0)], new Site());
        m.IsTraining = training;
        var player = Place(m, 10, 0, Vector3.Zero, predictablePlayer);
        m.TrainingPlayerShipId = player.Id;
        return m;
    }

    private static Ship Place(Mission m, short id, short side, Vector3 position, bool predictable = false)
    {
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        { HitPoints = 100, Length = 10, Mass = 1, ScannerRange = 100, Signature = 1 };
        var ship = new Ship(id, hull, position, Vector3.Zero, Orientation.Identity) { SideId = side };
        ship.SideVisibility.Predictable = predictable;
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    private static void Sweep(Mission m) => typeof(Sector)
        .GetMethod("ResolveCrossSideVisibility", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(m.Sectors[0], null);

    [Fact]
    public void One_enemy_scanner_supplies_every_other_side_and_ship_contact_decays()
    {
        using var m = World(); var player = m.GetShip(10)!;
        var scanner = Place(m, 11, 1, new(0, 50, 0));
        Place(m, 12, 2, new(50000, 0, 0));
        Sweep(m);
        Assert.True(player.SeenBySide(1));
        Assert.True(player.SeenBySide(2));
        Assert.True(player.GetCurrentEye(2));
        Assert.True(player.SeenBySide(99)); // arbitrary non-player read, not a union on read
        Assert.True(ShipDetection.IsDetectedByEnemy(player));
        scanner.Position = new(50000, 0, 0); Sweep(m);
        Assert.True(player.SeenBySide(0));
        Assert.False(player.SeenBySide(1)); Assert.False(player.GetCurrentEye(2));
        Assert.False(ShipDetection.IsDetectedByEnemy(player));
    }

    [Fact]
    public void A_non_player_owned_model_is_trivially_eyed_by_all_other_sides()
    {
        using var m = World();
        var target = Place(m, 11, 1, new(50000, 0, 0)); Sweep(m);
        Assert.False(target.SeenBySide(0));
        Assert.True(target.SeenBySide(2)); Assert.True(target.GetCurrentEye(99));
    }

    [Fact]
    public void Explicit_other_side_set_and_clear_write_one_shared_latch()
    {
        using var m = World(); var player = m.GetShip(10)!;
        player.SideVisibility.SetSideVisibility(2, true);
        Assert.True(player.SeenBySide(1)); // bound before the first sweep
        Sweep(m);
        player.SideVisibility.SetSideVisibility(2, true);
        Assert.True(player.SeenBySide(1)); Assert.True(player.GetCurrentEye(99));
        player.SideVisibility.SetSideVisibility(99, false);
        Assert.False(player.SeenBySide(2)); Assert.False(player.GetCurrentEye(1));
        Assert.True(player.SeenBySide(0));
    }

    [Fact]
    public void Predictable_initial_visibility_retains_knowledge_but_not_eye_and_explicit_clear_wins()
    {
        using var m = World(predictablePlayer: true); var player = m.GetShip(10)!;
        // The same state holder serves stations, rocks, probes and warps.
        Sweep(m);
        Assert.True(player.SeenBySide(2)); // SetCluster reveals predictable models without a scan
        Assert.False(player.GetCurrentEye(2));
        var scanner = Place(m, 11, 1, new(0, 50, 0)); Sweep(m);
        Assert.True(player.GetCurrentEye(2));
        scanner.Position = new(50000, 0, 0); Sweep(m);
        Assert.True(player.SeenBySide(99)); Assert.False(player.GetCurrentEye(99));
        player.SideVisibility.SetSideVisibility(2, false);
        Assert.False(player.SeenBySide(1)); Assert.False(player.GetCurrentEye(1));
        Sweep(m); Assert.False(player.SeenBySide(2)); // no implicit reinitialization
    }

    [Fact]
    public void Last_enemy_spotter_survives_loss_and_is_reused_even_after_a_side_change()
    {
        using var m = World(); var player = m.GetShip(10)!;
        var scanner = Place(m, 11, 1, new(0, 50, 0)); Sweep(m);
        scanner.Position = new(50000, 0, 0); Sweep(m);
        Assert.False(player.GetCurrentEye(1));
        // Native keeps the pointer on loss and does not check its side on reuse.
        scanner.SideId = 0; scanner.Position = new(0, 50, 0); Sweep(m);
        Assert.True(player.GetCurrentEye(2));
        m.Sectors[0].Remove(scanner); Sweep(m);
        Assert.False(player.GetCurrentEye(2)); // a detached scanner cannot see the cluster
    }

    [Fact]
    public void Reusing_a_retired_spotter_id_does_not_reuse_its_pointer()
    {
        using var m = World(); var player = m.GetShip(10)!;
        var scanner = Place(m, 11, 1, new(0, 50, 0)); Sweep(m);
        m.Sectors[0].Terminate(scanner);
        Place(m, 11, 0, new(0, 50, 0)); Sweep(m);
        Assert.False(player.GetCurrentEye(2));
    }

    [Fact]
    public void A_real_station_initializes_both_slots_and_its_enemy_knowledge_outlives_the_eye()
    {
        using var m = World();
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 21, ClusterId = 0, SideId = 0, Position = [0, 0, 0] },
            DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]));
        m.PlaceStation(station, m.Sectors[0]);
        Assert.True(station.SeenBySide(99)); Assert.True(station.GetCurrentEye(2));
        Sweep(m);
        Assert.True(station.SeenBySide(2)); Assert.False(station.GetCurrentEye(1));
        station.SideVisibility.SetSideVisibility(2, false);
        Assert.False(station.SeenBySide(1)); Sweep(m);
        Assert.False(station.SeenBySide(99));
    }

    [Fact]
    public void Repeated_sector_membership_registers_each_scanner_identity_once()
    {
        using var m = World(); var player = m.GetShip(10)!;
        var scanner = Place(m, 11, 1, new(0, 50, 0));
        // Legacy lesson graph fixtures retain duplicate sector membership.
        // A native scanner pointer still represents one scanner, not a new ID.
        m.Sectors[0].Add(scanner);
        Sweep(m);
        Assert.True(player.GetCurrentEye(2));
        scanner.Position = new(50000, 0, 0); Sweep(m);
        Assert.False(player.GetCurrentEye(2));
    }

    [Fact]
    public void Allied_scanners_are_in_the_player_pool_even_when_allied_viz_is_off()
    {
        using var m = World(); var player = m.GetShip(10)!;
        m.Sides[0].AllyGroup = 7; m.Sides[1].AllyGroup = 7;
        Place(m, 11, 1, new(0, 50, 0)); Sweep(m);
        Assert.False(player.GetCurrentEye(2));
    }

    [Fact]
    public void Enemy_ship_scanning_uses_the_native_quarter_rear_range()
    {
        using var m = World(); var player = m.GetShip(10)!;
        var scanner = Place(m, 11, 1, new(0, 0, -50)); Sweep(m);
        Assert.False(player.GetCurrentEye(1)); // identity looks -Z; target is behind
        scanner.Position = new(0, 0, 50); Sweep(m);
        Assert.True(player.GetCurrentEye(2));
    }

    [Fact]
    public void Multiplayer_keeps_independent_sides_and_existing_predictable_set_retention()
    {
        using var m = World(false); var player = m.GetShip(10)!;
        Place(m, 11, 1, new(0, 50, 0)); Place(m, 12, 2, new(50000, 0, 0)); Sweep(m);
        Assert.True(player.GetCurrentEye(1)); Assert.False(player.GetCurrentEye(2));
        Assert.False(player.SeenBySide(99));
        player.SideVisibility.SetSideVisibility(2, true);
        player.SideVisibility.SetSideVisibility(1, false);
        Assert.True(player.SeenBySide(2)); Assert.False(player.SeenBySide(1));
        player.SideVisibility.Predictable = true;
        player.SideVisibility.SetSideVisibility(2, false);
        Assert.True(player.SeenBySide(2)); Assert.False(player.GetCurrentEye(2));
    }
}
