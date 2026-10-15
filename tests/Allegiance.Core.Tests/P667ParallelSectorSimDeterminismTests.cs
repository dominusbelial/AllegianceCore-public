using System.Buffers.Binary;
using System.Security.Cryptography;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P667 — the determinism gate for <c>sim.parallelSectorCells</c>. A crowded
/// single-sector battle (many ships on two sides with closing velocities, plus
/// ordnance so the combat pass fires) is run for hundreds of ticks with the
/// flag OFF and with it ON at worker counts 1 / 2 / 4 / 8, and the full per-tick
/// sim output — every ship's position/velocity/health/shields/energy/ammo/liveness,
/// every projectile/missile, every asteroid's ore/hull and every station's
/// armor/shield, plus the raised kill/damage/missile-destroy host events — is
/// captured as a byte digest each tick. The digest sequences must be identical
/// across all five configurations. This is the proof that the parallel
/// detection path (which only reorders the read-only broad/narrow pair testing)
/// produces byte-identical ticks to the serial path, independent of thread count.
/// </summary>
[Collection("LiveNetwork")]
public class P667ParallelSectorSimDeterminismTests
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

    private const int ShipCount = 160;
    private const int Ticks = 140;
    private const double Dt = 0.049;

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Alpha", civ.CivilizationId), new SideSpec(1, "Bravo", civ.CivilizationId)];
    }

    /// <summary>A site that counts the damage/kill/destroy host events the sim
    /// raises, so the gate covers "events" as well as ship state.</summary>
    private sealed class CountingSite : IIgcSite
    {
        public int KillShips;
        public int DamageShips;
        public int MissileDestroys;
        public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2) => KillShips++;
        public void DamageShipEvent(double now, IShipIgc ship, IModelIgc? launcher, byte type, float amount, float leakage, Vector3 p1, Vector3 p2) => DamageShips++;
        public void MissileDestroyedEvent(Missile m) => MissileDestroys++;
    }

    /// <summary>
    /// Assemble a deterministic, crowded, single-sector mission: two sides, a
    /// dense ball of ships with closing velocities (so ship-ship / ship-asteroid
    /// / ship-station elastic collisions fire constantly) plus a small salvo of
    /// projectiles (so the combat pass's detector runs too). Same seed every
    /// call ⇒ identical initial state, so digest sequences are comparable.
    /// </summary>
    private static (Mission Mission, CountingSite Site) BuildCrowdedSector(int seed)
    {
        var rng = new Random(seed);
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), new CountingSite());
        m.Stage = MissionStage.Started;
        var sector = m.Sectors.OrderByDescending(s => s.Mineable.Count).First(); // richest sector (asteroids + likely a station)

        var hull = RepresentativeCore.Value.HullTypes.Values.OrderBy(h => h.HullId).First(h => h.Hardpoints.Length > 0);
        var projectileTypes = RepresentativeCore.Value.ProjectileTypes.Values.Where(p => p.Power > 0f).ToList();

        short nextId = 9100;
        for (int i = 0; i < ShipCount; i++)
        {
            // Dense ball around the sector origin, alternating sides, closing
            // toward the centre so pairs are permanently colliding.
            double a = rng.NextDouble() * Math.PI * 2, b = rng.NextDouble() * Math.PI;
            float r = 40f + 850f * (float)rng.NextDouble();
            var pos = new Vector3(
                r * MathF.Sin((float)b) * MathF.Cos((float)a),
                r * MathF.Sin((float)b) * MathF.Sin((float)a),
                r * MathF.Cos((float)b));
            var vel = new Vector3(
                -pos.X * 0.055f + (float)(rng.NextDouble() * 20 - 10),
                -pos.Y * 0.055f + (float)(rng.NextDouble() * 20 - 10),
                -pos.Z * 0.055f + (float)(rng.NextDouble() * 20 - 10));
            var ship = new Ship(nextId++, hull, pos, vel, Orientation.Identity) { SideId = (short)(i % 2) };
            m.PlaceShip(ship, sector);
        }

        // A few lifepod ↔ rescue-capable ship clusters (same side, close together)
        // so the P115 rescue pass's one-per-ship latch actually ACCEPTS rescues
        // under the parallel branch — exercising the latch determinism, not just
        // the empty-qualifying-set scan.
        var rescueHulls = RepresentativeCore.Value.HullTypes.Values
            .Where(h => h.HasCapability(DataHullTypeIGC.HabmRescue)).ToList();
        if (rescueHulls.Count > 0)
        {
            var rescueHull = rescueHulls[0];
            var baseHull = RepresentativeCore.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
            var lifepodHull = baseHull with { HabmCapabilities = DataHullTypeIGC.HabmLifepod };
            for (int k = 0; k < 24; k++)
            {
                var centre = new Vector3(8f * k, 0f, 8f * (k % 7));   // a sparse chain of clusters
                var lifepod = new Ship(nextId++, lifepodHull, centre + new Vector3(0, 0, -2), new Vector3(1, 0, 0), Orientation.Identity) { SideId = 0 };
                var rescuer = new Ship(nextId++, rescueHull, centre + new Vector3(0, 0, 2), new Vector3(0, 0, 0), Orientation.Identity) { SideId = 0 };
                m.PlaceShip(lifepod, sector);
                m.PlaceShip(rescuer, sector);
            }
        }

        // A salvo of projectiles streaking through the crowd so the combat pass
        // (ordnance-present gate) runs its detector for the early ticks.
        if (projectileTypes.Count > 0)
        {
            var pt = projectileTypes[0];
            var launcher = sector.Models.FirstOrDefault(mm => mm is Ship) as Ship;
            if (launcher is not null)
                for (int k = 0; k < 24; k++)
                {
                    var dir = new Vector3(MathF.Cos(k * 0.7f), MathF.Sin(k * 0.4f), 0.3f).Normalize();
                    var p = new Projectile(nextId++, sector, pt, -dir * 900f, dir * pt.Speed, dir, m.LastUpdate, launcher);
                    sector.Add(p);
                }
        }

        return (m, (CountingSite)m.Site);
    }

    /// <summary>Byte digest of the whole world at the current instant: every
    /// model (ship/projectile/missile/probe) in sector order, every mineable
    /// asteroid and station runtime, plus the site's event tallies.</summary>
    private static byte[] Digest(Mission m, CountingSite site)
    {
        var buf = new MemoryStream();
        void W(float f) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, f); buf.Write(s); }
        void Wi(int i) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, i); buf.Write(s); }
        void Wb(byte b) => buf.WriteByte(b);

        foreach (var sector in m.Sectors)
        {
            Wi(sector.Models.Count);
            foreach (var model in sector.Models)
            {
                switch (model)
                {
                    case Ship sh:
                        Wb(1);
                        Wi(sh.Id);
                        W(sh.Position.X); W(sh.Position.Y); W(sh.Position.Z);
                        W(sh.Velocity.X); W(sh.Velocity.Y); W(sh.Velocity.Z);
                        W(sh.Fraction); W(sh.ShieldFraction); W(sh.Energy); W(sh.Ammo);
                        Wb(sh.Dead ? (byte)1 : (byte)0);
                        Wb(sh.StationId is null ? (byte)0 : (byte)1);
                        break;
                    case Projectile pr:
                        Wb(2);
                        W(pr.Position.X); W(pr.Position.Y); W(pr.Position.Z);
                        W(pr.Velocity.X); W(pr.Velocity.Y); W(pr.Velocity.Z);
                        break;
                    case Missile mi:
                        Wb(3);
                        W(mi.Position.X); W(mi.Position.Y); W(mi.Position.Z);
                        W(mi.Velocity.X); W(mi.Velocity.Y); W(mi.Velocity.Z);
                        break;
                    default:
                        Wb(0);
                        break;
                }
            }
            Wi(sector.Mineable.Count);
            foreach (var ast in sector.Mineable)
            {
                var p = ast.Data.Position;
                if (p is { Length: 3 }) { W(p[0]); W(p[1]); W(p[2]); }
                W(ast.Fraction);
            }
            Wi(sector.StationRuntimes.Count);
            foreach (var st in sector.StationRuntimes)
            {
                W(st.Center.X); W(st.Center.Y); W(st.Center.Z);
                W(st.ArmorFraction); W(st.ShieldFraction);
            }
        }
        Wi(site.KillShips); Wi(site.DamageShips); Wi(site.MissileDestroys);

        return SHA256.HashData(buf.ToArray());
    }

    /// <summary>Fresh build + run; returns the per-tick digest sequence.</summary>
    private static List<byte[]> RunDigests(bool enabled, int threads, int seed)
    {
        ParallelSectorSim.Enabled = enabled;
        ParallelSectorSim.ThreadCount = threads;
        try
        {
            var (m, site) = BuildCrowdedSector(seed);
            var digests = new List<byte[]>(Ticks);
            double t = m.LastUpdate;
            for (int i = 0; i < Ticks; i++)
            {
                t += Dt;
                m.Update(t);
                digests.Add(Digest(m, site));
            }
            return digests;
        }
        finally
        {
            ParallelSectorSim.Reset();
        }
    }

    private static void AssertSequencesEqual(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b, string label)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.True(a[i].SequenceEqual(b[i]),
                $"{label}: tick {i} digest differs ({Convert.ToHexString(a[i])} vs {Convert.ToHexString(b[i])})");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Crowded_sector_tick_output_is_byte_identical_flag_off_vs_on_all_thread_counts()
    {
        Require.Artwork();
        const int seed = 667667;
        var serial = RunDigests(enabled: false, threads: 1, seed);

        AssertSequencesEqual(serial, RunDigests(enabled: true, threads: 1, seed), "on t=1");
        AssertSequencesEqual(serial, RunDigests(enabled: true, threads: 2, seed), "on t=2");
        AssertSequencesEqual(serial, RunDigests(enabled: true, threads: 4, seed), "on t=4");
        AssertSequencesEqual(serial, RunDigests(enabled: true, threads: 8, seed), "on t=8");

        Console.WriteLine($"P667-DETERMINISM: {Ticks} ticks × {ShipCount} ships — serial == parallel (t=1/2/4/8), all tick digests byte-identical.");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Default_flag_is_off()
    {
        ParallelSectorSim.Reset();
        Assert.False(ParallelSectorSim.Enabled);
        Assert.True(ParallelSectorSim.ThreadCount >= 1);
    }

    /// <summary>
    /// The short battle-with-the-flag-on proof: with <c>sim.parallelSectorCells</c>
    /// ON, an enemy cluster (weak hulls) is riddled by aimed fire from the other
    /// side; the run must complete with real kills (KillShipEvent &gt; 0) and no
    /// exception — the parallel detection path sustains a real combat with
    /// ordinary death bookkeeping.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Combat_with_flag_on_produces_kills_and_runs_clean()
    {
        Require.Artwork();
        ParallelSectorSim.Enabled = true;
        ParallelSectorSim.ThreadCount = 4;
        try
        {
            var site = new CountingSite();
            var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), site);
            m.Stage = MissionStage.Started;
            var sector = m.Sectors.OrderByDescending(s => s.Mineable.Count).First();

            var core = RepresentativeCore.Value;
            var victimHull = core.HullTypes.Values.OrderBy(h => h.HullId)
                .First(h => h.Hardpoints.Length == 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var fighter = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => h.Hardpoints.Length > 0);
            var pt = core.ProjectileTypes.Values.First(p => p.Power > 0f);

            // 48 near-death enemy victims clustered at the origin.
            short id = 8810;
            var launcher = new Ship(id++, fighter, new Vector3(0, 0, 400f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            m.PlaceShip(launcher, sector);
            for (int k = 0; k < 48; k++)
            {
                var v = new Ship(id++, victimHull,
                    new Vector3((k % 8 - 4) * 22f, (k / 8 % 6 - 3) * 22f, (k / 48) * 10f - 30f),
                    Vector3.Zero, Orientation.Identity) { SideId = 1 };
                v.SetFraction(0.012f);   // a hair above dead: one hit kills
                m.PlaceShip(v, sector);
            }
            // Aimed fire: projectiles streaking -z through the cluster.
            for (int k = 0; k < 30; k++)
            {
                var dir = new Vector3((k % 5 - 2) * 8f, (k % 3 - 1) * 6f, -1f).Normalize();
                sector.Add(new Projectile(id++, sector, pt,
                    new Vector3(0, 0, 300f) + new Vector3((k % 5 - 2) * 8f, (k % 3 - 1) * 6f, 0),
                    dir * pt.Speed, dir, m.LastUpdate, launcher));
            }

            double t = m.LastUpdate;
            for (int i = 0; i < 120 && site.KillShips == 0; i++)
            {
                t += 0.038;
                m.Update(t);                       // throws on any parallel-path fault
                // Refresh fire so the pass stays hot (new rounds each few ticks).
                if (i % 4 == 0)
                    for (int k = 0; k < 10; k++)
                    {
                        var dir = new Vector3((k % 5 - 2) * 8f, (k % 3 - 1) * 6f, -1f).Normalize();
                        sector.Add(new Projectile(id++, sector, pt,
                            new Vector3(0, 0, 300f) + new Vector3((k % 5 - 2) * 8f, (k % 3 - 1) * 6f, 0),
                            dir * pt.Speed, dir, t, launcher));
                    }
            }

            Assert.True(site.KillShips > 0, $"expected kills with the flag ON, got {site.KillShips}");
            Console.WriteLine($"P667-COMBAT: flag ON, t=4 — {site.KillShips} kills, {site.DamageShips} damage events, no exceptions.");
        }
        finally
        {
            ParallelSectorSim.Reset();
        }
    }
}
