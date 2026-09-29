using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P428 — resolves which real original-game <c>.mdl</c> artwork backs each
/// missile-lock display state, and where that artwork lives. Native's radar
/// (<c>WinTrek/radarimage.cpp:217-218,330-332</c>) swaps between two fixed
/// assets by lock state, named in <c>WinTrek/artwork.h:80-81</c>:
/// <list type="bullet">
///   <item>Partial lock → <c>AWF_FLIGHT_PARTIAL_MISSILE_LOCK_ICON</c> =
///   <c>plockbmp.mdl</c> — the four orange corner brackets, no centre.</item>
///   <item>Full lock → <c>AWF_FLIGHT_MISSILE_LOCKED_ICON</c> =
///   <c>lockedbmp.mdl</c> — the same orange corner brackets plus the green
///   4-petal flower-burst filling the centre (the "flower" Domingo remembers).</item>
/// </list>
/// Both are real 64×64 RGB565 bitmaps decoded via the port's established
/// <see cref="MdlTextureDecoder"/> pipeline (same as <see cref="GaugeTextures"/>).
/// </summary>
public static class LockTextureSource
{
    /// <summary>The root-level <c>.mdl</c> stem for the partial-lock sprite.</summary>
    public const string PartialMdl = "plockbmp";

    /// <summary>The root-level <c>.mdl</c> stem for the full-lock sprite.</summary>
    public const string LockedMdl = "lockedbmp";

    /// <summary>The <c>.mdl</c> stem for a lock state, or null for no-lock
    /// (native draws NO lock sprite for <c>lock == 0</c>, radarimage.cpp:330).</summary>
    public static string? MdlStemFor(MissileLockFeedback.LockState state) => state switch
    {
        MissileLockFeedback.LockState.Partial => PartialMdl,
        MissileLockFeedback.LockState.Full => LockedMdl,
        _ => null,
    };

    /// <summary>The configured artwork root (same convention as the missile
    /// model loader / <see cref="GaugeTextureSource"/>).</summary>
    public static string ResolveArtworkDir() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";

    /// <summary>Full path to a lock state's real <c>.mdl</c> sprite, or null
    /// when that state has no sprite (none).</summary>
    public static string? PathFor(MissileLockFeedback.LockState state)
    {
        var stem = MdlStemFor(state);
        return stem is null ? null : System.IO.Path.Combine(ResolveArtworkDir(), stem + ".mdl");
    }
}

/// <summary>
/// P428 — the two real missile-lock <see cref="Texture2D"/>s decoded from the
/// original <c>plockbmp.mdl</c> / <c>lockedbmp.mdl</c> artwork, in the
/// premultiplied transparent-surround form the HUD renders (exactly like
/// <see cref="GaugeTextures"/> — the black surround is keyed to alpha 0 so the
/// sprite sits cleanly over the 3-D scene). Built in <c>Game1.LoadContent</c>;
/// handed to <see cref="HudRenderer"/>. Missing/undecodable assets degrade
/// gracefully to the procedural bracket fallback.
/// </summary>
public sealed class LockTextures : IDisposable
{
    private Texture2D? _partial;
    private Texture2D? _locked;
    private bool _disposed;

    /// <summary>The loaded real partial-lock sprite, or null if unavailable.</summary>
    public Texture2D? Partial => _partial;

    /// <summary>The loaded real full-lock sprite, or null if unavailable.</summary>
    public Texture2D? Locked => _locked;

    /// <summary>Both real sprites loaded successfully.</summary>
    public bool AllLoaded => _partial is not null && _locked is not null;

    /// <summary>The real sprite for a lock state, or null when that state has
    /// no sprite (none) or its file failed to decode.</summary>
    public Texture2D? For(MissileLockFeedback.LockState state) => state switch
    {
        MissileLockFeedback.LockState.Partial => _partial,
        MissileLockFeedback.LockState.Full => _locked,
        _ => null,
    };

    /// <summary>Release both decoded textures.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _partial?.Dispose();
        _locked?.Dispose();
        _partial = null;
        _locked = null;
    }

    /// <summary>
    /// Decode both real lock sprites from <paramref name="artworkDir"/>.
    /// Missing/undecodable files are skipped with a logged warning — the
    /// indicator degrades gracefully to the procedural bracket rather than
    /// refusing to start, mirroring the port's established convention
    /// (<see cref="GaugeTextures"/>, <see cref="RockTextures"/>).
    /// </summary>
    public static LockTextures Load(GraphicsDevice gd, string artworkDir)
    {
        var result = new LockTextures();

        string[] states = [
            MissileLockFeedback.LockState.Partial.ToString(),
            MissileLockFeedback.LockState.Full.ToString(),
        ];
        string[] stems = [LockTextureSource.PartialMdl, LockTextureSource.LockedMdl];

        for (int i = 0; i < stems.Length; i++)
        {
            string path = System.IO.Path.Combine(artworkDir, stems[i] + ".mdl");
            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[monogame] lock sprite '{states[i]}' missing ({path}) — procedural fallback");
                    continue;
                }
                var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));
                byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
                var surface = new Texture2D(gd, tex.Width, tex.Height);
                var colors = new Color[tex.Width * tex.Height];
                for (int c = 0; c < colors.Length; c++)
                {
                    int b = c * 4;
                    colors[c] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                surface.SetData(colors);
                if (i == 0) result._partial = surface;
                else result._locked = surface;
                Console.WriteLine($"[monogame] lock sprite '{states[i]}': {tex.Width}x{tex.Height} '{Path.GetFileName(path)}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] lock sprite '{states[i]}' failed to decode: {ex.Message} — procedural fallback");
            }
        }
        return result;
    }
}
