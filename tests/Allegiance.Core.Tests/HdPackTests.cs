using System.Security.Cryptography;
using System.Text.Json;

namespace Allegiance.Core.Tests;

public sealed class HdPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-" + Guid.NewGuid());
    private static readonly byte[] NativeModel = [1, 2, 3], NativeTexture = [4, 5, 6];
    private readonly List<string> _logs = [];
    public HdPackTests() => Directory.CreateDirectory(_root);
    private void Manifest(string? textureHash = null, bool? accepted = null, int schema = 1)
    {
        var models = new Dictionary<string, object> { ["hull"] = new { native = new {
            Model = "hull", Texture = "hullbmp", ModelSha256 = Hash(NativeModel), TextureSha256 = textureHash ?? Hash(NativeTexture) } } };
        var manifest = new Dictionary<string, object> { ["schema"] = schema, ["models"] = models };
        if (accepted is { } review) manifest["jobs"] = new Dictionary<string, object> {
            ["hull:multiview"] = new { complete = true, metrics = new { accepted = review } } };
        File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(manifest));
        Directory.CreateDirectory(Path.Combine(_root, "models", "hull"));
        foreach (string name in HdPack.MapNames) File.WriteAllBytes(Path.Combine(_root, "models", "hull", name + ".png"), [0]);
    }
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    private static Stream Open(string name) => new MemoryStream(name == "hull.mdl" || name == "HULL.mdl" ? NativeModel : NativeTexture);
    [Fact]
    public void Valid_entry_resolves_case_insensitively_and_hashes_only_on_first_use()
    {
        Manifest(accepted: true); int reads = 0;
        var pack = new HdPack(_root, _logs.Add);
        Assert.Equal(0, reads);
        var result = pack.Resolve("HULL", "hullbmp", name => { reads++; return Open(name); });
        Assert.NotNull(result); Assert.Equal(2, reads);
        Assert.Equal(Path.Combine(_root, "models", "hull", "normal.png"), result.MapPath("normal"));
        Assert.Same(result, pack.Resolve("hull", "hullbmp", _ => throw new Exception("must be cached")));
    }
    [Fact]
    public void Missing_pack_or_unknown_model_falls_back_without_reading_artwork()
    {
        var pack = new HdPack(_root, _logs.Add);
        Assert.Null(pack.Resolve("hull", "hullbmp", _ => throw new Exception("unexpected IO")));
        Assert.Single(_logs);
        Manifest(); pack = new HdPack(_root, _logs.Add);
        Assert.Null(pack.Resolve("other", "hullbmp", _ => throw new Exception("unexpected IO")));
        Assert.Null(pack.Resolve("hull", null, Open));
    }
    [Fact]
    public void Stale_texture_sha_is_ignored_and_logged_once()
    {
        Manifest(new string('0', 64)); var pack = new HdPack(_root, _logs.Add);
        Assert.Null(pack.Resolve("hull", "hullbmp", Open));
        Assert.Null(pack.Resolve("hull", "hullbmp", Open));
        Assert.Single(_logs, l => l.Contains("SHA256 mismatch"));
    }
    [Fact]
    public void Changed_native_model_or_texture_override_falls_back()
    {
        Manifest(); var pack = new HdPack(_root, _logs.Add);
        Assert.Null(pack.Resolve("hull", "hullbmp", name => name == "hull.mdl" ? new MemoryStream([99]) : Open(name)));
        Assert.Null(pack.Resolve("hull", "alternatebmp", _ => throw new Exception("must not read override")));
    }
    [Fact]
    public void Missing_maps_and_unreadable_native_asset_fall_back()
    {
        Manifest(); File.Delete(Path.Combine(_root, "models", "hull", "normal.png"));
        Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", Open));
        Manifest(); Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", _ => throw new FileNotFoundException()));
    }
    [Fact]
    public void Rejected_review_and_corrupt_or_unsupported_manifest_fall_back()
    {
        Manifest(accepted: false);
        Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", _ => throw new Exception("must not read rejected maps")));
        Manifest(schema: 2); Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", Open));
        Manifest();
        string path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema\":1", "\"schema\":100000000000000000000"));
        Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", Open));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{bad");
        Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", Open));
    }
    [Fact]
    public void Malformed_entry_does_not_hide_other_entries()
    {
        Manifest(); string path = Path.Combine(_root, "manifest.json");
        string json = File.ReadAllText(path).Replace("\"models\":{", "\"models\":{\"../escape\":{},\"broken\":{},");
        File.WriteAllText(path, json);
        Assert.NotNull(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", Open));
    }
    [Fact]
    public void External_path_kind_exposes_hd_environment_setting()
        => Assert.Equal("ALLEGIANCE_HD_PACK_PATH", ExternalPaths.EnvVar(ExternalResourceKind.HdPack));
    public void Dispose() => Directory.Delete(_root, true);
}
