using Allegiance.Core;

namespace Allegiance.TestInfra;

/// <summary>
/// Test-side helpers for the external path resolver
/// (<see cref="ExternalPaths"/>). Corpus-backed tests are annotated with
/// <c>[SkippableFact]</c>/<c>[SkippableTheory]</c> (Xunit.SkippableFact) and call
/// <see cref="Require.Artwork"/> / <see cref="Require.NativeSource"/> as their
/// first line, so on a clean clone — where the artwork corpus or native source
/// is absent — they SKIP with a clear reason instead of failing. See
/// <c>docs/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md</c>.
/// </summary>
public static class Require
{
    public static bool ArtworkAvailable => ExternalPaths.IsAvailable(ExternalResourceKind.Artwork);
    public static bool NativeSourceAvailable => ExternalPaths.IsAvailable(ExternalResourceKind.NativeSource);

    /// <summary>Skips the running <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>
    /// test (with the reason) when the artwork corpus is not configured/present.</summary>
    public static void Artwork() => Xunit.Skip.IfNot(ArtworkAvailable, ExternalPaths.DescribeMissing(ExternalResourceKind.Artwork));

    /// <summary>Skips the running test when the native C++ source checkout is
    /// not configured/present.</summary>
    public static void NativeSource() => Xunit.Skip.IfNot(NativeSourceAvailable, ExternalPaths.DescribeMissing(ExternalResourceKind.NativeSource));
}
