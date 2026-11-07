using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P945 — draws the native hangar screen (<c>hangar.mdl</c>): the 800×600
/// station-interior background, the eight native buttons at their authored
/// points, the hover text strip and the in-turret badge, all scaled uniformly
/// to the viewport and centred exactly like native's <c>Screen</c>
/// (<c>WinTrek/WinTrek.cpp:3656-3681</c>).
///
/// <para>Every rectangle comes from <see cref="HangarScreen"/> — the same source
/// the hangar hit test uses — so what is drawn is what can be clicked. A pane
/// whose artwork is missing degrades to a labelled box at its native rectangle
/// (native's <c>LoadImage</c> returns NULL and the pane simply vanishes; the
/// port keeps the verb reachable instead). When the background itself is
/// missing the caller keeps the pre-P945 text-row hangar, so the screen is never
/// blank.</para>
///
/// <para>Both HUD styles get the same native art: <see cref="HudStyle"/> changes
/// HUD typography and reticles, and the native hangar screen's own art is not a
/// per-style asset. The only style-visible difference is the letterbox scale,
/// which is identical for both.</para>
/// </summary>
public sealed class HangarScreenRenderer : IDisposable
{
    private readonly SpriteBatch _spriteBatch;
    private readonly Texture2D _whitePixel;
    private readonly SpriteFont _fallbackFont;
    private readonly NativeFonts? _nativeFonts;
    private readonly GraphicsDevice _graphics;
    private readonly IAssetSource _assets;
    private HangarArt? _art;
    private float? _hoverLineSpacing;
    private bool _disposed;

    public HangarScreenRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont fallbackFont,
        NativeFonts? nativeFonts, GraphicsDevice graphics, IAssetSource assets)
    {
        _spriteBatch = spriteBatch;
        _whitePixel = whitePixel;
        _fallbackFont = fallbackFont;
        _nativeFonts = nativeFonts;
        _graphics = graphics;
        _assets = assets;
    }

    /// <summary>Whether the native screen can be drawn (the background resolves
    /// and decodes). False → the caller keeps the text-row hangar.</summary>
    public bool CanDrawNative => HangarArt.IsInstalled(_assets) && BackgroundLoaded;

    private bool BackgroundLoaded
    {
        get
        {
            if (_art is null) _art = new HangarArt(_graphics, _assets);
            return _art.Background is not null;
        }
    }

    /// <summary>Draw the native hangar screen. <paramref name="hover"/> is the
    /// verb under the pointer, <paramref name="launch"/> the launch pane's frame
    /// animation, <paramref name="boardState"/> the native BoardState
    /// (<c>WinTrek/console.cpp:797-804</c>; 2 = turret gunner).</summary>
    public void Draw(Viewport viewport, HangarAction hover, HangarLaunchAnimation launch,
        int? boardState, bool developments)
    {
        _art ??= new HangarArt(_graphics, _assets);
        int w = viewport.Width, h = viewport.Height;
        var transform = HangarScreen.ScreenTransform.For(w, h);

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        if (_art.Background is { } background)
            _spriteBatch.Draw(background,
                transform.Map(0, 0, HangarScreen.NativeWidth, HangarScreen.NativeHeight), Color.White);

        foreach (var control in HangarScreen.Controls)
        {
            if (control.Action == HangarAction.Invest && !developments) continue;   // hangarscreen.cpp:129-130
            DrawControl(viewport, control, hover == control.Action, launch);
        }

        DrawPortVerbs(viewport, hover);
        DrawHoverText(viewport, transform, hover);
        if (HangarScreen.ShowsInTurretBadge(boardState) && _art.InTurret is { } badge)
            _spriteBatch.Draw(badge, HangarScreen.InTurretRect(w, h), Color.White);

        _spriteBatch.End();
    }

    private void DrawControl(Viewport viewport, HangarScreen.Control control, bool hovered,
        HangarLaunchAnimation launch)
    {
        int w = viewport.Width, h = viewport.Height;
        if (control.Action == HangarAction.Launch)
        {
            var frames = _art!.LaunchFrames;
            var size = _art.LaunchSize ?? HangarScreen.NativeSize[HangarAction.Launch];
            var rect = HangarScreen.Rect(w, h, control, size);
            if (frames.Count > 0)
            {
                _spriteBatch.Draw(frames[launch.Frame(frames.Count)], rect, Color.White);
                return;
            }
            DrawFallbackPane(rect, control.Action, hovered);
            return;
        }

        if (_art!.Face(control.Action, hovered) is { } face)
        {
            _spriteBatch.Draw(face.Texture, HangarScreen.Rect(w, h, control), face.Source, Color.White);
            return;
        }
        DrawFallbackPane(HangarScreen.Rect(w, h, control), control.Action, hovered);
    }

    /// <summary>A pane whose artwork is missing: keep the verb visible and
    /// clickable at its native rectangle (the fallback never hides a verb).</summary>
    private void DrawFallbackPane(Rectangle rect, HangarAction action, bool hovered)
    {
        _spriteBatch.Draw(_whitePixel, rect, hovered ? new Color(30, 100, 105) : new Color(12, 30, 40));
        string text = action.ToString();
        float scale = MathF.Min(1f, (rect.Width - 8) / MathF.Max(1f, _fallbackFont.MeasureString(text).X));
        _spriteBatch.DrawString(_fallbackFont, text, new Vector2(rect.X + 4, rect.Y + 3), Color.White,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>The two port-only verbs (native's hangar has no Buy/Default
    /// button). They sit in the letterbox margin under the scaled screen — see
    /// <see cref="HangarScreen.PortVerbRect"/> — so they never cover native art
    /// in a widescreen window, with the screen's bottom-right corner as the 4:3
    /// fallback.</summary>
    private void DrawPortVerbs(Viewport viewport, HangarAction hover)
    {
        foreach (var (action, label) in new[] { (HangarAction.Buy, "Buy"), (HangarAction.Default, "Def") })
        {
            var rect = HangarScreen.PortVerbRect(viewport.Width, viewport.Height, action == HangarAction.Default);
            _spriteBatch.Draw(_whitePixel, rect, hover == action ? new Color(30, 100, 105) : new Color(10, 22, 30));
            float scale = MathF.Min(1f, (rect.Width - 4) / MathF.Max(1f, _fallbackFont.MeasureString(label).X));
            _spriteBatch.DrawString(_fallbackFont, label, new Vector2(rect.X + 2, rect.Y + 1), Color.LightCyan,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }

    /// <summary>The native hover text: the pane at (100,561) 600×24 in
    /// <c>hugeFont</c>, colour 0.75 grey, justified centre
    /// (<c>hangar.mdl:26-72</c>; <c>StringPane::Paint</c> draws at the pane's
    /// top-left and centres by the measured text width,
    /// <c>engine/controls.cpp:533-575</c>).</summary>
    private void DrawHoverText(Viewport viewport, HangarScreen.ScreenTransform transform, HangarAction hover)
    {
        string text = HangarControls.Feedback(hover);
        if (text.Length == 0) return;
        var pane = HangarScreen.HoverTextRect(viewport.Width, viewport.Height);
        var (font, scale) = HoverFont(transform.Scale);
        float width = font.MeasureString(text).X * scale;
        float x = pane.X + (pane.Width - width) / 2f;
        // Native clips at the pane width (StringPane::Paint, GetMaxTextLength);
        // the port instead shrinks a substituted face that measures wider, so the
        // whole native string stays readable.
        if (width > pane.Width && width > 0f)
        {
            scale *= pane.Width / width;
            width = pane.Width;
            x = pane.X;
        }
        _spriteBatch.DrawString(font, text, new Vector2(x, pane.Y), HangarScreen.HoverTextColor,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>The bake and residual scale that put the native <c>hugeFont</c>
    /// cell height (23 px, <c>hangar.mdl:28</c>) on screen. The port has no
    /// hugeFont bake, so the proportional <see cref="NativeFont.SmallFont"/> face
    /// is scaled to the native cell height; <see cref="NativeFonts.Get"/> picks
    /// the 2× density bake whenever the total scale needs it.</summary>
    private (SpriteFont Font, float Scale) HoverFont(float screenScale)
    {
        float wanted = HangarScreen.HoverTextHeight * screenScale;
        if (_nativeFonts is not null)
        {
            _hoverLineSpacing ??= MathF.Max(1f, _nativeFonts.Get(NativeFont.SmallFont, 1f).Font.LineSpacing);
            // Get() returns (bake, residual) so the drawn line height is
            // lineSpacing(1x) × the requested scale for both densities.
            return _nativeFonts.Get(NativeFont.SmallFont, wanted / _hoverLineSpacing.Value);
        }
        return (_fallbackFont, _fallbackFont.LineSpacing > 0f ? wanted / _fallbackFont.LineSpacing : 1f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _art?.Dispose();
        _art = null;
    }
}
