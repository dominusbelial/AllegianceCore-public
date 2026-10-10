namespace Allegiance.Core.Tests;

/// <summary>
/// The single resolved artwork root every live test's own <c>ArtRoot</c>
/// field reads from (see <see cref="ExternalPaths"/>). On this workstation the
/// gitignored local.env provides the path; on a clean clone it resolves to
/// empty and corpus-backed tests skip via <c>Require.Artwork()</c>.
/// </summary>
internal static class TestArtRoot
{
    public static readonly string Value =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);
}
