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
    public static bool MeshyPackAvailable => Allegiance.Core.MeshyPack.IsAvailable();

    /// <summary>Skips the running <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>
    /// test (with the reason) when the artwork corpus is not configured/present.</summary>
    public static void Artwork() => Xunit.Skip.IfNot(ArtworkAvailable, ExternalPaths.DescribeMissing(ExternalResourceKind.Artwork));

    /// <summary>Skips the running test when the native C++ source checkout is
    /// not configured/present.</summary>
    public static void NativeSource() => Xunit.Skip.IfNot(NativeSourceAvailable, ExternalPaths.DescribeMissing(ExternalResourceKind.NativeSource));

    /// <summary>Skips the running <c>[SkippableFact]</c> test when the private
    /// Meshy asset pack is not configured/built. Used by the P601 Meshy-coupling
    /// tests: on a clean clone (no pack) they skip with a reason rather than fail,
    /// exactly like the P597 corpus tests.</summary>
    public static void MeshyPack() => Xunit.Skip.IfNot(MeshyPackAvailable, Allegiance.Core.MeshyPack.DescribeUnavailable());

    /// <summary>Marker MonoGame prints when it cannot create a GL device (the
    /// offscreen SDL/EGL context has no driver — e.g. a GPU-less CI runner).</summary>
    public const string NoGraphicsDeviceMarker = "NoSuitableGraphicsDeviceException";

    /// <summary>Set to <c>1</c> on a machine that HAS a GPU to turn the
    /// <see cref="GraphicsDevice"/> skip back into a failure.</summary>
    public const string RequireGpuEnvVar = "ALLEGIANCE_REQUIRE_GPU";

    /// <summary>CI-1 — real-GL tests run a proof tool in a child process with
    /// <c>SDL_VIDEODRIVER=offscreen</c>. Call this with the child's combined
    /// output right after it exits (before asserting on it) from a
    /// <c>[SkippableFact]</c>: if MonoGame could not create a graphics device at
    /// all, the test SKIPS with the reason instead of failing. Any other failure
    /// (wrong pixels, missing PASS marker, crash after device creation) still
    /// fails, so nothing is weakened where a GPU is present.</summary>
    public static void GraphicsDevice(string processOutput) => Xunit.Skip.If(
        processOutput.Contains(NoGraphicsDeviceMarker, StringComparison.Ordinal)
            && Environment.GetEnvironmentVariable(RequireGpuEnvVar) != "1",
        "No usable GL graphics device (MonoGame " + NoGraphicsDeviceMarker + " from the offscreen " +
        "SDL/EGL context — e.g. a clean CI runner without a GPU). Set " + RequireGpuEnvVar +
        "=1 to make this a failure. See docs/CI_TEST_GATES.md.");
}
