using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P832cFlagVisibilityTests
{
    [Fact]
    public void Carrier_is_visible_in_same_sector_despite_range_signature_and_occlusion()
    {
        VisibilityMath.SightBody eye = new(Vector3.Zero, 1);
        VisibilityMath.SightBody target = new(new Vector3(100_000, 0, 0), 1);
        VisibilityMath.SightBody[] rocks = [new(new Vector3(50_000, 0, 0), 20_000)];
        Assert.False(VisibilityMath.InScannerRange(eye, 1, 0, target, 0, 0, rocks));
        Assert.True(VisibilityMath.InScannerRange(eye, 1, 0, target, 0, 0, rocks, targetCarriesFlag: true));
        Assert.False(VisibilityMath.InScannerRange(eye, 1, 0, target, 0, 1, rocks, targetCarriesFlag: true));
        Assert.False(VisibilityMath.InScannerRange(eye, 1, null, target, 0, null, rocks, targetCarriesFlag: true));
    }
    // P858 — the last-spotter short-circuit is unconditional now (no cache flag).
    [SkippableFact]
    public void Production_sweep_tracks_pickup_drop_and_sector_boundaries()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        var mission = Mission.Assemble(core, map, [new(0, "Alpha", 25), new(1, "Bravo", 3)], new NullIgcSite());
        var sector = mission.Sectors[0];
        var scanner = new Ship(29999, core.HullTypes.Values.First(), Vector3.Zero,
            Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(scanner, sector);
        var ship = new Ship(30000, core.HullTypes.Values.First(), new Vector3(100_000, 0, 0),
            Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(ship, sector);
        {
            sector.ResolveCrossSideVisibility();
            Assert.False(ship.SeenBySide(0));
            ship.Flag = 0;
            sector.ResolveCrossSideVisibility();
            Assert.True(ship.SeenBySide(0));
            ship.Flag = null;
            sector.ResolveCrossSideVisibility();
            Assert.False(ship.SeenBySide(0));
            // The bypass requires an actual scanner of that side in this sector.
            var empty = mission.Sectors.First(s => s != sector && s.StationRuntimes.Count == 0);
            mission.PlaceShip(ship, empty);
            ship.Flag = 0;
            empty.ResolveCrossSideVisibility();
            Assert.False(ship.SeenBySide(0));
        }
    }

}
