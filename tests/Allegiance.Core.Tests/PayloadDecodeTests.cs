namespace Allegiance.Core.Tests;

/// <summary>
/// Payload decoders are asserted against the <b>whole corpus</b>, not one file —
/// a decoder that parses cleanly on a single sample could be silently wrong on
/// others. Corrupt values surface here as range violations.
/// </summary>
public class PayloadDecodeTests
{
    /// <summary>Yield each record's raw payload (correctly body-indexed) for a given type across the whole corpus.</summary>
    private static IEnumerable<(string File, byte[] Payload)> RawOf(IgcObjectType type)
    {
        foreach (var p in Corpus.Files)
        {
            var raw = File.ReadAllBytes(p);
            var f = IgcReader.ParseFile(p);
            var bodyStart = f.Wrapper == IgcWrapper.VersionAndDataSize ? 8 : 4;
            foreach (var r in f.Records)
            {
                if (r.Type != type) continue;
                yield return (Path.GetFileName(p), raw[(bodyStart + r.PayloadOffset)..(bodyStart + r.PayloadOffset + r.Size)]);
            }
        }
    }

    /// <summary>Enumerate decoded <see cref="DataProjectileTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataProjectileTypeIGC V)> Projectiles()
        => RawOf(IgcObjectType.ProjectileType).Select(t => (t.File, DataProjectileTypeIGC.Decode(t.Payload)));

    [Fact]
    public void ProjectileType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Projectiles())
        {
            n++;
            Assert.InRange(v.ProjectileTypeId, 0, short.MaxValue);
            Assert.InRange(v.DamageType, 0, 19);        // DamageTypeID < c_dmgidMax
            Assert.True(float.IsFinite(v.Radius), $"{file}: radius NaN/Inf");
            Assert.True(float.IsFinite(v.Rotation), $"{file}: rotation NaN/Inf");
            Assert.True(float.IsFinite(v.Power), $"{file}: power NaN/Inf");
            Assert.True(float.IsFinite(v.BlastPower), $"{file}: blastPower NaN/Inf");
            Assert.True(float.IsFinite(v.BlastRadius), $"{file}: blastRadius NaN/Inf");
            Assert.True(float.IsFinite(v.Speed), $"{file}: speed NaN/Inf");
            Assert.True(float.IsFinite(v.Lifespan), $"{file}: lifespan NaN/Inf");
            Assert.True(float.IsFinite(v.Width), $"{file}: width NaN/Inf");
            foreach (var f in v.Color) Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.ModelName, v.TextureName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 9300, $"expected ~9498 projectile records, saw {n}");
    }

    /// <summary>Enumerate decoded civilizations across every record in every file.</summary>
    public static IEnumerable<(string File, DataCivilizationIGC V)> Civilizations()
        => RawOf(IgcObjectType.Civilization).Select(t => (t.File, DataCivilizationIGC.Decode(t.Payload)));

    [Fact]
    public void Civilization_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Civilizations())
        {
            n++;
            Assert.InRange(v.CivilizationId, 0, short.MaxValue);
            Assert.InRange(v.Lifepod, 0, short.MaxValue);
            Assert.InRange(v.InitialStationTypeId, 0, short.MaxValue);
            Assert.True(float.IsFinite(v.IncomeMoney), $"{file}: income NaN/Inf");
            Assert.True(float.IsFinite(v.BonusMoney), $"{file}: bonus NaN/Inf");
            Assert.True(v.GasBaseAttributes.Length == DataCivilizationIGC.GaCount);
            Assert.All(v.GasBaseAttributes, g => Assert.True(float.IsFinite(g), $"{file}: gas element NaN/Inf"));
            Assert.NotEmpty(v.Name);
            foreach (var s in new[] { v.Name, v.IconName, v.HudName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
            Assert.Equal(50, v.TtbmBaseTechs.Length);
            Assert.Equal(50, v.TtbmNoDevTechs.Length);
        }
        Assert.True(n >= 950, $"expected ~965 civilization records, saw {n}");
    }

    /// <summary>Enumerate decoded developments across every record in every file.</summary>
    public static IEnumerable<(string File, DataDevelopmentIGC V)> Developments()
        => RawOf(IgcObjectType.Development).Select(t => (t.File, DataDevelopmentIGC.Decode(t.Payload)));

    [Fact]
    public void Development_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Developments())
        {
            n++;
            Assert.True(v.Price >= 0, $"{file}: negative price {v.Price}");
            Assert.InRange(v.DevelopmentId, 0, short.MaxValue);
            Assert.InRange(v.CompletionSound, -1, short.MaxValue); // NA = -1
            Assert.True(float.IsFinite(v.TimeToBuild));
            Assert.NotEmpty(v.Name);
            foreach (var s in new[] { v.Name, v.IconName, v.ModelName, v.Description })
                Assert.DoesNotContain(s, c => char.IsControl(c));
            Assert.True(v.Gas.Length == DataDevelopmentIGC.GaCount);
            Assert.All(v.Gas, g => Assert.True(float.IsFinite(g), $"{file}: gas element NaN/Inf"));
            Assert.Equal(50, v.TtbmRequired.Length);
            Assert.Equal(50, v.TtbmEffects.Length);
        }
        Assert.True(n >= 28000, $"expected ~28843 development records, saw {n}");
    }
}
