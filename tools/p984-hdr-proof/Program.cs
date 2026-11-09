// P984 — GPU proof for the linear HDR scene foundation
// (visuals.space.hdrPipeline, default OFF).
//
//   SDL_VIDEODRIVER=offscreen dotnet p984-hdr-proof.dll --check [shaderDir]
//
// It runs the REAL Tonemap.ogl.mgfxo and the REAL VfxPostProcessor against a
// real (offscreen) GraphicsDevice and asserts, with pixel readback:
//
//  1. the single output transform's two curves (neutral / ACES) match the C#
//     mirror in HdrOutput — so there is ONE tone map, not two;
//  2. an over-1.0 value written by the (linear) PBR path survives into the float
//     scene target and is graded by the curve instead of being clipped at 1.0;
//  3. the transform runs AFTER additive composition (bloom's job): the curve sees
//     the summed value;
//  4. the HUD drawn after the transform is NOT graded (an exact opaque colour
//     survives untransformed);
//  5. the float scene target exists, is RGBA16F, survives resize/recreate, and
//     falls back to the native 8-bit path when the transform shader is missing.
//
// The tool is deliberately standalone (the P921 proof pattern) because the test
// host has no graphics device; HdrFoundationTests runs it and asserts the
// P984_*_PASS markers below.
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

string shaderDir = args.Length > 1 ? Path.GetFullPath(args[1]) : DefaultShaderDir();
using var proof = new HdrProof(shaderDir);
proof.Run();
return proof.Ok ? 0 : 1;

static string DefaultShaderDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
    return dir is null
        ? "src/Allegiance.ClientCore.MonoGame/Content/Shaders"
        : Path.Combine(dir.FullName, "src/Allegiance.ClientCore.MonoGame/Content/Shaders");
}

sealed class HdrProof : Game
{
    private const int Width = 8, Height = 1;
    private readonly string _shaderDir;
    public bool Ok { get; private set; }
    private GraphicsDeviceManager _gdm;

    public HdrProof(string shaderDir)
    {
        _shaderDir = shaderDir;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
        };
    }

    /// <summary>Make the shader bytecode reachable through MonoGame's
    /// TitleContainer the same way the shipped client sees it
    /// (<c>Content/Shaders/…</c> next to the executable).</summary>
    private void LinkContent()
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        string shaders = Path.Combine(content, "Shaders");
        if (Directory.Exists(shaders) || !Directory.Exists(_shaderDir)) return;
        Directory.CreateDirectory(content);
        Directory.CreateSymbolicLink(shaders, _shaderDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        LinkContent();
        try { Ok = RunChecks(GraphicsDevice); }
        catch (Exception exception) { Console.WriteLine("[p984-proof] FATAL: " + exception); Ok = false; }
        Exit();
    }

    private ShaderAssetCache Cache(GraphicsDevice gd) => new(bytes => new Effect(gd, bytes));

    private Effect? Load(ShaderAssetCache cache, GraphicsDevice gd, string file)
    {
        string path = Path.Combine(_shaderDir, file);
        if (!File.Exists(path)) { Console.WriteLine($"[p984-proof] missing shader {path}"); return null; }
        return cache.LoadFrom(path, () => File.OpenRead(path));
    }

    private bool RunChecks(GraphicsDevice gd)
    {
        using var cache = Cache(gd);
        var tonemap = Load(cache, gd, "Tonemap.ogl.mgfxo");
        if (tonemap is null) { Console.WriteLine("[p984-proof] tonemap effect unavailable"); return false; }
        using var transform = new HdrOutputTransform(gd, tonemap);
        if (!transform.IsUsable) { Console.WriteLine("[p984-proof] transform not usable"); return false; }

        // ── 1) the two curves, exactly as the C# mirror computes them ──────
        // Stored display-encoded samples, including one OVER 1.0 (the value the
        // linear PBR shader can now leave in the float target).
        float[] stored = { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f, 1.5f, HdrOutput.Encode(0.18f) };
        using var source = FloatTexture(gd, stored);
        byte[] neutral = RunTransform(gd, transform, source, HdrCurve.Neutral);
        byte[] aces = RunTransform(gd, transform, source, HdrCurve.Aces);
        bool transformOk = true;
        for (int i = 0; i < stored.Length; i++)
        {
            byte expectedNeutral = Byte(HdrOutput.Output(new Vector3(stored[i]), HdrCurve.Neutral).X);
            byte expectedAces = Byte(HdrOutput.Output(new Vector3(stored[i]), HdrCurve.Aces).X);
            int gotNeutral = neutral[i * 4], gotAces = aces[i * 4];
            if (Math.Abs(gotNeutral - expectedNeutral) > 1 || Math.Abs(gotAces - expectedAces) > 1)
            {
                Console.WriteLine($"[p984-proof] transform mismatch at stored={stored[i]:0.000}: "
                    + $"neutral gpu={gotNeutral} cpu={expectedNeutral}, aces gpu={gotAces} cpu={expectedAces}");
                transformOk = false;
            }
        }
        // Overbright: neutral saturates at the display clamp, ACES grades 1.5 to a
        // value below white — i.e. the float highlight reached the curve.
        byte overNeutral = neutral[6 * 4], overAces = aces[6 * 4];
        bool overbrightOk = overNeutral == 255 && overAces is > 200 and < 255;
        Console.WriteLine($"[p984-proof] stored=1.5 neutral={overNeutral}/255 aces={overAces}/255 "
            + $"expectedAces={Byte(HdrOutput.Output(new Vector3(1.5f), HdrCurve.Aces).X)}/255");
        Console.WriteLine($"[p984-proof] samples neutral={string.Join(',', Enumerable.Range(0, stored.Length).Select(i => neutral[i * 4]))}");

        // ── 2) the transform runs AFTER additive composition ───────────────
        // 0.6 + 0.9 = 1.5 in a float target (what bloom's composite does), then
        // the curve. If the transform had run first, the sum would be 1.0 + 0.9
        // and clip; the summed value is what must be graded.
        using var additive = FloatTarget(gd, Width, Height);
        var a = FloatTexture(gd, Fill(0.6f, Width));
        var b = FloatTexture(gd, Fill(0.9f, Width));
        gd.SetRenderTarget(additive);
        gd.Clear(Color.Black);
        var batch = new SpriteBatch(gd);
        batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
        batch.Draw(a, new Rectangle(0, 0, Width, Height), Color.White);
        batch.End();
        batch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp);
        batch.Draw(b, new Rectangle(0, 0, Width, Height), Color.White);
        batch.End();
        batch.Dispose();
        a.Dispose();
        b.Dispose();
        byte[] summed = RunTransform(gd, transform, additive, HdrCurve.Aces);
        byte expectedSummed = Byte(HdrOutput.Output(new Vector3(1.5f), HdrCurve.Aces).X);
        bool orderOk = Math.Abs(summed[0] - expectedSummed) <= 1;
        Console.WriteLine($"[p984-proof] additive 0.6+0.9 -> aces={summed[0]}/255 expected={expectedSummed}/255 "
            + $"(transform-after-composite={(orderOk ? "yes" : "NO")})");

        // ── 3) the HUD drawn after the transform is never graded ───────────
        // ACES-grade a mid grey, then draw an opaque HUD-coloured quad on top;
        // the HUD pixel must come back exactly as drawn.
        using var hudTarget = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        var mid = FloatTexture(gd, Fill(0.5f, Width));
        gd.Textures[0] = null;
        transform.Render(mid, hudTarget, HdrCurve.Aces);
        mid.Dispose();
        using (var hudTex = SolidTexture(gd, new Color(128, 64, 0)))
        {
            var hud = new SpriteBatch(gd);
            gd.SetRenderTarget(hudTarget);
            hud.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            hud.Draw(hudTex, new Rectangle(0, 0, Width, Height), Color.White);
            hud.End();
            hud.Dispose();
        }
        var hudPixels = new Color[Width * Height];
        hudTarget.GetData(hudPixels);
        bool hudOk = hudPixels.All(p => p.R == 128 && p.G == 64 && p.B == 0);
        Console.WriteLine($"[p984-proof] hud-after-transform pixel=({hudPixels[0].R},{hudPixels[0].G},{hudPixels[0].B}) "
            + $"expected=(128,64,0) ungraded={hudOk}");

        // ── 4) float scene target, resize, fallback ────────────────────────
        bool targetOk = true;
        using (var post = new VfxPostProcessor(gd, cache))
        {
            post.SetHdr(true);
            targetOk &= post.HdrActive;
            targetOk &= post.SceneRT?.Format == SurfaceFormat.HalfVector4;
            targetOk &= post.HdrCompositeRT?.Format == SurfaceFormat.HalfVector4;
            targetOk &= post.Output is { IsUsable: true };
            Console.WriteLine($"[p984-proof] hdr requested=true active={post.HdrActive} "
                + $"scene={post.SceneRT?.Format} composite={post.HdrCompositeRT?.Format} "
                + $"size={post.SceneRT?.Width}x{post.SceneRT?.Height}");
            // resize / device-reset path: SetSize re-creates every target.
            post.SetSize(320, 180);
            targetOk &= post.SceneRT?.Width == 320 && post.SceneRT?.Height == 180;
            targetOk &= post.HdrCompositeRT?.Width == 320 && post.HdrActive;
            post.SetSize(640, 360);
            targetOk &= post.SceneRT?.Width == 640 && post.HdrCompositeRT?.Width == 640;
            // withdraw the flag: back to the native RGBA8 path, same size.
            post.SetHdr(false);
            targetOk &= !post.HdrActive && post.SceneRT?.Format == SurfaceFormat.Color
                && post.SceneRT?.Width == 640 && post.HdrCompositeRT is null;
            // and back on again (an options-page toggle applies at once).
            post.SetHdr(true);
            targetOk &= post.HdrActive && post.SceneRT?.Format == SurfaceFormat.HalfVector4;
        }
        bool fallbackOk = true;
        using (var brokenCache = new ShaderAssetCache(_ => throw new InvalidOperationException("shader denied (fallback proof)")))
        using (var fallback = new VfxPostProcessor(gd, brokenCache))
        {
            fallback.SetHdr(true);
            fallbackOk = fallback.HdrRequested && !fallback.HdrActive
                && fallback.SceneRT?.Format == SurfaceFormat.Color
                && fallback.HdrCompositeRT is null;
            Console.WriteLine($"[p984-proof] fallback requested={fallback.HdrRequested} active={fallback.HdrActive} "
                + $"scene={fallback.SceneRT?.Format} reason={fallback.HdrFallbackReason ?? "shader missing"}");
        }
        Console.WriteLine($"[p984-proof] target checks: float={targetOk} fallback={fallbackOk}");

        bool ok = transformOk && overbrightOk && orderOk && hudOk && targetOk && fallbackOk;
        if (transformOk) Console.WriteLine("P984_HDR_TRANSFORM_PASS neutral+aces match the CPU mirror, one curve per sample");
        if (overbrightOk && orderOk && hudOk)
            Console.WriteLine("P984_HDR_ORDER_PASS overbright graded once after composition, HUD drawn after the transform stays exact");
        if (targetOk && fallbackOk)
            Console.WriteLine("P984_HDR_TARGET_PASS float scene target, resize/recreate, and 8-bit fallback with the transform missing");
        if (ok) Console.WriteLine("P984_HDR_CHECK_PASS");
        return ok;
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static float[] Fill(float value, int count)
    {
        var values = new float[count];
        Array.Fill(values, value);
        return values;
    }

    private static Texture2D FloatTexture(GraphicsDevice gd, float[] values)
    {
        var texture = new Texture2D(gd, values.Length, 1, false, SurfaceFormat.HalfVector4);
        var data = new HalfVector4[values.Length];
        for (int i = 0; i < values.Length; i++) data[i] = new HalfVector4(values[i], values[i], values[i], 1f);
        texture.SetData(data);
        return texture;
    }

    private static Texture2D SolidTexture(GraphicsDevice gd, Color color)
    {
        var texture = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        texture.SetData(new[] { color });
        return texture;
    }

    private static RenderTarget2D FloatTarget(GraphicsDevice gd, int width, int height)
        => new(gd, width, height, false, SurfaceFormat.HalfVector4, DepthFormat.None);

    /// <summary>Run the real output transform over <paramref name="source"/> into an
    /// 8-bit target and return the decoded RGBA bytes (8 texels).</summary>
    private static byte[] RunTransform(GraphicsDevice gd, HdrOutputTransform transform, Texture2D source, HdrCurve curve)
    {
        using var target = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        gd.Textures[0] = null;
        transform.Render(source, target, curve);
        var pixels = new Color[Width * Height];
        target.GetData(pixels);
        gd.SetRenderTarget(null);
        var bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].R;
            bytes[i * 4 + 1] = pixels[i].G;
            bytes[i * 4 + 2] = pixels[i].B;
            bytes[i * 4 + 3] = pixels[i].A;
        }
        return bytes;
    }
}
