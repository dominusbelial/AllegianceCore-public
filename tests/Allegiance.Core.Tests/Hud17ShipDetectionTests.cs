using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-17 — the own-side <c>ShipStatus::GetDetected()</c> rule
/// (<see cref="ShipDetection.IsDetectedByEnemy"/>), the port of
/// <c>FedSrv/fsship.cpp:237-335</c>:
/// <list type="bullet">
/// <item>true iff a side that is neither the ship's own side nor allied has a
/// known (spotted) status for it (<c>fsship.cpp:263-265,306-318</c>);</item>
/// <item>a docked ship is not detected — docking leaves the cluster
/// (<c>Igc/shipIGC.h:421-457</c>, <c>FedSrv.CPP:10711-10746</c>);</item>
/// <item>a boarded child copies its carrier's value
/// (<c>fsship.cpp:268-284,321-334</c>).</item>
/// </list>
/// The port's "status not unknown" flag is the P132 sweep's latched
/// <see cref="Model.SeenBySide"/> (the same mapping P493 uses), so these tests
/// set it directly and do not need a scanner pass.
/// </summary>
public class Hud17ShipDetectionTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private const short Side0 = 0;
    private const short Side1 = 1;

    private static Mission StartedMission()
    {
        var core = RepresentativeCore.Value;
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> specs =
        [
            new SideSpec(Side0, "Team 1", civs[0].CivilizationId),
            new SideSpec(Side1, "Team 2", civs[1].CivilizationId),
        ];
        var m = Mission.Assemble(core, SomeMap.Value, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewShip(Mission m, short side)
    {
        var hull = RepresentativeCore.Value.HullTypes.Values.First(h => h.HitPoints > 0f);
        return new Ship(4001, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        {
            SideId = side,
            Mission = m,
        };
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Detected_is_true_only_while_a_non_own_non_allied_side_sees_the_ship()
    {
        Require.Artwork();
        var m = StartedMission();
        var ship = NewShip(m, Side0);

        // Nobody sees it: false (the old server sent true unconditionally).
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));

        // An enemy side's known (spotted) status: true — fsship.cpp:263-265.
        ship.SideVisibility.SetSideVisibility(Side1, true);
        Assert.True(ShipDetection.IsDetectedByEnemy(ship));

        // Losing sight: false — fsship.cpp:306-318 recomputes from every
        // remaining side's !unknown status.
        ship.SideVisibility.SetSideVisibility(Side1, false);
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));

        // The ship's OWN side seeing it never sets the bit (fsship.cpp:306).
        ship.SideVisibility.SetSideVisibility(Side0, true);
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Allied_side_spot_does_not_count_as_detected()
    {
        Require.Artwork();
        var m = StartedMission();
        var ship = NewShip(m, Side0);
        m.Sides.First(s => s.Id == Side0).AllyGroup = 0;
        m.Sides.First(s => s.Id == Side1).AllyGroup = 0;   // same non-null group == allied

        // The ally's scanner sees the ship, but fsship.cpp:263/306 skips allies.
        ship.SideVisibility.SetSideVisibility(Side1, true);
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));

        // Breaking the alliance makes the same spot count.
        m.Sides.First(s => s.Id == Side1).AllyGroup = 1;
        Assert.True(ShipDetection.IsDetectedByEnemy(ship));

        // A null group on both sides is NOT an alliance (NA, igc.h:471).
        m.Sides.First(s => s.Id == Side0).AllyGroup = null;
        m.Sides.First(s => s.Id == Side1).AllyGroup = null;
        Assert.True(ShipDetection.IsDetectedByEnemy(ship));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Docked_and_sideless_and_unplaced_ships_are_not_detected()
    {
        Require.Artwork();
        var m = StartedMission();
        var ship = NewShip(m, Side0);
        ship.SideVisibility.SetSideVisibility(Side1, true);

        // Docked == left the cluster (shipIGC.h:421-457): no enemy can see it.
        ship.StationId = 1;
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));
        ship.StationId = null;
        Assert.True(ShipDetection.IsDetectedByEnemy(ship));

        // A sideless ship has no own-side copy to mark.
        ship.SideId = null;
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));
        ship.SideId = Side0;

        // An unplaced ship has no mission side universe.
        ship.Mission = null;
        Assert.False(ShipDetection.IsDetectedByEnemy(ship));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Malformed_parent_chain_does_not_recurse_forever()
    {
        Require.Artwork();
        var m = StartedMission();
        var a = NewShip(m, Side0);
        var b = NewShip(m, Side0);

        // A hand-built fixture can self-parent (or form a two-ship cycle); the
        // native carrier tree cannot, so the guard must simply return false
        // rather than overflow the stack (found by the fast-tier run).
        a.ParentShip = a;
        Assert.False(ShipDetection.IsDetectedByEnemy(a));
        a.ParentShip = b;
        b.ParentShip = a;
        Assert.False(ShipDetection.IsDetectedByEnemy(a));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Boarded_child_copies_its_carriers_detected_bit()
    {
        Require.Artwork();
        var m = StartedMission();
        var carrier = NewShip(m, Side0);
        var child = NewShip(m, Side0);
        child.ParentShip = carrier;

        // Child's own visibility is irrelevant while boarded (fsship.cpp:268-284).
        child.SideVisibility.SetSideVisibility(Side1, false);
        carrier.SideVisibility.SetSideVisibility(Side1, false);
        Assert.False(ShipDetection.IsDetectedByEnemy(child));

        carrier.SideVisibility.SetSideVisibility(Side1, true);
        Assert.True(ShipDetection.IsDetectedByEnemy(child));

        // A docked carrier is not detected, so its child is not either.
        carrier.StationId = 1;
        Assert.False(ShipDetection.IsDetectedByEnemy(child));
    }
}
