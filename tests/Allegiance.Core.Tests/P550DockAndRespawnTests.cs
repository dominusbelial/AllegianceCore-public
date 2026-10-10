using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P550 — failing-first proofs for the two fixes:
/// <list type="number">
/// <item><b>Docking gates.</b> A ship may only dock at a station whose type
/// carries the land ability its hull needs (a <c>c_habmFighter</c> hull needs
/// <c>SabmLand</c>; any non-fighter — a capital — needs <c>SabmCapLand</c>,
/// native <c>shipIGC.cpp:725-727</c>), and docking requires the native landing
/// GEOMETRY (<see cref="Sim.Station.InGarage"/>, <c>stationIGC.cpp:385-436</c>)
/// when the station's <c>capgarage</c> bay is resolvable — not merely a 200-unit
/// dock sphere. Tested end-to-end through the real
/// <see cref="Sector.ResolveDockCollisions"/> pass with a latching
/// <see cref="IIgcSite"/> (its <c>DockWithStationEvent</c> latches
/// <see cref="Ship.StationId"/>, the same effect the server host performs), so
/// the gate decisions are observed exactly as the production dock observes them.
/// </item>
/// <item><b>Respawn station choice.</b> <see cref="Srv.FindNearestRestartStation"/>
/// — the native <c>FindTarget(c_ttNearest, c_sabmRestart)</c> + <c>GetBase</c>
/// fallback (<c>FedSrv.CPP:12034-12038</c>, <c>fsmission.cpp:1433-1444</c>): the
/// NEAREST friendly restart-capable station, skipping a nearer one without
/// restart.</item>
/// </list>
/// Each case FAILS on the pre-P550 code (which docked any same-side ship within
/// 200u regardless of station ability/geometry, and respawned at the first
/// friendly station regardless of restart) and PASSES after.
/// </summary>
public sealed class P550DockAndRespawnTests
{
    private const short FlySector = 1052;   // XXV_simple's no-station fly sector (P111)

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        return StaticCore.Load(IgcReader.ParseFile(path));
    }

    private static MissionMap DemoMap()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("XXV_simple.igc not in corpus");
        return MissionMap.Load(IgcReader.ParseFile(path));
    }

    private static CvhFile? LoadCvh(string name)
    {
        var path = CvhCorpus.Files.FirstOrDefault(f => Path.GetFileName(f) == name + ".cvh");
        return path is null ? null : CvhReader.Parse(File.ReadAllText(path));
    }

    /// <summary>A real geometry-bearing land-capable type (tecsy, type 29:
    /// SabmLand|SabmCapLand, real capgarage frames in the corpus) whose model
    /// reconstructs to a genuine landing bay — used (a) as the base type for the
    /// capability tests (via `with` overriding SabmCapabilities) and (b) verbatim
    /// for the geometry test.</summary>
    private static DataStationTypeIGC Tecsy() => Core().StationTypes[29];

    /// <summary>An IIgcSite whose only non-default behaviour latches a dock the
    /// way the production host does (<see cref="Ship.StationId"/> =
    /// station id, <c>ServerHost.DockWithStationEvent</c>): the Sector dock pass
    /// calls this to COMMIT the dock, so observing StationId after the pass is
    /// exactly how the server observes "did it dock?".</summary>
    private sealed class LatchingSite : IIgcSite
    {
        public bool DockWithStationEvent(IShipIgc ship, IStationIgc station)
        {
            if (ship is Ship sh && station is DockStation ds)
                sh.StationId = ds.Station.StationId;
            return true;
        }
    }

    /// <summary>A placed ship+synthetic-station world on the fly sector, ready
    /// for a single <see cref="Sector.Update"/> to run the dock pass.</summary>
    private sealed record World(Mission Mission, Sector Sector, Ship Ship);

    private static World Place(Mission mission, DataStationTypeIGC stationType, short side,
        short stationId, DataHullTypeIGC shipHull, Vector3 shipPos, bool attachGeometry)
    {
        var sector = mission.Sectors.First(s => s.Id == FlySector);
        var data = new DataStationIGC(
            Position: [0f, 0f, 0f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: side, StationId: stationId,
            StationTypeId: stationType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{stationId}");
        mission.PlaceStation(new Station(data, stationType), sector);
        if (attachGeometry)
            sector.StationLandingBays = new StationLandingBayCache(n => LoadCvh(n));
        var ship = new Ship(9001, shipHull, shipPos, Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, sector);
        return new World(mission, sector, ship);
    }

    private static Mission NewMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, DemoMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static DataHullTypeIGC FighterHull(StaticCore core) =>
        core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter));

    private static DataHullTypeIGC CapitalHull(StaticCore core) =>
        core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmFighter)
                                         && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    // ── capability gate (native shipIGC.cpp:725-727) ────────────────────────

    /// <summary>⭐ A fighter docks at a SabmLand-capable station (unchanged
    /// feel).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fighter_docks_at_sabml_and_capable_station()
    {
        Require.Artwork();
        var core = Core();
        var m = NewMission(new LatchingSite());
        var w = Place(m, Tecsy(), 0, 5901, FighterHull(core), Vector3.Zero, attachGeometry: false);
        w.Sector.Update(1.0);
        Assert.Equal((short)5901, w.Ship.StationId);
    }

    /// <summary>⭐ A fighter is REFUSED at a station type WITHOUT SabmLand — the
    /// old code docked any same-side ship regardless of station ability.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fighter_is_refused_at_station_without_sabml_and()
    {
        Require.Artwork();
        var core = Core();
        var m = NewMission(new LatchingSite());
        var noLand = Tecsy() with { SabmCapabilities = DataStationTypeIGC.SabmCapLand };   // capital-only base
        var w = Place(m, noLand, 0, 5951, FighterHull(core), Vector3.Zero, attachGeometry: false);
        w.Sector.Update(1.0);
        Assert.Null(w.Ship.StationId);
    }

    /// <summary>⭐ A capital (non-fighter hull) docks at a SabmCapLand-capable
    /// station, and is REFUSED at one with only SabmLand.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Capital_needs_sabmc_apland()
    {
        Require.Artwork();
        var core = Core();
        var capital = CapitalHull(core);
        Assert.False(capital.HasCapability(DataHullTypeIGC.HabmFighter), "capital hull must not be a fighter");

        // CapLand present → docks.
        var m1 = NewMission(new LatchingSite());
        var w1 = Place(m1, Tecsy(), 0, 5961, capital, Vector3.Zero, attachGeometry: false);
        w1.Sector.Update(1.0);
        Assert.Equal((short)5961, w1.Ship.StationId);

        // Only SabmLand present (no SabmCapLand) → a capital is refused.
        var m2 = NewMission(new LatchingSite());
        var fighterOnly = Tecsy() with { SabmCapabilities = DataStationTypeIGC.SabmLand };
        var w2 = Place(m2, fighterOnly, 0, 5971, capital, Vector3.Zero, attachGeometry: false);
        w2.Sector.Update(1.0);
        Assert.Null(w2.Ship.StationId);
    }

    // ── geometry gate (native CstationIGC::InGarage, stationIGC.cpp:385-436) ──

    /// <summary>⭐ Docking requires the native landing GEOMETRY, not just the
    /// 200-unit dock sphere. Using the REAL tecsy bay: a ship at (−50,−400,0) is
    /// genuinely InGarage (verified from the reconstructed capgarage planes) and
    /// docks; a ship at (0,−100,0) is WELL within DockRadius (200) of the station
    /// centre yet OUTSIDE the bay and is refused. Pre-P550 both docked (the
    /// sphere alone decided).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docking_requires_native_landing_geometry_not_just_a_200u_sphere()
    {
        Require.Artwork();
        var core = Core();
        var tecsy = Tecsy();

        // In the bay (dist 403 from centre, i.e. OUTSIDE the 200u sphere) → docks.
        var inBay = NewMission(new LatchingSite());
        var wIn = Place(inBay, tecsy, 0, 5981, FighterHull(core), new Vector3(-50f, -400f, 0f), attachGeometry: true);
        wIn.Sector.Update(1.0);
        Assert.Equal((short)5981, wIn.Ship.StationId);

        // Within 200u of centre (dist 100) but OUTSIDE the bay → refused.
        var outBay = NewMission(new LatchingSite());
        var wOut = Place(outBay, tecsy, 0, 5991, FighterHull(core), new Vector3(0f, -100f, 0f), attachGeometry: true);
        wOut.Sector.Update(1.0);
        Assert.Null(wOut.Ship.StationId);
    }

    // ── respawn: FindNearestRestartStation (native FedSrv.CPP:12034-12038) ──

    private static (short restart, short noRestart) RestartTypeIds(StaticCore core)
    {
        var restart = core.StationTypes.Values.FirstOrDefault(t => t.HasCapability(DataStationTypeIGC.SabmRestart));
        var noRestart = core.StationTypes.Values.FirstOrDefault(t => !t.HasCapability(DataStationTypeIGC.SabmRestart));
        Assert.NotEqual(default, restart);
        Assert.NotEqual(default, noRestart);
        return ((short)restart.StationTypeId, (short)noRestart.StationTypeId);
    }

    private static void PlaceStation(Mission m, Sector sector, short id, short side, float x, float y, float z, short typeId)
    {
        var type = m.Core.StationTypes[typeId];
        var data = new DataStationIGC(
            Position: [x, y, z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: side, StationId: id, StationTypeId: typeId, BpHull: 1, BpShield: 1, Name: $"B{id}");
        m.PlaceStation(new Station(data, type), sector);
    }

    /// <summary>⭐ The NEAREST restart-capable station wins; a nearer friendly
    /// station WITHOUT restart is skipped (the pre-P550 code ignored the restart
    /// ability entirely).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Respawn_picks_nearest_restart_capable_station_skipping_one_without_restart()
    {
        Require.Artwork();
        var core = Core();
        var (restart, noRestart) = RestartTypeIds(core);
        var m = NewMission(new NullIgcSite());
        var sector = m.Sectors.First(s => s.Id == FlySector);
        // Ship dies at origin (side 0).
        PlaceStation(m, sector, 11, 0, 0f, 0f, 0f, noRestart);      // nearest but NO restart → must be skipped
        PlaceStation(m, sector, 12, 0, 200f, 0f, 0f, noRestart);    // nearer non-restart too
        PlaceStation(m, sector, 13, 0, 400f, 0f, 0f, restart);      // farthest but restart-capable → must win

        var found = Srv.FindNearestRestartStation(m, 0, FlySector, Vector3.Zero);
        Assert.True(found.HasValue);
        Assert.Equal((short)13, found.Value.StationId);
    }

    /// <summary>⭐ A nearer restart-capable station beats a farther restart-capable
    /// one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Respawn_picks_the_nearer_of_two_restart_stations()
    {
        Require.Artwork();
        var core = Core();
        var (restart, _) = RestartTypeIds(core);
        var m = NewMission(new NullIgcSite());
        var sector = m.Sectors.First(s => s.Id == FlySector);
        PlaceStation(m, sector, 21, 0, 500f, 0f, 0f, restart);   // far restart
        PlaceStation(m, sector, 22, 0, 100f, 0f, 0f, restart);   // near restart → wins
        PlaceStation(m, sector, 23, 1, 10f, 0f, 0f, restart);    // nearest but ENEMY side → excluded

        var found = Srv.FindNearestRestartStation(m, 0, FlySector, Vector3.Zero);
        Assert.True(found.HasValue);
        Assert.Equal((short)22, found.Value.StationId);
    }

    /// <summary>⭐ When no restart-capable friendly station exists anywhere,
    /// respawn is impossible — <c>null</c>, matching native's no-<c>pstationDest</c>
    /// no-respawn path (FedSrv.CPP:12055; the GetBase fallback is likewise
    /// c_sabmRestart-filtered, fsmission.cpp:1433-1444). Uses side 503 — a side
    /// that owns no real map station (the map's real side-0 garrison IS
    /// restart-capable, so side 0 would wrongly satisfy the GetBase fallback).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Respawn_returns_null_when_no_restart_capable_friendly_station_exists()
    {
        Require.Artwork();
        const short lonelySide = 503;
        var core = Core();
        var (_, noRestart) = RestartTypeIds(core);
        var m = NewMission(new NullIgcSite());
        var sector = m.Sectors.First(s => s.Id == FlySector);
        PlaceStation(m, sector, 31, lonelySide, 0f, 0f, 0f, noRestart);   // friendly but no restart

        Assert.Null(Srv.FindNearestRestartStation(m, lonelySide, FlySector, Vector3.Zero));
    }
}
