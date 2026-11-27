using Allegiance.Core.Imaging;

namespace Allegiance.Tools.MdlToGlb;

public sealed record PngImage(int Width, int Height, byte[] Rgba);

// Preserve the authoring API over the PNG codec also used for desktop texture preparation.
public static class PngCodec
{
    public static byte[] Encode(int width, int height, byte[] rgba) => RgbPng.Encode(width, height, rgba);

    public static PngImage Decode(byte[] png)
    {
        var image = RgbPng.Decode(png);
        return new(image.Width, image.Height, image.Rgba);
    }
}
