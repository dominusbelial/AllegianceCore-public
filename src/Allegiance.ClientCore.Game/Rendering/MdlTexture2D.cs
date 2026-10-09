using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P589 — small helper to decode a native <c>.mdl</c> texture into a GPU
/// <see cref="Texture2D"/> with its near-black surround colour-keyed to
/// transparent and premultiplied for <c>BlendState.AlphaBlend</c> (the same
/// path <c>PlanetPoster</c> and the sky dome use — native's surface colour-key,
/// <c>modeler.cpp:726</c>). Cached per stem; failures are remembered so we log
/// once and degrade gracefully (a missing flare/moon simply doesn't draw).
/// </summary>
internal sealed class MdlTexture2D : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly int _keyBelow;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public MdlTexture2D(GraphicsDevice gd, IAssetSource assets, int keyBelow = 10)
    {
        _gd = gd;
        _assets = assets;
        _keyBelow = keyBelow;
    }

    public bool Exists(string stem) => _textures.ContainsKey(stem);

    public Texture2D? Get(string stem)
    {
        if (_textures.TryGetValue(stem, out var t)) return t;
        if (_missing.Contains(stem)) return null;
        try
        {
            if (!_assets.Exists(stem + ".mdl")) { _missing.Add(stem); return null; }
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, stem + ".mdl"));
            var rgba = MdlTextureDecoder.ToPremultipliedRgba(mdlTex, _keyBelow);
            var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height, false, SurfaceFormat.Color);
            var cols = new Color[mdlTex.Width * mdlTex.Height];
            int n = 0;
            for (int i = 0; i < cols.Length; i++, n += 4)
                cols[i] = new Color(rgba[n], rgba[n + 1], rgba[n + 2], rgba[n + 3]);
            surf.SetData(cols);
            _textures[stem] = surf;
            return surf;
        }
        catch (Exception ex)
        {
            _missing.Add(stem);
            Console.WriteLine($"[monogame] sky object art '{stem}' failed to decode: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
    }
}
