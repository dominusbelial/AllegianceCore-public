using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P491 — an armed probe (one with a real <c>ProjectileTypeId</c>) actually
/// acquires and fires at hostile models in its sector: the port of
/// <c>CprobeIGC::Update</c>'s weapon branch (<c>probeigc.cpp:352-529</c>) into
/// <see cref="Probe"/>. These are the fail-first SIM tests; the same behaviour
/// is live-proven end-to-end in <see cref="ArmedProbeLiveTests"/> (real
/// projectile spawn + real damage + real <c>PROJECTILE_INSTANCE</c> broadcast
/// + ammo decrement through the P435 <c>DeployProbe</c> dispenser seam).
/// <para>The firing is driven by <see cref="Probe.Update"/> (the sector model
/// tick → <c>OnUpdate</c> → <c>FireIfArmed</c>), so each test walks the probe's
/// clock forward past the quick-ready <c>_nextFire</c> (5s for a
/// <c>c_eabmQuickReady</c> probe, <c>probeigc.cpp:137-139</c>) and observes the
/// spawned <see cref="Projectile"/>s in the sector. A hostile target is a
/// same-sector ship on a different side — or a sideless ship, which fights
/// everyone (the port's standing convention). The armed probe type used is the
/// real PCore001 'Plas Gen 1' (id 30): quick-ready, <c>c_eabmShootShips</c>,
/// ammo 5, dispersion 0, accuracy 1 — chosen so firing, lead and ammo decrement
/// are all deterministic and observable.</para>
/// </summary>
public class ArmedProbeFiringTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            if (!System.IO.Path.GetFileName(path).Equals("PCore001.igc", System.StringComparison.OrdinalIgnoreCase))
                continue;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("PCore001 not found in corpus");
    });

    private static readonly Lazy<MissionMap> Map = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            if (!System.IO.Path.GetFileName(path).Equals("XXV_simple.igc", System.StringComparison.OrdinalIgnoreCase))
                continue;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("XXV_simple.igc not found in corpus");
    });

    /// <summary>A started two-sided mission (the armed probe's side plus the
    /// enemy's), with identity GAs.</summary>
    private static Mission NewMission(out SideSpec sideA, out SideSpec sideB)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        sideA = new SideSpec(0, "Team A", civ.CivilizationId);
        sideB = new SideSpec(1, "Team B", civ.CivilizationId);
        var m = Mission.Assemble(Core.Value, Map.Value, [sideA, sideB], new NullIgcSite());
        m.Stage = MissionStage.Started;
        // Seed identity GAs on both sides (Mission.Assemble does not default to
        // identity — P479's NewMission seeds explicitly): a zero SpeedAmmo would
        // make the armed probe's effective speed 0, shrinking its reach to nil.
        foreach (var s in m.Sides) s.GlobalAttributes = GlobalAttributeSet.Identity;
        return m;
    }

    private static DataHullTypeIGC Hull()
        => Core.Value.HullTypes.Values.First(h => h.HitPoints > 0f && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    /// <summary>The real armed probe type PCore001: 'Plas Gen 1' (id 30) —
    /// quick-ready, c_eabmShootShips, ammo 5.</summary>
    private static DataProbeTypeIGC PlasGen() => Core.Value.ProbeTypes[30];

    private static Ship Ship(Mission m, short id, Vector3 pos, short? sideId)
        => new(id, Hull(), pos, Vector3.Zero, Orientation.Identity) { Mission = m, SideId = sideId };

    /// <summary>Walk the probe's clock forward in <c>step</c> increments up to
    /// <paramref name="until"/> (sim seconds), calling <see cref="Probe.Update"/>
    /// each step (the same advance the sector's model loop performs), stopping
    /// early when <paramref name="stop"/> returns true.</summary>
    private static void Walk(Probe probe, double step, double until, Func<bool>? stop = null)
    {
        double now = 0.0;
        for (; now < until; now += step)
        {
            probe.Update(now + step);
            if (stop is { } s && s()) return;
        }
    }

    [Fact]
    public void Armed_probe_fires_at_hostile_ship_with_ammo_decrement()
    {
        var m = NewMission(out _, out _);
        var sector = m.Sectors[0];
        short sideA = m.Sides[0].Id;

        var deployer = Ship(m, 9100, Vector3.Zero, sideA);   // launcher: mission + side (armed probe seam)
        var probe = new Probe(9200, sector, PlasGen(), Vector3.Zero, now: 0.0, sideA, deployer);
        sector.Add(probe);
        Assert.True(probe.IsArmed, $"probe must be armed (eabm=0x{PlasGen().EabmCapabilities:X4})");

        // Sideless enemy 50 m away in +z — hostile to the side-A probe
        // (fights everyone), comfortably inside the ~208 m Plas Gen reach.
        var enemy = Ship(m, 9300, new Vector3(0f, 0f, 50f), sideId: null);
        sector.Add(enemy);

        // Tick past the 5s quick-ready delay; a round must be spawned and ammo
        // decremented (5 → 4) on the first shot.
        Walk(probe, step: 0.025, until: 6.0,
            stop: () => probe.Ammo < PlasGen().Ammo);

        Assert.True(probe.Ammo < PlasGen().Ammo, "armed probe must spend ammo firing at a hostile ship");
        var round = sector.Models.OfType<Projectile>().FirstOrDefault(p => p.ProjectileType.ProjectileTypeId == PlasGen().ProjectileTypeId);
        Assert.NotNull(round);
        // The round is a real model in the sector at the probe's position, aimed
        // at the enemy in +z with the effective decoded speed (Plas Gen speed
        // 1000; ammo probe → no GA scaling on identity).
        Assert.Equal(probe.Position, round.Position);
        Assert.True(MathF.Abs(round.Velocity.Z - 1000f) < 1f, $"round velocity {round.Velocity} should point at the enemy (+z, speed 1000)");
        Assert.Equal(PlasGen().ProjectileTypeId, round.ProjectileType.ProjectileTypeId);
        Console.WriteLine($"P491-UNIT: armed probe fired round vel {round.Velocity}, ammo {probe.Ammo}/5");
    }

    [Fact]
    public void Armed_probe_ignores_same_side_ship()
    {
        var m = NewMission(out _, out _);
        var sector = m.Sectors[0];
        short sideA = m.Sides[0].Id;

        var deployer = Ship(m, 9110, Vector3.Zero, sideA);
        var probe = new Probe(9210, sector, PlasGen(), Vector3.Zero, now: 0.0, sideA, deployer);
        sector.Add(probe);

        // Only a SAME-SIDE ship is in range — the probe must NOT fire (the
        // hostile check, probeigc.cpp:268, rejects it).
        var ally = Ship(m, 9310, new Vector3(0f, 0f, 50f), sideA);
        sector.Add(ally);

        Walk(probe, step: 0.025, until: 8.0);

        Assert.Empty(sector.Models.OfType<Projectile>());
        Assert.Equal(PlasGen().Ammo, probe.Ammo);   // no ammo spent
    }

    [Fact]
    public void Unarmed_probe_never_fires()
    {
        var m = NewMission(out _, out _);
        var sector = m.Sectors[0];
        short sideA = m.Sides[0].Id;

        var deployer = Ship(m, 9120, Vector3.Zero, sideA);
        // A real unarmed (sensor) probe: no projectile type.
        var unarmedType = Core.Value.ProbeTypes.Values.First(p => p.ProjectileTypeId <= 0);
        var probe = new Probe(9220, sector, unarmedType, Vector3.Zero, now: 0.0, sideA, deployer);
        sector.Add(probe);
        var enemy = Ship(m, 9320, new Vector3(0f, 0f, 50f), sideId: null);
        sector.Add(enemy);

        Walk(probe, step: 0.025, until: 10.0);

        Assert.False(probe.IsArmed);
        Assert.Empty(sector.Models.OfType<Projectile>());
        Assert.Equal(unarmedType.Ammo, probe.Ammo);
    }

    [Fact]
    public void Armed_probe_self_destructs_when_ammo_exhausted()
    {
        var m = NewMission(out _, out _);
        var sector = m.Sectors[0];
        short sideA = m.Sides[0].Id;

        var deployer = Ship(m, 9130, Vector3.Zero, sideA);
        var probe = new Probe(9230, sector, PlasGen(), Vector3.Zero, now: 0.0, sideA, deployer);
        sector.Add(probe);
        var enemy = Ship(m, 9330, new Vector3(0f, 0f, 50f), sideId: null);
        sector.Add(enemy);

        // Drive far past the ammo pool: Plas Gen has 5 rounds at ~0.5s cadence,
        // so by ~8s it is exhausted (ammo 0) and removes itself (probeigc.cpp:513-521).
        Walk(probe, step: 0.025, until: 12.0,
            stop: () => !sector.Models.Contains(probe));

        Assert.True(!sector.Models.Contains(probe), "an armed probe must self-destruct when its ammo runs out");
        Assert.Equal((short)0, probe.Ammo);
    }

    [Fact]
    public void Fired_round_uses_the_P479_effective_speed_and_lifespan_seam()
    {
        // An AMMO probe on an identity-GA side flies at the raw decoded speed and
        // lifespan (EffectiveProjectileSpeed/Lifespan — the P479 seam this firing
        // builds from, probeigc.cpp:366-368 / probeigc.h:223-233).
        var m = NewMission(out _, out _);
        var sector = m.Sectors[0];
        short sideA = m.Sides[0].Id;

        var deployer = Ship(m, 9140, Vector3.Zero, sideA);
        var probe = new Probe(9240, sector, PlasGen(), Vector3.Zero, now: 0.0, sideA, deployer);
        sector.Add(probe);
        var enemy = Ship(m, 9340, new Vector3(0f, 0f, 50f), sideId: null);
        sector.Add(enemy);

        var proj = Core.Value.ProjectileTypes[PlasGen().ProjectileTypeId];
        Walk(probe, step: 0.025, until: 6.0,
            stop: () => sector.Models.OfType<Projectile>().Any());

        var round = sector.Models.OfType<Projectile>().FirstOrDefault(p => p.ProjectileType.ProjectileTypeId == PlasGen().ProjectileTypeId);
        Assert.NotNull(round);
        // The round's speed equals the P479 effective speed (identity GA ammo
        // probe → raw decoded speed 1000), and the effective lifespan matches
        // the raw (energy probes would scale via c_gaLifespanEnergy instead).
        Assert.Equal(probe.EffectiveProjectileSpeed(proj), round.Velocity.Length(), 4);
        Assert.Equal(proj.Speed, probe.EffectiveProjectileSpeed(proj), 4);
        Assert.Equal(proj.Lifespan, probe.EffectiveProjectileLifespan(proj), 4);
        Console.WriteLine($"P491-UNIT: ammo-probe round speed {probe.EffectiveProjectileSpeed(proj):F1} (raw {proj.Speed}), life {probe.EffectiveProjectileLifespan(proj):F3} (raw {proj.Lifespan})");
    }

    [Fact]
    public void ExportProbe_carries_the_persisted_target_on_otTarget_oidTarget()
    {
        var m = NewMission(out _, out _);
        var sector = m.Sectors[0];
        short sideA = m.Sides[0].Id;

        var deployer = Ship(m, 9150, Vector3.Zero, sideA);
        var probe = new Probe(9250, sector, PlasGen(), Vector3.Zero, now: 0.0, sideA, deployer);
        var targetShip = Ship(m, 9350, new Vector3(0f, 0f, 100f), sideId: null);
        probe.Target = targetShip;

        // With a persisted target: otTarget = OT_ship (0), oidTarget = the ship's id.
        var withTarget = ExportProbe.FromProbe(probe, timeCreate: 1u, launcherId: deployer.Id, sideId: sideA);
        Assert.Equal((short)0, withTarget.Probe.OtTarget);
        Assert.Equal(targetShip.Id, withTarget.Probe.OidTarget);

        // Without a target (the normal free-targeting case): NA (-1), exactly
        // native probeigc.cpp:558-562.
        probe.Target = null;
        var targetless = ExportProbe.FromProbe(probe, timeCreate: 1u, launcherId: deployer.Id, sideId: sideA);
        Assert.Equal((short)-1, targetless.Probe.OtTarget);
        Assert.Equal((short)-1, targetless.Probe.OidTarget);
    }
}
