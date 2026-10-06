using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;

/// <summary>
/// P547 — the <c>roster-capture vfx547</c> proof, exercising the GAME's custom
/// shader path end-to-end:
///   1) soft particles: smoke crossing a ship hull and an asteroid, near-fade-only
///      vs the linear-depth soft fade, measuring the hard-edge artifact as the
///      mean Sobel gradient in the intersection band (soft should drop it sharply);
///   2) bloom: dense combat + a station explosion, Off vs On, reporting the
///      blown-out share (On must stay ≤ ~20%);
///   3) cost: ms/frame at 1280×720 for Enhanced baseline, + soft, + bloom, and
///      the render-target memory added.
/// Everything runs through the game's <see cref="VfxPostProcessor"/> (compiled
/// .mgfxo loaded via <see cref="ShaderAssetCache"/>), so a missing shader makes
/// the relevant path degrade (soft/bloom report unavailable) instead of failing.
/// </summary>
internal sealed partial class RosterCapture
{
    private void RunVfx547(GraphicsDevice gd)
    {
        Directory.CreateDirectory(Path.Combine(_o.OutDir, "p547"));
        var sectorLight = new SectorLighting(
            new SimVec(0f, 0.5f, 1f), new SimVec(1f, 0.80f, 0.55f),
            new SimVec(0f, 0f, -1f), SectorLighting.AltColorConst,
            new SimVec(0.25f, 0.25f, 0.25f));

        Console.WriteLine("[roster-capture] P547 custom-shader proof (game classes):");

        using var shaders = ShaderAssetCache.ForDevice(gd);
        using var post = new VfxPostProcessor(gd, shaders);
        Console.WriteLine($"  shader effects loaded: depth={post.LinearDepthEffect != null} soft={post.SoftReady} bloom={post.BloomReady}");
        Console.WriteLine($"  loader: {shaders.LoggedFailureCount} shader load failure(s)");
        using var vfx = new VfxAssets(gd);
        using var smoke = new EnhancedSmokeField(gd, vfx, post.Soft);
        var original = ExplosionAssets.Load(gd, new DirectoryAssetSource(_o.ArtRoot));
        using var enh = new EnhancedExplosionRenderer(gd, vfx, original, smoke);
        using var orig = new ExplosionRenderer(gd, original);

        var shipRenderer = new ShipRenderer(gd);

        // 1) Soft particles (asteroid rock + station structure + ship hull),
        // near-fade vs soft. Asteroid/station use the compact procedural mesh
        // (where the 24u design softness is proportionate, matching in-game
        // ship scale); the ship is framed at its own raw FBX scale.
        RunSoftParticles(gd, post, shipRenderer, smoke, sectorLight, "asteroid");
        RunSoftParticles(gd, post, shipRenderer, smoke, sectorLight, "station");
        RunSoftParticles(gd, post, shipRenderer, smoke, sectorLight, "ship");

        // 2) Bloom — dense combat and a station explosion, Off vs On.
        if (post.BloomReady)
        {
            RunBloomDense(gd, post, enh, sectorLight);
            RunBloomStation(gd, post, enh, sectorLight);
        }
        else
            Console.WriteLine("  [roster-capture] bloom: effect unavailable — skipped (game falls back to off)");

        // 3) Cost + render-target memory.
        RunVfx547Cost(gd, post, shipRenderer, smoke, enh, sectorLight);
        ReportVfx547Memory(gd, post);

        original.Dispose();
        Console.WriteLine("[roster-capture] vfx547 PASS");
    }

    // ── soft particles: smoke crossing an occluder, near-fade vs soft ─────
    private void RunSoftParticles(GraphicsDevice gd, VfxPostProcessor post, ShipRenderer ship,
        EnhancedSmokeField smoke, SectorLighting sectorLight, string occluder)
    {
        const int W = 700, H = 394;
        post.SetSize(W, H);

        // Compact rendered radius for every occluder (asteroid/station/ship all
        // ~26u) so the 24u design softness is proportionate — matching how the
        // GAME renders ships (ShipRenderScale normalizes each hull to its real
        // length, tens of units, not the raw FBX's baked huge scale).
        float comp = 26f;
        float camFar = 6000f;   // the game's depth normalization range (softness 24u)
        var m = LoadModel("gt-fighter");
        float rr = RenderRadius(m);
        float shipScale = comp / rr;
        // Compensate the model's baked local offset so the scaled hull sits at
        // the scene origin (the fighter mesh is not centered on its own origin).
        Vector3 localCenter = ModelLocalCenter(m) * shipScale;
        XnaVec eye;
        Matrix view, proj;
        if (occluder == "ship")
        {
            eye = new XnaVec(0f, comp * 0.12f, comp * 2.4f);
            view = Matrix.CreateLookAt(eye, new XnaVec(0f, comp * 0.05f, 0f), XnaVec.Up);
            proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), W / (float)H, 1f, comp * 8f);
        }
        else
        {
            eye = new XnaVec(0f, 8f, comp * 2.6f);
            view = Matrix.CreateLookAt(eye, new XnaVec(0f, 0f, 0f), XnaVec.Up);
            proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), W / (float)H, 1f, comp * 8f);
        }

        // A volumetric slab of smoke puffs spanning the occluder (3 z-layers from
        // behind the hull to in front, widely in x) so the slab reliably crosses
        // each silhouette at a glancing angle: near-fade depth-clips it to the
        // silhouette (hard edge); soft fades it over the 24u softness (smooth).
        var puffs = new List<EnhancedSmokeField.Puff>();
        float span = comp * 2.2f;
        float zFront = comp;
        float[] layers = { zFront - comp * 0.55f, zFront, zFront + comp * 0.65f };
        const int NX = 15;
        for (int li = 0; li < layers.Length; li++)
            for (int i = 0; i < NX; i++)
            {
                float x = MathHelper.Lerp(-span, span, i / (float)(NX - 1));
                puffs.Add(new EnhancedSmokeField.Puff(new XnaVec(x, 0f, layers[li]), span / NX * 0.55f, 0.85f));
            }

        var ctx = post.Context(1f, camFar);
        Matrix ShipWorld() => Matrix.CreateTranslation(-localCenter) * Matrix.CreateTranslation(-eye);

        // Diagnostic: verify the depth pass actually wrote the occluder into the
        // depth target (some near pixel should be well below the sky far).
        if (post.DepthReady)
        {
            gd.SetRenderTarget(post.DepthRT);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
            if (occluder == "ship")
                ship.DrawDepth(m, ShipWorld(), view, proj, shipScale, post.LinearDepthEffect!, 1f, camFar);
            else if (occluder == "station")
                using (var dst = new StationRenderer(gd))
                    dst.DrawDepth(Matrix.CreateScale(comp) * Matrix.CreateTranslation(-eye), view, proj, post.LinearDepthEffect!, 1f, camFar);
            else
                using (var drock = new AsteroidRenderer(gd))
                    drock.DrawDepth(Matrix.CreateScale(comp) * Matrix.CreateTranslation(-eye), view, proj, post.LinearDepthEffect!, 1f, camFar);
            gd.SetRenderTarget(null);
            var dd = new Color[W * H];
            post.DepthRT!.GetData(dd);
            float minD = 1f; int nearCount = 0;
            for (int i = 0; i < dd.Length; i++)
            {
                float d = LinearDepth.Decode(dd[i]);
                if (d < minD) minD = d;
                if (d < 0.9f) nearCount++;
            }
            Console.WriteLine($"  depth-diagnostic[{occluder}]: min={minD:0.####} corner(sky)={LinearDepth.Decode(dd[2 * W + 2]):0.####} nearPx={(double)nearCount / dd.Length:P1} (expect min << corner)");
        }

        // near-fade only (no depth): no depth pass, soft context null.
        var a = RenderSoftCase(gd, W, H, view, proj, eye, post, ship, shipScale, occluder, localCenter, camFar, puffs, smoke, sectorLight, soft: null, withDepth: false);
        // soft (depth-fade): depth pass then sample.
        var b = RenderSoftCase(gd, W, H, view, proj, eye, post, ship, shipScale, occluder, localCenter, camFar, puffs, smoke, sectorLight, soft: ctx, withDepth: true);

        double gradA = MeanGradientInBand(gd, a, H / 2 - 22, H / 2 + 22);
        double gradB = MeanGradientInBand(gd, b, H / 2 - 22, H / 2 + 22);
        double reduction = gradA > 0 ? (1.0 - gradB / gradA) : 0.0;
        Console.WriteLine($"  SOFT[{occluder}]: near-fade-only grad={gradA:0.####} | soft grad={gradB:0.####}  (hard-edge reduction {reduction:P0})");

        DoubleSheet547(gd, a, b, W, H,
            $"SOFT PARTICLES {occluder}: near-fade-only | depth-soft  (band grad {gradA:0.###} -> {gradB:0.###}, -{reduction:P0})",
            "p547", $"soft_{occluder}.jpg");
    }

    /// <summary>Approximate the model's world-transform geometric center (union
    /// of mesh bounding spheres, each transformed by its ParentBone.Transform),
    /// used to compensate a baked mesh offset when compact-normalizing the hull
    /// for the soft-particle proof.</summary>
    private static Vector3 ModelLocalCenter(XnaModel m)
    {
        var acc = Vector3.Zero; int n = 0;
        foreach (var mesh in m.Meshes)
        {
            var bs = mesh.BoundingSphere;
            acc += Vector3.Transform(bs.Center, mesh.ParentBone.Transform);
            n++;
        }
        return n == 0 ? Vector3.Zero : acc / n;
    }

    private RenderTarget2D RenderSoftCase(GraphicsDevice gd, int W, int H, Matrix view, Matrix proj, XnaVec eye,
        VfxPostProcessor post, ShipRenderer ship, float shipScale, string occluder, Vector3 localCenter, float camFar, List<EnhancedSmokeField.Puff> puffs,
        EnhancedSmokeField smoke, SectorLighting sectorLight, SoftParticleContext? soft, bool withDepth)
    {
        // Build the occluder world (at origin, eye-relative).
        Matrix world;
        if (occluder == "ship")
        {
            var m = LoadModel("gt-fighter");
            world = Matrix.CreateTranslation(-localCenter) * Matrix.CreateTranslation(-eye);
            Texture2D? tex = null;
            try { tex = LoadTexture("gt-fighter"); } catch { /* untextured fallback ok */ }
            if (withDepth)
            {
                gd.SetRenderTarget(post.DepthRT);
                gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
                ship.DrawDepth(m, world, view, proj, shipScale, post.LinearDepthEffect!, 1f, camFar);
            }
            var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(rt);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.026f, 0.029f, 0.043f), 1f, 0);
            gd.DepthStencilState = DepthStencilState.Default;
            ship.Draw(m, world, view, proj, 1f, shipScale, tex, sectorLight);
            // Erase the occluder's COLOUR but keep its DEPTH, so the smoke below is
            // depth-clipped (near-fade) / depth-faded (soft) at the silhouette and
            // the gradient metric isolates the smoke's edge — not the hull's.
            gd.Clear(ClearOptions.Target, new Color(0.052f, 0.058f, 0.074f), 1f, 0);
            smoke.Draw(puffs, eye, view, proj, sectorLight, ParticleNearFade.Flight, soft);
            gd.SetRenderTarget(null);
            return rt;
        }
        else
        {
            using var solid = occluder == "station"
                ? (IDisposable)new StationRenderer(gd)
                : new AsteroidRenderer(gd);
            world = Matrix.CreateScale(26f) * Matrix.CreateTranslation(-eye);
            if (withDepth)
            {
                gd.SetRenderTarget(post.DepthRT);
                gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
                if (occluder == "station")
                    ((StationRenderer)solid).DrawDepth(world, view, proj, post.LinearDepthEffect!, 1f, camFar);
                else
                    ((AsteroidRenderer)solid).DrawDepth(world, view, proj, post.LinearDepthEffect!, 1f, camFar);
            }
            var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(rt);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.022f, 0.027f, 0.044f), 1f, 0);
            gd.DepthStencilState = DepthStencilState.Default;
            if (occluder == "station")
                ((StationRenderer)solid).Draw(world, view, proj, StationRenderer.FriendlyColor, sectorLight);
            else
                ((AsteroidRenderer)solid).Draw(world, view, proj);
            // Keep depth, erase colour (isolate the smoke's edge, not the rock's).
            gd.Clear(ClearOptions.Target, new Color(0.055f, 0.061f, 0.078f), 1f, 0);
            smoke.Draw(puffs, eye, view, proj, sectorLight, ParticleNearFade.Flight, soft);
            gd.SetRenderTarget(null);
            return rt;
        }
    }

    // ── bloom proofs ──────────────────────────────────────────────────────
    private void RunBloomDense(GraphicsDevice gd, VfxPostProcessor post, EnhancedExplosionRenderer enh,
        SectorLighting sectorLight)
    {
        const int W = 950, H = 550;
        post.SetSize(W, H);
        var eye = new XnaVec(0f, 2f, 26f);
        var view = Matrix.CreateLookAt(new XnaVec(0f, 0f, 0f), new XnaVec(0f, 0f, 0f) - eye, XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, W / (float)H, 0.5f, 300f);
        long nowMs = Environment.TickCount64;
        var explosions = new List<ExplosionEntity>();
        for (int i = 0; i < 6; i++)
        {
            float ang = i * MathHelper.TwoPi / 6f + 0.3f;
            var p = new XnaVec(MathF.Cos(ang) * 2.5f, MathF.Sin(i) * 1.4f, 8f + MathF.Sin(ang) * 2.5f);
            explosions.Add(MakeExplosion546(ExplosionType.SmallShip, p, nowMs, 0.9f));
        }
        // Off: straight into a color target (the P546 dense-combat baseline:
        // near-fade OFF so the smoke is maximally present, matching P546).
        var offRt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(offRt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.007f, 0.010f, 0.019f), 1f, 0);
        enh.Draw(explosions, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Off, null);
        gd.SetRenderTarget(null);
        double off = BlownOutShare(gd, offRt);

        // On: scene into the scene target, then bloom onto a final target.
        gd.SetRenderTarget(post.SceneRT!);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.007f, 0.010f, 0.019f), 1f, 0);
        enh.Draw(explosions, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Off, null);
        var onRt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(onRt);
        gd.Clear(Color.Black);
        post.Bloom!.Render(post.SceneRT!);
        gd.SetRenderTarget(null);
        double on = BlownOutShare(gd, onRt);

        Console.WriteLine($"  BLOOM dense combat: Off={off:P2} | On={on:P2}  (On must stay ≤ 20%; baseline ~15%)");
        DoubleSheet547(gd, offRt, onRt, W, H,
            $"DENSE COMBAT (6 deaths)  bloom Off {off:P2} | On {on:P2} (≤20% bar)",
            "p547", "bloom_dense.jpg");
    }

    private void RunBloomStation(GraphicsDevice gd, VfxPostProcessor post, EnhancedExplosionRenderer enh,
        SectorLighting sectorLight)
    {
        const int W = 850, H = 470;
        post.SetSize(W, H);
        var eye = new XnaVec(0f, 6f, 34f);
        var view = Matrix.CreateLookAt(new XnaVec(0f, 0f, 0f), new XnaVec(0f, 0f, 0f) - eye, XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, W / (float)H, 0.5f, 400f);
        long nowMs = Environment.TickCount64;
        var explosions = new List<ExplosionEntity>
        {
            MakeExplosion546(ExplosionType.BigShip, new XnaVec(0f, 0f, 4f), nowMs, 1.0f),
        };

        var offRt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(offRt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.031f, 0.051f, 0.071f), 1f, 0);
        enh.Draw(explosions, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Flight, null);
        gd.SetRenderTarget(null);
        double off = BlownOutShare(gd, offRt);

        gd.SetRenderTarget(post.SceneRT!);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.031f, 0.051f, 0.071f), 1f, 0);
        enh.Draw(explosions, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Flight, null);
        var onRt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(onRt);
        gd.Clear(Color.Black);
        post.Bloom!.Render(post.SceneRT!);
        gd.SetRenderTarget(null);
        double on = BlownOutShare(gd, onRt);

        Console.WriteLine($"  BLOOM station explosion: Off={off:P2} | On={on:P2}");
        DoubleSheet547(gd, offRt, onRt, W, H,
            $"STATION EXPLOSION  bloom Off {off:P2} | On {on:P2}",
            "p547", "bloom_station.jpg");
    }

    // ── cost + memory ─────────────────────────────────────────────────────
    private void RunVfx547Cost(GraphicsDevice gd, VfxPostProcessor post, ShipRenderer ship,
        EnhancedSmokeField smoke, EnhancedExplosionRenderer enh, SectorLighting sectorLight)
    {
        const int W = 1280, H = 720;
        post.SetSize(W, H);
        var m = LoadModel("gt-fighter");
        float rr = RenderRadius(m);
        float D = rr * 2.0f;
        var eye = new XnaVec(0f, rr * 0.12f, D);
        var view = Matrix.CreateLookAt(eye, new XnaVec(0f, rr * 0.05f, 0f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), 16f / 9f, 1f, D * 8f);
        long nowMs = Environment.TickCount64;
        var e = MakeExplosion546(ExplosionType.BigShip, new XnaVec(0f, 0f, 0f), nowMs, 0.5f);
        var list = new List<ExplosionEntity> { e };
        var shipWorld = Matrix.CreateTranslation(-eye);
        var ctx = post.Context(1f, 6000f);
        const int Iters = 30;

        // Preallocate the color / final targets so timing measures ONLY the draw
        // work (per-iteration RT allocation would dominate and distort the deltas).
        var rtA = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var rtFinal = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);

        // Enhanced baseline (no soft, no bloom): direct into rtA.
        double baseMs = TimeIt547(gd, () =>
        {
            gd.SetRenderTarget(rtA);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            enh.Draw(list, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Flight, null);
        }, Iters);

        // + soft particles: depth pass (ship) then smoke samples it.
        double softMs = TimeIt547(gd, () =>
        {
            gd.SetRenderTarget(post.DepthRT);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
            ship.DrawDepth(m, shipWorld, view, proj, 1f, post.LinearDepthEffect!, 1f, 6000f);
            gd.SetRenderTarget(rtA);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            enh.Draw(list, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Flight, ctx);
        }, Iters);

        // + bloom: scene into the scene target then bloom onto a final target.
        double bloomMs = TimeIt547(gd, () =>
        {
            gd.SetRenderTarget(post.SceneRT);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            enh.Draw(list, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Flight, null);
            gd.SetRenderTarget(rtFinal);
            gd.Clear(Color.Black);
            post.Bloom!.Render(post.SceneRT!);
        }, Iters);

        rtA.Dispose();
        rtFinal.Dispose();

        Console.WriteLine($"  frame cost @1280x720 (avg {Iters}): Enhanced={baseMs:F3}ms | +soft={softMs:F3}ms (Δ{softMs - baseMs:+0.000;-0.000}) | +bloom={bloomMs:F3}ms (Δ{bloomMs - baseMs:+0.000;-0.000})");
    }

    private static double TimeIt547(GraphicsDevice gd, Action render, int iters)
    {
        var sw = new Stopwatch();
        for (int i = 0; i < 3; i++) render();
        sw.Restart();
        for (int i = 0; i < iters; i++) render();
        sw.Stop();
        gd.SetRenderTarget(null);
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    private static void ReportVfx547Memory(GraphicsDevice gd, VfxPostProcessor post)
    {
        long MB(RenderTarget2D? rt) => rt is null ? 0L : (long)rt.Width * rt.Height * 4L;
        long total = MB(post.DepthRT) + MB(post.SceneRT);
        Console.WriteLine($"  RT memory (RGBA8): depth={MB(post.DepthRT) / 1024.0 / 1024.0:F2} MiB scene={MB(post.SceneRT) / 1024.0 / 1024.0:F2} MiB" +
            $" (total {total / 1024.0 / 1024.0:F2} MiB)");
    }

    // ── metrics + sheets ──────────────────────────────────────────────────
    /// <summary>Mean Sobel gradient magnitude of luminance in a horizontal band
    /// [y0,y1] — the hard-edge artifact metric. Sharp cut edges produce a high
    /// mean; the depth-soft fade spreads the transition so it drops sharply.</summary>
    private static double MeanGradientInBand(GraphicsDevice gd, RenderTarget2D rt, int y0, int y1)
    {
        var data = new Color[rt.Width * rt.Height];
        rt.GetData(data);
        double sum = 0; int count = 0;
        for (int y = Math.Max(1, y0); y < Math.Min(rt.Height - 1, y1); y++)
        {
            for (int x = 1; x < rt.Width - 1; x++)
            {
                float Lum(int xx, int yy)
                {
                    var c = data[yy * rt.Width + xx];
                    return 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                }
                float gx = Lum(x + 1, y - 1) + 2 * Lum(x + 1, y) + Lum(x + 1, y + 1)
                         - Lum(x - 1, y - 1) - 2 * Lum(x - 1, y) - Lum(x - 1, y + 1);
                float gy = Lum(x - 1, y + 1) + 2 * Lum(x, y + 1) + Lum(x + 1, y + 1)
                         - Lum(x - 1, y - 1) - 2 * Lum(x, y - 1) - Lum(x + 1, y - 1);
                sum += Math.Sqrt(gx * gx + gy * gy);
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    private void DoubleSheet547(GraphicsDevice gd, RenderTarget2D a, RenderTarget2D b, int W, int H,
        string caption, string subdir, string fileName)
    {
        int WW = W * 2 + 16;
        using var combo = new RenderTarget2D(gd, WW, H + 30, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(combo);
        gd.Clear(Color.Black);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
        _batch.Draw(a, new Rectangle(0, 30, W, H), Color.White);
        _batch.Draw(b, new Rectangle(W + 8, 30, W, H), Color.White);
        var sz = _font.MeasureString(Ascii(caption));
        _batch.DrawString(_font, Ascii(caption), new Vector2(WW / 2f - sz.X / 2f, 6f), Color.White);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(Path.Combine(subdir, fileName), combo);
        a.Dispose(); b.Dispose();
    }

    /// <summary>Map non-ASCII glyphs to font-safe ASCII (the HUD SpriteFont lacks
    /// ≤, →, −, —, …, curly quotes).</summary>
    private static string Ascii(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\u2264') { sb.Append("<="); continue; }
            if (c == '\u2192' || c == '\u2013' || c == '\u2014' || c == '\u2212') { sb.Append('-'); continue; }
            if (c == '\u2026') { sb.Append("..."); continue; }
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
            else if (c == ' ') sb.Append(' ');
        }
        return sb.ToString();
    }
}
