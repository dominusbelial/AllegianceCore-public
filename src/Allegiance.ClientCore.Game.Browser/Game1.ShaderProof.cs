using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    public static readonly string[] ShaderProofNames = ["Bloom", "DisplacedSphere", "FsrEasu", "FsrRcas",
        "LinearDepth", "NativePbr", "SoftParticle", "Tonemap", "Volumetric"];

    /// <summary>Dev-only real-content qualification. A draw and readback force
    /// KNI's lazy WebGL shader compilation/linking for every technique/pass.</summary>
    public object ProbeShaders()
    {
        var gd = GraphicsDevice;
        var targets = gd.GetRenderTargets(); var viewport = gd.Viewport;
        var blend = gd.BlendState; var depth = gd.DepthStencilState; var raster = gd.RasterizerState;
        var rows = new List<object>();
        var textures = Enumerable.Range(0, 5).Select(i => gd.Textures[i]).ToArray();
        var samplers = Enumerable.Range(0, 5).Select(i => gd.SamplerStates[i]).ToArray();
        using var white = new Texture2D(gd, 1, 1); white.SetData(new[] { Color.White });
        try
        {
            foreach (string name in ShaderProofNames)
            {
                var effect = Content.Load<Effect>("Shaders/" + name);
                foreach (var parameter in effect.Parameters) Initialize(parameter);
                var passes = new List<object>();
                foreach (var technique in effect.Techniques)
                {
                    effect.CurrentTechnique = technique;
                    foreach (var pass in technique.Passes)
                    {
                        // New targets avoid KNI's CPU readback cache across passes.
                        using var target = new RenderTarget2D(gd, 16, 16, false, SurfaceFormat.Color, DepthFormat.None);
                        gd.SetRenderTarget(target); gd.Clear(Color.CornflowerBlue);
                        gd.BlendState = BlendState.Opaque; gd.DepthStencilState = DepthStencilState.None;
                        gd.RasterizerState = RasterizerState.CullNone;
                        pass.Apply();
                        gd.DrawUserPrimitives(PrimitiveType.TriangleList, new[] {
                            new ShaderProofVertex(new(-1,-1,.5f), new(0,1,.5f)),
                            new ShaderProofVertex(new(-1, 3,.5f), new(0,-1,.5f)),
                            new ShaderProofVertex(new( 3,-1,.5f), new(2,1,.5f)),
                        }, 0, 1);
                        var pixels = new Color[256]; target.GetData(pixels);
                        passes.Add(new { technique = technique.Name, pass = pass.Name, drawn = true,
                            changedPixels = pixels.Count(p => p != Color.CornflowerBlue) });
                        gd.SetRenderTargets(targets);
                    }
                }
                rows.Add(new { name, techniques = effect.Techniques.Count, passes });
            }
            return new { profile = gd.GraphicsProfile.ToString(), rows, softParticle = ProbeSoftParticle() };
        }
        finally
        {
            gd.SetRenderTargets(targets); gd.Viewport = viewport;
            gd.BlendState = blend; gd.DepthStencilState = depth; gd.RasterizerState = raster;
            // The proof owns all bindings it creates, never leave disposed textures bound.
            for (int i = 0; i < textures.Length; i++) { gd.Textures[i] = textures[i]; gd.SamplerStates[i] = samplers[i]; }
        }

        void Initialize(EffectParameter p)
        {
            if (p.Elements.Count > 0) { foreach (var e in p.Elements) Initialize(e); return; }
            if (p.ParameterClass == EffectParameterClass.Matrix) { p.SetValue(Matrix.Identity); return; }
            if (p.ParameterClass == EffectParameterClass.Object) { p.SetValue(white); return; }
            if (p.ParameterType == EffectParameterType.Int32) { p.SetValue(0); return; }
            if (p.ParameterType == EffectParameterType.Bool) { p.SetValue(false); return; }
            switch (p.ColumnCount)
            {
                case 1: p.SetValue(1f); break;
                case 2: p.SetValue(new Vector2(1f / 16)); break;
                case 3: p.SetValue(Vector3.One); break;
                case 4: p.SetValue(Vector4.One); break;
            }
        }
    }

    // Cover the GLES-only coordinate change as well as the renderer's existing
    // parameter contract. The tilted quad has varying clip w; an asymmetric
    // top-left depth mask detects y flips and incorrect perspective division.
    private object ProbeSoftParticle()
    {
        var gd = GraphicsDevice; var effect = Content.Load<Effect>("Shaders/SoftParticle");
        using var renderer = new Rendering.SoftParticleRenderer(effect.Clone());
        if (!renderer.IsUsable) throw new InvalidDataException("SoftParticle renderer contract missing");
        using var depth = new Texture2D(gd, 2, 2);
        depth.SetData(new[] { Color.White, Color.Black, Color.Black, Color.Black });
        using var white = new Texture2D(gd, 1, 1); white.SetData(new[] { Color.White });
        using var target = new RenderTarget2D(gd, 32, 32, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(target); gd.Clear(Color.Transparent);
        gd.BlendState = BlendState.Opaque; gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        effect.Parameters["WorldView"].SetValue(Matrix.Identity);
        effect.Parameters["WorldViewProj"].SetValue(Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver2, 1, .1f, 10));
        effect.Parameters["InvViewportSize"].SetValue(new Vector2(1f / 32));
        effect.Parameters["Near"].SetValue(0f); effect.Parameters["InvRange"].SetValue(.1f);
        effect.Parameters["SoftnessInv"].SetValue(100f);
        effect.Parameters["Texture"].SetValue(white); effect.Parameters["DepthTexture"].SetValue(depth);
        effect.CurrentTechnique = effect.Techniques["SoftParticle"]; effect.CurrentTechnique.Passes[0].Apply();
        // Left edge z=-1, right edge z=-2; xy projects to a fullscreen quad.
        gd.DrawUserPrimitives(PrimitiveType.TriangleList, new[] {
            new VertexPositionColorTexture(new(-1, 1,-1), Color.White, Vector2.Zero),
            new VertexPositionColorTexture(new( 2, 2,-2), Color.White, Vector2.Zero),
            new VertexPositionColorTexture(new(-1,-1,-1), Color.White, Vector2.Zero),
            new VertexPositionColorTexture(new(-1,-1,-1), Color.White, Vector2.Zero),
            new VertexPositionColorTexture(new( 2, 2,-2), Color.White, Vector2.Zero),
            new VertexPositionColorTexture(new( 2,-2,-2), Color.White, Vector2.Zero),
        }, 0, 2);
        var pixels = new Color[32 * 32]; target.GetData(pixels);
        var samples = new[] { pixels[8 * 32 + 8].A, pixels[8 * 32 + 24].A,
            pixels[24 * 32 + 8].A, pixels[24 * 32 + 24].A };
        if (samples[0] < 250 || samples.Skip(1).Any(a => a > 5))
            throw new InvalidDataException("SoftParticle depth coordinates incorrect: " + string.Join(",", samples));
        gd.SetRenderTarget(null);
        return new { rendererUsable = true, perspectiveDepthMask = samples };
    }

    private struct ShaderProofVertex : IVertexType
    {
        public Vector3 Position, Normal;
        public Color Color;
        public Vector4 Tangent;
        public Vector3 Tex0;
        public float Tex1;
        public Vector3 Tex2;
        public float Tex3, Tex4, Tex5;
        public ShaderProofVertex(Vector3 position, Vector3 uv)
        {
            Position = position; Normal = Vector3.UnitZ; Color = Color.White;
            Tangent = new(1, 0, 0, 1); Tex0 = uv; Tex1 = 1; Tex2 = Vector3.One;
            Tex3 = .4f; Tex4 = 1; Tex5 = .1f;
        }
        private static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0),
            new VertexElement(28, VertexElementFormat.Vector4, VertexElementUsage.Tangent, 0),
            new VertexElement(44, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(56, VertexElementFormat.Single, VertexElementUsage.TextureCoordinate, 1),
            new VertexElement(60, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(72, VertexElementFormat.Single, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(76, VertexElementFormat.Single, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(80, VertexElementFormat.Single, VertexElementUsage.TextureCoordinate, 5));
        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }
}
