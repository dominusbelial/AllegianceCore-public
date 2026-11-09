using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Opt-in HD tails attach to actual bright texels, using the dome's
/// own UV triangles. No guessed equirectangular orientation or extra point layer.
/// Extraction happens once; the baked stars keep their original draw.</summary>
public static class HdStarSamples
{
    public static VertexStar[] Extract(Color[] pixels, int width, int height,
        VertexPositionColorTexture[] vertices, short[] indices)
    {
        var samples = new List<(int X, int Y, byte Brightness)>();
        const int tile = 16;
        for (int y = 0; y < height; y += tile)
            for (int x = 0; x < width; x += tile)
            {
                int bestX = x, bestY = y; byte best = 100;
                for (int yy = y; yy < Math.Min(y + tile, height); yy++)
                    for (int xx = x; xx < Math.Min(x + tile, width); xx++)
                    {
                        var color = pixels[yy * width + xx];
                        byte brightness = (byte)((color.R + color.G + color.B) / 3);
                        if (brightness > best) { best = brightness; bestX = xx; bestY = yy; }
                    }
                if (best > 100) samples.Add((bestX, bestY, best));
            }
        var stars = new List<VertexStar>();
        foreach (var sample in samples.OrderByDescending(s => s.Brightness).Take(SimStarMath.BlackTierCount))
        {
            var uv = new Vector2((sample.X + 0.5f) / width, (sample.Y + 0.5f) / height);
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = vertices[(ushort)indices[i]]; var b = vertices[(ushort)indices[i + 1]]; var c = vertices[(ushort)indices[i + 2]];
                var ab = b.TextureCoordinate - a.TextureCoordinate; var ac = c.TextureCoordinate - a.TextureCoordinate;
                var ap = uv - a.TextureCoordinate;
                float determinant = ab.X * ac.Y - ab.Y * ac.X;
                if (MathF.Abs(determinant) < 1e-8f) continue;
                float u = (ap.X * ac.Y - ap.Y * ac.X) / determinant;
                float v = (ab.X * ap.Y - ab.Y * ap.X) / determinant;
                if (u < 0 || v < 0 || u + v > 1) continue;
                var position = a.Position + (b.Position - a.Position) * u + (c.Position - a.Position) * v;
                if (position.LengthSquared() < 1e-6f) continue;
                byte level = sample.Brightness;
                stars.Add(new VertexStar(Vector3.Normalize(position), new Color(level, level, level)));
                break;
            }
        }
        return stars.ToArray();
    }
}
