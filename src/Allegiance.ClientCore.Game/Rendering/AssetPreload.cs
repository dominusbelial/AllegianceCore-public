using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One native <c>Preload(pszModelName, pszTextureName)</c> request.</summary>
public readonly record struct PreloadRequest(string? ModelStem, string? TextureStem)
{
    public bool IsEmpty => ModelStem is null && TextureStem is null;
}

/// <summary>
/// P871 / SIM-0223 — the rules of native <c>WinTrekClient::Preload</c>
/// (<c>WinTrek/trekigc.cpp:4912-4937</c>), which the core calls for every model
/// type it creates (<c>Igc/hullTypeIGC.cpp:49-51</c>):
/// <code>
/// bool bOldColorKeyValue = GetModeler()->SetColorKeyHint(false);
/// GetEngine()->SetEnableMipMapGeneration(true);
/// if (pszModelName)   GetModeler()->GetNameSpace(pszModelName);
/// if (pszTextureName) { strcpy(bfr, pszTextureName); strcat(bfr, "bmp"); GetModeler()->GetNameSpace(bfr); }
/// GetModeler()->SetColorKeyHint(bOldColorKeyValue);
/// GetEngine()->SetEnableMipMapGeneration(false);
/// </code>
/// Every element that is behavior rather than DirectX plumbing is preserved:
/// <list type="bullet">
/// <item>a null model name skips only the model, never the texture (the core
/// passes <c>modelName</c>/<c>textureName</c> independently);</item>
/// <item>the texture surface is the texture name with <c>"bmp"</c> appended;</item>
/// <item>the colour-key hint is turned OFF for the duration and restored
/// afterwards (including the previous value when nested);</item>
/// <item>mipmap generation is ON only inside the preload scope; it is reset to
/// <c>false</c> afterwards (native does not restore its previous value).</item>
/// </list>
/// The hull caller additionally only preloads a texture whose first character is
/// alphabetic (<c>iswalpha(textureName[0]) ? textureName : NULL</c>,
/// <c>Igc/hullTypeIGC.cpp:49-51</c>): a purely numeric texture field is a render
/// option, not a surface name.
/// </summary>
public static class PreloadRules
{
    /// <summary>The suffix native appends to a texture name (<c>trekigc.cpp:4925</c>).</summary>
    public const string TextureSuffix = "bmp";

    /// <summary>Native <c>iswalpha(textureName[0])</c> — only an alphabetic
    /// first character names a surface.</summary>
    public static bool IsSurfaceName(string? textureName)
        => !string.IsNullOrEmpty(textureName) && char.IsLetter(textureName[0]);

    /// <summary>Build the native request from a core type's two fields. The
    /// texture stem is <c>textureName + "bmp"</c> only for an alphabetic name.</summary>
    public static PreloadRequest FromType(string? modelName, string? textureName)
    {
        string? model = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();
        string? texture = IsSurfaceName(textureName?.Trim())
            ? string.Concat(textureName!.Trim(), TextureSuffix)
            : null;
        return new PreloadRequest(model, texture);
    }

    /// <summary>Native <c>Modeler::SetColorKeyHint</c> state (default true — the
    /// modeler colour-keys near-black surround, <c>modeler.cpp:726</c>).</summary>
    public static bool ColorKeyHint { get; private set; } = true;

    /// <summary>Native <c>SetEnableMipMapGeneration</c> state (default false).</summary>
    public static bool MipMapGeneration { get; private set; }

    /// <summary>
    /// Enter native's preload scope: colour-key hint OFF, mipmap generation ON.
    /// Disposing restores the colour-key hint from the saved value and
    /// unconditionally sets mipmap generation back to <c>false</c> — native does
    /// not restore it, it hard-resets it (<c>trekigc.cpp:4935-4936</c>:
    /// <c>SetColorKeyHint(bOldColorKeyValue);
    /// SetEnableMipMapGeneration(false);</c>).
    /// </summary>
    public static Scope Begin()
    {
        var scope = new Scope(ColorKeyHint);
        ColorKeyHint = false;
        MipMapGeneration = true;
        return scope;
    }

    /// <summary>The preload scope's restore token.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _colorKey;
        internal Scope(bool colorKey) => _colorKey = colorKey;
        public void Dispose() => Restore(_colorKey);
    }

    private static void Restore(bool colorKey)
    {
        ColorKeyHint = colorKey;
        // Native resets (does not restore) mipmap generation: trekigc.cpp:4936.
        MipMapGeneration = false;
    }
}

/// <summary>
/// P871 / SIM-0223 — the production preload: it touches the same content the
/// first draw would (model bytes + the decoded texture surface) inside the
/// native preload scope (<see cref="PreloadRules.Begin"/>), so the first-use
/// stalls move to load time and the resource is resident. The decoded surfaces
/// are kept, with their mip chain generated exactly when the scope enabled it.
/// </summary>
public sealed class AssetPreloader : IDisposable
{
    private readonly IAssetSource _assets;
    private readonly GraphicsDevice? _gd;
    private readonly Dictionary<string, byte[]> _modelBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Model files warmed.</summary>
    public int ModelsLoaded => _modelBytes.Count;
    /// <summary>Texture surfaces decoded (headless runs decode without a GPU).</summary>
    public int TexturesLoaded { get; private set; }
    /// <summary>Texture surfaces uploaded to the GPU (0 in a headless run).</summary>
    public int TexturesUploaded => _textures.Count;
    /// <summary>Requests whose model/texture file was absent (native's modeler
    /// namespace would simply have nothing to load).</summary>
    public int Missing { get; private set; }
    /// <summary>The last requests in arrival order (the live-proof log surface).</summary>
    public List<PreloadRequest> Requests { get; } = new();

    public AssetPreloader(IAssetSource assets, GraphicsDevice? gd = null)
    {
        _assets = assets;
        _gd = gd;
    }

    /// <summary>Warm one core type, following native's null-model and
    /// <c>bmp</c>-suffix rules.</summary>
    public void Preload(string? modelName, string? textureName)
    {
        var request = PreloadRules.FromType(modelName, textureName);
        if (request.IsEmpty) return;
        Requests.Add(request);
        using var scope = PreloadRules.Begin();
        if (request.ModelStem is { } model) WarmModel(model);
        if (request.TextureStem is { } texture) WarmTexture(texture);
    }

    /// <summary>Warmed model bytes, or null when the model was not preloaded.</summary>
    public byte[]? ModelBytes(string stem) => _modelBytes.GetValueOrDefault(stem);

    /// <summary>The warmed decoded surface, or null.</summary>
    public Texture2D? Texture(string stem) => _textures.GetValueOrDefault(stem);

    private void WarmModel(string stem)
    {
        foreach (string candidate in new[] { stem + ".cvh", stem + ".mdl" })
        {
            if (!_assets.Exists(candidate)) continue;
            if (_modelBytes.ContainsKey(stem)) return;
            try { _modelBytes[stem] = AssetSources.ReadAllBytes(_assets, candidate); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            {
                _missing.Add(stem);
                Missing++;
            }
            return;
        }
        _missing.Add(stem);
        Missing++;
    }

    private void WarmTexture(string stem)
    {
        if (_textures.ContainsKey(stem) || _missing.Contains(stem)) return;
        string file = stem + ".mdl";
        if (!_assets.Exists(file)) { _missing.Add(stem); Missing++; return; }
        try
        {
            var decoded = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, file));
            TexturesLoaded++;
            if (_gd is null) return;
            // Native's colour-key hint is OFF inside the scope: the surface is
            // decoded without the near-black alpha key, and the mip chain is
            // generated because the scope enabled it.
            int keyBelow = PreloadRules.ColorKeyHint ? 10 : 0;
            var rgba = MdlTextureDecoder.ToPremultipliedRgba(decoded, keyBelow);
            var colors = new Microsoft.Xna.Framework.Color[decoded.Width * decoded.Height];
            for (int i = 0, n = 0; i < colors.Length; i++, n += 4)
                colors[i] = new Microsoft.Xna.Framework.Color(rgba[n], rgba[n + 1], rgba[n + 2], rgba[n + 3]);
            _textures[stem] = PreloadRules.MipMapGeneration
                ? TextureMipmapBuilder.CreateMipmapped(_gd, colors, decoded.Width, decoded.Height)
                : Upload(_gd, colors, decoded.Width, decoded.Height);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            _missing.Add(stem);
            Missing++;
        }
    }

    private static Texture2D Upload(GraphicsDevice gd, Microsoft.Xna.Framework.Color[] pixels, int width, int height)
    {
        var texture = new Texture2D(gd, width, height);
        texture.SetData(pixels);
        return texture;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var texture in _textures.Values) texture.Dispose();
        _textures.Clear();
        _modelBytes.Clear();
    }
}
