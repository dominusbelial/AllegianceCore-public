using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P868DefaultOrderTests
{
    private sealed class OrderSite(bool handles) : IIgcSite
    {
        public List<IShipIgc> Requests { get; } = [];
        public bool HandlePickDefaultOrder(IShipIgc ship)
        {
            Requests.Add(ship);
            if (handles) ((Ship)ship).SetGoto(new(GotoKind.Waypoint, 0, new Vector3(10000, 0, 0)));
            return handles;
        }
    }

    // Native Igc/shipIGC.h:2438-2447: the callback gets first refusal;
    // false continues to the ordinary nearest-enemy decision.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Host_override_precedes_default_enemy_acquisition(bool handles)
    {
        Require.Artwork();
        var site = new OrderSite(handles);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var civ = core.Civilizations.Keys.First();
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new(0, "Alpha", civ), new(1, "Beta", civ)], site);
        mission.Stage = MissionStage.Started;
        var hull = core.HullTypes.Values.First() with
        {
            Thrust = 0, HitPoints = 1000, Length = 10, ScannerRange = 10000,
            HabmCapabilities = 0, MaxAmmo = 1000, MaxEnergy = 1000,
        };
        var wingman = new Ship(100, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        var enemy = new Ship(101, hull, new(0, 0, -5000), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(wingman, 0);
        mission.PlaceShip(enemy, 0);
        enemy.SideVisibility.SetSideVisibility(0, true);
        mission.Update(.05);
        Assert.Same(wingman, Assert.Single(site.Requests));
        if (handles)
        {
            Assert.Null(wingman.AttackTarget);
            Assert.NotNull(wingman.GotoWaypoint);
        }
        else Assert.Same(enemy, wingman.AttackTarget);
        mission.Update(.10);
        Assert.Single(site.Requests); // a committed order is no longer idle
    }
}
