using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P94 — the pure-domain (no sockets, no server) proof of <see cref="Mine"/>'s
/// two invariants: a mine <b>never moves</b> (it sits exactly where it was
/// constructed until it expires — <c>CmineIGC</c> has no position integration,
/// <c>src/Igc/mineigc.cpp:150-156</c>), and <b>expiry removes it from the
/// sector</b> (<c>Mine.OnUpdate</c> calls <c>sector.Remove(this)</c> on
/// <c>now &gt;= TimeExpire</c>). Constructed directly with a real
/// <see cref="DataMineTypeIGC"/> from a representative static core and a real
/// map sector; no launcher (launcher-less mines raise nothing on expiry — the
/// destruction broadcast is exercised live in <see cref="MineBroadcastTests"/>).
/// </summary>
public sealed class MineDomainTests
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

    /// <summary>A real mine type with a comfortable lifespan (&ge; 1 s) so the
    /// pre-expiry position probe runs several genuine chunks before expiry.</summary>
    private static DataMineTypeIGC RealMineType()
    {
        var t = RepresentativeCore.Value.MineTypes.Values
            .Where(m => m.Lifespan >= 1.0f)
            .OrderBy(m => m.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && RepresentativeCore.Value.MineTypes.Count > 0)
            throw new InvalidOperationException("representative core has no mine with lifespan >= 1");
        return t;
    }

    /// <summary>⭐ A mine's static-position + timed-expiry behaviour: it sits at
    /// its deployed position through many ticks, and once the clock passes its
    /// <c>Lifespan</c> it removes itself from the sector — its position unchanged
    /// even at the moment of expiry (no position advance anywhere). Constructed
    /// directly (no mission, no ship, no site): the launcher-less mine raises
    /// nothing on expiry, isolating the sector-removal + no-movement contract.</summary>
    [Fact]
    public void Mine_never_moves_and_expiry_removes_it_from_its_sector()
    {
        var sector = new Sector(SomeMap.Value.Clusters[0]);
        var mtype = RealMineType();
        var pos = new Vector3(10f, -20f, 305f);
        double now = 512.0;
        var mine = new Mine(501, sector, mtype, pos, now, launcher: null);
        sector.Add(mine);

        Assert.True(mine.TimeExpire > now, "expiry must be in the future at birth");
        Assert.True(MathF.Abs((float)(mine.TimeExpire - (now + mtype.Lifespan))) < 1e-6,
            "TimeExpire must be now + Lifespan");
        Assert.Equal(1.0f, mine.Fraction);   // freshly-armed mine starts at full fraction

        // Many genuine chunks well before expiry: the mine must not move.
        sector.Update(now + 0.017);
        sector.Update(now + 0.034);
        sector.Update(now + 0.051);
        sector.Update(now + 0.068);
        Assert.Equal(pos, mine.Position);
        Assert.True(sector.LastUpdate < mine.TimeExpire, "test must remain before expiry for the position probe");
        Assert.Contains(sector.Models, m => ReferenceEquals(m, mine));

        // Push past expiry in one chunk: the mine is removed, and its position
        // is still the deployed position — proving no position advance even at
        // the moment it expires.
        sector.Update(mine.TimeExpire + 0.5);
        Assert.DoesNotContain(sector.Models, m => ReferenceEquals(m, mine));
        Assert.Equal(pos, mine.Position);
    }
}
