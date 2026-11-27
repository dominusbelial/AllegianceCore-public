using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace HudProof;

internal static class ParticlePixelProof
{
    internal static void Run(HudStyle style)
    {
        using var game = new Game();
        _ = new GraphicsDeviceManager(game) { SynchronizeWithVerticalRetrace = false };
        game.Content.RootDirectory = "Content";
        game.RunOneFrame();
        var gd = game.GraphicsDevice;
        using var white = Texture(gd, Color.White);
        using var batch = new SpriteBatch(gd);
        var font = game.Content.Load<SpriteFont>("HUD");
        using var modern = new ModernHudRenderer(batch, white, font, font, new HudSettings { Style = style });
        HudRenderer hud = style == HudStyle.Classic ? new HudRenderer(batch, white, font, font) : modern;
        using var vfx = new VfxAssets(gd);
        var frames = vfx.Smoke;
        if (frames.Length != VfxAssets.SmokeGridFrames) throw new InvalidDataException("Incomplete smoke atlas");
        using var target = new RenderTarget2D(gd, 320, 180, false, SurfaceFormat.Color, DepthFormat.Depth24);
        using var basic = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true };
        var projection = Matrix.CreateOrthographic(8, 4.5f, .1f, 20);
        basic.Projection = projection;
        byte[] shader = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Content/Shaders/SoftParticle.ogl.mgfxo"));
        using var soft = new SoftParticleRenderer(new Effect(gd, shader));
        using var smoke = new EnhancedSmokeField(gd, vfx, soft);
        if (!soft.IsUsable) throw new InvalidDataException("Incomplete SoftParticle shader");
        var random = new Random(561);
        foreach (bool soften in new[] { false, true })
        {
            // A white depth texture puts this smoke wholly in front of the
            // scene. At full fade the soft shader and textured BasicEffect
            // both multiply the texel by the premultiplied vertex color.
            SoftParticleContext? context = soften ? new(white, .1f, 20, .01f) : null;
            foreach (int count in new[] { 21, 1, 0 })
            {
                var puffs = ParticleOracle.Puffs(random, count);
                Color[] reference = Render(() =>
                {
                    foreach (var (texture, group) in ParticleOracle.Groups(puffs, frames))
                    {
                        var (v, ix) = ParticleOracle.Geometry(group, Matrix.Identity);
                        basic.Texture = texture;
                        basic.CurrentTechnique.Passes[0].Apply();
                        gd.BlendState = BlendState.AlphaBlend;
                        gd.RasterizerState = RasterizerState.CullNone;
                        gd.DepthStencilState = DepthStencilState.DepthRead;
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, v, 0, v.Length, ix, 0, ix.Length / 3);
                    }
                });
                Color[] pixels = Render(() => smoke.Draw(puffs, Vector3.Zero, Matrix.Identity, projection,
                    null, default, default, default, context));
                if (!reference.AsSpan().SequenceEqual(pixels))
                    throw new InvalidDataException($"{style} soft={soften} count={count}: pixel mismatch");
                if (count == 21 && Render(() => { }).AsSpan().SequenceEqual(pixels))
                    throw new InvalidDataException("The burst produced no visible smoke");
            }
        }

        Console.WriteLine($"Particle pixels match {style}");

        Color[] Render(Action draw)
        {
            gd.SetRenderTarget(target);
            gd.SamplerStates[0] = SamplerState.LinearClamp;
            gd.Clear(new Color(21, 35, 49));
            draw();
            hud.Draw(null, CameraMode.Cockpit, gd.Viewport);
            gd.SetRenderTarget(null);
            var pixels = new Color[target.Width * target.Height];
            target.GetData(pixels);
            return pixels;
        }
    }

    private static Texture2D Texture(GraphicsDevice gd, Color color)
    {
        var tex = new Texture2D(gd, 1, 1);
        tex.SetData(new[] { color });
        return tex;
    }
}
