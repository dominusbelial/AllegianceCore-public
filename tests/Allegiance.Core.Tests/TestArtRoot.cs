namespace Allegiance.Core.Tests;

/// <summary>
/// The single resolved artwork root every live test's own <c>ArtRoot</c>
/// field reads from. Set <c>ALLEGIANCE_ARTWORK_PATH</c> to override; falls
/// back to Domingo's own machine's path so nothing changes there by default.
/// </summary>
internal static class TestArtRoot
{
    public static readonly string Value =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";
}
