using System.Diagnostics;
using System.Collections.Generic;
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
/// P546 — the <c>roster-capture vfx</c> proof, now calling the GAME's classes
/// (not the spike's copies): renders the NATIVE-art explosion path
/// (<see cref="ExplosionRenderer"/>) side-by-side with the ENHANCED path
/// (<see cref="EnhancedExplosionRenderer"/> + <see cref="EnhancedSmokeField"/>,
/// both using the game's <see cref="VfxAssets"/>) for a fighter blast and a
/// station blast over a time series, plus the dense-combat blown-out-share and
/// the player-flight-camera missile-volley smoke-readability proofs (Native vs
/// Enhanced vs Enhanced+near-fade), and reports per-frame costs + texture
/// memory. Everything here exercises the real code path the game renders with.
/// </summary>
internal sealed partial class RosterCapture
{
    private enum VfxColumn { Native = 0, Enhanced = 1, EnhancedNear = 2 }

    private void RunVfx546(GraphicsDevice gd)
    {
        Directory.CreateDirectory(Path.Combine(_o.OutDir, "p546"));
        var sectorLight = new SectorLighting(
            new SimVec(0f, 0.5f, 1f),
            new SimVec(1f, 0.80f, 0.55f),
            new SimVec(0f, 0f, -1f),
            SectorLighting.AltColorConst,
            new SimVec(0.25f, 0.25f, 0.25f));

        Console.WriteLine("[roster-capture] P546 VFX proof (game classes):");

        var original = ExplosionAssets.Load(gd, new DirectoryAssetSource(_o.ArtRoot));
        using var vfx = new VfxAssets(gd);
        using var smoke = new EnhancedSmokeField(gd, vfx);
        using var enh = new EnhancedExplosionRenderer(gd, vfx, original, smoke);
        using var orig = new ExplosionRenderer(gd, original);
        using var native = NativeEffectTextures.Load(gd, new DirectoryAssetSource(_o.ArtRoot));
        using var mTrail = new MissileTrailRenderer(gd, native, smoke);

        ReportVfxMemory(gd, vfx, original);

        // 1) Explosion time-series contact sheets (fighter + station), Native vs Enhanced.
        RenderExplosionSeries546(gd, "fighter", ExplosionType.SmallShip, "gt-fighter", orig, enh, sectorLight);
        RenderExplosionSeries546(gd, "station", ExplosionType.BigShip, "ic-carrier", orig, enh, sectorLight);

        // 2) Dense-combat blown-out share (Native vs Enhanced).
        RunDenseCombat546(gd, orig, enh, sectorLight);

        // 3) Player-flight-camera missile volley smoke readability.
        RunVolley546(gd, mTrail, smoke, sectorLight);

        // 4) Per-frame cost at fixed 1280x720 + VRAM added.
        RunFrameTiming546(gd, orig, enh, sectorLight);

        Console.WriteLine("[roster-capture] vfx546 PASS");
        original.Dispose();
    }

    // ── texture memory ──────────────────────────────────────────────────────
    private static void ReportVfxMemory(GraphicsDevice gd, VfxAssets vfx, ExplosionAssets orig)
    {
        static long Total(Texture2D[] a) => a.Sum(t => (long)t.Width * t.Height * 4L);
        long cc0 = Total(vfx.FireGlow) + Total(vfx.FireBall) + Total(vfx.Smoke)
                 + Total(vfx.WispySmoke) + Total(vfx.DiscSmoke) + (64L * 64 * 4);
        Console.WriteLine($"  CC0 atlas VRAM (uploaded, RGBA8): {cc0 / 1024.0 / 1024.0:F2} MiB");
        if (orig.Films.Count > 0)
        {
            long ntv = orig.Films.Values.Sum(arr => arr.Sum(t => (long)t.Width * t.Height * 4L))
                     + (orig.Shockwave is { } sw ? (long)sw.Width * sw.Height * 4 : 0);
            Console.WriteLine($"  Original native explosion VRAM: {ntv / 1024.0 / 1024.0:F2} MiB");
        }
    }

    // ── (1) explosion time series ───────────────────────────────────────────
    private void RenderExplosionSeries546(GraphicsDevice gd, string name, int type,
        string shipAsset, ExplosionRenderer orig, EnhancedExplosionRenderer enh, SectorLighting sectorLight)
    {
        var victim = LoadModel(shipAsset);
        float rr = RenderRadius(victim);
        float D = Math.Max(rr * 2.4f, 160f);
        var eye = new XnaVec(0f, rr * 0.10f, D);
        var view = Matrix.CreateLookAt(eye, new XnaVec(0f, rr * 0.05f, 0f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), 16f / 9f, 1f, D * 8f);
        const int W = 520, H = 292;
        float[] times = { 0.1f, 0.3f, 0.6f, 1.0f, 2.0f };

        long nowMs = Environment.TickCount64;
        var cell = new RenderTarget2D[times.Length][];   // [time][column]
        for (int ti = 0; ti < times.Length; ti++)
        {
            float t = times[ti];
            var e = MakeExplosion546(type, new XnaVec(0f, 0f, 0f), nowMs, t);
            cell[ti] = new RenderTarget2D[2];
            for (int col = 0; col < 2; col++)
            {
                using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
                gd.SetRenderTarget(rt);
                gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.030f, 0.032f, 0.048f), 1f, 0);
                gd.DepthStencilState = DepthStencilState.Default;
                var list = new List<ExplosionEntity> { e };
                if (col == (int)VfxColumn.Native)
                    orig.Draw(list, 1, nowMs, eye, view, proj);
                else
                    enh.Draw(list, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Off, ParticleFarFade.Off, default);
                gd.DepthStencilState = DepthStencilState.None;
                gd.SetRenderTarget(null);
                var keep = Copy546(gd, rt, W, H);
                cell[ti][col] = keep;
            }
        }
        AssembleExplosionSheet546(gd, name, times, cell, W, H);
        foreach (var row in cell) foreach (var c in row) c.Dispose();
        Console.WriteLine($"[roster-capture] vfx546 {name} explosion series written p546/{name}.jpg");
    }

    private static ExplosionEntity MakeExplosion546(int type, XnaVec pos, long nowMs, float tSec)
        => new() { Type = type, Position = new SimVec(pos.X, pos.Y, pos.Z), ClusterId = 1,
                   Velocity = new SimVec(0f, 0f, 0f), ReceivedLocalMs = nowMs - (long)(tSec * 1000f) };

    private void AssembleExplosionSheet546(GraphicsDevice gd, string name, float[] times,
        RenderTarget2D[][] cell, int W, int H)
    {
        const int LabelH = 26, HeaderH = 40;
        int sheetW = W * 2 + 8;
        int sheetH = LabelH + HeaderH + times.Length * H;
        string[] headers = { "NATIVE (exp art)", "ENHANCED (CC0 fireball + smoke)" };
        using var sheet = new RenderTarget2D(gd, sheetW, sheetH, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(sheet);
        gd.Clear(new Color(0.02f, 0.028f, 0.044f));
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
        for (int c = 0; c < 2; c++)
        {
            var sz = _font.MeasureString(headers[c]);
            _batch.DrawString(_font, headers[c], new Vector2(c * (W + 8) + W / 2f - sz.X / 2f, 8f), Color.Orange);
        }
        for (int ti = 0; ti < times.Length; ti++)
        {
            int y = LabelH + HeaderH + ti * H;
            _batch.DrawString(_font, $"t = {times[ti]:F1} s", new Vector2(6f, y + H / 2f - 8f), Color.LightGreen);
            for (int c = 0; c < 2; c++)
                _batch.Draw(cell[ti][c], new Rectangle(c * (W + 8) + 8, y, W, H), Color.White);
        }
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(Path.Combine("p546", name + ".jpg"), sheet);
    }

    // ── (2) dense combat blown-out share ────────────────────────────────────
    private void RunDenseCombat546(GraphicsDevice gd, ExplosionRenderer orig,
        EnhancedExplosionRenderer enh, SectorLighting sectorLight)
    {
        var eye = new XnaVec(0f, 2f, 26f);
        var view = Matrix.CreateLookAt(new XnaVec(0f, 0f, 0f), new XnaVec(0f, 0f, 0f) - eye, XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 16f / 9f, 0.5f, 300f);
        const int W = 950, H = 550;

        long nowMs = Environment.TickCount64;
        var explosions = new List<ExplosionEntity>();
        for (int i = 0; i < 6; i++)
        {
            float ang = i * MathHelper.TwoPi / 6f + 0.3f;
            var p = new XnaVec(MathF.Cos(ang) * 2.5f, MathF.Sin(i) * 1.4f, 8f + MathF.Sin(ang) * 2.5f);
            explosions.Add(MakeExplosion546(ExplosionType.SmallShip, p, nowMs, 0.9f));
        }

        var o = Shot546(gd, W, H, view, proj, eye, null, explosions,
            r => orig.Draw(explosions, 1, nowMs, eye, view, proj));
        var e = Shot546(gd, W, H, view, proj, eye, null, explosions,
            r => enh.Draw(explosions, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Off, ParticleFarFade.Off, default));
        Console.WriteLine($"  dense-combat blown-out  Native={o.Share:P2}  Enhanced={e.Share:P2}");
        DoubleSheet546(gd, o.Rt, e.Rt, W, H,
            $"dense combat 6 deaths  blown-out  Native {o.Share:P2} | Enhanced {e.Share:P2}",
            "p546", "dense_combat.jpg");
    }

    // ── (3) missile volley smoke readability (player flight camera) ────────
    private void RunVolley546(GraphicsDevice gd, MissileTrailRenderer mTrail, EnhancedSmokeField smoke,
        SectorLighting sectorLight)
    {
        var eye = new XnaVec(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(XnaVec.Zero, new XnaVec(0f, 0f, 40f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 16f / 9f, 0.5f, 400f);
        const int W = 930, H = 570;
        var near = ParticleNearFade.Flight;

        float[] times = { 0.2f, 0.5f, 1.0f };
        foreach (var t in times)
        {
            var pool = new MissileTrailParticles();
            var origins = new[]
            {
                new XnaVec(0f, 0f, 8f), new XnaVec(-1.5f, 1.2f, 8f),
                new XnaVec(1.5f, 1.2f, 8f), new XnaVec(0f, -1.8f, 8f),
            };
            var dirs = new[]
            {
                new XnaVec(0f, 0f, 42f), new XnaVec(-2f, 1.6f, 42f),
                new XnaVec(2f, 1.6f, 42f), new XnaVec(0f, -2.2f, 42f),
            };
            for (int i = 0; i < 4; i++) StepMissile(pool, t, origins[i], dirs[i]);
            var views = pool.Enumerate().ToList();

            // Column 0: Native (P545 dim additive smoke).
            var natRt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(natRt);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.003f, 0.005f, 0.011f), 1f, 0);
            mTrail.Draw(views, eye, view, proj);
            gd.SetRenderTarget(null);
            double natShared = BlownOutShare(gd, natRt);

            // Column 1: Enhanced — animated CC0 smoke, alpha-lit, no near-fade.
            var enRt = RenderVolley546(gd, W, H, view, proj, eye, smoke, views, sectorLight, ParticleNearFade.Off, out double enShare);

            // Column 2: Enhanced + near-fade.
            var nfRt = RenderVolley546(gd, W, H, view, proj, eye, smoke, views, sectorLight, near, out double nfShare);

            double natCover = CoverageOf546(gd, W, H, r => mTrail.Draw(views, eye, view, proj));
            double enCover = CoverageOf546(gd, W, H, r => RenderVolleyInto546(r, view, proj, eye, smoke, views, sectorLight, ParticleNearFade.Off));
            double nfCover = CoverageOf546(gd, W, H, r => RenderVolleyInto546(r, view, proj, eye, smoke, views, sectorLight, near));
            double reduction = enCover > 0 ? (1.0 - nfCover / enCover) : 0.0;

            Console.WriteLine($"  VOLLEY t={t:F1}s  blown-out native={natShared:P2} enh={enShare:P2} enh+near={nfShare:P2}"
                + $"   smoke-cover native={natCover:P2} enh={enCover:P2} enh+near={nfCover:P2} (near-fade reduction {reduction:P0})");
            TripleSheet546(gd, natRt, enRt, nfRt, W, H,
                $"4-MISSILE VOLLEY t={t:F1}s  Native | Enhanced CC0 | Enhanced+near-fade (reduction {reduction:P0})",
                "p546", $"volley_t{t:F1}.jpg");
        }
    }

    private static double CoverageOf546(GraphicsDevice gd, int W, int H, Action<RenderTarget2D> draw)
    {
        using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
        gd.DepthStencilState = DepthStencilState.Default;
        draw(rt);
        gd.SetRenderTarget(null);
        var data = new Color[W * H];
        rt.GetData(data);
        int cover = 0;
        foreach (var c in data) if (c.A >= 128) cover++;
        return (double)cover / data.Length;
    }

    private static void RenderVolleyInto546(RenderTarget2D rt, Matrix view, Matrix proj, XnaVec eye,
        EnhancedSmokeField smoke, List<TrailParticleView> views, SectorLighting sectorLight, ParticleNearFade near)
    {
        var gd = rt.GraphicsDevice;
        var puffs = new List<EnhancedSmokeField.Puff>();
        foreach (var v in views)
        {
            float rem = MathHelper.Clamp(v.RemainingFrac, 0f, 1f);
            if (v.Kind != TrailParticleKind.Smoke) continue;
            float half = v.Size * 2.4f * (0.5f + 2.2f * (1f - rem));
            puffs.Add(new EnhancedSmokeField.Puff(new XnaVec(v.Position.X, v.Position.Y, v.Position.Z), half, rem));
        }
        smoke.Draw(puffs, eye, view, proj, sectorLight, near, ParticleFarFade.Off, default);
    }

    private RenderTarget2D RenderVolley546(GraphicsDevice gd, int W, int H, Matrix view, Matrix proj,
        XnaVec eye, EnhancedSmokeField smoke, List<TrailParticleView> views, SectorLighting sectorLight,
        ParticleNearFade near, out double share)
    {
        var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.003f, 0.005f, 0.011f), 1f, 0);
        gd.DepthStencilState = DepthStencilState.Default;
        var puffs = new List<EnhancedSmokeField.Puff>();
        foreach (var v in views)
        {
            float rem = MathHelper.Clamp(v.RemainingFrac, 0f, 1f);
            if (v.Kind != TrailParticleKind.Smoke) continue;
            float half = v.Size * 2.4f * (0.5f + 2.2f * (1f - rem));
            puffs.Add(new EnhancedSmokeField.Puff(new XnaVec(v.Position.X, v.Position.Y, v.Position.Z), half, rem));
        }
        smoke.Draw(puffs, eye, view, proj, sectorLight, near, ParticleFarFade.Off, default);
        gd.SetRenderTarget(null);
        share = BlownOutShare(gd, rt);
        return rt;
    }

    // ── (4) per-frame cost ──────────────────────────────────────────────────
    private void RunFrameTiming546(GraphicsDevice gd, ExplosionRenderer orig,
        EnhancedExplosionRenderer enh, SectorLighting sectorLight)
    {
        const int W = 1280, H = 720;
        var m = LoadModel("gt-fighter");
        float rr = RenderRadius(m);
        float D = rr * 2.0f;
        var eye = new XnaVec(0f, rr * 0.12f, D);
        var view = Matrix.CreateLookAt(eye, new XnaVec(0f, rr * 0.05f, 0f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), 16f / 9f, 1f, D * 8f);
        long nowMs = Environment.TickCount64;
        var e = MakeExplosion546(ExplosionType.BigShip, new XnaVec(0f, 0f, 0f), nowMs, 0.5f);
        var list = new List<ExplosionEntity> { e };

        const int Iters = 30;
        double origMs = TimeIt546(gd, W, H, () =>
        {
            using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(rt);
            gd.Clear(Color.Black);
            orig.Draw(list, 1, nowMs, eye, view, proj);
            gd.SetRenderTarget(null);
        }, Iters);

        double enhMs = TimeIt546(gd, W, H, () =>
        {
            using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(rt);
            gd.Clear(Color.Black);
            enh.Draw(list, 1, nowMs, eye, view, proj, sectorLight, ParticleNearFade.Flight, ParticleFarFade.Off, default);
            gd.SetRenderTarget(null);
        }, Iters);

        Console.WriteLine($"  frame cost @1280x720 (avg {Iters}): Native={origMs:F3}ms  Enhanced={enhMs:F3}ms  Δ={enhMs - origMs:+0.000;-0.000}ms ({(origMs > 0 ? (enhMs / origMs - 1.0) * 100.0 : 0):+0.0;-0.0}%)");
    }

    private static double TimeIt546(GraphicsDevice gd, int W, int H, Action render, int iters)
    {
        var sw = new Stopwatch();
        for (int i = 0; i < 3; i++) render();
        sw.Restart();
        for (int i = 0; i < iters; i++) render();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    // ── shared helpers ──────────────────────────────────────────────────────
    private (RenderTarget2D Rt, double Share) Shot546(GraphicsDevice gd, int W, int H, Matrix view, Matrix proj,
        XnaVec eye, XnaModel? bg, List<ExplosionEntity> explosions, Action<RenderTarget2D> draw)
    {
        var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(0.007f, 0.010f, 0.019f), 1f, 0);
        gd.DepthStencilState = DepthStencilState.Default;
        draw(rt);
        gd.SetRenderTarget(null);
        double share = BlownOutShare(gd, rt);
        return (rt, share);
    }

    private RenderTarget2D Copy546(GraphicsDevice gd, RenderTarget2D src, int W, int H)
    {
        var keep = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(keep);
        gd.Clear(Color.Black);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
        _batch.Draw(src, new Rectangle(0, 0, W, H), Color.White);
        _batch.End();
        gd.SetRenderTarget(null);
        return keep;
    }

    private void DoubleSheet546(GraphicsDevice gd, RenderTarget2D a, RenderTarget2D b, int W, int H,
        string caption, string subdir, string fileName)
    {
        int WW = W * 2 + 16;
        using var combo = new RenderTarget2D(gd, WW, H + 30, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(combo);
        gd.Clear(Color.Black);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
        _batch.Draw(a, new Rectangle(0, 30, W, H), Color.White);
        _batch.Draw(b, new Rectangle(W + 8, 30, W, H), Color.White);
        var sz = _font.MeasureString(caption);
        _batch.DrawString(_font, caption, new Vector2(WW / 2f - sz.X / 2f, 6f), Color.White);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(Path.Combine(subdir, fileName), combo);
        a.Dispose(); b.Dispose();
    }

    private void TripleSheet546(GraphicsDevice gd, RenderTarget2D a, RenderTarget2D b, RenderTarget2D c,
        int W, int H, string caption, string subdir, string fileName)
    {
        int WW = W * 3 + 16;
        using var combo = new RenderTarget2D(gd, WW, H + 30, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(combo);
        gd.Clear(Color.Black);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
        _batch.Draw(a, new Rectangle(0, 30, W, H), Color.White);
        _batch.Draw(b, new Rectangle(W + 8, 30, W, H), Color.White);
        _batch.Draw(c, new Rectangle(W * 2 + 16, 30, W, H), Color.White);
        var sz = _font.MeasureString(caption);
        _batch.DrawString(_font, caption, new Vector2(WW / 2f - sz.X / 2f, 6f), Color.White);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(Path.Combine(subdir, fileName), combo);
        a.Dispose(); b.Dispose(); c.Dispose();
    }
}
