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
    private readonly bool _forceBc3;
    private long _clock;
    private readonly Func<double> _seconds;
    private readonly Dictionary<string, double> _deferredUntil = new(StringComparer.OrdinalIgnoreCase);
    public const double EvictionIdleSeconds = 2;
    private HdCompressedUpload? _compressed;
    private bool _compressedAttempted;
    private Effect? _effect;
    private bool _shaderAttempted;
    private sealed record Resident(Texture2D[] Maps, long Bytes, int Quality)
    {
        public long LastUse { get; set; }
        public double LastUseSeconds { get; set; }
        public double LastDetailSeconds { get; set; }
    }
    /// <summary>HD-5 — how the pack participates for this renderer: Off (native),
    /// Classic (HD basecolor + native lighting, the new default) or Pbr
    /// (experimental). Single source of truth for every renderer that shares this
    /// instance (ships, stations, missiles, probes, projectiles, asteroids,
    /// treasure, warps).</summary>
    public HdDrawMode Mode { get; set; } = HdDrawMode.Classic;
    /// <summary>Derive the pack participation from the two player settings:
    /// HD textures on/off and the experimental HD lighting (PBR) on/off. Pure and
    /// testable (HD-5).</summary>
    public static HdDrawMode ModeFor(bool hdTextures, bool hdPbr)
        => hdTextures ? (hdPbr ? HdDrawMode.Pbr : HdDrawMode.Classic) : HdDrawMode.Off;
    public EffectLights? Flashes { get; set; }
    public long ResidentBytes { get; private set; }
    public int ResidentMaterials => _resident.Count;
    public long BudgetBytes => _budget;

    public HdMaterialRenderer(GraphicsDevice gd, Func<string, Stream> openNative,
        HdPack? pack = null, long budgetBytes = DefaultBudgetBytes, ShaderAssetCache? shaders = null, Func<double>? seconds = null, bool forceBc3 = false)
    {
        _gd = gd; _openNative = openNative; _pack = pack ?? new HdPack();
        _budget = budgetBytes; _forceBc3 = forceBc3;
        _seconds = seconds ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        _shaders = shaders ?? ShaderAssetCache.ForDevice(gd);
        _ownsShaders = shaders is null;
    }
    private readonly bool _ownsShaders;

    /// <summary>Native unlit/additive geometry keeps its authored emissive pass,
    /// animation and vertex colours while sharing validation, residency and toggle.
    /// Returns the HD basecolor as a UNORM texture for BasicEffect, or null in
    /// <see cref="HdDrawMode.Off"/> / when unavailable.</summary>
    public Texture2D? EmissiveTexture(string texture)
    {
        if (Mode == HdDrawMode.Off) return null;
        var material = _pack.Resolve("", texture, _openNative);
        return material is null ? null : MapsFor(material, 256, unlit: true)?[0];
    }

    /// <summary>HD-5 — the pack's <b>basecolor</b> (UNORM / sRGB-encoded) for the
    /// native classic path: BasicEffect consumes the encoded samples directly, so
    /// the classic HD material renders with the EXACT native lighting/blending but
    /// a sharper texture. Returns null when the material is unavailable (caller
    /// keeps the native texture). Screen footprint selects the basecolor mip,
    /// mirroring the PBR resolution policy so an up-close hull gets its full
    /// resolution. The caller has already selected Classic mode; this is called
    /// only from that path.</summary>
    public Texture2D? ClassicColor(string? texture, float projectedPixels)
    {
        if (texture is null) return null;
        var material = _pack.Resolve("", texture, _openNative);
        if (material is null) return null;
        return MapsFor(material, projectedPixels, unlit: true)?[0];
    }

    /// <summary>Returns false before changing render state when unavailable, so
    /// the caller executes its identical native BasicEffect fallback. The caller
    /// has already selected Pbr mode (this is only reached from that path).</summary>
    public bool TryDraw(string model, string? texture, Func<VertexBuffer> vertices, IndexBuffer indices,
        int triangles, Matrix world, Matrix view, Matrix projection, float opacity, SectorLighting? lighting, int startIndex = 0, float modelRadius = 1)
    {
        var material = _pack.Resolve(model, texture, _openNative);
        if (material is null) return false;
        if (!_shaderAttempted)
        {
            _shaderAttempted = true;
            _effect = _shaders.Load("Content/Shaders/NativePbr.ogl.mgfxo");
        }
        if (_effect is null) return false;
        var center = Vector3.Transform(Vector3.Zero, world * view);
        float scale = Math.Max(world.Right.Length(), Math.Max(world.Up.Length(), world.Backward.Length()));
        float pixels = 2 * modelRadius * scale * Math.Abs(projection.M22) * _gd.Viewport.Height / Math.Max(.001f, 2 * Math.Abs(center.Z));
        var maps = MapsFor(material, pixels);
        if (maps is null) return false;
        var e = _effect;
        bool flashes = Flashes is { Detail: not Allegiance.ClientCore.State.EffectsDetail.Low };
        e.CurrentTechnique = e.Techniques[flashes ? "EffectLitPbr" : "NativePbr"];
        if (flashes)
        {
            e.Parameters["FlashPositions"].SetValue(Flashes!.Positions);
            e.Parameters["FlashColors"].SetValue(Flashes!.Colors);
        }
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
        e.Parameters["CompressedMaps"].SetValue(material.Compressed ? 1f : 0f);
        e.Parameters["Opacity"].SetValue(Math.Clamp(opacity, 0, 1));
        string[] parameters = ["BaseMap", "NormalMap", "MetalMap", "RoughMap", "EmissionMap"];
        for (int i = 0; i < maps.Length; i++) e.Parameters[parameters[i]].SetValue(maps[i]);
        var sampler = _gd.SamplerStates[0];
        e.CurrentTechnique.Passes[0].Apply();
        // Station/asteroid UVs wrap; hull/missile callers clamp. All maps must
        // follow the native material's addressing, including after shader Apply.
        for (int i = 0; i < maps.Length; i++) _gd.SamplerStates[i] = sampler;
        _gd.SetVertexBuffer(vertices()); _gd.Indices = indices;
        _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, startIndex, triangles);
        return true;
    }

    private Texture2D[]? MapsFor(HdPack.Material material, float projectedPixels, bool unlit = false)
    {
        // BasicEffect's unlit path consumes encoded sRGB samples directly.
        // Give it a separately budgeted UNORM color texture (basecolor only —
        // HD-5: the classic path residents just the color map, not the 5-map
        // PBR set); PBR uses sRGB and the full set.
        string key = material.Key + (unlit ? "|unlit" : "");
        string[] names = unlit ? ["basecolor"] : HdPack.MapNames;
        // Quantized requested resolution, shared by every user of an atlas.
        int quality = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(64, projectedPixels * 2))), 6, 13);
        if (_resident.TryGetValue(key, out var hit))
        {
            // Promote immediately on approach. Demote only after idle, so a far
            // instance cannot replace the close instance's atlas every frame.
            if (quality >= hit.Quality) hit.LastDetailSeconds = _seconds();
            if (!material.Compressed || quality == hit.Quality || quality < hit.Quality && _seconds() - hit.LastDetailSeconds < EvictionIdleSeconds)
            { hit.LastUse = ++_clock; hit.LastUseSeconds = _seconds(); return hit.Maps; }
            foreach (var map in hit.Maps) map.Dispose();
            ResidentBytes -= hit.Bytes; _resident.Remove(key);
        }
        if (_failed.Contains(key)) return null;
        if (_deferredUntil.TryGetValue(key, out double until) && _seconds() < until) return null;
        var maps = new List<Texture2D>();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // Read only PNG headers before decoding. Include every mip in the
            // budget; do not temporarily allocate the whole pack's textures.
            if (material.Compressed && !_compressedAttempted)
            { _compressedAttempted = true; _compressed = HdCompressedUpload.TryCreate(); }
            if (material.Compressed && _compressed is null) throw new InvalidDataException("compressed textures unsupported");
            string MapPath(string name)
            {
                string fallback = Path.Combine(material.Directory, name + ".fallback.dds");
                return material.Compressed && (name is "basecolor" or "emission") && (_forceBc3 || !_compressed!.Bptc) && File.Exists(fallback)
                    ? fallback : material.MapPath(name);
            }
            var headers = material.Compressed ? names.Select(name => HdDds.Read(MapPath(name), name)).ToArray() : [];
            if (material.Compressed && !_compressed!.Bptc && headers.Any(h => h.Bc == 7))
                throw new InvalidDataException("BC7 unavailable; install the BC3 release zip");
            // Map resolution ratios are fixed by the builder. Select each map's
            // mip independently against the same screen footprint.
            var first = headers.Select(h => HdDds.SelectFirstMip(h.Width, h.Height, MathF.Pow(2, quality) / 2)).ToArray();
            var sizes = material.Compressed ? headers.Select((h, i) => h.BytesFrom(first[i])).ToArray() :
                names.Select(name => PngBytes(MapPath(name))).ToArray();
            long bytes = sizes.Sum();
            // Reserve one extra map while PNG decoding uses a temporary GPU
            // surface. The mip-inclusive maximum conservatively bounds staging.
            long peakBytes = bytes + (material.Compressed ? headers.Select((h, i) => h.Bc == 4 ? sizes[i] : 0).DefaultIfEmpty().Max() : sizes.Max());
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
                    _deferredUntil[key] = _seconds() + .5;
                    return null;
                }
                foreach (var map in oldest.Value.Maps) map.Dispose();
                ResidentBytes -= oldest.Value.Bytes; _resident.Remove(oldest.Key);
            }
            foreach (string name in names)
            {
                if (material.Compressed)
                {
                    int i = maps.Count;
                    maps.Add(_compressed!.Load(_gd, MapPath(name), headers[i], first[i], colorSrgb: !unlit));
                    continue;
                }
                using var stream = File.OpenRead(material.MapPath(name));
                using var decoded = Texture2D.FromStream(_gd, stream);
                var pixels = new Color[decoded.Width * decoded.Height]; decoded.GetData(pixels);
                maps.Add(CreateMipmapped(_gd, pixels, decoded.Width, decoded.Height, name));
            }
            var resident = new Resident(maps.ToArray(), bytes, quality) { LastUse = ++_clock, LastUseSeconds = _seconds(), LastDetailSeconds = _seconds() };
            _resident.Add(key, resident); ResidentBytes += bytes;
            _deferredUntil.Remove(key);
            Console.WriteLine($"[hd] '{key}' resident: {bytes / 1048576.0:F2} MiB; total {ResidentBytes / 1048576.0:F2}/{_budget / 1048576.0:F0} MiB (all mips); first upload {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            return resident.Maps;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            foreach (var map in maps) map.Dispose();
            _failed.Add(key);
            Console.WriteLine($"[hd] '{key}' maps unavailable ({e.Message}); using native material");
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

    public void ResetResidency()
    {
        foreach (var resident in _resident.Values) foreach (var map in resident.Maps) map.Dispose();
        _resident.Clear(); _deferredUntil.Clear(); ResidentBytes = 0; _clock = 0;
    }
    public void Dispose()
    {
        ResetResidency();
        if (_ownsShaders) _shaders.Dispose();
    }
}
