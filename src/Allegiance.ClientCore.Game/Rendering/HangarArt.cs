using System.Collections.Concurrent;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P945 — the native hangar screen's artwork (<c>hangar.mdl</c>).
///
/// <para><b>What native loads.</b> A full 800×600 <c>ImportImage("hangarbmp",
/// false)</c> background, eight button panes — seven <c>ButtonPane(ImportImage(
/// "…bmp", false), 5, false)</c> strips plus the launch pane's
/// <c>FrameImageButtonPane("animlaunch", .7, false, false)</c>
/// (<c>hangar.mdl:12-20</c>) — the <c>hangarinturretbmp</c> badge, and the
/// <c>hugeFont</c> hover text. Every one of them is a binary RGB565 <c>.mdl</c>
/// surface, decoded by <see cref="MdlTextureDecoder"/> like the port's other 2-D
/// art; the launch animation is a <c>FrameImage</c> namespace whose XOR-delta
/// frames <see cref="MdlFrameImage"/> decodes (its 16 frames are also laid out
/// row-major in <c>animlaunchbmp</c>'s 4×4 sheet, which the tests use to pin the
/// frame count and order).
/// </para>
///
/// <para><b>Case.</b> Native shipped on Windows, so <c>hangar.mdl</c> asks for
/// <c>"btnquitbmp"</c> while the file on disk is <c>BtnQuitbmp.mdl</c>. Stems
/// resolve case-insensitively here (exact name first, then a cached scan of the
/// artwork root), so a Linux/macOS checkout loads the same art Windows did.
/// </para>
///
/// <para><b>Missing art</b> is remembered and logged once per stem; the caller
/// then falls back to the pre-P945 text-row hangar (never a blank screen) —
/// matching native's own <c>LoadImage</c> NULL path
/// (<c>engine/modeler.cpp:691-746</c>).</para>
/// </summary>
internal sealed class HangarArt : IDisposable
{
    /// <summary>Per-source case-insensitive stem indexes (built once per asset
    /// source; the artwork root has ~10k files).</summary>
    private static readonly ConcurrentDictionary<IAssetSource, Dictionary<string, string>> StemIndexes = new();

    private readonly GraphicsDevice _graphics;
    private readonly IAssetSource _assets;
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly List<string> _missing = [];
    private Texture2D?[]? _launchFrames;
    private bool _disposed;

    public HangarArt(GraphicsDevice graphics, IAssetSource assets)
    {
        _graphics = graphics;
        _assets = assets;
    }

    /// <summary>The bitmap stems resolvable without a GPU are probed by the
    /// artwork tests; missing stems are reported rather than thrown.</summary>
    public IReadOnlyList<string> Missing => _missing;

    /// <summary>Resolve <c>&lt;stem&gt;.mdl</c> case-insensitively, returning the
    /// artwork's real relative path (e.g. <c>BtnQuitbmp.mdl</c> for
    /// <c>btnquitbmp</c>) or null when the file is absent.</summary>
    public static string? ResolvePath(IAssetSource assets, string stem)
    {
        string exact = stem + ".mdl";
        try
        {
            if (assets.Exists(exact)) return exact;
        }
        catch (Exception)
        {
            return null;
        }
        var index = StemIndexes.GetOrAdd(assets, BuildStemIndex);
        return index.TryGetValue(stem, out var path) ? path : null;
    }

    private static Dictionary<string, string> BuildStemIndex(IAssetSource assets)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // ".". the artwork root: DirectoryAssetSource rejects an empty relative
        // path, and every other source treats "." as its root too.
        foreach (var directory in new[] { ".", "" })
        {
            try
            {
                foreach (var path in assets.Enumerate(directory))
                {
                    if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = path[(path.LastIndexOf('/') + 1)..];
                    index.TryAdd(name[..^4], path);           // first spelling wins, like a case-insensitive FS
                }
                if (index.Count > 0) break;
            }
            catch (Exception)
            {
                // An unwalkable source simply has no case-folded aliases:
                // exact-name lookups still work.
            }
        }
        return index;
    }

    /// <summary>A bitmap's native pixel size, headless (no GraphicsDevice) —
    /// the binary surface header's width/height. Null when absent.</summary>
    public static (int Width, int Height)? ProbeSize(IAssetSource assets, string stem)
    {
        try
        {
            if (ResolvePath(assets, stem) is not { } path) return null;
            var decoded = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, path), flipVertical: false);
            return (decoded.Width, decoded.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The launch animation's frame size and count, headless. Native
    /// takes the <c>FrameImage</c>'s background bounds as the pane size and
    /// <c>nFrame</c> from the same payload (<c>engine/frameimage.cpp:280-311,
    /// 361-368</c>). Null when the namespace is absent or unreadable.</summary>
    public static (int Width, int Height, int Frames)? ProbeLaunch(IAssetSource assets)
    {
        try
        {
            if (ResolvePath(assets, "animlaunch") is not { } path) return null;
            var animation = MdlFrameImage.Decode(AssetSources.ReadAllBytes(assets, path));
            return (animation.Width, animation.Height, animation.FrameCount);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether the native hangar screen can be drawn at all — the
    /// 800×600 background resolves. When false the caller keeps the text-row
    /// fallback (never a blank screen).</summary>
    public static bool IsInstalled(IAssetSource assets) =>
        ResolvePath(assets, HangarScreen.BackgroundStem) is not null;

    /// <summary>The 800×600 hangar background.</summary>
    public Texture2D? Background => Get(HangarScreen.BackgroundStem);

    /// <summary>The in-turret badge bitmap.</summary>
    public Texture2D? InTurret => Get(HangarScreen.InTurretStem);

    /// <summary>The button strip for an action plus the source rectangle of the
    /// face to draw: row 0 (top half) is the normal face, row 1 (bottom half)
    /// the mouse-inside face (<c>engine/button.cpp:108-198</c>).</summary>
    public (Texture2D Texture, Rectangle Source)? Face(HangarAction action, bool inside)
    {
        if (HangarScreen.ControlOf(action) is not { } control) return null;
        var texture = Get(control.Stem);
        if (texture is null) return null;
        int faceHeight = texture.Height / HangarScreen.FaceCount;
        if (faceHeight <= 0) return null;
        return (texture, new Rectangle(0, HangarScreen.FaceRow(inside) * faceHeight,
            texture.Width, faceHeight));
    }

    /// <summary>The decoded launch animation frames (native <c>nFrame</c> of
    /// them, frame 0 = the closed door). Empty when the namespace is absent.</summary>
    public IReadOnlyList<Texture2D> LaunchFrames
    {
        get
        {
            _launchFrames ??= LoadLaunchFrames();
            return _launchFrames.Length == 0 ? [] : Array.ConvertAll(_launchFrames, t => t!);
        }
    }

    /// <summary>The launch pane's native size (the FrameImage background), or
    /// null when the animation is missing.</summary>
    public (int Width, int Height)? LaunchSize
    {
        get
        {
            var frames = LaunchFrames;
            return frames.Count > 0 ? (frames[0].Width, frames[0].Height) : null;
        }
    }

    private Texture2D?[] LoadLaunchFrames()
    {
        string? path = ResolvePath(_assets, "animlaunch");
        if (path is null)
        {
            LogMissingOnce("animlaunch", "no such frame animation");
            return [];
        }
        try
        {
            var animation = MdlFrameImage.Decode(AssetSources.ReadAllBytes(_assets, path));
            var frames = new Texture2D?[animation.FrameCount];
            for (int i = 0; i < frames.Length; i++)
                frames[i] = Upload(animation.Frames[i]);
            return frames;
        }
        catch (Exception ex)
        {
            LogMissingOnce("animlaunch", ex.Message);
            return [];
        }
    }

    /// <summary>The drawable texture for a stem, or null when it cannot be
    /// loaded. Native imports these images with <c>colorKey = false</c>
    /// (<c>hangar.mdl:12-20</c>, and <c>SetColorKeyHint(false)</c> at
    /// <c>WinTrek/hangarscreen.cpp:54</c>), so they are blitted opaque: the
    /// decoded RGBA keeps alpha 255 and the near-black surround is NOT keyed
    /// (the hangar interior has genuinely black areas).</summary>
    public Texture2D? Get(string stem)
    {
        if (_textures.TryGetValue(stem, out var cached)) return cached;
        Texture2D? texture = null;
        string? path = ResolvePath(_assets, stem);
        if (path is null)
        {
            LogMissingOnce(stem, "no such bitmap");
        }
        else
        {
            try
            {
                texture = Upload(MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, path),
                    flipVertical: false));
            }
            catch (Exception ex)
            {
                LogMissingOnce(stem, ex.Message);
            }
        }
        _textures[stem] = texture;
        return texture;
    }

    private Texture2D Upload(MdlTexture decoded)
    {
        var texture = new Texture2D(_graphics, decoded.Width, decoded.Height, false, SurfaceFormat.Color);
        var colors = new Color[decoded.Width * decoded.Height];
        for (int i = 0; i < colors.Length; i++)
        {
            int b = i * 4;
            colors[i] = new Color(decoded.Rgba[b], decoded.Rgba[b + 1], decoded.Rgba[b + 2], decoded.Rgba[b + 3]);
        }
        texture.SetData(colors);
        return texture;
    }

    /// <summary>Log a missing stem exactly once (P943 rule: no per-frame spam).</summary>
    public void LogMissingOnce(string stem, string reason)
    {
        if (!_logged.Add(stem)) return;
        _missing.Add(stem);
        Console.WriteLine($"[hangar] art '{stem}' missing ({reason}) — text-row hangar for that pane");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var texture in _textures.Values) texture?.Dispose();
        _textures.Clear();
        if (_launchFrames is not null)
            foreach (var frame in _launchFrames) frame?.Dispose();
        _launchFrames = null;
    }
}
