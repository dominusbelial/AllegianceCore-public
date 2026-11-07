using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P943 — the training slideshow bitmaps. Native loads each <c>ImportImage</c> as a
/// surface through the modeler (<c>engine/modeler.cpp:691-706</c>,
/// <c>engine/modeler.cpp:2463-2492</c>); the port reuses the same two encodings the
/// HUD art ships in: a binary RGB565 <c>.mdl</c> (<see cref="MdlTextureDecoder"/>) and
/// an <c>ImportImageFromFile("&lt;file&gt;", …)</c> script. A stem imported with
/// <c>colorKey = true</c> has its near-black surround keyed to alpha 0
/// (<c>SetColorKeyHint</c>), exactly like the port's other 2-D art loaders.
/// Sizes are cached and available without a GPU, so the artwork test can resolve
/// every slide's layout headlessly. Missing art is remembered and logged once.
/// </summary>
internal sealed class SlideshowArt : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly Dictionary<string, (int Width, int Height)?> _sizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private bool _disposed;

    public SlideshowArt(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
    }

    /// <summary>A bitmap's native pixel size, or null when the artwork is absent —
    /// the resolver <see cref="Allegiance.ClientCore.Training.SlideshowLayout.Build"/>
    /// takes.</summary>
    public (int Width, int Height)? Size(string stem)
    {
        if (_sizes.TryGetValue(stem, out var cached)) return cached;
        var size = ProbeSize(_assets, stem);
        _sizes[stem] = size;
        return size;
    }

    /// <summary>The native pixel size of a slideshow bitmap, headless (no
    /// <see cref="GraphicsDevice"/>): a binary <c>.mdl</c> header/payload or the
    /// <c>ImportImageFromFile</c> script's referenced PNG/JPEG. Null when absent.</summary>
    public static (int Width, int Height)? ProbeSize(IAssetSource assets, string stem)
    {
        string rel = stem + ".mdl";
        try
        {
            if (!assets.Exists(rel)) return null;
            byte[] bytes = AssetSources.ReadAllBytes(assets, rel);
            if (IsBinaryMdl(bytes))
            {
                var decoded = MdlTextureDecoder.Decode(bytes, flipVertical: false);
                return (decoded.Width, decoded.Height);
            }
            return HudNativeTextures.ResolveImportedImage(assets, stem) is { } imported
                ? (imported.Width, imported.Height)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The drawable texture for a slide bitmap, or null when it cannot be
    /// loaded (the slide then falls back to its narration caption).</summary>
    public Texture2D? Get(string stem, bool colorKey)
    {
        if (_textures.TryGetValue(stem, out var cached)) return cached;
        Texture2D? texture = null;
        string rel = stem + ".mdl";
        try
        {
            if (!_assets.Exists(rel))
            {
                LogMissingOnce(stem, "no such bitmap");
            }
            else
            {
                byte[] bytes = AssetSources.ReadAllBytes(_assets, rel);
                if (IsBinaryMdl(bytes))
                {
                    var decoded = MdlTextureDecoder.Decode(bytes, flipVertical: false);
                    // Native ImportImage(..., colorKey=true) keys the black surround
                    // (modeler.cpp:2463-2492 -> SetColorKeyHint); the port's
                    // premultiplied key is the same visual (engine/modeler.cpp:2491-2492).
                    byte[] rgba = colorKey ? MdlTextureDecoder.ToPremultipliedRgba(decoded) : decoded.Rgba;
                    texture = new Texture2D(_gd, decoded.Width, decoded.Height, false, SurfaceFormat.Color);
                    var colors = new Color[decoded.Width * decoded.Height];
                    for (int i = 0; i < colors.Length; i++)
                    {
                        int b = i * 4;
                        colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                    }
                    texture.SetData(colors);
                }
                else if (HudNativeTextures.ResolveImportedImage(_assets, stem) is { } imported)
                {
                    using var stream = _assets.OpenRead(imported.File);
                    using var source = Texture2D.FromStream(_gd, stream);
                    var pixels = new Color[source.Width * source.Height];
                    source.GetData(pixels);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        float a = pixels[i].A / 255f;
                        pixels[i] = new Color((byte)(pixels[i].R * a), (byte)(pixels[i].G * a),
                            (byte)(pixels[i].B * a), pixels[i].A);
                    }
                    texture = new Texture2D(_gd, source.Width, source.Height);
                    texture.SetData(pixels);
                }
                else
                {
                    LogMissingOnce(stem, "unsupported image script");
                }
            }
        }
        catch (Exception ex)
        {
            LogMissingOnce(stem, ex.Message);
            texture = null;
        }
        _textures[stem] = texture;
        return texture;
    }

    /// <summary>Log a missing slide bitmap exactly once per stem (P943 rule: no
    /// per-frame spam while a slideshow sits on the same broken slide).</summary>
    public void LogMissingOnce(string stem, string reason)
    {
        if (_logged.Add(stem))
            Console.WriteLine($"[training] slide art '{stem}' missing ({reason}) — caption fallback");
    }

    private static bool IsBinaryMdl(byte[] bytes) =>
        bytes.Length >= 4 &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) == MdlTextureDecoder.MdlMagic;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        _sizes.Clear();
    }
}
