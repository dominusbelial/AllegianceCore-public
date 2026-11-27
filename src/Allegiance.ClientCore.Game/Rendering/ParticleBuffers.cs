using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

internal static class ParticleStaging
{
    // Normal trails (2048 samples × 3 sub-puffs) fit. Use a call-local list
    // when even the worst-case output could grow the retained list past this.
    internal const int MaxRetainedItems = 8192;

    internal static List<T> Begin<T>(List<T> retained, int sources, int maxPerSource = 1)
    {
        retained.Clear();
        return sources <= MaxRetainedItems / maxPerSource ? retained : [];
    }
}

// Both the smoke and glow renderers have a fixed atlas. A texture owns one
// reusable bin, but its draw position is determined anew by first use each call.
internal sealed class ParticleTextureBins<TTexture> where TTexture : class
{
    private readonly Dictionary<TTexture, List<ParticleBillboard>> _byTexture = [];
    internal List<(TTexture Texture, List<ParticleBillboard> Items)> Active { get; } = [];

    internal void Add(TTexture tex, ParticleBillboard billboard)
    {
        if (!_byTexture.TryGetValue(tex, out var items))
            _byTexture.Add(tex, items = []);
        if (items.Count == 0) Active.Add((tex, items));
        items.Add(billboard);
    }

    internal void Clear()
    {
        foreach (var (_, items) in Active)
        {
            items.Clear();
            if (items.Capacity > 512) items.Capacity = 0;
        }
        Active.Clear();
    }
}

internal sealed class ParticleQuadBuffers
{
    private VertexPositionColorTexture[] _vertices = [];
    private short[] _indices = [];

    internal void Get(int quads, out VertexPositionColorTexture[] vertices, out short[] indices)
    {
        // Keep the original single submission for callers with larger batches.
        // Only the existing chunked billboard draw splits at MaxQuadsPerDraw.
        if (quads > ParticleBillboardBatcher.MaxQuadsPerDraw)
        {
            vertices = new VertexPositionColorTexture[quads * 4];
            indices = new short[quads * 6];
            return;
        }
        if (quads * 4 > _vertices.Length)
        {
            int capacity = Math.Min(ParticleBillboardBatcher.MaxQuadsPerDraw,
                Math.Max(quads, _vertices.Length / 4 * 2));
            _vertices = new VertexPositionColorTexture[capacity * 4];
            _indices = new short[capacity * 6];
        }
        vertices = _vertices;
        indices = _indices;
    }
}
