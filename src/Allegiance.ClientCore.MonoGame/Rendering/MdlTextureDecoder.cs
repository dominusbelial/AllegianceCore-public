using System.Buffers.Binary;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A single decoded texture from a <c>.mdl</c> file's <c>BinarySurfaceInfo</c>
/// payload: width/height plus raw RGBA (non-premultiplied, alpha always 255 —
/// RGB565 has no alpha). Pure data, no MonoGame types on the surface, so the
/// decoder is unit-testable headless.
/// </summary>
public sealed record MdlTexture(int Width, int Height, byte[] Rgba)
{
    /// <summary>The number of RGBA bytes (width × height × 4).</summary>
    public int ByteCount => Width * Height * 4;
}

/// <summary>
/// Faithful C# port of the texture half of the <c>.mdl</c> binary-surface
/// format — the exact recipe proven in
/// <c>~/Development/allegiance-tools/reference/decode_texture.py</c> (and the
/// <c>allegiance-mdl-model-format</c> wiki page). The port already had the MESH
/// half (<see cref="Allegiance.Core.MdlFile"/>, <c>MeshGeo</c>/<c>FrameData</c>);
/// this is the missing TEXTURE half (<c>BinarySurfaceInfo</c> + RGB565 rows).
/// The pixel rows are flipped top-to-bottom to match the reference decoder, so
/// a consumer can rely on "row 0 = top of the picture".
/// </summary>
public static class MdlTextureDecoder
{
    public const uint MdlMagic = 0x0DEBADF00u;

    /// <summary>Parse a <c>.mdl</c> texture from raw bytes.</summary>
    public static MdlTexture Decode(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 24)
            throw new InvalidDataException("truncated .mdl texture (no header)");

        int pos = 0;
        if (ReadU32(data, ref pos) != MdlMagic)
            throw new InvalidDataException("bad .mdl texture magic (expected 0x0DEBADF00)");
        _ = ReadU32(data, ref pos); // version

        int countNamespaces = (int)ReadU32(data, ref pos);
        int countImports = (int)ReadU32(data, ref pos);
        int countExports = (int)ReadU32(data, ref pos);
        int countDefinitions = (int)ReadU32(data, ref pos);

        for (int i = 0; i < countNamespaces; i++)
            SkipCString(data, ref pos);
        for (int i = 0; i < countImports; i++)
        {
            pos += 4; // import library index
            SkipCString(data, ref pos);
        }
        for (int i = 0; i < countExports; i++)
            SkipCString(data, ref pos);

        // The object-graph prologue preceding the BinarySurfaceInfo: a definition
        // index, an ObjectImport token (9), an import index, and an ObjectBinary
        // token (7) — four 32-bit words the reference skips blindly.
        pos += 4; // definition index
        pos += 4; // ObjectImport token
        pos += 4; // import index
        pos += 4; // ObjectBinary token

        int sizeX = (int)ReadI32(data, ref pos);
        int sizeY = (int)ReadI32(data, ref pos);
        _ = (int)ReadI32(data, ref pos); // pitch
        int bitcount = (int)ReadI32(data, ref pos);
        uint redMask = ReadU32(data, ref pos);
        uint greenMask = ReadU32(data, ref pos);
        uint blueMask = ReadU32(data, ref pos);
        _ = ReadU32(data, ref pos); // alphaMask
        pos += 4; // remaining BinarySurfaceInfo tail: 1-byte colorkey + 3 pad

        if (bitcount != 16)
            throw new InvalidDataException(
                $"only RGB565 (bitcount=16) handled, got bitcount={bitcount}");
        if (sizeX <= 0 || sizeY <= 0)
            throw new InvalidDataException($"invalid texture size {sizeX}x{sizeY}");

        int pixelCount = sizeX * sizeY;
        if (pos + pixelCount * 2 > data.Length)
            throw new InvalidDataException("truncated .mdl texture (pixel data)");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            ushort v = ReadU16(data, ref pos);
            int x = i % sizeX;
            int srcRow = i / sizeX;          // row 0 = top of the file's pixel rows
            int dstRow = sizeY - 1 - srcRow; // vertical flip (reference convention)
            int di = (dstRow * sizeX + x) * 4;
            rgba[di] = (byte)MathF.Round(((v & redMask) >> 11) / 31f * 255f);
            rgba[di + 1] = (byte)MathF.Round(((v & greenMask) >> 5) / 63f * 255f);
            rgba[di + 2] = (byte)MathF.Round((v & blueMask) / 31f * 255f);
            rgba[di + 3] = 255;
        }

        return new MdlTexture(sizeX, sizeY, rgba);
    }

    /// <summary>
    /// Convert a decoded opaque texture into the PREMULTIPLIED, transparent-background
    /// form the HUD renders. The original gauge bitmaps are authored with a black
    /// surround (there is no alpha channel); the classic HUD sits over a dark
    /// backdrop so that black reads as invisible. Drawing that opaque black box over
    /// this port's 3-D scene would smear the view, so the near-black surround is
    /// keyed to alpha 0 (pixels whose luminance is ≤ <paramref name="keyBelow"/>),
    /// and surviving pixels are premultiplied for <c>BlendState.AlphaBlend</c>.
    /// Pure and testable; returns RGBA bytes (r,g,b,a per pixel).
    /// </summary>
    public static byte[] ToPremultipliedRgba(MdlTexture tex, int keyBelow = 40)
    {
        var src = tex.Rgba;
        var outBuf = new byte[tex.ByteCount];
        for (int i = 0; i < src.Length; i += 4)
        {
            byte r = src[i], g = src[i + 1], b = src[i + 2];
            int lum = (r * 299 + g * 587 + b * 114) / 1000; // Rec.601 luminance
            if (lum <= keyBelow)
            {
                outBuf[i] = outBuf[i + 1] = outBuf[i + 2] = outBuf[i + 3] = 0;
            }
            else
            {
                outBuf[i] = r;
                outBuf[i + 1] = g;
                outBuf[i + 2] = b;
                outBuf[i + 3] = 255;
            }
        }
        return outBuf;
    }

    private static uint ReadU32(byte[] d, ref int pos)
    {
        Ensure(d, pos, 4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static int ReadI32(byte[] d, ref int pos)
    {
        Ensure(d, pos, 4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static ushort ReadU16(byte[] d, ref int pos)
    {
        Ensure(d, pos, 2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos));
        pos += 2;
        return v;
    }

    private static void SkipCString(byte[] d, ref int pos)
    {
        int start = pos;
        int end = Array.IndexOf(d, (byte)0, start);
        if (end < 0)
            throw new InvalidDataException($"unterminated cstring in .mdl at 0x{start:x}");
        int length = end - start + 1;     // strlen + NUL
        pos = start + ((length + 3) & ~3); // padded to 4
    }

    private static void Ensure(byte[] d, int pos, int count)
    {
        if (pos < 0 || pos + count > d.Length)
            throw new InvalidDataException("truncated .mdl texture");
    }
}
