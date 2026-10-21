using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-19 — the native joystick turning-indicator layout, transcribed from
/// <c>RenderJoystickIndicator</c> (WinTrek/indicator.cpp:154-177). Native
/// image space is y-UP (engine/context.cpp:552 "DrawImage3D uses lower left as
/// (0,0)"), so the indicator's translate
/// <c>centre − 100·(yaw, pitch)</c> converts to screen y-down as
/// <c>(centre.X − 100·yaw, centre.Y + 100·pitch)</c> (y is mirrored about the
/// centre). The mesh apex points toward +y in image space; rotated by
/// <c>angle = atan2(pitch, yaw) + π/2 (+ π if yaw&lt;0)</c>, its on-screen
/// direction is <c>(−sin angle, −cos angle)</c>. Pure and GPU-free so the
/// placement/pointing is unit-testable without a graphics device.
/// </summary>
public static class JoystickLayout
{
    /// <summary>The 100 px/radian turn-offset constant (indicator.cpp:166-167).</summary>
    public const float TurnOffset = 100f;

    /// <summary>The native on-screen centre-relative position: image y-up
    /// <c>centre − 100·(yaw,pitch)</c> mapped to screen y-down (y negated).</summary>
    public static Vector2 Position(Vector2 centre, float yaw, float pitch)
        => new(centre.X - TurnOffset * yaw, centre.Y + TurnOffset * pitch);

    /// <summary>The native rotation angle in image y-up space
    /// (<c>atan(pitch/yaw) + π/2</c>, +π if yaw&lt;0, indicator.cpp:161-165).</summary>
    public static float Angle(float yaw, float pitch)
    {
        float angle = MathF.Atan2(pitch, yaw) + MathF.PI / 2f;
        if (yaw < 0f) angle += MathF.PI;
        return angle;
    }

    /// <summary>The direction the chevron APEX points on screen (the image
    /// y-up +y rotated by <see cref="Angle"/>, then y-mirrored for screen).</summary>
    public static Vector2 ApexDirection(float yaw, float pitch)
    {
        float angle = Angle(yaw, pitch);
        return new(-MathF.Sin(angle), -MathF.Cos(angle));
    }

    /// <summary>The SpriteBatch rotation that points a chevron whose apex is
    /// baked at the texture TOP toward <paramref name="apex"/> (local +y maps
    /// to screen <c>(sin r, −cos r)</c> under SpriteBatch rotation r).</summary>
    public static float Rotation(Vector2 apex) => MathF.Atan2(apex.X, -apex.Y);

    /// <summary>Native <c>|control|/2 + 0.5</c> scale (indicator.cpp:171).</summary>
    public static float Scale(float yaw, float pitch)
        => 0.5f + 0.5f * MathF.Min(1f, MathF.Sqrt(yaw * yaw + pitch * pitch));

    /// <summary>The native <c>|control|</c> alpha modulator (indicator.cpp:172
    /// material diffuse alpha).</summary>
    public static float Magnitude(float yaw, float pitch)
        => MathF.Min(1f, MathF.Sqrt(yaw * yaw + pitch * pitch));
}

/// <summary>
/// HUD-19 — rasterise the native <c>joystickindicator.mdl</c> 6-vertex chevron
/// into a small PREMULTIPLIED RGBA texture. The mesh is a flat <c>MeshGeo</c>
/// (all z = 0, normals (0,0,1)) wrapped in a <c>TextureGeo</c> with
/// <c>joystickindicatortexture.png</c> (2×2 RGBA, every texel
/// (255,255,255,0xB3) — white at alpha ≈ 0.70). Native renders it in an
/// orthographic layer, so mesh units are image pixels (about 18 × 7.5); it is
/// lit face-on (light (0,0,1), ambient 0.25 — a flat plane normal (0,0,1) has
/// N·L = 1 everywhere, so the lit colour is a near-uniform white), and its
/// material diffuse alpha is |control|. This rasteriser produces the premul
/// alpha = 0.70 × coverage; the renderer draws it centred with the native
/// rotation/scale and multiplies by |control|. Pure and GPU-free.
/// </summary>
public static class JoystickMeshRasterizer
{
    // Vertices (mesh x, y), y-up, all z = 0 (joystickindicator.mdl):
    // (-9,-3.5) (-8.4,-0.8) (0,1) (0,4) (9,-3.5) (8.4,-0.8).
    private static readonly (float x, float y)[] V =
    {
        (-9f, -3.5f), (-8.4f, -0.8f), (0f, 1f), (0f, 4f), (9f, -3.5f), (8.4f, -0.8f),
    };
    // Indices [0,1,2, 1,3,2, 3,5,2, 2,5,4] — a chevron ~18 × 7.5 with its apex
    // toward +y (vertex 3 = (0,4)).
    private static readonly (int a, int b, int c)[] Tri =
    {
        (0, 1, 2), (1, 3, 2), (3, 5, 2), (2, 5, 4),
    };

    /// <summary>Native texture alpha (joystickindicatortexture.png: 0xB3).</summary>
    public const float TextureAlpha = 0xB3 / 255f;

    /// <summary>Rasterised texture width (mesh spans x ∈ [−9, 9]).</summary>
    public const int Width = 18;
    /// <summary>Rasterised texture height (mesh spans y ∈ [−3.5, 4]).</summary>
    public const int Height = 8;

    /// <summary>
    /// Produce the chevron as a premultiplied RGBA byte array at the native
    /// pixel size, supersampled <paramref name="supersample"/>× per axis for
    /// clean edges (allocates nothing per frame; called once at load).
    /// </summary>
    public static byte[] RasterizePremultiplied(int supersample = 8)
    {
        if (supersample < 1) supersample = 1;
        var rgba = new byte[Width * Height * 4];
        float ymax = 4f;                       // mesh y-up top maps to texture row 0
        for (int py = 0; py < Height; py++)
        {
            for (int px = 0; px < Width; px++)
            {
                int hits = 0;
                for (int sy = 0; sy < supersample; sy++)
                {
                    for (int sx = 0; sx < supersample; sx++)
                    {
                        float mx = (px + (sx + 0.5f) / supersample) - Width / 2f;   // mesh x
                        float my = ymax - (py + (sy + 0.5f) / supersample);          // mesh y-up
                        if (InsideChevron(mx, my)) hits++;
                    }
                }
                float cov = hits / (float)(supersample * supersample);
                // Premultiplied white: rgb = a = 0.70·coverage (alpha pass draws
                // with AlphaBlend and multiplies the whole thing by |control|).
                byte a = (byte)MathF.Round(255f * cov * TextureAlpha);
                int i = (py * Width + px) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = a;
                rgba[i + 3] = a;
            }
        }
        return rgba;
    }

    private static bool InsideChevron(float x, float y)
    {
        foreach (var (a, b, c) in Tri)
        {
            float d1 = Cross(V[a], V[b], x, y);
            float d2 = Cross(V[b], V[c], x, y);
            float d3 = Cross(V[c], V[a], x, y);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(hasNeg && hasPos)) return true;
        }
        return false;
    }

    private static float Cross((float x, float y) a, (float x, float y) b, float px, float py)
        => (b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x);
}

/// <summary>
/// HUD-19 — build the native joystick chevron <see cref="Texture2D"/> once at
/// load (called in <c>Game1.LoadContent</c>). Rasterised from the mesh
/// geometry — no external artwork file is required.
/// </summary>
public static class JoystickMeshTexture
{
    public static Texture2D Create(GraphicsDevice gd, int supersample = 8)
    {
        byte[] rgba = JoystickMeshRasterizer.RasterizePremultiplied(supersample);
        var colors = new Color[JoystickMeshRasterizer.Width * JoystickMeshRasterizer.Height];
        for (int i = 0; i < colors.Length; i++)
        {
            int b = i * 4;
            colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
        }
        var tex = new Texture2D(gd, JoystickMeshRasterizer.Width, JoystickMeshRasterizer.Height);
        tex.SetData(colors);
        return tex;
    }
}
