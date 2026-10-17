using System.Buffers.Binary;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Reusable native-object PBR boundary: lazy validation, mipmapped maps,
/// bounded LRU residency and the existing sector lighting. All GPU work occurs
/// on the drawing thread. Stations/missiles/etc. can use this same material path.</summary>
public sealed class HdMaterialRenderer : IDisposable
{
    public const long DefaultBudgetBytes = 512L * 1024 * 1024;
    private readonly GraphicsDevice _gd;
    private readonly HdPack _pack;
    private readonly Func<string, Stream> _openNative;
    private readonly ShaderAssetCache _shaders;
    private readonly Dictionary<string, Resident> _resident = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _budget;
    private long _clock;
    private readonly Func<double> _seconds;
    private readonly Dictionary<string, double> _deferredUntil = new(StringComparer.OrdinalIgnoreCase);
    public const double EvictionIdleSeconds = 2;
    private Effect? _effect;
    private bool _shaderAttempted;
    private sealed record Resident(Texture2D[] Maps, long Bytes)
    {
        public long LastUse { get; set; }
        public double LastUseSeconds { get; set; }
    }
    public long ResidentBytes { get; private set; }
    public int ResidentMaterials => _resident.Count;
    public long BudgetBytes => _budget;

    public HdMaterialRenderer(GraphicsDevice gd, Func<string, Stream> openNative,
        HdPack? pack = null, long budgetBytes = DefaultBudgetBytes, ShaderAssetCache? shaders = null, Func<double>? seconds = null)
    {
        _gd = gd; _openNative = openNative; _pack = pack ?? new HdPack();
        _budget = budgetBytes;
        _seconds = seconds ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        _shaders = shaders ?? ShaderAssetCache.ForDevice(gd);
        _ownsShaders = shaders is null;
    }
    private readonly bool _ownsShaders;

    /// <summary>Returns false before changing render state when unavailable, so
    /// the caller executes its identical native BasicEffect fallback.</summary>
    public bool TryDraw(string model, string? texture, Func<VertexBuffer> vertices, IndexBuffer indices,
        int triangles, Matrix world, Matrix view, Matrix projection, float opacity, SectorLighting? lighting)
    {
        var material = _pack.Resolve(model, texture, _openNative);
        if (material is null) return false;
        if (!_shaderAttempted)
        {
            _shaderAttempted = true;
            _effect = _shaders.Load("Content/Shaders/NativePbr.ogl.mgfxo");
        }
        if (_effect is null) return false;
        var maps = MapsFor(material);
        if (maps is null) return false;
        var e = _effect;
        e.Parameters["World"].SetValue(world);
        e.Parameters["WorldViewProj"].SetValue(world * view * projection);
        e.Parameters["NormalMatrix"].SetValue(Matrix.Transpose(Matrix.Invert(world)));
        e.Parameters["CameraPosition"].SetValue(Matrix.Invert(view).Translation);
        var light = lighting is { } sector ? SectorLightingEffect.Derive(sector) :
            new SectorLightingEffect.SectorLightSettings(Vector3.Normalize(new(-1, -1, -1)), Vector3.One,
                Vector3.Normalize(new(1, 1, 1)), new(.15f, .2f, .3f), new(.32f, .34f, .36f));
        e.Parameters["KeyDirection"].SetValue(light.KeyDirection);
        e.Parameters["KeyColor"].SetValue(light.KeyColor);
        e.Parameters["AltDirection"].SetValue(light.AltDirection);
        e.Parameters["AltColor"].SetValue(light.AltColor);
        e.Parameters["Ambient"].SetValue(Vector3.Max(light.Ambient, ShipRenderer.ShipAmbientFloor));
        e.Parameters["Opacity"].SetValue(Math.Clamp(opacity, 0, 1));
        string[] parameters = ["BaseMap", "NormalMap", "MetalMap", "RoughMap", "EmissionMap"];
        for (int i = 0; i < maps.Length; i++) e.Parameters[parameters[i]].SetValue(maps[i]);
        e.CurrentTechnique.Passes[0].Apply();
        _gd.SetVertexBuffer(vertices()); _gd.Indices = indices;
        _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, triangles);
        return true;
    }

    private Texture2D[]? MapsFor(HdPack.Material material)
    {
        if (_resident.TryGetValue(material.Model, out var hit))
        {
            hit.LastUse = ++_clock; hit.LastUseSeconds = _seconds(); return hit.Maps;
        }
        if (_failed.Contains(material.Model)) return null;
        if (_deferredUntil.TryGetValue(material.Model, out double until) && _seconds() < until) return null;
        var maps = new List<Texture2D>();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // Read only PNG headers before decoding. Include every mip in the
            // budget; do not temporarily allocate the whole pack's textures.
            var sizes = HdPack.MapNames.Select(name => PngBytes(material.MapPath(name))).ToArray();
            long bytes = sizes.Sum();
            // Reserve one extra map while PNG decoding uses a temporary GPU
            // surface. The mip-inclusive maximum conservatively bounds staging.
            long peakBytes = bytes + sizes.Max();
            if (peakBytes > _budget) throw new InvalidDataException("material exceeds texture memory budget");
            while (ResidentBytes + peakBytes > _budget && _resident.Count > 0)
            {
                var oldest = _resident.MinBy(kv => kv.Value.LastUse);
                // A visible working set can exceed the budget (the HD-1 pilot
                // is ~299 MiB per model). Do not decode/evict it again on every
                // draw: retain recently drawn materials and use native shading
                // for excess models until an idle resident becomes evictable.
                if (_seconds() - oldest.Value.LastUseSeconds < EvictionIdleSeconds)
                {
                    _deferredUntil[material.Model] = _seconds() + .5;
                    return null;
                }
                foreach (var map in oldest.Value.Maps) map.Dispose();
                ResidentBytes -= oldest.Value.Bytes; _resident.Remove(oldest.Key);
            }
            foreach (string name in HdPack.MapNames)
            {
                using var stream = File.OpenRead(material.MapPath(name));
                using var decoded = Texture2D.FromStream(_gd, stream);
                var pixels = new Color[decoded.Width * decoded.Height]; decoded.GetData(pixels);
                maps.Add(CreateMipmapped(_gd, pixels, decoded.Width, decoded.Height, name));
            }
            var resident = new Resident(maps.ToArray(), bytes) { LastUse = ++_clock, LastUseSeconds = _seconds() };
            _resident.Add(material.Model, resident); ResidentBytes += bytes;
            _deferredUntil.Remove(material.Model);
            Console.WriteLine($"[hd] '{material.Model}' resident: {bytes / 1048576.0:F2} MiB; total {ResidentBytes / 1048576.0:F2}/{_budget / 1048576.0:F0} MiB (all mips); first upload {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            return resident.Maps;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            foreach (var map in maps) map.Dispose();
            _failed.Add(material.Model);
            Console.WriteLine($"[hd] '{material.Model}' maps unavailable ({e.Message}); using native material");
            return null;
        }
    }

    public static long MipBytes(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long bytes = 0;
        while (true)
        {
            bytes += (long)width * height * 4;
            if (width == 1 && height == 1) return bytes;
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
    }
    private static long PngBytes(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24]; stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !header[12..16].SequenceEqual("IHDR"u8)) throw new InvalidDataException("invalid PNG header");
        int width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new InvalidDataException("PNG size out of range");
        return MipBytes(width, height);
    }

    public static Texture2D CreateMipmapped(GraphicsDevice gd, Color[] pixels, int width, int height, string map)
    {
        var texture = new Texture2D(gd, width, height, true, SurfaceFormat.Color);
        try
        {
            int level = 0;
            while (true)
            {
                texture.SetData(level++, null, pixels, 0, pixels.Length);
                if (width == 1 && height == 1) break;
                int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
                var next = new Color[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        Vector3 sum = Vector3.Zero; float alpha = 0;
                        // Area coverage also retains the last row/column of odd-size PNGs.
                        int x0 = x * width / w, x1 = (x + 1) * width / w;
                        int y0 = y * height / h, y1 = (y + 1) * height / h;
                        for (int sy = y0; sy < y1; sy++)
                            for (int sx = x0; sx < x1; sx++)
                            {
                                var color = pixels[sy * width + sx]; var v = color.ToVector3();
                                if (map is "basecolor" or "emission") v = new(MathF.Pow(v.X, 2.2f), MathF.Pow(v.Y, 2.2f), MathF.Pow(v.Z, 2.2f));
                                if (map == "normal") v = v * 2 - Vector3.One;
                                sum += v; alpha += color.A / 255f;
                            }
                        int count = (x1 - x0) * (y1 - y0); sum /= count; alpha /= count;
                        if (map is "basecolor" or "emission") sum = new(MathF.Pow(sum.X, 1 / 2.2f), MathF.Pow(sum.Y, 1 / 2.2f), MathF.Pow(sum.Z, 1 / 2.2f));
                        if (map == "normal") sum = (sum.LengthSquared() > 1e-10f ? Vector3.Normalize(sum) : Vector3.UnitZ) * .5f + new Vector3(.5f);
                        next[y * w + x] = new Color(new Vector4(sum, alpha));
                    }
                pixels = next; width = w; height = h;
            }
            return texture;
        }
        catch { texture.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var resident in _resident.Values) foreach (var map in resident.Maps) map.Dispose();
        _resident.Clear(); _deferredUntil.Clear(); ResidentBytes = 0;
        if (_ownsShaders) _shaders.Dispose();
    }
}
