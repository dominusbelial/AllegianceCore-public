using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>How a native-object material participates in the HD texture pack
/// (HD-5). <see cref="Off"/> ignores the pack entirely and draws the native
/// texture with native lighting. <see cref="Classic"/> (the HD-5 default)
/// samples the pack's <b>basecolor</b> (UNORM / sRGB-encoded, so the encoded
/// samples feed BasicEffect exactly like a native texture) through the SAME
/// native classic shading, lighting and blending the native path uses — only
/// the sampled texture is sharper. <see cref="Pbr"/> uses the estimated
/// roughness/metallic full PBR material (experimental; tuned separately in
/// HD-6).</summary>
public enum HdDrawMode
{
    Off = 0,
    Classic = 1,
    Pbr = 2,
}

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
    private VertexBuffer? _pbr, _legacyPbr, _legacyClassic;
    private VertexPositionNormalTexture[]? _legacyVertices;
    private readonly float _radius;

    public NativeMaterialParts(IReadOnlyList<MdlMesh> meshes, VertexPositionNormalTexture[] vertices, uint[] indices)
    {
        _vertices = vertices; _indices = indices;
        _radius = vertices.Length == 0 ? 1 : vertices.Max(v => v.Position.Length());
        int offset = 0;
        _parts = meshes.Select(m =>
        {
            var part = new NativeStationPart(m.TextureStem, offset, m.TriangleCount);
            offset += m.Indices.Length;
            return part;
        }).ToArray();
    }

    /// <summary>
    /// Draw the material's parts. <paramref name="mode"/> defaults to the shared
    /// <see cref="HdMaterialRenderer.Mode"/> (so every renderer follows the single
    /// global HD setting); pass an explicit value to override (e.g. the ship
    /// renderer's inspector HD compare). In <see cref="HdDrawMode.Pbr"/> the pack's
    /// full PBR material is tried first with native fallback; in
    /// <see cref="HdDrawMode.Classic"/> the pack's HD basecolor is sampled through
    /// the SAME native BasicEffect lighting/blending the native path uses (native
    /// fallback when the material is missing); <see cref="HdDrawMode.Off"/> never
    /// touches the pack.
    /// </summary>
    public void Draw(GraphicsDevice gd, HdMaterialRenderer hd, string? model, string? inheritedStem,
        Texture2D? inheritedTexture, Func<string, Texture2D?> textureFor, BasicEffect native,
        VertexBuffer vertices, IndexBuffer indices, Matrix world, Matrix view, Matrix projection,
        float opacity, SectorLighting? lighting, HdDrawMode? mode = null)
    {
        var drawMode = mode ?? hd.Mode;
        // Classic HD mirrors the PBR path's screen-footprint mip selection so an
        // up-close hull samples its full-resolution basecolor (native stays
        // smaller/resident-cheaper). Computed once per draw; all parts share it.
        float projectedPixels = 0f;
        if (drawMode == HdDrawMode.Classic)
        {
            var center = Vector3.Transform(Vector3.Zero, world * view);
            float scale = Math.Max(world.Right.Length(), Math.Max(world.Up.Length(), world.Backward.Length()));
            projectedPixels = 2 * _radius * scale * Math.Abs(projection.M22) * gd.Viewport.Height / Math.Max(.001f, 2 * Math.Abs(center.Z));
        }
        string? hdStem = null;
        foreach (var part in _parts)
        {
            string? stem = part.TextureStem ?? inheritedStem;
            hdStem = stem;
            if (drawMode == HdDrawMode.Pbr && model is not null && hd.TryDraw(model, stem, Pbr, indices,
                part.TriangleCount, world, view, projection, opacity, lighting, part.StartIndex, _radius)) continue;
            var texture = part.TextureStem is { } own &&
                !string.Equals(own, inheritedStem, StringComparison.OrdinalIgnoreCase)
                ? textureFor(own) : inheritedTexture;
            bool legacyClassic = false;
            if (drawMode == HdDrawMode.Classic)
            {
                var hdBase = hd.ClassicColor(stem, projectedPixels);
                if (hdBase is not null) { texture = hdBase; legacyClassic = stem is not null && hd.ReverseMeshV(stem); }
            }
            native.Texture = texture;
            native.TextureEnabled = texture is not null;
            native.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(legacyClassic ? LegacyClassic() : vertices); gd.Indices = indices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, part.StartIndex, part.TriangleCount);
        }

        VertexBuffer LegacyClassic()
        {
            if (_legacyClassic is not null) return _legacyClassic;
            _legacyVertices ??= ReverseTextureV(_vertices);
            _legacyClassic = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), _vertices.Length, BufferUsage.WriteOnly);
            _legacyClassic.SetData(_legacyVertices);
            return _legacyClassic;
        }

        VertexBuffer Pbr()
        {
            bool reverseV = hdStem is not null && hd.ReverseMeshV(hdStem);
            ref var buffer = ref (reverseV ? ref _legacyPbr : ref _pbr);
            if (buffer is not null) return buffer;
            // Rebuild tangent handedness in the map's UV convention as well:
            // reflecting V reverses the bitangent, including normal-map green.
            var data = PbrVertex.Build(reverseV ? _legacyVertices ??= ReverseTextureV(_vertices) : _vertices, _indices);
            buffer = new VertexBuffer(gd, typeof(PbrVertex), data.Length, BufferUsage.WriteOnly);
            buffer.SetData(data);
            return buffer;
        }
    }

    /// <summary>Adapt legacy HD map rows at the material boundary. Source mesh
    /// positions, normals, UVs, indices and native/depth buffers stay intact.</summary>
    public static VertexPositionNormalTexture[] ReverseTextureV(VertexPositionNormalTexture[] source)
    {
        var result = (VertexPositionNormalTexture[])source.Clone();
        for (int i = 0; i < result.Length; i++) result[i].TextureCoordinate.Y = 1f - result[i].TextureCoordinate.Y;
        return result;
    }

    public void Dispose()
    {
        _pbr?.Dispose(); _legacyPbr?.Dispose(); _legacyClassic?.Dispose();
    }
}
