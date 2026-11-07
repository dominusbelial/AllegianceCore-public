namespace Allegiance.ClientCore.Training;

/// <summary>
/// P943 — evaluates a slide's <see cref="SlideshowImage"/> expression into native
/// screen-space draw operations. Native builds the slide as a live image tree
/// (<c>Slideshow::Slideshow</c> wraps it in a <c>WrapImage</c>,
/// <c>WinTrek/Slideshow.cpp:33-39</c>) and the UI scales the whole 800×600 screen to
/// the window, centring it (<c>WinTrek/WinTrek.cpp:3656-3693</c>
/// <c>UpdateBackdropCentering</c>: <c>scale = min(rect/screen)</c>, then a
/// half-the-slack translate). The coordinate space is native 2-D: y is UP,
/// origin bottom-left; an image's point is its lower-left corner, and a
/// <c>StringImage</c> hangs below its point.
/// </summary>
public static class SlideshowLayout
{
    /// <summary>Native <c>Screen::GetSize</c> — every screen is laid out at 800×600
    /// (<c>WinTrek/screen.h:14</c>).</summary>
    public const int NativeWidth = 800;
    public const int NativeHeight = 600;

    /// <summary>A bitmap placed in native screen space: (X, Y) is the lower-left
    /// corner in native y-up coordinates.</summary>
    public sealed record ImageOp(string Stem, bool ColorKey, int X, int Y, int Width, int Height);

    /// <summary>A text block: (X, Y) is the point the block hangs BELOW
    /// (<c>StringImage</c> bounds <c>Rect(0, -ysize, width, 0)</c>,
    /// <c>engine/image.cpp:337-355</c>).</summary>
    public sealed record TextOp(string Text, int X, int Y, int Width, string Font, string Color, bool LeftJustify);

    /// <summary>One slide's draw list, in paint order, plus the imports that could not
    /// be resolved (the caller logs each once and falls back to the caption).</summary>
    public sealed record Slide(IReadOnlyList<ImageOp> Images, IReadOnlyList<TextOp> Texts,
        IReadOnlyList<string> Missing)
    {
        public bool HasContent => Images.Count > 0 || Texts.Count > 0;
    }

    /// <summary>
    /// Evaluate the expression. <paramref name="resolve"/> returns the bitmap's native
    /// pixel size for a stem, or null when the artwork is absent — such a child is
    /// reported in <see cref="Slide.Missing"/> and skipped (native's
    /// <c>LoadImage</c> returns NULL there too, <c>engine/modeler.cpp:2463-2492</c>).
    /// </summary>
    public static Slide Build(SlideshowImage? image, Func<string, (int Width, int Height)?> resolve)
    {
        var images = new List<ImageOp>();
        var texts = new List<TextOp>();
        var missing = new List<string>();
        Walk(image, 0, 0, resolve, images, texts, missing);
        return new Slide(images, texts, missing);
    }

    private static void Walk(SlideshowImage? image, int x, int y,
        Func<string, (int Width, int Height)?> resolve,
        List<ImageOp> images, List<TextOp> texts, List<string> missing)
    {
        switch (image)
        {
            case SlideshowImage.Import import:
                if (resolve(import.Stem) is { } size && size.Width > 0 && size.Height > 0)
                    images.Add(new ImageOp(import.Stem, import.ColorKey, x, y, size.Width, size.Height));
                else if (!missing.Contains(import.Stem))
                    missing.Add(import.Stem);
                break;
            case SlideshowImage.Group group:
                // GroupImage paints its list in reverse: the first child is on top
                // (engine/modeler.cpp:1544-1562 AddImage pushes to the front;
                // engine/image.cpp:918-921, 954-976 renders front-to-back).
                for (int i = group.Items.Count - 1; i >= 0; i--)
                    Walk(group.Items[i], x, y, resolve, images, texts, missing);
                break;
            case SlideshowImage.Translate translate:
                Walk(translate.Child, x + translate.X, y + translate.Y, resolve, images, texts, missing);
                break;
            case SlideshowImage.Text text:
                texts.Add(new TextOp(text.Value, x, y, text.Width, text.Font, text.Color,
                    text.Justify.Equals("JustifyLeft", StringComparison.OrdinalIgnoreCase)));
                break;
        }
    }

    /// <summary>
    /// The native screen → window transform: scale the 800×600 screen by
    /// <c>min(w/800, h/600)</c> and centre it, then flip y for the port's top-left
    /// pixel space (native y-up lower-left).
    /// </summary>
    public readonly record struct ScreenTransform(float Scale, float OffsetX, float OffsetY)
    {
        public static ScreenTransform For(float windowWidth, float windowHeight)
        {
            float scale = MathF.Min(windowWidth / NativeWidth, windowHeight / NativeHeight);
            return new ScreenTransform(scale,
                (windowWidth - NativeWidth * scale) / 2f,
                (windowHeight - NativeHeight * scale) / 2f);
        }

        /// <summary>The destination rectangle (top-left pixels) of a placed bitmap.</summary>
        public (float X, float Y, float Width, float Height) ImageRect(ImageOp op) =>
            (OffsetX + op.X * Scale,
             OffsetY + (NativeHeight - (op.Y + op.Height)) * Scale,
             op.Width * Scale,
             op.Height * Scale);

        /// <summary>The top edge of a text block (the block hangs below its point).</summary>
        public float TextTop(TextOp op) => OffsetY + (NativeHeight - op.Y) * Scale;
    }
}
