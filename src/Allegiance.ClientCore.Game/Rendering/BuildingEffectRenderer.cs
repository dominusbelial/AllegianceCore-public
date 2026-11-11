using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Two rotating, textured build shells, independent of station geometry.
/// Native effect/efgeo.cpp:99-227; Igc/buildingeffectigc.h:93,160-225.</summary>
public sealed class BuildingEffectRenderer : IDisposable
{
    // Native uses the HSB complement, preserving saturation and brightness.
    public static readonly Vector3 OuterColor = new(50f / 255, 28f / 255, 146f / 255);
    public static readonly Vector3 InnerColor = new(124f / 255, 146f / 255, 28f / 255);
    public const float ShellRatio = 1.1f;
    private readonly BasicEffect _effect;
    private readonly List<(VertexBuffer Vertices, IndexBuffer Indices, int Triangles, Texture2D? Texture)> _parts = [];
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);

    public BuildingEffectRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _effect = new BasicEffect(gd) { LightingEnabled = false };
        try
        {
            // Load the same dedicated geometry and texture namespace as native.
            // Keep its authored unit sphere, UVs and winding (no station scale/flip).
            var model = MdlFile.Parse(AssetSources.ReadAllBytes(assets, "build.mdl"));
            foreach (var mesh in model.Meshes)
            {
                Texture2D? texture = null;
                if (mesh.TextureStem is { Length: > 0 } stem && !_textures.TryGetValue(stem, out texture))
                    _textures[stem] = texture = NativeStationTexture.Load(gd, assets, stem);
                var data = new VertexPositionNormalTexture[mesh.VertexCount];
                for (int i = 0; i < data.Length; i++)
                    data[i] = new(new(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]),
                        new(mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2]),
                        new(mesh.TexCoords[i * 2], mesh.TexCoords[i * 2 + 1]));
                var vertices = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), data.Length, BufferUsage.WriteOnly);
                vertices.SetData(data);
                var indices = new IndexBuffer(gd, IndexElementSize.SixteenBits, mesh.Indices.Length, BufferUsage.WriteOnly);
                indices.SetData(mesh.Indices);
                _parts.Add((vertices, indices, mesh.TriangleCount, texture));
            }
            if (_parts.Count == 0) throw new InvalidDataException("build.mdl has no shell geometry");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            foreach (var part in _parts) { part.Vertices.Dispose(); part.Indices.Dispose(); }
            _parts.Clear();
            foreach (var texture in _textures.Values) texture.Dispose();
            _textures.Clear();
            // Missing private artwork must never bring back the station placeholder.
            Console.WriteLine($"[monogame] native construction effect unavailable ({ex.Message})");
        }
    }

    /// <summary>Native column-vector rotations expressed in MonoGame row order.
    /// Rotation uses window time, not time since the individual build started.
    /// effect/efgeo.cpp:209-226.</summary>
    public static Matrix InnerRotation(float time) => Matrix.CreateRotationY(.5f * MathF.PI * time)
        * Matrix.CreateRotationZ(time / MathF.PI);
    public static Matrix OuterRotation(float time) => Matrix.CreateRotationY(MathF.PI * time)
        * Matrix.CreateRotationX(time);

    public static (Vector3 InnerRgb, float InnerAlpha, Vector3 OuterRgb, float OuterAlpha) ColorsFor(ConstructionPhase phase) =>
        (InnerColor * phase.InnerIntensity, phase.InnerAlpha, OuterColor * phase.OuterIntensity, 1);

    public void Draw(ConstructionPhase phase, Vector3 cameraEye, Matrix view, Matrix projection, float time)
    {
        if (phase.Expired || _parts.Count == 0) return;
        var gd = _effect.GraphicsDevice;
        var blend = gd.BlendState;
        var depth = gd.DepthStencilState;
        var raster = gd.RasterizerState;
        var sampler = gd.SamplerStates[0];
        try
        {
            gd.DepthStencilState = DepthStencilState.DepthRead;
            gd.RasterizerState = RasterizerState.CullCounterClockwise;
            gd.SamplerStates[0] = SamplerState.LinearWrap;
            _effect.View = view; _effect.Projection = projection;
            var position = phase.Position;
            var translation = Matrix.CreateTranslation(new Vector3(position.X, position.Y, position.Z) - cameraEye);
            var colors = ColorsFor(phase);
            DrawShell(InnerRotation(time) * Matrix.CreateScale(phase.Radius / ShellRatio) * translation,
                colors.InnerRgb, colors.InnerAlpha, BlendState.NonPremultiplied);
            // Native Color * intensity scales RGB but keeps alpha unchanged
            // (zlib/color.h:101-110); outer fade is intensity, not alpha.
            DrawShell(OuterRotation(time) * Matrix.CreateScale(phase.Radius) * translation,
                colors.OuterRgb, colors.OuterAlpha, BlendState.Additive);
        }
        finally
        {
            gd.BlendState = blend; gd.DepthStencilState = depth;
            gd.RasterizerState = raster; gd.SamplerStates[0] = sampler;
        }
    }

    private void DrawShell(Matrix world, Vector3 color, float alpha, BlendState blend)
    {
        if (alpha <= 0) return;
        var gd = _effect.GraphicsDevice;
        gd.BlendState = blend;
        _effect.World = world;
        // BasicEffect premultiplies RGB by Alpha; native's blend uses straight
        // alpha (engine/RasterD3D9.cpp:295-312), so compensate before upload.
        _effect.DiffuseColor = color / alpha;
        _effect.Alpha = alpha;
        foreach (var part in _parts)
        {
            _effect.Texture = part.Texture; _effect.TextureEnabled = part.Texture is not null;
            _effect.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(part.Vertices); gd.Indices = part.Indices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.Triangles);
        }
    }

    public void Dispose()
    {
        foreach (var part in _parts) { part.Vertices.Dispose(); part.Indices.Dispose(); }
        foreach (var texture in _textures.Values) texture.Dispose();
        _effect.Dispose();
    }
}
