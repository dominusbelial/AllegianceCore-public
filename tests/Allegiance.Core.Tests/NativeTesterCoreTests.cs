using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>The native C++ repo's extra static core, <c>artwork/tester.igc</c>, is not
/// part of the public corpus (<see cref="Corpus"/> is the FreeAllegiance Artwork only),
/// so it keeps its own check here: it parses byte-exact and loads as a core.</summary>
[Trait("Requires", "NativeSource")]
public class NativeTesterCoreTests
{
    [SkippableFact]
    public void Tester_core_parses_exactly_and_loads()
    {
        Require.NativeSource();
        var path = Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.NativeSource)!, "artwork", "tester.igc");
        Skip.IfNot(File.Exists(path), $"{path} not present");
        var f = IgcReader.ParseFile(path);
        Assert.True(f.IsExact, $"tester.igc: consumed {f.BytesConsumed} of {f.BodyLength}");
        Assert.Equal(IgcWrapper.VersionAndDataSize, f.Wrapper);
        var core = StaticCore.Load(f);
        Assert.Equal(379, core.HullTypes.Count);
        Assert.Equal(83, core.ProjectileTypes.Count);
    }
}
