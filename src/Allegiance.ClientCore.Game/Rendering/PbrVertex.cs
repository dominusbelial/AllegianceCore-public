using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Tangent-space vertex shared by native object renderers. W is the
/// bitangent sign, including mirrored UV islands; no geometry/UV changes.</summary>
public struct PbrVertex : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Vector4 Tangent;
    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.Tangent, 0));
    readonly VertexDeclaration IVertexType.VertexDeclaration => Declaration;

    public static PbrVertex[] Build(VertexPositionNormalTexture[] source, uint[] indices)
    {
        var tangents = new Vector3[source.Length];
        var bitangents = new Vector3[source.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
            var e1 = source[b].Position - source[a].Position;
            var e2 = source[c].Position - source[a].Position;
            var uv1 = source[b].TextureCoordinate - source[a].TextureCoordinate;
            var uv2 = source[c].TextureCoordinate - source[a].TextureCoordinate;
            float det = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (MathF.Abs(det) < 1e-10f) continue;
            var t = (e1 * uv2.Y - e2 * uv1.Y) / det;
            var bt = (e2 * uv1.X - e1 * uv2.X) / det;
            foreach (int v in new[] { a, b, c }) { tangents[v] += t; bitangents[v] += bt; }
        }
        var result = new PbrVertex[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var n = SafeNormal(source[i].Normal, Vector3.Up);
            var t = tangents[i] - n * Vector3.Dot(n, tangents[i]);
            t = SafeNormal(t, Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < .9f ? Vector3.Up : Vector3.Right, n)));
            float sign = Vector3.Dot(Vector3.Cross(n, t), bitangents[i]) < 0 ? -1 : 1;
            result[i] = new() { Position = source[i].Position, Normal = n, TexCoord = source[i].TextureCoordinate, Tangent = new(t, sign) };
        }
        return result;
    }
    private static Vector3 SafeNormal(Vector3 v, Vector3 fallback)
        => float.IsFinite(v.LengthSquared()) && v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
}
