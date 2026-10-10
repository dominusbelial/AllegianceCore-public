using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P301 — the <b>station-side</b> pod-rescue pass
/// (<c>Sector.ResolveStationRescueEvents</c> → <c>DrainStationRescues</c>),
/// the port of the native OT_station collision branch
/// (<c>shipIGC.cpp:862-867</c>): a drifting <c>c_habmLifepod</c> that reaches a
/// same-side station whose type carries <c>c_sabmRescue</c> (0x2000) is rescued
/// (docked at that station, cancelling the P279 oxygen timer). These are
/// model-level discriminator tests: they drive the REAL <c>Sector.Update</c>
/// (which runs the pass) on a real core/map mission with synthetic stations, so
/// the capability-bit gate is isolated from the normal dock pass.
///
/// Geometry (real data): the side-0 garrison station type (#15) has
/// <c>Radius=423.5</c> and the civ-1 lifepod hull (<c>dreghpod</c>) has
/// <c>Length=4</c>, so the rescue proximity threshold is
/// <c>0.5*4 + 423.5 = 425.5</c>. The normal dock pass uses a fixed
/// <c>DockRadius=200</c>. A pod parked 300u from a station center is therefore
/// <b>inside</b> rescue proximity (≤ 425.5) yet <b>outside</b> dock range
/// (&gt; 200) — so detection (or its absence) is attributable purely to the
/// <c>c_sabmRescue</c> gate, not to the ordinary dock pass.
/// </summary>
public class StationRescueModelTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Mission Assembly()
    {
        var core = Core();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[]
        {
            new SideSpec(0, "Alpha", 1),
            new SideSpec(1, "Bravo", 3),
        };
        var m = Mission.Assemble(core, map, sides, new RecordingIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A synthetic station of the given <paramref name="side"/> at a
    /// chosen position in <paramref name="sectorId"/>, built from the REAL
    /// garrison station type (#15, radius 423.5) with its
    /// <c>SabmCapabilities</c> overridden so the test controls the rescue bit
    /// exactly. Real type chosen (rather than the arbitrary First) so the
    /// radius-driven geometry above holds deterministically.</summary>
    private static Station AddStation(Mission m, short sectorId, short side, int id, short sabm, float x, float y, float z)
    {
        var baseType = m.Core.StationTypes[15];
        var type = baseType with { SabmCapabilities = sabm };
        var sector = m.Sectors.First(s => s.Id == sectorId);
        var data = new DataStationIGC(
            Position: [x, y, z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorId, SideId: side, StationId: (short)id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"SYN_{id}");
        var st = new Station(data, type);
        m.PlaceStation(st, sector);
        return st;
    }

    /// <summary>A real civ-1 lifepod (dreghpod, Length=4) placed flying in
    /// sector <paramref name="sectorId"/> on <paramref name="side"/>.</summary>
    private static Ship Lifepod(Mission m, short sectorId, short id, Vector3 pos, short side)
    {
        var hull = m.Core.HullTypes[m.Core.Civilizations[1].Lifepod];
        var s = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(s, sectorId);
        return s;
    }

    private const short FlySector = 1052;   // no real station in XXV_simple (LiveRescueTests topology)

    /// <summary>Drive a few ticks so the rescue pass runs.</summary>
    private static void Tick(Sector sector)
    {
        double now = 1000.0;
        for (int i = 0; i < 5; i++)
            sector.Update(now += 0.25);
    }

    /// <summary>⭐ Positive — a same-side lifepod 300u from a c_sabmRescue
    /// station is detected for station rescue (real native mechanic,
    /// shipIGC.cpp:862-867).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lifepod_near_rescue_capable_station_is_detected()
    {
        Require.Artwork();
        var m = Assembly();
        var rescue = AddStation(m, FlySector, 0, 7701, DataStationTypeIGC.SabmRescue, 0, 0, 0);
        var pod = Lifepod(m, FlySector, 1, new Vector3(0, 0, 300), 0);

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        var det = sector.DrainStationRescues();
        // No latch (atomic function, cf. ResolveRescueEvents): the in-range pod
        // is re-detected each tick until docked, so the drain has 5 identical
        // pairs (one per Tick above). Assert every one is the right pair.
        Assert.NotEmpty(det);
        Assert.All(det, d => Assert.True(ReferenceEquals(d.Rescuee, pod), "detected ship must be the lifepod"));
        Assert.All(det, d => Assert.True(ReferenceEquals(d.Station, rescue), "detected station must be the c_sabmRescue one"));
        Console.WriteLine($"STATION-RESCUE+: lifepod {pod.Id} @300u from c_sabmRescue station {rescue.StationId} -> detected on {det.Count} consecutive ticks");
    }

    /// <summary>⭐ Negative (the bit) — the SAME geometry but the station type
    /// LACKS c_sabmRescue (the real standard garrison #15) -> no detection. This
    /// is the real-data heart of the gate: real garrisons do not carry the bit
    /// and so do not rescue pods.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lifepod_near_non_rescue_station_is_NOT_detected()
    {
        Require.Artwork();
        var m = Assembly();
        var garrison = AddStation(m, FlySector, 0, 7702, 0x0377, 0, 0, 0);  // real garrison caps, no rescue
        var pod = Lifepod(m, FlySector, 1, new Vector3(0, 0, 300), 0);

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        Assert.Empty(sector.DrainStationRescues());
        Assert.Null(pod.StationId);          // not docked either (300 > DockRadius 200)
        Console.WriteLine($"STATION-RESCUE-: lifepod {pod.Id} @300u from non-rescue garrison {garrison.StationId} (sabm=0x377) -> NO station rescue, pod stays flying");
    }

    /// <summary>⭐ Side gate — a c_sabmRescue station of the OPPOSITE side does
    /// not rescue the pod (native requires same-side/allied, shipIGC.cpp:865;
    /// the allied leg collapses to same-side in this port).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Opposite_side_rescue_station_is_NOT_detected()
    {
        Require.Artwork();
        var m = Assembly();
        var rescue = AddStation(m, FlySector, 1, 7703, DataStationTypeIGC.SabmRescue, 0, 0, 0); // side 1
        var pod = Lifepod(m, FlySector, 1, new Vector3(0, 0, 300), 0);                          // side 0

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        Assert.Empty(sector.DrainStationRescues());
        Assert.Null(pod.StationId);
        Console.WriteLine($"STATION-RESCUE-SIDE: lifepod {pod.Id} (side0) @300u from side-1 c_sabmRescue station {rescue.StationId} -> NO rescue (cross-side gate)");
    }

    /// <summary>⭐ Proximity — a rescue-capable station far beyond the
    /// shipR+radius threshold is not detected.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Out_of_range_rescue_station_is_NOT_detected()
    {
        Require.Artwork();
        var m = Assembly();
        AddStation(m, FlySector, 0, 7704, DataStationTypeIGC.SabmRescue, 0, 0, 0);
        var pod = Lifepod(m, FlySector, 1, new Vector3(0, 0, 1000), 0);   // 1000 > 425.5

        var sector = m.Sectors.First(s => s.Id == FlySector);
        Tick(sector);

        Assert.Empty(sector.DrainStationRescues());
        Console.WriteLine("STATION-RESCUE-RANGE: lifepod @1000u from c_sabmRescue station (threshold 425.5) -> NO rescue (proximity gate)");
    }
}
