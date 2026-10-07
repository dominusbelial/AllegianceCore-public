using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaVec = Microsoft.Xna.Framework.Vector3;

/// <summary>
/// P545 — headless visual proof (the <c>roster-capture particles</c> mode).
/// Renders the same live particle state through (1) the pre-P545 procedural
/// <b>white soft-blob</b> look and (2) the new native-texture renderers, and
/// measures the <b>blown-out-pixel share</b> (all channels ≥ 250) in each
/// scene, old vs new — the numbers that show dense combat / missile launches /
/// asteroid hits no longer wash out to a white patch.
/// </summary>
internal sealed partial class RosterCapture
{
    private sealed class FixedRng : ITrailRandom
    {
        public float T = 0.5f;
        public float Range(float min, float max) => min + T * (max - min);
        public SimVec Direction() => new(0.7071f, 0.0f, 0.7071f);
    }

    private void RunParticles(GraphicsDevice gd)
    {
        Directory.CreateDirectory(Path.Combine(_o.OutDir, "p545"));
        using var native = NativeEffectTextures.Load(gd, new DirectoryAssetSource(_o.ArtRoot));

        using var mTrail = new MissileTrailRenderer(gd, native);
        using var eTrail = new ShipEngineTrailRenderer(gd, native);
        using var damage = new DamageDebrisRenderer(gd, native);
        using var hull = new HullHitSparkRenderer(gd, native, new DirectoryAssetSource(_o.ArtRoot));
        using var flares = new ShieldFlareRenderer(gd, native, new DirectoryAssetSource(_o.ArtRoot));

        using var oldFx = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true };
        using var oldSprite = MakeSoftSprite(gd, 64);
        oldFx.Texture = oldSprite;

        Console.WriteLine("[roster-capture] P545 particles proof:");
        RunMissileScenes(gd, mTrail, oldFx, oldSprite);
        RunDenseCombat(gd, eTrail, damage, hull, flares, oldFx, oldSprite);
        RunAsteroidHits(gd, hull, oldFx, oldSprite);

        Console.WriteLine("[roster-capture] particles PASS");
    }

    // ── (a) missile launch + volley, close to the camera ───────────────────
    private void RunMissileScenes(GraphicsDevice gd, MissileTrailRenderer mTrail,
        BasicEffect oldFx, Texture2D oldSprite)
    {
        var eye = new XnaVec(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(XnaVec.Zero, new XnaVec(0f, 0f, 40f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 16f / 9f, 0.5f, 400f);
        const int W = 930, H = 570;

        foreach (var t in new[] { 0.0f, 0.1f, 0.3f, 0.6f })
        {
            var pool = new MissileTrailParticles();
            StepMissile(pool, t, new XnaVec(0f, 0f, 8f), new XnaVec(0f, 0f, 40f));
            var views = pool.Enumerate().ToList();

            var (oldRt, oldShare) = Shot(gd, W, H, view, proj, eye,
                r => OldMissileTrail(oldFx, oldSprite, r, views, eye, view, proj));
            var (newRt, newShare) = Shot(gd, W, H, view, proj, eye,
                r => mTrail.Draw(views, eye, view, proj));
            Console.WriteLine($"  missile t={t:F1}s  blown-out old={oldShare:P2}  new={newShare:P2}");
            SideBySide(gd, oldRt, newRt, W, H, $"missile t={t:F1}s  old white {oldShare:P2}  vs  new native {newShare:P2}");
        }

        // Volley: 4 missiles from the own ship (chase view) spreading away.
        foreach (var t in new[] { 0.2f, 0.5f, 1.0f })
        {
            var pool = new MissileTrailParticles();
            var origins = new[]
            {
                new XnaVec(0f, 0f, 10f), new XnaVec(-1.5f, 1.2f, 10f),
                new XnaVec(1.5f, 1.2f, 10f), new XnaVec(0f, -1.8f, 10f),
            };
            var dirs = new[]
            {
                new XnaVec(0f, 0f, 42f), new XnaVec(-2f, 1.6f, 42f),
                new XnaVec(2f, 1.6f, 42f), new XnaVec(0f, -2.2f, 42f),
            };
            for (int i = 0; i < 4; i++)
                StepMissile(pool, t, origins[i], dirs[i]);
            var views = pool.Enumerate().ToList();

            var (oldRt, oldShare) = Shot(gd, W, H, view, proj, eye,
                r => OldMissileTrail(oldFx, oldSprite, r, views, eye, view, proj));
            var (newRt, newShare) = Shot(gd, W, H, view, proj, eye,
                r => mTrail.Draw(views, eye, view, proj));
            Console.WriteLine($"  VOLLEY t={t:F1}s  blown-out old={oldShare:P2}  new={newShare:P2}");
            SideBySide(gd, oldRt, newRt, W, H, $"VOLLEY t={t:F1}s  old white {oldShare:P2}  vs  new native {newShare:P2}");
        }
    }

    private static void StepMissile(MissileTrailParticles pool, float sec, XnaVec start, XnaVec vel)
    {
        const float dt = 1f / 60f;
        double now = 0;
        for (float el = 0f; el <= sec; el += dt)
        {
            var pos = start + vel * el;
            var missiles = new[] { new TrailMissile(1, new SimVec(pos.X, pos.Y, pos.Z), new SimVec(0, 0, 1), now) };
            pool.Update(now, dt, missiles, new FixedRng());
            now += dt;
        }
    }

    // ── (b) dense combat: ~10 ships, shield/hull hits, missiles, low-hull smoke ─
    private void RunDenseCombat(GraphicsDevice gd, ShipEngineTrailRenderer eTrail, DamageDebrisRenderer damage,
        HullHitSparkRenderer hull, ShieldFlareRenderer flares, BasicEffect oldFx, Texture2D oldSprite)
    {
        var eye = new XnaVec(0f, 2f, 26f);
        var view = Matrix.CreateLookAt(XnaVec.Zero, new XnaVec(0f, 0f, 0f) - eye, XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 16f / 9f, 0.5f, 300f);
        const int W = 950, H = 550;
        const float dt = 1f / 60f;

        var rng = new FixedRng();
        var engines = new ShipEngineTrailParticles();
        var dbris = new DamageDebrisParticles();
        var hits = new HullHitSparkParticles();
        var sh = new ShieldFlareParticles();
        var missiles = new MissileTrailParticles();

        for (int s = 0; s < 10; s++)
        {
            float ang = s * MathHelper.TwoPi / 10f;
            var shipPos = new SimVec(MathF.Cos(ang) * 6f, MathF.Sin(s) * 2f, MathF.Sin(ang) * 6f);
            engines.Emit(new EngineTrailShip((short)s, shipPos, new SimVec(0, 0, 0), new Orientation(), 10f, 1f), dt, rng);
            if (s < 3)
                dbris.Emit(shipPos, new SimVec(0, 0, 0), new Orientation(), 10f, shipIds(s), 3, dt, rng);
        }
        StepMissile(missiles, 0.5f, new XnaVec(-8f, 1f, -4f), new XnaVec(3f, 0f, 2f));
        StepMissile(missiles, 0.7f, new XnaVec(8f, -1f, -5f), new XnaVec(-3f, 0f, 3f));
        for (int i = 0; i < 14; i++)
            hits.Emit(new SimVec((i % 7 - 3) * 2f, (i / 7) * 2f, (i % 5) * 2f), new SimVec(1, 0, 0), 12f, new FixedRng());
        for (int i = 0; i < 10; i++)
        {
            var c = new SimVec(MathF.Cos(i) * 6f, 0f, MathF.Sin(i) * 6f);
            var d = new SimVec(MathF.Cos(i), MathF.Sin(i * 2f), MathF.Sin(i));
            sh.Emit((short)i, c, new Orientation(), c + d.Normalize() * 10f, new SimVec(10f, 10f, 10f), 10f, 10f);
        }
        // Age a few frames so everything is mid-life.
        missiles.Update(0.032f, 0.016f, Enumerable.Empty<TrailMissile>(), new FixedRng());
        dbris.Update(0.035f);
        hits.Update(0.048f);
        sh.Update(0.021f);

        var missViews = missiles.Enumerate().ToList();
        var engViews = engines.Enumerate().ToList();
        var damViews = dbris.Enumerate().ToList();
        var hitViews = hits.Enumerate().ToList();
        var flaViews = sh.Enumerate().ToList();
        var flashViews = hits.EnumerateFlashes().ToList();

        var (oldRt, oldShare) = Shot(gd, W, H, view, proj, eye, r =>
        {
            OldEngineTrail(oldFx, oldSprite, r, engViews, eye, view, proj);
            OldDamage(oldFx, oldSprite, r, damViews, eye, view, proj);
            OldBits(oldFx, oldSprite, r, hitViews, eye, view, proj);
            OldShieldFlare(oldFx, oldSprite, r, flaViews, eye, view, proj);
        });
        var (newRt, newShare) = Shot(gd, W, H, view, proj, eye, r =>
        {
            eTrail.Draw(engViews, eye, view, proj);
            damage.Draw(damViews, eye, view, proj);
            hull.Draw(hitViews, flashViews, eye, view, proj);
            flares.Draw(flaViews, eye, view, proj);
        });
        Console.WriteLine($"  dense-combat  blown-out old={oldShare:P2}  new={newShare:P2}  (10 ships, {engViews.Count} trail, {damViews.Count} smoke, {hitViews.Count}+{flashViews.Count} hull, {flaViews.Count} flare)");
        SideBySide(gd, oldRt, newRt, W, H, $"dense combat 10v10  old white {oldShare:P2}  vs  new native {newShare:P2}");
    }

    private static short shipIds(int s) => (short)(100 + s);

    // ── (c) asteroid hits: a gun stream pounding a large rock, close up ────
    private void RunAsteroidHits(GraphicsDevice gd, HullHitSparkRenderer hull, BasicEffect oldFx, Texture2D oldSprite)
    {
        var eye = new XnaVec(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(XnaVec.Zero, new XnaVec(0f, 0f, 20f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 16f / 9f, 0.5f, 200f);
        const int W = 870, H = 555;
        const float dt = 1f / 60f;

        var pool = new HullHitSparkParticles();
        for (int i = 0; i < 22; i++)
        {
            var p = new SimVec((i % 6 - 3) * 3f, 0f, 16f + (i / 6) * 0.4f);
            pool.Emit(p, new SimVec(0f, 0f, 1f), 60f, new FixedRng());
            pool.Update(dt * (i % 3));
        }
        var hitViews = pool.Enumerate().ToList();
        var flashViews = pool.EnumerateFlashes().ToList();

        var (oldRt, oldShare) = Shot(gd, W, H, view, proj, eye, r => OldBits(oldFx, oldSprite, r, hitViews, eye, view, proj));
        var (newRt, newShare) = Shot(gd, W, H, view, proj, eye, r => hull.Draw(hitViews, flashViews, eye, view, proj));
        Console.WriteLine($"  asteroid-hits (large rock r=60)  blown-out old={oldShare:P2}  new={newShare:P2}");
        SideBySide(gd, oldRt, newRt, W, H, $"ASTEROID HITS  old white {oldShare:P2}  vs  new native {newShare:P2}");
    }

    // ── OLD (pre-P545) white-blob reproductions ────────────────────────────
    private static void OldMissileTrail(BasicEffect fx, Texture2D spr, RenderTarget2D rt,
        List<TrailParticleView> v, XnaVec eye, Matrix view, Matrix proj)
    {
        var list = new List<(XnaVec c, float half, Color col)>();
        foreach (var p in v)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            float a = rem * rem;
            float growth = p.Kind == TrailParticleKind.Smoke ? 1f + 1.2f * (1f - rem) : 1f;
            float half = p.Size * 90f * 0.5f * growth;
            var baseC = p.Kind == TrailParticleKind.Fire ? new XnaVec(1f, 0.85f, 0.45f) : new XnaVec(0.62f, 0.62f, 0.66f);
            list.Add((new XnaVec(p.Position.X, p.Position.Y, p.Position.Z) - eye, half, new Color(baseC.X * a, baseC.Y * a, baseC.Z * a, a)));
        }
        OldDraw(fx, spr, rt, list, view, proj);
    }

    private static void OldEngineTrail(BasicEffect fx, Texture2D spr, RenderTarget2D rt,
        List<TrailParticleView> v, XnaVec eye, Matrix view, Matrix proj)
    {
        var list = new List<(XnaVec c, float half, Color col)>();
        foreach (var p in v)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            float a = rem * rem;
            float growth = p.Kind == TrailParticleKind.Smoke ? 1f + 1.0f * (1f - rem) : 1f;
            float half = p.Size * 0.5f * growth;
            var baseC = p.Kind == TrailParticleKind.Fire ? new XnaVec(1f, 0.62f, 0.25f) : new XnaVec(0.62f, 0.62f, 0.66f);
            list.Add((new XnaVec(p.Position.X, p.Position.Y, p.Position.Z) - eye, half, new Color(baseC.X * a, baseC.Y * a, baseC.Z * a, a)));
        }
        OldDraw(fx, spr, rt, list, view, proj);
    }

    private static void OldDamage(BasicEffect fx, Texture2D spr, RenderTarget2D rt,
        List<DamageDebrisView> v, XnaVec eye, Matrix view, Matrix proj)
    {
        var list = new List<(XnaVec c, float half, Color col)>();
        foreach (var p in v)
        {
            float rem = MathHelper.Clamp(p.RemainingFrac, 0f, 1f);
            float half = p.Size * 0.5f * (0.75f + 2.25f * (1f - rem));
            float c = 0.25f * rem;
            list.Add((new XnaVec(p.Position.X, p.Position.Y, p.Position.Z) - eye, half, new Color(c, c * 0.8f, c, c)));
        }
        OldDraw(fx, spr, rt, list, view, proj);
    }

    private static void OldBits(BasicEffect fx, Texture2D spr, RenderTarget2D rt,
        List<HullHitSparkView> v, XnaVec eye, Matrix view, Matrix proj)
    {
        var list = new List<(XnaVec c, float half, Color col)>();
        foreach (var b in v)
        {
            float rem = MathHelper.Clamp(b.RemainingFrac, 0f, 1f);
            list.Add((new XnaVec(b.Position.X, b.Position.Y, b.Position.Z) - eye, b.Size * 0.5f,
                new Color(rem, rem * 0.92f, rem * 0.65f, rem)));
        }
        OldDraw(fx, spr, rt, list, view, proj);
    }

    private static void OldShieldFlare(BasicEffect fx, Texture2D spr, RenderTarget2D rt,
        List<ShieldFlareView> v, XnaVec eye, Matrix view, Matrix proj)
    {
        var list = new List<(XnaVec c, float half, Color col)>();
        foreach (var f in v)
        {
            float rem = MathHelper.Clamp(f.RemainingFrac, 0f, 1f);
            list.Add((new XnaVec(f.Center.X, f.Center.Y, f.Center.Z) - eye, f.MeshRadius,
                new Color(0.62f * rem, 0.86f * rem, rem, rem)));
        }
        OldDraw(fx, spr, rt, list, view, proj);
    }

    private static void OldDraw(BasicEffect fx, Texture2D spr, RenderTarget2D rt,
        List<(XnaVec c, float half, Color col)> items, Matrix view, Matrix proj)
    {
        var gd = rt.GraphicsDevice;
        if (items.Count == 0) return;
        fx.Texture = spr;
        fx.World = Matrix.Identity;
        fx.View = view;
        fx.Projection = proj;
        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);
        var verts = new VertexPositionColorTexture[items.Count * 4];
        var indices = new short[items.Count * 6];
        int vi = 0, ib = 0;
        foreach (var (c, half, col) in items)
        {
            var tl = c - camRight * half + camUp * half;
            var tr = c + camRight * half + camUp * half;
            var br = c + camRight * half - camUp * half;
            var bl = c - camRight * half - camUp * half;
            verts[vi] = new VertexPositionColorTexture(tl, col, new Vector2(0, 0));
            verts[vi + 1] = new VertexPositionColorTexture(tr, col, new Vector2(1, 0));
            verts[vi + 2] = new VertexPositionColorTexture(br, col, new Vector2(1, 1));
            verts[vi + 3] = new VertexPositionColorTexture(bl, col, new Vector2(0, 1));
            indices[ib] = (short)vi; indices[ib + 1] = (short)(vi + 1); indices[ib + 2] = (short)(vi + 2);
            indices[ib + 3] = (short)vi; indices[ib + 4] = (short)(vi + 2); indices[ib + 5] = (short)(vi + 3);
            vi += 4; ib += 6;
        }
        gd.BlendState = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha, AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One, AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add, AlphaBlendFunction = BlendFunction.Add,
        };
        gd.RasterizerState = RasterizerState.CullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        fx.CurrentTechnique.Passes[0].Apply();
        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, items.Count * 2);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    // ── shared helpers ────────────────────────────────────────────────────
    private static Texture2D MakeSoftSprite(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float inv = 2f / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * inv - 1f, ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                data[y * size + x] = new Color(1f, 1f, 1f, a * a);
            }
        tex.SetData(data);
        return tex;
    }

    private (RenderTarget2D Rt, double Share) Shot(GraphicsDevice gd, int W, int H,
        Matrix view, Matrix proj, XnaVec eye, Action<RenderTarget2D> draw)
    {
        var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.006f, 0.009f, 0.018f));
        gd.DepthStencilState = DepthStencilState.Default;
        draw(rt);
        gd.SetRenderTarget(null);
        var share = BlownOutShare(gd, rt);
        return (rt, share);
    }

    private static double BlownOutShare(GraphicsDevice gd, RenderTarget2D rt)
    {
        var data = new Color[rt.Width * rt.Height];
        rt.GetData(data);
        int blown = 0;
        foreach (var c in data)
            if (c.R >= 250 && c.G >= 250 && c.B >= 250) blown++;
        return (double)blown / data.Length;
    }

    private void SideBySide(GraphicsDevice gd, RenderTarget2D oldRt, RenderTarget2D newRt,
        int W, int H, string caption)
    {
        int WW = W * 2 + 8;
        using var combo = new RenderTarget2D(gd, WW, H + 30, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(combo);
        gd.Clear(Color.Black);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
        _batch.Draw(oldRt, new Rectangle(0, 30, W, H), Color.White);
        _batch.Draw(newRt, new Rectangle(W + 8, 30, W, H), Color.White);
        var sz = _font.MeasureString(caption);
        _batch.DrawString(_font, caption, new Vector2(WW / 2f - sz.X / 2f, 4f), Color.White);
        _batch.End();
        gd.SetRenderTarget(null);
        string safe = string.Concat(caption.Split(Path.GetInvalidFileNameChars()));
        SaveJpeg(Path.Combine("p545", safe + ".jpg"), combo);
        oldRt.Dispose();
        newRt.Dispose();
    }
}
