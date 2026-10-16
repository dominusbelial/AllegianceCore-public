using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P699bPodNavigationTests
{
    private static DataWarpIGC Mouth(short id, short partner, short sector, float x) =>
        new(id, partner, 100, "", "", "", [x, 0, 0], [0, 0, -1], [0, 0, 0, 1], 1, sector);

    [Fact]
    public void Equal_hop_routes_take_nearest_mouth_instead_of_lowest_sector_id()
    {
        var warps = new[] { Mouth(1, 2, 10, -4000), Mouth(2, 1, 11, 0), Mouth(3, 4, 11, 0), Mouth(4, 3, 20, 0),
            Mouth(5, 6, 10, 100), Mouth(6, 5, 12, 0), Mouth(7, 8, 12, 0), Mouth(8, 7, 20, 0) }
            .ToDictionary(w => w.WarpId);
        Assert.Equal((short)5, PodReturnNavigation.NextWarp(warps, 10, 20, Vector3.Zero)!.Value.WarpId);
        Assert.Equal((short)1, PodReturnNavigation.NextWarp(warps, 10, 20, new(-3990, 0, 0))!.Value.WarpId);
        // Near entry can have an expensive intermediate crossing and far home exit.
        // The station-aware route must account for both, rather than the first mouth alone.
        warps[6] = warps[6] with { Position = [8000, 0, 0] };
        Assert.Equal((short)1, PodReturnNavigation.NextWarp(warps, 10, 20, Vector3.Zero, Vector3.Zero)!.Value.WarpId);
        warps[6] = warps[6] with { Position = [0, 0, 0] };
        warps[8] = warps[8] with { Position = [8000, 0, 0] };
        Assert.Equal((short)1, PodReturnNavigation.NextWarp(warps, 10, 20, Vector3.Zero, Vector3.Zero)!.Value.WarpId);
                Assert.Null(PodReturnNavigation.NextWarp(warps, 10, 99, Vector3.Zero));
        Assert.Null(PodReturnNavigation.NextWarp(warps, 20, 20, Vector3.Zero));
        // A direct path wins over a nearby detour. Missing partner is never traversed.
        warps.Add(9, Mouth(9, 10, 10, 9000)); warps.Add(10, Mouth(10, 9, 20, 0));
        warps.Add(11, Mouth(11, 99, 10, 0));
        Assert.Equal((short)9, PodReturnNavigation.NextWarp(warps, 10, 20, Vector3.Zero)!.Value.WarpId);
    }

    [Fact]
    public void A_short_three_jump_flight_beats_a_long_two_jump_sector_crossing()
    {
        var warps = new[] { Mouth(1, 2, 10, 0), Mouth(2, 1, 11, 0), Mouth(3, 4, 11, 10000), Mouth(4, 3, 20, 0),
            Mouth(5, 6, 10, 100), Mouth(6, 5, 12, 0), Mouth(7, 8, 12, 100), Mouth(8, 7, 13, 0),
            Mouth(9, 10, 13, 100), Mouth(10, 9, 20, 0) }.ToDictionary(w => w.WarpId);
        Assert.Equal((short)1, PodReturnNavigation.NextWarp(warps, 10, 20, Vector3.Zero)!.Value.WarpId);
        Assert.Equal((short)5, PodReturnNavigation.NextWarp(warps, 10, 20, Vector3.Zero, Vector3.Zero)!.Value.WarpId);
    }

    [Theory]
    [InlineData(10, 100, 1)]
    [InlineData(80, 100, .5f)]
    [InlineData(250, 100, .25f)]
    public void Stopping_distance_matches_integrated_native_drag_equation(float speed, float maxSpeed, float back)
    {
        const float thrust = 600, mass = 10;
        double dt = .00001, s = speed, distance = 0;
        double k = thrust / (mass * (double)maxSpeed), acceleration = thrust * back / mass;
        while (s > 0) { distance += s * dt; s -= (k * s + acceleration) * dt; }
        Assert.InRange(Math.Abs(DockApproach.StoppingDistance(speed, thrust, mass, maxSpeed, back) - distance), 0, .003);
        Assert.Equal(0, DockApproach.StoppingDistance(0, thrust, mass, maxSpeed, back));
    }

    [Fact]
    public void Bay_ties_spread_without_overriding_the_nearest_approach_or_reserving_a_slot()
    {
        DockApproach.DockBay[] bays = [new(new(-10, 0, 0), new(0, 0, 1)), new(new(10, 0, 0), new(0, 0, 1))];
        int Pick(float x, int seed) => DockApproach.DockStation(new(x, 0, 100), Vector3.Zero, Orientation.Identity,
            Vector3.Zero, bays, 2, 0, new(x, 0, 100), 50, 0, 0, false, seed).BayIndex;
        Assert.Equal(0, Pick(0, 0)); Assert.Equal(1, Pick(0, 1));
        Assert.Equal(0, Pick(-9, 1)); Assert.Equal(1, Pick(9, 0));
        Assert.Equal(100, Enumerable.Range(0, 200).Count(id => Pick(0, id) == 0));
    }
}
