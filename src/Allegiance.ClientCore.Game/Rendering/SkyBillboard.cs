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
/// Render states (depth, blend, raster, sampler) are pinned by the caller; this
/// helper only sets the blend state to premultiplied alpha and restores nothing
/// else — mirroring PlanetPoster.Draw.
/// </summary>
internal static class SkyBillboard
{
    /// <summary>Draw one billboarded textured quad at <paramref name="dir"/> on
    /// the sky shell at <paramref name="dist"/>, half-extent
    /// <paramref name="halfWorld"/>, tinted by <paramref name="diffuse"/>
    /// (multiplied into the texture; white leaves it unchanged).</summary>
    public static void Draw(
        GraphicsDevice gd, Matrix view, Matrix projection, Vector3 cameraEye,
        Vector3 dir, float dist, float halfWorld, Texture2D texture, Color? diffuse = null,
        float rotationRad = 0f)
    {
        if (texture is null) return;
        var center = dir * dist; // camera-relative: never offset by cameraEye (P576b)

        // Rotation (the deep-field galaxies spin freely): rotate the billboard's
        // local axes around the facing direction before composing up/right.
        var baseRight = NativeSkyPlacement.BillboardRight(dir);
        var baseUp = NativeSkyPlacement.BillboardUp(dir, baseRight);
        float c = MathF.Cos(rotationRad), s = MathF.Sin(rotationRad);
        var right = baseRight * c + baseUp * s;
        var up = -baseRight * s + baseUp * c;

#if BROWSER
        var scratch = BrowserSkyBuffers.For(gd);
        var corners = scratch.Quad;
        corners[0] = new VertexPositionColorTexture(center - right * halfWorld - up * halfWorld, Color.White, new Vector2(0, 1));
        corners[1] = new VertexPositionColorTexture(center + right * halfWorld - up * halfWorld, Color.White, new Vector2(1, 1));
        corners[2] = new VertexPositionColorTexture(center - right * halfWorld + up * halfWorld, Color.White, new Vector2(0, 0));
        corners[3] = new VertexPositionColorTexture(center + right * halfWorld + up * halfWorld, Color.White, new Vector2(1, 0));
        var indices = scratch.Indices;
#else
        var corners = new[]
        {
            new VertexPositionColorTexture(center - right * halfWorld - up * halfWorld, Color.White, new Vector2(0, 1)),
            new VertexPositionColorTexture(center + right * halfWorld - up * halfWorld, Color.White, new Vector2(1, 1)),
            new VertexPositionColorTexture(center - right * halfWorld + up * halfWorld, Color.White, new Vector2(0, 0)),
            new VertexPositionColorTexture(center + right * halfWorld + up * halfWorld, Color.White, new Vector2(1, 0)),
        };
        var indices = new short[] { 0, 1, 2, 2, 1, 3 };
#endif

        gd.BlendState = BlendState.AlphaBlend;
#if BROWSER
        var effect = BrowserSkyBuffers.For(gd).Prepare(texture, diffuse?.ToVector3() ?? Vector3.One, view, projection);
#else
        using var effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = texture,
            VertexColorEnabled = true,
            DiffuseColor = diffuse?.ToVector3() ?? Vector3.One,
            LightingEnabled = false,
            View = view,
            Projection = projection,
            World = Matrix.Identity,
        };
#endif
#if BROWSER
        for (int passIndex = 0; passIndex < effect.CurrentTechnique.Passes.Count; passIndex++)
        {
            var pass = effect.CurrentTechnique.Passes[passIndex];
#else
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
#endif
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, corners, 0, corners.Length, indices, 0, 2);
        }
    }
}
