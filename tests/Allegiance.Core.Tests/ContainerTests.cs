using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

[Trait("Requires","Artwork")]
public class ContainerTests
{
    [SkippableFact]
    public void Corpus_is_discovered_in_the_artwork_checkout()
    {
        Require.Artwork();
        Assert.Equal(Corpus.ExpectedUniqueFiles, Corpus.Files.Count);
    }

    /// <summary>
    /// The acceptance property. With no Windows host to diff against the
    /// original writer, exact byte consumption is the oracle: one wrong field
    /// width or offset desynchronises everything after it, so a stream that
    /// lands precisely on end-of-body is strong evidence the layout is right.
    /// Measured 210/210 by an independent Python scanner on 2026-08-27 (then incl. the native tester.igc; 209 in the artwork alone).
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Corpus.Theory), MemberType = typeof(Corpus))]
    public void Every_file_is_consumed_exactly(string path)
    {
        Require.Artwork();
        var f = IgcReader.ParseFile(path);
        Assert.True(f.IsExact,
            $"{Path.GetFileName(path)}: consumed {f.BytesConsumed} of {f.BodyLength} " +
            $"({f.Records.Count} records, wrapper {f.Wrapper})");
    }

    [SkippableFact]
    public void Wrapper_split_is_98_cores_and_111_maps()
    {
        Require.Artwork();
        var cores = 0; var maps = 0;
        foreach (var p in Corpus.Files)
        {
            switch (IgcReader.ParseFile(p).Wrapper)
            {
                case IgcWrapper.VersionAndDataSize: cores++; break;
                case IgcWrapper.DataSize: maps++; break;
            }
        }
        Assert.Equal(Corpus.ExpectedStaticCores, cores);
        Assert.Equal(Corpus.ExpectedMaps, maps);
    }

    /// <summary>Every static core carries exactly one 1760-byte OT_constants record.</summary>
    [SkippableFact]
    public void Each_static_core_has_exactly_one_constants_record()
    {
        Require.Artwork();
        foreach (var p in Corpus.Files)
        {
            var f = IgcReader.ParseFile(p);
            if (f.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            Assert.Equal(1, f.CountOf(IgcObjectType.Constants));
            var c = f.Records.First(r => r.Type == IgcObjectType.Constants);
            Assert.Equal(1760, c.Size);
        }
    }

    /// <summary>
    /// Hull payloads are <c>540 + 30 * maxWeapons</c>. The stride is <b>30</b>,
    /// not 29 — corrected 2026-08-27 after <c>570 != 540 + 29*1</c> was spotted.
    /// A one-byte stride error would corrupt every field past the first weapon
    /// while still parsing cleanly, so it is worth asserting directly.
    /// </summary>
    [SkippableFact]
    public void Hull_sizes_follow_stride_30_from_base_540()
    {
        Require.Artwork();
        foreach (var p in Corpus.Files)
        foreach (var r in IgcReader.ParseFile(p).Records)
        {
            if (r.Type != IgcObjectType.HullType) continue;
            Assert.True(r.Size >= 540 && (r.Size - 540) % 30 == 0,
                $"{Path.GetFileName(p)}: hull size {r.Size} is not 540 + 30k");
        }
    }
}
