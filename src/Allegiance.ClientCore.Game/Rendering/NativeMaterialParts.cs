using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>TextureGeo scopes survive buffer merging. Tangents allocate only
/// on the first successful HD draw; native and depth buffers stay unchanged.</summary>
public sealed class NativeMaterialParts : IDisposable
{
    public static Func<string, Stream> NativeReader(IAssetSource assets)
    {
        Dictionary<string, string>? names = null;
        return path =>
        {
            if (assets.Exists(path)) return assets.OpenRead(path);
            names ??= assets.Enumerate(".").ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
            return assets.OpenRead(names.GetValueOrDefault(path, path));
        };
    }
    private readonly VertexPositionNormalTexture[] _vertices;
    private readonly uint[] _indices;
    private readonly NativeStationPart[] _parts;
    private VertexBuffer? _pbr;

    public NativeMaterialParts(IReadOnlyList<MdlMesh> meshes, VertexPositionNormalTexture[] vertices, uint[] indices)
    {
        _vertices = vertices; _indices = indices;
        int offset = 0;
        _parts = meshes.Select(m =>
        {
            var part = new NativeStationPart(m.TextureStem, offset, m.TriangleCount);
            offset += m.Indices.Length;
            return part;
        }).ToArray();
    }

    public void Draw(GraphicsDevice gd, HdMaterialRenderer hd, string? model, string? inheritedStem,
        Texture2D? inheritedTexture, Func<string, Texture2D?> textureFor, BasicEffect native,
        VertexBuffer vertices, IndexBuffer indices, Matrix world, Matrix view, Matrix projection,
        float opacity, SectorLighting? lighting, bool enabled = true)
    {
        foreach (var part in _parts)
        {
            string? stem = part.TextureStem ?? inheritedStem;
            if (enabled && model is not null && hd.TryDraw(model, stem, Pbr, indices,
                part.TriangleCount, world, view, projection, opacity, lighting, part.StartIndex)) continue;
            var texture = part.TextureStem is { } own &&
                !string.Equals(own, inheritedStem, StringComparison.OrdinalIgnoreCase)
                ? textureFor(own) : inheritedTexture;
            native.Texture = texture;
            native.TextureEnabled = texture is not null;
            native.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(vertices); gd.Indices = indices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, part.StartIndex, part.TriangleCount);
        }

        VertexBuffer Pbr()
        {
            if (_pbr is not null) return _pbr;
            var data = PbrVertex.Build(_vertices, _indices);
            _pbr = new VertexBuffer(gd, typeof(PbrVertex), data.Length, BufferUsage.WriteOnly);
            _pbr.SetData(data);
            return _pbr;
        }
    }

    public void Dispose() => _pbr?.Dispose();
}
