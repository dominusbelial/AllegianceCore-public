using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P1087RescueTests
{
    private const short Pod = DataHullTypeIGC.HabmLifepod;
    private const short Rescue = DataHullTypeIGC.HabmRescue;

    private sealed class RescueSite : IIgcSite
    {
        public readonly List<(Ship Pod, Ship Rescuer)> Calls = [];
        public Func<bool> Accept = () => true;

        public bool RescueShipEvent(IShipIgc pod, IShipIgc? rescuer)
        {
            Calls.Add(((Ship)pod, (Ship)rescuer!));
            return Accept();
        }
    }

    private static Mission World(IIgcSite? site = null)
    {
        var body = new byte[DataConstantsIGC.Size];
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = IgcWrapper.VersionAndDataSize, Version = 1, Body = body,
            BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, body.Length, 0)]
        });
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]);
        return Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []), [], site ?? new NullIgcSite());
    }

    private static Ship Add(Mission mission, short id, short bits)
    {
        var hull = default(DataHullTypeIGC) with { HabmCapabilities = bits, Length = 10, HitPoints = 100 };
        var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    [Fact]
    public void Fighter_fleet_skips_the_pair_scan()
    {
        using var mission = World();
        var sector = mission.Sectors[0];
        for (short i = 0; i < 400; i++) Add(mission, i, DataHullTypeIGC.HabmFighter | Rescue);
        Assert.False(sector.ResolveRescueEvents());
        Assert.Empty(sector.DrainRescues());
    }

    [Fact]
    public void Pod_needs_a_flying_non_pod_rescuer()
    {
        using var mission = World();
        var sector = mission.Sectors[0];
        Add(mission, 1, Pod);
        Add(mission, 2, Pod | Rescue);
        Add(mission, 3, 0);
        Add(mission, 4, Rescue).StationId = 0;
        Add(mission, 5, Rescue).SideId = null;
        Add(mission, 6, Rescue).SetFraction(0);
        Add(mission, 7, Rescue).Mission = null;
        Assert.False(sector.ResolveRescueEvents());
        Assert.Empty(sector.DrainRescues());
    }

    [Fact]
    public void Rescuer_at_sphere_boundary_picks_up_the_pod()
    {
        using var mission = World();
        var sector = mission.Sectors[0];
        var rescuer = Add(mission, 9, Rescue);
        var pod = Add(mission, 3, Pod);
        pod.Position = new(10, 0, 0);
        Assert.True(sector.ResolveRescueEvents());
        Assert.Equal((pod, rescuer), Assert.Single(sector.DrainRescues()));
    }

    [Fact]
    public void Competing_rescuers_keep_pair_order_refusals_and_latches()
    {
        var site = new RescueSite();
        using var mission = World(site);
        var sector = mission.Sectors[0];
        var p1 = Add(mission, 9, Pod);
        var r1 = Add(mission, 5, Rescue);
        var r2 = Add(mission, 7, Rescue);
        var p2 = Add(mission, 2, Pod);
        site.Accept = () => site.Calls.Count != 1;
        Assert.True(sector.ResolveRescueEvents());
        Assert.Equal([(p1, r1), (p1, r2), (p2, r1)], site.Calls);
        Assert.Equal([(p1, r2), (p2, r1)], sector.DrainRescues());
    }

    [Fact]
    public void Nested_pass_keeps_the_outer_candidate_snapshot()
    {
        var site = new RescueSite();
        using var mission = World(site);
        var sector = mission.Sectors[0];
        var p1 = Add(mission, 1, Pod);
        var r1 = Add(mission, 2, Rescue);
        var p2 = Add(mission, 3, Pod);
        var r2 = Add(mission, 4, Rescue);
        site.Accept = () =>
        {
            if (site.Calls.Count == 1)
            {
                sector.Remove(p1);
                sector.Remove(p2);
                Assert.False(sector.ResolveRescueEvents());
            }
            return true;
        };
        sector.ResolveRescueEvents();
        Assert.Equal([(p1, r1), (p2, r2)], sector.DrainRescues());
    }

    [Fact]
    public void Throwing_site_does_not_leave_candidates_in_the_next_pass()
    {
        var site = new RescueSite { Accept = () => throw new InvalidOperationException() };
        using var mission = World(site);
        var sector = mission.Sectors[0];
        var departed = Add(mission, 1, Pod);
        var rescuer = Add(mission, 2, Rescue);
        Assert.Throws<InvalidOperationException>(() => sector.ResolveRescueEvents());
        sector.Remove(departed);
        var replacement = Add(mission, 3, Pod);
        site.Accept = () => true;
        sector.ResolveRescueEvents();
        Assert.Equal((replacement, rescuer), Assert.Single(sector.DrainRescues()));
    }
}
