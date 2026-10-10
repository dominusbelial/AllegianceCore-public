using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P99 — the pure-domain (no sockets, no server) proof of <see cref="Probe"/>'s
/// two invariants: a probe <b>never moves</b> (it sits exactly where it was
/// constructed until it expires — <c>CprobeIGC</c> has no position integration,
/// <c>src/Igc/probeigc.cpp:115,142,333-532</c>), and <b>expiry removes it from
/// the sector</b> (<c>Probe.OnUpdate</c> calls <c>sector.Remove(this)</c> on
/// <c>now &gt;= TimeExpire</c>). Constructed directly with a real
/// <see cref="DataProbeTypeIGC"/> from a representative static core and a real
/// map sector. A launcher is supplied as a <see cref="SideId"/> carrier only:
/// the constructor takes the side explicitly (the deliberate improvement over
/// <see cref="Mine"/>'s P94), so the test also proves the probe captures it
/// even with no Ship launcher — while raising nothing on expiry (the
/// destruction broadcast is exercised live in <see cref="ProbeBroadcastTests"/>,
/// which uses a real deploying <see cref="Ship"/>).
/// </summary>
public sealed class ProbeDomainTests
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

    /// <summary>A real probe type with a comfortable lifespan (&ge; 1 s) so the
    /// pre-expiry position probe runs several genuine chunks before expiry.</summary>
    private static DataProbeTypeIGC RealProbeType()
    {
        var t = RepresentativeCore.Value.ProbeTypes.Values
            .Where(pt => pt.Lifespan >= 1.0f)
            .OrderBy(pt => pt.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && RepresentativeCore.Value.ProbeTypes.Count > 0)
            throw new InvalidOperationException("representative core has no probe with lifespan >= 1");
        return t;
    }

    /// <summary>⭐ A probe's static-position + timed-expiry behaviour: it sits at
    /// its deployed position through many ticks, and once the clock passes its
    /// <c>Lifespan</c> it removes itself from the sector — its position unchanged
    /// even at the moment of expiry (no position advance anywhere). Constructed
    /// directly (no Ship launcher, so nothing is raised on expiry), with the
    /// side captured from construction — isolating the sector-removal,
    /// no-movement, and day-one-SideId contracts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_never_moves_expiry_removes_it_and_side_is_captured_day_one()
    {
        Require.Artwork();
        var sector = new Sector(SomeMap.Value.Clusters[0]);
        var ptype = RealProbeType();
        var pos = new Vector3(7f, -13f, 901f);
        double now = 410.0;
        const short side = 1;
        var probe = new Probe(777, sector, ptype, pos, now, sideId: side, launcher: null);
        sector.Add(probe);

        Assert.True(probe.TimeExpire > now, "expiry must be in the future at birth");
        Assert.True(MathF.Abs((float)(probe.TimeExpire - (now + ptype.Lifespan))) < 1e-6,
            "TimeExpire must be now + Lifespan");
        Assert.Equal(side, probe.SideId);          // the day-one SideId, captured at construction
        Assert.Equal(1.0f, probe.Fraction);        // a freshly-deployed probe starts at full fraction

        // Many genuine chunks well before expiry: the probe must not move.
        sector.Update(now + 0.017);
        sector.Update(now + 0.034);
        sector.Update(now + 0.051);
        sector.Update(now + 0.068);
        Assert.Equal(pos, probe.Position);
        Assert.True(sector.LastUpdate < probe.TimeExpire, "test must remain before expiry for the position probe");
        Assert.Contains(sector.Models, m => ReferenceEquals(m, probe));

        // Push past expiry in one chunk: the probe is removed, and its position
        // is still the deployed position — proving no position advance even at
        // the moment it expires.
        sector.Update(probe.TimeExpire + 0.5);
        Assert.DoesNotContain(sector.Models, m => ReferenceEquals(m, probe));
        Assert.Equal(pos, probe.Position);
    }
}
