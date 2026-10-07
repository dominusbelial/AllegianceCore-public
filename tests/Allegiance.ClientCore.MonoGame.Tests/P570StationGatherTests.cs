using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P570 — the "draw each station exactly once" invariant behind the live
/// double-station report (with soft particles ON a second, mis-tinted copy of
/// the home station could appear). The colour-pass station gather must yield
/// exactly ONE entry per distinct station in the local sector, even if the
/// world source ever yields the same station twice. Fails if the dedup is
/// removed (the second entry reappears) and passes with it.
/// </summary>
public class P570StationGatherTests
{
    private static DataStationIGC Station(short id, short cluster, short side) => new(
        Position: new[] { 10f, 20f, 30f },
        Up: new[] { 0f, 1f, 0f },
        Forward: new[] { 0f, 0f, -1f },
        Rotation: new[] { 0f, 0f, 0f, 1f },
        ClusterId: cluster,
        SideId: side,
        StationId: id,
        StationTypeId: 15,
        BpHull: 0,
        BpShield: 0,
        Name: $"st{id}");

    private static float RadiusOf(DataStationIGC _) => 302.5f;

    [Fact]
    public void Same_station_yielded_twice_is_drawn_exactly_once()
    {
        const short mySector = 1050;
        // Simulate the failure mode: the SAME station (StationId 7) appears under
        // two world keys in the local sector.
        var world = new List<KeyValuePair<short, DataStationIGC>>
        {
            new(1, Station(7, mySector, 3)),
            new(2, Station(7, mySector, 3)),   // duplicate of the same station
        };

        var entries = StationSceneGather.ColourPass(world, mySector, RadiusOf).ToList();

        Assert.Single(entries);   // <-- the exactly-once guarantee (fails without dedup)
        Assert.Equal((short)3, entries[0].SideId);
        Assert.Equal(302.5f, entries[0].Radius);
    }

    [Fact]
    public void Distinct_stations_in_sector_each_yield_one_entry()
    {
        const short mySector = 1050;
        var world = new List<KeyValuePair<short, DataStationIGC>>
        {
            new(1, Station(7, mySector, 10)),
            new(2, Station(8, mySector, 20)),
        };

        var entries = StationSceneGather.ColourPass(world, mySector, RadiusOf).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal(new short[] { 10, 20 }, entries.Select(e => e.SideId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Stations_in_other_sectors_are_excluded()
    {
        const short mySector = 1050;
        var world = new List<KeyValuePair<short, DataStationIGC>>
        {
            new(1, Station(7, mySector, 0)),       // in-sector -> kept
            new(2, Station(8, (short)(mySector + 1), 1)),  // other sector -> excluded
        };

        var entries = StationSceneGather.ColourPass(world, mySector, RadiusOf).ToList();

        Assert.Single(entries);
        Assert.Equal(302.5f, entries[0].Radius);
    }
}
