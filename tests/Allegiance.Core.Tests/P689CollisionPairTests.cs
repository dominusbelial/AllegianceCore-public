using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P689CollisionPairTests
{
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(30)]
    [InlineData(150)]
    public void Filtered_combat_events_equal_original_dispatch_including_times_shapes_and_ties(int count)
    {
        Require.Artwork();
        var sector = new Sector(DataClusterIGC.Decode(new byte[DataClusterIGC.Size]));
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]);
        object[] bodies =
        [
            new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
            new Projectile(2, sector, DataProjectileTypeIGC.Decode(new byte[DataProjectileTypeIGC.Size]),
                Vector3.Zero, Vector3.Zero, new Vector3(0, 0, 1), 0),
            new Missile(3, sector, DataMissileTypeIGC.Decode(new byte[DataMissileTypeIGC.Size]),
                Vector3.Zero, Vector3.Zero, new Vector3(0, 0, 1), 0),
            new Probe(4, sector, DataProbeTypeIGC.Decode(new byte[DataProbeTypeIGC.Size]), Vector3.Zero, 0, 0),
            new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]), DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size])),
            new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size])),
            "ignored model"
        ];
        var art = TestArtRoot.Value;
        var hulls = new[] { "fig07", "faohshbmb", "ss24a" }
            .Select(name => CvhReader.Parse(File.ReadAllText(Path.Combine(art, name + ".cvh")))).ToArray();
        var rng = new Random(689);
        var detector = new CollisionDetector();
        for (int i = 0; i < count; i++)
        {
            // Shared origins force exact ties; spread and moving hulls exercise
            // misses, nonzero contact times, and multi-hull sub-shapes too.
            var pos = i < 7 ? Vector3.Zero : new Vector3(rng.Next(-100, 101), rng.Next(-100, 101), rng.Next(-100, 101));
            var vel = i < 7 ? Vector3.Zero : new Vector3(rng.Next(-200, 201), rng.Next(-200, 201), rng.Next(-200, 201));
            detector.Add(new Collider(bodies[i % bodies.Length], pos, vel, Orientation.Identity,
                hulls[i % hulls.Length], 20, false));
        }

        // The old combat dispatch discovers ordnance and a supported target
        // only AFTER Detect. Keep this reference independent of CombatPair.
        static bool OldDispatch(Collider a, Collider b)
        {
            var ord = a.Data as IOrdnance ?? b.Data as IOrdnance;
            var ship = a.Data as Ship ?? b.Data as Ship;
            var probe = a.Data as Probe ?? b.Data as Probe;
            var station = a.Data as Station ?? b.Data as Station;
            var asteroid = a.Data as Asteroid ?? b.Data as Asteroid;
            return ord is not null && (ship is not null || probe is not null || station is not null || asteroid is not null);
        }
        var all = detector.Detect(.05f).ToArray();
        var expected = all.Where(e => OldDispatch(e.A, e.B)).ToArray();
        var actual = detector.Detect(.05f, Sector.CombatPair).ToArray();
        Assert.NotEmpty(expected);
        Assert.True(all.Length > actual.Length, "the fixture must contain work that combat discards");
        Assert.Equal(expected, actual);
        foreach (int threads in new[] { 1, 2, 4, 8 })
            Assert.Equal(expected, detector.Detect(.05f, true, threads, Sector.CombatPair));

        var candidates = BroadPhase.OverlappingPairsBruteForce(detector.Colliders)
            .Where(p => OldDispatch(detector.Colliders[p.I], detector.Colliders[p.J])).ToArray();
        Assert.Equal(candidates, BroadPhase.OverlappingPairs(detector.Colliders, Sector.CombatPair).ToArray());
        Assert.True(candidates.Length < detector.CandidatePairs);
    }

    [Fact]
    public void Broad_phase_filter_uses_registration_order_and_preserves_original_pair_order()
    {
        var detector = new CollisionDetector();
        // Reversed spatial/registration order exposes accidentally calling an
        // asymmetric predicate in sweep order instead of registration order.
        for (int i = 0; i < 40; i++)
            detector.Add(new Collider(i, new Vector3(40 - i, 0, 0), Vector3.Zero,
                Orientation.Identity, null, 100, false));
        detector.Detect(.05f);
        static bool Filter(Collider a, Collider b) => (int)a.Data! < (int)b.Data! && a.Ordinal % 2 == 0;
        var expected = BroadPhase.OverlappingPairsBruteForce(detector.Colliders)
            .Where(p => Filter(detector.Colliders[p.I], detector.Colliders[p.J])).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, BroadPhase.OverlappingPairs(detector.Colliders, Filter).ToArray());
        Assert.Empty(detector.Detect(.05f, static (_, _) => false));
        Assert.Equal(780, detector.Detect(.05f).Count); // default API still tests all pairs
    }
}
