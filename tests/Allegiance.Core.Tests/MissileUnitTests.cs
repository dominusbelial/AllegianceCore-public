using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P90 domain-level proof of <see cref="Missile"/>'s straight-line flight and
/// expiry-triggers-removal-from-sector — a pure-domain (no sockets) companion
/// to <see cref="MissileBroadcastTests"/>' live loopback. There is no existing
/// standalone <see cref="Projectile"/> unit-test file to mirror
/// (ProjectileBroadcastTests.cs covers Projectile only via the wire), so this
/// follows the established assembled-mission structure of the sim tests
/// (<c>ShipInSectorTests</c>/<c>CombatWeaponFiringTests</c>): build a started
/// mission, drop a <see cref="Missile"/> directly into a sector (no launcher —
/// the IIgcSite event is a no-op on a launcher-less missile), tick the mission,
/// and assert (1) it advances in a straight line at the inherited constant
/// velocity, and (2) past <c>now + Lifespan</c> it is removed from the sector
/// (the observable expiry contract).
/// </summary>
public class MissileUnitTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            if (core.MissileTypes.Count == 0) continue;
            return core;
        }
        throw new InvalidOperationException("no static core with a missile type found in corpus");
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

    /// <summary>A single missile type sized for a fast, deterministic test:
    /// initial speed &gt; 0 and a lifespan in [1 s, 6 s) so a handful of chunks
    /// comfortably both observes straight-line flight before expiry and drives
    /// past it.</summary>
    private static readonly Lazy<DataMissileTypeIGC> RepresentativeMissileType = new(() =>
    {
        var core = RepresentativeCore.Value;
        var t = core.MissileTypes.Values
            .Where(m => m.InitialSpeed > 0f && m.Lifespan >= 1.0f && m.Lifespan < 6.0f)
            .OrderBy(m => m.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && core.MissileTypes.Count > 0)
            throw new InvalidOperationException("representative core has no missile with speed>0 and 1<=lifespan<6");
        return t;
    });

    private static Mission NewStartedMission()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, SomeMap.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>
    /// A missile launched straight along an arbitrary direction moves in a
    /// straight line at the constant velocity it was given, and once the sim
    /// advances past <c>now + Lifespan</c> it removes itself from the sector.
    /// The pre-expiry position check proves flight; the post-expiry check proves
    /// removal-from-sector (the missile no longer ticks and is gone from
    /// <c>Sector.Models</c>), and that its final resting spot is at the expiry
    /// distance, not the launch point.
    /// </summary>
    [Fact]
    public void Missile_flies_straight_and_expires_out_of_sector()
    {
        var mType = RepresentativeMissileType.Value;
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];

        Vector3 p0 = new(100f, -20f, 30f);
        Vector3 dir = new Vector3(0f, 0f, -1f).Normalize();
        Vector3 vel = dir * mType.InitialSpeed;                 // inherited launcher velocity is zero here
        const short id = 951;

        var missile = new Missile(id, sector, mType, p0, vel, dir, now: 0.0);
        sector.Add(missile);
        Assert.True(sector.Models.Contains(missile), "the missile must be in its sector after Add");

        // ---- straight-line flight before expiry ----
        double halfLife = mType.Lifespan * 0.5;
        mission.Update(halfLife);
        var expectHalf = p0 + vel * (float)halfLife;
        Assert.True((missile.Position - expectHalf).Length() < 1f,
            $"post-half-life position {missile.Position} must equal {expectHalf} (±1 m)");
        Assert.True(sector.Models.Contains(missile), "must still be in the sector mid-flight");
        Assert.Equal((double)mType.Lifespan, missile.TimeExpire, 6);   // seeded clock (now=0) ⇒ expiry == Lifespan

        // ---- drive well past expiry ----
        mission.Update(mType.Lifespan + 0.5);

        Assert.False(sector.Models.Contains(missile), "an expired missile must be removed from its sector");
        float travelled = (missile.Position - p0).Length();
        float ideal = mType.InitialSpeed * mType.Lifespan;      // distance at the nominal expiry instant
        float chunkSlack = mType.InitialSpeed * 0.08f;          // up to ~one extra chunk's travel + float slack
        Assert.True(travelled >= ideal - chunkSlack,
            $"expired missile must have travelled ≥ {ideal} m, got {travelled}");
        Assert.True(travelled <= ideal + mType.InitialSpeed * 0.6f + chunkSlack,
            $"expired missile must not fly absurdly past its nominal expiry travel; {travelled} vs {ideal}");
        Assert.True(travelled > mType.InitialSpeed * 0.5f,
            "an expired missile must not be sitting at/near its launch point");
    }
}
