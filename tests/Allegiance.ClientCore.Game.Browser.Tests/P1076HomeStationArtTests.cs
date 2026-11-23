using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1076HomeStationArtTests
{
    [Theory]
    [InlineData(0, 0, false, 0)]
    [InlineData(0, 0, true, 0)]
    [InlineData(1, 0, false, 2)]
    [InlineData(1, 0, true, 0)]
    [InlineData(-1, 0, false, 2)]
    [InlineData(-1, 0, true, 0)]
    [InlineData(0, null, false, 2)]
    [InlineData(-1, -1, false, 2)]
    public void Station_priority_preserves_enemy_and_neutral_grace(short stationSide, int? ownSide, bool inView, int expected)
        => Assert.Equal(expected, Game1.StationArtPriority(stationSide, (short?)ownSide, inView));

    [Fact]
    public async Task Dock_requests_only_its_station_and_sky_throttles_and_leaves_launch_clock_unstarted()
    {
        await using var session = new ConnectionSession();
        var game = Game(session);
        var world = session.World;
        AddStation(world, 42, 7, 3, 0, 100);
        AddStation(world, 43, 9, 3, 0, 200);
        world.Apply(new ExportCluster { Cluster = new DataClusterIGC(7, [0, 0, 1], 0xffffff, 0, 0, 3, 0, 250,
            "Home", "globe6", "nebplnt19bmp", 0, 0, 60, true, true) });
        // Docked ships need not retain the dock's sector in their own SectorId.
        var own = new ShipEntity { ShipId = 1 };
        SetShip(own, "StationId", (short)42);
        SetShip(own, "SectorId", (short)-1);
        var queued = new List<(string, int)>();
        game.PrioritizeBattlePack = (key, priority) => queued.Add((key, priority));
        Set(game, "_nowSeconds", 10d);
        game.RequestDockedSectorArt(world, own);
        Assert.Equal(new[] { ("station-7", 0), ("mapgen-globe-6", 0), ("mapgen-planet-nebplnt19bmp", 0) }, queued);
        Assert.Equal(-1d, Get<double>(game, "_sectorArtStartedAt"));
        queued.Clear();
        Set(game, "_nowSeconds", 10.249d);
        game.RequestDockedSectorArt(world, own);
        Assert.Empty(queued);
        Set(game, "_nowSeconds", 10.25d);
        game.RequestDockedSectorArt(world, own);
        Assert.Equal(3, queued.Count);
        queued.Clear();
        SetShip(own, "StationId", (short)43);
        game.RequestDockedSectorArt(world, own);
        Assert.Equal(("station-9", 0), queued[0]); // changed dock bypasses previous dock's throttle
        Assert.Equal(-1d, Get<double>(game, "_sectorArtStartedAt"));
    }

    [Fact]
    public async Task Dock_ignores_undocked_unknown_station_and_ghost_and_supports_legacy_callback()
    {
        await using var session = new ConnectionSession();
        var game = Game(session);
        var own = new ShipEntity { ShipId = 1 };
        var queued = new List<string>();
        game.RequestBattlePack = queued.Add;
        foreach (short station in new short[] { -1, 99 })
        {
            SetShip(own, "StationId", station);
            game.RequestDockedSectorArt(session.World, own);
        }
        Assert.Empty(queued);
        AddStation(session.World, 42, 7, 3, 0, 100);
        SetShip(own, "StationId", (short)42);
        game.RequestDockedSectorArt(session.World, own);
        Assert.Equal(new[] { "station-7" }, queued);
        queued.Clear();
        session.World.Apply(new SetCluster { SectorId = 3, Ship = ServerShipUpdate.FromValues(1, 0, default, 0,
            Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity,
            Allegiance.Core.Collision.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        session.World.Apply(new PlayerInfo { ShipId = 1, ISide = 0, NDeaths = short.MaxValue });
        // Default mission has unlimited lives; explicitly supply the finite native mission parameter.
        session.World.Apply(new MissionDef { ILives = 0 });
        Assert.True(session.World.OwnIsGhost);
        Set(game, "_nowSeconds", 20d);
        game.RequestDockedSectorArt(session.World, own);
        Assert.Empty(queued);
    }

    [Fact]
    public async Task Flight_queues_stations_closest_first_and_starts_prefetch_grace_at_launch()
    {
        await using var session = new ConnectionSession();
        var game = Game(session);
        var world = session.World;
        world.Apply(new SetCluster { SectorId = 3, Ship = ServerShipUpdate.FromValues(1, 0, default, 0,
            Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity,
            Allegiance.Core.Collision.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new PlayerInfo { ShipId = 1, ISide = 0 });
        AddStation(world, 1, 11, 3, 0, 300);
        AddStation(world, 2, 12, 3, 1, 100);
        AddStation(world, 3, 13, 3, -1, 200);
        AddStation(world, 4, 14, 4, 0, 50);
        var queued = new List<(string, int)>();
        game.PrioritizeBattlePack = (key, priority) => queued.Add((key, priority));
        Set(game, "_nowSeconds", 100d); // ten+ seconds in the hangar do not consume launch grace
        game.RequestSectorArt(3, 4);
        Assert.Equal(new[] { ("station-12", 2), ("station-13", 2), ("station-11", 0) }, queued.Where(q => q.Item1.StartsWith("station-", StringComparison.Ordinal)));
        Assert.Equal(100d, Get<double>(game, "_sectorArtStartedAt"));
        queued.Clear();
        Set(game, "_nowSeconds", 100.249d);
        game.RequestSectorArt(3, 4);
        Assert.Empty(queued);
        // A capture changes ownership without changing roster size. Once the
        // throttle expires, the newly friendly offscreen pack must lose grace.
        world.Apply(new StationCapture { StationId = 2, SidOld = 1, SidNew = 0, ShipIdCredit = 1 });
        Set(game, "_nowSeconds", 100.25d);
        game.RequestSectorArt(3, 4);
        Assert.Contains(("station-12", 0), queued);
        Assert.DoesNotContain(("station-12", 2), queued);
        queued.Clear();
        Set(game, "_nowSeconds", 129.99d);
        game.RequestSectorArt(3, 4);
        Assert.DoesNotContain(queued, q => q.Item1 == "station-14");
        queued.Clear();
        Set(game, "_nowSeconds", 130.25d);
        game.RequestSectorArt(3, 4);
        Assert.Contains(("station-14", 3), queued);
    }

    private static Game1 Game(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game); // no GPU/window constructor, exercise actual request methods
        Set(game, "_flightSession", session);
        Set(game, "_sectorArtStartedAt", -1d);
        Set(game, "_lastArtSector", (short)-1);
        Set(game, "_lastDockArtStation", (short)-1);
        Set(game, "_rockMissionGeneration", session.World.MissionGeneration);
        Set(game, "_sectorRocks", new Dictionary<short, DataAsteroidIGC[]>());
        game.RequestBattlePack = _ => { };
        return game;
    }
    private static void AddStation(ClientWorld world, short id, short type, short sector, short side, float distance) =>
        world.Apply(new ExportStation { Station = new DataStationIGC([distance, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1],
            sector, side, id, type, 240, 240, "Garrison") });
    private static void Set(Game1 game, string field, object value) =>
        typeof(Game1).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string field) =>
        (T)typeof(Game1).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
    private static void SetShip(ShipEntity ship, string property, short value) => typeof(ShipEntity).GetProperty(property)!.SetValue(ship, value);
}

public sealed class P1076ProofCivilizationTests
{
    [Fact]
    public void Proof_host_selects_cold_garrison_civilizations_without_changing_defaults()
    {
        Assert.Null(BattleOptions.Parse(["art", "generation", "state"]).CivIds);
        Assert.Equal(new[] { 5, 101 }, BattleOptions.Parse(["art", "generation", "state", "--civ-ids", "5,101"]).CivIds);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("5,101,1")]
    [InlineData("-1,5")]
    [InlineData("5,no")]
    public void Proof_host_rejects_malformed_civilization_pairs(string value) =>
        Assert.Throws<ArgumentException>(() => BattleOptions.Parse(["art", "generation", "state", "--civ-ids", value]));
}
