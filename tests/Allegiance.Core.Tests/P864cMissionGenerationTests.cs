using System.Buffers.Binary;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>SIM-0748: missionigc.cpp:3977-4055; common.cpp:2963-3107.</summary>
public sealed class P864cMissionGenerationTests
{
    private static StaticCore Core()
    {
        var records = new List<IgcRecord>();
        var body = new List<byte>();
        void Add(IgcObjectType type, byte[] bytes)
        { records.Add(new(type, bytes.Length, body.Count)); body.AddRange(bytes); }
        var constants = new byte[DataConstantsIGC.Size];
        BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(0), .2f);
        BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(4), 10000);
        BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(36), 1000);
        Add(IgcObjectType.Constants, constants);
        var civ = new byte[DataCivilizationIGC.Size];
        Add(IgcObjectType.Civilization, civ);
        for (short id = 0; id < 6; id++)
        {
            var station = new byte[DataStationTypeIGC.Size];
            BinaryPrimitives.WriteInt16LittleEndian(station.AsSpan(446), id);
            BinaryPrimitives.WriteSingleLittleEndian(station.AsSpan(368), 1000);
            BinaryPrimitives.WriteSingleLittleEndian(station.AsSpan(392), 100);
            Add(IgcObjectType.StationType, station);
        }
        var treasure = new byte[36];
        BinaryPrimitives.WriteInt16LittleEndian(treasure.AsSpan(26), 11);
        BinaryPrimitives.WriteInt16LittleEndian(treasure.AsSpan(28), 1);
        BinaryPrimitives.WriteInt16LittleEndian(treasure.AsSpan(32), 101);
        treasure[34] = (byte)TreasureCode.Cash; treasure[35] = 1;
        Add(IgcObjectType.TreasureSet, treasure);
        return StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize,
            Version = 1, Records = records, Body = body.ToArray(), BodyLength = body.Count, BytesConsumed = body.Count });
    }

    private static byte[] Bits(params int[] indices)
    {
        var bits = new byte[50];
        foreach (int index in indices) bits[index / 8] |= (byte)(0x80 >> (index % 8));
        return bits;
    }

    private static Mission Assemble(StaticCore core, DataStationIGC[]? stations = null, DataAsteroidIGC[]? rocks = null,
        MissionGoals? goals = null, ResearchSettings? research = null)
    {
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]);
        return Mission.Assemble(core, MissionMap.FromLists([
            cluster with { ClusterId = 1, HomeSector = true },
            cluster with { ClusterId = 2 }, cluster with { ClusterId = 3 }], [], stations ?? [], rocks ?? []),
            [new SideSpec(0, "A", 0)], new NullIgcSite(), goals: goals, research: research);
    }

    private static DataStationIGC Station(short id, short type) => new([0, 0, 0], [0, 1, 0], [1, 0, 0],
        [0, 0, 1, 0], 1, 0, id, type, 153, 76, "Keep this name");

    [Fact]
    public void Station_masks_decode_at_native_buyable_and_station_offsets()
    {
        var bytes = new byte[DataStationTypeIGC.Size];
        Bits(0, 399).CopyTo(bytes, 262); Bits(130).CopyTo(bytes, 312); Bits(220).CopyTo(bytes, 396);
        var type = DataStationTypeIGC.Decode(bytes);
        Assert.Equal(Bits(0, 399), type.TtbmRequired);
        Assert.Equal(Bits(130), type.TtbmEffects);
        Assert.Equal(Bits(220), type.TtbmLocal);
    }

    [Fact]
    public void File_station_replacement_uses_original_local_effect_and_development_techs_and_first_matching_class()
    {
        var core = Core(); var types = (Dictionary<short, DataStationTypeIGC>)core.StationTypes;
        types[0] = types[0] with { ClassId = 9 }; // buyable, wrong class
        types[1] = types[1] with { ClassId = 2, TtbmRequired = Bits(130, 220, 399), TtbmLocal = Bits(15) };
        types[2] = types[2] with { ClassId = 2, TtbmRequired = Bits(130) };
        types[4] = types[4] with { ClassId = 2, TtbmRequired = Bits(14), TtbmLocal = Bits(130), TtbmEffects = Bits(220) };
        var m = Assemble(core, [Station(10, 4), Station(11, 4), Station(12, 2)]);
        m.Sides[0].DevelopmentTechs = new TechTreeBitMask(Bits(399, 130));
        m.ReplaceUnavailableStationTypes();
        Assert.Equal(new short[] { 12, 11, 10 }, m.Sectors[0].StationRuntimes.Select(s => s.StationId));
        foreach (var station in m.Sectors[0].StationRuntimes.Where(s => s.StationId != 12))
        {
            Assert.Equal((short)1, station.Type.StationTypeId);
            Assert.Equal("Keep this name", station.Data.Name);
            Assert.Equal((byte)153, station.Data.BpHull); Assert.Equal((byte)76, station.Data.BpShield);
        }
        Assert.All(m.Sectors[0].Stations.Where(s => s.StationId != 12), s => Assert.Equal((short)1, s.StationTypeId));
        Assert.All(m.Map.Stations.Where(s => s.StationId != 12), s => Assert.Equal((short)4, s.StationTypeId));
    }

    [Fact]
    public void Compatible_station_stays_and_other_buildings_or_candidate_effects_cannot_unlock_a_replacement()
    {
        var core = Core(); var types = (Dictionary<short, DataStationTypeIGC>)core.StationTypes;
        types[0] = types[0] with { ClassId = 8, TtbmEffects = Bits(100) };
        types[1] = types[1] with { ClassId = 2, TtbmRequired = Bits(100), TtbmEffects = Bits(100) };
        types[4] = types[4] with { ClassId = 2, TtbmRequired = Bits(101) };
        types[5] = types[5] with { ClassId = 3, TtbmRequired = Bits(220, 399), TtbmLocal = Bits(220), TtbmEffects = Bits(399) };
        var m = Assemble(core, [Station(10, 4), Station(11, 0), Station(12, 5)]);
        m.Sides[0].DevelopmentTechs = TechTreeBitMask.Empty;
        m.ReplaceUnavailableStationTypes();
        Assert.Equal(new short[] { 4, 0, 5 }, m.Sectors[0].StationRuntimes.Select(s => s.Type.StationTypeId));
    }

    [Fact]
    public void File_population_gates_lists_independently_preserves_existing_objects_and_counts_imported_He3()
    {
        var rock = DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        { AsteroidId = 100, ClusterId = 2, AabmCapabilities = DataAsteroidIGC.AabmMineHe3, Ore = 123, OreMax = 123 };
        var m = Assemble(Core(), rocks: [rock], goals: new() { Artifacts = 3 });
        m.TreasureParameters.PlayerStartSet = m.TreasureParameters.NeutralStartSet = 11;
        var old = m.GenerateTreasure(0, m.Sectors[2], 11, new Random(864))!;
        m.PopulateFileMap(0, MapGenParams.FileMapDefaults(1));
        Assert.Equal(23, m.Sectors[0].Asteroids.Count);
        Assert.Equal(rock, Assert.Single(m.Sectors[1].Asteroids));
        Assert.Equal(25, m.Sectors[2].Asteroids.Count);
        Assert.Equal(6, m.Sectors[1].Models.OfType<Treasure>().Count()); // 5 cash + artifact
        Assert.Same(old, Assert.Single(m.Sectors[2].Models.OfType<Treasure>()));
        var added = m.Sectors.SelectMany(s => s.Asteroids).Where(a => a.AsteroidId != 100).ToArray();
        Assert.All(added.Where(a => (a.AabmCapabilities & 1) != 0), a => Assert.Equal(1000f / 7, a.Ore, 3));
        Assert.Equal(49, m.Sectors.SelectMany(s => s.Asteroids).Select(a => a.AsteroidId).Distinct().Count());
        Assert.Equal(123f, m.Sectors[1].Mineable[0].OreRemaining);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Zero_He3_and_special_resource_path_selection_follow_native_file_population(bool anyPaths)
    {
        var m = Assemble(Core(), research: new() { AllowExpansionPath = false, AllowTacticalPath = anyPaths, AllowSupremacyPath = false });
        MissionMapGenerator.PopulateFileMapAsteroids(m, new MapGenParams { Seed = 864,
            PlayerSectorMineableAsteroids = 0, NeutralSectorMineableAsteroids = 0,
            PlayerSectorSpecialAsteroids = 3, NeutralSectorSpecialAsteroids = 3,
            PlayerSectorAsteroids = 0, NeutralSectorAsteroids = 0 });
        Assert.All(m.Sectors, s =>
        {
            Assert.Equal(3, s.Asteroids.Count);
            Assert.All(s.Asteroids, a => Assert.Equal(0f, a.Ore));
            if (anyPaths) Assert.All(s.Asteroids, a => Assert.Equal((short)(DataAsteroidIGC.AabmSpecial << 1), a.AabmCapabilities));
            else Assert.Equal(3, s.Asteroids.Select(a => a.AabmCapabilities).Distinct().Count());
        });
    }

    [Fact]
    public void Flags_created_during_population_do_not_suppress_initial_treasures()
    {
        var core = Core(); var types = (Dictionary<short, DataStationTypeIGC>)core.StationTypes;
        types[0] = types[0] with { SabmCapabilities = DataStationTypeIGC.SabmStart };
        types[5] = types[5] with { SabmCapabilities = DataStationTypeIGC.SabmPedestal };
        var m = Assemble(core, [Station(10, 0)], goals: new() { Flags = 2 });
        m.TreasureParameters.PlayerStartSet = 11;
        m.PopulateFileMap(0, new MapGenParams { PlayerSectorTreasures = 1,
            PlayerSectorAsteroids = 0, PlayerSectorMineableAsteroids = 0, PlayerSectorSpecialAsteroids = 0,
            NeutralSectorAsteroids = 0, NeutralSectorMineableAsteroids = 0, NeutralSectorSpecialAsteroids = 0 });
        Assert.Equal(2, m.Sectors[0].Models.OfType<Treasure>().Count(t => t.Code == TreasureCode.Flag));
        Assert.Single(m.Sectors[0].Models.OfType<Treasure>(), t => t.Code == TreasureCode.Cash);
    }
}
