using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P149 — pure-domain (no sockets) proofs of the narrowed
/// <c>FindTarget</c> port, <see cref="Ship.FindNearestFriendlyStation"/>: the
/// nearest friendly station in the ship's own sector whose station type carries
/// a given ability bit (native <c>FindTarget(pship, c_ttFriendly|c_ttStation|
/// c_ttNearest|c_ttAnyCluster, …, c_sabmRepair)</c>, <c>src/Igc/common.cpp:470</c>,
/// minus <c>c_ttAnyCluster</c>). All station types are real records from the
/// <c>PCore001.igc</c> static core — a repair-capable type (repair bit set) and
/// a non-repair-capable type (repair bit clear) are selected data-driven from
/// the core rather than hardcoded, matching this port's data-derived ethos; the
/// positions/sides are crafted to exercise each filter branch. The ship sits in
/// sector 1052 of <c>XXV_simple.igc</c>, the P111-documented no-station fly
/// sector, so the only stations in play are the ones each test injects.
/// </summary>
/// <remarks>
/// <para>No live/network test is required for this method: it is pure
/// data-driven logic over <see cref="Sector.Stations"/>, <c>mission.Core.
/// StationTypes</c> and the ship's own position/side — a unit test is the
/// honest, sufficient bar (stated plainly, per the task). The real
/// <c>PCore001.igc</c> static core supplies genuine station types so the
/// ability-bit test is exercised against real capability masks, not literals
/// bolted onto the code under test.</para>
/// <para>The five required behaviours map one-to-one onto the five
/// <c>[Fact]</c>s below (friendly-repair found; friendly-non-repair excluded;
/// enemy-repair excluded; nearer-of-two wins; none-qualify → null).</para>
/// </remarks>
public sealed class FindNearestFriendlyStationTests
{
    /// <summary><c>c_sabmRepair = 0x40</c> (<c>Wopr/Wopr/Constants/
    /// StationAbilityBitMask.cs:16</c> — the C# interop mirror of the native
    /// <c>igc.h</c> bit constants). The same mask the native "return to base"
    /// call passes.</summary>
    private const short RepairBit = 0x40;

    /// <summary><c>XXV_simple.igc</c>: the no-station fly sector (P111 report).</summary>
    private const short FlySector = 1052;

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap DemoMap()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("XXV_simple.igc not in corpus");
        return MissionMap.Load(IgcReader.ParseFile(path));
    }

    private static Sector Sector(Mission m) =>
        m.Sectors.FirstOrDefault(s => s.Id == FlySector)
        ?? throw new InvalidOperationException($"XXV_simple has no sector {FlySector}");

    private static Mission NewMission(StaticCore core)
    {
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, DemoMap(), [new SideSpec(0, "Team 1", civ)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewFriendlyShip(Mission m, Sector sector)
    {
        var ship = new Ship(9001, m.Core.HullTypes.Values.OrderBy(h => h.HitPoints).First(),
                            Vector3.Zero, Vector3.Zero, Orientation.Identity);
        ship.SideId = 0;
        m.PlaceShip(ship, sector);   // gives it Mission + Sector
        return ship;
    }

    /// <summary>A synthetic station: a crafted <see cref="DataStationIGC"/>
    /// referencing a <b>real</b> station type from the core (so the search's
    /// <c>mission.Core.StationTypes</c> lookup resolves it to that real type
    /// and <see cref="DataStationTypeIGC.HasCapability"/> tests its real
    /// capability mask), placed in sector <paramref name="sector"/> via the
    /// public <see cref="Mission.PlaceStation"/> injection seam.</summary>
    private static void PlaceStation(Mission m, Sector sector, short id, short side,
                                     float x, float y, float z, short typeId)
    {
        var type = m.Core.StationTypes[typeId];   // must resolve — we picked it from the core
        var spot = new Vector3(x, y, z);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: typeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
        m.PlaceStation(new Station(data, type), sector);
    }

    /// <summary>The first repair-capable and first non-repair-capable real
    /// station type ids in the core. Each test uses them so the ability-bit
    /// branch is exercised against genuine capability masks; asserting both
    /// pools are non-empty keeps the test data-driven, not corpus-luck.</summary>
    private static (short Repair, short NonRepair) TypeIds(StaticCore core)
    {
        var repair = core.StationTypes.Values.FirstOrDefault(t => (t.SabmCapabilities & RepairBit) != 0);
        var nonRepair = core.StationTypes.Values.FirstOrDefault(t => (t.SabmCapabilities & RepairBit) == 0);
        Assert.NotEqual(default(DataStationTypeIGC), repair);    // PCore001 must have a repair-capable station type
        Assert.NotEqual(default(DataStationTypeIGC), nonRepair); // …and one without the repair bit, or the exclusion branch can't be exercised
        return ((short)repair.StationTypeId, (short)nonRepair.StationTypeId);
    }

    /// <summary>⭐ A friendly (same-side) repair-capable station is found —
    /// verification point 1 of the task.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Finds_nearest_friendly_repair_station()
    {
        Require.Artwork();
        var core = Core();
        var (repair, _) = TypeIds(core);
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewFriendlyShip(mission, sector);

        PlaceStation(mission, sector, id: 101, side: 0, x: 100f, y: 0f, z: 0f, repair);

        var found = ship.FindNearestFriendlyStation(RepairBit);
        Assert.True(found.HasValue, "a friendly repair station must be found");
        Assert.Equal(101, found.Value.StationId);
        Console.WriteLine($"P149-FOUND: side-0 repair station @ (100,0,0) found at d²=10000, StationId {found.Value.StationId}");
    }

    /// <summary>⭐ A friendly non-repair-capable station is correctly excluded —
    /// verification point 2.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Excludes_friendly_non_repair_station()
    {
        Require.Artwork();
        var core = Core();
        var (_, nonRepair) = TypeIds(core);
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewFriendlyShip(mission, sector);

        PlaceStation(mission, sector, id: 201, side: 0, x: 50f, y: 0f, z: 0f, nonRepair);

        Assert.Null(ship.FindNearestFriendlyStation(RepairBit));
    }

    /// <summary>⭐ An enemy-side repair-capable station is correctly excluded —
    /// verification point 3.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Excludes_enemy_repair_station()
    {
        Require.Artwork();
        var core = Core();
        var (repair, _) = TypeIds(core);
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewFriendlyShip(mission, sector);   // SideId 0

        PlaceStation(mission, sector, id: 301, side: 1, x: 10f, y: 0f, z: 0f, repair);

        Assert.Null(ship.FindNearestFriendlyStation(RepairBit));
    }

    /// <summary>⭐ When two friendly repair stations exist, the nearer one wins,
    /// and a still-nearer friendly non-repair station does not steal it —
    /// verification point 4 (nearest-by-squared-distance +
    /// non-repair exclusion combined).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Nearer_of_two_friendly_repair_stations_wins()
    {
        Require.Artwork();
        var core = Core();
        var (repair, nonRepair) = TypeIds(core);
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewFriendlyShip(mission, sector);   // origin

        PlaceStation(mission, sector, id: 421, side: 0, x: 300f, y: 0f, z: 0f, repair);  // far repair
        PlaceStation(mission, sector, id: 422, side: 0, x: 100f, y: 0f, z: 0f, repair);  // near repair → must win
        PlaceStation(mission, sector, id: 423, side: 0, x: 10f,  y: 0f, z: 0f, nonRepair); // closest, but excluded

        var found = ship.FindNearestFriendlyStation(RepairBit);
        Assert.True(found.HasValue, "a friendly repair station must be found");
        Assert.Equal(422, found.Value.StationId);
        Console.WriteLine($"P149-NEAREST: chose StationId {found.Value.StationId} @ d²=10000 over 421 @ d²=90000 (423 @ d²=100 excluded: non-repair)");
    }

    /// <summary>⭐ When nothing qualifies, <c>null</c> is returned — an enemy
    /// repair station plus a friendly non-repair station, neither eligible —
    /// verification point 5.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Returns_null_when_nothing_qualifies()
    {
        Require.Artwork();
        var core = Core();
        var (repair, nonRepair) = TypeIds(core);
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewFriendlyShip(mission, sector);

        PlaceStation(mission, sector, id: 501, side: 1, x: 10f, y: 0f, z: 0f, repair);      // enemy repair
        PlaceStation(mission, sector, id: 502, side: 0, x: 20f, y: 0f, z: 0f, nonRepair);   // friendly non-repair

        Assert.Null(ship.FindNearestFriendlyStation(RepairBit));
    }
}
