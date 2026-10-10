using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P479 — the last unwired side-global-attribute (GA) consumers: the ripcord
/// countdown (<c>c_gaRipcordTime = 20</c>) and the armed-probe projectile seam
/// (<c>c_gaSpeedAmmo = 14</c>, <c>c_gaLifespanEnergy = 15</c>).
/// <para><b>Ripcord.</b> Native <c>MyHullType::GetRipcordSpeed</c>
/// (<c>shipIGC.cpp:4665</c>) returns <c>ripcordSpeed /
/// GetAttribute(c_gaRipcordTime)</c>; the port's <see cref="Ship.RipcordSpeed"/>
/// is that GA-effective value, and every ripcord consumer (the countdown seed
/// <see cref="Ship.ResetRipcordTimeLeft"/>, the <c>IsSafeToRipcord</c> spin-up
/// horizon, <c>Probe.CanRipcord</c>'s expiry margin via
/// <c>Sector.ResolveRipcordCountdowns</c>, and the server's anchor pick) routes
/// through it. A higher multiplier is a FASTER ripcord (shorter countdown).</para>
/// <para><b>Probe projectile.</b> A probe fires its OWN projectile with its OWN
/// speed (<c>probeigc.cpp:367</c>), so it is not a one-liner through the shared
/// weapon path. Native scales the round's speed by <c>c_gaSpeedAmmo</c> when the
/// probe is <b>ammo</b>-based (<c>m_ammo != 0</c>, <c>probeigc.cpp:366-368</c>)
/// and the round's lifespan by <c>c_gaLifespanEnergy</c> when the probe is
/// <b>energy</b>-based (<c>m_ammo == 0</c>, <c>probeigc.h:223-233</c>) — the
/// mirror image of the weapon path's gates. This is exposed as
/// <see cref="Probe.EffectiveProjectileSpeed"/> /
/// <see cref="Probe.EffectiveProjectileLifespan"/> (plus
/// <see cref="Probe.ResolveProjectileType"/>) — the seam a future armed-probe
/// firing implementation builds its round from. Armed probes still do NOT fire
/// in the port (a parity-pass finding, <c>Sim/Probe.cs:39-45</c>), recorded as a
/// named follow-up; this task wires the seam so it is correct once they do.</para>
/// <para>PCore001 HAS real developments for all three indices (dev 153/154
/// raise <c>Gas[14]</c>, 141/142 raise <c>Gas[15]</c>, 155/156 raise
/// <c>Gas[20]</c>) and real civ bases that vary (Omicron <c>[14]=1.1</c>,
/// Technoflux <c>[15]=1.2</c> / <c>[20]=1.3</c>, Ga'Taraan <c>[20]=0.9</c>,
/// Rixian <c>[14]=1.2 / [15]=1.2</c>). The mandated synthetic non-1.0 tests are
/// the primary assertions here.</para>
/// </summary>
public class P479SideGaRipcordProbesTests
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

    /// <summary>Build a started single-side mission whose sole side carries the
    /// given GA factors (everything else identity 1.0 — a synthetic Gas
    /// isolating the multiplier under test, same recipe as P467's
    /// <c>NewMission</c>).</summary>
    private static Mission NewMission(params (int idx, float val)[] factors)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(Core.Value, Map.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        foreach (var (idx, val) in factors) arr[idx] = val;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(arr);
        return m;
    }

    /// <summary>Any real hull (only <c>RipcordSpeed</c> / mission linkage are
    /// read by the ripcord tests).</summary>
    private static DataHullTypeIGC Hull() => Core.Value.HullTypes.Values.First(h => h.RipcordSpeed > 0f);

    /// <summary>A ship on the mission's sole side (so GA resolves), carrying
    /// the given <see cref="Mission"/> — never placed (the probe/seam reads
    /// only the launcher's <c>Mission</c>).</summary>
    private static Ship SideShip(Mission m, int id = 9500)
        => new(shipId: (short)id, hull: Hull(), position: Vector3.Zero, velocity: Vector3.Zero,
               orientation: Orientation.Identity) { Mission = m, SideId = m.Sides[0].Id };

    /// <summary>An armed probe of <paramref name="probeType"/> whose launcher is
    /// a real ship on the mission's side (so
    /// <see cref="Probe.SideGlobalAttributes"/> resolves the side GA).</summary>
    private static Probe ArmedProbe(Mission m, DataProbeTypeIGC probeType, int id = 9680)
        => new((short)id, m.Sectors[0], probeType, Vector3.Zero, now: 100.0, m.Sides[0].Id, SideShip(m, id - 980));

    /// <summary>A real ammo-based armed probe from PCore001 (positive ammo +
    /// a resolvable projectile type).</summary>
    private static DataProbeTypeIGC AmmoProbeType() =>
        Core.Value.ProbeTypes.Values.First(p => p.Ammo > 0 && Core.Value.ProjectileTypes.ContainsKey(p.ProjectileTypeId));

    // ── ripcord countdown (c_gaRipcordTime = 20) ─────────────────────────────

    /// <summary>⭐ A side whose GA has <c>RipcordTime = 2.0</c>: the ripcord
    /// countdown seeds to HALF the hull's raw <c>RipcordSpeed</c> (higher =
    /// faster ripcord), whereas identity seeds the raw hull value. Pre-fix
    /// (raw <c>_hull.RipcordSpeed</c> in <see cref="Ship.ResetRipcordTimeLeft"/>)
    /// the boosted countdown equals the raw one, so the ratio assertion fails —
    /// recorded in the P479 report.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RipcordTime_GA_halves_the_seeded_countdown()
    {
        Require.Artwork();
        var hull = Hull();

        var id = NewMission();
        var idShip = SideShip(id);
        idShip.ResetRipcordTimeLeft();
        Assert.Equal(hull.RipcordSpeed, idShip.RipcordTimeLeft, 4);
        Assert.Equal(hull.RipcordSpeed, idShip.RipcordSpeed, 4);

        var boost = NewMission((GlobalAttributeSet.RipcordTime, 2.0f));
        var boostShip = SideShip(boost);
        boostShip.ResetRipcordTimeLeft();
        Assert.Equal(hull.RipcordSpeed / 2.0f, boostShip.RipcordTimeLeft, 4);
        Assert.Equal(hull.RipcordSpeed / 2.0f, boostShip.RipcordSpeed, 4);
    }

    // ── armed-probe projectile seam (c_gaSpeedAmmo=14, c_gaLifespanEnergy=15)

    /// <summary>⭐ <c>c_gaSpeedAmmo</c> scales an AMMO probe's projectile speed
    /// (times the GA) but leaves an energy probe's speed raw — the exact
    /// <c>m_ammo != 0</c> gate of <c>probeigc.cpp:366-368</c>. Pre-fix (raw
    /// speed) the boosted ammo-probe speed equals identity, so it fails —
    /// recorded in the P479 report.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SpeedAmmo_GA_scales_ammo_probe_speed_but_not_energy_probe()
    {
        Require.Artwork();
        var ammoType = AmmoProbeType();
        var pt = Core.Value.ProjectileTypes[ammoType.ProjectileTypeId];

        var boost = NewMission((GlobalAttributeSet.SpeedAmmo, 1.25f));
        Assert.Equal(pt.Speed * 1.25f, ArmedProbe(boost, ammoType).EffectiveProjectileSpeed(pt), 4);
        // energy probe (Ammo == 0) on the same boosted side flies raw.
        Assert.Equal(pt.Speed, ArmedProbe(boost, ammoType with { Ammo = 0 }).EffectiveProjectileSpeed(pt), 4);
        // identity baseline.
        Assert.Equal(pt.Speed, ArmedProbe(NewMission(), ammoType).EffectiveProjectileSpeed(pt), 4);
    }

    /// <summary>⭐ <c>c_gaLifespanEnergy</c> scales an ENERGY probe's projectile
    /// lifespan (times the GA) but leaves an ammo probe's lifespan raw — the
    /// exact <c>m_ammo == 0</c> gate of <c>probeigc.h:223-233</c>. Pre-fix (raw
    /// lifespan) the boosted energy-probe lifespan equals identity, so it fails
    /// — recorded in the P479 report.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LifespanEnergy_GA_scales_energy_probe_lifespan_but_not_ammo_probe()
    {
        Require.Artwork();
        var ammoType = AmmoProbeType();
        var pt = Core.Value.ProjectileTypes[ammoType.ProjectileTypeId];

        var boost = NewMission((GlobalAttributeSet.LifespanEnergy, 1.5f));
        Assert.Equal(pt.Lifespan * 1.5f, ArmedProbe(boost, ammoType with { Ammo = 0 }).EffectiveProjectileLifespan(pt), 4);
        // ammo probe (Ammo != 0) on the same boosted side keeps raw lifespan.
        Assert.Equal(pt.Lifespan, ArmedProbe(boost, ammoType).EffectiveProjectileLifespan(pt), 4);
        // identity baseline.
        Assert.Equal(pt.Lifespan, ArmedProbe(NewMission(), ammoType).EffectiveProjectileLifespan(pt), 4);
    }

    /// <summary>The projectile-type resolver resolves a real armed probe's
    /// projectile from the launcher's core, and returns null for an unarmed
    /// probe.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_resolves_its_projectile_type_from_the_launcher_core()
    {
        Require.Artwork();
        var ammoType = AmmoProbeType();
        var probe = ArmedProbe(NewMission(), ammoType);
        var resolved = probe.ResolveProjectileType();
        Assert.NotNull(resolved);
        Assert.Equal(ammoType.ProjectileTypeId, resolved.Value.ProjectileTypeId);

        var unarmed = ArmedProbe(NewMission(), ammoType with { ProjectileTypeId = 0 });
        Assert.Null(unarmed.ResolveProjectileType());
    }

    /// <summary>Indices match native, and a sideless probe reads identity.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Indices_match_native_and_sideless_probe_is_identity()
    {
        Require.Artwork();
        Assert.Equal(14, GlobalAttributeSet.SpeedAmmo);
        Assert.Equal(15, GlobalAttributeSet.LifespanEnergy);
        Assert.Equal(20, GlobalAttributeSet.RipcordTime);

        // A sideless probe (no launcher) reads identity: GA 1.25 present on a
        // side it cannot see has no effect.
        var ammoType = AmmoProbeType();
        var pt = Core.Value.ProjectileTypes[ammoType.ProjectileTypeId];
        var boost = NewMission((GlobalAttributeSet.SpeedAmmo, 1.25f), (GlobalAttributeSet.LifespanEnergy, 1.5f));
        var sideless = new Probe(9700, boost.Sectors[0], ammoType, Vector3.Zero, 100.0, sideId: null, launcher: null);
        Assert.Equal(pt.Speed, sideless.EffectiveProjectileSpeed(pt), 4);
        Assert.Equal(pt.Lifespan, sideless.EffectiveProjectileLifespan(pt), 4);
    }

    /// <summary>⭐ End-to-end through a REAL PCore001 development: completing
    /// dev 155 'GA: Ripcord 1' (<c>Gas[20] = 1.15</c>) on a Dreghklar side (whose
    /// base <c>Gas[20] = 1.0</c>) via the exact production completion apply
    /// (<c>Server.Economy.cs:979</c>: <c>side.GlobalAttributes =
    /// side.GlobalAttributes.Apply(FromArray(dev.Gas))</c>) shrinks the ripcord
    /// countdown by 1.15× — the synthetic multiplier made real. The probe-armed
    /// projectile cannot be E2E'd live (armed probes don't fire), so the
    /// per-consumer synthetic tests above are the primary assertions for 14/15,
    /// while the real development and civ scan in the report lists who touches
    /// each attribute.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_dev_155_GA_Ripcord_scales_the_countdown()
    {
        Require.Artwork();
        // Real development 155, verified by name and gas.
        var dev = Core.Value.Developments[155];
        Assert.Equal("GA: Ripcord 1", dev.Name);
        Assert.InRange(dev.Gas[GlobalAttributeSet.RipcordTime], 1.149f, 1.151f);
        Assert.False(dev.Gas.All(g => Math.Abs(g - 1f) < 1e-6f), "dev 155 must carry a real attribute delta (not tech-only)");

        // A Dreghklar side: base Gas[20] == 1.0 (measured in the P479 report).
        var civ = Core.Value.Civilizations.Values.First(c => c.Name == "Dreghklar");
        var mPre = Mission.Assemble(Core.Value, Map.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        mPre.Stage = MissionStage.Started;
        var preShip = SideShip(mPre);
        Assert.Equal(1f, mPre.Sides[0].GlobalAttributes.Get(GlobalAttributeSet.RipcordTime), 3);
        preShip.ResetRipcordTimeLeft();
        float preCountdown = preShip.RipcordTimeLeft;
        Assert.Equal(Hull().RipcordSpeed, preCountdown, 3);

        // Complete dev 155 exactly as Server.Economy.cs:979 does.
        mPre.Sides[0].GlobalAttributes = mPre.Sides[0].GlobalAttributes.Apply(GlobalAttributeSet.FromArray(dev.Gas));
        Assert.InRange(mPre.Sides[0].GlobalAttributes.Get(GlobalAttributeSet.RipcordTime), 1.149f, 1.151f);

        var postShip = SideShip(mPre);
        postShip.ResetRipcordTimeLeft();
        Assert.InRange(postShip.RipcordTimeLeft, preCountdown / 1.151f, preCountdown / 1.149f);
        Console.WriteLine($"P479-E2E: dev 155 '{dev.Name}' (gas[20] {dev.Gas[GlobalAttributeSet.RipcordTime]:F3}) -> countdown {preCountdown:F3}s -> {postShip.RipcordTimeLeft:F3}s (ratio {preCountdown / postShip.RipcordTimeLeft:F3}, expect ~1.15)");
    }
}
