using Allegiance.ClientCore.Training;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P943 — the training slideshow screen's artwork. Native builds each slide as a live
/// image tree and the UI paints the screen's image into the window, scaling the whole
/// 800×600 layout by <c>min(window/screen)</c> and centring it
/// (<c>Slideshow::GetImage</c>/<c>WrapImage</c>, <c>WinTrek/Slideshow.cpp:33-39</c>,
/// <c>:153-156</c>; <c>WinTrek/WinTrek.cpp:2180-2217</c> builds the screen image,
/// <c>UpdateBackdropCentering</c> <c>:3656-3693</c> applies the scale). The port draws
/// the same operations with the sprite batch; when a slide's image cannot be
/// resolved the native narration caption stands in, so a slide is never blank.
/// </summary>
public sealed partial class Game1
{
    private Rendering.SlideshowArt? _slideshowArt;

    private Rendering.SlideshowArt SlideshowArt =>
        _slideshowArt ??= new Rendering.SlideshowArt(GraphicsDevice, _assets);

    /// <summary>Draw one slide's image expression at the native 800×600 layout.
    /// Returns false when nothing could be drawn (a missing namespace/bitmap), so the
    /// caller shows the narration caption instead.</summary>
    private bool DrawSlideArt(SlideshowImage image, float w, float h)
    {
        var layout = SlideshowLayout.Build(image, SlideshowArt.Size);
        foreach (var stem in layout.Missing)
            SlideshowArt.LogMissingOnce(stem, "not installed");

        var transform = SlideshowLayout.ScreenTransform.For(w, h);
        int drawn = 0;
        foreach (var op in layout.Images)
        {
            var texture = SlideshowArt.Get(op.Stem, op.ColorKey);
            if (texture is null) continue;
            var (x, y, width, height) = transform.ImageRect(op);
            _spriteBatch.Draw(texture, new Rectangle(
                (int)MathF.Round(x), (int)MathF.Round(y),
                Math.Max(1, (int)MathF.Round(width)), Math.Max(1, (int)MathF.Round(height))),
                Color.White);
            drawn++;
        }
        foreach (var op in layout.Texts)
        {
            if (op.Text.Length == 0) continue;
            // StringImage wraps at its native width and hangs below its point
            // (engine/image.cpp:337-400); the block scales with the screen. Native's
            // slide text is authored white (tm_1_introduction.mdl) — the port keeps
            // white for either justification.
            float lineHeight = _hudSmall.LineSpacing * transform.Scale;
            float y = transform.TextTop(op);
            float left = transform.OffsetX + op.X * transform.Scale;
            foreach (var line in WrapText(op.Text, op.Width))
            {
                float x = op.LeftJustify ? left : CentreX(line);
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(x, y), Color.White,
                    0f, Vector2.Zero, transform.Scale, SpriteEffects.None, 0f);
                y += lineHeight;
            }
            drawn++;
        }
        return drawn > 0;

        float CentreX(string line)
        {
            float width = _hudSmall.MeasureString(line).X;
            float centre = transform.OffsetX + (SlideshowLayout.NativeWidth * transform.Scale) / 2f;
            return centre - (width * transform.Scale) / 2f;
        }
    }

    /// <summary>The caption stand-in: the slide's native narration, centred and
    /// wrapped (the pre-P943 presentation). Used when the slide has no resolvable
    /// image; when the narration itself is empty the slideshow title labels the
    /// slide so the screen is never blank.</summary>
    private void DrawSlideCaption(string caption, float w, float h, Color textCol)
    {
        float textWidth = MathF.Min(w * 0.8f, 900f);
        var lines = WrapText(caption.Replace("\n", " "), textWidth);
        var first = _hudSmall.MeasureString(lines.Count > 0 ? lines[0] : "");
        float top = (h - lines.Count * (first.Y + 6f)) / 2f;
        for (int i = 0; i < lines.Count; i++)
        {
            var size = _hudSmall.MeasureString(lines[i]);
            _spriteBatch.DrawString(_hudSmall, lines[i], new Vector2((w - size.X) / 2f, top + i * (first.Y + 6f)), textCol);
        }
    }
}
