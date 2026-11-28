using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P589 — draw one camera-facing textured billboard ("poster") on the sky shell
/// at a unit direction, sized by a world half-extent, tinted by a diffuse
/// colour. This is the exact draw path the native planet uses
/// (<c>PosterImage::Render</c>, <c>efimage.cpp:527-563</c>: a billboarded
/// square at a direction, camera-facing, <c>BlendModeSourceAlpha</c>), shared
/// by the moons and the distant-object cut-outs so all three "paintings" look
/// identical in style (Domingo: "added just like the planets").
///
/// The tier pins depth and rasterizer state for the entire sky pass.
/// </summary>
internal sealed class SkyBillboard(GraphicsDevice device) : IDisposable
{
    internal static readonly short[] Indices = [0, 1, 2, 2, 1, 3];
    private readonly VertexPositionColorTexture[] _corners = new VertexPositionColorTexture[4];
    private readonly SkyEffect _effect = new(device);

    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye, Vector3 dir, float dist,
        float halfWorld, Texture2D texture, Color? diffuse = null)
    {
        var center = NativeSkyPlacement.PlanetShellCenter(dir, dist, cameraEye);
        var right = NativeSkyPlacement.BillboardRight(dir);
        var up = NativeSkyPlacement.BillboardUp(dir, right);
        _corners[0] = new VertexPositionColorTexture(center - right * halfWorld - up * halfWorld, Color.White, new Vector2(0, 1));
        _corners[1] = new VertexPositionColorTexture(center + right * halfWorld - up * halfWorld, Color.White, new Vector2(1, 1));
        _corners[2] = new VertexPositionColorTexture(center - right * halfWorld + up * halfWorld, Color.White, new Vector2(0, 0));
        _corners[3] = new VertexPositionColorTexture(center + right * halfWorld + up * halfWorld, Color.White, new Vector2(1, 0));

        device.BlendState = BlendState.AlphaBlend;
        var effect = _effect.Prepare(texture, diffuse?.ToVector3() ?? Vector3.One, view, projection, Matrix.Identity);
        for (int i = 0; i < effect.CurrentTechnique.Passes.Count; i++)
        {
            effect.CurrentTechnique.Passes[i].Apply();
            device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _corners, 0, 4, Indices, 0, 2);
        }
    }

    public void Dispose() => _effect.Dispose();
}
