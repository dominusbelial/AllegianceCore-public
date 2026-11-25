using System.Security.Cryptography;
using System.Text;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Tools.KniShaders;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1083ShaderTests
{
    private static string Root
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
            return root?.FullName ?? throw new DirectoryNotFoundException();
        }
    }
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Opt_in_is_explicit_and_independent_of_style(HudStyle style)
    {
        Assert.True(Enum.IsDefined(style));
        Assert.False(BrowserShaderOptions.Volumetric(null, true));
        Assert.False(BrowserShaderOptions.Volumetric("?dev=1", true));
        Assert.False(BrowserShaderOptions.Volumetric("?volumetric=1", false));
        Assert.True(BrowserShaderOptions.Volumetric("?dev=1&volumetric=1", true));
        Assert.False(BrowserShaderOptions.Volumetric("?volumetric=0", true));
        Assert.False(BrowserShaderOptions.Volumetric("?volumetric=1&volumetric=0", true));
        Assert.False(BrowserShaderOptions.Volumetric("?volumetric=1&volumetric=1", true));
        Assert.False(BrowserShaderOptions.Volumetric("?volumetric=%31", true));
    }
    [Fact]
    public void Deferred_shader_allowlist_accepts_only_the_exact_effect_and_preserves_canonical_validation()
    {
        var core = Asset("core.igc", [1]);
        var resident = new StagedManifest(2, [core]);
        foreach (string name in new[] { "content/shaders/volumetric.xnb", "Content/Shaders/Volumetric.xnb" })
        {
            var effect = Asset(name, [2]);
            new BattleManifest(1, [effect], [new("shader-volumetric", [name])]).Validate(resident);
        }
        foreach (string name in new[] { "volumetric.xnb", "content/shaders/other.xnb", "content/shaders/volumetric.ogl.mgfxo", "content/shaders/../volumetric.xnb" })
        {
            var effect = Asset(name, [2]);
            Assert.Throws<InvalidDataException>(() => new BattleManifest(1, [effect], [new("shader-volumetric", [name])]).Validate(resident));
        }
    }
    [Fact]
    public void Legacy_container_keeps_every_shader_byte_and_does_not_touch_desktop_artifact()
    {
        var (source, hash, desktop) = Inputs();
        byte[] before = desktop.ToArray();
        byte[] result = LegacyVolumetric.Convert(source, hash, hash, desktop);
        Assert.Equal(before, desktop);
        Assert.Equal((byte)10, result[4]);
        Assert.Equal((byte)0, result[5]);
        Assert.Equal(Shaders(desktop, 11), Shaders(result, 10));
        Assert.Equal(result, LegacyVolumetric.Convert(source, hash, hash, desktop));
        string xnb = Path.Combine(Root, "src/Allegiance.ClientCore.Game.Browser/Content/Shaders/Volumetric.xnb");
        byte[] shipped = File.ReadAllBytes(xnb);
        Assert.Contains("Microsoft.Xna.Framework.Content.EffectReader", Encoding.UTF8.GetString(shipped));
        Assert.DoesNotContain("ReflectiveReader", Encoding.UTF8.GetString(shipped));
        Assert.True(shipped.AsSpan().IndexOf("KNIF"u8) >= 0, "Shipped XNB contains a KNI-compiled effect");
    }
    [Fact]
    public void Legacy_conversion_rejects_stale_sources_unknown_versions_and_truncation()
    {
        var (source, hash, desktop) = Inputs();
        Assert.Throws<InvalidDataException>(() => LegacyVolumetric.Convert(source, hash, new string('0', 64), desktop));
        Assert.Throws<InvalidDataException>(() => LegacyVolumetric.Convert([0], hash, hash, desktop));
        byte[] bad = desktop.ToArray(); bad[4] = 12;
        Assert.Throws<InvalidDataException>(() => LegacyVolumetric.Convert(source, hash, hash, bad));
        Assert.ThrowsAny<IOException>(() => LegacyVolumetric.Convert(source, hash, hash, desktop[..25]));
    }
    private static (byte[], string, byte[]) Inputs()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Root, "src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx"));
        string hash = File.ReadAllText(Path.Combine(Root, "src/Allegiance.ClientCore.MonoGame/Content/Shaders/Volumetric.fx.sha256")).Trim();
        byte[] desktop = File.ReadAllBytes(Path.Combine(Root, "src/Allegiance.ClientCore.MonoGame/Content/Shaders/Volumetric.ogl.mgfxo"));
        return (source, hash, desktop);
    }
    private static string[] Shaders(byte[] bytes, int version)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.BaseStream.Position = 10;
        int buffers = reader.ReadInt32();
        for (int i = 0; i < buffers; i++)
        { _ = reader.ReadString(); _ = reader.ReadInt16(); int n = reader.ReadInt32(); reader.BaseStream.Position += n * 6; }
        int count = reader.ReadInt32(); var hashes = new string[count];
        for (int i = 0; i < count; i++)
        {
            byte stage = reader.ReadByte();
            if (version == 11) { _ = reader.ReadString(); _ = reader.ReadString(); }
            byte[] code = reader.ReadBytes(reader.ReadInt32());
            hashes[i] = stage + ":" + Convert.ToHexStringLower(SHA256.HashData(code));
            int samplers = reader.ReadByte();
            for (int s = 0; s < samplers; s++)
            { reader.BaseStream.Position += 3; if (reader.ReadBoolean()) reader.BaseStream.Position += 20; _ = reader.ReadString(); _ = reader.ReadByte(); }
            int constants = reader.ReadByte(); reader.BaseStream.Position += constants;
            int attributes = reader.ReadByte();
            for (int a = 0; a < attributes; a++) { _ = reader.ReadString(); reader.BaseStream.Position += 4; }
        }
        return hashes;
    }
    private static StagedAsset Asset(string name, byte[] bytes)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(name, "packs/" + hash, bytes.Length, hash, "application/octet-stream", "fixture", []);
    }
}
