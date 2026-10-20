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
        Manifest(schema: 4); Assert.Null(new HdPack(_root, _logs.Add).Resolve("hull", "hullbmp", Open));
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

    private void AtlasManifest()
    {
        foreach (string stem in new[] { "atlas_a", "atlas_b" })
        {
            string dir = Path.Combine(_root, "textures", stem);
            Directory.CreateDirectory(dir);
            foreach (string name in HdPack.MapNames) File.WriteAllBytes(Path.Combine(dir, name + ".png"), [0]);
        }
        File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(new
        {
            schema = 2, textures = new Dictionary<string, object>
            {
                ["atlas_a"] = new { native = new { TextureSha256 = Hash(NativeTexture) } },
                ["atlas_b"] = new { native = new { TextureSha256 = Hash(NativeModel) } }
            }
        }));
    }

    [Fact]
    public void Schema2_multi_atlas_resolves_each_mesh_and_shares_material_across_model_kinds()
    {
        AtlasManifest(); var pack = new HdPack(_root, _logs.Add); int reads = 0;
        Stream Artwork(string name) { reads++; return new MemoryStream(name == "atlas_b.mdl" ? NativeModel : NativeTexture); }
        var a = pack.Resolve("multi_atlas_ship", "ATLAS_A", Artwork);
        var b = pack.Resolve("multi_atlas_ship", "atlas_b", Artwork);
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotEqual(a.Directory, b.Directory);
        Assert.Equal(Path.Combine(_root, "textures", "atlas_a", "normal.png"), a.MapPath("normal"));
        foreach (string kind in new[] { "station", "missile", "probe", "projectile", "asteroid", "aleph", "treasure" })
            Assert.Same(a, pack.Resolve(kind, "atlas_a", Artwork));
        Assert.Equal(2, reads); // No model geometry hash in an atlas material.
    }

    [Fact]
    public void Schema2_hash_guard_and_missing_maps_only_disable_the_affected_atlas()
    {
        AtlasManifest();
        File.Delete(Path.Combine(_root, "textures", "atlas_b", "normal.png"));
        var pack = new HdPack(_root, _logs.Add);
        Assert.NotNull(pack.Resolve("ship", "atlas_a", _ => new MemoryStream(NativeTexture)));
        Assert.Null(pack.Resolve("ship", "atlas_b", _ => new MemoryStream(NativeModel)));
        Assert.Null(pack.Resolve("ship", "absent", _ => throw new Exception("unexpected IO")));
        pack = new HdPack(_root, _logs.Add);
        Assert.Null(pack.Resolve("ship", "atlas_a", _ => new MemoryStream([99])));
        Assert.Null(pack.Resolve("station", "atlas_a", _ => throw new Exception("negative result must cache")));
        Assert.Single(_logs, l => l.Contains("SHA256 mismatch"));
    }
    [Fact]
    public void Schema3_resolves_dds_atlases_and_keeps_hash_and_complete_map_guards()
    {
        AtlasManifest();
        string path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema\":2", "\"schema\":3"));
        foreach (string stem in new[] { "atlas_a", "atlas_b" })
            foreach (string name in HdPack.MapNames)
                File.Move(Path.Combine(_root, "textures", stem, name + ".png"), Path.Combine(_root, "textures", stem, name + ".dds"));
        var pack = new HdPack(_root, _logs.Add);
        var material = pack.Resolve("ship", "atlas_a", _ => new MemoryStream(NativeTexture));
        Assert.NotNull(material); Assert.True(material.Compressed);
        Assert.EndsWith("normal.dds", material.MapPath("normal"));
        Assert.Same(material, pack.Resolve("station", "ATLAS_A", _ => throw new Exception("cached")));
        Assert.Null(pack.Resolve("ship", "atlas_b", _ => new MemoryStream([99])));
        File.Delete(material.MapPath("normal"));
        Assert.Null(new HdPack(_root, _logs.Add).Resolve("ship", "atlas_a", _ => new MemoryStream(NativeTexture)));
    }

    [Fact]
    public void Schema2_materialTuning_section_is_exposed_per_atlas_and_absent_otherwise()
    {
        // HD-6 — per-atlas tuning overrides (no texture regeneration). A manifest
        // "materialTuning" section keyed by atlas stem is surfaced via TuningFor,
        // case-insensitively; unstyled atlases return null (renderer uses defaults).
        AtlasManifest();
        string path = Path.Combine(_root, "manifest.json");
        string json = File.ReadAllText(path);
        json = json.Substring(0, json.Length - 1) + ",\"materialTuning\":{\"ATLAS_A\":{\"RoughnessScale\":0.4,\"EnvSheen\":0.0}}}";
        File.WriteAllText(path, json);
        var pack = new HdPack(_root, _logs.Add);
        var a = pack.TuningFor("atlas_a");   // case-insensitive stem lookup
        Assert.NotNull(a);
        Assert.Equal(0.4f, a["roughnessscale"]);   // param names case-insensitive
        Assert.Equal(0f, a["EnvSheen"]);
        Assert.Null(pack.TuningFor("atlas_b"));    // no override → null (defaults)
        Assert.Null(pack.TuningFor("absent"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
