using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static Program;

internal static class PixelComparison
{
    // Compare the actual Game1-owned renderers with the pre-fix construction on
    // the same GL device. No window, HUD clock or driver-specific hash in the oracle.
    internal static string Compare(Game1 game, bool healthy, EffectsQuality quality)
    {
        var gd = game.GraphicsDevice;
        var native = Field<NativeEffectTextures>(game, "_nativeEffects");
        var vfx = Field<VfxAssets>(game, "_vfxAssets");
        var films = Field<ExplosionAssets>(game, "_explosionAssets");
        using var smoke = healthy ? new EnhancedSmokeField(gd, vfx) : null;
        using var engine = new ShipEngineTrailRenderer(gd, native, smoke);
        using var missile = new MissileTrailRenderer(gd, native, smoke);
        using var damage = new DamageDebrisRenderer(gd, native, smoke);
        using var target = new RenderTarget2D(gd, 96, 96, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var eye = Vector3.Zero;
        var view = Matrix.Identity;
        var proj = Matrix.CreatePerspectiveFieldOfView(1, 1, 1, 2000);
        TrailParticleView[] exhaust = [new(new(-3, 0, -40), 18, TrailParticleKind.Smoke, .6f),
            new(new(4, 0, -44), 12, TrailParticleKind.Smoke, .4f)];
        DamageDebrisView[] vents = [new(new(0, 0, -44), default, default, 18, .7f)];
        ExplosionEntity[] blasts = [new() { Type = ExplosionType.SmallShip, ClusterId = 2, Position = new(0, 0, -200) }];
        var results = new List<string>();
        void ComparePass(string pass, Action actual, Action reference)
        {
            (long, Color[]) Capture(Action draw)
            {
                gd.SetRenderTarget(target);
                gd.Clear(Color.Black);
                var before = gd.Metrics.DrawCount;
                draw();
                long draws = gd.Metrics.DrawCount - before;
                gd.SetRenderTarget(null);
                var pixels = new Color[target.Width * target.Height];
                target.GetData(pixels);
                return (draws, pixels);
            }
            var (draws, pixels) = Capture(actual);
            var (expected, expectedPixels) = Capture(reference);
            Check(draws > 0 && pixels.Any(p => p.R + p.G + p.B > 0), pass + " drew no visible pixels");
            Check(draws == expected && pixels.SequenceEqual(expectedPixels), pass + " draw count or pixels differ");
            var hash = SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan()));
            results.Add($"{pass}={draws}/{Convert.ToHexString(hash)[..16]}");
        }
        ComparePass("engine", () => Field<ShipEngineTrailRenderer>(game, "_engineTrailRenderer").Draw(exhaust, eye, view, proj),
            () => engine.Draw(exhaust, eye, view, proj));
        ComparePass("missile", () => Field<MissileTrailRenderer>(game, "_trailRenderer").Draw(exhaust, eye, view, proj),
            () => missile.Draw(exhaust, eye, view, proj));
        ComparePass("damage", () => Field<DamageDebrisRenderer>(game, "_damageDebrisRenderer").Draw(vents, eye, view, proj),
            () => damage.Draw(vents, eye, view, proj));
        if (quality == EffectsQuality.Volumetric)
        {
            using var effect = new Effect(gd, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Content/Shaders/Volumetric.ogl.mgfxo")));
            using var volume = new VolumetricRenderer(effect);
            void Draw(VolumetricRenderer renderer) => renderer.Draw(blasts, 2, eye, view, proj, .5f, SoftParticleContext.Off, 500);
            ComparePass("blast", () => Draw(Field<VolumetricRenderer>(game, "_volumetricRenderer")), () => Draw(volume));
        }
        else if (smoke is not null)
        {
            using var blast = new EnhancedExplosionRenderer(gd, vfx, films, smoke);
            void Draw(EnhancedExplosionRenderer renderer) => renderer.Draw(blasts, 2, 500, eye, view, proj, null);
            ComparePass("blast", () => Draw(Field<EnhancedExplosionRenderer>(game, "_enhancedExplosionRenderer")), () => Draw(blast));
        }
        else
        {
            using var blast = new ExplosionRenderer(gd, films);
            void Draw(ExplosionRenderer renderer) => renderer.Draw(blasts, 2, 0, eye, view, proj);
            ComparePass("blast", () => Draw(Field<ExplosionRenderer>(game, "_explosionRenderer")), () => Draw(blast));
        }
        return string.Join(' ', results);
    }
}
