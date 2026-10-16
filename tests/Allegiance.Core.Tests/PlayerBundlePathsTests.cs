using Allegiance.Core;

namespace Allegiance.Core.Tests;

public sealed class PlayerBundlePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p644-paths-" + Guid.NewGuid());
    private string Exe => Path.Combine(_root, "install");
    private string Cwd => Path.Combine(_root, "elsewhere");
    public PlayerBundlePathsTests() { Directory.CreateDirectory(Exe); Directory.CreateDirectory(Cwd); }
    public void Dispose() => Directory.Delete(_root, true);
    private string Resolve(ExternalResourceKind kind, string? env = null) =>
        ExternalPaths.Resolve(kind, Exe, Cwd, _ => env);

    [Theory]
    [InlineData(ExternalResourceKind.Artwork, "artwork")]
    [InlineData(ExternalResourceKind.SkyPack, "sky")]
    public void UsesExecutableDirectoryOutsideRepository(ExternalResourceKind kind, string folder)
    {
        string bundled = Path.Combine(Exe, folder);
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(Path.Combine(Cwd, folder));
        Assert.Equal(bundled, Resolve(kind));
    }

    [Theory]
    [InlineData(ExternalResourceKind.Artwork)]
    [InlineData(ExternalResourceKind.SkyPack)]
    [InlineData(ExternalResourceKind.NativeSource)]
    public void MissingDirectoryHasNoDefault(ExternalResourceKind kind) => Assert.Equal("", Resolve(kind));

    [Fact]
    public void NativeSourceNeverGuessesAnInstallSubdirectory()
    {
        Directory.CreateDirectory(Path.Combine(Exe, "native"));
        Assert.Equal("", Resolve(ExternalResourceKind.NativeSource));
    }

    [Fact]
    public void EnvironmentWinsEvenIfMissingAndTrimsWhitespace()
    {
        Directory.CreateDirectory(Path.Combine(Exe, "artwork"));
        File.WriteAllText(Path.Combine(Exe, "local.env"), "ALLEGIANCE_ARTWORK_PATH=configured");
        Assert.Equal("explicit", Resolve(ExternalResourceKind.Artwork, "  explicit "));
    }

    [Fact]
    public void LocalConfigWinsOverBundleWithBlankEnvironment()
    {
        Directory.CreateDirectory(Path.Combine(Exe, "sky"));
        File.WriteAllText(Path.Combine(Cwd, "AllegianceCore.slnx"), "");
        File.WriteAllText(Path.Combine(Cwd, "local.env"), "# ALLEGIANCE_SKY_PACK_PATH=comment\nALLEGIANCE_SKY_PACK_PATH= configured \n");
        Assert.Equal("configured", Resolve(ExternalResourceKind.SkyPack, " "));
    }

    [Fact]
    public void BlankAndCommentedKeysFallBackToBundle()
    {
        Directory.CreateDirectory(Path.Combine(Exe, "artwork"));
        File.WriteAllText(Path.Combine(Exe, "local.env"), "# ALLEGIANCE_ARTWORK_PATH=ignored\nALLEGIANCE_ARTWORK_PATH=\n");
        Assert.Equal(Path.Combine(Exe, "artwork"), Resolve(ExternalResourceKind.Artwork));
    }
}
