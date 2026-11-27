using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Puff = Allegiance.ClientCore.MonoGame.Rendering.EnhancedSmokeField.Puff;

namespace HudProof;

// Allocating reference for the fixed eye, default lighting and disabled fades
// used in the fixture. The base grouped by texture identity in encounter order.
internal static class ParticleOracle
{
    internal static Dictionary<T, List<ParticleBillboard>> Groups<T>(Puff[] puffs, T[] frames) where T : class
    {
        var groups = new Dictionary<T, List<ParticleBillboard>>();
        var tint = EnhancedVfx.LitSmokeTint(null);
        foreach (var p in puffs)
        {
            float rem = Math.Clamp(p.RemainingFrac, 0, 1), age = 1 - rem;
            float alpha = rem * rem * EnhancedSmokeField.PeakAlpha;
            if (age <= 0 || age >= 1 || alpha <= .004f) continue;
            uint seed = p.Seed;
            if (seed == 0)
            {
                var v = p.Position;
                seed = EnhancedVfx.Hash(EnhancedVfx.Hash((uint)BitConverter.SingleToInt32Bits(v.X)
                    ^ (uint)(BitConverter.SingleToInt32Bits(v.Y) * 0x9E3779B1)
                    ^ (uint)(BitConverter.SingleToInt32Bits(v.Z) * 0x85EBCA77)));
            }
            var tex = frames[(int)(seed % (uint)frames.Length + age * 14f) % frames.Length];
            if (!groups.TryGetValue(tex, out var list)) groups.Add(tex, list = []);
            float spin = ((seed >> 8) % 617u) / 613f * 2f - 1f;
            var color = new Color(tint.X * alpha, tint.Y * alpha, tint.Z * alpha, alpha);
            list.Add(new(p.Position, p.Half * Math.Clamp(p.SizeJitter, .8f, 1.2f),
                color, p.Angle + spin * 1.4f * age));
        }
        return groups;
    }

    internal static (VertexPositionColorTexture[] Vertices, short[] Indices) Geometry(
        List<ParticleBillboard> group, Matrix view)
    {
        var vertices = new VertexPositionColorTexture[group.Count * 4];
        var indices = new short[group.Count * 6];
        var right = new Vector3(view.M11, view.M12, view.M13);
        var up = new Vector3(view.M21, view.M22, view.M23);
        for (int i = 0; i < group.Count; i++)
        {
            var b = group[i];
            float c = MathF.Cos(b.Angle), s = MathF.Sin(b.Angle);
            var x = (right * c + up * s) * b.Half;
            var y = (up * c - right * s) * b.Half;
            int v = i * 4;
            vertices[v] = new(b.CentreEye - x + y, b.Col, Vector2.Zero);
            vertices[v + 1] = new(b.CentreEye + x + y, b.Col, Vector2.UnitX);
            vertices[v + 2] = new(b.CentreEye + x - y, b.Col, Vector2.One);
            vertices[v + 3] = new(b.CentreEye - x - y, b.Col, Vector2.UnitY);
            ReadOnlySpan<short> quad = [(short)v, (short)(v + 1), (short)(v + 2),
                (short)v, (short)(v + 2), (short)(v + 3)];
            quad.CopyTo(indices.AsSpan(i * 6));
        }
        return (vertices, indices);
    }

    internal static Puff[] Puffs(Random random, int count)
    {
        var puffs = new Puff[count];
        for (int i = 0; i < puffs.Length; i++)
            puffs[i] = new(new Vector3(random.NextSingle() * 3 - 1.5f, random.NextSingle() - .5f, -3),
                .25f + random.NextSingle(), i % 13 == 12 ? 1 : random.NextSingle(),
                random.NextSingle() * 6, random.NextSingle() + .5f, i % 7 == 0 ? 0 : (uint)random.Next());
        return puffs;
    }
}
