using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// Regression only: production renderers, real GL, externally built DDS fixtures.
sealed class CompressedProof : Game
{
    private readonly string _repo, _pack, _art;
    private readonly bool _synthetic;
    public CompressedProof(string repo, string pack, string art)
    {
        _repo = repo; _synthetic = pack == "synthetic";
        _pack = _synthetic ? Path.Combine(Path.GetTempPath(), "hd4-gl-" + Guid.NewGuid()) : pack; _art = art;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 256, PreferredBackBufferHeight = 256,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
    }
    protected override void Draw(GameTime gameTime)
    {
        using var timer = Allegiance.ClientCore.MonoGame.Diagnostics.GlFrameTimer.TryCreate();
        Console.WriteLine($"HD4_GL renderer={timer?.Renderer}; driver={timer?.DriverVersion}");
        var assets = new DirectoryAssetSource(_art);
        if (_synthetic) Fixture();
        using var shaders = ShaderAssetCache.ForDevice(GraphicsDevice);
        const string shader = "Content/Shaders/NativePbr.ogl.mgfxo";
        if (shaders.LoadFrom(shader, () => File.OpenRead(Path.Combine(_repo, "src/Allegiance.ClientCore.MonoGame", shader))) is null)
            throw new Exception("shader");
        using var hd = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(assets), new HdPack(_pack), shaders: shaders);
        using var bc3 = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(assets), new HdPack(_pack), shaders: shaders, forceBc3: true);
        using var ship = new NativeShipRenderer(GraphicsDevice, assets, hd: hd);
        var geometry = ship.MeshFor("wc_icfig")!;
        var native = ship.TextureFor("fig02bmp");
        using var target = new RenderTarget2D(GraphicsDevice, 256, 256, false, SurfaceFormat.Color, DepthFormat.Depth24);
        Color[] Render(HdMaterialRenderer renderer, float distance, bool enabled)
        {
            renderer.Enabled = enabled;
            using var draw = new NativeShipRenderer(GraphicsDevice, assets, hd: renderer);
            GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
            GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            var view = Matrix.CreateLookAt(new(0, .4f, distance), Vector3.Zero, Vector3.Up);
            var projection = Matrix.CreatePerspectiveFieldOfView(.75f, 1, .1f, 1000);
            draw.Draw(geometry, native, Matrix.Identity, view, projection, 1, 1.5f / geometry.MeshRadius,
                SectorLighting.FromCluster([.4f, .5f, 1], 0xffddddcc), "wc_icfig", "fig02bmp");
            GraphicsDevice.SetRenderTarget(null);
            var pixels = new Color[256 * 256]; target.GetData(pixels); return pixels;
        }
        var off = Render(hd, 5, false);
        var on = Render(hd, 5, true);
        if (off.SequenceEqual(on) || hd.ResidentMaterials != 1) throw new Exception("DDS failed to shade native mesh");
        long nearBytes = hd.ResidentBytes;
        var fallback = Render(bc3, 5, true);
        if (fallback.SequenceEqual(off) || bc3.ResidentBytes != nearBytes) throw new Exception("BC3 fallback failed");
        hd.Enabled = true;
        var unlit = hd.EmissiveTexture("fig02bmp") ?? throw new Exception("compressed unlit map missing");
        GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
        using (var batch = new SpriteBatch(GraphicsDevice))
        { batch.Begin(); batch.Draw(unlit, new Rectangle(0, 0, 256, 256), Color.White); batch.End(); }
        GraphicsDevice.SetRenderTarget(null);
        var unlitPixels = new Color[256 * 256]; target.GetData(unlitPixels);
        var center = unlitPixels[128 * 256 + 128];
        if (_synthetic && (Math.Abs(center.R - 200) > 5 || Math.Abs(center.G - 40) > 5 || Math.Abs(center.B - 180) > 5))
            throw new Exception($"unlit sRGB samples changed: {center}");
        var again = Render(hd, 5, false);
        if (!off.SequenceEqual(again)) throw new Exception("toggle changed native output");
        hd.ResetResidency(); Render(hd, 100, true);
        if (hd.ResidentBytes >= nearBytes) throw new Exception("distant hull did not select small mip tail");
        long distantBytes = hd.ResidentBytes; Render(hd, 5, true);
        if (hd.ResidentBytes != nearBytes) throw new Exception("approach did not promote detail");
        using var tiny = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(assets), new HdPack(_pack), budgetBytes: 1, shaders: shaders);
        if (!off.SequenceEqual(Render(tiny, 5, true)) || tiny.ResidentBytes != 0) throw new Exception("budget fallback differs");
        if (_synthetic)
        {
            // A standalone BC3 release has primary .dds names and no optional
            // sibling fallback files. It must load on the same renderer path.
            foreach (string name in new[] { "basecolor", "emission" })
            {
                string dir = Path.Combine(_pack, "textures", "fig02bmp");
                File.Move(Path.Combine(dir, name + ".fallback.dds"), Path.Combine(dir, name + ".dds"), true);
            }
            using var standalone = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(assets), new HdPack(_pack), shaders: shaders, forceBc3: true);
            if (off.SequenceEqual(Render(standalone, 5, true)) || standalone.ResidentMaterials != 1)
                throw new Exception("standalone BC3 archive failed");
        }
        Console.WriteLine($"HD4_COMPRESSED_PASS near={nearBytes} distant={distantBytes}; BC7/BC5/BC4, BC3, mips, promotion, toggle, budget/native parity");
        Exit();
    }
    private void Fixture()
    {
        string dir = Path.Combine(_pack, "textures", "fig02bmp"); Directory.CreateDirectory(dir);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_art, "fig02bmp.mdl"))));
        File.WriteAllText(Path.Combine(_pack, "manifest.json"), "{\"schema\":3,\"textures\":{\"fig02bmp\":{\"native\":{\"TextureSha256\":\"" + hash + "\"}}}}");
        foreach (string name in HdPack.MapNames)
        {
            int bc = name is "basecolor" or "emission" ? 7 : name == "normal" ? 5 : 4;
            Write(name + ".dds", bc);
            if (bc == 7) Write(name + ".fallback.dds", 3);
        }
        void Write(string file, int bc)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(dir, file)));
            var h = new uint[36]; h[0] = 124; h[2] = h[3] = 1024; h[6] = 11;
            h[18] = 32; h[19] = 4; h[20] = 0x30315844;
            h[31] = bc switch { 7 => 99u, 5 => 83u, 4 => 80u, _ => 78u }; h[32] = 3; h[34] = 1;
            writer.Write("DDS "u8); foreach (uint value in h) writer.Write(value);
            // Golden constant blocks from ISPCTextureCompressor. Data codecs
            // are endpoint-only blocks, and never require a software decoder.
            byte[] block = bc switch {
                7 => [8,144,201,100,50,133,66,161,104,181,90,45,172,170,170,170],
                3 => [255,255,73,146,36,73,146,36,86,193,86,193,0,0,0,0],
                5 => [128,128,0,0,0,0,0,0,128,128,0,0,0,0,0,0],
                _ => [128,128,0,0,0,0,0,0] };
            for (int size = 1024;; size /= 2)
            {
                for (int i = 0; i < ((size + 3) / 4) * ((size + 3) / 4); i++) writer.Write(block);
                if (size == 1) break;
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _synthetic && Directory.Exists(_pack)) Directory.Delete(_pack, true);
    }

}
