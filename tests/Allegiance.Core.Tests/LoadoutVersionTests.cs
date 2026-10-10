using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P317 — <c>Server.BroadcastShipStates</c> now gates a teammate's loadout
/// re-export on <see cref="Ship.LoadoutVersion"/>, a monotonic revision bumped
/// only when the equipped loadout actually mutates, instead of re-snapshotting
/// and re-comparing the parts every (recipient, subject) pair every tick (the
/// O(peers²) allocation wall). These tests pin the bump wiring at each mutation
/// path: a missed <c>BumpLoadout()</c> silently breaks the
/// loadout-change→re-export propagation that the behavioural
/// <c>TeammateLoadoutBroadcastTests</c> guards live.
///
/// Discriminating tests: revert any <c>BumpLoadout()</c> call at the exercised
/// mutation path → the corresponding "must bump" assertion goes red.
/// </summary>
public class LoadoutVersionTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc")));

    private static Ship NewShip()
    {
        var core = Core();
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        return new Ship(1, hull, new Vector3(10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fresh_ship_starts_at_version_zero()
    {
        Require.Artwork();
        Assert.Equal(0, NewShip().LoadoutVersion);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MountPart_and_ClearLoadout_monotonically_advance_the_version()
    {
        Require.Artwork();
        var core = Core();
        var anyPart = core.PartTypes.Values.First();
        var ship = NewShip();
        Assert.Equal(0, ship.LoadoutVersion);

        ship.MountPart(anyPart, 1, 0, ExpandedPartData.FullyMounted);
        long v1 = ship.LoadoutVersion;
        Assert.True(v1 > 0, "mounting a part must bump LoadoutVersion");

        ship.MountPart(anyPart, 1, 1, ExpandedPartData.FullyMounted);
        Assert.True(ship.LoadoutVersion > v1, "each mount must advance the version");

        long v2 = ship.LoadoutVersion;
        ship.ClearLoadout();
        Assert.True(ship.LoadoutVersion > v2, "clearing the loadout must bump LoadoutVersion");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TryRemovePart_advances_the_version()
    {
        Require.Artwork();
        var core = Core();
        var anyPart = core.PartTypes.Values.First();
        var ship = NewShip();
        ship.MountPart(anyPart, 1, 0, ExpandedPartData.FullyMounted);
        long v = ship.LoadoutVersion;

        Assert.True(ship.TryRemovePart(anyPart.EquipmentType, 0, out _));
        Assert.True(ship.LoadoutVersion > v, "removing a part must bump LoadoutVersion");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SetPartAmount_advances_the_version()
    {
        Require.Artwork();
        var core = Core();
        var anyPart = core.PartTypes.Values.First();
        var ship = NewShip();
        var mounted = ship.MountPart(anyPart, 1, 0, ExpandedPartData.FullyMounted);
        long v = ship.LoadoutVersion;

        ship.SetPartAmount(mounted!, 7);
        Assert.True(ship.LoadoutVersion > v, "changing a part's Amount must bump LoadoutVersion");
    }
}
