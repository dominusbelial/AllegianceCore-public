using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Resolves which real original-game <c>.mdl</c> asteroid surface bitmap backs
/// each <see cref="RockTextureFactory.AsteroidResourceType"/>, and where that
/// artwork lives. The mapping mirrors the native authoritative table in
/// <c>Igc/asteroidigc.cpp</c> (<c>asteroidTypes[]</c>): each native resource
/// type's rock uses a specific <c>bgrndNN</c> rock model whose surface texture
/// is the matching <c>bgrndNNbmp.mdl</c> (all 2048×2048 RGB565). This replaces
/// the old procedural stand-in for the types that have a real asset.
/// </summary>
/// <remarks>
/// <para>
/// Native mapping (verified against <c>asteroidTypes[]</c> in the native
/// source, NOT guessed): plain/buildable → <c>bgrnd03</c>/<c>bgrnd05</c>,
/// helium → <c>bgrnd55</c>/<c>bgrnd56</c>, uranium → <c>bgrnd51</c>, silicon →
/// <c>bgrnd52</c>, carbon → <c>bgrnd53</c>. Note there is <b>no gold row</b> in
/// the native table (<c>c_aabmMineGold = 0x04</c> is a capability constant but
/// no dedicated native rock), so <see cref="RockTextureFactory.AsteroidResourceType.Gold"/>
/// deliberately falls back to the procedural generator (see <see cref="RockTextures"/>).
/// </para>
/// <para>
/// The small <c>*rockbmp.mdl</c> files (<c>heliumrockbmp</c>/<c>hotrockbmp</c>/
/// <c>copperrockbmp</c>/<c>carbonrockbmp</c>/<c>goldrockbmp</c>/<c>waterrockbmp</c>)
/// are the native <c>iconName</c> RADAR/MAP icons (16×16), not surface textures —
/// they are deliberately <b>not</b> used here as rock surfaces.
/// </para>
/// </remarks>
public static class RockTextureSource
{
    /// <summary>The root-level <c>.mdl</c> surface-bitmap stem for each resource
    /// type — the real original rock surface for that type's native rock model.
    /// Returns null for types with no real asset (gold), signalling the caller
    /// to fall back to the procedural <see cref="RockTextureFactory"/>.</summary>
    public static string? MdlStemFor(RockTextureFactory.AsteroidResourceType type) => type switch
    {
        RockTextureFactory.AsteroidResourceType.Plain => "bgrnd05bmp",
        RockTextureFactory.AsteroidResourceType.Buildable => "bgrnd05bmp",
        RockTextureFactory.AsteroidResourceType.Helium => "bgrnd55bmp",
        RockTextureFactory.AsteroidResourceType.Uranium => "bgrnd51bmp",
        RockTextureFactory.AsteroidResourceType.Silicon => "bgrnd52bmp",
        RockTextureFactory.AsteroidResourceType.Carbon => "bgrnd53bmp",
        _ => null,   // Gold — no native rock row; procedural fallback
    };

    /// <summary>The configured artwork root (same convention as the missile
    /// model loader / <see cref="GaugeTextureSource"/>).</summary>
    public static string ResolveArtworkDir() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";

    /// <summary>Full path to a type's real rock-surface <c>.mdl</c> file, or
    /// null when that type has no real asset.</summary>
    public static string? PathFor(RockTextureFactory.AsteroidResourceType type)
    {
        var stem = MdlStemFor(type);
        return stem is null ? null : System.IO.Path.Combine(ResolveArtworkDir(), stem + ".mdl");
    }
}

/// <summary>
/// The real original-game asteroid surface <see cref="Texture2D"/>s decoded from
/// the <c>bgrndNNbmp.mdl</c> artwork (via the proven <see cref="MdlTextureDecoder"/>,
/// the same pipeline <see cref="GaugeTextures"/> uses for the gauges). Each is
/// decoded once at startup and cached by <see cref="RockTextureFactory.AsteroidResourceType"/>.
/// A type with no real asset (gold) — or one whose file is missing/undecodable —
/// reports null so the caller falls back to the procedural generator.
/// </summary>
public sealed class RockTextures : IDisposable
{
    private readonly Dictionary<RockTextureFactory.AsteroidResourceType, Texture2D> _byType = [];
    private bool _disposed;

    /// <summary>Dispose every decoded real surface texture.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _byType.Values) t.Dispose();
        _byType.Clear();
    }

    /// <summary>The loaded real surface for a type, or null when that type has
    /// no real asset (gold) or its file failed to decode (graceful fallback to
    /// the procedural <see cref="RockTextureFactory"/>).</summary>
    public Texture2D? For(RockTextureFactory.AsteroidResourceType type) =>
        _byType.TryGetValue(type, out var tex) ? tex : null;

    /// <summary>Whether the given type resolved to a real asset successfully.</summary>
    public bool HasReal(RockTextureFactory.AsteroidResourceType type) =>
        _byType.ContainsKey(type);

    /// <summary>Count of real rock surfaces successfully loaded (diagnostics).</summary>
    public int LoadedCount => _byType.Count;

    /// <summary>
    /// Decode the real surface bitmap for each resource type that has one.
    /// Missing/undecodable files are skipped with a logged warning — the type
    /// degrades gracefully to the procedural generator rather than failing to
    /// start, mirroring the port's established "graceful degradation" convention.
    /// </summary>
    public static RockTextures Load(GraphicsDevice gd, string artworkDir)
    {
        var result = new RockTextures();
        foreach (RockTextureFactory.AsteroidResourceType type in Enum.GetValues<RockTextureFactory.AsteroidResourceType>())
        {
            string? path = RockTextureSource.PathFor(type);
            if (path is null)
                continue;   // no real asset for this type (gold) — procedural fallback

            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[monogame] rock surface '{type}' missing ({path}) — procedural fallback");
                    continue;
                }
                var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));
                var surface = new Texture2D(gd, tex.Width, tex.Height);
                var colors = new Color[tex.Width * tex.Height];
                var rgba = tex.Rgba;
                for (int i = 0; i < colors.Length; i++)
                {
                    int b = i * 4;
                    colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], (byte)255);
                }
                surface.SetData(colors);
                result._byType[type] = surface;
                Console.WriteLine($"[monogame] rock surface '{type}': {tex.Width}x{tex.Height} '{Path.GetFileName(path)}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] rock surface '{type}' failed to decode: {ex.Message} — procedural fallback");
            }
        }
        return result;
    }
}
