// P989 — GPU proof for the sim colour grade (visuals.space.simPalette, default OFF).
//
//   SDL_VIDEODRIVER=offscreen dotnet p989-palette-proof.dll --check [shaderDir]
//
// It runs the REAL Tonemap.ogl.mgfxo and the REAL Bloom.fx against a real
// (offscreen) GraphicsDevice and asserts, with pixel readback:
//
//  1. the sim grade (filmic curve, exposure 0.9, world saturation 0.85) matches
//     the C# mirror in HdrOutput — the grade really is ONE transform, not a
//     second curve layered on the first;
//  2. the saturation is a LUMA-PRESERVING linear-light operation: the graded
//     colour keeps the scene's brightness and loses chroma (that is what makes
//     hulls read as the same faction colour, just calmer);
//  3. the default grade (flag off / pipeline off) is byte-identical to P984's
//     neutral output — the no-op proof at the transform level;
//  4. the HUD drawn after the graded transform is NOT graded (exact colour);
//  5. the existing bloom pass at the sim settings (threshold 1.2 linear,
//     contribution 0.08) leaves a below-threshold highlight alone and adds far
//     less glow than the P547 default — with NO new pass.
//
// The tool is standalone (the P921 proof pattern) because the test host has no
// graphics device; SimPaletteTests runs it and asserts the P989_*_PASS markers.
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

string shaderDir = args.Length > 1 ? Path.GetFullPath(args[1]) : DefaultShaderDir();
using var proof = new PaletteProof(shaderDir);
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

sealed class PaletteProof : Game
{
    private const int Width = 4, Height = 1;
    private readonly string _shaderDir;
    public bool Ok { get; private set; }

    public PaletteProof(string shaderDir)
    {
        _shaderDir = shaderDir;
        _ = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
        };
    }

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
        catch (Exception exception) { Console.WriteLine("[p989-proof] FATAL: " + exception); Ok = false; }
        Exit();
    }

    private Effect? Load(ShaderAssetCache cache, GraphicsDevice gd, string file)
    {
        string path = Path.Combine(_shaderDir, file);
        if (!File.Exists(path)) { Console.WriteLine($"[p989-proof] missing shader {path}"); return null; }
        return cache.LoadFrom(path, () => File.OpenRead(path));
    }

    private bool RunChecks(GraphicsDevice gd)
    {
        using var cache = new ShaderAssetCache(bytes => new Effect(gd, bytes));
        var tonemap = Load(cache, gd, "Tonemap.ogl.mgfxo");
        var bloomEffect = Load(cache, gd, "Bloom.ogl.mgfxo");
        if (tonemap is null) { Console.WriteLine("[p989-proof] tonemap effect unavailable"); return false; }
        using var transform = new HdrOutputTransform(gd, tonemap);
        if (!transform.IsUsable) { Console.WriteLine("[p989-proof] transform not usable"); return false; }

        var grade = SimPalette.Grade(requested: true, hdrActive: true, acesComparison: false);
        bool transformOk = CheckTransform(gd, transform, grade);
        bool flagOffOk = CheckFlagOff(gd, transform);
        bool hudOk = CheckHudUngraded(gd, transform, grade);
        bool bloomOk = bloomEffect is not null && CheckBloom(gd, bloomEffect);
        if (bloomEffect is null) Console.WriteLine("[p989-proof] bloom shader unavailable");
        // The 4K fold is MEASURED, not adopted: it changes pixels by one 8-bit step
        // (the RGBA16F round trip the fold removes), so the brief's
        // "pixel-equivalent only" rule keeps the separate transform pass.
        MeasureFsrFold(gd, cache, transform);

        bool ok = transformOk && flagOffOk && hudOk && bloomOk;
        if (transformOk) Console.WriteLine("P989_PALETTE_TRANSFORM_PASS the sim grade matches the CPU mirror; saturation keeps brightness and cuts chroma");
        if (flagOffOk) Console.WriteLine("P989_PALETTE_FLAGOFF_PASS flag off / pipeline off is byte-identical to the P984 neutral output");
        if (hudOk) Console.WriteLine("P989_PALETTE_HUD_PASS the HUD drawn after the graded transform is ungraded");
        if (bloomOk) Console.WriteLine("P989_PALETTE_BLOOM_PASS existing bloom at 1.2 linear / 0.08 leaves below-threshold highlights alone");

        if (ok) Console.WriteLine("P989_PALETTE_CHECK_PASS");
        return ok;
    }

    /// <summary>The graded transform == the C# mirror, and the saturation is a
    /// luma-preserving linear op (brightness kept, chroma reduced).</summary>
    private bool CheckTransform(GraphicsDevice gd, HdrOutputTransform transform, HdrGrade grade)
    {
        bool ok = true;
        // Greys first: the curve + exposure, no chroma to lose.
        float[] greys = { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f, 1.5f };
        using (var source = FloatTexture(gd, greys.Select(v => (v, v, v)).ToArray()))
        {
            byte[] gpu = RunTransform(gd, transform, source, grade, greys.Length);
            for (int i = 0; i < greys.Length; i++)
            {
                byte expected = Byte(HdrOutput.Output(new Vector3(greys[i]), grade.Curve, grade.Exposure, grade.Saturation).X);
                if (Math.Abs(gpu[i * 4] - expected) > 1)
                {
                    Console.WriteLine($"[p989-proof] grade mismatch stored={greys[i]:0.000}: gpu={gpu[i * 4]} cpu={expected}");
                    ok = false;
                }
            }
            Console.WriteLine($"[p989-proof] greys sim={string.Join(',', Enumerable.Range(0, greys.Length).Select(i => gpu[i * 4]))}");
        }

        // A saturated colour: the sim grade must land on the same LUMA as the
        // filmic-only grade (in linear light) while pulling chroma toward grey.
        var stored = new Vector3(0.90f, 0.35f, 0.15f);
        using (var source = FloatTexture(gd, new[] { (stored.X, stored.Y, stored.Z) }))
        {
            byte[] simPixels = RunTransform(gd, transform, source, grade, 1);
            byte[] acesPixels = RunTransform(gd, transform, source,
                HdrGrade.Native(HdrCurve.Aces) with { Exposure = SimPalette.Exposure }, 1);
            var sim = new Vector3(simPixels[0] / 255f, simPixels[1] / 255f, simPixels[2] / 255f);
            var aces = new Vector3(acesPixels[0] / 255f, acesPixels[1] / 255f, acesPixels[2] / 255f);
            float simLuma = Luma(HdrOutput.Decode(sim)), acesLuma = Luma(HdrOutput.Decode(aces));
            float simChroma = Chroma(sim), acesChroma = Chroma(aces);
            // Saturation is a LUMA-PRESERVING operation in LINEAR light (asserted
            // exactly in SimPaletteTests), but it runs BEFORE the per-channel filmic
            // fit, so the luma read back through the curve can shift by a few
            // hundredths: pulling a saturated red down stops that channel clipping
            // on the shoulder. ≤ 0.05 is the bound; the frame as a whole gets darker
            // (measured in docs/reports/SIM_PALETTE_P989.md §3.3).
            bool lumaKept = MathF.Abs(simLuma - acesLuma) < 0.05f;
            bool calmer = simChroma < acesChroma - 0.01f;
            Console.WriteLine($"[p989-proof] colour sim=({sim.X:0.000},{sim.Y:0.000},{sim.Z:0.000}) aces=({aces.X:0.000},{aces.Y:0.000},{aces.Z:0.000}) "
                + $"luma {simLuma:0.0000}/{acesLuma:0.0000} chroma {simChroma:0.000}/{acesChroma:0.000} "
                + $"(luma-kept={lumaKept} calmer={calmer})");
            ok &= lumaKept && calmer;
        }
        return ok;
    }

    /// <summary>Flag off (and the pipeline-off fallback) is P984's picture, byte
    /// for byte: the same stored samples through the same shader.</summary>
    private bool CheckFlagOff(GraphicsDevice gd, HdrOutputTransform transform)
    {
        float[] stored = { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f, 1.5f, HdrOutput.Encode(0.18f) };
        bool ok = true;
        using var source = FloatTexture(gd, stored.Select(v => (v, v, v)).ToArray());
        // What the renderer resolves when the flag is off or the float target was
        // refused — both must be the P984 default, never a hidden grade.
        var off = SimPalette.Grade(requested: false, hdrActive: true, acesComparison: false);
        var fallback = SimPalette.Grade(requested: true, hdrActive: false, acesComparison: false);
        ok &= off == HdrGrade.Native(HdrCurve.Neutral) && fallback == HdrGrade.Native(HdrCurve.Neutral);
        byte[] gpu = RunTransform(gd, transform, source, off, stored.Length);
        for (int i = 0; i < stored.Length; i++)
        {
            byte expected = Byte(HdrOutput.Output(new Vector3(stored[i]), HdrCurve.Neutral).X);
            if (gpu[i * 4] != expected) { Console.WriteLine($"[p989-proof] flag-off differs at {stored[i]:0.000}: {gpu[i * 4]} != {expected}"); ok = false; }
        }
        Console.WriteLine($"[p989-proof] flag-off samples={string.Join(',', Enumerable.Range(0, stored.Length).Select(i => gpu[i * 4]))}");
        return ok;
    }

    /// <summary>An opaque HUD pixel drawn after the graded transform comes back
    /// exactly as drawn.</summary>
    private bool CheckHudUngraded(GraphicsDevice gd, HdrOutputTransform transform, HdrGrade grade)
    {
        using var hudTarget = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        using var mid = FloatTexture(gd, Enumerable.Range(0, Width).Select(_ => (0.5f, 0.5f, 0.5f)).ToArray());
        gd.Textures[0] = null;
        transform.Render(mid, hudTarget, grade);
        using (var hudTex = SolidTexture(gd, new Color(128, 64, 0)))
        {
            var hud = new SpriteBatch(gd);
            gd.SetRenderTarget(hudTarget);
            hud.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            hud.Draw(hudTex, new Rectangle(0, 0, Width, Height), Color.White);
            hud.End();
            hud.Dispose();
        }
        var pixels = new Color[Width * Height];
        hudTarget.GetData(pixels);
        bool ok = pixels.All(p => p.R == 128 && p.G == 64 && p.B == 0);
        Console.WriteLine($"[p989-proof] hud-after-graded-transform pixel=({pixels[0].R},{pixels[0].G},{pixels[0].B}) expected=(128,64,0) ungraded={ok}");
        return ok;
    }

    /// <summary>
    /// The EXISTING bloom pass at the two settings. The scene stores a dim
    /// highlight (linear 1.0 -&gt; stored 1.0) and a bright one (linear 2.0 -&gt;
    /// stored ~1.24) on a black field: the sim threshold (linear 1.2 -&gt; stored
    /// 1.083) must leave the dim one alone and still bloom the bright one, and the
    /// 0.08 contribution must add far less glow than the P547 default. No extra
    /// pass is involved - this is the same Bloom.fx the frame already runs.
    /// </summary>
    private bool CheckBloom(GraphicsDevice gd, Effect effect)
    {
        const int w = 16, h = 4;
        using var bloom = new BloomPostProcessor(gd, effect);
        bloom.Polished = true;                       // the float-path composite pass
        float dim = 1.0f, bright = HdrOutput.ToStored(2.0f);
        var columns = new (float, float, float)[w];
        columns[2] = (dim, dim, dim);
        columns[12] = (bright, bright, bright);
        using var source = FloatTexture(gd, columns);
        using var target = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None);

        (float[] pixels, bool ran) Run(float threshold, float contribution)
        {
            bloom.BrightPassThreshold = threshold;
            bloom.BloomScale = contribution;
            gd.Textures[0] = null;
            gd.SetRenderTarget(target);
            gd.Clear(Color.Transparent);
            if (!bloom.Render(source)) return (Array.Empty<float>(), false);
            gd.SetRenderTarget(null);
            var data = new HalfVector4[w * h];
            target.GetData(data);
            var values = new float[w * h];
            for (int i = 0; i < data.Length; i++)
            {
                var v = data[i].ToVector4();
                values[i] = (v.X + v.Y + v.Z) / 3f;
            }
            return (values, true);
        }

        var (thresholdOnly, ranA) = Run(SimPalette.BloomThresholdStored, 1f);
        var (p547, ranB) = Run(BloomPostProcessor.DefaultThreshold, 1f);
        var (sim, ranC) = Run(SimPalette.BloomThresholdStored, SimPalette.BloomContribution);
        if (!ranA || !ranB || !ranC) { Console.WriteLine("[p989-proof] bloom composite did not run"); return false; }

        // The composite keeps headroom ON a bright texel, so glow is measured on
        // its neighbours (the dim one's neighbours are black, the bright one's too).
        static float Near(float[] p) => MathF.Max(MathF.Max(p[1], p[3]), MathF.Max(p[11], p[13]));
        static float NearDim(float[] p) => MathF.Max(p[1], p[3]);
        static float NearBright(float[] p) => MathF.Max(p[11], p[13]);
        // The dim highlight's own glow is small even under the P547 threshold
        // (the bright pass keeps its peak-channel extraction), so the rule is
        // RELATIVE: it glows at 0.8 and is gone at 1.2 linear.
        bool dimNotBloomed = NearDim(thresholdOnly) < 0.0005f && NearDim(p547) > 0.001f;
        bool brightBlooms = NearBright(thresholdOnly) > 0.001f;
        bool weakerGlow = NearBright(sim) < NearBright(thresholdOnly) * 0.25f;
        Console.WriteLine($"[p989-proof] bloom nearDim/nearBright: p547-threshold={NearDim(p547):0.0000}/{NearBright(p547):0.0000} "
            + $"sim-threshold={NearDim(thresholdOnly):0.0000}/{NearBright(thresholdOnly):0.0000} "
            + $"sim-full={NearDim(sim):0.0000}/{NearBright(sim):0.0000} (all={Near(sim):0.0000}) "
            + $"(dim-below-threshold={dimNotBloomed} bright-blooms={brightBlooms} weaker-glow={weakerGlow})");
        return dimNotBloomed && brightBlooms && weakerGlow;
    }


    /// <summary>
    /// P989 — the 4K fold experiment: FSR's RCAS output pass applies the SAME grade,
    /// so the extra full-resolution RGBA16F target (the one the unfolded path stores
    /// RCAS's result in) would not be needed. This renders a float scene through both
    /// paths with the real EASU/RCAS bytecode and compares the decoded 8-bit result,
    /// printing the max channel delta and the changed-pixel count. The measurement is
    /// the deliverable: the fold is only adopted when the paths are identical.
    /// </summary>
    private void MeasureFsrFold(GraphicsDevice gd, ShaderAssetCache cache, HdrOutputTransform transform)
    {
        const int outW = 128, outH = 72;
        // The FSR processor loads its own two shaders through TitleContainer, which
        // this tool links to the compiled-shader directory (LinkContent).
        using var fsr = new FsrPostProcessor(gd, cache);
        if (!fsr.Ready) { Console.WriteLine("[p989-proof] FSR shaders unavailable"); return; }
        fsr.SetSize(outW, outH, 0.75f, hdr: true);
        if (!fsr.FoldReady || fsr.Scene is null)
        {
            Console.WriteLine("[p989-proof] FSR fold passes unavailable");
            return;
        }

        // A scene with over-1.0 highlights, saturated colours and mid greys: what
        // the curve is for, and what a fold could get wrong.
        var scene = new Color[fsr.Scene.Width * fsr.Scene.Height];
        for (int y = 0; y < fsr.Scene.Height; y++)
            for (int x = 0; x < fsr.Scene.Width; x++)
            {
                int i = y * fsr.Scene.Width + x;
                scene[i] = new Color((x * 37 + y * 11) % 256, (x * 13 + y * 47) % 256, (x * 7 + y * 3) % 256);
            }
        using var sceneTex = new Texture2D(gd, fsr.Scene.Width, fsr.Scene.Height, false, SurfaceFormat.HalfVector4);
        var half = new HalfVector4[scene.Length];
        for (int i = 0; i < scene.Length; i++)
        {
            // Display-encoded stored values including headroom (>1.0).
            half[i] = new HalfVector4(scene[i].R / 255f * 1.4f, scene[i].G / 255f * 1.4f, scene[i].B / 255f * 1.4f, 1f);
        }
        sceneTex.SetData(half);

        var grade = SimPalette.Grade(requested: true, hdrActive: true, acesComparison: false);
        using var unfoldedTarget = new RenderTarget2D(gd, outW, outH, false, SurfaceFormat.Color, DepthFormat.None);
        using var foldedTarget = new RenderTarget2D(gd, outW, outH, false, SurfaceFormat.Color, DepthFormat.None);
        gd.Textures[0] = null;
        fsr.Render(sceneTex, unfoldedTarget, 0.2f, hdr: true);
        gd.Textures[0] = null;
        transform.Render(fsr.HdrUpscaled!, unfoldedTarget, grade);
        gd.Textures[0] = null;
        fsr.Render(sceneTex, foldedTarget, 0.2f, hdr: true, foldGrade: grade);
        gd.SetRenderTarget(null);

        var a = new Color[outW * outH];
        var b = new Color[outW * outH];
        unfoldedTarget.GetData(a);
        foldedTarget.GetData(b);
        int changed = 0, maxDelta = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Max(Math.Abs(a[i].R - b[i].R),
                Math.Max(Math.Abs(a[i].G - b[i].G), Math.Abs(a[i].B - b[i].B)));
            if (d != 0) changed++;
            maxDelta = Math.Max(maxDelta, d);
        }
        Console.WriteLine($"[p989-proof] fsr fold: changed={changed}/{a.Length} maxDelta={maxDelta}/255 "
            + $"equivalent={(changed == 0 ? "yes" : "no")}");
        Console.WriteLine(changed == 0
            ? "P989_PALETTE_FOLD_EQUIVALENT the folded FSR output pass matches the unfolded path exactly"
            : "P989_PALETTE_FOLD_MEASURED folded path differs by one 8-bit step - NOT adopted (separate transform kept)");
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static float Luma(Vector3 linear) => 0.2126f * linear.X + 0.7152f * linear.Y + 0.0722f * linear.Z;

    private static float Chroma(Vector3 encoded)
    {
        float max = MathF.Max(encoded.X, MathF.Max(encoded.Y, encoded.Z));
        float min = MathF.Min(encoded.X, MathF.Min(encoded.Y, encoded.Z));
        return max - min;
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static Texture2D FloatTexture(GraphicsDevice gd, (float R, float G, float B)[] values)
    {
        var texture = new Texture2D(gd, values.Length, 1, false, SurfaceFormat.HalfVector4);
        var data = new HalfVector4[values.Length];
        for (int i = 0; i < values.Length; i++) data[i] = new HalfVector4(values[i].R, values[i].G, values[i].B, 1f);
        texture.SetData(data);
        return texture;
    }

    private static Texture2D SolidTexture(GraphicsDevice gd, Color color)
    {
        var texture = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        texture.SetData(new[] { color });
        return texture;
    }

    /// <summary>Run the real output transform over <paramref name="source"/> into an
    /// 8-bit target and return the decoded RGBA bytes.</summary>
    private static byte[] RunTransform(GraphicsDevice gd, HdrOutputTransform transform, Texture2D source,
        HdrGrade grade, int width)
    {
        using var target = new RenderTarget2D(gd, width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        gd.Textures[0] = null;
        transform.Render(source, target, grade);
        var pixels = new Color[width * Height];
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
