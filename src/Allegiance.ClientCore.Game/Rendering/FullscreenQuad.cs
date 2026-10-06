using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P547 — a tiny fullscreen (NDC) quad used by the post-processing passes to
/// run a pixel shader over the whole viewport without a SpriteBatch (whose
/// vertex space and per-vertex colour don't fit a custom NDC VS).
/// Triangle strip: (-1,-1),(1,-1),(-1,1),(1,1) with matching UVs.
/// </summary>
internal sealed class FullscreenQuad : IDisposable
{
    private readonly VertexBuffer _vb;
    private readonly int _vertexCount;

    public FullscreenQuad(GraphicsDevice gd)
    {
        var verts = new[]
        {
            new VertexPositionTexture(new Vector3(-1f, -1f, 0f), new Vector2(0f, 1f)),
            new VertexPositionTexture(new Vector3( 1f, -1f, 0f), new Vector2(1f, 1f)),
            new VertexPositionTexture(new Vector3(-1f,  1f, 0f), new Vector2(0f, 0f)),
            new VertexPositionTexture(new Vector3( 1f,  1f, 0f), new Vector2(1f, 0f)),
        };
        _vertexCount = verts.Length;
        _vb = new VertexBuffer(gd, typeof(VertexPositionTexture), _vertexCount, BufferUsage.WriteOnly);
        _vb.SetData(verts);
    }

    public void Draw(GraphicsDevice gd)
    {
        gd.SetVertexBuffer(_vb);
        gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, _vertexCount - 2);
    }

    public void Dispose() => _vb.Dispose();
}
