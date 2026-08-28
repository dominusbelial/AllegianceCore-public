namespace Allegiance.Core.Tests;

/// <summary>
/// Payload decoders are asserted against the <b>whole corpus</b>, not one file —
/// a decoder that parses cleanly on a single sample could be silently wrong on
/// others. Corrupt values surface here as range violations.
/// </summary>
public class PayloadDecodeTests
{
    /// <summary>Enumerate decoded <see cref="DataProjectileTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataProjectileTypeIGC V)> Projectiles()
    {
        foreach (var p in Corpus.Files)
        {
            var raw = File.ReadAllBytes(p);
            var f = IgcReader.ParseFile(p);
            var bodyStart = f.Wrapper == IgcWrapper.VersionAndDataSize ? 8 : 4;
            foreach (var r in f.Records)
            {
                if (r.Type != IgcObjectType.ProjectileType) continue;
                yield return (Path.GetFileName(p), DataProjectileTypeIGC.Decode(
                    raw.AsSpan(bodyStart + r.PayloadOffset, r.Size)));
            }
        }
    }

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
}
