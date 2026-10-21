using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-16 — the native per-contact radar icon surfaces. Every radar contact's
/// model icon is a 2-D bitmap loaded once from the IGC <c>iconName</c> +
/// <c>"bmp"</c> (<c>WinTrek/trekigc.cpp:2731-2744</c>) and drawn additively at
/// 1:1 pixel size (<c>radarimage.cpp:417-420</c>, <c>BlendModeAdd</c>).
///
/// <para>
/// The loader reuses the port's established <c>*bmp.mdl</c> decode path
/// (<see cref="MdlTextureDecoder"/>, the same one HUD-13/15 use) and keeps one
/// <see cref="Texture2D"/> per surface stem in a dictionary: each bitmap is
/// decoded exactly once, no matter how many contacts use it or how many frames
/// are drawn. Surfaces whose pixels are all colour-keyed to alpha 0 are treated
/// as <b>absent</b> (native's artwork tree ships some bracket art blank — HUD-13
/// found <c>enemybmp.mdl</c> all black), so the renderer keeps its procedural
/// fallback instead of drawing nothing silently.
/// </para>
/// <para>
/// Stems come from <see cref="RadarIconNameCache"/> at startup (every hull,
/// station, missile, probe, mine, asteroid and treasure surface is known from
/// the core) and from the wire for per-instance icons (warp/asteroid) the first
/// time a contact uses them. Both are one-off costs outside the per-frame path.
/// </para>
/// </summary>
public sealed partial class RadarIconTextures : IDisposable
{
    [GeneratedRegex("ImportImageFromFile\\s*\\(\\s*\"([^\"]+)\"\\s*,\\s*(?:true|false)\\s*\\)")]
    private static partial Regex ImageImport();

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly Dictionary<string, Texture2D?> _byStem = new(StringComparer.OrdinalIgnoreCase);
    // Native's artwork tree was loaded from a case-insensitive Windows
    // filesystem, so a stem like "aleph" resolved 'Alephbmp.mdl'. This
    // checkout's asset sources are case-sensitive, so fall back to one
    // lower-cased file-name index (built lazily, startup-only) when the exact
    // spelling is absent — the same courtesy StatusTextures pays 'Targetedbmp'.
    private Dictionary<string, string>? _caseInsensitiveFiles;
    private bool _disposed;

    /// <summary>Surfaces decoded successfully.</summary>
    public int LoadedCount { get; private set; }
    /// <summary>Surfaces whose art file is missing or failed to decode.</summary>
    public int MissingCount { get; private set; }
    /// <summary>Surfaces present but fully transparent (treated as absent).</summary>
    public int BlankCount { get; private set; }

    private RadarIconTextures(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
    }

    /// <summary>Create the cache and preload every surface the core's type
    /// tables reference (<paramref name="names"/>), matching native's "load the
    /// icon once when the model type is created" lifetime.</summary>
    public static RadarIconTextures Load(GraphicsDevice gd, IAssetSource assets, RadarIconNameCache? names)
    {
        var textures = new RadarIconTextures(gd, assets);
        if (names is not null)
            foreach (var stem in names.KnownStems())
                textures.EnsureLoaded(stem);
        Console.WriteLine($"[monogame] hud-16 radar icons: {textures.LoadedCount} surfaces, "
            + $"{textures.BlankCount} blank, {textures.MissingCount} missing");
        return textures;
    }

    /// <summary>True when a decoded surface carries no visible pixel at all
    /// (every pixel colour-keyed to alpha 0). Same rule HUD-13 applied to
    /// <c>enemybmp</c> (<see cref="StatusTextures.IsFullyTransparent"/>). Pure.</summary>
    public static bool IsBlank(ReadOnlySpan<Color> pixels)
    {
        foreach (var p in pixels)
            if (p.A != 0) return false;
        return true;
    }

    /// <summary>The decoded surface for a stem, or null when it is missing,
    /// blank or undecodable. The lookup is a dictionary read after the first
    /// (startup/lazy) load, so it allocates nothing on the frame path.</summary>
    public Texture2D? Get(string? stem) => stem is null ? null : EnsureLoaded(stem);

    /// <summary>Null-allocation peek used by the draw loop when the surface was
    /// already resolved (the additive pass preloads before it begins).</summary>
    public Texture2D? Peek(string? stem)
        => stem is not null && _byStem.TryGetValue(stem, out var texture) ? texture : null;

    /// <summary>Decode one stem if it has not been looked up before; returns the
    /// cached result (null for missing/blank art). Never decodes twice.</summary>
    public Texture2D? EnsureLoaded(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        if (_byStem.TryGetValue(stem, out var cached)) return cached;
        var texture = Decode(stem);
        _byStem[stem] = texture;
        if (texture is not null) LoadedCount++;
        return texture;
    }

    /// <summary>Decode one native radar-icon surface from
    /// <c>&lt;stem&gt;.mdl</c> (the artwork tree's unit name; native's
    /// <c>LoadSurface(stem)</c> resolves the same file). Accepts the binary
    /// RGB565 surface <see cref="MdlTextureDecoder"/> reads and the tiny
    /// <c>ImportImageFromFile("&lt;png&gt;") </c> script form some units use.</summary>
    private Texture2D? Decode(string stem)
    {
        string rel = stem + ".mdl";
        try
        {
            rel = ResolvePath(rel);
            if (!_assets.Exists(rel))
            {
                MissingCount++;
                Console.WriteLine($"[monogame] hud-16 radar icon '{stem}' missing ({rel}) — procedural fallback");
                return null;
            }
            byte[] data = AssetSources.ReadAllBytes(_assets, rel);
            if (data.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data)
                == MdlTextureDecoder.MdlMagic)
            {
                var decoded = MdlTextureDecoder.Decode(data, flipVertical: false);
                byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(decoded);
                var colors = new Color[decoded.Width * decoded.Height];
                for (int i = 0; i < colors.Length; i++)
                {
                    int b = i * 4;
                    colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                if (IsBlank(colors))
                {
                    BlankCount++;
                    Console.WriteLine($"[monogame] hud-16 radar icon '{stem}' is fully transparent "
                        + $"({decoded.Width}x{decoded.Height} '{rel}') — procedural fallback");
                    return null;
                }
                var surface = new Texture2D(_gd, decoded.Width, decoded.Height);
                surface.SetData(colors);
                return surface;
            }

            var match = ImageImport().Match(Encoding.UTF8.GetString(data));
            if (!match.Success)
            {
                MissingCount++;
                Console.WriteLine($"[monogame] hud-16 radar icon '{stem}' has no decodable surface ('{rel}') — procedural fallback");
                return null;
            }
            using var stream = _assets.OpenRead(match.Groups[1].Value);
            using var source = Texture2D.FromStream(_gd, stream);
            var pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);
            for (int i = 0; i < pixels.Length; i++)
            {
                float a = pixels[i].A / 255f;
                pixels[i] = new Color((byte)(pixels[i].R * a), (byte)(pixels[i].G * a), (byte)(pixels[i].B * a), pixels[i].A);
            }
            if (IsBlank(pixels))
            {
                BlankCount++;
                Console.WriteLine($"[monogame] hud-16 radar icon '{stem}' is fully transparent "
                    + $"({source.Width}x{source.Height} '{match.Groups[1].Value}') — procedural fallback");
                return null;
            }
            var png = new Texture2D(_gd, source.Width, source.Height);
            png.SetData(pixels);
            return png;
        }
        catch (Exception ex)
        {
            MissingCount++;
            Console.WriteLine($"[monogame] hud-16 radar icon '{stem}' failed: {ex.Message} — procedural fallback");
            return null;
        }
    }

    /// <summary>The on-disk path of a native surface name, matching native's
    /// case-insensitive Windows lookup. Falls back to the requested spelling
    /// when the asset source cannot be enumerated.</summary>
    private string ResolvePath(string relativePath)
    {
        if (_assets.Exists(relativePath)) return relativePath;
        if (_caseInsensitiveFiles is null)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var file in _assets.Enumerate("."))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        index[name] = file;
                }
            }
            catch (Exception)
            {
                // Non-enumerable asset source: only exact spellings resolve.
            }
            _caseInsensitiveFiles = index;
        }
        return _caseInsensitiveFiles.TryGetValue(relativePath, out var match) ? match : relativePath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var texture in _byStem.Values) texture?.Dispose();
        _byStem.Clear();
    }
}
